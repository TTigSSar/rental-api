namespace RentalPlatform.Application.Common;

// BCrypt (BCrypt.Net-Next, the hasher used by BcryptPasswordHasher) silently truncates its
// input to 72 bytes: both hashing and Verify only look at the first 72 bytes of the UTF-8
// encoded password. A password longer than that is only partly protective — everything past
// byte 72 is ignored — and, worse, two different passwords that happen to share the same first
// 72 bytes hash identically and verify as equal to one another.
//
// This cap must be enforced wherever a password is CREATED (register's Password, change-
// password's NewPassword) so a password's protection never silently degrades past this length.
// It must NOT be enforced where a password is SUBMITTED FOR VERIFICATION (login's Password,
// change-password's CurrentPassword) — someone who already holds a password longer than this
// (e.g. set before this cap existed) must still be able to prove they know it. See AuthService
// for where each side is checked. Keep this at 72 — do not "tidy" it to match the DTOs'
// [MaxLength(128)], which is only the cheap outer guard.
public static class PasswordPolicy
{
    public const int MaxPasswordBytes = 72;
}
