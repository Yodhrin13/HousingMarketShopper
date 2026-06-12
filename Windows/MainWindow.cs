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
    private readonly IPluginLog          _log;

    // ── File picker state ─────────────────────────────────────────────────────
    private string _filePath     = "";
    private string _filePickerErr = "";

    // ── Shopping loop cancellation ────────────────────────────────────────────
    private CancellationTokenSource? _shopCts;

    public MainWindow(
        Configuration       cfg,
        ShoppingListService shopList,
        NavigationService   nav,
        IObjectTable        objects,
        IPluginLog          log)
        : base("Housing Market Shopper##HMS",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        _cfg      = cfg;
        _shopList = shopList;
        _nav      = nav;
        _cs       = objects;
        _log      = log;

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
    }

    private static void DrawItemRow(ShoppingItem item, int idx)
    {
        // Checkbox: checked = include in plan, unchecked = exclude
        var included = !item.Excluded;
        if (ImGui.Checkbox($"##incl{idx}", ref included))
            item.Excluded = !included;
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
            ? $"{item.Name} ({item.DyeName})  ×{item.QuantityNeeded}"
            : $"{item.Name}  ×{item.QuantityNeeded}";
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        if (!item.Excluded && item.ResolveWarning != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(item.ResolveWarning);
    }

    // ── Shopping list (plan) tab ───────────────────────────────────────────────

    private void DrawPlanTab()
    {
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
                ImGui.Indent(12f);
                var worldHeader =
                    $"{world.WorldName}  — {world.Items.Count} items  " +
                    $"(~{world.TotalEstimatedCost:N0} gil)";
                if (!ImGui.CollapsingHeader(worldHeader, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Unindent(12f);
                    continue;
                }

                if (ImGui.BeginTable($"##tbl_{world.WorldName}", 4,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                        ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Item",      ImGuiTableColumnFlags.WidthStretch, 3f);
                    ImGui.TableSetupColumn("Qty",       ImGuiTableColumnFlags.WidthFixed,  40f);
                    ImGui.TableSetupColumn("Gil/unit",  ImGuiTableColumnFlags.WidthFixed,  80f);
                    ImGui.TableSetupColumn("Total",     ImGuiTableColumnFlags.WidthFixed,  90f);
                    ImGui.TableHeadersRow();

                    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    foreach (var item in world.Items)
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
                        var isStale = ageHours > 24;

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
                    }
                    ImGui.EndTable();
                }

                ImGui.Unindent(12f);
                ImGui.Spacing();
            }

            // Unresolved / not listed
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
        }

        ImGui.EndChild();
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
