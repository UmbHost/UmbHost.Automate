namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>A clock stopped at one instant, so version ages don't drift with the calendar.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>The day these tests were written against: 18 November 2026.</summary>
    public static FixedClock Today { get; } = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    public static FixedClock At(int year, int month, int day) => new(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => now;
}
