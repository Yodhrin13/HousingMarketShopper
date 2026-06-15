using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HousingMarketShopper.Models;

namespace HousingMarketShopper.Services;

/// <summary>
/// Owns all marketboard addon interaction: opening the board, searching for items,
/// reading listings, and driving the purchase confirmation dialogs.
///
/// Design constraints:
///   • Every unsafe pointer dereference happens inside a private method that is
///     always dispatched through <see cref="IFramework.RunOnFrameworkThread"/>.
///   • The public API is fully safe — callers never touch raw pointers.
///   • Defensive null-checks guard every pointer access; the method returns a
///     sentinel value rather than crashing if an addon node is absent.
///
/// Patch-sensitivity notes (read before updating the game):
///   • AgentItemSearch field offsets and method names change frequently.
///     Verify <c>SearchItemId</c> and <c>SearchByItemId()</c> against the current
///     FFXIVClientStructs source before each major patch.
///   • ItemSearchResult node IDs are documented with "verify" comments below.
///     Run <c>/xllog</c> with debug node-dump logging enabled to re-confirm.
/// </summary>
public sealed class MarketboardService
{
    // ── Addon name constants ──────────────────────────────────────────────────
    private const string AddonItemSearch       = "ItemSearch";
    private const string AddonItemSearchResult = "ItemSearchResult";
    private const string AddonSelectYesno      = "SelectYesno";
    private const string AddonInputNumeric     = "InputNumeric";

    // ── Marketboard NPC name substrings (EN client) ───────────────────────────
    private static readonly string[] MbNpcNames = ["Marketboard", "Market Board"];

    // Maximum price increase (above Universalis snapshot) we auto-accept, as a
    // ratio derived from Configuration.MaxPricePremiumPercent.
    private float MaxPricePremium => _config.MaxPricePremiumPercent / 100f;

    // ── Services ──────────────────────────────────────────────────────────────
    private readonly IGameGui       _gameGui;
    private readonly IFramework     _framework;
    private readonly ITargetManager _targetManager;
    private readonly IObjectTable   _objects;
    private readonly IMarketBoard   _marketBoard;
    private readonly IPluginLog     _log;
    private readonly Configuration  _config;

    // ── Offering state ────────────────────────────────────────────────────────
    // Set before triggering a listing request; completed by OnOfferingsReceived.
    private volatile TaskCompletionSource<IReadOnlyList<IMarketBoardItemListing>>? _offeringsTcs;
    private uint _offeringsItemId;

    // Last received offerings, read by ReadCurrentListingsAsync.
    private IReadOnlyList<IMarketBoardItemListing> _lastOfferings = [];

    public MarketboardService(
        IGameGui       gameGui,
        IFramework     framework,
        ITargetManager targetManager,
        IObjectTable   objects,
        IMarketBoard   marketBoard,
        Configuration  config,
        IPluginLog     log)
    {
        _gameGui       = gameGui;
        _framework     = framework;
        _targetManager = targetManager;
        _objects       = objects;
        _marketBoard   = marketBoard;
        _config        = config;
        _log           = log;

        _marketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        var tcs = _offeringsTcs;
        if (tcs == null) return;

        // Accept if the first listing matches our item, or if the list is empty
        // (server responded with 0 listings — no ItemId to check against).
        if (offerings.ItemListings.Count > 0 &&
            offerings.ItemListings[0].ItemId != _offeringsItemId) return;

        tcs.TrySetResult(offerings.ItemListings);
    }

    // =========================================================================
    // Public entry point
    // =========================================================================

