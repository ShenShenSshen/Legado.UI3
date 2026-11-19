using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Legado.UI3.Core;

public sealed class Network : IDisposable
{
    static Network() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private readonly HttpClient client;
    public const int Limit = 24 * 1024 * 1024;
    public Network()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false, UseCookies = true };
        client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Legado.UI3/1.0");
    }

    public static Uri WebUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("只允许 HTTP/HTTPS 网络地址");
        return uri;
    }

    public async Task<JsonObject> FetchAsync(JsonObject options, CancellationToken cancellation = default, bool httpsOnly = false)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(Math.Clamp(options.I("timeout", 20000), 1000, 60000));
            var uri = WebUri(options.S("url"));
            var original = uri;
            var method = options.S("method", "GET").ToUpperInvariant();
            if (method is not ("GET" or "POST" or "HEAD")) throw new InvalidDataException("不支持的书源请求方法");
            var body = options.S("body");
            for (int redirects = 0; redirects <= 8; redirects++)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), uri);
                if (method == "POST") request.Content = new StringContent(body, Encoding.UTF8);
                if (options["headers"] is JsonObject headers)
                    foreach (var (key, value) in headers)
                    {
                        if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                        if (uri.Authority != original.Authority && (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))) continue;
                        var text = value?.ToString() ?? "";
                        if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content is not null)
                        { request.Content.Headers.Remove(key); request.Content.Headers.TryAddWithoutValidation(key, text); }
                        else request.Headers.TryAddWithoutValidation(key, text);
                    }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    uri = WebUri(new Uri(uri, location).AbsoluteUri);
                    if (httpsOnly && uri.Scheme != "https") throw new InvalidDataException("导入链接重定向到了非 HTTPS 地址");
                    if (status == 303 || (status is 301 or 302 && method == "POST")) { method = "GET"; body = ""; }
                    continue;
                }
                if (response.Content.Headers.ContentLength > Limit) throw new InvalidDataException("响应文件过大");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = await ReadLimitedAsync(stream, Limit, timeout.Token);
                var charset = options.S("charset", "auto");
                if (charset == "auto") charset = response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "";
                var textContent = Decode(bytes, charset);
                return new() { ["ok"] = true, ["status"] = status, ["url"] = uri.AbsoluteUri, ["text"] = textContent };
            }
            throw new InvalidDataException("网络重定向次数过多");
        }
        catch (OperationCanceledException) { return new() { ["ok"] = false, ["error"] = cancellation.IsCancellationRequested ? "已取消" : "请求超时" }; }
        catch (Exception e) { return new() { ["ok"] = false, ["error"] = e.Message }; }
    }

    public async Task<string> DownloadSourceAsync(string input, CancellationToken token)
    {
        var uri = SourceImport.ResolveUrl(input);
        var result = await FetchAsync(new() { ["url"] = uri.AbsoluteUri, ["timeout"] = 30000 }, token, httpsOnly: true);
        token.ThrowIfCancellationRequested();
        if (!result.B("ok") || result.I("status") >= 400) throw new IOException(result.S("error", $"HTTP {result.I("status")}"));
        return result.S("text");
    }

    public static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken token = default)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("文件超过允许大小");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }

    public static string Decode(byte[] bytes, string charset = "")
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (!string.IsNullOrWhiteSpace(charset) && charset != "auto")
        { try { return Encoding.GetEncoding(charset).GetString(bytes); } catch (ArgumentException) { } }
        var head = Encoding.ASCII.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        var match = Regex.Match(head, "charset\\s*=\\s*[\"']?([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
        if (match.Success) { try { return Encoding.GetEncoding(match.Groups[1].Value).GetString(bytes); } catch (ArgumentException) { } }
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding("gb18030").GetString(bytes); }
    }
    public void Dispose() => client.Dispose();
}
