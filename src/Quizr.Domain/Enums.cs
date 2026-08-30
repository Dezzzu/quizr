namespace Quizr.Domain;

public enum ReminderChannel
{
    Off,
    Group,
    Dm,
}

public enum ParticipationKind
{
    Member,
    Guest,
    TeamGuest,
    VenueAssigned,
}

public enum NotificationKind
{
    ReservePromotion,
    ReminderEveningBefore,
    ReminderMorningOf,
    ReminderBeforeStart,
}
