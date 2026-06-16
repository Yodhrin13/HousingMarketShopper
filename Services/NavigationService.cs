using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState;
using FFXIVClientStructs.FFXIV.Client.Game;
using HousingMarketShopper.Models;


namespace HousingMarketShopper.Services;

/// <summary>
/// Drives the automated shopping loop via Lifestream IPC and chat commands.
/// All navigation (world travel, DC travel, MB entry) is delegated to Lifestream
/// via <c>/li</c> commands — no vnavmesh dependency.
/// </summary>
public sealed class NavigationService : IDisposable
{
    // ── Lifestream IPC ────────────────────────────────────────────────────────
    private ICallGateSubscriber<bool>? _lifestreamIsBusy;

    private readonly ICommandManager    _commands;
    private readonly IFramework         _framework;
    private readonly MarketboardService _mb;
    private readonly IObjectTable       _objects;
    private readonly IClientState       _clientState;
    private readonly IPluginLog         _log;
    private readonly Configuration      _cfg;


    // ── State ─────────────────────────────────────────────────────────────────
    public bool   IsRunning     { get; private set; }
    public bool   IsPaused      { get; private set; }
    public string CurrentAction { get; private set; } = "";
    public List<LogEntry> Log   { get; } = [];

    /// <summary>True when the loop is paused specifically because inventory is nearly full.</summary>
    public bool IsInventoryPause     { get; private set; }
    /// <summary>Free slots at the time of the inventory pause.</summary>
    public int  InventoryFreeSlots   { get; private set; }
    /// <summary>Minimum extra slots the user needs to deposit before resuming.</summary>
    public int  InventorySlotsNeeded { get; private set; }
    /// <summary>Total items remaining in the plan at the time of the inventory pause.</summary>
    public int  InventoryFutureItems { get; private set; }

    public bool IpcReady => _lifestreamIsBusy != null;

    /// <summary>Gil actually spent during the current/last run.</summary>
    public int TotalActualSpend    { get; private set; }
    /// <summary>Estimated total cost of the plan at run start.</summary>
    public int TotalEstimatedSpend { get; private set; }

    /// <summary>
    /// Items that were not fully purchased during the last shopping session.
    /// Populated once the loop finishes (or is aborted/errored).
    /// Cleared at the start of each new run.
    /// </summary>
    public List<ShoppingItem> MissedItems { get; } = [];

    // ── Cross-session resume ──────────────────────────────────────────────────
    private readonly string _runStatePath;
    private bool            _hasSavedRun;
    private ShoppingPlan?       _activePlan;
    private List<ShoppingItem>  _activeOverflow = [];
    private string?         _runPlayerDc;
    private string?         _runPlayerWorld;

    private static readonly JsonSerializerOptions RunJsonOpts = new() { WriteIndented = false };

    /// <summary>True when an interrupted run snapshot exists on disk.</summary>
    public bool    HasSavedRun  => _hasSavedRun;
    /// <summary>Human-readable summary of the saved run, for the resume banner.</summary>
    public string? SavedRunInfo { get; private set; }

    public event Action? StateChanged;

    private sealed class RunStateDto
    {
        public string?           PlayerDc    { get; set; }
        public string?           PlayerWorld { get; set; }
        public DateTime          SavedAt     { get; set; }
        public List<RunItemDto>  Items       { get; set; } = [];
    }

    private sealed class RunItemDto
    {
        public int                 ItemId            { get; set; }
        public string              Name              { get; set; } = "";
        public string?             DyeName           { get; set; }
        public int                 QuantityRemaining { get; set; }
        public int                 PricePerUnit      { get; set; }
        public bool                IsHighValue       { get; set; }
        public ResolveQuality      ResolveQuality    { get; set; }
        public List<MarketListing> AvailableListings { get; set; } = [];
    }

