namespace NovaWallet.Api.Application;

/// <summary>Abstraction over the clock so tests can control "today" for daily-limit windows.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
