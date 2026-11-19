using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Legado.UI3.Core;

internal sealed record UpdateRelease(Version Version, string FileName, Uri Url, long Size, string Sha256);

internal static class Updates
{
    private const string Repo = "ShenShenSshen/Legado.UI3";
    internal const string ReleasePage = "https://github.com/" + Repo + "/releases";
    private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    internal static readonly Version CurrentVersion = new(typeof(Updates).Assembly.GetName().Version!.ToString(3));
    private static readonly HttpClient Client = new(new HttpClientHandler { UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { UserAgent = { new("Legado.UI3", CurrentVersion.ToString()) } }
    };

    internal static async Task<UpdateRelease> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Client.GetAsync(LatestApi, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("GitHub 暂无可查询的正式版本。");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return Parse(JsonNode.Parse(await Network.ReadLimitedAsync(stream, 1024 * 1024, timeout.Token)));
    }

    internal static UpdateRelease Parse(JsonNode? release)
    {
        var tag = release.S("tag_name");
        if (release.B("draft") || release.B("prerelease") || !tag.StartsWith('v') ||
            !Version.TryParse(tag[1..], out var version) || version.Build < 0 || version.Revision >= 0 || tag != "v" + version)
            throw new InvalidDataException("GitHub 未返回有效的正式版本号。");
        var name = $"Legado.UI3-{tag}-win-x64.zip";
        var url = $"{ReleasePage}/download/{tag}/{name}";
        var asset = release?["assets"].Objects().FirstOrDefault(item => item.S("name") == name);
        if (asset is null || asset.S("browser_download_url") != url)
            throw new InvalidDataException("该版本暂未提供项目的 Windows x64 下载包。");
        var digest = asset.S("digest");
        if (asset["size"] is not JsonValue number || !number.TryGetValue<long>(out var size) ||
            size <= 0 || size > 1024L * 1024 * 1024 || digest.Length != 71 ||
            !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("GitHub 未提供有效的下载包大小和 SHA-256。");
        return new(version, name, new(url), size, digest[7..]);
    }

    internal static async Task DownloadAsync(UpdateRelease release, string path, IProgress<string> status,
        CancellationToken token, HttpClient? client = null)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
        overall.CancelAfter(TimeSpan.FromMinutes(15));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        var sources = new[]
        {
            ("国内加速一", new Uri("https://gh-proxy.org/" + release.Url.AbsoluteUri)),
            ("国内加速二", new Uri("https://ghfast.top/" + release.Url.AbsoluteUri)),
            ("GitHub 原站", release.Url)
        };
        try
        {
            foreach (var (label, url) in sources)
            {
                overall.Token.ThrowIfCancellationRequested();
                status.Report($"{label} · 正在连接…");
                try { await FetchPackageAsync(client ?? Client, release, url, temporary, label, status, overall.Token); }
                catch (Exception error) when (!overall.IsCancellationRequested &&
                    error is HttpRequestException or InvalidDataException or OperationCanceledException) { continue; }
                overall.Token.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
                return;
            }
            throw new IOException("下载线路均未返回通过校验的文件，可稍后重试或打开 GitHub 发布页。");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task FetchPackageAsync(HttpClient client, UpdateRelease release, Uri url, string path,
        string label, IProgress<string> status, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("下载包大小与 GitHub 发布信息不符。");
        idle.CancelAfter(TimeSpan.FromSeconds(20));
        await using var input = await response.Content.ReadAsStreamAsync(idle.Token);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long received = 0;
        var previous = -1;
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(20));
            int count;
            try { count = await input.ReadAsync(buffer, idle.Token); }
            catch (IOException error) { throw new HttpRequestException("下载连接中断。", error); }
            if (count == 0) break;
            received += count;
            if (received > release.Size) throw new InvalidDataException("下载包超出发布大小。");
            hash.AppendData(buffer, 0, count);
            await file.WriteAsync(buffer.AsMemory(0, count), idle.Token);
            var percent = (int)(received * 100 / release.Size);
            if (percent != previous) { status.Report($"{label} · {percent}%"); previous = percent; }
        }
        if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载包未通过 SHA-256 校验。");
    }
}
