using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HousingMarketShopper.Models;

namespace HousingMarketShopper.Services;

/// <summary>
/// Resolves item names from the shopping list file to FFXIV item IDs,
/// using XIVAPI CSV and Teamcraft JSON as data sources.
/// </summary>
public sealed class ItemResolverService : IDisposable
{
    private const string XivapiCsvUrl    = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/Item.csv";
    private const string TeamcraftJsonUrl = "https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/master/libs/data/src/lib/json/items.json";
    private const string WorldCsvUrl     = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/World.csv";
    private const string GilShopItemCsvUrl = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/GilShopItem.csv";
    private const double CacheTtlHours   = 24;

    private readonly HttpClient  _http;
    private readonly IPluginLog  _log;
    private readonly string      _cacheDir;

    // item name (lower) -> item ID, and the reverse id -> proper-case name
    private Dictionary<string, int> _itemMap   = [];
    private Dictionary<int, string> _itemNames = [];
    private Dictionary<int, WorldInfo> _worldMap = [];
    // Item IDs sold by any gil-shop NPC vendor (so they need not be bought off the market board).
    private HashSet<int> _npcSoldItemIds = [];

    public bool IsItemDataLoaded => _itemMap.Count > 0;
    public bool IsWorldDataLoaded => _worldMap.Count > 0;

    /// <summary>True if the item is sold by an NPC vendor for gil.</summary>
    public bool IsNpcSold(int itemId) => _npcSoldItemIds.Contains(itemId);

    /// <summary>Canonical (proper-case) display name for an item ID, if known.</summary>
    public string? GetItemName(int id) => _itemNames.GetValueOrDefault(id);

    /// <summary>
    /// All known item names as (id, name) pairs, for manual-resolution search.
    /// </summary>
    public IReadOnlyDictionary<int, string> ItemNames => _itemNames;

