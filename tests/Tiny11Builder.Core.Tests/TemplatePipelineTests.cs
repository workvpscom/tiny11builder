using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class TemplatePipelineTests
{
    // TM-07: Use actual pinned artifacts and the real verifier with a fake DISM boundary.
    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Template_checkpoint_preserves_payload_and_resumes_without_cache_when_configured()
    {
        var cache = TemplateArtifactFactAttribute.Require("TINY11_TEST_TEMPLATE_CACHE");
        using var fixture = new BuildFixture(build: 26300);
        // AC-S2: Use the supported fixed Core profile while exercising Setup checkpoint recovery.
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
        Assert.Equal(SetupProgress.Pending, checkpoint.SetupState);
        Assert.Contains(fixture.Runner.Calls, args => args.Contains("/Add-Driver") && args.Any(arg => arg.EndsWith("vioscsi.inf", StringComparison.Ordinal)));
        var inventory = JsonSerializer.Deserialize<TemplateGuestFile[]>(await File.ReadAllTextAsync(Path.Combine(payload, "guest-files.json")), JsonDefaults.Options)!;
        Assert.Equal(22, inventory.Length);
        foreach (var name in new[] { "vioscsi.cat", "vioscsi.inf", "vioscsi.sys", "vioscsi-LICENSE.txt" })
            Assert.Contains(inventory, file => file.Path == "drivers\\" + name);
        var manifest = JsonSerializer.Deserialize<TemplateMediaManifest>(await File.ReadAllTextAsync(Path.Combine(payload, "manifest.json")), JsonDefaults.Options)!;
        Assert.Equal(6, manifest.DriverHashes.Count);
        Assert.Equal("E49A966F7313DA4F6130DB00D84B57A167ADA0DE3D877C7CC584A7AED99C5BA2", manifest.DriverHashes["vioscsi.inf"]);
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.Report.TemplateManifestSha256));
        Assert.Contains(checkpoint.StagedMedia, file => file.RelativePath == Path.Combine("tiny11-template", "guest-files.json"));
        await VerifyDisabledTransformAsync(failure.WorkDirectory);
        Directory.Delete(Path.Combine(recipe.WorkspacePath, "downloads"), recursive: true);

        var resumedCalls = fixture.Runner.Calls.Count;
        var result = await engine.ResumeAsync(Path.Combine(failure.WorkDirectory, "job.json"), recipe, null, CancellationToken.None);
        var calls = fixture.Runner.Calls.Skip(resumedCalls).ToArray();
        var bootMounts = calls.Where(args => args.Contains("/Mount-Image")).ToArray();
        Assert.Equal(2, bootMounts.Length);
        Assert.All(bootMounts, args => Assert.Contains("/ImageFile:" + Path.Combine(failure.WorkDirectory, "setup", "boot.wim"), args));
        Assert.Contains(bootMounts, args => args.Contains("/Index:1"));
        Assert.Contains(bootMounts, args => args.Contains("/Index:2"));
        Assert.Equal(2, calls.Count(args => args.Contains("/Add-Driver") && args.Any(arg => arg.EndsWith("vioscsi.inf", StringComparison.Ordinal))));
        // Successful publication removes the resume checkpoint even when working files are retained.
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory));

        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(2, report.RootElement.GetProperty("TemplateArtifacts").GetArrayLength());
        Assert.False(report.RootElement.GetProperty("BootValidated").GetBoolean());
    }

    // TM-07: Reject manifest/payload tampering before mutation.
    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Changed_template_payload_is_refused_on_resume_when_configured()
    {
        var cache = TemplateArtifactFactAttribute.Require("TINY11_TEST_TEMPLATE_CACHE");
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

    // TM-04 / TM-T11: Semantic policy prevents publication even when media inventory is valid.
    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Invalid_initial_answer_is_refused_before_iso_creation_when_configured()
    {
        var cache = TemplateArtifactFactAttribute.Require("TINY11_TEST_TEMPLATE_CACHE");
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

    // TM-04 / TM-T13: A valid manifest must not hide invalid encoding from the actual parser.
    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Configuration_bom_is_refused_even_with_updated_manifest_when_configured()
    {
        var cache = TemplateArtifactFactAttribute.Require("TINY11_TEST_TEMPLATE_CACHE");
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

    // AC-S2: A failure after the first private commit must not alter staged media; resume retries both indexes.
    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Scsi_Setup_failure_keeps_original_boot_image_until_all_indexes_commit()
    {
        var cache = TemplateArtifactFactAttribute.Require("TINY11_TEST_TEMPLATE_CACHE");
        using var fixture = new BuildFixture(build: 26300);
        var recipe = Profile(fixture);
        SeedCache(cache, recipe.WorkspacePath);
        var bootMounts = 0;
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Mount-Image") || !args.Any(arg => arg.EndsWith("boot.wim", StringComparison.Ordinal))) return;
            Assert.Contains(args, arg => arg.EndsWith(Path.Combine("setup", "boot.wim"), StringComparison.Ordinal));
            if (++bootMounts == 2) throw new IOException("Injected second boot-index failure");
        };
        var engine = new BuildEngine(new SignatureBoundaryRunner(fixture.Runner), new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, CancellationToken.None));
        Assert.Equal(2, bootMounts);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.Source, @"sources\boot.wim")),
            await File.ReadAllBytesAsync(Path.Combine(failure.WorkDirectory, "media", @"sources\boot.wim")));
        Assert.Equal(SetupProgress.Pending, CheckpointStore.Read(failure.WorkDirectory).SetupState);
        fixture.Runner.OnCommand = null;
        var before = fixture.Runner.Calls.Count;
        await engine.ResumeAsync(Path.Combine(failure.WorkDirectory, "job.json"), recipe, null, CancellationToken.None);
        Assert.Equal(2, fixture.Runner.Calls.Skip(before).Count(args => args.Contains("/Mount-Image")));
    }

    // AC-S3: Both catalog paths must be checked, not only the serial package.
    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Untrusted_serial_catalog_prevents_payload_preparation() => await RejectCatalogAsync("vioser.cat");

    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Untrusted_scsi_catalog_prevents_payload_preparation() => await RejectCatalogAsync("vioscsi.cat");

    private static async Task RejectCatalogAsync(string rejectedCatalog)
    {
        var cache = TemplateArtifactFactAttribute.Require("TINY11_TEST_TEMPLATE_CACHE");
        using var fixture = new BuildFixture(build: 26300);
        SeedCache(cache, fixture.Recipe.WorkspacePath);
        var job = Path.Combine(fixture.Recipe.WorkspacePath, "tiny11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        await File.WriteAllTextAsync(Path.Combine(job, "job.json"), "{}");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new TemplatePayloadStager(new CatalogBoundaryRunner(rejectedCatalog))
            .PrepareMediaAsync(fixture.Recipe.WorkspacePath, job, Path.Combine(job, "media"), CancellationToken.None));
        Assert.Contains("catalog is not trusted", error.Message, StringComparison.Ordinal);
    }

    // AC-S1: Rehashing a tampered storage package cannot bypass the independent package pins.
    [TemplateArtifactFact("TINY11_TEST_TEMPLATE_CACHE")]
    public async Task Rehashed_scsi_payload_is_refused_by_pinned_policy()
    {
        var cache = TemplateArtifactFactAttribute.Require("TINY11_TEST_TEMPLATE_CACHE");
        using var fixture = new BuildFixture(build: 26300);
        SeedCache(cache, fixture.Recipe.WorkspacePath);
        var job = Path.Combine(fixture.Recipe.WorkspacePath, "tiny11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        await File.WriteAllTextAsync(Path.Combine(job, "job.json"), "{}");
        var media = Path.Combine(job, "media");
        var receipts = await new TemplatePayloadStager(new SignatureBoundaryRunner(fixture.Runner))
            .PrepareMediaAsync(fixture.Recipe.WorkspacePath, job, media, CancellationToken.None);
        var root = Path.Combine(media, "tiny11-template");
        var driver = Path.Combine(root, "drivers", "vioscsi.inf");
        await File.AppendAllTextAsync(driver, "tamper");
        var manifestPath = Path.Combine(root, "guest-files.json");
        var entries = JsonSerializer.Deserialize<TemplateGuestFile[]>(await File.ReadAllTextAsync(manifestPath), JsonDefaults.Options)!;
        var changedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(driver)));
        entries = entries.Select(entry => entry.Path == "drivers\\vioscsi.inf"
            ? entry with { Sha256 = changedHash }
            : entry).ToArray();
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(entries, JsonDefaults.Options));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(manifestPath)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => TemplatePayloadStager.ValidateMediaAsync(media, receipts, hash, CancellationToken.None));
        Assert.Contains("pinned policy", error.Message, StringComparison.Ordinal);
    }

    private sealed class CatalogBoundaryRunner(string rejectedCatalog) : IProcessRunner
    {
        private readonly ProcessRunner real = new();
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output,
            CancellationToken cancellationToken, bool finishOnCancellation = true)
        {
            var args = arguments.ToArray();
            var encoded = Array.IndexOf(args, "-EncodedCommand");
            if (encoded >= 0)
            {
                var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args[encoded + 1]));
                if (script.Contains("vioser.cat", StringComparison.Ordinal))
                {
                    var boundary = "function Get-AuthenticodeSignature { param($LiteralPath); [pscustomobject]@{Status=$(if($LiteralPath.EndsWith('" + rejectedCatalog + "')){'NotTrusted'}else{'Valid'});SignerCertificate=[pscustomobject]@{Thumbprint='FAC666005546D6BE881A31C1267717879401A950'}} }; ";
                    args[encoded + 1] = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(boundary + script));
                }
            }
            return real.RunAsync(executable, args, output, cancellationToken, finishOnCancellation);
        }
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
