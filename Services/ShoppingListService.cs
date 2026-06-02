using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HousingMarketShopper.Models;

namespace HousingMarketShopper.Services;

/// <summary>
/// Orchestrates: file parsing → item resolution → Universalis price fetch →
/// ShoppingPlan construction.
/// </summary>
public sealed class ShoppingListService
{
    private readonly ItemResolverService _resolver;
    private readonly UniversalisService  _universalis;
    private readonly IPluginLog          _log;
    private readonly Configuration       _cfg;

    public ShoppingPlan?        CurrentPlan  { get; private set; }
    public List<ShoppingItem>   LoadedItems  { get; private set; } = [];


    // Progress state for the UI
    public bool   IsLoading        { get; private set; }
    public bool   IsFetchingPrices { get; private set; }
    public int    FetchProgress    { get; private set; }
    public int    FetchTotal       { get; private set; }
    public string StatusMessage    { get; private set; } = "";

    public event Action? StateChanged;

    public ShoppingListService(
        ItemResolverService resolver,
        UniversalisService  universalis,
        Configuration       cfg,
        IPluginLog          log)
    {
        _resolver    = resolver;
        _universalis = universalis;
        _cfg         = cfg;
        _log         = log;
    }

    // ── Step 1: Load and resolve items from file ──────────────────────────────

