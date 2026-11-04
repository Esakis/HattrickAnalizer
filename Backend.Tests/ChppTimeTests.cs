using HattrickAnalizer.Services;

namespace Backend.Tests;

public class ChppTimeTests
{
    [Theory]
    [InlineData("2026-01-10 18:00:00", "2026-01-10T17:00:00Z")]
    [InlineData("2026-07-10 18:00:00", "2026-07-10T16:00:00Z")]
    [InlineData("2026-07-10T18:00:00Z", "2026-07-10T18:00:00Z")]
    [InlineData("2026-07-10T18:00:00+02:00", "2026-07-10T16:00:00Z")]
    public void Converts_to_the_correct_instant(string raw, string expected)
    {
        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), ChppTime.Parse(raw));
        Assert.Equal(DateTimeKind.Utc, ChppTime.Parse(raw)!.Value.Kind);
    }

    [Fact]
    public void Weather_calendar_uses_the_Hattrick_date_across_UTC_midnight()
    {
        var instant=DateTime.Parse("2026-07-10T22:30:00Z").ToUniversalTime();
        Assert.Equal(new DateTime(2026,7,11),ChppTime.LocalDate(instant));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("2026-03-29 02:30:00")]
    [InlineData("2026-10-25 02:30:00")]
    public void Does_not_guess_missing_or_ambiguous_times(string? value) => Assert.Null(ChppTime.Parse(value));
}
