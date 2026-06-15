using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using HousingMarketShopper.Models;
using HousingMarketShopper.Services;
using Dalamud.Bindings.ImGui;

namespace HousingMarketShopper.Windows;

/// <summary>Main tabbed plugin window.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration       _cfg;
    private readonly ShoppingListService _shopList;
    private readonly NavigationService   _nav;
    private readonly IObjectTable         _cs;

    // ── File picker state ─────────────────────────────────────────────────────
    private string _filePath     = "";
    private string _filePickerErr = "";

    // ── Shopping loop cancellation ────────────────────────────────────────────
    private CancellationTokenSource? _shopCts;

    // ── Manual-resolution picker state ────────────────────────────────────────
    private ShoppingItem? _resolveTarget;
    private string        _resolveSearch    = "";
    private bool          _openResolvePopup;

    // ── Saved-lists state ─────────────────────────────────────────────────────
    private string _saveListName = "";

    // ── Plan tab view state ───────────────────────────────────────────────────
    private int           _planSortMode;            // 0 price↓, 1 price↑, 2 name, 3 qty↓
    private string        _planFilter = "";
    private ShoppingItem? _compareTarget;
    private bool          _openComparePopup;
    private static readonly string[] PlanSortLabels =
        ["Price (high→low)", "Price (low→high)", "Name (A→Z)", "Quantity (high→low)"];

    public MainWindow(
        Configuration       cfg,
        ShoppingListService shopList,
        NavigationService   nav,
        IObjectTable        objects)
        : base("Housing Market Shopper##HMS",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        _cfg      = cfg;
        _shopList = shopList;
        _nav      = nav;
        _cs       = objects;

        _filePath = cfg.LastImportPath;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(1200, 900),
        };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##hmsTabs")) return;

        if (ImGui.BeginTabItem("Import"))      { DrawImportTab();   ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Shopping List")){ DrawPlanTab();    ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Progress"))    { DrawProgressTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Settings"))    { DrawSettingsTab(); ImGui.EndTabItem(); }

        ImGui.EndTabBar();
    }

    // ── Import tab ────────────────────────────────────────────────────────────

    private void DrawImportTab()
    {
        ImGui.TextUnformatted("Select your MakePlace / housing list (.txt file):");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90);
        ImGui.InputText("##filepath", ref _filePath, 512);
        ImGui.SameLine();

        if (ImGui.Button("Browse…"))
            BrowseForFile();

        if (!string.IsNullOrWhiteSpace(_filePickerErr))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.TextUnformatted(_filePickerErr);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();

        var canLoad = !string.IsNullOrWhiteSpace(_filePath) && !_shopList.IsLoading;
        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Load & Resolve Items"))
        {
            _cfg.LastImportPath = _filePath;
            _cfg.Save();
            _ = _shopList.LoadFileAsync(_filePath);
        }
        if (!canLoad) ImGui.EndDisabled();

        if (_shopList.IsLoading)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(_shopList.StatusMessage);
        }

        // ── Saved lists ───────────────────────────────────────────────────────
        DrawSavedLists();

        // ── Item list with resolution status ──────────────────────────────────
        if (_shopList.LoadedItems.Count == 0) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var totalCount    = _shopList.LoadedItems.Count;
        var resolvedCount = _shopList.LoadedItems.Count(i => i.IsResolved);
        var excludedCount = _shopList.LoadedItems.Count(i => i.Excluded);
        var summaryText   = $"{totalCount} items loaded  |  {resolvedCount} resolved  |  " +
                            $"{totalCount - resolvedCount} unresolved";
        if (excludedCount > 0)
            summaryText += $"  |  {excludedCount} excluded";
        ImGui.TextUnformatted(summaryText);

        ImGui.Spacing();

        var canFetch = !_shopList.IsLoading && !_shopList.IsFetchingPrices &&
                       _shopList.LoadedItems.Any(i => i.IsResolved && !i.Excluded);
        if (!canFetch) ImGui.BeginDisabled();
        if (ImGui.Button("Fetch Prices from Universalis"))
            _ = _shopList.FetchPricesAsync(GetPlayerDcName(), GetPlayerWorldName());
        if (!canFetch) ImGui.EndDisabled();

        if (_shopList.IsFetchingPrices)
        {
            ImGui.SameLine();
            var pct = _shopList.FetchTotal > 0
                ? (float)_shopList.FetchProgress / _shopList.FetchTotal
                : 0f;
            ImGui.ProgressBar(pct, new Vector2(200, 0));
            ImGui.SameLine();
            ImGui.TextUnformatted(_shopList.StatusMessage);
        }

        ImGui.Spacing();

        if (ImGui.Button("Select All"))
            foreach (var i in _shopList.LoadedItems) i.Excluded = false;
        ImGui.SameLine();
        if (ImGui.Button("Deselect All"))
            foreach (var i in _shopList.LoadedItems) i.Excluded = true;
        ImGui.SameLine();
        ImGui.TextDisabled("  Uncheck items to skip them from the plan (e.g. sourcing elsewhere)");

        ImGui.Spacing();

        if (ImGui.BeginChild("##itemList", new Vector2(0, -1), true))
        {
            for (var idx = 0; idx < _shopList.LoadedItems.Count; idx++)
                DrawItemRow(_shopList.LoadedItems[idx], idx);
            ImGui.EndChild();
        }

        DrawResolvePicker();
    }

    // ── Saved lists ───────────────────────────────────────────────────────────

    private void DrawSavedLists()
    {
        if (!ImGui.CollapsingHeader("Saved Lists")) return;

        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##saveListName", "List name…", ref _saveListName, 64);
        ImGui.SameLine();
        var canSave = !string.IsNullOrWhiteSpace(_saveListName) && _shopList.LoadedItems.Count > 0;
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Save Current"))
        {
            _shopList.SaveList(_saveListName);
            _saveListName = "";
        }
        if (!canSave) ImGui.EndDisabled();

        var names = _shopList.GetSavedListNames();
        if (names.Count == 0)
        {
            ImGui.TextDisabled("No saved lists yet.");
            return;
        }

        ImGui.Spacing();
        foreach (var n in names)
        {
            ImGui.PushID($"saved_{n}");
            if (ImGui.SmallButton("Load")) _shopList.LoadSavedList(n!);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete")) _shopList.DeleteSavedList(n!);
            ImGui.SameLine();
            ImGui.TextUnformatted(n!);
            ImGui.PopID();
        }
    }

    // ── Manual-resolution picker ──────────────────────────────────────────────

    private void OpenResolvePicker(ShoppingItem item)
    {
        _resolveTarget    = item;
        _resolveSearch    = item.Name;
        _openResolvePopup = true;
    }

    private void DrawResolvePicker()
    {
        if (_openResolvePopup)
        {
            ImGui.OpenPopup("Resolve Item##resolvePopup");
            _openResolvePopup = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(440, 380), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Resolve Item##resolvePopup", ref open,
                ImGuiWindowFlags.NoCollapse))
            return;

        if (_resolveTarget == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Original: {_resolveTarget.Name}");
        if (_resolveTarget.ResolvedItemName != null)
            ImGui.TextDisabled($"Currently: {_resolveTarget.ResolvedItemName}");
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##resolveSearch", "Search item name…", ref _resolveSearch, 128);

        if (ImGui.BeginChild("##resolveResults", new Vector2(0, 260), true))
        {
            foreach (var (id, name) in _shopList.SearchItems(_resolveSearch, 200))
            {
                if (ImGui.Selectable($"{name}##res{id}"))
                {
                    _shopList.ApplyManualResolution(_resolveTarget, id);
                    ImGui.CloseCurrentPopup();
                    break;
                }
            }
            ImGui.EndChild();
        }

        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawItemRow(ShoppingItem item, int idx)
    {
        // Checkbox: checked = include in plan, unchecked = exclude
        var included = !item.Excluded;
        if (ImGui.Checkbox($"##incl{idx}", ref included))
            item.Excluded = !included;
        ImGui.SameLine();

        // Quantity stepper
        ImGui.SetNextItemWidth(96f);
        var qty = item.QuantityNeeded;
        if (ImGui.InputInt($"##qty{idx}", ref qty, 1, 5))
            item.QuantityNeeded = Math.Max(1, qty);
        ImGui.SameLine();

        var color = item.Excluded
            ? new Vector4(0.45f, 0.45f, 0.45f, 1f)   // dim grey when excluded
            : item.ResolveQuality switch
            {
                ResolveQuality.Exact      => new Vector4(0.4f, 1f, 0.4f, 1f),
                ResolveQuality.FuzzyMatch => new Vector4(1f, 0.85f, 0.2f, 1f),
                _                         => new Vector4(1f, 0.3f, 0.3f, 1f),
            };

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        var label = item.DyeName != null
            ? $"{item.Name} ({item.DyeName})"
            : item.Name;
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        if (!item.Excluded && item.ResolveWarning != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(item.ResolveWarning);

        // ── Inline resolution confidence ───────────────────────────────────────
        if (!item.Excluded)
        {
            if (item.IsManualOverride)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f),
                    $"→ {item.ResolvedItemName} (manual)");
            }
            else if (item.ResolveQuality == ResolveQuality.FuzzyMatch)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f),
                    $"≈ {item.ResolvedItemName} (dist {item.FuzzyDistance})");
            }
            else if (item.ResolveQuality == ResolveQuality.Unresolved)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "unresolved");
            }
        }

        // Manual-resolution affordance for anything not an exact auto-match.
        if (!item.Excluded && item.ResolveQuality != ResolveQuality.Exact)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Fix##fix{idx}"))
                OpenResolvePicker(item);
        }
    }

    // ── Shopping list (plan) tab ───────────────────────────────────────────────

    private void DrawPlanTab()
    {
        DrawResumeBanner();

        var plan = _shopList.CurrentPlan;
        if (plan == null)
        {
            ImGui.TextDisabled("Load a file and fetch prices first.");
            return;
        }

        var dcCount    = plan.Groups.Count;
        var worldCount = plan.Groups.Sum(g => g.Worlds.Count);
        ImGui.TextUnformatted(
            $"Plan: {plan.TotalItemCount} purchases  |  " +
            $"{worldCount} world{(worldCount != 1 ? "s" : "")} across " +
            $"{dcCount} DC{(dcCount != 1 ? "s" : "")}  |  " +
            $"~{plan.TotalEstimatedCost:N0} gil total");

        // Gil-on-hand check
        var gil = _nav.GetPlayerGilOnFrame();
        if (gil >= 0)
        {
            if (gil < plan.TotalEstimatedCost)
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.3f, 1f),
                    $"⚠ You have {gil:N0} gil — short by ~{plan.TotalEstimatedCost - gil:N0}.");
            else
                ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.5f, 1f),
                    $"You have {gil:N0} gil (~{gil - plan.TotalEstimatedCost:N0} to spare).");
        }

        ImGui.Spacing();

        // IPC availability warning
        if (!_nav.IpcReady)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0.1f, 1f));
            ImGui.TextWrapped(
                "⚠ Requires the Lifestream plugin to be installed and enabled. Automation unavailable.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        var canShop = _nav.IpcReady && !_nav.IsRunning && plan.TotalItemCount > 0;
        if (!canShop) ImGui.BeginDisabled();
        if (ImGui.Button("Start Shopping"))
            StartShopping(plan!);
        if (!canShop) ImGui.EndDisabled();

        ImGui.SameLine();
        var canSim = !_nav.IsRunning && plan.TotalItemCount > 0;
        if (!canSim) ImGui.BeginDisabled();
        if (ImGui.Button("Dry Run"))
            _nav.SimulateRun(plan);
        if (!canSim) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Log the full route and intended purchases without travelling or buying.\nSee the Progress tab.");

        // Consolidation savings + rough travel estimate
        var premium    = plan.TotalEstimatedCost - plan.PreConsolidationCost;
        var worldsSaved = plan.PreConsolidationWorldCount - worldCount;
        if (worldsSaved > 0 && premium >= 0)
            ImGui.TextDisabled(
                $"Consolidation: {worldsSaved} fewer world{(worldsSaved != 1 ? "s" : "")} " +
                $"for +{premium:N0} gil");

        if (worldCount > 0)
        {
            // Rough: ~40s travel/setup per world + a few seconds per purchase.
            var perBuy   = Math.Max(_cfg.NavigationDelayMs, 2000) / 1000.0 + 3;
            var estSecs  = worldCount * 40 + plan.TotalItemCount * perBuy;
            var span     = TimeSpan.FromSeconds(estSecs);
            ImGui.SameLine();
            ImGui.TextDisabled($"   ~{(int)span.TotalMinutes}m {span.Seconds}s estimated");
        }

        ImGui.Spacing();

        // Sort + filter controls
        ImGui.SetNextItemWidth(180f);
        ImGui.Combo("Sort##planSort", ref _planSortMode, PlanSortLabels, PlanSortLabels.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("##planFilter", "Filter by name…", ref _planFilter, 64);

        ImGui.Spacing();
        ImGui.Separator();

        if (!ImGui.BeginChild("##planScroll", new Vector2(0, -1), false)) return;

        foreach (var dcGroup in plan.Groups)
        {
            if (!ImGui.CollapsingHeader(
                    $"{dcGroup.DataCenterName}  (~{dcGroup.TotalEstimatedCost:N0} gil)",
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            foreach (var world in dcGroup.Worlds)
            {
                var visibleItems = SortAndFilter(world.Items);
                if (visibleItems.Count == 0) continue;

                ImGui.Indent(12f);
                var worldHeader =
                    $"{world.WorldName}  — {visibleItems.Count} items  " +
                    $"(~{world.TotalEstimatedCost:N0} gil)";
                if (!ImGui.CollapsingHeader(worldHeader, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Unindent(12f);
                    continue;
                }

                if (ImGui.BeginTable($"##tbl_{world.WorldName}", 5,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                        ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Item",      ImGuiTableColumnFlags.WidthStretch, 3f);
                    ImGui.TableSetupColumn("Qty",       ImGuiTableColumnFlags.WidthFixed,  40f);
                    ImGui.TableSetupColumn("Gil/unit",  ImGuiTableColumnFlags.WidthFixed,  80f);
                    ImGui.TableSetupColumn("Total",     ImGuiTableColumnFlags.WidthFixed,  90f);
                    ImGui.TableSetupColumn("",          ImGuiTableColumnFlags.WidthFixed, 110f);
                    ImGui.TableHeadersRow();

                    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    foreach (var item in visibleItems)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);

                        var isHigh = item.IsHighValue;
                        var bestListing = item.AvailableListings
                            .Where(l => l.WorldName.Equals(world.WorldName,
                                StringComparison.OrdinalIgnoreCase))
                            .OrderBy(l => l.PricePerUnit)
                            .FirstOrDefault();
                        var ageHours  = bestListing != null && bestListing.LastReviewTime > 0
                            ? (nowUnix - bestListing.LastReviewTime) / 3600.0
                            : 0;
                        var isStale = ageHours > _cfg.StaleListingHours;

                        var textColor = isStale  ? new Vector4(1f, 0.75f, 0.2f, 1f)   // amber — stale
                                      : isHigh   ? new Vector4(1f, 0.55f, 0.1f, 1f)   // orange — high value
                                      :            Vector4.One;
                        ImGui.PushStyleColor(ImGuiCol.Text, textColor);

                        var prefix = (isStale ? "⏱ " : "") + (isHigh ? "⚠ " : "");
                        var name   = item.DyeName != null
                            ? $"{prefix}{item.Name} ({item.DyeName})"
                            : $"{prefix}{item.Name}";
                        ImGui.TextUnformatted(name);
                        ImGui.PopStyleColor();

                        if (ImGui.IsItemHovered())
                        {
                            if (isStale && isHigh)
                                ImGui.SetTooltip($"High value  |  Listing {ageHours:F0}h old — price may be stale");
                            else if (isStale)
                                ImGui.SetTooltip($"Listing {ageHours:F0}h old — price may be stale");
                            else if (isHigh)
                                ImGui.SetTooltip("High value — will require confirmation");
                        }

                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(item.QuantityNeeded.ToString());
                        ImGui.TableSetColumnIndex(2);
                        ImGui.TextUnformatted($"{item.PricePerUnit:N0}");
                        ImGui.TableSetColumnIndex(3);
                        ImGui.TextUnformatted($"{item.TotalPrice:N0}");

                        ImGui.TableSetColumnIndex(4);
                        if (ImGui.SmallButton($"Worlds##cmp{item.ItemId}_{world.WorldName}"))
                            OpenComparePopup(item);
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"U##uni{item.ItemId}_{world.WorldName}"))
                            Dalamud.Utility.Util.OpenLink($"https://universalis.app/market/{item.ItemId}");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Open on Universalis");
                    }
                    ImGui.EndTable();
                }

                ImGui.Unindent(12f);
                ImGui.Spacing();
            }
        }

        // Unresolved / not listed — shown once, after all DC groups (and even when
        // there are no priced groups at all, so a fully-unresolved list still explains itself).
        if (plan.Unresolved.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                $"Unresolved ({plan.Unresolved.Count}):");
            foreach (var u in plan.Unresolved)
                ImGui.TextUnformatted($"  • {u.Name}");
        }

        if (plan.NotListed.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
                $"Not listed on market ({plan.NotListed.Count}):");
            foreach (var nl in plan.NotListed)
                ImGui.TextUnformatted($"  • {nl.Name}");
        }

        if (plan.OverBudget.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.1f, 1f),
                $"Dropped — over budget cap ({plan.OverBudget.Count}):");
            foreach (var ob in plan.OverBudget)
                ImGui.TextUnformatted($"  • {ob.Name}  ×{ob.QuantityNeeded}  (~{ob.TotalPrice:N0} gil)");
        }

        ImGui.EndChild();

        DrawComparePopup();
    }

    private void DrawResumeBanner()
    {
        if (!_nav.HasSavedRun || _nav.IsRunning) return;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.20f, 0.30f, 1f));
        if (ImGui.BeginChild("##resumeBanner", new Vector2(0, 76), true))
        {
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "⟳  Interrupted run found");
            if (_nav.SavedRunInfo != null)
                ImGui.TextDisabled(_nav.SavedRunInfo);
            ImGui.Spacing();

            var canResume = _nav.IpcReady;
            if (!canResume) ImGui.BeginDisabled();
            if (ImGui.Button("Resume Run"))
            {
                var loaded = _nav.LoadSavedRunItems();
                if (loaded is { } l && l.items.Count > 0)
                {
                    var resumePlan = _shopList.BuildRetryPlan(l.items, GetPlayerWorldName());
                    if (resumePlan.TotalItemCount > 0)
                        StartShopping(resumePlan);
                }
            }
            if (!canResume) ImGui.EndDisabled();
            if (!canResume && ImGui.IsItemHovered())
                ImGui.SetTooltip("Lifestream is required to resume automated shopping.");

            ImGui.SameLine();
            if (ImGui.Button("Discard##resume"))
                _nav.DiscardSavedRun();

            ImGui.EndChild();
        }
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private List<ShoppingItem> SortAndFilter(List<ShoppingItem> items)
    {
        IEnumerable<ShoppingItem> q = items;

        if (!string.IsNullOrWhiteSpace(_planFilter))
            q = q.Where(i => i.Name.Contains(_planFilter, StringComparison.OrdinalIgnoreCase)
                          || (i.DyeName?.Contains(_planFilter, StringComparison.OrdinalIgnoreCase) ?? false));

        q = _planSortMode switch
        {
            1 => q.OrderBy(i => i.TotalPrice),
            2 => q.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            3 => q.OrderByDescending(i => i.QuantityNeeded),
            _ => q.OrderByDescending(i => i.TotalPrice),
        };

        return q.ToList();
    }

    // ── Price-per-world comparison ────────────────────────────────────────────

    private void OpenComparePopup(ShoppingItem item)
    {
        _compareTarget    = item;
        _openComparePopup = true;
    }

    private void DrawComparePopup()
    {
        if (_openComparePopup)
        {
            ImGui.OpenPopup("World Prices##cmpPopup");
            _openComparePopup = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(420, 420), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("World Prices##cmpPopup", ref open, ImGuiWindowFlags.NoCollapse))
            return;

        if (_compareTarget == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var need = _compareTarget.QuantityNeeded;
        ImGui.TextUnformatted(_compareTarget.Name);
        ImGui.TextDisabled($"Need ×{need}  ·  planned at {_compareTarget.PricePerUnit:N0}/unit on {_compareTarget.SourceWorld ?? "?"}");
        ImGui.Separator();

        // Per world: accumulate the cheapest listings up to the needed quantity, mirroring
        // how the plan picks a source (prefer NQ, fall back to HQ). "Have" is total stock on
        // that world; "Total" is the cost to buy all `need` units there (— if it can't fill).
        var perWorld = _compareTarget.AvailableListings
            .GroupBy(l => l.WorldName)
            .Select(g =>
            {
                var candidates = _cfg.PreferNQ ? g.Where(l => !l.IsHQ).ToList() : g.ToList();
                if (candidates.Count == 0) candidates = g.ToList();
                var sorted = candidates.OrderBy(l => l.PricePerUnit).ToList();

                int remaining = need, total = 0, effPpu = 0, have = 0;
                foreach (var l in sorted)
                {
                    have += l.Quantity;
                    if (remaining <= 0) continue;
                    var take = Math.Min(remaining, l.Quantity);
                    total += take * l.PricePerUnit;
                    effPpu = l.PricePerUnit;
                    remaining -= take;
                }
                return (World: g.Key, EffPpu: effPpu, Total: total, Have: have, CanFill: remaining <= 0);
            })
            .OrderBy(x => x.CanFill ? 0 : 1)   // worlds that can fill the order first
            .ThenBy(x => x.Total)
            .ToList();

        if (ImGui.BeginChild("##cmpList", new Vector2(0, 320), true))
        {
            if (ImGui.BeginTable("##cmpTbl", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("World",       ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("Gil/unit",    ImGuiTableColumnFlags.WidthFixed,  90f);
                ImGui.TableSetupColumn("Have",        ImGuiTableColumnFlags.WidthFixed,  50f);
                ImGui.TableSetupColumn($"Total ×{need}", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableHeadersRow();

                foreach (var w in perWorld)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var chosen = _compareTarget.SourceWorld != null &&
                                 w.World.Equals(_compareTarget.SourceWorld, StringComparison.OrdinalIgnoreCase);
                    if (chosen)
                        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"● {w.World}");
                    else if (!w.CanFill)
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"   {w.World}");
                    else
                        ImGui.TextUnformatted($"   {w.World}");

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted($"{w.EffPpu:N0}");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(w.Have.ToString());
                    ImGui.TableSetColumnIndex(3);
                    if (w.CanFill)
                        ImGui.TextUnformatted($"{w.Total:N0}");
                    else
                        // Can't fill the full order — show the cost of what's available, dimmed.
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"{w.Total:N0}");
                }
                ImGui.EndTable();
            }
            ImGui.EndChild();
        }

        if (ImGui.Button("Close")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // ── Progress tab ──────────────────────────────────────────────────────────

    private void DrawProgressTab()
    {
        if (!_nav.IsRunning && _nav.Log.Count == 0)
        {
            ImGui.TextDisabled("No shopping session active.");
            return;
        }

        ImGui.TextUnformatted($"Status: {(_nav.IsRunning ? (_nav.IsPaused ? "Paused" : "Running") : "Done")}");

        // Gil spend line
        if (_nav.IsRunning)
        {
            ImGui.SameLine(160f);
            ImGui.TextUnformatted($"Spent: {_nav.TotalActualSpend:N0}  /  ~{_nav.TotalEstimatedSpend:N0} gil");
        }
        else if (_nav.TotalActualSpend > 0)
        {
            ImGui.SameLine(160f);
            var diff    = _nav.TotalActualSpend - _nav.TotalEstimatedSpend;
            var diffStr = diff >= 0 ? $"+{diff:N0}" : $"{diff:N0}";
            ImGui.TextUnformatted($"Final: {_nav.TotalActualSpend:N0} gil  ({diffStr} vs estimate)");
        }

        ImGui.Spacing();

        // ── Inventory pause banner ─────────────────────────────────────────────
        if (_nav.IsInventoryPause)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.4f, 0.15f, 0f, 1f));
            if (ImGui.BeginChild("##invBanner", new Vector2(0, 80), true))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.1f, 1f));
                ImGui.TextUnformatted("⚠  Inventory Nearly Full — Deposit Required");
                ImGui.PopStyleColor();
                ImGui.TextUnformatted(
                    $"Free slots: {_nav.InventoryFreeSlots}  |  " +
                    $"Need to deposit: ≥{_nav.InventorySlotsNeeded}  |  " +
                    $"Items remaining in plan: {_nav.InventoryFutureItems}");
                ImGui.Spacing();
                if (ImGui.Button("Teleport to Ul'dah to Deposit"))
                    _ = _nav.TeleportToDepositLocationAsync();
                ImGui.SameLine();
                if (ImGui.Button("Resume (I've deposited)")) _nav.Resume();
                ImGui.EndChild();
            }
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        if (_nav.IsRunning)
        {
            if (_nav.IsPaused && !_nav.IsInventoryPause)
            {
                if (ImGui.Button("Resume")) _nav.Resume();
            }
            else if (!_nav.IsPaused)
            {
                if (ImGui.Button("Pause")) _nav.Pause();
            }

            ImGui.SameLine();
            if (ImGui.Button("Abort"))
            {
                _shopCts?.Cancel();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"Current: {_nav.CurrentAction}");

        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 80);
        if (ImGui.Button("Clear Log"))
            _nav.Log.Clear();

        ImGui.Separator();

        // ── Missed items summary (shown when run is finished) ──────────────────
        if (!_nav.IsRunning && _nav.MissedItems.Count > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.TextUnformatted($"⚠  {_nav.MissedItems.Count} item type(s) not fully purchased:");
            ImGui.PopStyleColor();
            ImGui.Spacing();

            var listHeight = Math.Min(_nav.MissedItems.Count * 20f + 12f, 140f);
            if (ImGui.BeginChild("##missedItems", new Vector2(0, listHeight), true))
            {
                foreach (var missed in _nav.MissedItems)
                {
                    var label = missed.DyeName != null
                        ? $"• {missed.Name} ({missed.DyeName})  ×{missed.QuantityNeeded}"
                        : $"• {missed.Name}  ×{missed.QuantityNeeded}";
                    ImGui.TextUnformatted(label);
                }
                ImGui.EndChild();
            }

            ImGui.Spacing();
            if (ImGui.Button("Copy Missed Items"))
            {
                var lines = _nav.MissedItems.Select(i =>
                    i.DyeName != null
                        ? $"{i.Name} ({i.DyeName}) ×{i.QuantityNeeded}"
                        : $"{i.Name} ×{i.QuantityNeeded}");
                ImGui.SetClipboardText(string.Join("\n", lines));
            }

            ImGui.SameLine();
            var canRetry = _nav.IpcReady && !_nav.IsRunning;
            if (!canRetry) ImGui.BeginDisabled();
            if (ImGui.Button("Retry Missed Items"))
            {
                var retryItems = _nav.MissedItems.ToList();
                var retryPlan  = _shopList.BuildRetryPlan(retryItems, GetPlayerWorldName());
                if (retryPlan.TotalItemCount > 0)
                    StartShopping(retryPlan);
            }
            if (!canRetry) ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        if (!ImGui.BeginChild("##navLog", new Vector2(0, -1), true)) return;

        // Show log lines newest-first with color coding
        for (var i = _nav.Log.Count - 1; i >= 0; i--)
        {
            var entry = _nav.Log[i];
            var color = entry.Tag switch
            {
                LogTag.Navigation => new Vector4(0.4f, 0.85f, 1f,   1f),  // light blue
                LogTag.Purchase   => new Vector4(1f,   1f,   0.6f,  1f),  // pale yellow
                LogTag.Success    => new Vector4(0.3f, 1f,   0.4f,  1f),  // green
                LogTag.Partial    => new Vector4(1f,   0.85f, 0.2f, 1f),  // amber
                LogTag.Warning    => new Vector4(1f,   0.6f, 0.1f,  1f),  // orange
                LogTag.Error      => new Vector4(1f,   0.3f, 0.3f,  1f),  // red
                _                 => new Vector4(0.85f,0.85f,0.85f, 1f),  // dim white
            };
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(entry.Text);
            ImGui.PopStyleColor();
        }

        ImGui.EndChild();
    }

    // ── Settings tab ──────────────────────────────────────────────────────────

    private void DrawSettingsTab() => ConfigWindow.DrawContent(_cfg);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void BrowseForFile()
    {
        _filePickerErr = "";
        // Run on an STA thread so Windows dialogs work properly
        var t = new System.Threading.Thread(() =>
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Filter      = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                Title       = "Select Housing Shopping List",
                FileName    = _filePath,
            };
            if (!string.IsNullOrWhiteSpace(_filePath) && File.Exists(_filePath))
                dlg.InitialDirectory = Path.GetDirectoryName(_filePath) ?? "";

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _filePath = dlg.FileName;
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private string? GetPlayerDcName()
    {
        try { return _cs.LocalPlayer?.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ToString(); }
        catch { return null; }
    }

    private string? GetPlayerWorldName()
    {
        try { return _cs.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString(); }
        catch { return null; }
    }

    private void StartShopping(ShoppingPlan plan)
    {
        _shopCts?.Cancel();
        _shopCts?.Dispose();
        _shopCts = new CancellationTokenSource();
        var ct   = _shopCts.Token;
        _ = _nav.RunShoppingLoopAsync(plan, GetPlayerDcName(), GetPlayerWorldName(),
            async item => await Plugin.ConfirmWindow.ShowAsync(item), ct);
    }

    public void Dispose()
    {
        _shopCts?.Cancel();
        _shopCts?.Dispose();
    }
}
