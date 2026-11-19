using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Legado.UI3.Core;

public sealed record HttpVoice(string Name, string Url, string Method, JsonNode? Body,
    Dictionary<string, string> Headers, string ContentType)
{
    private const int AudioLimit = 12 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
    });
    private static readonly Regex Options = new(@",\s*(?=\{)", RegexOptions.CultureInvariant);
    private static readonly Regex Template = new(@"\{\{\s*(.*?)\s*\}\}", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex JsonStringsOrTemplates = new("\"(?:\\\\.|[^\"\\\\])*\"|\\{\\{.*?\\}\\}", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    [JsonIgnore] public string Id => "http:" + J.StableId("", Name + "\n" + Url);
    [JsonIgnore] public bool HasRate => HasTemplate(e => e is "rate" or "edgeRate" || e.Contains("speakSpeed", StringComparison.Ordinal));
    [JsonIgnore] public bool HasPitch => HasTemplate(e => e == "pitch");
    private bool HasTemplate(Func<string, bool> predicate) =>
        Template.Matches(Url + (Body?.ToJsonString() ?? "")).Any(m => predicate(Canonical(m.Groups[1].Value)));

    public static List<HttpVoice> EdgePresets(string address)
    {
        var uri = Address(address.Trim());
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api/rap", StringComparison.Ordinal)) path += "/api/rap";
        var endpoint = new UriBuilder(uri) { Path = path, Fragment = "" }.Uri.AbsoluteUri;
        return [Create("云希", "Yunxi"), Create("晓晓", "Xiaoxiao")];

        HttpVoice Create(string label, string voice) => new($"Edge TTS · {label}", endpoint, "POST",
            JsonValue.Create("text={{speakText}}&rate={{edgeRate}}&voice=zh-CN-" + voice + "Neural"), [], "audio/mpeg");
    }

    public static (List<HttpVoice> Voices, List<string> Rejected) Parse(string input)
    {
        if (input.Length > 1024 * 1024) throw new InvalidDataException("朗读配置超过 1 MB");
        input = input.Trim().TrimStart('\uFEFF');
        if (input.Length == 0) throw new InvalidDataException("请输入接口或朗读配置");
        JsonNode? root;
        if (input.StartsWith('{') || input.StartsWith('[')) root = JsonNode.Parse(input);
        else
        {
            var address = Options.Split(input, 2)[0];
            var uri = Address(Replace(address, "试听", 1, 1, true));
            root = new JsonObject { ["name"] = uri.Host, ["url"] = input };
        }
        if (root is JsonObject wrapper && wrapper["httpTTS"] is JsonArray wrapped) root = wrapped;
        IEnumerable<JsonNode?> entries = root is JsonArray array ? array : new[] { root };
        var voices = new List<HttpVoice>();
        var rejected = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                if (entry is not JsonObject data) throw new InvalidDataException("需要名称和接口地址");
                var name = data.S("name").Trim();
                var parts = Options.Split(data.S("url", data.S("endpoint")).Trim(), 2);
                if (name.Length == 0 || parts[0].Length == 0) throw new InvalidDataException("缺少名称或接口地址");
                if (name.Length > 80) throw new InvalidDataException("语音名称太长");
                var config = parts.Length == 2
                    ? JsonNode.Parse(JsonStringsOrTemplates.Replace(parts[1], m => m.Value[0] == '"' ? m.Value : JsonSerializer.Serialize(m.Value))) as JsonObject
                        ?? throw new InvalidDataException("请求配置必须是 JSON 对象")
                    : data;
                var body = config["body"]?.DeepClone();
                var method = config.S("method", body is null ? "GET" : "POST").ToUpperInvariant();
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddHeaders(data["header"] ?? data["headers"], headers);
                if (!ReferenceEquals(data, config)) AddHeaders(config["headers"] ?? config["header"], headers);
                var voice = new HttpVoice(name, parts[0], method, body, headers, data.S("contentType"));
                if (!voice.HasTemplate(e => e.Contains("speakText", StringComparison.Ordinal) || e == "text"))
                    throw new InvalidDataException("接口需要 {{speakText}} 正文占位符");
                using var probe = voice.BuildRequest("试听", 1, 1);
                voices.Add(voice);
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException or FormatException)
            {
                rejected.Add(entry.S("name", "未命名语音") + "：" + (e is JsonException ? "请求配置不是有效 JSON" : e.Message));
            }
        }
        return (voices, rejected);
    }

    private static void AddHeaders(JsonNode? node, Dictionary<string, string> headers)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var raw) && !string.IsNullOrWhiteSpace(raw)) node = JsonNode.Parse(raw);
        if (node is null || node is JsonValue empty && empty.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)) return;
        if (node is not JsonObject obj) throw new InvalidDataException("请求头必须是 JSON 对象");
        foreach (var (key, _) in obj) headers[key] = obj.S(key);
    }

    private static Uri Address(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidDataException("接口须使用 HTTPS，本机服务可使用 HTTP");
        return uri;
    }

    public HttpRequestMessage BuildRequest(string text, double rate, double pitch)
    {
        if (Method is not ("GET" or "POST")) throw new InvalidDataException("只支持 GET 和 POST");
        if (Method == "GET" && Body is not null) throw new InvalidDataException("GET 接口要把正文写在网址参数中");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var origin) || Template.IsMatch(origin.Authority))
            throw new InvalidDataException("接口必须有固定的域名或本机地址");
        var uri = Address(Replace(Url, text, rate, pitch, true));
        var request = new HttpRequestMessage(new HttpMethod(Method), uri);
        try
        {
            if (Method == "POST")
            {
                var isString = Body is JsonValue item && item.TryGetValue<string>(out _);
                var contentType = Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ??
                    (isString ? "application/x-www-form-urlencoded" : "application/json");
                var body = isString
                    ? Replace(Body!.GetValue<string>(), text, rate, pitch, contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                    : RenderJson(Body, text, rate, pitch)?.ToJsonString() ?? "{}";
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            }
            foreach (var (key, value) in Headers)
            {
                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || value.Contains('\n') || value.Contains('\r') || Template.IsMatch(value)
                    || !request.Headers.TryAddWithoutValidation(key, value))
                    throw new InvalidDataException("存在不支持的请求头");
            }
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    private static JsonNode? RenderJson(JsonNode? node, string text, double rate, double pitch) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, RenderJson(p.Value, text, rate, pitch)))),
        JsonArray arr => new JsonArray(arr.Select(n => RenderJson(n, text, rate, pitch)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var template) => RenderValue(template, text, rate, pitch),
        _ => node?.DeepClone()
    };

    private static JsonNode? RenderValue(string value, string text, double rate, double pitch)
    {
        var match = Template.Match(value);
        if (match.Success && match.Length == value.Length && Numeric(match.Groups[1].Value, rate, pitch) is { } number)
            return JsonValue.Create(number);
        return JsonValue.Create(Replace(value, text, rate, pitch, false));
    }

    private static string Replace(string template, string text, double rate, double pitch, bool encode) =>
        Template.Replace(template, match =>
        {
            var expression = Canonical(match.Groups[1].Value);
            return expression switch
            {
                "text" or "speakText" => encode ? Uri.EscapeDataString(text) : text,
                "java.encodeURI(speakText)" or "encodeURIComponent(speakText)" => Uri.EscapeDataString(text),
                "java.encodeURI(java.encodeURI(speakText))" => Uri.EscapeDataString(Uri.EscapeDataString(text)),
                _ when Numeric(expression, rate, pitch) is { } number => number.ToString("0.###", CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException("此规则包含不支持的 Legado 脚本或占位符")
            };
        });

    private static string Canonical(string expression)
    {
        var compact = Regex.Replace(expression, @"\s+", "");
        return compact.StartsWith("String(") && compact.EndsWith(')') ? compact[7..^1] : compact;
    }

    private static double? Numeric(string expression, double rate, double pitch)
    {
        var speed = 20 * rate;
        return Canonical(expression) switch
        {
            "rate" => rate,
            "edgeRate" => Math.Clamp(Math.Round(25 + (rate - 1) * 100), 0, 100),
            "pitch" => pitch,
            "speakSpeed" => speed,
            "(speakSpeed+5)/10+4" => (speed + 5) / 10 + 4,
            "(speakSpeed+5)/10" => (speed + 5) / 10,
            "(speakSpeed)*20-400" or "speakSpeed*20-400" => speed * 20 - 400,
            _ => null
        };
    }

    public async Task<(byte[] Bytes, string Mime)> SynthesizeAsync(string text, double rate, double pitch, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var request = BuildRequest(text, rate, pitch);
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new IOException($"朗读接口返回 HTTP {(int)response.StatusCode}");
            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (mime.Length == 0 || mime == "application/octet-stream") mime = ContentType.Split(';')[0].Trim().ToLowerInvariant();
            if (response.Content.Headers.ContentLength > AudioLimit) throw new IOException("朗读返回的音频过大");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await Network.ReadLimitedAsync(stream, AudioLimit, timeout.Token);
            if (mime == "audio/mp3") mime = "audio/mpeg";
            if (mime == "audio/x-wav") mime = "audio/wav";
            if (mime.Length == 0 && bytes.Length > 12)
            {
                if (bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) mime = "audio/wav";
                else if (bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) || bytes[0] == 255 && (bytes[1] & 224) == 224) mime = "audio/mpeg";
                else if (bytes.AsSpan(0, 4).SequenceEqual("OggS"u8)) mime = "audio/ogg";
            }
            if (bytes.Length < 12 || !mime.StartsWith("audio/", StringComparison.Ordinal)) throw new IOException("朗读接口没有直接返回音频");
            return (bytes, mime);
        }
        catch (HttpRequestException) { throw new IOException("无法连接朗读接口，请检查地址和网络"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("朗读接口请求超时"); }
    }
}

public sealed class HttpVoiceLibrary(string root)
{
    private readonly SemaphoreSlim changes = new(1);
    private string FilePath => Path.Combine(root, "tts-voices.json");
    public List<HttpVoice> Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<List<HttpVoice>>(File.ReadAllText(FilePath)) ?? []
        : [];

    public Task<bool> MergeAsync(IReadOnlyList<HttpVoice> imported) =>
        UpdateAsync(voices => voices.Concat(imported).GroupBy(voice => voice.Id).Select(group => group.Last()).ToList());

    public async Task<int> RemoveAsync(IReadOnlySet<string> ids)
    {
        var count = 0;
        await UpdateAsync(voices =>
        {
            count = voices.RemoveAll(voice => ids.Contains(voice.Id));
            return count > 0 ? voices : null;
        });
        return count;
    }

    private async Task<bool> UpdateAsync(Func<List<HttpVoice>, List<HttpVoice>?> change)
    {
        await changes.WaitAsync();
        try
        {
            var updated = change(Load());
            if (updated is null) return false;
            await SaveAsync(updated);
            return true;
        }
        finally { changes.Release(); }
    }

    public async Task SaveAsync(IEnumerable<HttpVoice> voices)
    {
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(voices, J.Pretty));
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
