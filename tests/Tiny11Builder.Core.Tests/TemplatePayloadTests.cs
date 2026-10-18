using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplatePayloadTests
{
    // TM-03 / TM-T04: cancellation trước verify không tạo payload.
    [Fact]
    public async Task Cancelled_staging_creates_no_payload()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var stager = new TemplatePayloadStager(new ProcessRunner());

        await Assert.ThrowsAsync<OperationCanceledException>(() => stager.StageCloudbaseAsync(
            "unused.msi", "unused-job", cancellation.Token));
    }

    // TM-04 / TM-T05: không ghi đè installer đã có trong owned job.
    [Fact]
    public async Task Existing_payload_is_never_overwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "template-payload"));
        try
        {
            File.WriteAllText(Path.Combine(root, "job.json"), "{}");
            var existing = Path.Combine(root, "template-payload", "CloudbaseInitSetup_1_1_9_x64.msi");
            File.WriteAllText(existing, "keep-existing");
            var stager = new TemplatePayloadStager(new ProcessRunner());

            await Assert.ThrowsAsync<IOException>(() => stager.StageCloudbaseAsync(
                Path.Combine(root, "missing.msi"), root, CancellationToken.None));

            Assert.Equal("keep-existing", File.ReadAllText(existing));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // TM-04 / TM-T05: opt-in Windows test dùng đúng publisher artifact thật.
    [Fact]
    public async Task Approved_cloudbase_installer_stages_with_signature_receipt_when_configured()
    {
        var source = Environment.GetEnvironmentVariable("TINY11_TEST_CLOUDBASE_MSI");
        if (string.IsNullOrEmpty(source)) return;
        Assert.True(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "tiny11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "job.json"), "{}");
            var receipt = await new TemplatePayloadStager(new ProcessRunner()).StageCloudbaseAsync(
                source, root, CancellationToken.None);
            var staged = Path.Combine(root, "template-payload", "CloudbaseInitSetup_1_1_9_x64.msi");

            Assert.Equal("1.1.9", receipt.Version);
            Assert.Equal("Cloudbase Solutions Srl", receipt.Publisher);
            Assert.Contains("O=Cloudbase Solutions Srl", receipt.SignerSubject, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(receipt.SignerThumbprint));
            Assert.Equal("FFBF80515A7311ED00800276A6AF7AAB23B41D7ACF13E79CE3D2D56844F638F7",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(staged))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // TM-03 / TM-T04: bytes không đúng pin publisher không được stage.
    [Fact]
    public async Task Corrupt_cloudbase_installer_is_rejected_before_staging()
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-payload-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "corrupt.msi");
            var target = Path.Combine(root, "tiny11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "job.json"), "{}");
            File.WriteAllText(source, "not the approved Cloudbase installer");
            var stager = new TemplatePayloadStager(new ProcessRunner());

            await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageCloudbaseAsync(source, target, CancellationToken.None));

            Assert.False(Directory.Exists(Path.Combine(target, "template-payload")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
