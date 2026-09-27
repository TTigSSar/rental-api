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
    }

    private static readonly HashSet<string> AllowedPreferredLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "en", "hy", "ru" };

    private readonly IUserAuthStore _userAuthStore;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IExternalIdentityTokenValidator _externalIdentityTokenValidator;

    public AuthService(
        IUserAuthStore userAuthStore,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        ICurrentUserContext currentUserContext,
        IExternalIdentityTokenValidator externalIdentityTokenValidator)
    {
        _userAuthStore = userAuthStore;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _currentUserContext = currentUserContext;
        _externalIdentityTokenValidator = externalIdentityTokenValidator;
    }

    public async Task<ServiceResult<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var emailExists = await _userAuthStore.EmailExistsAsync(normalizedEmail, cancellationToken);
        if (emailExists)
        {
            return ServiceResult<AuthResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.DuplicateEmail,
                Message = "A user with this email already exists."
            });
        }

        // Enforced here (not a DataAnnotation) so the response carries an errorCode the Angular
        // client can map to a translated message — see PasswordPolicy. Byte length, not char
        // length: BCrypt truncates at 72 UTF-8 bytes, and a char-based cap under-counts
        // multi-byte scripts (Armenian/Russian are 2 bytes/char in UTF-8).
        if (Encoding.UTF8.GetByteCount(request.Password) > PasswordPolicy.MaxPasswordBytes)
        {
            return ServiceResult<AuthResponse>.Failure(new ServiceError
            {
                Code = ErrorCodes.PasswordTooLong,
                Message = $"Password must be at most {PasswordPolicy.MaxPasswordBytes} bytes (UTF-8)."
            });
        }

        var user = new User
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
            CreatedAt = DateTime.UtcNow,
            IsBlocked = false,
            Role = UserRole.User
        };

        await _userAuthStore.AddAsync(user, cancellationToken);
        await _userAuthStore.SaveChangesAsync(cancellationToken);

        var token = _jwtTokenService.GenerateAccessToken(user);

        return ServiceResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = token,
            User = MapUser(user)
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
        if (user is null || string.IsNullOrEmpty(user.PasswordHash) || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
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

        var token = _jwtTokenService.GenerateAccessToken(user);

        return ServiceResult<AuthResponse>.Success(new AuthResponse
        {
            AccessToken = token,
            User = MapUser(user)
        });
    }

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

            var normalizedEmail = NormalizeEmail(externalUser.Email);
            user = await _userAuthStore.FindByEmailAsync(normalizedEmail, cancellationToken);

            if (user is null)
            {
                user = new User
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
                    CreatedAt = DateTime.UtcNow,
                    IsBlocked = false,
                    Role = UserRole.User
                };

                await _userAuthStore.AddAsync(user, cancellationToken);
            }
            else
            {
                if (!CanLinkExternalIdentity(user, provider, externalUser.ProviderUserId))
                {
                    return ServiceResult<AuthResponse>.Failure(new ServiceError
                    {
                        Code = ErrorCodes.ExternalLinkConflict,
                        Message = "Existing account is already linked to another external identity."
                    });
                }

                user.ExternalAuthProvider = provider;
                user.ExternalProviderId = externalUser.ProviderUserId;
                if (!string.IsNullOrWhiteSpace(externalUser.AvatarUrl))
                {
                    user.AvatarUrl = externalUser.AvatarUrl;
                }
            }

            await _userAuthStore.SaveChangesAsync(cancellationToken);
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

        if (string.IsNullOrEmpty(user.PasswordHash))
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
        Role = user.Role
    };
}
