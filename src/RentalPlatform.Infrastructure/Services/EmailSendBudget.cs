using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalPlatform.Application.Abstractions;

namespace RentalPlatform.Infrastructure.Services;

// Process-wide cap on emails actually sent (ADR-028 section 8), fixed window anchored at the first
// send. One API instance today; a second instance would double the cap, which is still far below
// any abuse that matters.
public sealed class EmailSendBudget : IEmailSendBudget
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmailSendBudget> _logger;
    private readonly object _gate = new();

    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _count;

    public EmailSendBudget(IOptions<EmailOptions> options, TimeProvider timeProvider, ILogger<EmailSendBudget> logger)
    {
        _limit = options.Value.EffectiveSendBudgetLimit;
        _window = options.Value.EffectiveSendBudgetWindow;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool TryAcquire()
    {
        DateTimeOffset resetAt;

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_windowStart == DateTimeOffset.MinValue || now - _windowStart >= _window)
            {
                _windowStart = now;
                _count = 0;
            }

            if (_count < _limit)
            {
                _count++;
                return true;
            }

            resetAt = _windowStart + _window;
        }

        _logger.LogCritical(
            "Email send budget exhausted ({Limit} per {WindowHours} h). Verification emails are NOT being sent until {ResetAt:O}. " +
            "Check for abuse and the provider quota.",
            _limit, _window.TotalHours, resetAt);
        return false;
    }
}
