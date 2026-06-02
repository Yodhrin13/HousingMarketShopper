namespace HousingMarketShopper.Models;

/// <summary>World information including datacenter membership.</summary>
public class WorldInfo
{
    public int    Id         { get; set; }
    public string Name       { get; set; } = string.Empty;
    public string DataCenter { get; set; } = string.Empty;
    public string Region     { get; set; } = string.Empty;
    public bool   IsPublic   { get; set; }
}
