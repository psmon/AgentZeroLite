using Agent.Common.Music;
using Xunit;

namespace ZeroCommon.Tests;

[Trait("Category", "MusicSettings")]
public sealed class MusicSettingsStoreTests
{
    // NOTE: MusicSettingsStore.Save writes to the real %LOCALAPPDATA% file, so
    // these tests exercise the Changed event via NotifyChanged() (the same
    // Changed?.Invoke() the Save path uses) rather than clobbering the operator's
    // music-settings.json.

    [Fact]
    public void NotifyChanged_raises_the_event_for_subscribers()
    {
        int fired = 0;
        void Handler() => fired++;
        MusicSettingsStore.Changed += Handler;
        try
        {
            MusicSettingsStore.NotifyChanged();
            MusicSettingsStore.NotifyChanged();
            Assert.Equal(2, fired);
        }
        finally
        {
            MusicSettingsStore.Changed -= Handler;
        }
    }

    [Fact]
    public void Unsubscribed_handler_does_not_fire()
    {
        int fired = 0;
        void Handler() => fired++;
        MusicSettingsStore.Changed += Handler;
        MusicSettingsStore.NotifyChanged();
        MusicSettingsStore.Changed -= Handler;

        MusicSettingsStore.NotifyChanged(); // should not reach the removed handler
        Assert.Equal(1, fired);
    }

    [Fact]
    public void NotifyChanged_is_safe_with_no_subscribers()
    {
        // Null-conditional invoke must not throw when nothing is subscribed.
        var ex = Record.Exception(MusicSettingsStore.NotifyChanged);
        Assert.Null(ex);
    }
}
