using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;

namespace HousingMarketShopper.Windows;

/// <summary>Standalone settings window (also embedded as a tab in MainWindow).</summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration _cfg;
    private readonly Func<string?>? _getPlayerDc;

    // Region → ordered DC list, in the order we want to display them.
    private static readonly (string Region, string[] DCs)[] KnownRegions =
    [
        ("NA",  ["Aether", "Crystal", "Primal", "Dynamis"]),
        ("EU",  ["Chaos", "Light"]),
        ("JP",  ["Elemental", "Gaia", "Mana", "Meteor"]),
        ("OCE", ["Materia"]),
    ];

    public ConfigWindow(Configuration cfg, Func<string?>? getPlayerDc = null)
        : base("HousingMarketShopper Settings##HMS",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        _cfg         = cfg;
        _getPlayerDc = getPlayerDc;
        IsOpen  = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(380, 320),
            MaximumSize = new System.Numerics.Vector2(600, 700),
        };
    }

    public override void Draw() => DrawContent(_cfg, _getPlayerDc?.Invoke());

    /// <summary>Draws the settings UI inline (usable inside a tab too).</summary>
    public static void DrawContent(Configuration cfg, string? playerDc = null)
    {
        ImGui.TextUnformatted("Price Thresholds");
        ImGui.Separator();

        var autoApprove = cfg.MaxPriceAutoApprove;
        if (ImGui.InputInt("Auto-approve below (gil)", ref autoApprove, 1000, 10000))
            cfg.MaxPriceAutoApprove = Math.Max(0, autoApprove);

        var warnThreshold = cfg.MaxPriceWarn;
        if (ImGui.InputInt("Extra warning above (gil)", ref warnThreshold, 1000, 10000))
            cfg.MaxPriceWarn = Math.Max(0, warnThreshold);

        var skipHigh = cfg.SkipHighValueItems;
        if (ImGui.Checkbox("Auto-skip items over auto-approve threshold", ref skipHigh))
            cfg.SkipHighValueItems = skipHigh;

        var premium = cfg.MaxPricePremiumPercent;
        if (ImGui.SliderInt("Max price premium (%)", ref premium, 0, 100))
            cfg.MaxPricePremiumPercent = premium;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How far above the Universalis snapshot price a live listing may be\n" +
                "and still be bought automatically. Guards against price spikes.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Search Preferences");
        ImGui.Separator();

        var preferNq = cfg.PreferNQ;
        if (ImGui.Checkbox("Prefer NQ over HQ", ref preferNq))
            cfg.PreferNQ = preferNq;

        var onlyDC = cfg.OnlyCurrentDC;
        if (ImGui.Checkbox("Only search current datacenter", ref onlyDC))
        {
            cfg.OnlyCurrentDC    = onlyDC;
            if (onlyDC) cfg.OnlyCurrentWorld = false; // DC overrides world-only
        }

        var onlyWorld = cfg.OnlyCurrentWorld;
        if (ImGui.Checkbox("Only search current world", ref onlyWorld))
        {
            cfg.OnlyCurrentWorld = onlyWorld;
            if (onlyWorld) cfg.OnlyCurrentDC = false;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Plan Optimisation");
        ImGui.Separator();
        ImGui.TextDisabled("Consolidates items onto fewer worlds to reduce travel.");
        ImGui.Spacing();

        var consolidation = cfg.WorldConsolidationTolerance;
        if (ImGui.SliderInt("World consolidation tolerance (%)", ref consolidation, 0, 30))
            cfg.WorldConsolidationTolerance = consolidation;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Items are moved to a world already in the plan when its price is within\n" +
                "this % of the cheapest. 0 = always buy at the absolute cheapest world.");

        var staleHours = cfg.StaleListingHours;
        if (ImGui.SliderInt("Stale listing warning (hours)", ref staleHours, 1, 168))
            cfg.StaleListingHours = staleHours;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Listings older than this are flagged with a (stale) in the shopping plan.");

        var budgetCap = cfg.BudgetCap;
        if (ImGui.InputInt("Budget cap (gil, 0 = none)", ref budgetCap, 10000, 100000))
            cfg.BudgetCap = Math.Max(0, budgetCap);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When a plan exceeds this total, the most expensive items are dropped\n" +
                "until it fits. Dropped items are listed at the bottom of the plan.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Navigation");
        ImGui.Separator();

        var delay = cfg.NavigationDelayMs;
        if (ImGui.SliderInt("Navigation delay (ms)", ref delay, 100, 3000))
            cfg.NavigationDelayMs = delay;

        var autoMb = cfg.AutoOpenMB;
        if (ImGui.Checkbox("Show reminder to open marketboard", ref autoMb))
            cfg.AutoOpenMB = autoMb;


        ImGui.Spacing();
        ImGui.TextUnformatted("Enabled Datacenters");
        ImGui.Separator();
        ImGui.TextDisabled("Uncheck to skip a datacenter when fetching prices and shopping.");

        // The game can't travel cross-region, so only the player's own region is searched.
        // Determine the player's region (by their current DC) and disable the others.
        var playerRegion = string.IsNullOrEmpty(playerDc)
            ? null
            : KnownRegions
                .FirstOrDefault(r => r.DCs.Any(d => d.Equals(playerDc, StringComparison.OrdinalIgnoreCase)))
                .Region;

        if (playerRegion != null)
            ImGui.TextDisabled($"Only your region ({playerRegion}) can be reached — others are disabled.");
        ImGui.Spacing();

        foreach (var (region, dcs) in KnownRegions)
        {
            var reachable = playerRegion == null ||
                            region.Equals(playerRegion, StringComparison.OrdinalIgnoreCase);

            ImGui.TextUnformatted(region);
            ImGui.SameLine(50f);

            if (!reachable) ImGui.BeginDisabled();
            for (var i = 0; i < dcs.Length; i++)
            {
                var dc      = dcs[i];
                var enabled = !cfg.DisabledDataCenters.Contains(dc);
                if (ImGui.Checkbox($"{dc}##dc", ref enabled))
                {
                    if (enabled) cfg.DisabledDataCenters.Remove(dc);
                    else         cfg.DisabledDataCenters.Add(dc);
                }
                if (i < dcs.Length - 1) ImGui.SameLine();
            }
            if (!reachable)
            {
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextDisabled("(other region)");
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Inventory Management");
        ImGui.Separator();
        ImGui.TextDisabled("Pauses before worlds where inventory will run low.");
        ImGui.Spacing();

        var autoPause = cfg.AutoInventoryPause;
        if (ImGui.Checkbox("Auto-pause when inventory is nearly full", ref autoPause))
            cfg.AutoInventoryPause = autoPause;

        ImGui.BeginDisabled(!cfg.AutoInventoryPause);

        var pauseThresh = cfg.InventoryPauseThreshold;
        if (ImGui.SliderInt("Pre-world pause threshold (free slots)", ref pauseThresh, 1, 50))
            cfg.InventoryPauseThreshold = pauseThresh;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pause before a world if free slots after buying its items would fall below this.");

        var emergThresh = cfg.InventoryEmergencyThreshold;
        if (ImGui.SliderInt("Emergency pause threshold (free slots)", ref emergThresh, 1, 20))
            cfg.InventoryEmergencyThreshold = Math.Min(emergThresh, cfg.InventoryPauseThreshold - 1);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hard minimum: pause immediately mid-world if free slots drop to this.");

        ImGui.EndDisabled();

        ImGui.Spacing();
        if (ImGui.Button("Save##cfg"))
            cfg.Save();
    }
}
