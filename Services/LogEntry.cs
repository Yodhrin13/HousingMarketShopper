namespace HousingMarketShopper.Services;

public enum LogTag { Info, Navigation, Purchase, Success, Partial, Warning, Error }
public readonly record struct LogEntry(string Text, LogTag Tag);
