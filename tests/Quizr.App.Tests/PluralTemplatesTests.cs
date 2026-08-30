using AwesomeAssertions;
using Quizr.App.Localization;

namespace Quizr.App.Tests;

// CLAUDE.md: "Snapshot-test plural templates at 1, 2, 5, 21 and 111." Those five numbers hit
// every boundary Russian's three-form plural rule has for integers — 1 and 21 both land on
// "one" (nominative singular), 2 lands on "few" (2-4), 5 and 111 both land on "many" (0, 5-20,
// 25-30, and the 11-14 exception that catches a naively "ends in 1" rule). SmartFormat's
// plural forms are positional, so a wrong form order in a locale file is otherwise silent —
// this is the check that would have caught it.
public class PluralTemplatesTests
{
    private readonly Strings _strings = new();

    public static TheoryData<string, string, int, string> CapacityCases =>
        new()
        {
            { "en", "Franchise.Capacity", 1, "👥 Capacity: 1 player" },
            { "en", "Franchise.Capacity", 2, "👥 Capacity: 2 players" },
            { "en", "Franchise.Capacity", 5, "👥 Capacity: 5 players" },
            { "en", "Franchise.Capacity", 21, "👥 Capacity: 21 players" },
            { "en", "Franchise.Capacity", 111, "👥 Capacity: 111 players" },
            { "ru", "Franchise.Capacity", 1, "👥 Вместимость: 1 игрок" },
            { "ru", "Franchise.Capacity", 2, "👥 Вместимость: 2 игрока" },
            { "ru", "Franchise.Capacity", 5, "👥 Вместимость: 5 игроков" },
            { "ru", "Franchise.Capacity", 21, "👥 Вместимость: 21 игрок" },
            { "ru", "Franchise.Capacity", 111, "👥 Вместимость: 111 игроков" },
            { "de", "Franchise.Capacity", 1, "👥 Kapazität: 1 Spieler" },
            { "de", "Franchise.Capacity", 2, "👥 Kapazität: 2 Spieler" },
            { "de", "Franchise.Capacity", 5, "👥 Kapazität: 5 Spieler" },
            { "de", "Franchise.Capacity", 21, "👥 Kapazität: 21 Spieler" },
            { "de", "Franchise.Capacity", 111, "👥 Kapazität: 111 Spieler" },
            { "en", "NewGame.Capacity", 1, "👥 Capacity: 1 player" },
            { "en", "NewGame.Capacity", 2, "👥 Capacity: 2 players" },
            { "en", "NewGame.Capacity", 5, "👥 Capacity: 5 players" },
            { "en", "NewGame.Capacity", 21, "👥 Capacity: 21 players" },
            { "en", "NewGame.Capacity", 111, "👥 Capacity: 111 players" },
            { "ru", "NewGame.Capacity", 1, "👥 Вместимость: 1 игрок" },
            { "ru", "NewGame.Capacity", 2, "👥 Вместимость: 2 игрока" },
            { "ru", "NewGame.Capacity", 5, "👥 Вместимость: 5 игроков" },
            { "ru", "NewGame.Capacity", 21, "👥 Вместимость: 21 игрок" },
            { "ru", "NewGame.Capacity", 111, "👥 Вместимость: 111 игроков" },
            { "de", "NewGame.Capacity", 1, "👥 Kapazität: 1 Spieler" },
            { "de", "NewGame.Capacity", 2, "👥 Kapazität: 2 Spieler" },
            { "de", "NewGame.Capacity", 5, "👥 Kapazität: 5 Spieler" },
            { "de", "NewGame.Capacity", 21, "👥 Kapazität: 21 Spieler" },
            { "de", "NewGame.Capacity", 111, "👥 Kapazität: 111 Spieler" },
        };

    [Theory]
    [MemberData(nameof(CapacityCases))]
    public void CapacityRendersTheCorrectPluralFormAtEveryBoundary(
        string locale,
        string key,
        int capacity,
        string expected
    )
    {
        var text = _strings.For(locale).Text(key, new { Capacity = capacity });

        text.Should().Be(expected);
    }
}
