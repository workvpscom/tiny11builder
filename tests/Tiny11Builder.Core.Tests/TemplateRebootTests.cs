using System.Runtime.Versioning;
using System.Text;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class TemplateRebootTests
{
    // GP-01: The auditUser contract does not depend on desktop reference ImageState.
    [Theory]
    [InlineData("IMAGE_STATE_UNDEPLOYABLE", true, true, "S-1-5-21-1-2-3-500", "AuditUser", true)]
    [InlineData("IMAGE_STATE_COMPLETE", true, true, "S-1-5-21-1-2-3-500", "AuditUser", true)]
    [InlineData("IMAGE_STATE_SPECIALIZE_RESEAL_TO_AUDIT", false, true, "S-1-5-21-1-2-3-500", "AuditUser", false)]
    [InlineData("IMAGE_STATE_SPECIALIZE_RESEAL_TO_AUDIT", true, false, "S-1-5-21-1-2-3-500", "AuditUser", false)]
    [InlineData("IMAGE_STATE_SPECIALIZE_RESEAL_TO_AUDIT", true, true, "S-1-5-21-1-2-3-1001", "AuditUser", false)]
    [InlineData("IMAGE_STATE_SPECIALIZE_RESEAL_TO_AUDIT", true, true, "S-1-5-21-1-2-3-500", "", false)]
    public async Task Preparation_context_validates_launcher_identity_and_manifest_not_image_state(
        string imageState, bool markerValid, bool manifestValid, string sid, string invocation, bool accepted)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = TemplateGuestAssets.StateScript + "\n$ErrorActionPreference='Stop'; $context=@{ImageState='" + imageState +
            "';MarkerValid=$" + markerValid.ToString().ToLowerInvariant() + ";ManifestVerified=$" + manifestValid.ToString().ToLowerInvariant() +
            ";UserSid='" + sid + "';Elevated=$true;RootMatches=$true;InvocationContext='" + invocation +
            "'};$rejected=$false;try{Assert-TemplatePreparationContext -Context $context}catch{$rejected=$true};" +
            "if($rejected -eq $" + accepted.ToString().ToLowerInvariant() + "){exit 1};exit 0";
        var result = await new ProcessRunner().RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    // GP-09: The correct SID is insufficient when root or elevation is invalid.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Preparation_context_rejects_wrong_root_or_missing_elevation(bool rootMatches, bool elevated)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = TemplateGuestAssets.StateScript + "\n$ErrorActionPreference='Stop'; $c=@{MarkerValid=$true;ManifestVerified=$true;UserSid='S-1-5-21-1-2-3-500';InvocationContext='AuditUser';RootMatches=$" +
            rootMatches.ToString().ToLowerInvariant() + ";Elevated=$" + elevated.ToString().ToLowerInvariant() +
            "};try{Assert-TemplatePreparationContext -Context $c;exit 1}catch{exit 0}";
        var result = await new ProcessRunner().RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    // GP-02/GP-05: Shared entrypoint logic preserves reboot requirements after interruption.
    [Theory]
    [InlineData("boot-a", "boot-a", true)]
    [InlineData("boot-a", "boot-b", false)]
    public async Task Reboot_request_remains_outstanding_only_in_the_original_boot(string requestedBoot, string currentBoot, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = TemplateGuestAssets.StateScript + "\n$s=[pscustomobject]@{RebootRequested=$true;BootIdentity='" + requestedBoot +
            "'}; if((Test-TemplateRebootOutstanding -State $s -CurrentBootIdentity '" + currentBoot + "') -ne $" + expected.ToString().ToLowerInvariant() + "){exit 1};exit 0";
        var result = await new ProcessRunner().RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }
}
