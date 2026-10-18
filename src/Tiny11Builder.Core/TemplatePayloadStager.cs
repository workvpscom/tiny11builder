using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tiny11Builder.Core;

public sealed record TemplateArtifactReceipt(string Name, string Version, string Sha256, string Publisher, string SourceUrl,
    string SignerSubject, string SignerThumbprint, string TrustPolicy = "AuthenticodePublisherAndSha256");

internal sealed record TemplateGuestFile(string Path, string Sha256);
internal sealed record TemplateMediaManifest(int SchemaVersion, TemplateProfile Profile,
    IReadOnlyList<TemplateArtifactReceipt> Artifacts, IReadOnlyDictionary<string, string> DriverHashes,
    string CloneOobePolicy = "OperatorApprovedSkipMachineAndUserOobeRequiresBuildSpecificValidation");

/// <summary>Stages verified template payloads into an existing build job, never into a running guest.</summary>
/// <param name="runner">Caller-owned runner; template verification children may be terminated on cancellation.</param>
/// <param name="subprocessTimeout">Positive, finite per-child deadline; defaults to two minutes. Does not affect DISM.</param>
public sealed class TemplatePayloadStager(IProcessRunner runner, TimeSpan? subprocessTimeout = null)
{
    private static readonly TimeSpan DefaultSubprocessTimeout = TimeSpan.FromMinutes(2);
    private readonly TimeSpan subprocessTimeout = ValidateTimeout(subprocessTimeout ?? DefaultSubprocessTimeout);

