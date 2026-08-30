using AwesomeAssertions;
using Quizr.App.Localization;
using Quizr.App.Rendering;
using Quizr.Domain;
using Quizr.Domain.Entities;

namespace Quizr.App.Tests;

public class FranchiseRendererTests
{
    private static readonly Strings Strings = new();

    // The schedule should render back in the same day names a captain in that locale would
    // type to set it — DayOfWeek.ToString() has no locale of its own and used to leak
    // English abbreviations into every team's franchise summary regardless of language.
    [Test]
    public void RenderSummaryUsesTheTeamsOwnDayNames()
    {
        var franchise = Franchise(
            new Dictionary<DayOfWeek, TimeOnly>
            {
                [DayOfWeek.Monday] = new TimeOnly(19, 0),
                [DayOfWeek.Saturday] = new TimeOnly(16, 0),
            }
        );

        var en = FranchiseRenderer.RenderSummary(franchise, Strings.For("en"));
        var ru = FranchiseRenderer.RenderSummary(franchise, Strings.For("ru"));
        var de = FranchiseRenderer.RenderSummary(franchise, Strings.For("de"));

        en.Should().Contain("Mon 19:00").And.Contain("Sat 16:00");
        ru.Should().Contain("Пн 19:00").And.Contain("Сб 16:00");
        de.Should().Contain("Mo 19:00").And.Contain("Sa 16:00");
    }

    [Test]
    public void RenderSummaryShowsADashForAnEmptySchedule()
    {
        var franchise = Franchise([]);

        FranchiseRenderer.RenderSummary(franchise, Strings.For("en")).Should().Contain("—");
    }

    private static Franchise Franchise(Dictionary<DayOfWeek, TimeOnly> schedule) =>
        new()
        {
            Id = new FranchiseId(1),
            TeamId = new TeamId(1),
            Name = "Квиз, плиз!",
            DefaultVenue = "The Pub",
            DefaultCapacity = 20,
            Schedule = schedule,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
