using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Legado.UI3.Core;

public sealed class WebDav(JsonObject settings) : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
    private Uri Root
    {
        get
        {
            var uri = Network.WebUri(settings.S("url").TrimEnd('/') + "/");
            if (uri.Scheme != "https" && !uri.IsLoopback) throw new InvalidDataException("WebDAV 请使用 HTTPS 地址");
            return uri;
        }
    }
    private Uri DirectoryUri => new(Root, string.Join('/', settings.S("dir", "legado").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + "/");
    private async Task<HttpResponseMessage> SendAsync(string method, Uri uri, HttpContent? content = null, string? depth = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.S("user") + ":" + settings.S("pass"))));
        if (depth is not null) request.Headers.TryAddWithoutValidation("Depth", depth);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new IOException($"WebDAV HTTP {status}"); }
        return response;
    }
    public async Task TestAsync() { using var response = await SendAsync("PROPFIND", Root, depth: "0"); }
    public async Task EnsureDirectoryAsync()
    {
        var current = Root;
        foreach (var part in settings.S("dir", "legado").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = new Uri(current, Uri.EscapeDataString(part) + "/");
            try { using var response = await SendAsync("MKCOL", current); }
            catch (IOException e) when (e.Message.Contains("405")) { }
        }
    }
    public async Task<List<(string Name, DateTimeOffset Modified)>> ListAsync()
    {
        await EnsureDirectoryAsync();
        using var response = await SendAsync("PROPFIND", DirectoryUri, depth: "1");
        await using var stream = await response.Content.ReadAsStreamAsync();
        var xml = XDocument.Parse(Network.Decode(await Network.ReadLimitedAsync(stream, 4 * 1024 * 1024)));
        XNamespace d = "DAV:";
        return xml.Descendants(d + "response").Select(row =>
        {
            var href = row.Element(d + "href")?.Value ?? "";
            var name = Uri.UnescapeDataString(href.TrimEnd('/').Split('/').Last());
            DateTimeOffset.TryParse(row.Descendants(d + "getlastmodified").FirstOrDefault()?.Value, out var modified);
            return (Name: name, Modified: modified);
        }).Where(row => row.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).OrderByDescending(row => row.Modified).ToList();
    }
    private Uri FileUri(string name)
    {
        if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new InvalidDataException("远端文件名错误");
        return new Uri(DirectoryUri, Uri.EscapeDataString(name));
    }
    public async Task UploadAsync(string name, string path) { await EnsureDirectoryAsync(); using var content = new StreamContent(File.OpenRead(path)); using var response = await SendAsync("PUT", FileUri(name), content); }
    public async Task DownloadAsync(string name, string path)
    {
        using var response = await SendAsync("GET", FileUri(name));
        await using var input = await response.Content.ReadAsStreamAsync();
        var bytes = await Network.ReadLimitedAsync(input, 512 * 1024 * 1024);
        await File.WriteAllBytesAsync(path, bytes);
    }
    public async Task<string> DownloadSourcesAsync()
    {
        using var response = await SendAsync("GET", FileUri("bookSource.json"));
        await using var input = await response.Content.ReadAsStreamAsync();
        return Network.Decode(await Network.ReadLimitedAsync(input, Network.Limit));
    }
    public async Task UploadSourcesAsync(IEnumerable<JsonObject> sources)
    {
        await EnsureDirectoryAsync();
        using var content = new StringContent(J.Array(sources.Select(SourceImport.ToLegado)).ToJsonString(J.Pretty), Encoding.UTF8, "application/json");
        using var response = await SendAsync("PUT", FileUri("bookSource.json"), content);
    }
    public async Task DeleteAsync(string name) { using var response = await SendAsync("DELETE", FileUri(name)); }
    public void Dispose() => client.Dispose();
}
