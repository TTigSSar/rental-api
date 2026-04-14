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
    }

    private readonly IUserAuthStore _userAuthStore;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ICurrentUserContext _currentUserContext;

    public AuthService(
        IUserAuthStore userAuthStore,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        ICurrentUserContext currentUserContext)
    {
        _userAuthStore = userAuthStore;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _currentUserContext = currentUserContext;
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

    private static CurrentUserResponse MapUser(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        PhoneNumber = user.PhoneNumber,
        PreferredLanguage = user.PreferredLanguage,
        CreatedAt = user.CreatedAt,
        IsBlocked = user.IsBlocked,
        Role = user.Role
    };
}
