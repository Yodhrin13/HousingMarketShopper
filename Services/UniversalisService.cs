using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HousingMarketShopper.Models;

namespace HousingMarketShopper.Services;

/// <summary>Queries the Universalis v2 API for current market listings.</summary>
public sealed class UniversalisService : IDisposable
{
    private const string BaseUrl      = "https://universalis.app/api/v2";
    private const int    BatchSize    = 10;    // items per request — Universalis 504s on large batches
    private const int    MaxListings  = 20;    // listings per item per request

    private readonly HttpClient   _http;
    private readonly IPluginLog   _log;
    private readonly RateLimiter  _rate = new(20); // 20 req/s
    // Cancelled on Dispose so in-flight fetches stop cleanly instead of spamming
    // "Cannot access a disposed object" through the retry loop.
    private readonly CancellationTokenSource _disposeCts = new();

    // ── Data-center / world catalogue ─────────────────────────────────────────
    public List<DataCenterInfo>   DataCenters { get; private set; } = [];
    public List<WorldCatalogEntry> Worlds     { get; private set; } = [];

    public UniversalisService(IPluginLog log)
    {
        _log  = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent
             .ParseAdd("HousingMarketShopper/1.0 (Dalamud plugin)");
    }

    // ── Catalogue fetch ───────────────────────────────────────────────────────

