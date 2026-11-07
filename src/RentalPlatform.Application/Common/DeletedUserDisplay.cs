using RentalPlatform.Domain.Entities;

namespace RentalPlatform.Application.Common;

// ADR-056: a deleted user (Users.DeletedAt != null) shows up as a counterparty / author / participant with
// isDeleted = true, an empty name and no avatar. The deletion already erases those columns; these helpers
// make the projection hold even for a row whose columns were not scrubbed, so a response never depends on
// the erase having run. The UI localizes "Deleted user" itself.
public static class DeletedUserDisplay
{
    public static bool IsDeleted(User? user) => user?.DeletedAt is not null;

    public static string FirstName(User? user) => user is null || IsDeleted(user) ? string.Empty : user.FirstName;

    public static string LastName(User? user) => user is null || IsDeleted(user) ? string.Empty : user.LastName;

    public static string? AvatarUrl(User? user) => user is null || IsDeleted(user) ? null : user.AvatarUrl;

    // The email of a deleted row is the synthetic deleted-{id}@deleted.invalid; it is not shown to anyone.
    public static string Email(User? user) => user is null || IsDeleted(user) ? string.Empty : user.Email;

    public static string? PhoneNumber(User? user) => user is null || IsDeleted(user) ? null : user.PhoneNumber;

    public static string FullName(User? user) => $"{FirstName(user)} {LastName(user)}".Trim();
}
