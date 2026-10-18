using System.Text;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateGuestPolicyBehaviorTests
{
    // GP-08 / GP-T14: thực thi helper với cmdlets cô lập, không thay host.
    [Theory]
    [InlineData("prepare", 0)]
    [InlineData("activate", 0)]
    [InlineData("verify", 0)]
    [InlineData("bad-nla", 1)]
    [InlineData("bad-firewall", 1)]
    [InlineData("bad-rule", 1)]
    [InlineData("bad-service", 1)]
    public async Task Policy_behavior_respects_activation_and_verification(string mode, int expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "policy.ps1"), TemplateGuestAssets.PolicyScript.Replace("#Requires -RunAsAdministrator", "", StringComparison.Ordinal), new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(root, "harness.ps1"), """
                param($Root,$Mode)
                $ErrorActionPreference='Stop'
                Import-Module Microsoft.PowerShell.Management,Microsoft.PowerShell.Utility
                function Record($x){Add-Content (Microsoft.PowerShell.Management\Join-Path $Root 'events') $x}
                function Join-Path {param($Path,$ChildPath);if($Path -like 'HKLM:*'){return ($Path+'\'+$ChildPath)};Microsoft.PowerShell.Management\Join-Path $Path $ChildPath}
                function Set-ItemProperty {Record 'rdp-registry'}
                function Set-NetFirewallProfile {Record 'firewall-enable'}
                function Get-NetFirewallProfile {[pscustomobject]@{Enabled=($Mode -ne 'bad-firewall')}}
                function Get-ItemProperty {param($Path)
                    [pscustomobject]@{fDenyTSConnections=0;UserAuthentication=$(if($Mode -eq 'bad-nla'){0}else{1});NtpServer='time.windows.com,0x9'}
                }
                function Get-NetFirewallRule { [pscustomobject]@{Enabled='True';Direction='Inbound';Action=$(if($Mode -eq 'bad-rule'){'Block'}else{'Allow'})} }
                function Get-NetFirewallPortFilter {process{[pscustomobject]@{Protocol='TCP';LocalPort='3389'}}}
                function Get-NetFirewallAddressFilter {process{[pscustomobject]@{RemoteAddress='Any'}}}
                function Remove-NetFirewallRule {Record 'remove-rule'}
                function New-NetFirewallRule {Record 'create-rule'}
                function Set-Service {param($Name,$StartupType);Record ('service-'+$Name)}
                function Start-Service {param($Name);Record ('start-'+$Name)}
                function Get-CimInstance {[pscustomobject]@{StartMode='Auto';State=$(if($Mode -eq 'bad-service'){'Stopped'}else{'Running'})}}
                try {
                    $params=@{}
                    if($Mode -eq 'verify' -or $Mode -like 'bad-*'){$params.VerifyOnly=$true;$params.ActivateRdp=$true}
                    elseif($Mode -eq 'activate'){$params.ActivateRdp=$true}
                    & (Join-Path $Root 'policy.ps1') @params
                    exit 0
                } catch {Write-Output $_.Exception.Message;exit 1}
                """, new UTF8Encoding(true));
            var exe = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "pwsh";
            var result = await new ProcessRunner().RunAsync(exe, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "harness.ps1"), "-Root", root, "-Mode", mode], null, CancellationToken.None);
            Assert.True(expected == result.ExitCode, result.Output);
            var events = File.Exists(Path.Combine(root, "events")) ? await File.ReadAllLinesAsync(Path.Combine(root, "events")) : [];
            if (mode == "prepare")
            {
                Assert.DoesNotContain("rdp-registry", events);
                Assert.DoesNotContain("create-rule", events);
                Assert.Contains("start-W32Time", events);
            }
            else if (mode == "activate") Assert.Contains("create-rule", events);
            else Assert.Empty(events);
        }
        finally { Directory.Delete(root, true); }
    }
}
