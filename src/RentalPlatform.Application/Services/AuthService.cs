using System.Text;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Application.Common;
using RentalPlatform.Application.DTOs;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Services;

public sealed class AuthService : IAuthService
{
    private static class ErrorCodes
    {
        public const string DuplicateEmail = "auth.duplicate_email";
        public const string InvalidCredentials = "auth.invalid_credentials";
        public const string UserBlocked = "auth.user_blocked";
        public const string Unauthenticated = "auth.unauthenticated";
        public const string UnsupportedProvider = "auth.external_provider_unsupported";
        public const string InvalidExternalToken = "auth.external_invalid_token";
        public const string ExternalEmailMissing = "auth.external_email_missing";
        public const string ExternalLinkConflict = "auth.external_link_conflict";
        public const string InvalidLanguage = "auth.invalid_language";
        public const string InvalidCurrentPassword = "auth.invalid_current_password";
        public const string PasswordNotSet = "auth.password_not_set";
        public const string PasswordUnchanged = "auth.password_unchanged";
        public const string PasswordTooLong = "auth.password_too_long";
        public const string EmailNotVerified = "auth.email_not_verified";
        public const string VerificationTokenInvalid = "auth.verification_token_invalid";
    }

    // ADR-028 §10. Google is trusted to vouch for an email only when it is the mailbox provider
    // itself (gmail) or the Workspace domain it administers (hd == the email's domain). Apple only
    // for the domains it issues itself: relay addresses and its own mail service.
    private static readonly HashSet<string> GoogleAutoLinkDomains =
        new(StringComparer.OrdinalIgnoreCase) { "gmail.com", "googlemail.com" };

    private static readonly HashSet<string> AppleAutoLinkDomains =
        new(StringComparer.OrdinalIgnoreCase) { "privaterelay.appleid.com", "icloud.com", "me.com", "mac.com" };

    private const int MaxExternalResolveAttempts = 3;

