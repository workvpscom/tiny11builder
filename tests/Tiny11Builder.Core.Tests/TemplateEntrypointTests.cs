using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateEntrypointTests
{
    // GP-01 / GP-T09: chạy entrypoint trong sandbox, mọi OS mutation bị stub.
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
            // Hash trong manifest giữ nguyên; altered helper có tác dụng quan sát được nếu bị execute.
            await File.AppendAllTextAsync(helper,
                "\n[IO.File]::WriteAllText((Join-Path $env:ProgramData 'helper-executed'), 'unexpected')\n");
            var scriptPath = Path.Combine(payload, "Finalize-Template.ps1");
            // Chỉ bỏ elevation directive trên bản sao test. Production không có sandbox bypass.
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

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
