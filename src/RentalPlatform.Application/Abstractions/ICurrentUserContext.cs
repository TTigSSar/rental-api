namespace RentalPlatform.Application.Abstractions;

public interface ICurrentUserContext
{
    Guid? UserId { get; }
}
