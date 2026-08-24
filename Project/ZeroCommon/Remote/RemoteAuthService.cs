using System.Security.Cryptography;
using System.Text;

namespace Agent.Common.Remote;

/// <summary>Outcome of a <see cref="RemoteAuthService.TryPair"/> attempt.</summary>
public enum PairOutcome
{
    /// <summary>PIN matched; a fresh bearer token was issued.</summary>
    Success,
    /// <summary>A PIN was supplied but it does not match the active one.</summary>
    InvalidPin,
    /// <summary>The active PIN has expired (past its TTL).</summary>
    Expired,
    /// <summary>No PIN has been issued (or it was already consumed).</summary>
    NoActivePin,
    /// <summary>Too many failed attempts — pairing is temporarily locked.</summary>
    LockedOut,
}

/// <summary>Result of a pairing attempt. <see cref="Token"/> is non-null only on
/// <see cref="PairOutcome.Success"/> and is the ONE time the raw token is ever revealed.</summary>
public sealed record PairResult(PairOutcome Outcome, string? Token);

/// <summary>Snapshot of the active one-time PIN for UI display.</summary>
public sealed record PinInfo(string Pin, DateTimeOffset ExpiresAt);

/// <summary>
/// Auth core for the Remote feature — WPF-free and self-contained so it can be unit
/// tested headlessly. Owns:
/// <list type="bullet">
///   <item>the single active one-time PIN (crypto-random, short TTL, single-use, in-memory only),</item>
///   <item>bearer-token issuance on successful pairing (raw token returned once; only its
///     SHA-256 hash is persisted to <see cref="RemoteSettings.PairedTokenHashes"/>),</item>
///   <item>constant-time token validation and revocation,</item>
///   <item>a lockout that trips after repeated wrong PIN attempts.</item>
/// </list>
/// Persistence is delegated to the <c>save</c> callback the host supplies (writes
/// <see cref="RemoteSettingsStore"/>), so tests can pass an in-memory settings object and a
/// no-op save. All public members are safe to call from multiple listener threads.
/// </summary>
public sealed class RemoteAuthService
{
    /// <summary>Number of digits in an issued PIN.</summary>
    public const int PinDigits = 8;
    /// <summary>How long an issued PIN stays valid.</summary>
    public static readonly TimeSpan PinTtl = TimeSpan.FromMinutes(5);
    /// <summary>Consecutive wrong PIN attempts before pairing locks.</summary>
    public const int MaxFailedAttempts = 5;
    /// <summary>How long pairing stays locked once tripped.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly RemoteSettings _settings;
    private readonly Action _save;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    // Active PIN state (in-memory only — never persisted).
    private string? _pin;
    private DateTimeOffset _pinExpiresAt;

    // Lockout state.
    private int _failedAttempts;
    private DateTimeOffset _lockedUntil;

    public RemoteAuthService(RemoteSettings settings, Action save, TimeProvider? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Generate and store a new one-time PIN, replacing any previous one and clearing the
    /// lockout so the user can always mint a fresh code. Returns the PIN to show in the GUI.
    /// </summary>
    public PinInfo IssuePin()
    {
        lock (_gate)
        {
            var sb = new StringBuilder(PinDigits);
            for (int i = 0; i < PinDigits; i++)
                sb.Append((char)('0' + RandomNumberGenerator.GetInt32(0, 10)));

            _pin = sb.ToString();
            _pinExpiresAt = _clock.GetUtcNow() + PinTtl;
            _failedAttempts = 0;
            _lockedUntil = default;
            return new PinInfo(_pin, _pinExpiresAt);
        }
    }

    /// <summary>The active PIN for UI display, or null if none is issued / it has expired.</summary>
    public PinInfo? CurrentPin
    {
        get
        {
            lock (_gate)
            {
                if (_pin is null || _clock.GetUtcNow() >= _pinExpiresAt) return null;
                return new PinInfo(_pin, _pinExpiresAt);
            }
        }
    }

    /// <summary>Whether pairing is currently locked out.</summary>
    public bool IsLockedOut
    {
        get { lock (_gate) { return _clock.GetUtcNow() < _lockedUntil; } }
    }

    /// <summary>
    /// Validate a supplied PIN. On success the PIN is consumed (single-use), a 256-bit
    /// bearer token is issued, its hash persisted, and the raw token returned. On failure
    /// the attempt counter advances and, once it reaches <see cref="MaxFailedAttempts"/>,
    /// pairing locks for <see cref="LockoutDuration"/>.
    /// </summary>
    public PairResult TryPair(string? pin)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();

            if (now < _lockedUntil)
                return new PairResult(PairOutcome.LockedOut, null);

            if (_pin is null)
                return Fail(PairOutcome.NoActivePin, now);

            if (now >= _pinExpiresAt)
            {
                _pin = null;
                return Fail(PairOutcome.Expired, now);
            }

            // Constant-time compare to avoid leaking match progress via timing.
            var supplied = Encoding.ASCII.GetBytes(pin ?? "");
            var expected = Encoding.ASCII.GetBytes(_pin);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
                return Fail(PairOutcome.InvalidPin, now);

            // Success: consume the PIN, reset lockout, mint a token.
            _pin = null;
            _failedAttempts = 0;
            _lockedUntil = default;

            var token = GenerateToken();
            var hash = HashToken(token);
            if (!_settings.PairedTokenHashes.Contains(hash))
            {
                _settings.PairedTokenHashes.Add(hash);
                _save();
            }
            return new PairResult(PairOutcome.Success, token);
        }
    }

    private PairResult Fail(PairOutcome outcome, DateTimeOffset now)
    {
        _failedAttempts++;
        if (_failedAttempts >= MaxFailedAttempts)
        {
            _lockedUntil = now + LockoutDuration;
            _failedAttempts = 0;
        }
        return new PairResult(outcome, null);
    }

    /// <summary>True if <paramref name="token"/> matches a paired hash (constant-time).</summary>
    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = HashToken(token);
        var candidate = Convert.FromHexString(hash);
        lock (_gate)
        {
            foreach (var stored in _settings.PairedTokenHashes)
            {
                byte[] storedBytes;
                try { storedBytes = Convert.FromHexString(stored); }
                catch { continue; }
                if (storedBytes.Length == candidate.Length &&
                    CryptographicOperations.FixedTimeEquals(storedBytes, candidate))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Revoke a paired token by its stored hash (the UI only sees hashes).
    /// Returns true if a hash was removed.</summary>
    public bool RevokeTokenHash(string hash)
    {
        lock (_gate)
        {
            if (_settings.PairedTokenHashes.Remove(hash))
            {
                _save();
                return true;
            }
            return false;
        }
    }

    /// <summary>Revoke every paired token.</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            if (_settings.PairedTokenHashes.Count == 0) return;
            _settings.PairedTokenHashes.Clear();
            _save();
        }
    }

    /// <summary>Snapshot of currently paired token hashes (for the revoke UI).</summary>
    public IReadOnlyList<string> PairedHashes
    {
        get { lock (_gate) { return _settings.PairedTokenHashes.ToArray(); } }
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    /// <summary>SHA-256 of the token, lowercase hex — the persisted form.</summary>
    public static string HashToken(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(digest);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