    public NavigationService(
        IDalamudPluginInterface pi,
        ICommandManager         commands,
        IFramework              framework,
        MarketboardService      mb,
        IObjectTable            objects,
        IClientState            clientState,
        Configuration           cfg,
        IPluginLog              log)
    {
        _commands    = commands;
        _framework   = framework;
        _mb          = mb;
        _objects     = objects;
        _clientState = clientState;
        _cfg         = cfg;
        _log         = log;
        _runStatePath = Path.Combine(pi.GetPluginConfigDirectory(), "runstate.json");

        ProbeSavedRun();

        try
        {
            _lifestreamIsBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        }
        catch (Exception ex) { _log.Warning($"[HMS] Lifestream IPC unavailable: {ex.Message}"); }
    }

    // ── Shopping loop ─────────────────────────────────────────────────────────

    public async Task RunShoppingLoopAsync(
        ShoppingPlan     plan,
        string?          currentDcName,
        string?          currentWorldName,
        Func<ShoppingItem, Task<bool>> confirmHighValue,
        CancellationToken ct = default)
    {
        IsRunning           = true;
        IsPaused            = false;
        TotalActualSpend    = 0;
        TotalEstimatedSpend = plan.TotalEstimatedCost;
        MissedItems.Clear();
        StateChanged?.Invoke();

        // Tracks every overflow (re-routed) ShoppingItem created during the run
        // so we can compute per-item purchased totals for the end-of-run summary.
        var allOverflowItems = new List<ShoppingItem>();

        // Wire run-state persistence so a crash/logout can be resumed next session.
        _activePlan     = plan;
        _activeOverflow = allOverflowItems;
        _runPlayerDc    = currentDcName;
        _runPlayerWorld = currentWorldName;
        SaveRunState();

        try
        {
            var congestedWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Flatten plan worlds once for look-ahead calculations.
            var allPlanWorlds = plan.Groups.SelectMany(g => g.Worlds).ToList();

            for (var dcIdx = 0; dcIdx < plan.Groups.Count; dcIdx++)
            {
                if (ct.IsCancellationRequested) break;
                var dcGroup = plan.Groups[dcIdx];

                // fallbackQueue accumulates items re-routed away from congested worlds,
                // keyed by the replacement world name.
                var fallbackQueue = new Dictionary<string, List<ShoppingItem>>(
                    StringComparer.OrdinalIgnoreCase);

                for (var wIdx = 0; wIdx < dcGroup.Worlds.Count; wIdx++)
                {
                    if (ct.IsCancellationRequested) break;
                    var worldGroup = dcGroup.Worlds[wIdx];

                    // Items still pending on worlds that come AFTER this one in the plan.
                    var futureItems = dcGroup.Worlds.Skip(wIdx + 1).Sum(w => w.PendingCount)
                                    + plan.Groups.Skip(dcIdx + 1).SelectMany(g => g.Worlds).Sum(w => w.PendingCount);

                    var thisWorldPending = worldGroup.PendingCount;

                    // ── Predictive inventory check (Option C) ─────────────────
                    if (_cfg.AutoInventoryPause)
                        await CheckInventoryAndPauseAsync(
                            worldGroup.WorldName, thisWorldPending, futureItems, ct);

                    if (!string.Equals(worldGroup.WorldName, currentWorldName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var arrived = await TeleportToWorldAsync(worldGroup.WorldName, ct);
                        if (!arrived)
                        {
                            congestedWorlds.Add(worldGroup.WorldName);
                            LogAction($"(!) {worldGroup.WorldName} is congested — re-routing items.", LogTag.Warning);
                            foreach (var i in worldGroup.Items)
                            {
                                var alt = FindFallbackWorld(i, congestedWorlds);
                                if (alt == null)
                                {
                                    i.Status = PurchaseStatus.Skipped;
                                    LogAction($"  No fallback for {i.Name} — skipped.", LogTag.Error);
                                }
                                else
                                {
                                    if (!fallbackQueue.ContainsKey(alt)) fallbackQueue[alt] = [];
                                    fallbackQueue[alt].Add(i);
                                    LogAction($"  {i.Name} -> {alt}", LogTag.Warning);
                                }
                            }
                            continue;
                        }
                        currentWorldName = worldGroup.WorldName;
                    }

                    await NavigateToMarketboardAsync(ct);
                    await PurchaseWorldItemsAsync(worldGroup.Items, confirmHighValue,
                        fallbackQueue, allOverflowItems, congestedWorlds, worldGroup.WorldName, ct);

                    if (!ct.IsCancellationRequested)
                        await _mb.CloseMarketboardAsync(ct);
                }

                // Drain the fallback queue. PurchaseWorldItemsAsync may add new entries
                // (chained re-routes), so we loop until the queue is fully empty rather
                // than using a single foreach (which cannot see entries added mid-iteration).
                while (fallbackQueue.Count > 0 && !ct.IsCancellationRequested)
                {
                    // Snapshot current entries and clear so new re-routes go into the
                    // same dict and are picked up on the next iteration of this while loop.
                    var batch = fallbackQueue.ToList();
                    fallbackQueue.Clear();

                    foreach (var (fallbackWorld, items) in batch)
                    {
                        if (ct.IsCancellationRequested) break;

                        if (_cfg.AutoInventoryPause)
                            await CheckInventoryAndPauseAsync(
                                fallbackWorld, items.Count(i => i.Status == PurchaseStatus.Pending), 0, ct);

                        if (!string.Equals(fallbackWorld, currentWorldName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            var arrived = await TeleportToWorldAsync(fallbackWorld, ct);
                            if (!arrived)
                            {
                                congestedWorlds.Add(fallbackWorld);
                                LogAction($"(!) Fallback world {fallbackWorld} also congested — items skipped.", LogTag.Error);
                                foreach (var i in items) i.Status = PurchaseStatus.Skipped;
                                continue;
                            }
                            currentWorldName = fallbackWorld;
                        }

                        await NavigateToMarketboardAsync(ct);
                        await PurchaseWorldItemsAsync(items, confirmHighValue,
                            fallbackQueue, allOverflowItems, congestedWorlds, fallbackWorld, ct);

                        if (!ct.IsCancellationRequested)
                            await _mb.CloseMarketboardAsync(ct);
                    }
                }
            }

            LogAction("Shopping complete!", LogTag.Success);
        }
        catch (OperationCanceledException)
        {
            LogAction("Shopping aborted.", LogTag.Warning);
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] Shopping loop error: {ex}");
            LogAction($"Error: {ex.Message}");
        }
        finally
        {
            BuildMissedItemsSummary(plan, allOverflowItems);
            // Run finished (completed or user-aborted) — discard the resume snapshot.
            // Only a hard crash/logout leaves it on disk for next session.
            ClearRunState();
            _activePlan     = null;
            _activeOverflow = [];
            IsRunning = false;
            StateChanged?.Invoke();
        }
    }

    private async Task PurchaseWorldItemsAsync(
        IEnumerable<ShoppingItem>              items,
        Func<ShoppingItem, Task<bool>>         confirmHighValue,
        Dictionary<string, List<ShoppingItem>> fallbackQueue,
        List<ShoppingItem>                     allOverflowItems,
        HashSet<string>                        congestedWorlds,
        string                                 currentWorld,
        CancellationToken                      ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            await WaitWhilePausedAsync(ct);

            // Emergency inventory check — catches unexpected overflows mid-world.
            if (_cfg.AutoInventoryPause)
            {
                var free = await GetFreeInventorySlotsAsync();
                if (free <= _cfg.InventoryEmergencyThreshold)
                    await CheckInventoryAndPauseAsync(currentWorld, 1, 0, ct);
            }

            if (item.IsHighValue && !item.PurchaseConfirmed)
            {
                var confirmed = await confirmHighValue(item);
                if (!confirmed)
                {
                    item.Status = PurchaseStatus.Skipped;
                    LogAction($"Skipped: {item.Name} ({item.PricePerUnit:N0} gil)", LogTag.Warning);
                    continue;
                }
                item.PurchaseConfirmed = true;
            }

            await PurchaseItemAsync(item, ct);

            // Persist progress after every item so a crash loses at most one purchase.
            SaveRunState();

            // Re-route if partially purchased OR not listed at all on this world.
            var needsReroute = item.Status == PurchaseStatus.Partial
                            || item.Status == PurchaseStatus.NotListed;
            if (needsReroute)
            {
                var remaining = item.Status == PurchaseStatus.NotListed
                    ? item.QuantityNeeded
                    : item.QuantityNeeded - item.QuantityPurchased;

                var altWorld = item.AvailableListings
                    .Where(l => !l.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase)
                             && !congestedWorlds.Contains(l.WorldName))
                    .OrderBy(l => l.PricePerUnit)
                    .FirstOrDefault()?.WorldName;

                if (altWorld != null)
                {
                    var overflow = new ShoppingItem
                    {
                        Name              = item.Name,
                        DyeName           = item.DyeName,
                        ItemId            = item.ItemId,
                        QuantityNeeded    = remaining,
                        PricePerUnit      = item.PricePerUnit,
                        TotalPrice        = item.PricePerUnit * remaining,
                        IsHighValue       = item.IsHighValue,
                        PurchaseConfirmed = item.PurchaseConfirmed,
                        AvailableListings = item.AvailableListings,
                        ResolveQuality    = item.ResolveQuality,
                        SourceWorld       = altWorld,
                    };
                    if (!fallbackQueue.ContainsKey(altWorld)) fallbackQueue[altWorld] = [];
                    fallbackQueue[altWorld].Add(overflow);
                    allOverflowItems.Add(overflow); // track for end-of-run summary
                    LogAction($"  Routing {remaining}× {item.Name} -> {altWorld}", LogTag.Warning);
                }
                else
                {
                    LogAction($"  No alternate source for remaining {remaining}× {item.Name}", LogTag.Error);
                }
            }
        }
    }

    private static string? FindFallbackWorld(ShoppingItem item, HashSet<string> exclude)
    {
        return item.AvailableListings
            .Where(l => !exclude.Contains(l.WorldName))
            .OrderBy(l => l.PricePerUnit)
            .FirstOrDefault()
            ?.WorldName;
    }

    /// <summary>
    /// Walks the plan and logs every intended teleport and purchase without travelling
    /// or buying anything — lets the user validate the route and prices first.
    /// </summary>
    public void SimulateRun(ShoppingPlan plan)
    {
        Log.Clear();
        LogAction("── Dry run — no teleport, no purchases ──", LogTag.Info);

        var totalGil   = 0;
        var worldCount = 0;
        foreach (var dc in plan.Groups)
        {
            LogAction($"DC: {dc.DataCenterName}", LogTag.Navigation);
            foreach (var w in dc.Worlds)
            {
                worldCount++;
                LogAction(
                    $"  -> {w.WorldName}  ({w.Items.Count} items, ~{w.TotalEstimatedCost:N0} gil)",
                    LogTag.Navigation);
                foreach (var item in w.Items.OrderByDescending(i => i.TotalPrice))
                {
                    var tag = item.IsHighValue ? LogTag.Warning : LogTag.Purchase;
                    LogAction(
                        $"      buy {item.QuantityNeeded}× {item.Name} @ {item.PricePerUnit:N0} " +
                        $"= {item.TotalPrice:N0}{(item.IsHighValue ? "  (high value — would prompt)" : "")}",
                        tag);
                    totalGil += item.TotalPrice;
                }
            }
        }

        if (plan.Unresolved.Count > 0)
            LogAction($"  {plan.Unresolved.Count} unresolved item(s) skipped.", LogTag.Error);
        if (plan.NotListed.Count > 0)
            LogAction($"  {plan.NotListed.Count} not-listed item(s) skipped.", LogTag.Warning);

        LogAction(
            $"── Dry run complete: {plan.TotalItemCount} items across {worldCount} world(s), " +
            $"~{totalGil:N0} gil ──", LogTag.Success);
    }

    public void Pause()  { IsPaused = true;  StateChanged?.Invoke(); }
    public void Resume() { IsPaused = false; StateChanged?.Invoke(); }
    public void Stop()   { IsRunning = false; IsPaused = false; StateChanged?.Invoke(); }

    // ── Navigation primitives ─────────────────────────────────────────────────

    private async Task<bool> TeleportToWorldAsync(string worldName, CancellationToken ct)
    {
        LogAction($"Teleporting to {worldName}…", LogTag.Navigation);
        try
        {
            await _framework.RunOnFrameworkThread(
                () => _commands.ProcessCommand($"/li {worldName}"));
        }
        catch (Exception ex) { _log.Warning($"[HMS] Teleport error: {ex.Message}"); }

        await WaitForLifestreamAsync(ct);
        var arrived = await WaitForWorldAsync(worldName, ct);
        if (arrived)
            await Task.Delay(_cfg.NavigationDelayMs, ct);
        return arrived;
    }

    private async Task NavigateToMarketboardAsync(CancellationToken ct)
    {
        // Ul'dah - Steps of Nald = 130, Steps of Thal = 131
        var territory = await _framework.RunOnFrameworkThread(() => _clientState.TerritoryType);
        if (territory != 130 && territory != 131)
        {
            LogAction("Teleporting to Ul'dah…", LogTag.Navigation);
            try
            {
                await _framework.RunOnFrameworkThread(
                    () => _commands.ProcessCommand("/li Ul'dah - Steps of Nald"));
            }
            catch (Exception ex) { _log.Warning($"[HMS] Ul'dah teleport error: {ex.Message}"); }

            await WaitForLifestreamAsync(ct);
            await Task.Delay(_cfg.NavigationDelayMs, ct);
        }

        // Brief settle so Lifestream is ready to accept a new command after world travel.
        await Task.Delay(2_000, ct);

        LogAction("Moving to marketboard…", LogTag.Navigation);
        try
        {
            await _framework.RunOnFrameworkThread(
                () => _commands.ProcessCommand("/li mb"));
        }
        catch (Exception ex) { _log.Warning($"[HMS] MB navigation error: {ex.Message}"); }

        // Wait for Lifestream to open ItemSearch (it navigates to the board AND opens it).
        var opened = await _mb.WaitForItemSearchOpenAsync(90_000, ct);
        if (!opened)
            _log.Warning("[HMS] Timed out waiting for ItemSearch to open after /li mb.");
        await Task.Delay(_cfg.NavigationDelayMs, ct);
    }

    private async Task PurchaseItemAsync(ShoppingItem item, CancellationToken ct)
    {
        LogAction($"Purchasing {item.QuantityNeeded}× {item.Name} @ {item.PricePerUnit:N0} gil…", LogTag.Purchase);

        // For high-value confirmed items apply the same price-drift tolerance that
        // MarketboardService.InitiatePurchaseUnsafe allows, so listings slightly above the
        // Universalis snapshot pass the candidate filter and are not incorrectly rejected.
        // For auto-approve items MaxPriceAutoApprove is the user's hard budget ceiling.
        var maxPpu = item.IsHighValue
            ? (int)(item.PricePerUnit * (1f + _cfg.MaxPricePremiumPercent / 100f))
            : _cfg.MaxPriceAutoApprove;

        var result = await _mb.PurchaseItemQuantityAsync(
            (uint)item.ItemId, item.Name, item.QuantityNeeded, maxPpu, ct);

        item.QuantityPurchased = result.QuantityPurchased;
        item.ActualSpend       = result.TotalSpent;
        TotalActualSpend      += result.TotalSpent;
        item.Status = result.Outcome switch
        {
            PurchaseOutcome.Success      => PurchaseStatus.Purchased,
            PurchaseOutcome.Partial      => PurchaseStatus.Partial,
            PurchaseOutcome.Cancelled    => PurchaseStatus.Skipped,
            PurchaseOutcome.NotListed    => PurchaseStatus.NotListed,
            PurchaseOutcome.PriceChanged => PurchaseStatus.Failed,
            _                            => PurchaseStatus.Failed,
        };

        var (statusStr, statusTag) = result.Outcome switch
        {
            PurchaseOutcome.Success   => ($"[ok] {result.QuantityPurchased}× for {result.TotalSpent:N0} gil", LogTag.Success),
            PurchaseOutcome.Partial   => ($"~ {result.QuantityPurchased}/{item.QuantityNeeded}× for {result.TotalSpent:N0} gil", LogTag.Partial),
            PurchaseOutcome.NotListed => ("[x] not listed", LogTag.Warning),
            _                         => ($"[x] {result.Outcome}: {result.FailureReason}", LogTag.Error),
        };

        LogAction($"  {item.Name}: {statusStr}", statusTag);
        StateChanged?.Invoke();
    }

    // ── Wait helpers ──────────────────────────────────────────────────────────

    private async Task WaitForLifestreamAsync(CancellationToken ct)
    {
        await Task.Delay(1500, ct);
        var timeout = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < timeout)
        {
            if (ct.IsCancellationRequested) return;
            bool busy;
            try   { busy = _lifestreamIsBusy?.InvokeFunc() ?? false; }
            catch { busy = false; }
            if (!busy) break;
            await Task.Delay(500, ct);
        }
        await Task.Delay(_cfg.NavigationDelayMs, ct);
    }

    private async Task<bool> WaitForWorldAsync(string worldName, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!ct.IsCancellationRequested)
        {
            // While paused, keep extending the deadline so manual teleports don't time out.
            if (IsPaused)
            {
                deadline = DateTime.UtcNow.AddMinutes(2);
                await Task.Delay(500, ct);
                continue;
            }

            if (DateTime.UtcNow > deadline)
                break;

            string? current = null;
            try
            {
                current = await _framework.RunOnFrameworkThread(
                    () => _objects.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString());
            }
            catch { /* ignore */ }

            if (string.Equals(current, worldName, StringComparison.OrdinalIgnoreCase))
                return true;

            await Task.Delay(1000, ct);
        }
        _log.Warning($"[HMS] Timed out waiting to arrive on {worldName} — likely congested.");
        return false;
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (IsPaused && !ct.IsCancellationRequested)
            await Task.Delay(200, ct);
    }

    // ── Inventory management ──────────────────────────────────────────────────

    /// <summary>
    /// Predictive inventory check. Loops until the player has deposited enough
    /// items that <c>freeSlots - slotsNeededHere >= InventoryPauseThreshold</c>.
    /// </summary>
    private async Task CheckInventoryAndPauseAsync(
        string worldName, int slotsNeededHere, int futureItems, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var free = await GetFreeInventorySlotsAsync();
            var projectedAfter = free - slotsNeededHere;

            if (projectedAfter >= _cfg.InventoryPauseThreshold)
                return; // enough room — proceed

            // Calculate how many slots the player needs to free up.
            var needed = _cfg.InventoryPauseThreshold - projectedAfter;

            InventoryFreeSlots   = free;
            InventorySlotsNeeded = needed;
            InventoryFutureItems = futureItems;
            IsInventoryPause     = true;

            LogAction(
                $"(!) Inventory: {free} free, ~{slotsNeededHere} needed for {worldName} " +
                $"(+{futureItems} items remain after). Deposit >={needed} items then Resume.",
                LogTag.Warning);

            IsPaused = true;
            StateChanged?.Invoke();
            await WaitWhilePausedAsync(ct);

            // Re-check after resume — the player may not have deposited enough.
            IsInventoryPause = false;
            StateChanged?.Invoke();
        }
    }

    private async Task<int> GetFreeInventorySlotsAsync()
    {
        try
        {
            return await _framework.RunOnFrameworkThread(GetFreeInventorySlotsUnsafe);
        }
        catch { return 999; }
    }

    /// <summary>
    /// Reads the player's current gil. Must be called on the framework thread —
    /// the UI Draw callback qualifies, so windows can call this directly.
    /// Returns -1 if the inventory manager is unavailable.
    /// </summary>
    public unsafe int GetPlayerGilOnFrame()
    {
        var mgr = InventoryManager.Instance();
        return mgr != null ? (int)mgr->GetInventoryItemCount(1) : -1; // item id 1 = gil
    }

    private static unsafe int GetFreeInventorySlotsUnsafe()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return 999;

        var free = 0;
        foreach (var bagType in (ReadOnlySpan<InventoryType>)[
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4])
        {
            var container = mgr->GetInventoryContainer(bagType);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0) free++;
            }
        }
        return free;
    }

    /// <summary>Teleports to Ul'dah so the player can deposit at their retainer.</summary>
    public async Task TeleportToDepositLocationAsync()
    {
        try
        {
            await _framework.RunOnFrameworkThread(
                () => _commands.ProcessCommand("/li Ul'dah - Steps of Nald"));
        }
        catch (Exception ex) { _log.Warning($"[HMS] Deposit teleport error: {ex.Message}"); }
    }

    /// <summary>
    /// Aggregates purchased vs needed quantities across original plan items and every
    /// overflow (re-routed) item. Items where total purchased &lt; total needed are
    /// written into <see cref="MissedItems"/> for display in the Progress tab.
    /// </summary>
    private void BuildMissedItemsSummary(ShoppingPlan plan, List<ShoppingItem> overflowItems)
    {
        MissedItems.Clear();

        foreach (var (meta, remaining) in AggregateRemaining(plan, overflowItems))
            MissedItems.Add(new ShoppingItem
            {
                ItemId            = meta.ItemId,
                Name              = meta.Name,
                DyeName           = meta.DyeName,
                QuantityNeeded    = remaining,
                ResolveQuality    = meta.ResolveQuality,
                AvailableListings = meta.AvailableListings,
            });

        if (MissedItems.Count > 0)
            LogAction($"(!) {MissedItems.Count} item type(s) not fully purchased — see missed items list.", LogTag.Warning);
    }

    /// <summary>
    /// Aggregates needed vs purchased per ItemId across the original plan and every
    /// overflow item, returning (meta, remaining) for each item not yet fully bought.
    /// </summary>
    private static List<(ShoppingItem meta, int remaining)> AggregateRemaining(
        ShoppingPlan plan, List<ShoppingItem> overflowItems)
    {
        var totalNeeded    = new Dictionary<int, int>();
        var totalPurchased = new Dictionary<int, int>();
        var itemMeta       = new Dictionary<int, ShoppingItem>();

        foreach (var item in plan.Groups.SelectMany(g => g.Worlds).SelectMany(w => w.Items))
        {
            totalNeeded[item.ItemId]    = totalNeeded.GetValueOrDefault(item.ItemId)    + item.QuantityNeeded;
            totalPurchased[item.ItemId] = totalPurchased.GetValueOrDefault(item.ItemId) + item.QuantityPurchased;
            itemMeta.TryAdd(item.ItemId, item);
        }

        foreach (var item in overflowItems)
        {
            totalPurchased[item.ItemId] = totalPurchased.GetValueOrDefault(item.ItemId) + item.QuantityPurchased;
            itemMeta.TryAdd(item.ItemId, item);
        }

        var result = new List<(ShoppingItem, int)>();
        foreach (var (itemId, needed) in totalNeeded)
        {
            var purchased = totalPurchased.GetValueOrDefault(itemId);
            if (purchased < needed) result.Add((itemMeta[itemId], needed - purchased));
        }
        return result;
    }

    // ── Cross-session run-state persistence ───────────────────────────────────

    private void ProbeSavedRun()
    {
        try
        {
            if (!File.Exists(_runStatePath)) return;
            _hasSavedRun = true;
            var dto = JsonSerializer.Deserialize<RunStateDto>(File.ReadAllText(_runStatePath));
            if (dto != null)
                SavedRunInfo = $"{dto.Items.Sum(i => i.QuantityRemaining)} item(s) remaining, " +
                               $"saved {dto.SavedAt.ToLocalTime():g}";
        }
        catch (Exception ex) { _log.Warning($"[HMS] ProbeSavedRun failed: {ex.Message}"); }
    }

    /// <summary>Writes the remaining items of the active run to disk for crash recovery.</summary>
    private void SaveRunState()
    {
        if (_activePlan == null) return;
        try
        {
            var remaining = AggregateRemaining(_activePlan, _activeOverflow);
            if (remaining.Count == 0) { ClearRunState(); return; }

            var dto = new RunStateDto
            {
                PlayerDc    = _runPlayerDc,
                PlayerWorld = _runPlayerWorld,
                SavedAt     = DateTime.UtcNow,
                Items       = remaining.Select(r => new RunItemDto
                {
                    ItemId            = r.meta.ItemId,
                    Name              = r.meta.Name,
                    DyeName           = r.meta.DyeName,
                    QuantityRemaining = r.remaining,
                    PricePerUnit      = r.meta.PricePerUnit,
                    IsHighValue       = r.meta.IsHighValue,
                    ResolveQuality    = r.meta.ResolveQuality,
                    AvailableListings = r.meta.AvailableListings,
                }).ToList(),
            };

            File.WriteAllText(_runStatePath, JsonSerializer.Serialize(dto, RunJsonOpts));
            _hasSavedRun = true;
        }
        catch (Exception ex) { _log.Warning($"[HMS] SaveRunState failed: {ex.Message}"); }
    }

    private void ClearRunState()
    {
        try { if (File.Exists(_runStatePath)) File.Delete(_runStatePath); }
        catch (Exception ex) { _log.Warning($"[HMS] ClearRunState failed: {ex.Message}"); }
        _hasSavedRun = false;
        SavedRunInfo = null;
    }

    /// <summary>Deletes the saved run snapshot at the user's request.</summary>
    public void DiscardSavedRun()
    {
        ClearRunState();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Loads the saved run snapshot into fresh ShoppingItems (with their remaining
    /// quantities and cached listings). Returns null if no valid snapshot exists.
    /// </summary>
    public (List<ShoppingItem> items, string? playerWorld)? LoadSavedRunItems()
    {
        try
        {
            if (!File.Exists(_runStatePath)) return null;
            var dto = JsonSerializer.Deserialize<RunStateDto>(File.ReadAllText(_runStatePath));
            if (dto == null || dto.Items.Count == 0) return null;

            var items = dto.Items.Select(d => new ShoppingItem
            {
                ItemId            = d.ItemId,
                Name              = d.Name,
                DyeName           = d.DyeName,
                QuantityNeeded    = d.QuantityRemaining,
                PricePerUnit      = d.PricePerUnit,
                IsHighValue       = d.IsHighValue,
                ResolveQuality    = d.ResolveQuality,
                AvailableListings = d.AvailableListings ?? [],
            }).ToList();
            return (items, dto.PlayerWorld);
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] LoadSavedRunItems failed: {ex.Message}");
            return null;
        }
    }

    private void LogAction(string msg, LogTag tag = LogTag.Info)
    {
        CurrentAction = msg;
        Log.Add(new LogEntry($"[{DateTime.Now:HH:mm:ss}] {msg}", tag));
        _log.Information($"[HMS] {msg}");
        StateChanged?.Invoke();
    }

    public void Dispose() { }
}
