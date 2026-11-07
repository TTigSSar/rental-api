namespace RentalPlatform.Application.DTOs;

public sealed class ChatEmailUnsubscribeResponse
{
    public const string Unsubscribed = "unsubscribed";
    public const string AlreadyOff = "alreadyOff";

    /// <summary>"unsubscribed" or "alreadyOff".</summary>
    public string Result { get; init; } = Unsubscribed;
}