    private static TimeSpan ValidateTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero && timeout.TotalMilliseconds <= uint.MaxValue - 1
        ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));

    public const string CloudbaseVersion = "1.1.9";
    public const string CloudbaseSha256 = "FFBF80515A7311ED00800276A6AF7AAB23B41D7ACF13E79CE3D2D56844F638F7";
    public const string CloudbasePublisher = "Cloudbase Solutions Srl";
    public const string CloudbaseUrl = "https://github.com/cloudbase/cloudbase-init/releases/download/1.1.9/CloudbaseInitSetup_1_1_9_x64.msi";

    /// <summary>Populates this job's media directory with verified installers, guest helpers, and integrity manifests.</summary>
    /// <remarks>Requires an existing job.json and Windows trust verification. Partial job files are retained for diagnosis on failure.</remarks>
    public async Task<IReadOnlyList<TemplateArtifactReceipt>> PrepareMediaAsync(string workspace, string jobDirectory,
        string mediaDirectory, CancellationToken token)
    {
        if (!PathSafety.IsInside(mediaDirectory, jobDirectory) || Path.GetFileName(mediaDirectory) != "media")
            throw new InvalidDataException("Template media must belong to this build job.");
        using var client = TemplateArtifactAcquirer.CreateClient();
        var files = await new TemplateArtifactAcquirer(client).AcquireAsync(workspace, token).ConfigureAwait(false);
        var cloudbase = await StageCloudbaseAsync(files.Cloudbase, jobDirectory, token).ConfigureAwait(false);
        var payload = Path.Combine(mediaDirectory, "tiny11-template");
        PathSafety.RejectReparsePoints(payload);
        Directory.CreateDirectory(payload);
        File.Copy(Path.Combine(jobDirectory, "template-payload", "CloudbaseInitSetup_1_1_9_x64.msi"),
            Path.Combine(payload, "CloudbaseInitSetup_1_1_9_x64.msi"), overwrite: false);
        await TemplateArtifactAcquirer.VerifyAsync(Path.Combine(payload, "CloudbaseInitSetup_1_1_9_x64.msi"),
            CloudbaseSha256, 80L << 20, token).ConfigureAwait(false);
        await TemplateArtifactAcquirer.VerifyAsync(files.Qga, TemplateArtifactAcquirer.QgaSha256, 32L << 20, token).ConfigureAwait(false);
        var qgaDestination = Path.Combine(payload, "qemu-ga-x86_64.msi");
        File.Copy(files.Qga, qgaDestination, overwrite: false);
        await TemplateArtifactAcquirer.VerifyAsync(qgaDestination, TemplateArtifactAcquirer.QgaSha256, 32L << 20, token).ConfigureAwait(false);
        IReadOnlyList<TemplateArtifactReceipt> receipts = [cloudbase,
            new("QEMU Guest Agent", "110.2.3-2.el10", TemplateArtifactAcquirer.QgaSha256, "", TemplateArtifactAcquirer.QgaUrl,
                "", "", "OperatorApprovedUpstreamHttpsAndSha256UnsignedMsi")];
        await File.WriteAllTextAsync(Path.Combine(payload, "manifest.json"),
            JsonSerializer.Serialize(new TemplateMediaManifest(1, TemplateProfile.ProxmoxCloudbaseInit, receipts, DriverHashes),
                JsonDefaults.Options), token).ConfigureAwait(false);
        await WriteTextAsync("cloudbase-init.conf", TemplateGuestAssets.MainConfiguration);
        await WriteTextAsync("cloudbase-init-unattend.conf", TemplateGuestAssets.UnattendConfiguration);
        await WriteTextAsync("Prepare-Template.ps1", TemplateGuestAssets.PrepareScript);
        await WriteTextAsync("Finalize-Template.ps1", TemplateGuestAssets.FinalizeScript);
        await WriteTextAsync("Initialize-Clone.ps1", TemplateGuestAssets.CloneScript);
        await WriteTextAsync("tiny11_cloudbase.py", TemplateGuestAssets.ReconcilePlugin);
        await WriteTextAsync("Configure-GuestPolicy.ps1", TemplateGuestAssets.PolicyScript);
        await WriteTextAsync("Template-State.ps1", TemplateGuestAssets.StateScript);
        await WriteTextAsync("preparation-profile.json", "{\"SchemaVersion\":1,\"Profile\":\"ProxmoxCloudbaseInit\",\"Lifecycle\":\"AuditUser\"}");
        await WriteTextAsync("template-unattend.xml", TemplateGuestAssets.FinalAnswerFile());
        await CreateTransformAsync(jobDirectory, payload, token).ConfigureAwait(false);
        var drivers = Path.Combine(payload, "drivers");
        Directory.CreateDirectory(drivers);
        foreach (var name in new[] { "vioser.cat", "vioser.inf", "vioser.sys", "LICENSE.txt" })
        {
            var bytes = TemplateGuestAssets.Driver(name);
            if (DriverHashes.TryGetValue(name, out var expected) && Digest.HashBytes(bytes) != expected)
                throw new InvalidDataException("Embedded driver bytes do not match the approved catalog package.");
            await File.WriteAllBytesAsync(Path.Combine(drivers, name), bytes, token).ConfigureAwait(false);
        }
        await VerifyDriverCatalogAsync(drivers, token).ConfigureAwait(false);
        var guestFiles = new List<TemplateGuestFile>();
        foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var (_, sha) = await Digest.HashAsync(path, null, token).ConfigureAwait(false);
            guestFiles.Add(new(Path.GetRelativePath(payload, path).Replace(Path.DirectorySeparatorChar, '\\'), sha));
        }
        await WriteTextAsync("guest-files.json", JsonSerializer.Serialize(guestFiles, JsonDefaults.Options));
        return receipts;

        Task WriteTextAsync(string name, string text) => File.WriteAllTextAsync(Path.Combine(payload, name), text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)), token);
    }

    internal async Task CreateTransformAsync(string jobDirectory, string payload, CancellationToken token)
    {
        var transformScript = Path.Combine(jobDirectory, "create-transform.ps1");
        await File.WriteAllTextAsync(transformScript, TemplateGuestAssets.TransformScript, new UTF8Encoding(true), token).ConfigureAwait(false);
        var transformWork = Path.Combine(jobDirectory, "template-transform");
        Directory.CreateDirectory(transformWork);
        var preparedTransform = Path.Combine(transformWork, "CloudbaseDisabled.mst");
        var diagnosticPath = Path.Combine(jobDirectory, "template-transform.log");
        using var diagnostic = new StreamWriter(diagnosticPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(subprocessTimeout);
        var transformResult = await runner.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", transformScript,
                "-Source", Path.Combine(payload, "CloudbaseInitSetup_1_1_9_x64.msi"), "-Destination", preparedTransform],
            line => { lock (diagnostic) diagnostic.WriteLine(line); }, deadline.Token, finishOnCancellation: false).ConfigureAwait(false);
        if (transformResult.ExitCode != 0)
            throw new InvalidDataException($"MSI transform failed with exit code {transformResult.ExitCode}. Diagnostic: {diagnosticPath}");
        deadline.Token.ThrowIfCancellationRequested();
        File.Copy(preparedTransform, Path.Combine(payload, "CloudbaseDisabled.mst"), overwrite: false);
    }

    internal static void ValidateConfigurationEncoding(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith("[DEFAULT]"u8))
            throw new InvalidDataException("Cloudbase configuration must start with [DEFAULT] in UTF-8 without BOM.");
        try { _ = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Cloudbase configuration is not valid UTF-8.", error);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> DriverHashes = new Dictionary<string, string>
    {
        ["vioser.cat"] = "1E93A77AA320F515E6998DA233FA6F1B0E1DA9409EB7AACEDCFD4D21DF255E4D",
        ["vioser.inf"] = "4A8FCFDEEDBF05BF0B02535ACE5156D5DB5F1C229D30532F88AE477ADF82BEED",
        ["vioser.sys"] = "B6767019C660903B59A3BB8396C40EDA2DE3CDA51CCC121B229050AE28B619FF"
    };

    internal static async Task ValidateMediaAsync(string media, IReadOnlyList<TemplateArtifactReceipt> receipts,
        string expectedManifestHash, CancellationToken token)
    {
        var root = Path.Combine(media, "tiny11-template");
        PathSafety.RejectReparsePoints(root);
        var manifestPath = Path.Combine(root, "guest-files.json");
        if ((await Digest.HashAsync(manifestPath, null, token).ConfigureAwait(false)).Sha256 != expectedManifestHash)
            throw new InvalidDataException("Template guest manifest does not match the saved identity.");
        var files = JsonSerializer.Deserialize<TemplateGuestFile[]>(await File.ReadAllTextAsync(manifestPath, token), JsonDefaults.Options)
            ?? throw new InvalidDataException("Template guest manifest is empty.");
        string[] required = ["CloudbaseInitSetup_1_1_9_x64.msi", "qemu-ga-x86_64.msi", "manifest.json",
            "cloudbase-init.conf", "cloudbase-init-unattend.conf", "Prepare-Template.ps1", "Finalize-Template.ps1", "Template-State.ps1",
            "Initialize-Clone.ps1", "tiny11_cloudbase.py", "Configure-GuestPolicy.ps1", "template-unattend.xml", "preparation-profile.json", "CloudbaseDisabled.mst", "drivers\\vioser.cat", "drivers\\vioser.inf", "drivers\\vioser.sys", "drivers\\LICENSE.txt"];
        if (files.Length != required.Length || !files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal).SetEquals(required))
            throw new InvalidDataException("Template guest manifest inventory mismatch. Missing: " +
                string.Join(", ", required.Except(files.Select(f => f.Path), StringComparer.Ordinal)) + "; Unexpected: " +
                string.Join(", ", files.Select(f => f.Path).Except(required, StringComparer.Ordinal)) + "; Duplicate: " +
                string.Join(", ", files.GroupBy(f => f.Path, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key)));
        foreach (var file in files)
        {
            var path = Path.Combine(root, file.Path.Replace('\\', Path.DirectorySeparatorChar));
            PathSafety.RejectReparsePoints(path);
            if (!PathSafety.IsInside(path, root) || (await Digest.HashAsync(path, null, token).ConfigureAwait(false)).Sha256 != file.Sha256)
                throw new InvalidDataException("Template guest payload differs from its manifest.");
        }
        foreach (var name in new[] { "cloudbase-init.conf", "cloudbase-init-unattend.conf" })
            ValidateConfigurationEncoding(await File.ReadAllBytesAsync(Path.Combine(root, name), token).ConfigureAwait(false));
        TemplateContractValidator.ValidateFinalAnswer(await File.ReadAllTextAsync(Path.Combine(root, "template-unattend.xml"), token).ConfigureAwait(false));
        TemplateContractValidator.ValidateConfiguration(await File.ReadAllTextAsync(Path.Combine(root, "cloudbase-init.conf"), token).ConfigureAwait(false), main: true);
        TemplateContractValidator.ValidateConfiguration(await File.ReadAllTextAsync(Path.Combine(root, "cloudbase-init-unattend.conf"), token).ConfigureAwait(false), main: false);
        string Hash(string path) => files.Single(f => f.Path == path).Sha256;
        if (Hash("CloudbaseInitSetup_1_1_9_x64.msi") != CloudbaseSha256 || Hash("qemu-ga-x86_64.msi") != TemplateArtifactAcquirer.QgaSha256 ||
            DriverHashes.Any(pair => Hash("drivers\\" + pair.Key) != pair.Value))
            throw new InvalidDataException("Template artifact does not match the pinned policy.");
        var manifest = JsonSerializer.Deserialize<TemplateMediaManifest>(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), token), JsonDefaults.Options)
            ?? throw new InvalidDataException("Template artifact manifest is missing.");
        if (manifest.SchemaVersion != 1 || manifest.Profile != TemplateProfile.ProxmoxCloudbaseInit || receipts.Count != 2 ||
            !manifest.Artifacts.SequenceEqual(receipts) || manifest.DriverHashes.Count != DriverHashes.Count ||
            DriverHashes.Any(pair => !manifest.DriverHashes.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidDataException("Template profile, artifact receipts and manifest are inconsistent.");
        var cloudbase = receipts.SingleOrDefault(r => r.Name == "Cloudbase-Init");
        var qga = receipts.SingleOrDefault(r => r.Name == "QEMU Guest Agent");
        if (cloudbase is null || qga is null || cloudbase.Version != CloudbaseVersion || cloudbase.Sha256 != CloudbaseSha256 ||
            cloudbase.SourceUrl != CloudbaseUrl || cloudbase.Publisher != CloudbasePublisher ||
            cloudbase.TrustPolicy != "AuthenticodePublisherAndSha256" || string.IsNullOrWhiteSpace(cloudbase.SignerThumbprint) ||
            !cloudbase.SignerSubject.Contains("O=Cloudbase Solutions Srl", StringComparison.Ordinal) ||
            qga.Sha256 != TemplateArtifactAcquirer.QgaSha256 || qga.SourceUrl != TemplateArtifactAcquirer.QgaUrl ||
            qga.Version != "110.2.3-2.el10" || qga.TrustPolicy != "OperatorApprovedUpstreamHttpsAndSha256UnsignedMsi")
            throw new InvalidDataException("Template artifact receipts do not satisfy the approved policy.");
    }

    private async Task VerifyDriverCatalogAsync(string drivers, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Driver verification requires Windows.");
        var escaped = Path.Combine(drivers, "vioser.cat").Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; $s=Get-AuthenticodeSignature -LiteralPath '" + escaped +
            "'; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Thumbprint -ne 'FAC666005546D6BE881A31C1267717879401A950'){exit 1}; exit 0";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(subprocessTimeout);
        var result = await runner.RunAsync(executable,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            null, deadline.Token, finishOnCancellation: false).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidDataException("Template serial driver catalog is not trusted.");
    }

    internal static async Task CopyIntoImageAsync(string media, string mount, CancellationToken token)
    {
        var source = Path.Combine(media, "tiny11-template");
        var destination = Path.Combine(mount, "ProgramData", "Tiny11Template");
        PathSafety.RejectReparsePoints(destination);
        if (Directory.Exists(destination)) throw new IOException("Template guest payload destination already exists.");
        var manifest = JsonSerializer.Deserialize<TemplateGuestFile[]>(await File.ReadAllTextAsync(Path.Combine(source, "guest-files.json"), token), JsonDefaults.Options)
            ?? throw new InvalidDataException("Template guest manifest is missing.");
        Directory.CreateDirectory(destination);
        foreach (var entry in manifest.Append(new TemplateGuestFile("guest-files.json", (await Digest.HashAsync(Path.Combine(source, "guest-files.json"), null, token)).Sha256)))
        {
            var relative = entry.Path.Replace('\\', Path.DirectorySeparatorChar);
            var input = Path.Combine(source, relative);
            var output = Path.Combine(destination, relative);
            if (!PathSafety.IsInside(output, destination) || !PathSafety.IsInside(input, source))
                throw new InvalidDataException("Template guest manifest path escaped payload.");
            PathSafety.RejectReparsePoints(input);
            PathSafety.RejectReparsePoints(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(input, output, overwrite: false);
            var (_, sha) = await Digest.HashAsync(output, null, token).ConfigureAwait(false);
            if (sha != entry.Sha256) throw new InvalidDataException("Template payload changed during image staging.");
        }
    }

    /// <summary>Verifies the pinned MSI and Windows publisher before publishing a job-local installer copy.</summary>
    /// <remarks>Requires a tiny11-{GUID:N} job directory containing job.json. Existing payloads are never overwritten.</remarks>
    public async Task<TemplateArtifactReceipt> StageCloudbaseAsync(string source, string jobDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = PathSafety.Full(source);
        jobDirectory = PathSafety.Full(jobDirectory);
        PathSafety.RejectReparsePoints(source);
        PathSafety.RejectReparsePoints(jobDirectory);
        var leaf = Path.GetFileName(jobDirectory);
        if (!leaf.StartsWith("tiny11-", StringComparison.Ordinal) || !Guid.TryParseExact(leaf[7..], "N", out _) ||
            !File.Exists(Path.Combine(jobDirectory, "job.json")))
            throw new InvalidDataException("Template payload must belong to an existing build job.");
        var destination = Path.Combine(jobDirectory, "template-payload");
        PathSafety.RejectReparsePoints(destination);
        var final = Path.Combine(destination, "CloudbaseInitSetup_1_1_9_x64.msi");
        if (File.Exists(final)) throw new IOException("Template installer destination already exists.");
        // Deny writes/deletes while verifying the signature and copying on Windows.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        if (digest != CloudbaseSha256)
            throw new InvalidDataException("Cloudbase-Init installer does not match the approved release SHA-256.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cloudbase-Init Authenticode verification requires Windows.");
        var escaped = source.Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; $s=Get-AuthenticodeSignature -LiteralPath '" + escaped +
            "'; if($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate){exit 1}; " +
            "if($s.SignerCertificate.Subject -notmatch '(?:^|,\\s*)O=Cloudbase Solutions Srl(?:,|$)'){exit 2}; " +
            "@{Subject=$s.SignerCertificate.Subject;Thumbprint=$s.SignerCertificate.Thumbprint}|ConvertTo-Json -Compress; exit 0";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(subprocessTimeout);
        var result = await runner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            null, deadline.Token, finishOnCancellation: false).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidDataException("Cloudbase-Init installer signature or publisher is not trusted.");
        using var signature = JsonDocument.Parse(result.StandardOutput);
        var subject = signature.RootElement.GetProperty("Subject").GetString();
        var thumbprint = signature.RootElement.GetProperty("Thumbprint").GetString();
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidDataException("Cloudbase-Init signature evidence is incomplete.");
        token.ThrowIfCancellationRequested();
        PathSafety.RejectReparsePoints(destination);
        Directory.CreateDirectory(destination);
        PathSafety.RejectReparsePoints(destination);
        var temporary = Path.Combine(destination, ".cloudbase-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            await using (var copy = File.OpenRead(temporary))
                if (Convert.ToHexString(await SHA256.HashDataAsync(copy, token).ConfigureAwait(false)) != CloudbaseSha256)
                    throw new InvalidDataException("Staged Cloudbase-Init installer hash changed during copy.");
            token.ThrowIfCancellationRequested();
            PathSafety.RejectReparsePoints(destination);
            File.Move(temporary, final, overwrite: false);
            return new("Cloudbase-Init", CloudbaseVersion, CloudbaseSha256, CloudbasePublisher, CloudbaseUrl,
                subject, thumbprint);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
