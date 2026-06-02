using System;

namespace HousingMarketShopper.Services;

/// <summary>Thrown when a required game addon does not become visible within the timeout.</summary>
public class AddonNotVisibleException(string addonName, int timeoutMs)
    : Exception($"Addon '{addonName}' did not appear within {timeoutMs} ms");

/// <summary>
/// Thrown when the actual listing price on the marketboard differs from the
/// Universalis snapshot price beyond an acceptable tolerance.
/// </summary>
public class PriceChangedException(int expected, int actual)
    : Exception($"Price changed: expected {expected:N0} gil, found {actual:N0} gil")
{
    public int Expected { get; } = expected;
    public int Actual   { get; } = actual;
}

/// <summary>Thrown when the player does not have enough gil to complete a purchase.</summary>
public class InsufficientFundsException(int required, int available)
    : Exception($"Insufficient gil: need {required:N0}, have {available:N0}")
{
    public int Required  { get; } = required;
    public int Available { get; } = available;
}

/// <summary>Thrown when the MB closes or changes state unexpectedly mid-loop.</summary>
public class MarketboardStateException(string message)
    : Exception(message);
