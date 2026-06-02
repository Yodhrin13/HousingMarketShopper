using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HousingMarketShopper.Services;

/// <summary>
/// Reusable async helpers for polling Dalamud game addon visibility.
/// All methods are safe to call from any thread — they never dereference
/// game memory directly; they schedule the pointer check on the framework thread.
/// </summary>
public static class AddonWaiter
{
    /// <summary>
    /// Polls until the named addon is visible, or the timeout elapses.
    /// </summary>
    /// <returns><c>true</c> if the addon became visible; <c>false</c> on timeout.</returns>
    public static async Task<bool> WaitForAddonAsync(
        string            addonName,
        IGameGui          gameGui,
        IFramework        framework,
        int               timeoutMs      = 5_000,
        int               pollIntervalMs = 100,
        CancellationToken ct             = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var visible = await framework.RunOnFrameworkThread(
                () => IsAddonVisible(gameGui, addonName));
            if (visible) return true;
            await Task.Delay(pollIntervalMs, ct);
        }
        return false;
    }

    /// <summary>
    /// Polls until the named addon is no longer visible (dismissed / closed).
    /// </summary>
    /// <returns><c>true</c> if the addon closed; <c>false</c> on timeout.</returns>
    public static async Task<bool> WaitForAddonCloseAsync(
        string            addonName,
        IGameGui          gameGui,
        IFramework        framework,
        int               timeoutMs      = 5_000,
        int               pollIntervalMs = 100,
        CancellationToken ct             = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var visible = await framework.RunOnFrameworkThread(
                () => IsAddonVisible(gameGui, addonName));
            if (!visible) return true;
            await Task.Delay(pollIntervalMs, ct);
        }
        return false;
    }

    /// <summary>
    /// Waits until the addon is visible, then immediately throws
    /// <see cref="AddonNotVisibleException"/> if it never appeared.
    /// </summary>
    public static async Task RequireAddonAsync(
        string            addonName,
        IGameGui          gameGui,
        IFramework        framework,
        int               timeoutMs      = 5_000,
        CancellationToken ct             = default)
    {
        if (!await WaitForAddonAsync(addonName, gameGui, framework, timeoutMs, ct: ct))
            throw new AddonNotVisibleException(addonName, timeoutMs);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Must be called on the framework thread.
    /// Returns true when the addon exists AND its IsVisible flag is set.
    /// </summary>
    internal static unsafe bool IsAddonVisible(IGameGui gameGui, string name)
    {
        var ptr = (AtkUnitBase*)(nint)gameGui.GetAddonByName(name);
        return ptr != null && ptr->IsVisible;
    }

    /// <summary>
    /// Returns the addon pointer if it is currently visible, otherwise null.
    /// Must be called on the framework thread.
    /// </summary>
    internal static unsafe AtkUnitBase* GetVisibleAddon(IGameGui gameGui, string name)
    {
        var ptr = (AtkUnitBase*)(nint)gameGui.GetAddonByName(name);
        return ptr != null && ptr->IsVisible ? ptr : null;
    }
}
