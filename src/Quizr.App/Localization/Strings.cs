using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using SmartFormat;

namespace Quizr.App.Localization;

// A single operation routinely renders in two locales — the group's and a promoted
// player's own — so locale is a parameter, never ambient. See CLAUDE.md.
public interface IStrings
{
    // CLAUDE.md fixes this exact shape ("IStrings.For(locale) returns an IStringsFor") —
    // CA1716 flags "For" as a reserved word in some CLS languages, which doesn't apply here.
    [SuppressMessage("Design", "CA1716:Identifiers should not match keywords")]
    IStringsFor For(string locale);
}

public interface IStringsFor
{
    string Locale { get; }
    string Text(string key);
    string Text(string key, object args);
}

// Loads every embedded Localization/Strings/*.json once. A locale with no file of its
// own (every locale but English until M8) falls back to English rather than throwing,
// so LocaleResolver can already resolve to "ru"/"de" before those files exist.
public sealed class Strings : IStrings
{
    private const string FallbackLocale = "en";
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _byLocale;

    public Strings() => _byLocale = LoadAll();

    public IStringsFor For(string locale)
    {
        var templates = _byLocale.TryGetValue(locale, out var found) ? found : _byLocale[FallbackLocale];
        return new StringsFor(locale, templates);
    }

    // Internal rather than private so StringsTests can check key parity across every loaded
    // locale directly, instead of guessing at the file layout from outside.
    internal static Dictionary<string, IReadOnlyDictionary<string, string>> LoadAll()
    {
        var assembly = typeof(Strings).Assembly;
        var byLocale = new Dictionary<string, IReadOnlyDictionary<string, string>>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            // "Quizr.App.Localization.Strings.en.json" -> "en", regardless of root namespace.
            var locale = resourceName.Split('.')[^2];

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            byLocale[locale] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }

        return byLocale;
    }
}

internal sealed class StringsFor : IStringsFor
{
    private readonly IReadOnlyDictionary<string, string> _templates;

    public StringsFor(string locale, IReadOnlyDictionary<string, string> templates)
    {
        Locale = locale;
        _templates = templates;
    }

    public string Locale { get; }

    public string Text(string key) => Format(key, []);

    public string Text(string key, object args) => Format(key, [args]);

    private string Format(string key, object?[] args)
    {
        if (!_templates.TryGetValue(key, out var template))
        {
            throw new KeyNotFoundException($"No localization template for key '{key}'.");
        }

        return Smart.Default.Format(CultureInfo.GetCultureInfo(Locale), template, args);
    }
}