    /// <summary>
    /// Searches the currently open (or nearby) marketboard for <paramref name="itemId"/>,
    /// then greedily purchases <paramref name="quantityNeeded"/> units at or below
    /// <paramref name="maxPricePerUnit"/> gil each.
    /// </summary>
    /// <remarks>
    /// The MB must already be reachable — the caller (NavigationService) is responsible
    /// for moving the player to the board NPC before calling this method.
    /// </remarks>
    public async Task<PurchaseResult> PurchaseItemQuantityAsync(
        uint              itemId,
        string            itemName,
        int               quantityNeeded,
        int               maxPricePerUnit,
        CancellationToken ct = default)
    {
        var quantityPurchased = 0;
        var totalSpent        = 0;

        try
        {
            // ── Step 1: ensure MB is open ────────────────────────────────────
            await EnsureMarketboardOpenAsync(ct);

            // ── Step 2: search for the item ──────────────────────────────────
            await SearchForItemAsync(itemId, itemName, ct);

            // ── Step 3: greedy purchase loop ─────────────────────────────────
            var remaining = quantityNeeded;
            var passes    = 0;          // guard against infinite loops
            const int maxPasses = 10;

            while (remaining > 0 && passes++ < maxPasses)
            {
                ct.ThrowIfCancellationRequested();

                var listings = await ReadCurrentListingsAsync(ct);
                if (listings.Count == 0)
                {
                    _log.Information($"[HMS] No more listings for {itemName}.");
                    break;
                }

                // Filter and sort candidates
                var candidates = listings
                    .Where(l => l.PricePerUnit <= maxPricePerUnit
                             && (!_config.PreferNQ || !l.IsHQ))
                    .OrderBy(l => l.PricePerUnit)
                    .ToList();

                // If nothing under budget with NQ preference, relax HQ restriction
                if (candidates.Count == 0 && _config.PreferNQ)
                {
                    candidates = listings
                        .Where(l => l.PricePerUnit <= maxPricePerUnit)
                        .OrderBy(l => l.PricePerUnit)
                        .ToList();
                }

                if (candidates.Count == 0)
                {
                    _log.Information($"[HMS] No affordable listings for {itemName} " +
                                     $"(max {maxPricePerUnit:N0} gil).");
                    break;
                }

                // Purchase from the cheapest listing, taking as many as needed
                var listing = candidates[0];
                var buyQty  = Math.Min(listing.Quantity, remaining);

                _log.Debug($"[HMS] Buying {buyQty}× {itemName} @ " +
                           $"{listing.PricePerUnit:N0} from {listing.RetainerName}");

                var purchased = await PurchaseListingAsync(
                    listing, buyQty, itemId, itemName, ct);

                if (!purchased) break; // price changed or listing not found

                quantityPurchased += buyQty;
                totalSpent        += listing.PricePerUnit * buyQty;
                remaining         -= buyQty;

                if (remaining > 0)
                    await Task.Delay(_config.NavigationDelayMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return Result(PurchaseOutcome.Cancelled, quantityPurchased, totalSpent,
                          "Purchase cancelled by user.");
        }
        catch (AddonNotVisibleException ex)
        {
            _log.Warning($"[HMS] {ex.Message}");
            return Result(PurchaseOutcome.Error, quantityPurchased, totalSpent, ex.Message);
        }
        catch (PriceChangedException ex)
        {
            _log.Warning($"[HMS] {ex.Message}");
            return Result(PurchaseOutcome.PriceChanged, quantityPurchased, totalSpent, ex.Message);
        }
        catch (InsufficientFundsException ex)
        {
            _log.Warning($"[HMS] {ex.Message}");
            return Result(PurchaseOutcome.Error, quantityPurchased, totalSpent, ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error($"[HMS] Unexpected error purchasing {itemName}: {ex}");
            return Result(PurchaseOutcome.Error, quantityPurchased, totalSpent, ex.Message);
        }

        var outcome = quantityPurchased == 0        ? PurchaseOutcome.NotListed
                    : quantityPurchased < quantityNeeded ? PurchaseOutcome.Partial
                    :                                  PurchaseOutcome.Success;

        return new PurchaseResult
        {
            ItemId            = itemId,
            ItemName          = itemName,
            QuantityRequested = quantityNeeded,
            QuantityPurchased = quantityPurchased,
            TotalSpent        = totalSpent,
            Outcome           = outcome,
        };

        PurchaseResult Result(PurchaseOutcome o, int qty, int spent, string? reason) =>
            new()
            {
                ItemId            = itemId,
                ItemName          = itemName,
                QuantityRequested = quantityNeeded,
                QuantityPurchased = qty,
                TotalSpent        = spent,
                Outcome           = o,
                FailureReason     = reason,
            };
    }

    // =========================================================================
    // Step 1 — Open marketboard
    // =========================================================================

    /// <summary>
    /// If the ItemSearch addon is already visible, returns immediately.
    /// Otherwise targets and interacts with the nearest marketboard NPC.
    /// </summary>
    private async Task EnsureMarketboardOpenAsync(CancellationToken ct)
    {
        var alreadyOpen = await _framework.RunOnFrameworkThread(
            () => AddonWaiter.IsAddonVisible(_gameGui, AddonItemSearch));

        if (alreadyOpen)
        {
            _log.Debug("[HMS] ItemSearch already visible.");
            return;
        }

        // Find and target the nearest MB EventNpc
        var targeted = await _framework.RunOnFrameworkThread(() => TargetNearestMbNpc());
        if (!targeted)
            throw new MarketboardStateException(
                "No marketboard NPC found nearby. Navigate to a marketboard first.");

        // Interact with targeted object
        await _framework.RunOnFrameworkThread(() => InteractWithTarget());
        await Task.Delay(300, ct); // let the interact animation start

        await AddonWaiter.RequireAddonAsync(
            AddonItemSearch, _gameGui, _framework, timeoutMs: 6_000, ct: ct);

        // Wait for the addon to fully initialize its internal state before calling RunSearch.
        await Task.Delay(1_000, ct);

        _log.Debug("[HMS] ItemSearch opened.");
    }

    /// <summary>
    /// Finds the nearest marketboard EventNpc and sets it as the current target.
    /// Must be called on the framework thread. Returns true if a target was found.
    /// </summary>
    private bool TargetNearestMbNpc()
    {
        var playerPos  = _objects.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
        var bestDist   = float.MaxValue;
        IGameObject? bestObj = null;

        foreach (var obj in _objects)
        {
            if (obj.ObjectKind != ObjectKind.EventObj) continue;

            var nameLower = obj.Name.TextValue.ToLowerInvariant();
            if (!MbNpcNames.Any(n => nameLower.Contains(n.ToLowerInvariant()))) continue;

            var dist = System.Numerics.Vector3.Distance(obj.Position, playerPos);
            if (dist >= bestDist) continue;

            bestDist = dist;
            bestObj  = obj;
        }

        if (bestObj is null) return false;

        _targetManager.Target = bestObj;
        _log.Debug($"[HMS] Targeted MB NPC '{bestObj.Name.TextValue}' at distance {bestDist:F1}m");
        return true;
    }

    /// <summary>
    /// Calls <c>TargetSystem::InteractWithObject</c> on the current hard target.
    /// Must be called on the framework thread.
    /// </summary>
    private unsafe void InteractWithTarget()
    {
        var target = _targetManager.Target;
        if (target == null) return;

        var gameObj = (GameObject*)target.Address;
        if (gameObj == null) return;

        TargetSystem.Instance()->InteractWithObject(gameObj, checkLineOfSight: false);
    }

    // =========================================================================
    // Step 2 — Search: RunSearch to confirm item exists, SelectItem to trigger
    //          the server listing request, then await IMarketBoard.OfferingsReceived.
    // =========================================================================

    private async Task SearchForItemAsync(uint itemId, string itemName, CancellationToken ct)
    {
        // Close any ISR left open from the previous search before opening a new one.
        await _framework.RunOnFrameworkThread(() => CloseAddonUnsafe(AddonItemSearchResult));

        // Arm the TCS before triggering the request so we never miss the event.
        _offeringsItemId = itemId;
        _offeringsTcs    = new TaskCompletionSource<IReadOnlyList<IMarketBoardItemListing>>(
                               TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _framework.RunOnFrameworkThread(() => RunTextSearchUnsafe(itemName));

            // Poll for ~3.5 s. If still empty, the game may have dropped the search —
            // re-fire RunSearch once and poll for up to 6 s more before giving up.
            int resultCount = 0;
            for (var attempt = 0; attempt < 9 && resultCount == 0; attempt++)
            {
                await Task.Delay(400, ct);
                resultCount = await _framework.RunOnFrameworkThread(
                    () => GetItemSearchResultCountUnsafe());
            }

            if (resultCount == 0)
            {
                _log.Information($"[HMS] No results after 3.5 s — re-running search for '{itemName}'.");
                await _framework.RunOnFrameworkThread(() => RunTextSearchUnsafe(itemName));

                for (var attempt = 0; attempt < 15 && resultCount == 0; attempt++)
                {
                    await Task.Delay(400, ct);
                    resultCount = await _framework.RunOnFrameworkThread(
                        () => GetItemSearchResultCountUnsafe());
                }
            }

            if (resultCount == 0)
                throw new MarketboardStateException($"RunSearch returned no results for '{itemName}'.");

            // SelectItem(0, dispatchEvent:true) fires the game's natural AtkEvent handler,
            // which opens ISR and sends the server listing request packet.
            await _framework.RunOnFrameworkThread(() => RequestItemListingsUnsafe(itemId));

            _lastOfferings = await AwaitOfferingsAsync(itemId, itemName, ct);
            _log.Debug($"[HMS] Offerings received for '{itemName}': {_lastOfferings.Count} listing(s).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — not user cancellation.
            throw new MarketboardStateException(
                $"No listing response received for '{itemName}' within 10 s.");
        }
        finally
        {
            _offeringsTcs    = null;
            _offeringsItemId = 0;
        }
    }

    /// <summary>
    /// Re-requests the listing packet for an already-selected item without
    /// re-running the text search. Used between purchases of the same item.
    /// </summary>
    private async Task RefreshListingsAsync(uint itemId, string itemName, CancellationToken ct)
    {
        _offeringsItemId = itemId;
        _offeringsTcs    = new TaskCompletionSource<IReadOnlyList<IMarketBoardItemListing>>(
                               TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _framework.RunOnFrameworkThread(() => RequestItemListingsUnsafe(itemId));

            _lastOfferings = await AwaitOfferingsAsync(itemId, itemName, ct);
            _log.Debug($"[HMS] Listings refreshed for '{itemName}': {_lastOfferings.Count} listing(s).");
        }
        finally
        {
            _offeringsTcs    = null;
            _offeringsItemId = 0;
        }
    }

    /// <summary>
    /// Waits for the next <see cref="IMarketBoard.OfferingsReceived"/> for the given item.
    /// Assumes <see cref="_offeringsTcs"/> is already armed and the initial listing request
    /// has been dispatched. If nothing arrives within 5 s the server packet was likely
    /// dropped — re-arms, re-dispatches, and waits up to 10 s more before throwing.
    /// </summary>
    private async Task<IReadOnlyList<IMarketBoardItemListing>> AwaitOfferingsAsync(
        uint itemId, string itemName, CancellationToken ct)
    {
        IReadOnlyList<IMarketBoardItemListing>? received = null;
        for (var pass = 0; pass < 2 && received == null; pass++)
        {
            if (pass == 1)
            {
                _log.Information($"[HMS] No offerings response after 5 s — re-dispatching listing request for '{itemName}'.");
                _offeringsTcs = new TaskCompletionSource<IReadOnlyList<IMarketBoardItemListing>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                await _framework.RunOnFrameworkThread(() => RequestItemListingsUnsafe(itemId));
            }

            try
            {
                using var passTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                passTimeout.CancelAfter(TimeSpan.FromSeconds(pass == 0 ? 5 : 10));
                received = await _offeringsTcs!.Task.WaitAsync(passTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* retry */ }
        }

        if (received == null)
            throw new MarketboardStateException($"No listing response received for '{itemName}' within timeout.");

        return received;
    }

    private unsafe void RunTextSearchUnsafe(string itemName)
    {
        var baseAddon = AddonWaiter.GetVisibleAddon(_gameGui, AddonItemSearch);
        if (baseAddon == null) { _log.Error("[HMS] ItemSearch not visible."); return; }

        var addon = (AddonItemSearch*)baseAddon;
        addon->SearchText.SetString(itemName);

        if (addon->SearchTextInput != null)
        {
            var encoded = System.Text.Encoding.UTF8.GetBytes(itemName + "\0");
            fixed (byte* ptr = encoded)
                addon->SearchTextInput->SetText(ptr);
        }

        addon->RunSearch(false);
        _log.Debug($"[HMS] RunSearch() called for '{itemName}'.");
    }

    private unsafe void RequestItemListingsUnsafe(uint itemId)
    {
        var baseAddon = AddonWaiter.GetVisibleAddon(_gameGui, AddonItemSearch);
        if (baseAddon == null) { _log.Error("[HMS] ItemSearch not visible for listing request."); return; }

        var addon = (AddonItemSearch*)baseAddon;
        var list  = addon->ResultsList;
        if (list == null || list->ListLength == 0)
        {
            _log.Error("[HMS] ResultsList empty — cannot request listings.");
            return;
        }

        // DispatchItemEvent with ListItemClick (35) fires the same event the game sends
        // on a real mouse click, which triggers the addon's handler to open ISR and
        // call InfoProxyItemSearch.RequestData() with the correct internal state.
        list->DispatchItemEvent(0, AtkEventType.ListItemClick);
        _log.Debug($"[HMS] DispatchItemEvent(0, ListItemClick) fired for itemId={itemId}.");
    }

    private unsafe int GetItemSearchResultCountUnsafe()
    {
        var baseAddon = AddonWaiter.GetVisibleAddon(_gameGui, AddonItemSearch);
        if (baseAddon == null) return 0;
        var list  = ((AddonItemSearch*)baseAddon)->ResultsList;
        var count = list != null ? list->ListLength : 0;
        _log.Debug($"[HMS] ItemSearch ResultsList ListLength={count}");
        return count;
    }

    // =========================================================================
    // Step 3 — Read listings from the last IMarketBoard.OfferingsReceived event
    // =========================================================================

    public Task<List<MarketListing>> ReadCurrentListingsAsync(CancellationToken ct)
    {
        var results = _lastOfferings
            .Select((l, i) => new MarketListing
            {
                ListingIndex    = i,
                InGameListingId = l.ListingId,
                PricePerUnit    = (int)l.PricePerUnit,
                Quantity        = (int)l.ItemQuantity,
                Total           = (int)(l.PricePerUnit * l.ItemQuantity + l.TotalTax),
                IsHQ            = l.IsHq,
                RetainerName    = l.RetainerName,
                WorldName       = string.Empty,
            })
            .ToList();

        _log.Debug($"[HMS] Returning {results.Count} listings from last offerings event.");
        return Task.FromResult(results);
    }

    // =========================================================================
    // Step 4 — Purchase via InfoProxyItemSearch.SendPurchaseRequestPacket()
    // =========================================================================

    private async Task<bool> PurchaseListingAsync(
        MarketListing     listing,
        int               buyQty,
        uint              itemId,
        string            itemName,
        CancellationToken ct)
    {
        var ok = await _framework.RunOnFrameworkThread(
            () => InitiatePurchaseUnsafe(listing.InGameListingId, listing.PricePerUnit, buyQty));

        if (!ok) return false;

        // Allow the server round-trip + settle time.
        await Task.Delay(Math.Max(_config.NavigationDelayMs, 2_000), ct);

        // Re-request listings so the next loop iteration has fresh data.
        // We only need to re-dispatch the listing request — RunSearch is not
        // needed again because the item is already selected in the results list.
        try
        {
            await RefreshListingsAsync(itemId, itemName, ct);
        }
        catch (MarketboardStateException)
        {
            // If refresh fails (e.g. item sold out), leave _lastOfferings empty
            // so the outer loop sees 0 listings and exits cleanly.
            _lastOfferings = [];
        }

        return true;
    }

    private unsafe bool InitiatePurchaseUnsafe(ulong listingId, int expectedPpu, int buyQty)
    {
        var agent = AgentItemSearch.Instance();
        if (agent == null) return false;
        var proxy = agent->InfoProxyItemSearch;
        if (proxy == null) return false;

        // Find the listing in the proxy by ListingId so we have ContainerIndex.
        var count       = (int)proxy->ListingCount;
        var listingsPtr = (FFXIVClientStructs.FFXIV.Client.UI.Info.MarketBoardListing*)
                          ((byte*)proxy + 0x30);

        FFXIVClientStructs.FFXIV.Client.UI.Info.MarketBoardListing* target = null;
        for (var i = 0; i < count; i++)
        {
            if (listingsPtr[i].ListingId == listingId)
            {
                target = listingsPtr + i;
                break;
            }
        }

        if (target == null)
        {
            _log.Warning($"[HMS] ListingId {listingId} not found in proxy; proxy has {count} entries.");
            return false;
        }

        var actualPpu = (int)target->UnitPrice;
        if (actualPpu > (int)(expectedPpu * (1.0f + MaxPricePremium)))
        {
            _log.Warning(
                $"[HMS] Price too high: expected {expectedPpu:N0}, actual {actualPpu:N0} " +
                $"(>{MaxPricePremium:P0} premium) — skipping listing.");
            return false;
        }
        if (actualPpu != expectedPpu)
            _log.Information($"[HMS] Price adjusted {expectedPpu:N0} → {actualPpu:N0} (within tolerance).");

        proxy->SetLastPurchasedItem(target);
        proxy->LastPurchasedMarketboardItem.Quantity =
            (uint)Math.Min(buyQty, (int)target->Quantity);

        var sent = proxy->SendPurchaseRequestPacket();
        _log.Debug(
            $"[HMS] SendPurchaseRequestPacket() → {sent}; " +
            $"{proxy->LastPurchasedMarketboardItem.Quantity}× @ {target->UnitPrice:N0} gil.");
        return sent;
    }

    // =========================================================================
    // Window management
    // =========================================================================

    // =========================================================================
    // Navigation helpers
    // =========================================================================

    /// <summary>
    /// Polls until ItemSearch (the marketboard UI) is open, or times out.
    /// Lifestream's /li mb navigates to the board AND opens it, so this is
    /// the correct completion signal — more reliable than object-table scanning.
    /// </summary>
    public async Task<bool> WaitForItemSearchOpenAsync(int timeoutMs, CancellationToken ct)
    {
        return await AddonWaiter.WaitForAddonAsync(
            AddonItemSearch, _gameGui, _framework, timeoutMs, ct: ct);
    }

    /// <summary>
    /// Closes ItemSearchResult if it is open. Call before searching for a new item
    /// so we never double-open it.
    /// </summary>
    public async Task CloseItemSearchResultAsync(CancellationToken ct = default)
    {
        await _framework.RunOnFrameworkThread(() => CloseAddonUnsafe(AddonItemSearchResult));
        await Task.Delay(300, ct);
    }

    /// <summary>
    /// Closes ItemSearchResult and ItemSearch. Call when leaving a world or after shopping is done.
    /// </summary>
    public async Task CloseMarketboardAsync(CancellationToken ct = default)
    {
        await _framework.RunOnFrameworkThread(() =>
        {
            CloseAddonUnsafe(AddonItemSearchResult);
            CloseAddonUnsafe(AddonItemSearch);
        });
        await Task.Delay(400, ct);
    }

    private unsafe void CloseAddonUnsafe(string addonName)
    {
        var addon = (AtkUnitBase*)(nint)_gameGui.GetAddonByName(addonName);
        if (addon != null && addon->IsVisible)
            addon->Close(true);
    }

    public void Dispose()
    {
        _marketBoard.OfferingsReceived -= OnOfferingsReceived;
        _offeringsTcs?.TrySetCanceled();
    }
}
