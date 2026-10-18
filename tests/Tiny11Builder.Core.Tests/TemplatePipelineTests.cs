using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class TemplatePipelineTests
{
    // TM-07: actual pinned artifacts và real verifier, fake DISM boundary.
    [Fact]
    public async Task Template_checkpoint_preserves_payload_and_resumes_without_cache_when_configured()
    {
        var cache = Environment.GetEnvironmentVariable("TINY11_TEST_TEMPLATE_CACHE");
        if (string.IsNullOrEmpty(cache)) return;
        using var fixture = new BuildFixture(build: 26300);
        var recipe = Profile(fixture);
        SeedCache(cache, recipe.WorkspacePath);
        var runner = new SignatureBoundaryRunner(fixture.Runner);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        using var cancel = new CancellationTokenSource();
        var sawImagePayload = false;
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Unmount-Image") || !args.Contains("/Commit") || sawImagePayload) return;
            var mount = args.Single(arg => arg.StartsWith("/MountDir:", StringComparison.Ordinal))[10..];
            sawImagePayload = File.Exists(Path.Combine(mount, "ProgramData", "Tiny11Template", "Prepare-Template.ps1"));
            cancel.Cancel();
        };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, cancel.Token));
        fixture.Runner.OnCommand = null;
        Assert.True(failure.IsCancellation, failure.ToString());
        Assert.True(sawImagePayload, failure.ToString());
        var payload = Path.Combine(failure.WorkDirectory, "media", "tiny11-template");
        Assert.Equal(TemplateGuestAssets.StateScript, await File.ReadAllTextAsync(Path.Combine(payload, "Template-State.ps1")));
        foreach (var name in new[] { "cloudbase-init.conf", "cloudbase-init-unattend.conf" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(payload, name));
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            Assert.True(bytes.AsSpan().StartsWith("[DEFAULT]"u8));
        }
        Assert.True((await File.ReadAllBytesAsync(Path.Combine(payload, "Prepare-Template.ps1"))).AsSpan()
            .StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var checkpoint = CheckpointStore.Read(failure.WorkDirectory);
        Assert.Equal(2, checkpoint.Report.TemplateArtifacts.Count);
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.Report.TemplateManifestSha256));
        Assert.Contains(checkpoint.StagedMedia, file => file.RelativePath == Path.Combine("tiny11-template", "guest-files.json"));
        await VerifyDisabledTransformAsync(failure.WorkDirectory);
        Directory.Delete(Path.Combine(recipe.WorkspacePath, "downloads"), recursive: true);

        var result = await engine.ResumeAsync(Path.Combine(failure.WorkDirectory, "job.json"), recipe, null, CancellationToken.None);

        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(2, report.RootElement.GetProperty("TemplateArtifacts").GetArrayLength());
        Assert.False(report.RootElement.GetProperty("BootValidated").GetBoolean());
    }

    // TM-07: manifest/payload tamper từ chối trước mutation.
    [Fact]
    public async Task Changed_template_payload_is_refused_on_resume_when_configured()
    {
        var cache = Environment.GetEnvironmentVariable("TINY11_TEST_TEMPLATE_CACHE");
        if (string.IsNullOrEmpty(cache)) return;
        using var fixture = new BuildFixture(build: 26300);
        var recipe = Profile(fixture);
        SeedCache(cache, recipe.WorkspacePath);
        var engine = new BuildEngine(new SignatureBoundaryRunner(fixture.Runner), new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Unmount-Image") && args.Contains("/Commit")) cancel.Cancel(); };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, cancel.Token));
        fixture.Runner.OnCommand = null;
        Assert.True(failure.IsCancellation, failure.ToString());
        Assert.Equal(ResumePhase.Committed, CheckpointStore.Read(failure.WorkDirectory).Phase);
        File.AppendAllText(Path.Combine(failure.WorkDirectory, "media", "tiny11-template", "cloudbase-init.conf"), "tampered");
        var calls = fixture.Runner.Calls.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.ResumeAsync(Path.Combine(failure.WorkDirectory, "job.json"), recipe, null, CancellationToken.None));

        Assert.DoesNotContain(fixture.Runner.Calls.Skip(calls), JobScenarios.Mutating);
    }

    // TM-04 / TM-T11: semantic policy chặn publish dù file thuộc media inventory hợp lệ.
    [Fact]
    public async Task Invalid_initial_answer_is_refused_before_iso_creation_when_configured()
    {
        var cache = Environment.GetEnvironmentVariable("TINY11_TEST_TEMPLATE_CACHE");
        if (string.IsNullOrEmpty(cache)) return;
        using var fixture = new BuildFixture(build: 26300);
        var recipe = Profile(fixture);
        SeedCache(cache, recipe.WorkspacePath);
        var changed = false;
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Unmount-Image") || !args.Contains("/Commit") || changed) return;
            var mount = args.Single(arg => arg.StartsWith("/MountDir:", StringComparison.Ordinal))[10..];
            var answer = Path.Combine(Path.GetDirectoryName(mount)!, "media", "autounattend.xml");
            File.WriteAllText(answer, File.ReadAllText(answer).Replace("-InvocationContext AuditUser", "-InvocationContext Wrong", StringComparison.Ordinal));
            changed = true;
        };
        var engine = new BuildEngine(new SignatureBoundaryRunner(fixture.Runner), new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());

        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, CancellationToken.None));

        Assert.True(changed);
        Assert.Contains("launcher contract", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("-m"));
        Assert.False(File.Exists(recipe.OutputPath));
    }

    // TM-04 / TM-T13: manifest hợp lệ không được che encoding sai cho parser thật.
    [Fact]
    public async Task Configuration_bom_is_refused_even_with_updated_manifest_when_configured()
    {
        var cache = Environment.GetEnvironmentVariable("TINY11_TEST_TEMPLATE_CACHE");
        if (string.IsNullOrEmpty(cache)) return;
        using var fixture = new BuildFixture(build: 26300);
        var recipe = Profile(fixture);
        SeedCache(cache, recipe.WorkspacePath);
        var job = Path.Combine(recipe.WorkspacePath, "tiny11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "job.json"), "{}");
        var media = Path.Combine(job, "media");
        var receipts = await new TemplatePayloadStager(new SignatureBoundaryRunner(fixture.Runner))
            .PrepareMediaAsync(recipe.WorkspacePath, job, media, CancellationToken.None);
        var root = Path.Combine(media, "tiny11-template");
        var config = Path.Combine(root, "cloudbase-init-unattend.conf");
        await File.WriteAllTextAsync(config, TemplateGuestAssets.UnattendConfiguration, new System.Text.UTF8Encoding(true));
        var manifestPath = Path.Combine(root, "guest-files.json");
        var entries = JsonSerializer.Deserialize<TemplateGuestFile[]>(await File.ReadAllTextAsync(manifestPath), JsonDefaults.Options)!;
        entries = entries.Select(entry => entry.Path == "cloudbase-init-unattend.conf"
            ? entry with { Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(config))) }
            : entry).ToArray();
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(entries, JsonDefaults.Options));
        var manifestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifestPath)));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => TemplatePayloadStager.ValidateMediaAsync(
            media, receipts, manifestHash, CancellationToken.None));

        Assert.Contains("without BOM", error.Message, StringComparison.Ordinal);
    }

    private static async Task VerifyDisabledTransformAsync(string job)
    {
        var root = Path.Combine(job, "media", "tiny11-template");
        var original = Path.Combine(root, "CloudbaseInitSetup_1_1_9_x64.msi");
        var copy = Path.Combine(job, "transform-check.msi");
        File.Copy(original, copy);
        var script = "$ErrorActionPreference='Stop'; $i=New-Object -ComObject WindowsInstaller.Installer; " +
            "$d=$i.OpenDatabase('" + copy.Replace("'", "''", StringComparison.Ordinal) + "',1); " +
            "$d.ApplyTransform('" + Path.Combine(root, "CloudbaseDisabled.mst").Replace("'", "''", StringComparison.Ordinal) + "',0); " +
            "$v=$d.OpenView('SELECT `Name`,`StartType` FROM `ServiceInstall`');$v.Execute();$r=$v.Fetch(); " +
            "if($r.StringData(1) -ne 'cloudbase-init' -or $r.IntegerData(2) -ne 4){exit 1};exit 0";
        var result = await new ProcessRunner().RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(TemplatePayloadStager.CloudbaseSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(original))));
    }

    private static BuildRecipe Profile(BuildFixture fixture) => fixture.Recipe with
    {
        Mode = BuildMode.Tiny11Core, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
        SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core),
        TemplateProfile = TemplateProfile.ProxmoxCloudbaseInit, KeepWorkingFiles = true
    };

    private static void SeedCache(string source, string workspace)
    {
        var target = Path.Combine(workspace, "downloads", "template-artifacts");
        Directory.CreateDirectory(target);
        foreach (var name in new[] { "CloudbaseInitSetup_1_1_9_x64.msi", "qemu-ga-110.2.3-x64.msi" })
        {
            var copy = Path.Combine(target, name);
            File.Copy(Path.Combine(source, name), copy);
            File.SetAttributes(copy, FileAttributes.Normal);
        }
    }

    private sealed class SignatureBoundaryRunner(IProcessRunner dism) : IProcessRunner
    {
        private readonly ProcessRunner real = new();
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output,
            CancellationToken cancellationToken, bool finishOnCancellation = true) =>
            executable.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
                ? real.RunAsync(executable, arguments, output, cancellationToken, finishOnCancellation)
                : dism.RunAsync(executable, arguments, output, cancellationToken, finishOnCancellation);
    }
}
