using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
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
    private readonly string              _listsDir;

    private static readonly JsonSerializerOptions ListJsonOpts = new() { WriteIndented = true };

    public ShoppingPlan?        CurrentPlan  { get; private set; }
    public List<ShoppingItem>   LoadedItems  { get; private set; } = [];

    // World name → datacenter name, rebuilt from the Universalis catalogue at the
    // start of each plan build so per-item lookups are O(1) instead of nested scans.
    private Dictionary<string, string> _worldToDc =
        new(StringComparer.OrdinalIgnoreCase);


    // Progress state for the UI
    public bool   IsLoading        { get; private set; }
    public bool   IsFetchingPrices { get; private set; }
    public int    FetchProgress    { get; private set; }
    public int    FetchTotal       { get; private set; }
    public string StatusMessage    { get; private set; } = "";

    public event Action? StateChanged;

    public ShoppingListService(
        IDalamudPluginInterface pi,
        ItemResolverService resolver,
        UniversalisService  universalis,
        Configuration       cfg,
        IPluginLog          log)
    {
        _resolver    = resolver;
        _universalis = universalis;
        _cfg         = cfg;
        _log         = log;
        _listsDir    = Path.Combine(pi.GetPluginConfigDirectory(), "lists");
    }

    // ── Saved lists ───────────────────────────────────────────────────────────

    /// <summary>Lightweight, market-data-free snapshot of a parsed/resolved item.</summary>
    private sealed class SavedItemDto
    {
        public string         RawLine          { get; set; } = "";
        public string         Name             { get; set; } = "";
        public string?        DyeName          { get; set; }
        public int            QuantityNeeded   { get; set; } = 1;
        public int            ItemId           { get; set; }
        public ResolveQuality ResolveQuality   { get; set; }
        public string?        ResolvedItemName { get; set; }
        public bool           IsManualOverride { get; set; }
        public bool           Excluded         { get; set; }
    }

    public List<string> GetSavedListNames()
    {
        if (!Directory.Exists(_listsDir)) return [];
        return [.. Directory.GetFiles(_listsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    public void SaveList(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || LoadedItems.Count == 0) return;
        try
        {
            Directory.CreateDirectory(_listsDir);
            var dtos = LoadedItems.Select(i => new SavedItemDto
            {
                RawLine          = i.RawLine,
                Name             = i.Name,
                DyeName          = i.DyeName,
                QuantityNeeded   = i.QuantityNeeded,
                ItemId           = i.ItemId,
                ResolveQuality   = i.ResolveQuality,
                ResolvedItemName = i.ResolvedItemName,
                IsManualOverride = i.IsManualOverride,
                Excluded         = i.Excluded,
            }).ToList();

            File.WriteAllText(PathFor(name), JsonSerializer.Serialize(dtos, ListJsonOpts));
            StatusMessage = $"Saved list '{name}'.";
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] SaveList error: {ex.Message}");
            StatusMessage = $"Save failed: {ex.Message}";
        }
        StateChanged?.Invoke();
    }

    public void LoadSavedList(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return;
        try
        {
            var dtos = JsonSerializer.Deserialize<List<SavedItemDto>>(File.ReadAllText(path)) ?? [];
            LoadedItems = dtos.Select(d => new ShoppingItem
            {
                RawLine          = d.RawLine,
                Name             = d.Name,
                DyeName          = d.DyeName,
                QuantityNeeded   = d.QuantityNeeded,
                ItemId           = d.ItemId,
                ResolveQuality   = d.ResolveQuality,
                ResolvedItemName = d.ResolvedItemName,
                IsManualOverride = d.IsManualOverride,
                Excluded         = d.Excluded,
            }).ToList();
            CurrentPlan   = null;   // prices must be re-fetched for the loaded set
            StatusMessage = $"Loaded '{name}' ({LoadedItems.Count} items). Fetch prices to build a plan.";
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] LoadSavedList error: {ex.Message}");
            StatusMessage = $"Load failed: {ex.Message}";
        }
        StateChanged?.Invoke();
    }

    public void DeleteSavedList(string name)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { _log.Error($"[HMS] DeleteSavedList error: {ex.Message}"); }
        StateChanged?.Invoke();
    }

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrEmpty(safe)) safe = "list";
        return Path.Combine(_listsDir, safe + ".json");
    }

    // ── Quick add ─────────────────────────────────────────────────────────────

    /// <summary>True once the item-name catalogue is loaded and search is usable.</summary>
    public bool IsItemDataReady => _resolver.IsItemDataLoaded;

    /// <summary>Loads the item catalogue if it isn't already (for quick-add without a file).</summary>
    public async Task EnsureItemDataAsync(CancellationToken ct = default)
    {
        if (_resolver.IsItemDataLoaded || IsLoading) return;

        IsLoading     = true;
        StatusMessage = "Loading item database…";
        StateChanged?.Invoke();
        try
        {
            await _resolver.LoadDataAsync(ct);
            _resolver.Overrides = new Dictionary<string, int>(
                _cfg.ResolutionOverrides, StringComparer.OrdinalIgnoreCase);
            StatusMessage = $"Item database ready ({_resolver.ItemNames.Count:N0} items).";
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] EnsureItemDataAsync error: {ex}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Adds a directly-chosen (already resolved) item to the loaded list, or bumps
    /// its quantity if it's already present.
    /// </summary>
    public void AddQuickItem(int itemId, string name)
    {
        var existing = LoadedItems.FirstOrDefault(i => i.ItemId == itemId && i.IsResolved);
        if (existing != null)
        {
            existing.QuantityNeeded++;
        }
        else
        {
            LoadedItems.Add(new ShoppingItem
            {
                RawLine          = name,
                Name             = name,
                QuantityNeeded   = 1,
                ItemId           = itemId,
                ResolveQuality   = ResolveQuality.Exact,
                ResolvedItemName = name,
            });
        }
        StateChanged?.Invoke();
    }

    // ── Manual resolution ─────────────────────────────────────────────────────

    /// <summary>Case-insensitive item-name search for the manual-resolution picker.</summary>
    public List<(int id, string name)> SearchItems(string query, int limit = 100)
        => _resolver.SearchItems(query, limit);

    /// <summary>True if the item is sold by an NPC vendor for gil (likely cheaper than the MB).</summary>
    public bool IsVendorSold(int itemId) => _resolver.IsNpcSold(itemId);

    /// <summary>
    /// Pins a loaded item to a specific item ID, marks it a manual override, and
    /// persists the mapping so future imports of the same name resolve automatically.
    /// </summary>
    public void ApplyManualResolution(ShoppingItem item, int itemId)
    {
        item.ItemId           = itemId;
        item.ResolveQuality   = ResolveQuality.Exact;
        item.ResolvedItemName = _resolver.GetItemName(itemId);
        item.IsManualOverride = true;
        item.FuzzyDistance    = 0;
        item.ResolveWarning   = null;

        var key = item.Name.ToLowerInvariant().Trim();
        _cfg.ResolutionOverrides[key] = itemId;
        _cfg.Save();
        StateChanged?.Invoke();
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

            // Apply the user's pinned resolution overrides before parsing.
            _resolver.Overrides = new Dictionary<string, int>(
                _cfg.ResolutionOverrides, StringComparer.OrdinalIgnoreCase);

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
        foreach (var item in LoadedItems.Where(i => !i.IsResolved && !i.Excluded))
            plan.Unresolved.Add(item);
        return BuildPlanItems(plan, resolvedItems, playerWorld);
    }

    /// <summary>
    /// Rebuilds a shopping plan from a set of previously-missed items, reusing
    /// their existing Universalis listings without a network fetch.
    /// </summary>
    public ShoppingPlan BuildRetryPlan(List<ShoppingItem> missedItems, string? playerWorld)
    {
        foreach (var item in missedItems)
        {
            item.Status            = PurchaseStatus.Pending;
            item.QuantityPurchased = 0;
            item.ActualSpend       = 0;
            item.PurchaseConfirmed = false;
            item.Excluded          = false;
        }
        var plan    = BuildPlanItems(new ShoppingPlan(), missedItems, playerWorld);
        CurrentPlan = plan;
        return plan;
    }

    private ShoppingPlan BuildPlanItems(ShoppingPlan plan, List<ShoppingItem> resolvedItems, string? playerWorld)
    {
        BuildWorldToDcMap();

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
                // No single world has enough stock. Estimate the realistic cost by buying
                // the cheapest listings across ALL worlds (the run re-routes overflow between
                // worlds), instead of assuming the full quantity at the single cheapest price.
                best = EstimateAcrossWorlds(listings, item.QuantityNeeded);
            }

            item.PricePerUnit = best.Value.pricePerUnit;
            item.TotalPrice   = best.Value.totalCost;
            item.IsHighValue  = item.PricePerUnit > _cfg.MaxPriceAutoApprove;

            var worldName = best.Value.world;
            item.SourceWorld = worldName;
            var dc        = FindDCForWorld(worldName);

            var key = worldName;
            if (!worldGroups.ContainsKey(key))
                worldGroups[key] = (dc, []);
            worldGroups[key].items.Add(item);
        }

        // Snapshot the absolute-cheapest assignment (pre-consolidation) so the UI can
        // report how much the consolidation tolerance costs in exchange for fewer hops.
        plan.PreConsolidationCost       = worldGroups.Sum(kv => kv.Value.items.Sum(i => i.TotalPrice));
        plan.PreConsolidationWorldCount = worldGroups.Count;

        // ── Consolidation pass ─────────────────────────────────────────────────
        // Reassign items to an already-planned world when the price difference is
        // within the configured tolerance, reducing the number of world hops.
        if (_cfg.WorldConsolidationTolerance > 0)
        {
            var tolerance = _cfg.WorldConsolidationTolerance / 100.0;
            bool changed;
            do
            {
                changed = false;
                foreach (var srcWorld in worldGroups.Keys.ToList())
                {
                    if (!worldGroups.TryGetValue(srcWorld, out var srcEntry)) continue;

                    foreach (var item in srcEntry.items.ToList())
                    {
                        var maxAcceptableTotal = (int)(item.TotalPrice * (1 + tolerance));
                        string? bestTarget = null;
                        int     bestCount  = srcEntry.items.Count;

                        foreach (var (tgtWorld, (_, tgtItems)) in worldGroups)
                        {
                            if (tgtWorld.Equals(srcWorld, StringComparison.OrdinalIgnoreCase)) continue;
                            if (tgtItems.Count <= bestCount) continue;

                            var cost = CalcWorldCost(item, tgtWorld);
                            if (cost == null || cost.Value.total > maxAcceptableTotal) continue;

                            bestTarget = tgtWorld;
                            bestCount  = tgtItems.Count;
                        }

                        if (bestTarget != null)
                        {
                            var cost = CalcWorldCost(item, bestTarget)!.Value;
                            srcEntry.items.Remove(item);
                            item.PricePerUnit = cost.ppu;
                            item.TotalPrice   = cost.total;
                            item.IsHighValue  = item.PricePerUnit > _cfg.MaxPriceAutoApprove;
                            item.SourceWorld  = bestTarget;
                            worldGroups[bestTarget].items.Add(item);

                            if (srcEntry.items.Count == 0)
                                worldGroups.Remove(srcWorld);

                            changed = true;
                            break;
                        }
                    }
                    if (changed) break;
                }
            } while (changed);
        }

        // ── Budget cap ─────────────────────────────────────────────────────────
        // Drop the most expensive items until the plan total fits under the cap.
        if (_cfg.BudgetCap > 0)
        {
            var assigned = worldGroups
                .SelectMany(kv => kv.Value.items.Select(i => (world: kv.Key, item: i)))
                .OrderByDescending(x => x.item.TotalPrice)
                .ToList();

            var runningTotal = assigned.Sum(x => x.item.TotalPrice);
            foreach (var (world, item) in assigned)
            {
                if (runningTotal <= _cfg.BudgetCap) break;
                worldGroups[world].items.Remove(item);
                item.Status = PurchaseStatus.Skipped;
                plan.OverBudget.Add(item);
                runningTotal -= item.TotalPrice;
                if (worldGroups[world].items.Count == 0)
                    worldGroups.Remove(world);
            }
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

    /// <summary>
    /// Greedily fills <paramref name="quantityNeeded"/> from the cheapest listings across
    /// all worlds (respecting NQ preference), summing actual listing prices. Used when no
    /// single world can fill the order — the run buys across worlds via overflow re-routing.
    /// Returns the world supplying the most units as the primary source, the worst per-unit
    /// price consumed (so the run's price tolerance stays generous enough), and the true total.
    /// </summary>
    private (string world, int pricePerUnit, int totalCost) EstimateAcrossWorlds(
        List<MarketListing> listings, int quantityNeeded)
    {
        var candidates = _cfg.PreferNQ ? listings.Where(l => !l.IsHQ).ToList() : [.. listings];
        if (candidates.Count == 0) candidates = [.. listings];

        var sorted      = candidates.OrderBy(l => l.PricePerUnit).ToList();
        var remaining   = quantityNeeded;
        var total       = 0;
        var worstPpu    = sorted[0].PricePerUnit;
        var perWorldQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var l in sorted)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, l.Quantity);
            total      += take * l.PricePerUnit;
            worstPpu    = l.PricePerUnit;
            perWorldQty[l.WorldName] = perWorldQty.GetValueOrDefault(l.WorldName) + take;
            remaining  -= take;
        }

        // Primary source = the world contributing the most units, to minimise re-routes.
        var primaryWorld = perWorldQty
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .First().Key;

        return (primaryWorld, worstPpu, total);
    }

    private (int ppu, int total)? CalcWorldCost(ShoppingItem item, string worldName)
    {
        var listings = item.AvailableListings
            .Where(l => l.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (listings.Count == 0) return null;

        var candidates = _cfg.PreferNQ ? listings.Where(l => !l.IsHQ).ToList() : listings;
        if (candidates.Count == 0) candidates = listings;

        var sorted    = candidates.OrderBy(l => l.PricePerUnit).ToList();
        var remaining = item.QuantityNeeded;
        var total     = 0;
        var worstPpu  = 0;

        foreach (var l in sorted)
        {
            if (remaining <= 0) break;
            var take  = Math.Min(remaining, l.Quantity);
            total    += take * l.PricePerUnit;
            worstPpu  = l.PricePerUnit;
            remaining -= take;
        }

        return remaining > 0 ? null : (worstPpu, total);
    }

    /// <summary>
    /// Rebuilds the world→DC lookup from the Universalis catalogue. Each DC lists its
    /// world IDs; we resolve those IDs to names via the resolver's WorldMap, falling
    /// back to the Universalis worlds list for any the resolver doesn't know.
    /// </summary>
    private void BuildWorldToDcMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dc in _universalis.DataCenters)
        {
            foreach (var wId in dc.Worlds)
            {
                string? name = _resolver.WorldMap.TryGetValue(wId, out var w)
                    ? w.Name
                    : _universalis.Worlds.FirstOrDefault(uw => uw.Id == wId)?.Name;

                if (!string.IsNullOrEmpty(name))
                    map[name] = dc.Name;
            }
        }

        _worldToDc = map;
    }

    private string FindDCForWorld(string worldName)
        => _worldToDc.TryGetValue(worldName, out var dc) ? dc : "Unknown";
}
