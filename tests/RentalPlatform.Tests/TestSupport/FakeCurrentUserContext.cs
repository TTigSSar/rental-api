using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Tests.TestSupport;

// Mutable test double for the authenticated-user context.
public sealed class FakeCurrentUserContext : ICurrentUserContext
{
    public Guid? UserId { get; set; }

    public FakeCurrentUserContext(Guid? userId = null) => UserId = userId;
}
