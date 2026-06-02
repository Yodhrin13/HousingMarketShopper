namespace HousingMarketShopper.Models;

/// <summary>A single market board listing returned from the Universalis API.</summary>
public class MarketListing
{
    public int    ItemId        { get; set; }
    public int    PricePerUnit  { get; set; }
    public int    Quantity      { get; set; }
    public int    Total         { get; set; }
    public string WorldName     { get; set; } = string.Empty;
    public int    WorldId       { get; set; }
    public bool   IsHQ          { get; set; }
    public long   LastReviewTime { get; set; }
    public string RetainerName  { get; set; } = string.Empty;
    public string ListingId     { get; set; } = string.Empty;
    /// <summary>Numeric listing ID from the in-game server packet, used to match proxy entries for purchasing.</summary>
    public ulong  InGameListingId { get; set; }
    /// <summary>Zero-based index in the server's offering list for this search request.</summary>
    public int    ListingIndex  { get; set; }
}