    /// <summary>User-pinned name->id overrides, consulted before fuzzy matching.</summary>
    public Dictionary<string, int> Overrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Case-insensitive substring search over item names, shortest first.</summary>
    public List<(int id, string name)> SearchItems(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        return _itemNames
            .Where(kv => kv.Value.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Length)
            .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    // ── Section header patterns to skip ──────────────────────────────────────
    private static readonly string[] SectionKeywords =
        ["furniture", "dyes", "====================="];

    // ── Dye colour -> purchaseable item name ───────────────────────────────────
    // Sourced from the in-game dye consolidation: most classic colours now share
    // "Standard Spectrum Dye"; the extended palette uses Spectrum #1/#2; and the
    // special/metallic shades retain their individual General-purpose items.
    private static readonly Dictionary<string, string> DyeColorToItem =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Standard Spectrum Dye ─────────────────────────────────────────────
        // White/Grey/Black
        ["Snow White"]          = "Standard Spectrum Dye",
        ["Ash Grey"]            = "Standard Spectrum Dye",
        ["Goobbue Grey"]        = "Standard Spectrum Dye",
        ["Slate Grey"]          = "Standard Spectrum Dye",
        ["Charcoal Grey"]       = "Standard Spectrum Dye",
        ["Soot Black"]          = "Standard Spectrum Dye",
        // Red/Pink
        ["Rose Pink"]           = "Standard Spectrum Dye",
        ["Lilac Purple"]        = "Standard Spectrum Dye",
        ["Rolanberry Red"]      = "Standard Spectrum Dye",
        ["Dalamud Red"]         = "Standard Spectrum Dye",
        ["Rust Red"]            = "Standard Spectrum Dye",
        ["Wine Red"]            = "Standard Spectrum Dye",
        ["Coral Pink"]          = "Standard Spectrum Dye",
        ["Blood Red"]           = "Standard Spectrum Dye",
        ["Salmon Pink"]         = "Standard Spectrum Dye",
        // Orange/Brown
        ["Sunset Orange"]       = "Standard Spectrum Dye",
        ["Mesa Red"]            = "Standard Spectrum Dye",
        ["Bark Brown"]          = "Standard Spectrum Dye",
        ["Chocolate Brown"]     = "Standard Spectrum Dye",
        ["Russet Brown"]        = "Standard Spectrum Dye",
        ["Kobold Brown"]        = "Standard Spectrum Dye",
        ["Cork Brown"]          = "Standard Spectrum Dye",
        ["Qiqirn Brown"]        = "Standard Spectrum Dye",
        ["Opo-opo Brown"]       = "Standard Spectrum Dye",
        ["Aldgoat Brown"]       = "Standard Spectrum Dye",
        ["Pumpkin Orange"]      = "Standard Spectrum Dye",
        ["Acorn Brown"]         = "Standard Spectrum Dye",
        ["Orchard Brown"]       = "Standard Spectrum Dye",
        ["Chestnut Brown"]      = "Standard Spectrum Dye",
        ["Gobbiebag Brown"]     = "Standard Spectrum Dye",
        ["Shale Brown"]         = "Standard Spectrum Dye",
        ["Mole Brown"]          = "Standard Spectrum Dye",
        ["Loam Brown"]          = "Standard Spectrum Dye",
        // Yellow
        ["Bone White"]          = "Standard Spectrum Dye",
        ["Ul Brown"]            = "Standard Spectrum Dye",
        ["Desert Yellow"]       = "Standard Spectrum Dye",
        ["Honey Yellow"]        = "Standard Spectrum Dye",
        ["Millioncorn Yellow"]  = "Standard Spectrum Dye",
        ["Coeurl Yellow"]       = "Standard Spectrum Dye",
        ["Cream Yellow"]        = "Standard Spectrum Dye",
        ["Halatali Yellow"]     = "Standard Spectrum Dye",
        ["Raisin Brown"]        = "Standard Spectrum Dye",
        // Green
        ["Mud Green"]           = "Standard Spectrum Dye",
        ["Sylph Green"]         = "Standard Spectrum Dye",
        ["Lime Green"]          = "Standard Spectrum Dye",
        ["Moss Green"]          = "Standard Spectrum Dye",
        ["Meadow Green"]        = "Standard Spectrum Dye",
        ["Olive Green"]         = "Standard Spectrum Dye",
        ["Marsh Green"]         = "Standard Spectrum Dye",
        ["Apple Green"]         = "Standard Spectrum Dye",
        ["Cactuar Green"]       = "Standard Spectrum Dye",
        ["Hunter Green"]        = "Standard Spectrum Dye",
        ["Ochu Green"]          = "Standard Spectrum Dye",
        ["Adamantoise Green"]   = "Standard Spectrum Dye",
        ["Nophica Green"]       = "Standard Spectrum Dye",
        ["Deepwood Green"]      = "Standard Spectrum Dye",
        ["Celeste Green"]       = "Standard Spectrum Dye",
        ["Turquoise Green"]     = "Standard Spectrum Dye",
        ["Morbol Green"]        = "Standard Spectrum Dye",
        // Blue
        ["Ice Blue"]            = "Standard Spectrum Dye",
        ["Sky Blue"]            = "Standard Spectrum Dye",
        ["Seafog Blue"]         = "Standard Spectrum Dye",
        ["Peacock Blue"]        = "Standard Spectrum Dye",
        ["Rhotano Blue"]        = "Standard Spectrum Dye",
        ["Corpse Blue"]         = "Standard Spectrum Dye",
        ["Ceruleum Blue"]       = "Standard Spectrum Dye",
        ["Woad Blue"]           = "Standard Spectrum Dye",
        ["Ink Blue"]            = "Standard Spectrum Dye",
        ["Raptor Blue"]         = "Standard Spectrum Dye",
        ["Othard Blue"]         = "Standard Spectrum Dye",
        ["Storm Blue"]          = "Standard Spectrum Dye",
        ["Void Blue"]           = "Standard Spectrum Dye",
        ["Royal Blue"]          = "Standard Spectrum Dye",
        ["Midnight Blue"]       = "Standard Spectrum Dye",
        ["Shadow Blue"]         = "Standard Spectrum Dye",
        ["Abyssal Blue"]        = "Standard Spectrum Dye",
        // Purple
        ["Lavender Purple"]     = "Standard Spectrum Dye",
        ["Gloom Purple"]        = "Standard Spectrum Dye",
        ["Currant Purple"]      = "Standard Spectrum Dye",
        ["Iris Purple"]         = "Standard Spectrum Dye",
        ["Grape Purple"]        = "Standard Spectrum Dye",
        ["Lotus Pink"]          = "Standard Spectrum Dye",
        ["Colibri Pink"]        = "Standard Spectrum Dye",
        ["Plum Purple"]         = "Standard Spectrum Dye",
        ["Regal Purple"]        = "Standard Spectrum Dye",

        // ── Wide Spectrum #1 Dye ──────────────────────────────────────────────
        ["Ruby Red"]            = "Wide Spectrum #1 Dye",
        ["Cherry Pink"]         = "Wide Spectrum #1 Dye",
        ["Canary Yellow"]       = "Wide Spectrum #1 Dye",
        ["Vanilla Yellow"]      = "Wide Spectrum #1 Dye",
        ["Dragoon Blue"]        = "Wide Spectrum #1 Dye",
        ["Turquoise Blue"]      = "Wide Spectrum #1 Dye",
        ["Gunmetal Black"]      = "Wide Spectrum #1 Dye",
        ["Pearl White"]         = "Wide Spectrum #1 Dye",
        ["Metallic Brass"]      = "Wide Spectrum #1 Dye",

        // ── Wide Spectrum #2 Dye ──────────────────────────────────────────────
        ["Carmine Red"]         = "Wide Spectrum #2 Dye",
        ["Neon Pink"]           = "Wide Spectrum #2 Dye",
        ["Bright Orange"]       = "Wide Spectrum #2 Dye",
        ["Neon Yellow"]         = "Wide Spectrum #2 Dye",
        ["Neon Green"]          = "Wide Spectrum #2 Dye",
        ["Azure Blue"]          = "Wide Spectrum #2 Dye",
        ["Violet Purple"]       = "Wide Spectrum #2 Dye",
        ["Metallic Pink"]       = "Wide Spectrum #2 Dye",
        ["Metallic Ruby Red"]   = "Wide Spectrum #2 Dye",
        ["Metallic Cobalt Green"]  = "Wide Spectrum #2 Dye",
        ["Metallic Dark Blue"]  = "Wide Spectrum #2 Dye",

        // ── General-purpose individual dyes ───────────────────────────────────
        ["Pure White"]          = "General-purpose Pure White Dye",
        ["Jet Black"]           = "General-purpose Jet Black Dye",
        ["Pastel Pink"]         = "General-purpose Pastel Pink Dye",
        ["Dark Red"]            = "General-purpose Dark Red Dye",
        ["Dark Brown"]          = "General-purpose Dark Brown Dye",
        ["Pastel Green"]        = "General-purpose Pastel Green Dye",
        ["Dark Green"]          = "General-purpose Dark Green Dye",
        ["Pastel Blue"]         = "General-purpose Pastel Blue Dye",
        ["Dark Blue"]           = "General-purpose Dark Blue Dye",
        ["Pastel Purple"]       = "General-purpose Pastel Purple Dye",
        ["Dark Purple"]         = "General-purpose Dark Purple Dye",
        ["Metallic Silver"]     = "General-purpose Metallic Silver Dye",
        ["Metallic Gold"]       = "General-purpose Metallic Gold Dye",
        ["Metallic Red"]        = "General-purpose Metallic Red Dye",
        ["Metallic Orange"]     = "General-purpose Metallic Orange Dye",
        ["Metallic Yellow"]     = "General-purpose Metallic Yellow Dye",
        ["Metallic Green"]      = "General-purpose Metallic Green Dye",
        ["Metallic Sky Blue"]   = "General-purpose Metallic Sky Blue Dye",
        ["Metallic Blue"]       = "General-purpose Metallic Blue Dye",
        ["Metallic Purple"]     = "General-purpose Metallic Purple Dye",
    };