    public async Task LoadCatalogueAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(
            FetchDataCentersAsync(ct),
            FetchWorldsAsync(ct)
        );
    }

    private async Task FetchDataCentersAsync(CancellationToken ct)
    {
        try
        {
            var json = await _rate.ExecuteAsync(
                () => _http.GetStringAsync($"{BaseUrl}/data-centers", ct), ct);
            DataCenters = JsonSerializer.Deserialize<List<DataCenterInfo>>(json,
                              JsonOpts.Default) ?? [];
        }
        catch (Exception ex) { _log.Error($"[HMS] Failed to fetch DCs: {ex.Message}"); }
    }

    private async Task FetchWorldsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _rate.ExecuteAsync(
                () => _http.GetStringAsync($"{BaseUrl}/worlds", ct), ct);
            Worlds = JsonSerializer.Deserialize<List<WorldCatalogEntry>>(json,
                         JsonOpts.Default) ?? [];
        }
        catch (Exception ex) { _log.Error($"[HMS] Failed to fetch worlds: {ex.Message}"); }
    }

    // ── Listings fetch ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch cheapest listings for a set of item IDs across one datacenter.
    /// Returns a dict of itemId → sorted listing list (cheapest NQ first).
    /// </summary>
    public async Task<Dictionary<int, List<MarketListing>>> FetchListingsAsync(
        IEnumerable<int> itemIds,
        string           dcName,
        bool             preferNq,
        CancellationToken ct = default)
    {
        var result = new Dictionary<int, List<MarketListing>>();
        var batches = itemIds.Chunk(BatchSize);

        foreach (var batch in batches)
        {
            var ids    = string.Join(',', batch);
            // entries=0 skips the sale-history array (we only use listings) — a big cut in
            // server work and payload, which reduces 504s on large fetches.
            var url    = $"{BaseUrl}/{Uri.EscapeDataString(dcName)}/{ids}" +
                         $"?listings={MaxListings}&entries=0&noGst=true";
            var json   = await _rate.ExecuteAsync(
                             () => FetchWithRetryAsync(url, ct), ct);
            if (json == null) continue;

            // Single item → UniversalisMarketResponse
            // Multiple    → UniversalisMultiResponse (dict)
            if (batch.Length == 1)
            {
                var single = ParseSingle(json);
                if (single != null)
                    result[batch[0]] = SortListings(single, preferNq);
            }
            else
            {
                var multi = ParseMulti(json);
                foreach (var (id, listings) in multi)
                    result[id] = SortListings(listings, preferNq);
            }
        }

        return result;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private List<MarketListing>? ParseSingle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractListings(doc.RootElement);
        }
        catch (Exception ex) { _log.Warning($"[HMS] Parse error (single): {ex.Message}"); return null; }
    }

    private Dictionary<int, List<MarketListing>> ParseMulti(string json)
    {
        var result = new Dictionary<int, List<MarketListing>>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Multi-item response has "items" dict keyed by item ID string
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var prop in items.EnumerateObject())
                {
                    if (!int.TryParse(prop.Name, out var id)) continue;
                    var listings = ExtractListings(prop.Value);
                    if (listings != null) result[id] = listings;
                }
            }
        }
        catch (Exception ex) { _log.Warning($"[HMS] Parse error (multi): {ex.Message}"); }
        return result;
    }

    private static List<MarketListing>? ExtractListings(JsonElement root)
    {
        if (!root.TryGetProperty("listings", out var arr)) return null;

        var list = new List<MarketListing>();
        foreach (var el in arr.EnumerateArray())
        {
            list.Add(new MarketListing
            {
                PricePerUnit = el.TryGetProperty("pricePerUnit", out var p)  ? p.GetInt32()  : 0,
                Quantity     = el.TryGetProperty("quantity",     out var q)  ? q.GetInt32()  : 0,
                Total        = el.TryGetProperty("total",        out var t)  ? t.GetInt32()  : 0,
                IsHQ         = el.TryGetProperty("hq",           out var hq) && hq.GetBoolean(),
                WorldName    = el.TryGetProperty("worldName",    out var wn) ? wn.GetString() ?? "" : "",
                WorldId      = el.TryGetProperty("worldID",      out var wi) ? wi.GetInt32()  : 0,
                LastReviewTime = el.TryGetProperty("lastReviewTime", out var lr) ? lr.GetInt64() : 0,
                RetainerName = el.TryGetProperty("retainerName", out var rn) ? rn.GetString() ?? "" : "",
                ListingId    = el.TryGetProperty("listingID",    out var li) ? li.GetString() ?? "" : "",
            });
        }
        return list;
    }

    private static List<MarketListing> SortListings(
        List<MarketListing> raw, bool preferNq)
    {
        // Prefer NQ → sort NQ first, then by price ascending
        return preferNq
            ? [.. raw.OrderBy(l => l.IsHQ ? 1 : 0).ThenBy(l => l.PricePerUnit)]
            : [.. raw.OrderBy(l => l.PricePerUnit)];
    }

    // ── Best listing selection ────────────────────────────────────────────────

    /// <summary>
    /// From a list of listings, pick the cheapest source that can satisfy
    /// <paramref name="quantityNeeded"/> units on a single world.
    /// </summary>
    public static (string world, int pricePerUnit, int totalCost)?
        FindBestSource(List<MarketListing> listings, int quantityNeeded, bool preferNq)
    {
        // Group by world, then find worlds with enough stock
        var byWorld = listings.GroupBy(l => l.WorldName);

        (string world, int ppu, int total)? best = null;

        foreach (var group in byWorld)
        {
            var candidates = preferNq
                ? group.Where(l => !l.IsHQ).ToList()
                : group.ToList();

            if (candidates.Count == 0)
                candidates = group.ToList(); // fall back to HQ if no NQ

            // Walk listings sorted by price, accumulate stock
            var sorted    = candidates.OrderBy(l => l.PricePerUnit).ToList();
            var remaining = quantityNeeded;
            var totalCost = 0;
            var worstPpu  = 0;

            foreach (var l in sorted)
            {
                if (remaining <= 0) break;
                var take  = Math.Min(remaining, l.Quantity);
                totalCost += take * l.PricePerUnit;
                worstPpu   = l.PricePerUnit;
                remaining -= take;
            }

            if (remaining > 0) continue; // not enough stock on this world

            if (best == null || totalCost < best.Value.total)
                best = (group.Key, worstPpu, totalCost);
        }

        return best;
    }

    // ── HTTP helper ───────────────────────────────────────────────────────────

    private async Task<string?> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        // Delays: 3s, 8s, 15s — long enough for a 504 gateway to recover between batches.
        var retryDelays = new[] { 3, 8, 15 };

        for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            try
            {
                // Link the caller's token with the dispose token so unload aborts the fetch.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                return await _http.GetStringAsync(url, linked.Token);
            }
            catch (Exception ex) when (ex is ObjectDisposedException ||
                                       (ex is OperationCanceledException && _disposeCts.IsCancellationRequested))
            {
                // Service was disposed (plugin unload/reload) — stop immediately, no retry/spam.
                return null;
            }
            catch (Exception ex) when (attempt < retryDelays.Length)
            {
                var delay = retryDelays[attempt];
                _log.Warning($"[HMS] Attempt {attempt + 1} failed ({ex.Message}), retry in {delay}s");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { return null; }
            }
            catch (Exception ex)
            {
                _log.Error($"[HMS] Failed: {url} — {ex.Message}");
            }
        }
        return null;
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _http.Dispose();
        _disposeCts.Dispose();
    }
}

// ── DTO models for Universalis catalogue ─────────────────────────────────────

public class DataCenterInfo
{
    [JsonPropertyName("name")]    public string   Name   { get; set; } = "";
    [JsonPropertyName("region")]  public string   Region { get; set; } = "";
    [JsonPropertyName("worlds")]  public int[]    Worlds { get; set; } = [];
}

public class WorldCatalogEntry
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

// ── Rate limiter ─────────────────────────────────────────────────────────────

internal sealed class RateLimiter
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly int           _minDelayMs;
    private DateTime               _lastCall = DateTime.MinValue;

    public RateLimiter(int maxPerSecond)
        => _minDelayMs = 1000 / maxPerSecond;

    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            var elapsed = (DateTime.UtcNow - _lastCall).TotalMilliseconds;
            if (elapsed < _minDelayMs)
                await Task.Delay((int)(_minDelayMs - elapsed), ct);
            _lastCall = DateTime.UtcNow;
            return await action();
        }
        finally { _sem.Release(); }
    }
}

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
