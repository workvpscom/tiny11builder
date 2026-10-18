using System.Security.Cryptography;

namespace Tiny11Builder.Core;

/// <summary>Acquires pinned installers without taking ownership of the caller's HTTP client.</summary>
/// <param name="client">Caller-owned client with automatic redirects disabled.</param>
/// <param name="downloadTimeout">Positive, finite per-download deadline covering redirects and body verification; defaults to ten minutes.</param>
public sealed class TemplateArtifactAcquirer(HttpClient client, TimeSpan? downloadTimeout = null)
{
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(10);
    private readonly TimeSpan downloadTimeout = ValidateTimeout(downloadTimeout ?? DefaultDownloadTimeout);

    private static TimeSpan ValidateTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero && timeout.TotalMilliseconds <= uint.MaxValue - 1
        ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));

    public const string QgaUrl = "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-qemu-ga/qemu-ga-win-110.2.3-2.el10/qemu-ga-x86_64.msi";
    public const string QgaSha256 = "19DCF8ABC30F70C2EB6E87282601FD0476E4E0B19936F63D9C80BE30DB0796ED";
    /// <summary>Creates a caller-owned client; redirects are validated by the acquirer, not followed automatically.</summary>
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Returns hash-verified cache paths under the workspace; failed downloads leave no partial installer.</summary>
    /// <remarks>The supplied client must disable automatic redirects to preserve the approved-host policy.</remarks>
    public async Task<(string Cloudbase, string Qga)> AcquireAsync(string workspace, CancellationToken token)
    {
        var cache = Path.Combine(PathSafety.Full(workspace), "downloads", "template-artifacts");
        PathSafety.RejectReparsePoints(cache);
        Directory.CreateDirectory(cache);
        var cloudbase = await AcquireFileAsync(cache, "CloudbaseInitSetup_1_1_9_x64.msi",
            TemplatePayloadStager.CloudbaseUrl, TemplatePayloadStager.CloudbaseSha256, 80L << 20, token).ConfigureAwait(false);
        var qga = await AcquireFileAsync(cache, "qemu-ga-110.2.3-x64.msi", QgaUrl, QgaSha256, 32L << 20, token).ConfigureAwait(false);
        return (cloudbase, qga);
    }

    private async Task<string> AcquireFileAsync(string cache, string name, string url, string hash, long maximumBytes,
        CancellationToken token)
    {
        var path = Path.Combine(cache, name);
        PathSafety.RejectReparsePoints(path);
        if (File.Exists(path))
        {
            await VerifyAsync(path, hash, maximumBytes, token).ConfigureAwait(false);
            return path;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(downloadTimeout);
        token = deadline.Token;
        var uri = new Uri(url);
        HttpResponseMessage? response = null;
        try
        {
            for (var redirects = 0; redirects < 5; redirects++)
            {
                if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
                    uri.IdnHost is not ("github.com" or "release-assets.githubusercontent.com" or "fedorapeople.org"))
                    throw new InvalidDataException("Template artifact redirect left the approved HTTPS hosts.");
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) break;
                var next = response.Headers.Location;
                response.Dispose();
                response = null;
                if (next is null) throw new IOException("Template artifact redirect has no location.");
                uri = next.IsAbsoluteUri ? next : new(uri, next);
            }
            if (response is null) throw new IOException("Too many template artifact redirects.");
            response.EnsureSuccessStatusCode();
            var temporary = Path.Combine(cache, ".artifact-" + Guid.NewGuid().ToString("N") + ".download");
            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1 << 20, FileOptions.Asynchronous))
                {
                    var buffer = new byte[1 << 20];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > maximumBytes) throw new InvalidDataException("Template artifact exceeds the size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                }
                await VerifyAsync(temporary, hash, maximumBytes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                PathSafety.RejectReparsePoints(cache);
                File.Move(temporary, path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
        finally { response?.Dispose(); }
    }

    internal static async Task VerifyAsync(string path, string hash, long maximumBytes, CancellationToken token)
    {
        PathSafety.RejectReparsePoints(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.Asynchronous);
        if (input.Length > maximumBytes || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != hash)
            throw new InvalidDataException("Template artifact does not match the approved SHA-256 and size policy.");
    }
}
