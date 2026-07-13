using Microsoft.Extensions.Logging;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Domain.Entities;
using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Infrastructure.DependencyInjection.AdminBootstrap;

/// <summary>
/// Idempotently creates the first Admin user on startup in environments where the
/// (Development-only) demo seed does not run, so a brand-new Production database is
/// never left with zero admin accounts. Driven entirely by configuration:
///
///   Bootstrap:AdminEmail    — the admin's login email
///   Bootstrap:AdminPassword — the admin's initial password (BCrypt-hashed before storage)
///
/// Both must be non-empty or the run is a silent no-op (e.g. staging, or an operator who
/// deliberately left them unset because the admin account already exists). If a user with
/// that email already exists, nothing changes — this is safe to run on every startup.
/// </summary>
internal sealed class AdminBootstrapRunner
{
    private readonly IUserAuthStore _userAuthStore;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<AdminBootstrapRunner> _logger;

    public AdminBootstrapRunner(
        IUserAuthStore userAuthStore,
        IPasswordHasher passwordHasher,
        ILogger<AdminBootstrapRunner> logger)
    {
        _userAuthStore = userAuthStore;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task RunAsync(string? adminEmail, string? adminPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            // Not configured — nothing to do. No log line by design: this is the expected,
            // silent steady state once the admin account has been created (operators are
            // expected to remove the values from .env after first boot).
            return;
        }

        var normalizedEmail = NormalizeEmail(adminEmail);

        if (await _userAuthStore.EmailExistsAsync(normalizedEmail, cancellationToken))
        {
            _logger.LogInformation("Admin bootstrap: user {Email} already exists, skipping.", normalizedEmail);
            return;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            // Hashed the same way every other account's password is hashed (see AuthService.RegisterAsync) —
            // both go through IPasswordHasher, backed by BCrypt.Net. Never log the raw password.
            PasswordHash = _passwordHasher.HashPassword(adminPassword),
            FirstName = "Admin",
            LastName = "Admin",
            PhoneNumber = null,
            PreferredLanguage = null,
            ExternalAuthProvider = null,
            ExternalProviderId = null,
            AvatarUrl = null,
            CreatedAt = DateTime.UtcNow,
            IsBlocked = false,
            Role = UserRole.Admin
        };

        await _userAuthStore.AddAsync(user, cancellationToken);
        await _userAuthStore.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Admin bootstrap: created admin user {Email}.", normalizedEmail);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
