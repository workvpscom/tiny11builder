using System.Text;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateImportProbeTests
{
    // AC-5: Run the shipped import-to-status entrypoint path with all guest setup replaced in the test copy.
    [Fact]
    public async Task Import_timeout_records_failure_without_prepared_marker()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "tiny11-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var shipped = TemplateGuestAssets.PrepareScript;
            var import = shipped.IndexOf("    $import = Start-Process", StringComparison.Ordinal);
            Assert.True(import > 0);
            var preamble = """
                param($Sandbox)
                $ErrorActionPreference='Stop'
                $root=$Sandbox
                $status=Join-Path $root 'preparation-status.json'
                $stage='Import plugin'
                $contextValidated=$false
                $context=@{}
                $pythonRoot=$Sandbox
                function Start-Process {
                    param($FilePath,$ArgumentList,[switch]$Wait,[switch]$PassThru,[switch]$NoNewWindow)
                    if($Wait){throw 'Unbounded Start-Process wait requested'}
                    $process=[pscustomobject]@{Id=123;Handle=123;ExitCode=0}
                    $process | Add-Member ScriptMethod WaitForExit {param($Milliseconds)
                        if($Milliseconds -le 0 -or $Milliseconds -gt 60000){throw 'Import deadline outside bound'}
                        [IO.File]::WriteAllText((Join-Path $root 'wait-observed'),[string]$Milliseconds)
                        return $false
                    }
                    $process | Add-Member ScriptMethod Kill { [IO.File]::WriteAllText((Join-Path $root 'kill-observed'),'yes') }
                    return $process
                }
                function Set-Service {throw 'OS mutation blocked'}
                function Start-Service {throw 'OS mutation blocked'}
                function Get-CimInstance {throw 'OS mutation blocked'}
                try {
                """;
            var script = Path.Combine(sandbox, "probe.ps1");
            await File.WriteAllTextAsync(script, preamble + "\n" + shipped[import..], new UTF8Encoding(true));
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "pwsh";
            var result = await new ProcessRunner().RunAsync(executable,
                ["-NoProfile", "-NonInteractive", "-File", script, "-Sandbox", sandbox], null, watchdog.Token, finishOnCancellation: false);
            Assert.Equal(10, result.ExitCode);
            using var status = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sandbox, "preparation-status.json")));
            Assert.Contains("import timed out", status.RootElement.GetProperty("Error").GetString());
            Assert.True(File.Exists(Path.Combine(sandbox, "wait-observed")), result.Output);
            Assert.True(File.Exists(Path.Combine(sandbox, "kill-observed")), result.Output);
            Assert.False(File.Exists(Path.Combine(sandbox, "prepared.json")));
        }
        finally { Directory.Delete(sandbox, true); }
    }
}