    // ── Quantity patterns ─────────────────────────────────────────────────────
    // Matches:  "Oak Loft: 3"  "Oak Loft x3"  "Oak Loft (3)"
    private static readonly Regex QtyColonRx = new(@":\s*(\d+)\s*$",       RegexOptions.Compiled);
    private static readonly Regex QtyXRx     = new(@"\bx\s*(\d+)\s*$",     RegexOptions.Compiled);
    private static readonly Regex QtyParenRx = new(@"\(\s*(\d+)\s*\)\s*$", RegexOptions.Compiled);

    // Matches dye names in parentheses that are NOT a bare number
    // e.g. "(Kobold Brown)" but not "(3)"
    private static readonly Regex DyeParenRx =
        new(@"\(\s*([A-Za-z][^)]*)\s*\)\s*$", RegexOptions.Compiled);

    public ItemResolverService(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log      = log;
        _cacheDir = pi.GetPluginConfigDirectory();
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task LoadDataAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(
            LoadItemDataAsync(ct),
            LoadWorldDataAsync(ct)
        );
    }

    public IReadOnlyDictionary<int, WorldInfo> WorldMap => _worldMap;

    /// <summary>
    /// Parse a .txt shopping list file into <see cref="ShoppingItem"/> objects
    /// with item IDs resolved where possible.
    /// </summary>
    /// <remarks>
    /// MakePlace .list files contain three sections:
    ///   1. "Furniture"            — plain item list with no dye info (summary only)
    ///   2. "Dyes"                 — dye quantities (player applies these separately)
    ///   3. "Furniture (With Dye)" — authoritative list: every item with its dye assignment
    ///
    /// Only section 3 is parsed when detected, to avoid double-counting items that
    /// appear in both the plain Furniture section and the Furniture (With Dye) section.
    /// Files without section headers are parsed in full (backwards compatibility).
    /// </remarks>
    public List<ShoppingItem> ParseFile(string filePath)
    {
        var allLines = File.ReadAllLines(filePath, Encoding.UTF8);

        // Detect the MakePlace 3-section format.
        var hasDyeSection = allLines.Any(l =>
            l.Trim().IndexOf("with dye", StringComparison.OrdinalIgnoreCase) >= 0);

        var raw       = new List<ShoppingItem>();
        var dyeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // If there's no "Furniture (With Dye)" section, treat the whole file as item lines.
        var inTarget = !hasDyeSection;

        foreach (var rawLine in allLines)
        {
            var trimmed = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith('='))            continue;
            if (trimmed.StartsWith('#'))            continue;

            if (IsSectionHeader(trimmed))
            {
                // Enter the "Furniture (With Dye)" section; skip all others.
                inTarget = hasDyeSection &&
                           trimmed.IndexOf("with dye", StringComparison.OrdinalIgnoreCase) >= 0;
                continue;
            }

            if (!inTarget) continue;

            var item = TryParseLine(rawLine);
            if (item == null) continue;

            // If the item had a dye in parentheses, count it then strip it.
            // We only need to buy the base furniture piece; dyes are added separately below.
            if (!string.IsNullOrEmpty(item.DyeName))
            {
                dyeCounts[item.DyeName] = dyeCounts.GetValueOrDefault(item.DyeName) + item.QuantityNeeded;
                item.DyeName = null;
            }

            Resolve(item);
            raw.Add(item);
        }

