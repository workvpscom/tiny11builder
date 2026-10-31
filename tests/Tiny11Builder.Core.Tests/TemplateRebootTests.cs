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

    // AR-1/AR-2: MSI success does not suppress OS reboot requests, and repeated boots are bounded.
    [Fact]
    public async Task Pending_reboot_requests_are_bounded_by_boot_identity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "tiny11-reboot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = TemplateGuestAssets.StateScript + "\n$ErrorActionPreference='Stop';$p='" + root.Replace("'", "''") +
                "\\reboot.json';" +
                "if(Request-TemplatePendingReboot -Path $p -ManifestSha256 'manifest-a' -BootIdentity 'boot-a' -Pending $false){throw 'Unexpected request'};" +
                "if(Test-Path $p){throw 'Clean boot created journal'};" +
                "foreach($b in @('boot-a','boot-a','boot-b')){if(-not(Request-TemplatePendingReboot -Path $p -ManifestSha256 'manifest-a' -BootIdentity $b -Pending $true)){throw 'Request lost'}};" +
                "$j=Get-Content $p -Raw|ConvertFrom-Json;if($j.Attempts -ne 2){throw 'Same boot consumed attempt'};" +
                "$rejected=$false;try{Request-TemplatePendingReboot -Path $p -ManifestSha256 'manifest-a' -BootIdentity 'boot-c' -Pending $true}catch{$rejected=$true};if(-not $rejected){throw 'Unbounded reboot'};" +
                "if(Request-TemplatePendingReboot -Path $p -ManifestSha256 'manifest-a' -BootIdentity 'boot-c' -Pending $false){throw 'Cleared pending state blocked resume'};exit 0";
            var result = await RunStateScriptAsync(script);
            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // AR-3: Reboot state belongs to the verified guest manifest.
    [Fact]
    public async Task Reboot_journal_rejects_another_manifest()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "tiny11-reboot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = TemplateGuestAssets.StateScript + "\n$ErrorActionPreference='Stop';$p='" + root.Replace("'", "''") +
                "\\reboot.json';$null=Request-TemplatePendingReboot -Path $p -ManifestSha256 'manifest-a' -BootIdentity 'boot-a' -Pending $true;" +
                "$rejected=$false;try{Request-TemplatePendingReboot -Path $p -ManifestSha256 'manifest-b' -BootIdentity 'boot-b' -Pending $true}catch{$rejected=$true};if(-not $rejected){throw 'Foreign journal accepted'};exit 0";
            var result = await RunStateScriptAsync(script);
            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // AR-4: Only actual pending OS work requests a reboot; an empty rename value is harmless.
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public async Task Pending_reboot_reads_Windows_flags(bool cbs, bool update, bool rename, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = TemplateGuestAssets.StateScript + "\n$ErrorActionPreference='Stop';" +
            "function Test-Path {param($Path) if($Path -like '*Servicing*'){return $" + cbs.ToString().ToLowerInvariant() + "};return $" + update.ToString().ToLowerInvariant() + "};" +
            "function Get-ItemProperty {param($Path,$Name,$ErrorAction) [pscustomobject]@{PendingFileRenameOperations=@(" +
            (rename ? "'\\??\\C:\\old.exe',''" : "'',''") + ")}};" +
            "if((Test-TemplateWindowsRebootPending) -ne $" + expected.ToString().ToLowerInvariant() + "){throw 'Wrong pending classification'};exit 0";
        var result = await RunStateScriptAsync(script);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static Task<ProcessResult> RunStateScriptAsync(string script) => new ProcessRunner().RunAsync(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
        null, CancellationToken.None);

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
