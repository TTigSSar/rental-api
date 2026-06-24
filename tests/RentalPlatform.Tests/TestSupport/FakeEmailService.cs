using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// Records the notifications the moderation flow fires so tests can assert on them.
public sealed class FakeEmailService : IEmailService
{
    public List<(string Email, string Name, string Title)> ApprovedSent { get; } = new();
    public List<(string Email, string Name, string Title, string Reason)> RejectedSent { get; } = new();

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
}
