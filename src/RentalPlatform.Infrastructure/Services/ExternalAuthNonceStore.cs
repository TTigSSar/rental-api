using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace RentalPlatform.Infrastructure.Services;

public sealed record IssuedNonce(string Nonce, DateTimeOffset ExpiresAt);

/// <summary>
/// Single-use server-issued nonces for Google sign-in (ADR-030 section 2). The key is the hex
/// SHA-256 of the nonce (the raw value is never stored), the value its expiry. Consuming is an
/// atomic TryRemove, so of any number of concurrent replays exactly one wins. When the store is full
/// issuance is refused: a live entry is never evicted, so an attacker cannot push real users'
/// nonces out. The size is tracked by our own counter because ConcurrentDictionary.Count takes
/// every lock.
/// </summary>
public sealed class ExternalAuthNonceStore
{
    public const int DefaultCapacity = 50_000;
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int MaxNonceLength = 64;
    private static readonly long FullLogIntervalTicks = TimeSpan.FromMinutes(1).Ticks;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);
    private readonly ILogger<ExternalAuthNonceStore> _logger;
    private readonly int _capacity;
    private int _count;
    private long _lastFullLogTicks;

    public ExternalAuthNonceStore(ILogger<ExternalAuthNonceStore> logger)
        : this(logger, DefaultCapacity)
    {
    }

    internal ExternalAuthNonceStore(ILogger<ExternalAuthNonceStore> logger, int capacity)
    {
        _logger = logger;
        _capacity = capacity;
    }

    public int Count => Volatile.Read(ref _count);

    /// <summary>A fresh nonce valid for <see cref="Ttl"/>, or null when the store is full.</summary>
    public IssuedNonce? TryIssue(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            LogFull(now);
            return null;
        }

        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now + Ttl;

        if (!_entries.TryAdd(Hash(nonce), expiresAt))
        {
            // 256 random bits colliding is not a thing; give the slot back rather than leak it.
            Interlocked.Decrement(ref _count);
            return null;
        }

        return new IssuedNonce(nonce, expiresAt);
    }

    /// <summary>True exactly once for an issued, unexpired nonce.</summary>
    public bool TryConsume(string? nonce, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > MaxNonceLength)
        {
            return false;
        }

        if (!_entries.TryRemove(Hash(nonce), out var expiresAt))
        {
            return false;
        }

        Interlocked.Decrement(ref _count);
        return expiresAt > now;
    }

    /// <summary>Removes expired entries; returns how many.</summary>
    public int SweepExpired(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var entry in _entries)
        {
            // Compare-and-remove on the pair: a concurrent consume that already took the key wins
            // and this removal is then a no-op, so the counter is decremented exactly once per entry.
            if (entry.Value <= now &&
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)_entries).Remove(entry))
            {
                Interlocked.Decrement(ref _count);
                removed++;
            }
        }

        return removed;
    }

    private void LogFull(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastFullLogTicks);
        if (last != 0 && now.UtcTicks - last < FullLogIntervalTicks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastFullLogTicks, now.UtcTicks, last) == last)
        {
            _logger.LogCritical(
                "External auth nonce store is full ({Capacity} live entries); POST /api/auth/external/nonce answers 503 until entries expire.",
                _capacity);
        }
    }

    private static string Hash(string nonce) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
}
