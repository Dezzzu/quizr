using System.Globalization;

namespace Quizr.App.Validation;

// Shared parse+validate helpers for the free-text replies captain flows collect — franchise
// creation/editing, game creation (both the one-off path and the confirm-screen overrides),
// and game editing. Returns a locale key on failure, never English prose — callers just do
// strings.Text(errorKey), same as everywhere else user-visible text is rendered.
internal static class FieldParsing
{
    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday,
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

    // "Mon-Fri:19:00, Sat:16:00, Sun:16:00" — comma-separated day-or-range:time pairs. An
    // absent day is one the franchise doesn't run (Franchise.Schedule's own doc comment).
    public static bool TryParseSchedule(string? input, out Dictionary<DayOfWeek, TimeOnly> value, out string? errorKey)
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
                || !TryParseDayRange(parts[0].Trim(), out var days)
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

    private static bool TryParseDayRange(string token, out List<DayOfWeek> days)
    {
        days = [];
        var range = token.Split('-', 2, StringSplitOptions.TrimEntries);

        if (range.Length == 1)
        {
            if (!DayNames.TryGetValue(range[0], out var single))
            {
                return false;
            }

            days.Add(single);
            return true;
        }

        if (!DayNames.TryGetValue(range[0], out var start) || !DayNames.TryGetValue(range[1], out var end))
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
}
