namespace RentalPlatform.Application.Common;

// Error codes shared by every entry point that writes a home point (AuthService.RegisterAsync,
// HomePointService.SetHomePointAsync/ClearHomePointAsync). They live here rather than as private
// constants on one service because two services raise them and the client matches on the exact
// string — one spelling, one place.
public static class HomePointErrorCodes
{
    /// <summary>
    /// The point does not fall inside any of the 12 Yerevan districts. DoRent operates in Yerevan
    /// only, so this is a hard refusal for every user, owner or renter — not a warning, and not
    /// something a listing-less account is exempt from.
    /// </summary>
    /// <remarks>
    /// A single stable token on purpose: the client renders it as a blocking inline error AND a
    /// popup, so splitting it into "outside Armenia" / "outside Yerevan" variants would double the
    /// copy for a distinction the product does not make.
    ///
    /// Existing rows are NOT re-validated against this rule (M-038). A user whose home point was
    /// derived by the AddUserHomePoint migration from a listing outside Yerevan keeps that point and
    /// keeps working — editing, archiving and restoring their listings all still succeed. Only a NEW
    /// write has to satisfy it.
    /// </remarks>
    public const string OutsideYerevan = "auth.home_point_outside_yerevan";

    public const string InUse = "auth.home_point_in_use";
}