        // Aggregate dye colours into purchaseable items.
        // Multiple colours can share one item (e.g. Standard Spectrum Dye), so sum first.
        var dyeItemTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (colour, qty) in dyeCounts)
        {
            var itemName = DyeColorToItem.TryGetValue(colour, out var mapped) ? mapped : colour;
            dyeItemTotals[itemName] = dyeItemTotals.GetValueOrDefault(itemName) + qty;
        }
        foreach (var (dyeItemName, qty) in dyeItemTotals)
        {
            var dyeItem = new ShoppingItem
            {
                RawLine        = $"[Dye] {dyeItemName}: {qty}",
                Name           = dyeItemName,
                DyeName        = null,
                QuantityNeeded = qty,
            };
            Resolve(dyeItem);
            raw.Add(dyeItem);
        }

        return MergeByItemId(raw);
    }

    // "Oak Loft: 3" and "Oak Loft (Kobold Brown): 2" both resolve to the same
    // item ID — merge them so the plan only shows one purchase of 5 Oak Lofts.
    private static List<ShoppingItem> MergeByItemId(List<ShoppingItem> items)
    {
        var seen   = new Dictionary<int, ShoppingItem>();
        var result = new List<ShoppingItem>();

        foreach (var item in items)
        {
            if (item.ItemId == 0 || item.ResolveQuality == ResolveQuality.Unresolved)
            {
                result.Add(item);
                continue;
            }

            if (seen.TryGetValue(item.ItemId, out var existing))
            {
                existing.QuantityNeeded += item.QuantityNeeded;
                // Multiple entries with different (or absent) dyes — clear the label
                // since we're buying the base furniture piece either way.
                if (existing.DyeName != item.DyeName)
                    existing.DyeName = null;
            }
            else
            {
                seen[item.ItemId] = item;
                result.Add(item);
            }
        }

        return result;
    }

    // ── Line parsing ──────────────────────────────────────────────────────────

    private ShoppingItem? TryParseLine(string raw)
    {
        var trimmed = raw.Trim();

        // Skip blank, comment, separator, or section header lines
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        if (trimmed.StartsWith('#'))            return null;
        if (trimmed.StartsWith('='))            return null;
        if (IsSectionHeader(trimmed))           return null;

        // Extract quantity
        var (itemText, qty) = ExtractQuantity(trimmed);
        if (string.IsNullOrWhiteSpace(itemText)) return null;

        // Extract dye from parentheses, e.g. "Ash Cabinet (Kobold Brown)"
        string? dyeName = null;
        var dyeMatch = DyeParenRx.Match(itemText);
        if (dyeMatch.Success)
        {
            dyeName  = dyeMatch.Groups[1].Value.Trim();
            itemText = itemText[..dyeMatch.Index].Trim();
        }

        return new ShoppingItem
        {
            RawLine        = raw,
            Name           = itemText.Trim(),
            DyeName        = dyeName,
            QuantityNeeded = qty,
        };
    }

    private static bool IsSectionHeader(string line)
    {
        // A header has no ": N" at the end where N is a pure number
        var hasQuantity = QtyColonRx.IsMatch(line) || QtyXRx.IsMatch(line);
        if (hasQuantity) return false;

        var lower = line.ToLowerInvariant();
        return SectionKeywords.Any(k => lower.Contains(k));
    }

    private static (string text, int qty) ExtractQuantity(string line)
    {
        var m = QtyColonRx.Match(line);
        if (m.Success)
            return (line[..m.Index].Trim(), int.Parse(m.Groups[1].Value));

        m = QtyXRx.Match(line);
        if (m.Success)
            return (line[..m.Index].Trim(), int.Parse(m.Groups[1].Value));

        m = QtyParenRx.Match(line);
        if (m.Success)
            return (line[..m.Index].Trim(), int.Parse(m.Groups[1].Value));

        return (line, 1);
    }

    // ── Name -> ID resolution ─────────────────────────────────────────────────

    private void Resolve(ShoppingItem item)
    {
        if (_itemMap.Count == 0) return;

        var lower = item.Name.ToLowerInvariant().Trim();

        // User-pinned override takes precedence over everything.
        if (Overrides.TryGetValue(lower, out var ovId))
        {
            item.ItemId           = ovId;
            item.ResolveQuality   = ResolveQuality.Exact;
            item.ResolvedItemName = _itemNames.GetValueOrDefault(ovId);
            item.IsManualOverride = true;
            return;
        }

        // Exact match
        if (_itemMap.TryGetValue(lower, out var id))
        {
            item.ItemId           = id;
            item.ResolveQuality   = ResolveQuality.Exact;
            item.ResolvedItemName = _itemNames.GetValueOrDefault(id);
            return;
        }

        // Fuzzy match: lowest Levenshtein distance within a tolerance
        var best      = int.MaxValue;
        var bestKey   = string.Empty;
        var bestId    = 0;

        foreach (var (key, keyId) in _itemMap)
        {
            if (Math.Abs(key.Length - lower.Length) > 5) continue; // fast pre-filter
            var dist = Levenshtein(lower, key);
            if (dist < best)
            {
                best    = dist;
                bestKey = key;
                bestId  = keyId;
            }
        }

        // Accept fuzzy match if distance ≤ 3 or ≤ 15 % of name length
        var threshold = Math.Max(3, (int)(lower.Length * 0.15));
        if (best <= threshold)
        {
            item.ItemId           = bestId;
            item.ResolveQuality   = ResolveQuality.FuzzyMatch;
            item.ResolvedItemName = _itemNames.GetValueOrDefault(bestId) ?? bestKey;
            item.FuzzyDistance    = best;
            item.ResolveWarning   = $"Fuzzy match: '{item.Name}' -> '{item.ResolvedItemName}' (dist {best})";
            _log.Warning($"[HMS] Fuzzy matched '{item.Name}' -> '{bestKey}' (id {bestId})");
            return;
        }

        item.ResolveQuality = ResolveQuality.Unresolved;
        item.ResolveWarning = $"Could not resolve item name: '{item.Name}'";
        _log.Warning($"[HMS] Unresolved item: '{item.Name}'");
    }

    // ── Data loading & caching ────────────────────────────────────────────────

    private async Task LoadItemDataAsync(CancellationToken ct)
    {
        var xivapiPath    = Path.Combine(_cacheDir, "items_xivapi.cache");
        var teamcraftPath = Path.Combine(_cacheDir, "items_teamcraft.cache");
        var gilshopPath   = Path.Combine(_cacheDir, "gilshop.cache");

        var xivapiTask    = FetchCachedAsync(XivapiCsvUrl,    xivapiPath,    ct);
        var teamcraftTask = FetchCachedAsync(TeamcraftJsonUrl, teamcraftPath, ct);
        var gilshopTask   = FetchCachedAsync(GilShopItemCsvUrl, gilshopPath,  ct);

        await Task.WhenAll(xivapiTask, teamcraftTask, gilshopTask);

        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names  = new Dictionary<int, string>();

        if (xivapiTask.Result is { } xivapiText)
            ParseXivapiCsv(xivapiText, merged, names);

        if (teamcraftTask.Result is { } tcText)
            ParseTeamcraftJson(tcText, merged, names);

        var npcSold = new HashSet<int>();
        if (gilshopTask.Result is { } gsText)
            ParseGilShopCsv(gsText, npcSold);

        _itemMap        = merged;
        _itemNames      = names;
        _npcSoldItemIds = npcSold;
        _log.Information($"[HMS] Item map loaded: {_itemMap.Count} entries; " +
                         $"{_npcSoldItemIds.Count} NPC-sold items");
    }

    private async Task LoadWorldDataAsync(CancellationToken ct)
    {
        var worldPath = Path.Combine(_cacheDir, "worlds.cache");
        var text = await FetchCachedAsync(WorldCsvUrl, worldPath, ct);
        if (text == null) return;

        var map = new Dictionary<int, WorldInfo>();
        ParseWorldCsv(text, map);
        _worldMap = map;
        _log.Information($"[HMS] World map loaded: {_worldMap.Count} entries");
    }

    private async Task<string?> FetchCachedAsync(string url, string cachePath, CancellationToken ct)
    {
        // Return cached file if fresh
        if (File.Exists(cachePath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            if (age.TotalHours < CacheTtlHours)
            {
                _log.Debug($"[HMS] Using cache: {cachePath}");
                return await File.ReadAllTextAsync(cachePath, ct);
            }
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _log.Information($"[HMS] Fetching {url}");
                var text = await _http.GetStringAsync(url, ct);
                await File.WriteAllTextAsync(cachePath, text, ct);
                return text;
            }
            catch (Exception ex) when (attempt < 2)
            {
                _log.Warning($"[HMS] Fetch attempt {attempt + 1} failed for {url}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _log.Error($"[HMS] Failed to fetch {url}: {ex.Message}");
            }
        }

        // Fall back to stale cache if available
        if (File.Exists(cachePath))
            return await File.ReadAllTextAsync(cachePath, ct);

        return null;
    }

    // ── CSV / JSON parsers ────────────────────────────────────────────────────

    private static void ParseXivapiCsv(
        string csv, Dictionary<string, int> map, Dictionary<int, string> names)
    {
        // Row 0 = column headers, Row 1 = type row, rows 2+ = data
        // Columns: #,Name,Description,...  (column 0 = ID, column 1 = Name)
        using var reader = new StringReader(csv);
        var headerSeen = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (headerSeen < 2) { headerSeen++; continue; }

            var parts = SplitCsvLine(line);
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[0], out var id)) continue;
            var name = parts[1].Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(name)) continue;
            map.TryAdd(name.ToLowerInvariant(), id);
            names.TryAdd(id, name);
        }
    }

    private static void ParseTeamcraftJson(
        string json, Dictionary<string, int> map, Dictionary<int, string> names)
    {
        // { "12345": { "en": "Item Name", "de": "...", ... }, ... }
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var id)) continue;
            if (!prop.Value.TryGetProperty("en", out var enProp)) continue;
            var name = enProp.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            map.TryAdd(name.ToLowerInvariant(), id);
            names.TryAdd(id, name);
        }
    }

    private static void ParseGilShopCsv(string csv, HashSet<int> set)
    {
        // Format: header row "#,Item,QuestRequired[0],...", then data rows where
        // column 0 is the shop subrow key (e.g. "262144.0") and column 1 is the item ID.
        using var reader = new StringReader(csv);
        string? line;
        var headerParsed = false;
        var itemCol = 1;

        while ((line = reader.ReadLine()) != null)
        {
            var parts = SplitCsvLine(line);
            if (!headerParsed)
            {
                headerParsed = true;
                for (var i = 0; i < parts.Length; i++)
                    if (parts[i].Trim().Equals("Item", StringComparison.OrdinalIgnoreCase))
                    {
                        itemCol = i;
                        break;
                    }
                continue;
            }

            if (parts.Length <= itemCol) continue;
            if (int.TryParse(parts[itemCol].Trim(), out var id) && id > 0)
                set.Add(id);
        }
    }

    private static void ParseWorldCsv(string csv, Dictionary<int, WorldInfo> map)
    {
        // Columns: #,Name,UserType,DataCenter,IsPublic,...
        // DataCenter column is a row reference; we'll use name only for now
        using var reader = new StringReader(csv);
        var headerSeen = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (headerSeen < 2) { headerSeen++; continue; }
            var parts = SplitCsvLine(line);
            if (parts.Length < 5) continue;
            if (!int.TryParse(parts[0], out var id)) continue;
            var name = parts[1].Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(name)) continue;

            bool.TryParse(parts[4].Trim(), out var isPublic);
            map[id] = new WorldInfo { Id = id, Name = name, IsPublic = isPublic };
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] SplitCsvLine(string line)
    {
        // Simple CSV split — handles quoted fields with commas
        var fields  = new List<string>();
        var sb      = new StringBuilder();
        var inQuote = false;
        foreach (var ch in line)
        {
            if (ch == '"')      { inQuote = !inQuote; continue; }
            if (ch == ',' && !inQuote) { fields.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return [.. fields];
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            dp[i, j] = a[i - 1] == b[j - 1]
                ? dp[i - 1, j - 1]
                : 1 + Math.Min(dp[i - 1, j], Math.Min(dp[i, j - 1], dp[i - 1, j - 1]));
        }
        return dp[a.Length, b.Length];
    }

    public void Dispose() => _http.Dispose();
}
