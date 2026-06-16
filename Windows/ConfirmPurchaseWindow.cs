using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HousingMarketShopper.Models;

namespace HousingMarketShopper.Windows;

/// <summary>
/// Modal dialog asking the user to confirm, skip, or abort a high-value purchase.
/// </summary>
public sealed class ConfirmPurchaseWindow : Window
{
    private readonly Configuration _cfg;

    private ShoppingItem? _item;
    private TaskCompletionSource<bool>? _tcs;

    public ConfirmPurchaseWindow(Configuration cfg)
        : base("Confirm Purchase##HMS",
               ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        _cfg  = cfg;
        IsOpen = false;
    }

    /// <summary>
    /// Show the dialog for <paramref name="item"/> and await the user's decision.
    /// Returns <c>true</c> if confirmed, <c>false</c> if skipped or aborted.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> ShowAsync(ShoppingItem item)
    {
        // If a dialog is already showing, wait for it to be resolved first.
        if (_tcs != null)
            await _tcs.Task;

        _item  = item;
        _tcs   = new TaskCompletionSource<bool>(
                     TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        return await _tcs.Task;
    }

    public override void Draw()
    {
        if (_item == null || _tcs == null) { IsOpen = false; return; }

        ImGui.PushTextWrapPos(400f);

        ImGui.TextUnformatted("A high-value item requires confirmation:");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted($"Item:      {_item.Name}");
        if (_item.DyeName != null)
            ImGui.TextUnformatted($"Dye:       {_item.DyeName}");
        ImGui.TextUnformatted($"Quantity:  {_item.QuantityNeeded}");
        ImGui.TextUnformatted($"Price/unit:{_item.PricePerUnit:N0} gil");
        ImGui.TextUnformatted($"Total:     {_item.TotalPrice:N0} gil");
        if (_item.SourceWorld != null)
            ImGui.TextUnformatted($"World:     {_item.SourceWorld}");

        // Expected listings to be consumed on the source world (from the Universalis
        // snapshot — live prices may differ slightly when the purchase runs).
        DrawExpectedListings();

        // Extra warning for extremely high prices
        if (_item.PricePerUnit > _cfg.MaxPriceWarn)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.2f, 0.2f, 1f));
            ImGui.TextWrapped(
                $"(!) This item exceeds the warning threshold of {_cfg.MaxPriceWarn:N0} gil!");
            ImGui.PopStyleColor();
        }

        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Confirm Purchase", new Vector2(150, 0)))
        {
            IsOpen = false;
            _tcs.TrySetResult(true);
        }

        ImGui.SameLine();

        if (ImGui.Button("Skip This Item", new Vector2(120, 0)))
        {
            IsOpen = false;
            _tcs.TrySetResult(false);
        }

        ImGui.SameLine();

        if (ImGui.Button("Abort Shopping", new Vector2(120, 0)))
        {
            IsOpen = false;
            _tcs.TrySetResult(false);
            Plugin.NavigationService.Stop();
        }
    }

    /// <summary>
    /// Shows which snapshot listings on the source world would be consumed to fill the
    /// quantity, with retainer names — a preview so the user knows what they're buying.
    /// </summary>
    private void DrawExpectedListings()
    {
        if (_item == null) return;

        var listings = _item.AvailableListings
            .Where(l => _item.SourceWorld == null
                     || l.WorldName.Equals(_item.SourceWorld, StringComparison.OrdinalIgnoreCase))
            .Where(l => !_cfg.PreferNQ || !l.IsHQ)
            .OrderBy(l => l.PricePerUnit)
            .ToList();

        if (listings.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextDisabled("Expected listings (snapshot):");

        var remaining = _item.QuantityNeeded;
        foreach (var l in listings)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, l.Quantity);
            remaining -= take;
            var retainer = string.IsNullOrEmpty(l.RetainerName) ? "?" : l.RetainerName;
            ImGui.BulletText($"{take}× @ {l.PricePerUnit:N0}{(l.IsHQ ? " HQ" : "")}  ·  {retainer}");
        }
        if (remaining > 0)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.1f, 1f),
                $"  (snapshot short by {remaining} — may need another world)");
    }
}
