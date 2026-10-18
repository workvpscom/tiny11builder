using System.Text;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateDriverTests
{
    // GP-05 / GP-T11..12: Test the CIM/native boundary using real INF/SYS bytes from the pinned package.
    [Theory]
    [InlineData("current", 259, true, "AlreadyCurrent", false)]
    [InlineData("install", 259, true, "InstallRequested", false)]
    [InlineData("install", 0, true, "InstallRequested", false)]
    [InlineData("install", 3010, true, "InstallRequested", true)]
    [InlineData("missing", 259, false, "", false)]
    [InlineData("version", 259, false, "", false)]
    [InlineData("binding", 259, false, "", false)]
    [InlineData("provider", 259, false, "", false)]
    [InlineData("unsigned", 259, false, "", false)]
    [InlineData("unhealthy", 259, false, "", false)]
    [InlineData("sys-tamper", 259, false, "", false)]
    [InlineData("inf-tamper", 259, false, "", false)]
    [InlineData("service", 259, false, "", false)]
    [InlineData("reboot-state", 3010, true, "InstallRequested", true)]
    [InlineData("query-fail", 3010, true, "AlreadyCurrent", true)]
    [InlineData("unhealthy", 0, false, "", false)]
    [InlineData("install", 5, false, "", false)]
    public async Task Serial_driver_preparation_enforces_observed_identity(
        string mode, int code, bool accepted, string state, bool reboot)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "tiny11-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            Directory.CreateDirectory(Path.Combine(sandbox, "INF"));
            Directory.CreateDirectory(Path.Combine(sandbox, "System32", "drivers"));
            await File.WriteAllBytesAsync(Path.Combine(sandbox, "INF", "oem7.inf"), TemplateGuestAssets.Driver("vioser.inf"));
            await File.WriteAllBytesAsync(Path.Combine(sandbox, "System32", "drivers", "vioser.sys"), TemplateGuestAssets.Driver("vioser.sys"));
            if (mode == "inf-tamper") await File.AppendAllTextAsync(Path.Combine(sandbox, "INF", "oem7.inf"), "tamper");
            if (mode == "sys-tamper") await File.AppendAllTextAsync(Path.Combine(sandbox, "System32", "drivers", "vioser.sys"), "tamper");
            var helper = Path.Combine(sandbox, "Template-State.ps1");
            await File.WriteAllTextAsync(helper, TemplateGuestAssets.StateScript, new UTF8Encoding(true));
            var harness = Path.Combine(sandbox, "harness.ps1");
            await File.WriteAllTextAsync(harness, """
                param([string]$Sandbox,[string]$Mode,[int]$Code)
                $ErrorActionPreference='Stop'
                Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management
                if($PSVersionTable.PSEdition -eq 'Desktop'){Import-Module CimCmdlets}
                $env:WINDIR=$Sandbox
                $global:Mode=$Mode;$global:Code=$Code;$global:Reads=0;$global:Installs=0
                $global:DeviceId='PCI\VEN_1AF4&DEV_1003&SUBSYS_00031AF4&REV_00\5&EXAMPLE'
                function Get-CimInstance {
                    param($ClassName)
                    if($ClassName -eq 'Win32_PnPEntity'){
                        $global:Reads++
                        if($global:Mode -eq 'query-fail' -and $global:Reads -eq 2){throw 'Injected post-install CIM failure'}
                        if($global:Mode -eq 'missing' -or ($global:Mode -ne 'current' -and $global:Reads -eq 1)){return}
                        return [pscustomobject]@{PNPDeviceID=$global:DeviceId;Service=$(if($global:Mode -eq 'service'){'Other'}else{'VirtioSerial'});ConfigManagerErrorCode=$(if($global:Mode -eq 'unhealthy'){28}else{0})}
                    }
                    if($ClassName -ne 'Win32_PnPSignedDriver'){throw 'Unexpected CIM query'}
                    return [pscustomobject]@{
                        DeviceID=$(if($global:Mode -eq 'binding'){'PCI\OTHER'}else{$global:DeviceId})
                        InfName='oem7.inf';DriverVersion=$(if($global:Mode -eq 'version'){'1.0.0.0'}else{'100.93.104.24000'})
                        DriverProviderName=$(if($global:Mode -eq 'provider'){'Other'}else{'Red Hat, Inc.'})
                        IsSigned=($global:Mode -ne 'unsigned')
                    }
                }
                function pnputil.exe { $global:Installs++;$global:LASTEXITCODE=$global:Code }
                if((Get-Command pnputil.exe).CommandType -ne 'Function'){throw 'Native installer not isolated'}
                . (Join-Path $Sandbox 'Template-State.ps1')
                $accepted=$false;$result=$null;$errorText=$null
                try{$result=Initialize-TemplateSerialDriver -Root $Sandbox -Logs $Sandbox -BootIdentity 'boot-a';$accepted=$true}catch{$errorText=$_.Exception.Message}
                if($Mode -eq 'query-fail'){
                    if($accepted -or $errorText -notlike '*Injected post-install CIM failure*'){throw 'Expected post-install query failure'}
                    $result=Initialize-TemplateSerialDriver -Root $Sandbox -Logs $Sandbox -BootIdentity 'boot-a'
                    $next=Initialize-TemplateSerialDriver -Root $Sandbox -Logs $Sandbox -BootIdentity 'boot-b'
                    if(-not $result.RebootRequired -or $next.RebootRequired -or $global:Installs -ne 1){throw 'Reboot requirement lost after query failure'}
                    $accepted=$true
                }
                if($Mode -eq 'reboot-state' -and $accepted){
                    $same=Initialize-TemplateSerialDriver -Root $Sandbox -Logs $Sandbox -BootIdentity 'boot-a'
                    $next=Initialize-TemplateSerialDriver -Root $Sandbox -Logs $Sandbox -BootIdentity 'boot-b'
                    if(-not $same.RebootRequired -or $next.RebootRequired -or $global:Installs -ne 1){throw 'Reboot state lost or duplicate driver install'}
                }
                @{Accepted=$accepted;Result=$result;Installs=$global:Installs;Error=$errorText}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $Sandbox 'test-result.json') -Encoding UTF8
                exit 0
                """, new UTF8Encoding(true));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "pwsh";
            var result = await new ProcessRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-Sandbox", sandbox, "-Mode", mode, "-Code", code.ToString()],
                null, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            var text = await File.ReadAllTextAsync(Path.Combine(sandbox, "test-result.json"));
            using var receipt = JsonDocument.Parse(text);
            Assert.True(receipt.RootElement.GetProperty("Accepted").GetBoolean() == accepted, text);
            Assert.Equal(mode == "current" ? 0 : 1, receipt.RootElement.GetProperty("Installs").GetInt32());
            if (accepted)
            {
                var driver = receipt.RootElement.GetProperty("Result");
                Assert.Equal(state, driver.GetProperty("State").GetString());
                Assert.Equal(reboot, driver.GetProperty("RebootRequired").GetBoolean());
            }
            else Assert.False(string.IsNullOrWhiteSpace(receipt.RootElement.GetProperty("Error").GetString()));
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }
}
