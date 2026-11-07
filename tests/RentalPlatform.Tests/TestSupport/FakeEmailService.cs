using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// Records the notifications the moderation flow fires so tests can assert on them.
public sealed class FakeEmailService : IEmailService
{
    public List<(string Email, string Name, string Title)> ApprovedSent { get; } = new();
    public List<(string Email, string Name, string Title, string Reason)> RejectedSent { get; } = new();
    public List<(string Email, string? Language, string Link)> VerificationSent { get; } = new();

    public Task SendListingApprovedAsync(
        string ownerEmail, string ownerName, string listingTitle, CancellationToken cancellationToken = default)
    {
        ApprovedSent.Add((ownerEmail, ownerName, listingTitle));
        return Task.CompletedTask;
    }

    public Task SendListingRejectedAsync(
        string ownerEmail, string ownerName, string listingTitle, string rejectionReason, CancellationToken cancellationToken = default)
    {
        RejectedSent.Add((ownerEmail, ownerName, listingTitle, rejectionReason));
        return Task.CompletedTask;
    }

    public Task SendEmailVerificationAsync(
        string email, string? preferredLanguage, string link, CancellationToken cancellationToken = default)
    {
        VerificationSent.Add((email, preferredLanguage, link));
        return Task.CompletedTask;
    }

    public List<(string Email, string? Language, string Link)> PasswordResetSent { get; } = new();

    public Task SendPasswordResetAsync(
        string email, string? preferredLanguage, string link, CancellationToken cancellationToken = default)
    {
        PasswordResetSent.Add((email, preferredLanguage, link));
        return Task.CompletedTask;
    }

    public List<RentalPlatform.Application.DTOs.ChatUnreadEmail> ChatUnreadSent { get; } = new();

    public Task SendChatUnreadAsync(
        RentalPlatform.Application.DTOs.ChatUnreadEmail email, CancellationToken cancellationToken = default)
    {
        ChatUnreadSent.Add(email);
        return Task.CompletedTask;
    }

    public List<(string Email, string? Language)> AccountDeletedSent { get; } = new();

    // Set to prove a throwing implementation cannot fail a committed deletion (the real one never throws).
    public bool ThrowOnAccountDeleted { get; set; }

    public Task SendAccountDeletedAsync(
        string email, string? preferredLanguage, CancellationToken cancellationToken = default)
    {
        AccountDeletedSent.Add((email, preferredLanguage));
        if (ThrowOnAccountDeleted)
        {
            throw new InvalidOperationException("Simulated email failure.");
        }

        return Task.CompletedTask;
    }
}
