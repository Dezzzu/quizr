using System.Globalization;

namespace Quizr.App.Validation;

// Shared parse+validate helpers for the free-text replies captain flows collect — franchise
// creation/editing, game creation (both the one-off path and the confirm-screen overrides),
// and game editing. Returns a locale key on failure, never English prose — callers just do
// strings.Text(errorKey), same as everywhere else user-visible text is rendered.
internal static class FieldParsing
{
    private static readonly Dictionary<string, DayOfWeek> EnglishDayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, DayOfWeek> RussianDayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Пн"] = DayOfWeek.Monday,
        ["Вт"] = DayOfWeek.Tuesday,
        ["Ср"] = DayOfWeek.Wednesday,
        ["Чт"] = DayOfWeek.Thursday,
        ["Пт"] = DayOfWeek.Friday,
        ["Сб"] = DayOfWeek.Saturday,
        ["Вс"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, DayOfWeek> GermanDayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mo"] = DayOfWeek.Monday,
        ["Di"] = DayOfWeek.Tuesday,
        ["Mi"] = DayOfWeek.Wednesday,
        ["Do"] = DayOfWeek.Thursday,
        ["Fr"] = DayOfWeek.Friday,
        ["Sa"] = DayOfWeek.Saturday,
        ["So"] = DayOfWeek.Sunday,
    };

    // English always works, regardless of the team's language — a captain who's used to
    // typing "Mon-Fri" shouldn't have that break the day they switch the group to Russian.
    private static readonly Dictionary<string, Dictionary<string, DayOfWeek>> DayNamesByLocale = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["en"] = EnglishDayNames,
        ["ru"] = RussianDayNames,
        ["de"] = GermanDayNames,
    };

    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday,
    ];

    public static bool TryParseText(string? input, out string value, out string? errorKey)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            value = "";
            errorKey = "Validation.Empty";
            return false;
        }

        value = trimmed;
        errorKey = null;
        return true;
    }

    // Unlike TryParseText, an empty reply is valid here — it clears the field (Notes on a
    // game, e.g.) rather than being rejected.
    public static bool TryParseOptionalText(string? input, out string? value, out string? errorKey)
    {
        var trimmed = input?.Trim();
        value = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        errorKey = null;
        return true;
    }

    // "music, detective theme" -> two tags. Always succeeds, like TryParseOptionalText — an
    // empty reply clears the tags rather than being rejected.
    public static bool TryParseTags(string? input, out List<string> value, out string? errorKey)
    {
        var trimmed = input?.Trim();
        value = string.IsNullOrEmpty(trimmed)
            ? []
            : trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        errorKey = null;
        return true;
    }

    public static bool TryParseCapacity(string? input, out int value, out string? errorKey)
    {
        if (!int.TryParse(input?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            value = 0;
            errorKey = "Validation.CapacityInvalid";
            return false;
        }

        errorKey = null;
        return true;
    }

    // "skip" (case-insensitive), or an empty reply, means no price — CLAUDE.md: price is a
    // display field, never tracked.
    public static bool TryParsePrice(string? input, out decimal? value, out string? errorKey)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            errorKey = null;
            return true;
        }

        if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            value = null;
            errorKey = "Validation.PriceInvalid";
            return false;
        }

        value = parsed;
        errorKey = null;
        return true;
    }

    public static bool TryParseDate(string? input, out DateOnly value, out string? errorKey)
    {
        if (
            !DateOnly.TryParseExact(
                input?.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value
            )
        )
        {
            errorKey = "Validation.DateInvalid";
            return false;
        }

        errorKey = null;
        return true;
    }

    public static bool TryParseTime(string? input, out TimeOnly value, out string? errorKey)
    {
        if (
            !TimeOnly.TryParseExact(
                input?.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value
            )
        )
        {
            errorKey = "Validation.TimeInvalid";
            return false;
        }

        errorKey = null;
        return true;
    }

    // Reuses HH:mm, but as a duration rather than a clock time — "02:00" means two hours,
    // e.g. how long before kickoff the "starting soon" reminder fires.
    public static bool TryParseDuration(string? input, out TimeSpan value, out string? errorKey)
    {
        if (
            !TimeOnly.TryParseExact(
                input?.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time
            )
        )
        {
            value = default;
            errorKey = "Validation.TimeInvalid";
            return false;
        }

        value = time.ToTimeSpan();
        errorKey = null;
        return true;
    }

    // "Mon-Fri:19:00, Sat:16:00, Sun:16:00" — comma-separated day-or-range:time pairs. An
    // absent day is one the franchise doesn't run (Franchise.Schedule's own doc comment). Day
    // names are read in the team's own language first, falling back to English.
    public static bool TryParseSchedule(
        string? input,
        string locale,
        out Dictionary<DayOfWeek, TimeOnly> value,
        out string? errorKey
    )
    {
        value = [];
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errorKey = "Validation.ScheduleInvalid";
            return false;
        }

        foreach (
            var entry in trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        )
        {
            var parts = entry.Split(':', 2);
            if (
                parts.Length != 2
                || !TimeOnly.TryParseExact(
                    parts[1].Trim(),
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time
                )
                || !TryParseDayRange(parts[0].Trim(), locale, out var days)
            )
            {
                value = [];
                errorKey = "Validation.ScheduleInvalid";
                return false;
            }

            foreach (var day in days)
            {
                value[day] = time;
            }
        }

        if (value.Count == 0)
        {
            value = [];
            errorKey = "Validation.ScheduleInvalid";
            return false;
        }

        errorKey = null;
        return true;
    }

    private static bool TryParseDayRange(string token, string locale, out List<DayOfWeek> days)
    {
        days = [];
        var range = token.Split('-', 2, StringSplitOptions.TrimEntries);

        if (range.Length == 1)
        {
            if (!TryResolveDayName(range[0], locale, out var single))
            {
                return false;
            }

            days.Add(single);
            return true;
        }

        if (!TryResolveDayName(range[0], locale, out var start) || !TryResolveDayName(range[1], locale, out var end))
        {
            return false;
        }

        var index = Array.IndexOf(WeekOrder, start);
        var endIndex = Array.IndexOf(WeekOrder, end);
        while (true)
        {
            days.Add(WeekOrder[index]);
            if (index == endIndex)
            {
                return true;
            }

            index = (index + 1) % WeekOrder.Length;
        }
    }

    private static bool TryResolveDayName(string token, string locale, out DayOfWeek day)
    {
        var localeNames = DayNamesByLocale.TryGetValue(locale, out var found) ? found : EnglishDayNames;

        return localeNames.TryGetValue(token, out day) || EnglishDayNames.TryGetValue(token, out day);
    }
}
