using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateEntrypointTests
{
    // GP-01 / GP-T09: Run the entrypoint in a sandbox with all OS mutations stubbed.
    [Fact]
    public async Task Finalizer_rejects_tampered_helper_before_execution()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "tiny11-entrypoint-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(sandbox, "Tiny11Template");
        Directory.CreateDirectory(payload);
        try
        {
            var helper = Path.Combine(payload, "Template-State.ps1");
            await File.WriteAllTextAsync(helper, TemplateGuestAssets.StateScript, new UTF8Encoding(true));
            var manifest = JsonSerializer.Serialize(new[] { new { Path = "Template-State.ps1", Sha256 = Hash(helper) } });
            var manifestPath = Path.Combine(payload, "guest-files.json");
            await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(payload, "prepared.json"),
                JsonSerializer.Serialize(new { ManifestSha256 = Hash(manifestPath) }), new UTF8Encoding(true));
            // Keep manifest hashes unchanged; the altered helper has an observable effect if executed.
            await File.AppendAllTextAsync(helper,
                "\n[IO.File]::WriteAllText((Join-Path $env:ProgramData 'helper-executed'), 'unexpected')\n");
            var scriptPath = Path.Combine(payload, "Finalize-Template.ps1");
            // Remove the elevation directive only from the test copy; production has no sandbox bypass.
            await File.WriteAllTextAsync(scriptPath,
                TemplateGuestAssets.FinalizeScript.Replace("#Requires -RunAsAdministrator", "", StringComparison.Ordinal), new UTF8Encoding(true));
            var harness = Path.Combine(sandbox, "harness.ps1");
            await File.WriteAllTextAsync(harness, """
                param([string]$Sandbox)
                $ErrorActionPreference='Stop'
                $env:ProgramData=$Sandbox
                function Get-CimInstance { [pscustomobject]@{LastBootUpTime=[datetime]'2026-01-01T00:00:00Z'} }
                function Deny-Mutation { [IO.File]::WriteAllText((Join-Path $env:ProgramData 'os-mutation'), 'unexpected');throw 'OS mutation blocked by sandbox' }
                function Set-Service { Deny-Mutation }
                function Stop-Service { Deny-Mutation }
                function Start-Service { Deny-Mutation }
                function Start-Process { Deny-Mutation }
                function Set-LocalUser { Deny-Mutation }
                & (Join-Path $Sandbox 'Tiny11Template/Finalize-Template.ps1') -InvocationContext AuditUser
                exit $LASTEXITCODE
                """, new UTF8Encoding(true));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")
                : "pwsh";
            var result = await new ProcessRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-Sandbox", sandbox],
                null, CancellationToken.None);

            Assert.Equal(10, result.ExitCode);
            Assert.False(File.Exists(Path.Combine(sandbox, "helper-executed")), result.Output);
            Assert.False(File.Exists(Path.Combine(sandbox, "os-mutation")), result.Output);
            using var status = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(payload, "finalization-status.json")));
            Assert.Equal("Failed", status.RootElement.GetProperty("Stage").GetString());
            Assert.Equal("Finalization payload integrity failed", status.RootElement.GetProperty("Error").GetString());
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    // AR-1/AR-2: Execute the real finalizer; only OS observations are replaced in this sandbox.
    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 10)]
    public async Task Finalizer_requests_a_bounded_Setup_reboot_before_Sysprep(int previousBoots, int expectedExit)
    {
        if (!OperatingSystem.IsWindows()) return;
        var sandbox = Path.Combine(Path.GetTempPath(), "tiny11-entrypoint-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(sandbox, "Tiny11Template");
        Directory.CreateDirectory(payload);
        try
        {
            var helper = Path.Combine(payload, "Template-State.ps1");
            await File.WriteAllTextAsync(helper, TemplateGuestAssets.StateScript + """

                function Assert-TemplatePreparationContext { param($Context) }
                function Get-TemplateSerialDriverState { [pscustomobject]@{Healthy=$true} }
                function Get-TemplateScsiBootDriverState { [pscustomobject]@{Healthy=$true} }
                function Test-TemplateWindowsRebootPending { return -not (Microsoft.PowerShell.Management\Test-Path "$env:ProgramData/boot2") }
                """, new UTF8Encoding(true));
            var manifestPath = Path.Combine(payload, "guest-files.json");
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(new[] { new { Path = "Template-State.ps1", Sha256 = Hash(helper) } }), new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(payload, "prepared.json"),
                JsonSerializer.Serialize(new { ManifestSha256 = Hash(manifestPath), RebootRequested = false }), new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(payload, "preparation-profile.json"),
                "{\"SchemaVersion\":1,\"Profile\":\"ProxmoxCloudbaseInit\",\"Lifecycle\":\"AuditUser\"}");
            if (previousBoots > 0)
                await File.WriteAllTextAsync(Path.Combine(payload, "pending-reboot.json"),
                    JsonSerializer.Serialize(new { SchemaVersion = 1, ManifestSha256 = Hash(manifestPath), BootIdentity = "older-boot", Attempts = previousBoots }));
            await File.WriteAllTextAsync(Path.Combine(payload, "Finalize-Template.ps1"),
                TemplateGuestAssets.FinalizeScript.Replace("#Requires -RunAsAdministrator", "", StringComparison.Ordinal), new UTF8Encoding(true));
            var harness = Path.Combine(sandbox, "harness.ps1");
            await File.WriteAllTextAsync(harness, """
                param([string]$Sandbox)
                $ErrorActionPreference='Stop';$env:ProgramData=$Sandbox
                function Get-CimInstance {
                    param($ClassName,$Filter)
                    switch($ClassName){
                        'Win32_OperatingSystem' { [pscustomobject]@{LastBootUpTime=$(if(Microsoft.PowerShell.Management\Test-Path "$Sandbox/boot2"){[datetime]'2026-01-02T00:00:00Z'}else{[datetime]'2026-01-01T00:00:00Z'})} }
                        'Win32_UserAccount' { [pscustomobject]@{SID='S-1-5-21-1-500';Name='Administrator'} }
                        'Win32_Service' { [pscustomobject]@{StartMode='Disabled';State='Stopped';StartName='LocalSystem'} }
                        default { throw 'Unexpected CIM observation' }
                    }
                }
                function Get-ItemProperty { param($Path,$Name,$ErrorAction) [pscustomobject]@{ImageState='IMAGE_STATE_UNDEPLOYABLE';PendingFileRenameOperations=$(if(Microsoft.PowerShell.Management\Test-Path "$Sandbox/boot2"){@()}else{@('\??\C:\old-updater.exe','')})} }
                function Get-Service { [pscustomobject]@{Status='Running'} }
                function Deny-Mutation { [IO.File]::WriteAllText((Join-Path $env:ProgramData 'os-mutation'),'unexpected');throw 'Mutation reached before reboot' }
                function Set-Service { }
                function Start-Process {
                    if(-not (Test-Path "$Sandbox/boot2")){Deny-Mutation}
                    [IO.File]::WriteAllText("$Sandbox/sysprep-reached",'yes')
                    $p=[pscustomobject]@{Handle=1;ExitCode=0}
                    $p|Add-Member ScriptMethod WaitForExit {param($Deadline) return $true}
                    $p
                }
                function Get-Process { param($Name,$ErrorAction) }
                function Set-LocalUser { if(-not (Test-Path "$Sandbox/boot2")){Deny-Mutation} }
                & (Join-Path $Sandbox 'Tiny11Template/Finalize-Template.ps1') -InvocationContext AuditUser
                exit $LASTEXITCODE
                """, new UTF8Encoding(true));
            var result = await new ProcessRunner().RunAsync(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-Sandbox", sandbox], null, CancellationToken.None);
            Assert.True(result.ExitCode == expectedExit, result.Output + await File.ReadAllTextAsync(Path.Combine(payload, "finalization-status.json")));
            Assert.False(File.Exists(Path.Combine(sandbox, "os-mutation")), result.Output);
            using var status = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(payload, "finalization-status.json")));
            Assert.Equal(expectedExit == 2 ? "RebootRequested" : "Failed", status.RootElement.GetProperty("Stage").GetString());
            if (expectedExit == 2)
            {
                var installed = Path.Combine(sandbox, "Cloudbase Solutions", "Cloudbase-Init");
                Directory.CreateDirectory(Path.Combine(installed, "conf"));
                Directory.CreateDirectory(Path.Combine(installed, "Python", "Lib", "site-packages"));
                foreach (var name in new[] { "cloudbase-init.conf", "cloudbase-init-unattend.conf", "tiny11_cloudbase.py", "template-unattend.xml" })
                    await File.WriteAllTextAsync(Path.Combine(payload, name), "sandbox");
                foreach (var name in new[] { "cloudbase-init.conf", "cloudbase-init-unattend.conf" })
                    File.Copy(Path.Combine(payload, name), Path.Combine(installed, "conf", name));
                File.Copy(Path.Combine(payload, "tiny11_cloudbase.py"), Path.Combine(installed, "Python", "Lib", "site-packages", "tiny11_cloudbase.py"));
                await File.WriteAllTextAsync(Path.Combine(payload, "Configure-GuestPolicy.ps1"), "param([switch]$VerifyOnly)");
                var resumeHarness = Path.Combine(sandbox, "resume.ps1");
                await File.WriteAllTextAsync(resumeHarness,
                    (await File.ReadAllTextAsync(harness)).Replace("$env:ProgramData=$Sandbox", "$env:ProgramData=$Sandbox;$env:ProgramFiles=$Sandbox", StringComparison.Ordinal), new UTF8Encoding(true));
                await File.WriteAllTextAsync(Path.Combine(sandbox, "boot2"), "new boot");
                var resumed = await new ProcessRunner().RunAsync(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", resumeHarness, "-Sandbox", sandbox], null, CancellationToken.None);
                Assert.True(resumed.ExitCode == 0, resumed.Output + await File.ReadAllTextAsync(Path.Combine(payload, "finalization-status.json")));
                Assert.True(File.Exists(Path.Combine(sandbox, "sysprep-reached")), resumed.Output);
                using var resumedStatus = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(payload, "finalization-status.json")));
                Assert.Equal("SysprepReturned", resumedStatus.RootElement.GetProperty("Stage").GetString());
            }
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    // AR-1: Exercise the shipped post-install/prepared-marker path, not a reimplementation.
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public async Task Prepare_successful_install_observes_Windows_pending_reboot(bool pending, int expectedExit)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "tiny11-prepare-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var source = TemplateGuestAssets.PrepareScript;
            var start = source.IndexOf("    Set-Service QEMU-GA -StartupType Automatic", StringComparison.Ordinal);
            Assert.True(start > 0);
            await File.WriteAllTextAsync(Path.Combine(sandbox, "state.ps1"), TemplateGuestAssets.StateScript, new UTF8Encoding(true));
            var harness = Path.Combine(sandbox, "harness.ps1");
            var preamble = """
                param($Sandbox,[int]$Pending)
                $ErrorActionPreference='Stop';$root=$Sandbox;$status=Join-Path $root 'preparation-status.json'
                $manifestHash='verified-manifest';$bootIdentity='boot-one';$reboot=$false;$contextValidated=$false;$context=@{};$stage='Configure agents'
                . (Join-Path $root 'state.ps1')
                function Set-Service { }
                function Start-Service { }
                function Get-CimInstance { [pscustomobject]@{SID='S-1-5-21-1-500';Name='Administrator'} }
                function Test-TemplateWindowsRebootPending { return [bool]$Pending }
                try {
                """;
            await File.WriteAllTextAsync(harness, preamble + "\n" + source[start..], new UTF8Encoding(true));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "pwsh";
            var result = await new ProcessRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-Sandbox", sandbox, "-Pending", pending ? "1" : "0"], null, CancellationToken.None);
            Assert.True(result.ExitCode == expectedExit, result.Output);
            using var prepared = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sandbox, "prepared.json")));
            Assert.False(prepared.RootElement.GetProperty("RebootRequested").GetBoolean());
            Assert.Equal(pending, File.Exists(Path.Combine(sandbox, "pending-reboot.json")));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    // AR-2: The real prepared-resume branch must terminate before reaching installer code.
    [Fact]
    public async Task Prepare_resumes_after_reboot_without_reinstalling_packages()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "tiny11-prepare-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var conf = Path.Combine(sandbox, "Cloudbase Solutions", "Cloudbase-Init", "conf");
            var plugin = Path.Combine(sandbox, "Cloudbase Solutions", "Cloudbase-Init", "Python", "Lib", "site-packages");
            Directory.CreateDirectory(conf);Directory.CreateDirectory(plugin);
            foreach (var name in new[] { "cloudbase-init.conf", "tiny11_cloudbase.py" })
            {
                await File.WriteAllTextAsync(Path.Combine(sandbox, name), "sandbox");
                File.Copy(Path.Combine(sandbox, name), Path.Combine(name.EndsWith(".conf", StringComparison.Ordinal) ? conf : plugin, name));
            }
            await File.WriteAllTextAsync(Path.Combine(sandbox, "Configure-GuestPolicy.ps1"), "param([switch]$VerifyOnly)");
            await File.WriteAllTextAsync(Path.Combine(sandbox, "state.ps1"), TemplateGuestAssets.StateScript, new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(sandbox, "prepared.json"),
                JsonSerializer.Serialize(new { ManifestSha256 = "manifest", RebootRequested = false, BootIdentity = "old-boot" }));
            var source = TemplateGuestAssets.PrepareScript;
            var start = source.IndexOf("    if (Test-Path (Join-Path $root 'prepared.json'))", StringComparison.Ordinal);
            var end = source.IndexOf("    foreach ($name in @('vioser.cat'", start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            var harness = Path.Combine(sandbox, "harness.ps1");
            await File.WriteAllTextAsync(harness, """
                param($Sandbox)
                $ErrorActionPreference='Stop';$root=$Sandbox;$env:ProgramFiles=$Sandbox;$manifestHash='manifest';$bootIdentity='new-boot'
                . (Join-Path $root 'state.ps1')
                function Get-Service { [pscustomobject]@{Status='Running'} }
                function Get-TemplateSerialDriverState { [pscustomobject]@{Healthy=$true} }
                function Test-TemplateWindowsRebootPending { return $false }
                """ + "\n" + source[start..end] + "\nthrow 'Reached installer path on prepared resume'", new UTF8Encoding(true));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "pwsh";
            var result = await new ProcessRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-Sandbox", sandbox], null, CancellationToken.None);
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.False(File.Exists(Path.Combine(sandbox, "installer-in-progress.json")));
        }
        finally { Directory.Delete(sandbox, true); }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
