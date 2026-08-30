using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Quizr.App.Data;

// A franchise's Schedule is a jsonb column, not its own table — see PLAN.md.
// ComplexProperty()/.ToJson() doesn't fit here: it maps a type's own
// reflectable properties, and a Dictionary<,> has none as far as EF is
// concerned ("has no properties defined"). A ValueConverter serializing the
// whole dictionary is the mechanism that actually works for an open map.
// The comparer is needed alongside it because EF can't tell a mutated
// Dictionary from an untouched one without one to compare by value.
internal static class ScheduleConversion
{
    public static readonly ValueConverter<Dictionary<DayOfWeek, TimeOnly>, string> Converter = new(
        schedule => JsonSerializer.Serialize(schedule, JsonSerializerOptions.Web),
        json => JsonSerializer.Deserialize<Dictionary<DayOfWeek, TimeOnly>>(json, JsonSerializerOptions.Web)!
    );

    public static readonly ValueComparer<Dictionary<DayOfWeek, TimeOnly>> Comparer = new(
        (a, b) => AreEqual(a, b),
        schedule => schedule.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key, kv.Value)),
        schedule => new Dictionary<DayOfWeek, TimeOnly>(schedule)
    );

    // A regular method, not inlined into the comparer above: the equality
    // parameter there is an expression tree, and TryGetValue's `out var`
    // can't be represented in one. Compares Key/Value directly rather than
    // via SequenceEqual, too — KeyValuePair has no IEquatable<T>, so comparing
    // the pairs themselves would fall back to boxed, reflection-based equality.
    private static bool AreEqual(Dictionary<DayOfWeek, TimeOnly>? a, Dictionary<DayOfWeek, TimeOnly>? b)
    {
        var left = a ?? [];
        var right = b ?? [];

        return left.Count == right.Count
            && left.All(kv => right.TryGetValue(kv.Key, out var value) && value == kv.Value);
    }
}
