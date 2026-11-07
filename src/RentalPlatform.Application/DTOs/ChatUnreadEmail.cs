namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Everything the unread-chat email (ADR-059) needs, with links already built by the caller. Deliberately has
/// no message text: chat carries addresses and phone numbers (ADR-053).
/// </summary>
/// <param name="HasHumanMessage">True when the unread lines include a non-system message.</param>
/// <param name="SenderFirstName">First name of the human sender; null when there is none or the sender is deleted.</param>
/// <param name="SenderDeleted">True when the human sender's account is deleted (the email says "Deleted user").</param>
public sealed record ChatUnreadEmail(
    string To,
    string? PreferredLanguage,
    string ToyTitle,
    string? ToyImageUrl,
    bool HasHumanMessage,
    string? SenderFirstName,
    bool SenderDeleted,
    string OpenChatUrl,
    string UnsubscribeUrl,
    string SettingsUrl);
