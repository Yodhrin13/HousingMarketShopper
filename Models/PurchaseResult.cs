namespace HousingMarketShopper.Models;

/// <summary>Outcome of attempting to purchase one line item's full quantity.</summary>
public class PurchaseResult
{
    public uint           ItemId            { get; init; }
    public string         ItemName          { get; init; } = string.Empty;
    public int            QuantityRequested { get; init; }
    public int            QuantityPurchased { get; init; }
    public int            RemainingNeeded   => QuantityRequested - QuantityPurchased;
    public int            TotalSpent        { get; init; }
    public PurchaseOutcome Outcome          { get; init; }
    public string?        FailureReason     { get; init; }
}

public enum PurchaseOutcome
{
    /// <summary>Full quantity acquired.</summary>
    Success,
    /// <summary>Some quantity purchased; remainder not affordable or out of stock.</summary>
    Partial,
    /// <summary>Actual MB price drifted beyond tolerance versus the Universalis snapshot.</summary>
    PriceChanged,
    /// <summary>Item had no listings when the MB was opened.</summary>
    NotListed,
    /// <summary>User declined the high-value confirmation dialog.</summary>
    Cancelled,
    /// <summary>Unexpected error during purchase.</summary>
    Error,
}
