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
    }

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
        if (user is null || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
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
