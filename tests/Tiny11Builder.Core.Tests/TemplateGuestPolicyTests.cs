using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateGuestPolicyTests
{
    // GP-08 / GP-T14: Approved policy does not force the clock or alter RTC.
    [Fact]
    public void Policy_keeps_nla_firewall_and_windows_time_management()
    {
        var policy = TemplateGuestAssets.PolicyScript;
        Assert.Contains("-Name UserAuthentication -Value 1", policy);
        Assert.Contains("Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True", policy);
        Assert.Contains("-LocalPort 3389 -RemoteAddress Any", policy);
        Assert.Contains("Set-Service W32Time -StartupType Automatic", policy);
        Assert.DoesNotContain("RealTimeIsUniversal", policy);
        Assert.DoesNotContain("w32tm.exe", policy);
        Assert.DoesNotContain("Set-Date", policy);
        Assert.DoesNotContain("10.10.", policy);
    }

    // GP-08 / GP-T14: VerifyOnly does not write policy; runtime checks remain necessary.
    [Fact]
    public void Preparation_and_finalize_verify_profile_policy_after_integrity()
    {
        Assert.Contains("param([switch]$VerifyOnly, [switch]$ActivateRdp)", TemplateGuestAssets.PolicyScript);
        Assert.Contains("if (-not $VerifyOnly)", TemplateGuestAssets.PolicyScript);
        Assert.Contains("if (-not $VerifyOnly -and $ActivateRdp)", TemplateGuestAssets.PolicyScript);
        Assert.True(TemplateGuestAssets.PrepareScript.IndexOf("Assert-TemplatePreparationContext", StringComparison.Ordinal) <
                    TemplateGuestAssets.PrepareScript.IndexOf("'Configure guest policy'", StringComparison.Ordinal));
        Assert.Contains("Configure-GuestPolicy.ps1') -VerifyOnly", TemplateGuestAssets.FinalizeScript);
    }
}
