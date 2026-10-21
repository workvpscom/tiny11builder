using System.Text;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateCloneTests
{
    // GP-03 / GP-T10: Isolate the OS boundary without running Cloudbase or installing services.
    [Theory]
    [InlineData("success", 0, "MainServiceStarted")]
    [InlineData("plugin-tamper", 10, "Verify clone service")]
    [InlineData("plugin-missing", 10, "Verify clone service")]
    [InlineData("start-fail", 10, "Activate main service")]
    [InlineData("ack-timeout", 10, "Activate main service")]
    public async Task Clone_activation_respects_command_result(string mode, int expectedExit, string expectedStage)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "tiny11-clone-test-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(sandbox, "Tiny11Template");
        Directory.CreateDirectory(payload);
        try
        {
            var conf = Path.Combine(sandbox, "Cloudbase Solutions", "Cloudbase-Init", "conf");
            Directory.CreateDirectory(conf);
            foreach (var name in new[] { "cloudbase-init.conf", "cloudbase-init-unattend.conf" })
            {
                await File.WriteAllTextAsync(Path.Combine(payload, name), "sandbox-config");
                await File.WriteAllTextAsync(Path.Combine(conf, name), "sandbox-config");
            }
            var plugin = Path.Combine(sandbox, "Cloudbase Solutions", "Cloudbase-Init", "Python", "Lib", "site-packages", "tiny11_cloudbase.py");
            Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
            await File.WriteAllTextAsync(Path.Combine(payload, "tiny11_cloudbase.py"), "sandbox-plugin");
            if (mode != "plugin-missing") await File.WriteAllTextAsync(plugin, mode == "plugin-tamper" ? "changed" : "sandbox-plugin");
            await File.WriteAllTextAsync(Path.Combine(payload, "guest-files.json"), "[]", new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(payload, "Initialize-Clone.ps1"),
                TemplateGuestAssets.CloneScript.Replace("#Requires -RunAsAdministrator", "", StringComparison.Ordinal), new UTF8Encoding(true));
            var harness = Path.Combine(sandbox, "harness.ps1");
            await File.WriteAllTextAsync(harness, """
                param([string]$Sandbox,[string]$Mode)
                $ErrorActionPreference='Stop'
                Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management
                if($PSVersionTable.PSEdition -eq 'Desktop'){Import-Module CimCmdlets}
                $env:ProgramData=$Sandbox
                $env:ProgramFiles=$Sandbox
                $global:Mode=$Mode
                function Record([string]$Event) { Add-Content (Join-Path $env:ProgramData 'events.txt') $Event }
                function Get-FileHash { param($LiteralPath,$Path)
                    $p=if($LiteralPath){$LiteralPath}else{$Path}
                    if($p -like '*OpenStackService.exe'){return [pscustomobject]@{Hash='E141FE1CCF2ACF9DF052894D25D8DF4A219F51C0A21D9C8156C34C6CBCA11425'}}
                    return Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $p
                }
                function Get-CimInstance {
                    $wrapper=Join-Path $env:ProgramFiles 'Cloudbase Solutions\Cloudbase-Init\bin\OpenStackService.exe'
                    [pscustomobject]@{StartName='LocalSystem';PathName=('"'+$wrapper+'" cloudbase-init command')}
                }
                function Stop-Service { Record 'stop' }
                function Set-Service { param($Name,$StartupType);Record $StartupType }
                function Start-Process {
                    throw 'Clone initializer must not execute a second provisioning owner'
                }
                function New-Object {
                    param($TypeName,$ArgumentList)
                    if($TypeName -ne 'System.ServiceProcess.ServiceController'){throw 'Unexpected OS object in sandbox'}
                    $c=[pscustomobject]@{}
                    $c|Add-Member ScriptMethod Start { Record 'start';if($global:Mode -eq 'start-fail'){throw 'injected SCM start failure'} }
                    $c|Add-Member ScriptMethod WaitForStatus {param($Status,$Timeout) Record ('ack-timeout='+$Timeout.TotalSeconds);if($global:Mode -eq 'ack-timeout'){throw 'injected SCM acknowledgement timeout'}}
                    $c|Add-Member ScriptMethod Dispose { Record 'dispose' }
                    return $c
                }
                foreach($name in @('Get-FileHash','Get-CimInstance','Start-Process','New-Object','Set-Service','Stop-Service')){
                    if((Get-Command $name).CommandType -ne 'Function'){throw ('Sandbox boundary not active: '+$name)}
                }
                & (Join-Path $Sandbox 'Tiny11Template/Initialize-Clone.ps1')
                exit $LASTEXITCODE
                """, new UTF8Encoding(true));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "pwsh";
            var result = await new ProcessRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-Sandbox", sandbox, "-Mode", mode],
                null, CancellationToken.None);

            var statusText = await File.ReadAllTextAsync(Path.Combine(payload, "clone-initialization-status.json"));
            Assert.True(expectedExit == result.ExitCode, statusText + "\n" + result.Output);
            using var status = JsonDocument.Parse(statusText);
            Assert.True(expectedStage == status.RootElement.GetProperty("Stage").GetString(), statusText);
            var events = await File.ReadAllLinesAsync(Path.Combine(sandbox, "events.txt"));
            string[] expected = mode switch
            {
                "success" or "ack-timeout" => ["stop", "Disabled", "Automatic", "start", "ack-timeout=60", "dispose"],
                "start-fail" => ["stop", "Disabled", "Automatic", "start", "dispose"],
                _ => ["stop", "Disabled"]
            };
            Assert.Equal(expected, events);
            if (mode == "success")
            {
                Assert.Equal("CloudbaseExtension", status.RootElement.GetProperty("ReconciliationOwner").GetString());
                Assert.False(status.RootElement.GetProperty("NetworkVerified").GetBoolean());
                Assert.False(status.RootElement.GetProperty("ProvisioningVerified").GetBoolean());
            }
            else Assert.Equal("Failed", status.RootElement.GetProperty("State").GetString());
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }
}
