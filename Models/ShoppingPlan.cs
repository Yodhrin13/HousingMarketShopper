using System.Collections.Generic;
using System.Linq;

namespace HousingMarketShopper.Models;

/// <summary>The complete shopping plan grouped by datacenter and world.</summary>
public class ShoppingPlan
{
    public List<DataCenterGroup> Groups        { get; set; } = [];
    public List<ShoppingItem>    Unresolved    { get; set; } = [];
    public List<ShoppingItem>    NotListed     { get; set; } = [];

    public int TotalEstimatedCost =>
        Groups.Sum(g => g.Worlds.Sum(w => w.TotalEstimatedCost));

    public int TotalItemCount =>
        Groups.Sum(g => g.Worlds.Sum(w => w.Items.Count));
}

public class DataCenterGroup
{
    public string            DataCenterName { get; set; } = string.Empty;
    public List<WorldGroup>  Worlds         { get; set; } = [];

    public int TotalEstimatedCost => Worlds.Sum(w => w.TotalEstimatedCost);
}

public class WorldGroup
{
    public string            WorldName { get; set; } = string.Empty;
    public int               WorldId   { get; set; }
    public List<ShoppingItem> Items    { get; set; } = [];

    public int TotalEstimatedCost => Items.Sum(i => i.TotalPrice);
    public int PendingCount       => Items.Count(i => i.Status == PurchaseStatus.Pending
                                                   || i.Status == PurchaseStatus.Confirmed);
    public int PurchasedCount     => Items.Count(i => i.Status == PurchaseStatus.Purchased);
}
