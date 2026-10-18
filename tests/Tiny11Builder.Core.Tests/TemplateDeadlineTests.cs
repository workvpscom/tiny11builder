using System.Net;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class TemplateDeadlineTests
{
    // AC-1: The public acquisition boundary must bound a stalled response body.
    [Fact]
    public async Task Stalled_body_times_out_without_publishing_or_leaving_temporary_files()
    {
        var temporaryRoot = Path.GetTempPath();
        if (!OperatingSystem.IsWindows()) temporaryRoot = new DirectoryInfo(temporaryRoot).ResolveLinkTarget(true)?.FullName ?? temporaryRoot;
        var root = Path.Combine(temporaryRoot, "tiny11-download-" + Guid.NewGuid().ToString("N"));
        using var handler = new StalledHandler();
        using var client = new HttpClient(handler);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var acquirer = new TemplateArtifactAcquirer(client, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquirer.AcquireAsync(root, watchdog.Token));
            Assert.False(watchdog.IsCancellationRequested, "The caller watchdog, not the download deadline, stopped acquisition.");
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "downloads", "template-artifacts")));
            Assert.True(handler.Body.Disposed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // AC-2: Only the template child is cancellable; the runner must receive a deadline.
    [Fact]
    public async Task Transform_timeout_cancels_runner_and_retains_partial_diagnostic()
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-transform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runner = new TransformRunner(stall: true);
        try
        {
            var stager = new TemplatePayloadStager(runner, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stager.CreateTransformAsync(root, root, CancellationToken.None));
            Assert.False(runner.FinishOnCancellation);
            Assert.Contains("private diagnostic", await File.ReadAllTextAsync(Path.Combine(root, "template-transform.log")));
            Assert.False(File.Exists(Path.Combine(root, "CloudbaseDisabled.mst")));
        }
        finally { Directory.Delete(root, true); }
    }

    // AC-3: Child output belongs in the job diagnostic, never in the exception.
    [Fact]
    public async Task Transform_failure_reports_exit_code_and_diagnostic_path_without_raw_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "tiny11-transform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new TemplatePayloadStager(new TransformRunner(false))
                .CreateTransformAsync(root, root, CancellationToken.None));
            var log = Path.Combine(root, "template-transform.log");
            Assert.Contains("37", error.Message);
            Assert.Contains(log, error.Message);
            Assert.DoesNotContain("private diagnostic", error.ToString());
            Assert.Contains("private diagnostic", await File.ReadAllTextAsync(log));
            Assert.False(File.Exists(Path.Combine(root, "CloudbaseDisabled.mst")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TransformRunner(bool stall) : IProcessRunner
    {
        public bool FinishOnCancellation { get; private set; } = true;
        public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output,
            CancellationToken cancellationToken, bool finishOnCancellation = true)
        {
            FinishOnCancellation = finishOnCancellation;
            output?.Invoke("private diagnostic");
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5));
            return new(37, "private diagnostic");
        }
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        public StalledStream Body { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) });
    }

    private sealed class StalledStream : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
