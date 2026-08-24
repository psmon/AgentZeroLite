using Agent.Common.Remote;
using Xunit;

namespace ZeroCommon.Tests.Remote;

/// <summary>
/// Contract tests for <see cref="RemoteAuthService"/> — one-time PIN lifecycle (issue, TTL,
/// single-use, lockout) and bearer-token issue/validate/revoke. Deterministic via an
/// injected <see cref="TestTimeProvider"/>; no file IO (in-memory settings + no-op save).
/// </summary>
public sealed class RemoteAuthServiceTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan d) => Now += d;
    }

    private static (RemoteAuthService svc, RemoteSettings settings, TestTimeProvider clock) NewService()
    {
        var settings = new RemoteSettings();
        var clock = new TestTimeProvider();
        var svc = new RemoteAuthService(settings, save: () => { }, clock);
        return (svc, settings, clock);
    }

    [Fact]
    public void IssuePin_returns_numeric_pin_and_sets_current()
    {
        var (svc, _, _) = NewService();
        var pin = svc.IssuePin();
        Assert.Equal(RemoteAuthService.PinDigits, pin.Pin.Length);
        Assert.All(pin.Pin, c => Assert.True(char.IsDigit(c)));
        Assert.NotNull(svc.CurrentPin);
    }

    [Fact]
    public void Correct_pin_pairs_and_issues_a_validatable_token()
    {
        var (svc, settings, _) = NewService();
        var pin = svc.IssuePin();

        var result = svc.TryPair(pin.Pin);

        Assert.Equal(PairOutcome.Success, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.Single(settings.PairedTokenHashes);
        Assert.True(svc.ValidateToken(result.Token));
        Assert.False(svc.ValidateToken("not-a-real-token"));
    }

    [Fact]
    public void Pin_is_single_use()
    {
        var (svc, _, _) = NewService();
        var pin = svc.IssuePin();

        Assert.Equal(PairOutcome.Success, svc.TryPair(pin.Pin).Outcome);
        // Same PIN again — already consumed.
        Assert.Equal(PairOutcome.NoActivePin, svc.TryPair(pin.Pin).Outcome);
    }

    [Fact]
    public void Expired_pin_is_rejected()
    {
        var (svc, _, clock) = NewService();
        var pin = svc.IssuePin();

        clock.Advance(RemoteAuthService.PinTtl + TimeSpan.FromSeconds(1));

        Assert.Equal(PairOutcome.Expired, svc.TryPair(pin.Pin).Outcome);
        Assert.Null(svc.CurrentPin);
    }

    [Fact]
    public void Repeated_wrong_pins_trip_lockout()
    {
        var (svc, _, _) = NewService();
        svc.IssuePin();

        for (int i = 0; i < RemoteAuthService.MaxFailedAttempts; i++)
            svc.TryPair("00000000-wrong");

        Assert.True(svc.IsLockedOut);
        Assert.Equal(PairOutcome.LockedOut, svc.TryPair("whatever").Outcome);
    }

    [Fact]
    public void IssuePin_clears_lockout()
    {
        var (svc, _, _) = NewService();
        svc.IssuePin();
        for (int i = 0; i < RemoteAuthService.MaxFailedAttempts; i++)
            svc.TryPair("wrong");
        Assert.True(svc.IsLockedOut);

        svc.IssuePin();
        Assert.False(svc.IsLockedOut);
    }

    [Fact]
    public void Revoke_removes_a_token()
    {
        var (svc, settings, _) = NewService();
        var pin = svc.IssuePin();
        var token = svc.TryPair(pin.Pin).Token!;
        var hash = RemoteAuthService.HashToken(token);

        Assert.True(svc.ValidateToken(token));
        Assert.True(svc.RevokeTokenHash(hash));
        Assert.False(svc.ValidateToken(token));
        Assert.Empty(settings.PairedTokenHashes);
    }

    [Fact]
    public void RevokeAll_clears_every_token()
    {
        var (svc, settings, _) = NewService();
        var t1 = svc.TryPair(svc.IssuePin().Pin).Token!;
        var t2 = svc.TryPair(svc.IssuePin().Pin).Token!;
        Assert.Equal(2, settings.PairedTokenHashes.Count);

        svc.RevokeAll();

        Assert.Empty(settings.PairedTokenHashes);
        Assert.False(svc.ValidateToken(t1));
        Assert.False(svc.ValidateToken(t2));
    }

    [Fact]
    public void Persisted_hash_is_not_the_raw_token()
    {
        var (svc, settings, _) = NewService();
        var token = svc.TryPair(svc.IssuePin().Pin).Token!;
        // The stored value is a one-way hash — the raw token must never appear at rest.
        Assert.DoesNotContain(token, settings.PairedTokenHashes);
        Assert.Equal(RemoteAuthService.HashToken(token), settings.PairedTokenHashes[0]);
    }
}