    public async Task LoadFileAsync(string filePath, CancellationToken ct = default)
    {
        IsLoading     = true;
        StatusMessage = "Loading item database…";
        StateChanged?.Invoke();

        try
        {
            await _resolver.LoadDataAsync(ct);
            StatusMessage = "Parsing file…";
            StateChanged?.Invoke();

            LoadedItems = await Task.Run(() => _resolver.ParseFile(filePath), ct);

            _log.Information($"[HMS] Parsed {LoadedItems.Count} items from {filePath}");
            StatusMessage = $"Loaded {LoadedItems.Count} items. " +
                            $"{LoadedItems.Count(i => !i.IsResolved)} unresolved.";
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] LoadFileAsync error: {ex}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            StateChanged?.Invoke();
        }
    }

    // ── Step 2: Fetch prices from Universalis ─────────────────────────────────

    public async Task FetchPricesAsync(
        string?           playerDC,
        string?           playerWorld,
        CancellationToken ct = default)
    {
        if (LoadedItems.Count == 0) return;

        IsFetchingPrices = true;
        FetchProgress    = 0;
        StatusMessage    = "Loading Universalis catalogue…";
        StateChanged?.Invoke();

        try
        {
            await _universalis.LoadCatalogueAsync(ct);

            // Determine which datacenters to query
            var targetDCs = BuildTargetDCList(playerDC);
            if (targetDCs.Count == 0)
            {
                StatusMessage = "No datacenters found — check Universalis connectivity.";
                return;
            }

            var resolvedItems = LoadedItems.Where(i => i.IsResolved).ToList();
            FetchTotal = resolvedItems.Count * targetDCs.Count;
            StatusMessage = $"Fetching prices for {resolvedItems.Count} items across {targetDCs.Count} DCs…";
            StateChanged?.Invoke();

            // Fetch all DC×item combinations
            var allListings = new Dictionary<int, List<MarketListing>>();
            foreach (var (id, _) in resolvedItems.GroupBy(i => i.ItemId).Select(g => (g.Key, g)))
                allListings[id] = [];

            foreach (var dc in targetDCs)
            {
                if (ct.IsCancellationRequested) break;

                var itemIds = resolvedItems.Select(i => i.ItemId).Distinct().ToList();
                StatusMessage = $"Querying {dc} ({itemIds.Count} items)…";
                StateChanged?.Invoke();

                var dcResults = await _universalis.FetchListingsAsync(
                    itemIds, dc, _cfg.PreferNQ, ct);

                foreach (var (id, listings) in dcResults)
                {
                    if (!allListings.ContainsKey(id)) allListings[id] = [];
                    allListings[id].AddRange(listings);
                }

                FetchProgress += itemIds.Count;
                StateChanged?.Invoke();
            }

            // Attach listings to items and build the plan
            foreach (var item in resolvedItems)
            {
                if (allListings.TryGetValue(item.ItemId, out var listings))
                    item.AvailableListings = listings;
            }

            CurrentPlan  = BuildPlan(resolvedItems, playerWorld);
            StatusMessage = "Price fetch complete.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Price fetch cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] FetchPricesAsync error: {ex}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsFetchingPrices = false;
            StateChanged?.Invoke();
        }
    }

    // ── Plan builder ──────────────────────────────────────────────────────────

    private ShoppingPlan BuildPlan(List<ShoppingItem> resolvedItems, string? playerWorld)
    {
        var plan = new ShoppingPlan();

        // Excluded items are omitted from the plan entirely.
        foreach (var item in LoadedItems.Where(i => !i.IsResolved && !i.Excluded))
            plan.Unresolved.Add(item);

        // For each resolved, non-excluded item, find the best source world
        var worldGroups = new Dictionary<string, (string dc, List<ShoppingItem> items)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in resolvedItems.Where(i => !i.Excluded))
        {
            var listings = item.AvailableListings;

            // Restrict to current world only when that setting is on
            if (_cfg.OnlyCurrentWorld && !string.IsNullOrEmpty(playerWorld))
                listings = listings
                    .Where(l => l.WorldName.Equals(playerWorld, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            // Strip listings from disabled datacenters
            if (_cfg.DisabledDataCenters.Count > 0)
                listings = listings
                    .Where(l => !_cfg.DisabledDataCenters.Contains(FindDCForWorld(l.WorldName)))
                    .ToList();

            if (listings.Count == 0)
            {
                item.Status = PurchaseStatus.NotListed;
                plan.NotListed.Add(item);
                continue;
            }

            var best = UniversalisService.FindBestSource(
                listings, item.QuantityNeeded, _cfg.PreferNQ);

            if (best == null)
            {
                // Listings exist but no single world has enough stock — take cheapest from filtered set
                var cheapest = listings
                    .OrderBy(l => l.PricePerUnit).First();
                best = (cheapest.WorldName, cheapest.PricePerUnit,
                        cheapest.PricePerUnit * item.QuantityNeeded);
            }

            item.PricePerUnit = best.Value.pricePerUnit;
            item.TotalPrice   = best.Value.totalCost;
            item.IsHighValue  = item.PricePerUnit > _cfg.MaxPriceAutoApprove;

            var worldName = best.Value.world;
            var dc        = FindDCForWorld(worldName);

            var key = worldName;
            if (!worldGroups.ContainsKey(key))
                worldGroups[key] = (dc, []);
            worldGroups[key].items.Add(item);
        }

        // Group into DC → World hierarchy
        var dcDict = new Dictionary<string, DataCenterGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var (worldName, (dc, items)) in worldGroups)
        {
            if (!dcDict.TryGetValue(dc, out var dcGroup))
            {
                dcGroup     = new DataCenterGroup { DataCenterName = dc };
                dcDict[dc]  = dcGroup;
            }

            var worldId = _resolver.WorldMap.Values
                .FirstOrDefault(w => w.Name.Equals(worldName, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;

            dcGroup.Worlds.Add(new WorldGroup
            {
                WorldName = worldName,
                WorldId   = worldId,
                // Sort items by price descending (expensive items first so user notices)
                Items     = [.. items.OrderByDescending(i => i.TotalPrice)],
            });
        }

        // Sort worlds within each DC: most items first
        foreach (var (_, dcGroup) in dcDict)
        {
            dcGroup.Worlds = [.. dcGroup.Worlds.OrderByDescending(w => w.Items.Count)];
            plan.Groups.Add(dcGroup);
        }

        return plan;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<string> BuildTargetDCList(string? playerDC)
    {
        if (_cfg.OnlyCurrentWorld || _cfg.OnlyCurrentDC)
            return playerDC != null ? [playerDC] : [];

        // Restrict to the player's region — cross-region travel is not possible in-game.
        // If the player DC is unknown, fall back to all DCs.
        var playerRegion = playerDC != null
            ? _universalis.DataCenters
                .FirstOrDefault(d => d.Name.Equals(playerDC, StringComparison.OrdinalIgnoreCase))
                ?.Region
            : null;

        var dcs = _universalis.DataCenters
            .Where(d => !string.IsNullOrWhiteSpace(d.Name)
                     && !_cfg.DisabledDataCenters.Contains(d.Name)
                     && (playerRegion == null ||
                         d.Region.Equals(playerRegion, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Name)
            .ToList();

        // Put player's own DC first so cheapest local options surface early
        if (playerDC != null)
        {
            dcs.Remove(playerDC);
            dcs.Insert(0, playerDC);
        }

        return dcs;
    }

    private string FindDCForWorld(string worldName)
    {
        foreach (var dc in _universalis.DataCenters)
        {
            var worldIds = dc.Worlds;
            foreach (var wId in worldIds)
            {
                if (_resolver.WorldMap.TryGetValue(wId, out var w) &&
                    w.Name.Equals(worldName, StringComparison.OrdinalIgnoreCase))
                    return dc.Name;
            }
            // Also check by name via the Universalis worlds list
            var worldEntry = _universalis.Worlds.FirstOrDefault(w =>
                w.Name.Equals(worldName, StringComparison.OrdinalIgnoreCase));
            if (worldEntry != null)
            {
                var match = _universalis.DataCenters.FirstOrDefault(d =>
                    d.Worlds.Contains(worldEntry.Id));
                if (match != null) return match.Name;
            }
        }
        return "Unknown";
    }
}
