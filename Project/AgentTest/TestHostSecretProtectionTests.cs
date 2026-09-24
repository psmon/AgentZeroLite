using Agent.Common.Security;
using AgentZeroWpf.Security;

namespace AgentTest;

/// <summary>
/// The test host's half of the credential-at-rest contract. ZeroCommon cannot see the
/// DPAPI protector (it takes no Win32 dependency), so the two assertions that need it
/// live here: that this process actually installed it, and that
/// <see cref="SecretMarkers"/> recognises what it writes.
/// </summary>
public class TestHostSecretProtectionTests
{
    [Fact]
    public void The_test_process_installs_the_hosts_protector()
    {
        // Without this, tests that read the operator's own llm-settings.json run on the
        // passthrough and see "no key" for every sealed credential — which is safe since
        // the SecretProtection fix, but means a configured provider looks unconfigured.
        Assert.IsType<DpapiSettingsProtector>(SecretProtection.Protector);
    }

    [Fact]
    public void The_dpapi_protectors_output_is_recognised_as_sealed()
    {
        // Keeps SecretMarkers.Dpapi in step with the literal inside the protector. If they
        // drift, SecretProtection.Unprotect stops recognising DPAPI tokens and a sealed
        // key can again be passed off as the secret — the failure this pair exists to
        // prevent. (AesGcm is asserted the same way from ZeroCommon.Tests.)
        var sealedValue = new DpapiSettingsProtector().Protect("sk-secret");
        Assert.True(SecretMarkers.LooksSealed(sealedValue),
            "DpapiSettingsProtector emits a marker SecretMarkers does not list.");
    }

    [Fact]
    public void A_key_this_machine_sealed_round_trips()
    {
        // Proves the installed protector is usable here, not merely present: DPAPI binds
        // to the current user profile, so this is the check that the settings the GUI
        // wrote on this account can be read back by a test.
        var protector = new DpapiSettingsProtector();
        var sealedValue = protector.Protect("sk-round-trip");
        Assert.NotEqual("sk-round-trip", sealedValue);
        Assert.Equal("sk-round-trip", SecretProtection.Unprotect(sealedValue));
    }
}
