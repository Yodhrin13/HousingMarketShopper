using System.Collections.Generic;

namespace HousingMarketShopper.Models;

public enum PurchaseStatus
{
    Pending,
    Confirmed,
    Skipped,
    Purchased,
    /// <summary>Some quantity was purchased but not the full amount needed.</summary>
    Partial,
    Failed,
    NotListed,
}

public enum ResolveQuality
{
    Unresolved,
    FuzzyMatch,
    Exact,
}

/// <summary>One line from the shopping list, with resolution and market data.</summary>
public class ShoppingItem
{
    // ── Parsed from file ──────────────────────────────────────────────────────
    public string  RawLine        { get; set; } = string.Empty;
    /// <summary>Base item name with dye stripped.</summary>
    public string  Name           { get; set; } = string.Empty;
    /// <summary>Dye name if the file line contained one, e.g. "Kobold Brown".</summary>
    public string? DyeName        { get; set; }
    public int     QuantityNeeded { get; set; } = 1;

    // ── Item resolution ───────────────────────────────────────────────────────
    public int            ItemId         { get; set; }
    public ResolveQuality ResolveQuality { get; set; } = ResolveQuality.Unresolved;
    public string?        ResolveWarning { get; set; }
    /// <summary>Canonical name of the item this resolved to (for display/verification).</summary>
    public string?        ResolvedItemName { get; set; }
    /// <summary>Levenshtein distance for a fuzzy match; 0 for exact/unresolved.</summary>
    public int            FuzzyDistance  { get; set; }
    /// <summary>True when the user manually picked the item ID, overriding auto-resolution.</summary>
    public bool           IsManualOverride { get; set; }

    // ── Market data ───────────────────────────────────────────────────────────
    public List<MarketListing> AvailableListings { get; set; } = [];
    public int  PricePerUnit { get; set; }
    public int  TotalPrice   { get; set; }
    /// <summary>World this item is planned to be purchased from.</summary>
    public string? SourceWorld { get; set; }

    // ── Purchase state ────────────────────────────────────────────────────────
    /// <summary>
    /// When true, the item is excluded from the shopping plan.
    /// The user may uncheck an item in the Import tab to source it elsewhere.
    /// </summary>
    public bool           Excluded           { get; set; } = false;
    public bool           IsHighValue        { get; set; }
    public bool           PurchaseConfirmed  { get; set; }
    public PurchaseStatus Status             { get; set; } = PurchaseStatus.Pending;
    /// <summary>Total gil actually spent (may be less than TotalPrice if partially purchased).</summary>
    public int            ActualSpend        { get; set; }
    /// <summary>How many units were successfully purchased.</summary>
    public int            QuantityPurchased  { get; set; }

    // ── Derived helpers ───────────────────────────────────────────────────────
    public bool IsResolved  => ResolveQuality != ResolveQuality.Unresolved;
    public bool HasListings => AvailableListings.Count > 0;
}