    private static readonly HashSet<string> AllowedPreferredLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "en", "hy", "ru" };

    private readonly IUserAuthStore _userAuthStore;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IExternalIdentityTokenValidator _externalIdentityTokenValidator;
    private readonly IHomePointService _homePointService;
    private readonly IEmailVerificationService _emailVerification;
    private readonly IEmailVerificationStore _emailVerificationStore;
    private readonly TimeProvider _timeProvider;

    public AuthService(
        IUserAuthStore userAuthStore,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        ICurrentUserContext currentUserContext,
        IExternalIdentityTokenValidator externalIdentityTokenValidator,
        IHomePointService homePointService,
        IEmailVerificationService emailVerification,
        IEmailVerificationStore emailVerificationStore,
        TimeProvider timeProvider)
    {
        _emailVerification = emailVerification;
        _emailVerificationStore = emailVerificationStore;
        _timeProvider = timeProvider;
        _userAuthStore = userAuthStore;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _currentUserContext = currentUserContext;
        _externalIdentityTokenValidator = externalIdentityTokenValidator;
        _homePointService = homePointService;
    }

    public async Task<ServiceResult<RegisterResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        // Production email gate (ADR-028 section 9): before anything is created or checked.
        if (!_emailVerification.IsAvailable)
        {
            return ServiceResult<RegisterResponse>.Failure(new ServiceError
            {
                Code = EmailVerificationService.ErrorCodes.RegistrationUnavailable,
                Message = "Registration is temporarily unavailable. Please try again later."
            });
        }

        var normalizedEmail = NormalizeEmail(request.Email);

        // Enforced here (not a DataAnnotation) so the response carries an errorCode the Angular
        // client can map to a translated message - see PasswordPolicy. Byte length, not char
        // length: BCrypt truncates at 72 UTF-8 bytes, and a char-based cap under-counts
        // multi-byte scripts (Armenian/Russian are 2 bytes/char in UTF-8).
        if (Encoding.UTF8.GetByteCount(request.Password) > PasswordPolicy.MaxPasswordBytes)
        {
            return ServiceResult<RegisterResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.PasswordTooLong,
                Message = $"Password must be at most {PasswordPolicy.MaxPasswordBytes} bytes (UTF-8)."
            });
        }

        // Checked BEFORE the account exists. The home-point step is optional but, when supplied, it
        // must be inside Yerevan - and rejecting it after the insert would leave a registered user
        // behind, so the obvious retry would then fail on a duplicate email instead of on the pin.
        if (request.HomeLatitude is not null || request.HomeLongitude is not null)
        {
            // Both-or-neither is already enforced by RegisterRequest.Validate; the null-coalesce
            // keeps this honest if a caller bypasses model validation.
            var areaCheck = _homePointService.ValidateForSave(
                request.HomeLatitude ?? 0m,
                request.HomeLongitude ?? 0m);

            if (!areaCheck.IsSuccess)
            {
                return ServiceResult<RegisterResponse>.Failure(areaCheck.Error!);
            }
        }

        var candidate = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            PhoneNumber = NormalizeOptional(request.PhoneNumber),
            PreferredLanguage = NormalizeOptional(request.PreferredLanguage),
            ExternalAuthProvider = null,
            ExternalProviderId = null,
            AvatarUrl = null,
            CreatedAt = Now(),
            IsBlocked = false,
            IsEmailConfirmed = false,
            Role = UserRole.User
        };

        // New pending registration or replacement of an unverified one, home point, then the email
        // (after the commit). Nothing is signed in: the account becomes usable only once the
        // mailbox is proven (ADR-028 section 1).
        var result = await _emailVerification.RegisterPendingAsync(
            candidate, request.HomeLatitude, request.HomeLongitude, cancellationToken);
        if (!result.IsSuccess)
        {
            return ServiceResult<RegisterResponse>.Failure(result.Error!);
        }

        return ServiceResult<RegisterResponse>.Success(new RegisterResponse
        {
            Email = normalizedEmail,
            VerificationRequired = true
        });
    }

    public async Task<ServiceResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var user = await _userAuthStore.FindByEmailAsync(normalizedEmail, cancellationToken);
        // Guard against an empty hash (external-auth users are created with
        // PasswordHash = string.Empty) before calling VerifyPassword: BCrypt.Net.BCrypt.Verify
        // throws SaltParseException on an empty hash instead of returning false, which would
        // otherwise unwind to a 500 and still burn a rate-limit permit. Treated identically to
        // a wrong password so we never reveal whether the account exists or is external-auth.
        if (user is null || !PasswordPolicy.HasUsablePassword(user.PasswordHash) || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return ServiceResult<AuthResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidCredentials,
                Message = "Invalid email or password."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<AuthResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "User account is blocked."
            });
        }

        // Only reachable with the correct password and an unblocked account, so this answer cannot
        // be used to probe which emails are registered (ADR-028 section 1).
        if (!user.IsEmailConfirmed)
        {
            return ServiceResult<AuthResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.EmailNotVerified,
                Message = "Please verify your email address before signing in."
            });
        }

        var token = _jwtTokenService.GenerateAccessToken(user);

        return ServiceResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = token,
            User = MapUser(user)
        });
    }

    public async Task<ServiceResult<AuthResponse>> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken = default)
    {
        var verified = await _emailVerification.VerifyAsync(request.Token, request.Password, cancellationToken);
        if (!verified.IsSuccess)
        {
            return ServiceResult<AuthResponse>.Failure(verified.Error!);
        }

        // The store cleared the change tracker after the commit, so this is a fresh read of the
        // verified row; the JWT and the response are built from it, never from the pre-commit snapshot.
        var user = await _userAuthStore.FindByIdAsync(verified.Value, cancellationToken);
        if (user is null)
        {
            return ServiceResult<AuthResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.VerificationTokenInvalid,
                Message = "The verification link is invalid."
            });
        }

        return ServiceResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = _jwtTokenService.GenerateAccessToken(user),
            User = MapUser(user)
        });
    }

    public Task<ServiceResult<bool>> ResendVerificationAsync(ResendVerificationRequest request, CancellationToken cancellationToken = default) =>
        _emailVerification.ResendAsync(request.Email, cancellationToken);

    public async Task<ServiceResult<AuthResponse>> ExternalAsync(ExternalAuthRequest request, CancellationToken cancellationToken = default)
    {
        var validationResult = await _externalIdentityTokenValidator.ValidateAsync(request.Provider, request.IdToken, cancellationToken);
        if (!validationResult.IsSuccess || validationResult.Value is null)
        {
            return ServiceResult<AuthResponse>.Failure(validationResult.Error ?? new ServiceError
            {
                Code = ErrorCodes.InvalidExternalToken,
                Message = "External identity token is invalid."
            });
        }

        var externalUser = validationResult.Value;
        var provider = externalUser.Provider.ToLowerInvariant();

        var user = await _userAuthStore.FindByExternalProviderAsync(provider, externalUser.ProviderUserId, cancellationToken);

        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(externalUser.Email))
            {
                return ServiceResult<AuthResponse>.Failure(new ServiceError
                {
                    Code = ErrorCodes.ExternalEmailMissing,
                    Message = "External provider did not return an email."
                });
            }

            var resolved = await ResolveExternalUserByEmailAsync(
                externalUser, provider, NormalizeEmail(externalUser.Email), cancellationToken);
            if (!resolved.IsSuccess)
            {
                return ServiceResult<AuthResponse>.Failure(resolved.Error!);
            }

            user = resolved.Value!;
        }

        if (user.IsBlocked)
        {
            return ServiceResult<AuthResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "User account is blocked."
            });
        }

        var token = _jwtTokenService.GenerateAccessToken(user);

        return ServiceResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = token,
            User = MapUser(user)
        });
    }

    public async Task<ServiceResult<CurrentUserResponse>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var user = await _userAuthStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "User account is blocked."
            });
        }

        return ServiceResult<CurrentUserResponse>.Success(MapUser(user));
    }

    public async Task<ServiceResult<CurrentUserResponse>> UpdatePreferredLanguageAsync(string? preferredLanguage, CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var user = await _userAuthStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "User account is blocked."
            });
        }

        var normalizedLanguage = NormalizeOptional(preferredLanguage);
        if (normalizedLanguage is not null && !AllowedPreferredLanguages.Contains(normalizedLanguage))
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidLanguage,
                Message = "Preferred language must be one of: en, hy, ru."
            });
        }

        user.PreferredLanguage = normalizedLanguage?.ToLowerInvariant();
        await _userAuthStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<CurrentUserResponse>.Success(MapUser(user));
    }

    public async Task<ServiceResult<bool>> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var user = await _userAuthStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        if (user.IsBlocked)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.UserBlocked,
                Message = "User account is blocked."
            });
        }

        if (!PasswordPolicy.HasUsablePassword(user.PasswordHash))
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.PasswordNotSet,
                Message = "This account signs in with an external provider and has no password."
            });
        }

        if (!_passwordHasher.VerifyPassword(currentPassword, user.PasswordHash))
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.InvalidCurrentPassword,
                Message = "Current password is incorrect."
            });
        }

        // NewPassword is being CREATED, so (unlike currentPassword above) it is subject to the
        // BCrypt 72-byte cap — see PasswordPolicy. Checked before the unchanged-password check
        // and before hashing.
        if (Encoding.UTF8.GetByteCount(newPassword) > PasswordPolicy.MaxPasswordBytes)
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.PasswordTooLong,
                Message = $"Password must be at most {PasswordPolicy.MaxPasswordBytes} bytes (UTF-8)."
            });
        }

        if (_passwordHasher.VerifyPassword(newPassword, user.PasswordHash))
        {
            return ServiceResult<bool>.Failure(new ServiceError
            {
                Code = ErrorCodes.PasswordUnchanged,
                Message = "New password must be different from the current password."
            });
        }

        user.PasswordHash = _passwordHasher.HashPassword(newPassword);
        await _userAuthStore.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Success(true);
    }

    // Both endpoints delegate the actual write to IHomePointService — the single writer for
    // User.Home*/Listing location fields (home-point model). AuthService only resolves "who is
    // asking" and re-reads the user afterwards so the response carries the loaded HomeDistrict.
    public async Task<ServiceResult<CurrentUserResponse>> UpdateHomePointAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var result = await _homePointService.SetHomePointAsync(userId, latitude, longitude, cancellationToken);
        if (!result.IsSuccess)
        {
            return ServiceResult<CurrentUserResponse>.Failure(result.Error!);
        }

        return await ReadCurrentUserAsync(userId, cancellationToken);
    }

    public async Task<ServiceResult<CurrentUserResponse>> ClearHomePointAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUserContext.UserId is not { } userId)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        var result = await _homePointService.ClearHomePointAsync(userId, cancellationToken);
        if (!result.IsSuccess)
        {
            return ServiceResult<CurrentUserResponse>.Failure(result.Error!);
        }

        return await ReadCurrentUserAsync(userId, cancellationToken);
    }

    // No account is linked to this provider identity yet: find or create one by the (provider
    // verified) email. Retries because two outcomes are races that resolve on the next lookup: a
    // concurrent insert of the same email, and a pending account that was verified or blocked
    // between our read and our conditional reset.
    private async Task<ServiceResult<User>> ResolveExternalUserByEmailAsync(
        ExternalUserInfo externalUser,
        string provider,
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxExternalResolveAttempts; attempt++)
        {
            var now = Now();
            var existing = await _userAuthStore.FindByEmailAsync(normalizedEmail, cancellationToken);

            if (existing is null)
            {
                // The provider has verified this mailbox, so the new account is verified at once.
                var created = new User
                {
                    Id = Guid.NewGuid(),
                    Email = normalizedEmail,
                    PasswordHash = string.Empty,
                    FirstName = ResolveName(externalUser.FirstName, normalizedEmail, "User"),
                    LastName = ResolveName(externalUser.LastName, normalizedEmail, string.Empty),
                    PhoneNumber = null,
                    PreferredLanguage = null,
                    ExternalAuthProvider = provider,
                    ExternalProviderId = externalUser.ProviderUserId,
                    AvatarUrl = NormalizeOptional(externalUser.AvatarUrl),
                    CreatedAt = now,
                    IsBlocked = false,
                    IsEmailConfirmed = true,
                    EmailConfirmedAt = now,
                    Role = UserRole.User
                };

                if (await _emailVerificationStore.TryAddUserAsync(created, null, cancellationToken))
                {
                    return ServiceResult<User>.Success(created);
                }

                continue; // lost the race on Users.Email: look it up again.
            }

            if (existing.IsEmailConfirmed)
            {
                if (!CanLinkExternalIdentity(existing, provider, externalUser.ProviderUserId) ||
                    !IsTrustedForAutoLink(provider, normalizedEmail, externalUser.HostedDomain))
                {
                    return ServiceResult<User>.Failure(new ServiceError
                    {
                        Code = ErrorCodes.ExternalLinkConflict,
                        Message = "This email belongs to an existing account that cannot be linked to this sign-in method."
                    });
                }

                existing.ExternalAuthProvider = provider;
                existing.ExternalProviderId = externalUser.ProviderUserId;
                if (!string.IsNullOrWhiteSpace(externalUser.AvatarUrl))
                {
                    existing.AvatarUrl = externalUser.AvatarUrl;
                }

                await _userAuthStore.SaveChangesAsync(cancellationToken);
                return ServiceResult<User>.Success(existing);
            }

            if (existing.IsBlocked)
            {
                // A blocked pending account is never reset; the caller answers auth.user_blocked.
                return ServiceResult<User>.Success(existing);
            }

            // Pending registration: the provider's proof of the mailbox replaces it completely
            // (ADR-028 section 2). A tracked SaveChanges here would write back stale pending values.
            var reset = await _emailVerificationStore.TryResetPendingForExternalAsync(
                existing.Id,
                externalUser,
                ResolveName(externalUser.FirstName, normalizedEmail, "User"),
                ResolveName(externalUser.LastName, normalizedEmail, string.Empty),
                now,
                cancellationToken);

            if (reset)
            {
                var refreshed = await _userAuthStore.FindByIdAsync(existing.Id, cancellationToken);
                if (refreshed is not null)
                {
                    return ServiceResult<User>.Success(refreshed);
                }
            }
        }

        return ServiceResult<User>.Failure(new ServiceError
        {
            Code = ErrorCodes.ExternalLinkConflict,
            Message = "Could not complete sign-in for this account. Please try again."
        });
    }

    private static bool IsTrustedForAutoLink(string provider, string normalizedEmail, string? hostedDomain)
    {
        var domain = normalizedEmail[(normalizedEmail.LastIndexOf('@') + 1)..];

        return provider switch
        {
            "google" => GoogleAutoLinkDomains.Contains(domain) ||
                        (!string.IsNullOrWhiteSpace(hostedDomain) &&
                         string.Equals(hostedDomain.Trim(), domain, StringComparison.OrdinalIgnoreCase)),
            "apple" => AppleAutoLinkDomains.Contains(domain),
            _ => false
        };
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private async Task<ServiceResult<CurrentUserResponse>> ReadCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _userAuthStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return ServiceResult<CurrentUserResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.Unauthenticated,
                Message = "Current user is not authenticated."
            });
        }

        return ServiceResult<CurrentUserResponse>.Success(MapUser(user));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool CanLinkExternalIdentity(User user, string provider, string providerUserId)
    {
        if (string.IsNullOrWhiteSpace(user.ExternalAuthProvider) && string.IsNullOrWhiteSpace(user.ExternalProviderId))
        {
            return true;
        }

        return string.Equals(user.ExternalAuthProvider, provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(user.ExternalProviderId, providerUserId, StringComparison.Ordinal);
    }

    private static string ResolveName(string? name, string normalizedEmail, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        var localPart = normalizedEmail.Split('@', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(localPart))
        {
            return localPart;
        }

        return fallback;
    }

    private static CurrentUserResponse MapUser(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        PhoneNumber = user.PhoneNumber,
        PreferredLanguage = user.PreferredLanguage,
        AvatarUrl = user.AvatarUrl,
        CreatedAt = user.CreatedAt,
        IsBlocked = user.IsBlocked,
        Role = user.Role,
        HomePoint = MapHomePoint(user)
    };

    // Self-view only (home-point model). CurrentUserResponse is the ONLY DTO carrying the exact
    // home coordinates, and it is only ever returned to the account that owns them — every other
    // surface (public profile, listing detail, map pins, search) sees the geohash-snapped public
    // pair on the listing instead, exactly as ADR-008 requires. There is a privacy test asserting
    // the exact decimals never appear anywhere else.
    private static HomePointResponse? MapHomePoint(User user)
    {
        if (user.HomeLatitude is not { } latitude || user.HomeLongitude is not { } longitude)
        {
            return null;
        }

        return new HomePointResponse
        {
            Latitude = latitude,
            Longitude = longitude,
            PublicLatitude = user.HomePublicLatitude,
            PublicLongitude = user.HomePublicLongitude,
            District = user.HomeDistrict is { } district
                ? new ListingDistrictResponse
                {
                    Id = district.Id,
                    Code = district.Code,
                    NameEn = district.NameEn,
                    NameHy = district.NameHy,
                    NameRu = district.NameRu
                }
                : null,
            UpdatedAt = user.HomePointUpdatedAt
        };
    }
}
