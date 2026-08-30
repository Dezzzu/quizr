using AwesomeAssertions;
using Quizr.App.Validation;

namespace Quizr.App.Tests;

public class FieldParsingTests
{
    [Test]
    [Arguments("The Pub")]
    [Arguments("  The Pub  ")]
    public void TryParseTextAcceptsAndTrimsNonEmptyInput(string input)
    {
        FieldParsing.TryParseText(input, out var value, out var errorKey).Should().BeTrue();
        value.Should().Be("The Pub");
        errorKey.Should().BeNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void TryParseTextRejectsEmptyInput(string? input)
    {
        FieldParsing.TryParseText(input, out _, out var errorKey).Should().BeFalse();
        errorKey.Should().Be("Validation.Empty");
    }

    [Test]
    public void TryParseOptionalTextTreatsEmptyInputAsClearingTheField()
    {
        FieldParsing.TryParseOptionalText("  ", out var value, out var errorKey).Should().BeTrue();
        value.Should().BeNull();
        errorKey.Should().BeNull();
    }

    [Test]
    public void TryParseOptionalTextTrimsNonEmptyInput()
    {
        FieldParsing.TryParseOptionalText("  Bring your own pen  ", out var value, out _).Should().BeTrue();
        value.Should().Be("Bring your own pen");
    }

    [Test]
    [Arguments("10", 10)]
    [Arguments(" 1 ", 1)]
    public void TryParseCapacityAcceptsPositiveIntegers(string input, int expected)
    {
        FieldParsing.TryParseCapacity(input, out var value, out var errorKey).Should().BeTrue();
        value.Should().Be(expected);
        errorKey.Should().BeNull();
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("abc")]
    [Arguments(null)]
    public void TryParseCapacityRejectsNonPositiveOrNonNumericInput(string? input)
    {
        FieldParsing.TryParseCapacity(input, out _, out var errorKey).Should().BeFalse();
        errorKey.Should().Be("Validation.CapacityInvalid");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("skip")]
    [Arguments("SKIP")]
    public void TryParsePriceTreatsSkipOrEmptyAsNoPrice(string? input)
    {
        FieldParsing.TryParsePrice(input, out var value, out var errorKey).Should().BeTrue();
        value.Should().BeNull();
        errorKey.Should().BeNull();
    }

    [Test]
    public void TryParsePriceAcceptsANonNegativeNumber()
    {
        FieldParsing.TryParsePrice("12.50", out var value, out var errorKey).Should().BeTrue();
        value.Should().Be(12.50m);
        errorKey.Should().BeNull();
    }

    [Test]
    [Arguments("-1")]
    [Arguments("free")]
    public void TryParsePriceRejectsNegativeOrNonNumericInput(string input)
    {
        FieldParsing.TryParsePrice(input, out _, out var errorKey).Should().BeFalse();
        errorKey.Should().Be("Validation.PriceInvalid");
    }

    [Test]
    public void TryParseDateAcceptsIsoFormat()
    {
        FieldParsing.TryParseDate("2026-09-12", out var value, out var errorKey).Should().BeTrue();
        value.Should().Be(new DateOnly(2026, 9, 12));
        errorKey.Should().BeNull();
    }

    [Test]
    [Arguments("12/09/2026")]
    [Arguments("not a date")]
    public void TryParseDateRejectsOtherFormats(string input)
    {
        FieldParsing.TryParseDate(input, out _, out var errorKey).Should().BeFalse();
        errorKey.Should().Be("Validation.DateInvalid");
    }

    [Test]
    public void TryParseTimeAcceptsTwentyFourHourFormat()
    {
        FieldParsing.TryParseTime("19:00", out var value, out var errorKey).Should().BeTrue();
        value.Should().Be(new TimeOnly(19, 0));
        errorKey.Should().BeNull();
    }

    [Test]
    [Arguments("7pm")]
    [Arguments("25:00")]
    public void TryParseTimeRejectsOtherFormats(string input)
    {
        FieldParsing.TryParseTime(input, out _, out var errorKey).Should().BeFalse();
        errorKey.Should().Be("Validation.TimeInvalid");
    }

    [Test]
    public void TryParseScheduleExpandsADayRangeAndSingleDays()
    {
        FieldParsing
            .TryParseSchedule("Mon-Fri: 19:00, Sat: 16:00, Sun: 16:00", out var value, out var errorKey)
            .Should()
            .BeTrue();

        value.Should().HaveCount(7);
        value[DayOfWeek.Monday].Should().Be(new TimeOnly(19, 0));
        value[DayOfWeek.Friday].Should().Be(new TimeOnly(19, 0));
        value[DayOfWeek.Saturday].Should().Be(new TimeOnly(16, 0));
        value[DayOfWeek.Sunday].Should().Be(new TimeOnly(16, 0));
        errorKey.Should().BeNull();
    }

    // A range wraps the week, same as any franchise that plays Friday through Monday.
    [Test]
    public void TryParseScheduleWrapsARangeAcrossTheWeekBoundary()
    {
        FieldParsing.TryParseSchedule("Fri-Mon: 20:00", out var value, out _).Should().BeTrue();

        value
            .Keys.Should()
            .BeEquivalentTo(new[] { DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday });
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("Someday: 19:00")]
    [Arguments("Mon 19:00")]
    [Arguments("Mon: 25:99")]
    public void TryParseScheduleRejectsInvalidInput(string? input)
    {
        FieldParsing.TryParseSchedule(input, out var value, out var errorKey).Should().BeFalse();
        value.Should().BeEmpty();
        errorKey.Should().Be("Validation.ScheduleInvalid");
    }
}
