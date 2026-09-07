namespace Quizr.App.Calendar;

internal static class CalendarFormat
{
    // Part of the cache key and the ETag, so that a deploy which changes what the serializer
    // emits invalidates every client's cached body. Without it, fixing a bug in the output
    // would keep answering 304 against the broken version forever, because the data behind it
    // never changed. Bump it by hand whenever Render's output changes for the same input.
    public const int Version = 1;

    // UIDs must stay stable for the lifetime of an event, so this is a fixed constant and
    // deliberately not derived from QUIZR_PUBLIC_URL: moving the bot to another hostname must
    // not orphan every event already sitting in everyone's calendar.
    public const string UidDomain = "quizr.bot";

    public const string ProductId = "-//Quizr//Quizr Bot//EN";

    // Apple honours both and refetches about hourly; Google ignores them and refreshes on its
    // own schedule, somewhere between 8 and 24 hours. Emitted anyway — they cost two lines,
    // and they are the only say we get in how often anyone asks.
    public const string RefreshInterval = "PT1H";
}
