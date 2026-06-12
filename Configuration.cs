using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace HousingMarketShopper;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── Price thresholds ──────────────────────────────────────────────────────
    /// <summary>Items at or below this price are purchased without confirmation.</summary>
    public int  MaxPriceAutoApprove { get; set; } = 100_000;
    /// <summary>Items above this price show an extra warning even after confirmation.</summary>
    public int  MaxPriceWarn        { get; set; } = 500_000;
    /// <summary>When true, items over MaxPriceAutoApprove are skipped automatically.</summary>
    public bool SkipHighValueItems  { get; set; } = false;

    // ── Search preferences ────────────────────────────────────────────────────
    public bool PreferNQ          { get; set; } = true;
    public bool OnlyCurrentDC     { get; set; } = false;
    public bool OnlyCurrentWorld  { get; set; } = false;
    /// <summary>Datacenters to exclude from price fetching and shopping.</summary>
    public HashSet<string> DisabledDataCenters { get; set; } = [];

    // ── Navigation ────────────────────────────────────────────────────────────
    public int  NavigationDelayMs { get; set; } = 500;
    public bool AutoOpenMB        { get; set; } = true;

    // ── Inventory management ──────────────────────────────────────────────────
    /// <summary>Pause before a world if projected free slots after buying its items would fall below this.</summary>
    public int  InventoryPauseThreshold { get; set; } = 10;
    /// <summary>Hard minimum: pause immediately during a world if free slots drop to this.</summary>
    public int  InventoryEmergencyThreshold { get; set; } = 3;
    public bool AutoInventoryPause      { get; set; } = true;

    // ── Plan optimisation ─────────────────────────────────────────────────────
    /// <summary>
    /// Items are reassigned to a world already in the plan when the price there
    /// is within this percentage of the cheapest world. 0 disables consolidation.
    /// </summary>
    public int WorldConsolidationTolerance { get; set; } = 10;

    // ── UI state ──────────────────────────────────────────────────────────────
    public string LastImportPath { get; set; } = string.Empty;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
