using System.Text;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateDriverTests
{
    // AC-S1: Literal pins supplied by the independently verified upstream package.
    [Theory]
    [InlineData("vioser.cat", "1E93A77AA320F515E6998DA233FA6F1B0E1DA9409EB7AACEDCFD4D21DF255E4D")]
    [InlineData("vioser.inf", "4A8FCFDEEDBF05BF0B02535ACE5156D5DB5F1C229D30532F88AE477ADF82BEED")]
    [InlineData("vioser.sys", "B6767019C660903B59A3BB8396C40EDA2DE3CDA51CCC121B229050AE28B619FF")]
    [InlineData("vioscsi.cat", "F484461B7202A840D6F70ABC2EDFD82A01DD44349D234FAED9166D561D199B6A")]
    [InlineData("vioscsi.inf", "E49A966F7313DA4F6130DB00D84B57A167ADA0DE3D877C7CC584A7AED99C5BA2")]
    [InlineData("vioscsi.sys", "BE1FD2D12FA178D44CCC40548CC160D695A356566414E88976FC33F9F6C5AFA3")]
    [InlineData("LICENSE.txt", "DF050022069D6DC3831F34A3787F8EC45234BA66AACA72079A2EC568A2898EB9")]
    [InlineData("vioscsi-LICENSE.txt", "ACCD84CB35EFF899D619FF40E6FEA8074305CDF0E7191DB71A5D9C3A015E3B02")]
    public void Embedded_driver_preserves_approved_package_bytes(string name, string hash) =>
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TemplateGuestAssets.Driver(name))));

    // AC-S4: Observe the actual system disk's PCI ancestry, not unrelated controllers.
    [Theory]
    [InlineData("1004", "current", true)]
    [InlineData("1048", "current", true)]
    [InlineData("1004", "unhealthy", false)]
    [InlineData("1048", "version", false)]
    [InlineData("1048", "unsigned", false)]
    [InlineData("1048", "binding", false)]
    [InlineData("1048", "provider", false)]
    [InlineData("1048", "service", false)]
    [InlineData("1048", "inf-tamper", false)]
    [InlineData("1048", "sys-tamper", false)]
    [InlineData("1048", "missing", false)]
    [InlineData("1048", "missing-disk", false)]
    [InlineData("1048", "ancestry-cycle", false)]
    [InlineData("1048", "ancestor-hop", true)]
    [InlineData("SATA", "current", true)]
    public async Task Scsi_boot_driver_health_requires_pinned_identity(string device, string mode, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-scsi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "INF"));
        Directory.CreateDirectory(Path.Combine(root, "System32", "drivers"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "INF", "oem8.inf"), TemplateGuestAssets.Driver("vioscsi.inf"));
            await File.WriteAllBytesAsync(Path.Combine(root, "System32", "drivers", "vioscsi.sys"), TemplateGuestAssets.Driver("vioscsi.sys"));
            if (mode == "inf-tamper") await File.AppendAllTextAsync(Path.Combine(root, "INF", "oem8.inf"), "tamper");
            if (mode == "sys-tamper") await File.AppendAllTextAsync(Path.Combine(root, "System32", "drivers", "vioscsi.sys"), "tamper");
            await File.WriteAllTextAsync(Path.Combine(root, "state.ps1"), TemplateGuestAssets.StateScript);
            await File.WriteAllTextAsync(Path.Combine(root, "test.ps1"), """
                param($Root,$Device,$Mode)
                $ErrorActionPreference='Stop';$env:WINDIR=$Root;$env:SystemDrive='C:'
                $global:ControllerId=if($Device -eq 'SATA'){'PCI\VEN_8086&DEV_2922\EXAMPLE'}else{'PCI\VEN_1AF4&DEV_'+$Device+'&SUBSYS_EXAMPLE\EXAMPLE'}
                function Get-Partition { param($DriveLetter); [pscustomobject]@{DiskNumber=0} }
                function Get-CimInstance {
                    param($ClassName,$Filter)
                    switch($ClassName){
                        'Win32_DiskDrive' { if($Mode -ne 'missing-disk'){[pscustomobject]@{PNPDeviceID='SCSI\DISK\BOOT'}} }
                        'Win32_PnPEntity' { if($Mode -ne 'missing'){[pscustomobject]@{PNPDeviceID=$global:ControllerId;Service=$(if($Mode -eq 'service'){'Other'}else{'vioscsi'});ConfigManagerErrorCode=$(if($Mode -eq 'unhealthy'){28}else{0})}} }
                        'Win32_PnPSignedDriver' { [pscustomobject]@{DeviceID=$(if($Mode -eq 'binding'){'PCI\OTHER'}else{$global:ControllerId});InfName='oem8.inf';DriverVersion=$(if($Mode -eq 'version'){'1.0'}else{'100.93.104.24000'});DriverProviderName=$(if($Mode -eq 'provider'){'Other'}else{'Red Hat, Inc.'});IsSigned=($Mode -ne 'unsigned')} }
                        default { throw "Unexpected class: $ClassName" }
                    }
                }
                function Get-PnpDeviceProperty {
                    param($InstanceId,$KeyName)
                    [pscustomobject]@{Data=$(if($Mode -eq 'ancestry-cycle'){$InstanceId}elseif($Mode -eq 'ancestor-hop' -and $InstanceId -eq 'SCSI\DISK\BOOT'){'STORAGE\INTERMEDIATE'}else{$global:ControllerId})}
                }
                function pnputil.exe { throw 'Storage must be supplied by the ISO, not patched after installation' }
                . (Join-Path $Root 'state.ps1')
                $accepted=$false;$result=$null
                try { $result=Get-TemplateScsiBootDriverState;$accepted=$result.Healthy } catch { $failure=$_.Exception.Message }
                @{Accepted=$accepted;Result=$result;Error=$failure}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $Root 'result.json')
                """);
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "pwsh";
            var run = await new ProcessRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "test.ps1"), "-Root", root, "-Device", device, "-Mode", mode], null, CancellationToken.None);
            Assert.True(run.ExitCode == 0, run.Output);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "result.json")));
            Assert.True(accepted == result.RootElement.GetProperty("Accepted").GetBoolean(), result.RootElement.ToString());
            if (accepted) Assert.Equal(device != "SATA", result.RootElement.GetProperty("Result").GetProperty("Required").GetBoolean());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
