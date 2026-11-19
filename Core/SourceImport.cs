using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Legado.UI3.Core;

public sealed class ImportRow : INotifyPropertyChanged
{
    public JsonObject? Source { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Group { get; init; } = "";
    public string Status { get; init; } = "";
    public bool Valid => Source is not null;
    private bool selected;
    public bool Selected { get => selected; set { selected = Valid && value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class SourceImport
{
    public static Uri ResolveUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("请输入 HTTPS 书源文件链接");
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return uri;
        var pieces = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length < 5 || pieces[2] is not ("blob" or "raw")) throw new InvalidDataException("请使用 GitHub 文件页或 Raw 链接，而不是仓库首页");
        // GitHub resolves the ref itself, including branch names containing '/'.
        return new Uri($"https://github.com/{pieces[0]}/{pieces[1]}/raw/{string.Join('/', pieces.Skip(3))}");
    }

    public static string Identity(JsonObject source)
    {
        var url = source.S("url").Trim().TrimEnd('/');
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            url = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant() + uri.PathAndQuery.TrimEnd('/') + uri.Fragment;
        return url + "\n" + source.S("name").Trim();
    }

    public static JsonObject Normalize(JsonObject raw)
    {
        var source = raw.Copy();
        source["id"] = string.IsNullOrWhiteSpace(raw.S("id")) ? J.Id("S") : raw.S("id");
        source["name"] = raw.S("bookSourceName", raw.S("name")).Trim();
        source["url"] = raw.S("bookSourceUrl", raw.S("url")).Trim().TrimEnd('/');
        source["group"] = raw.S("bookSourceGroup", raw.S("group", "未分组"));
        source["enabled"] = raw.B("enabled", true);
        source["charset"] = raw.S("charset", "auto");
        source["type"] = "legado";
        source["searchUrl"] = raw.S("searchUrl");
        source["comment"] = raw.S("bookSourceComment", raw.S("comment"));
        foreach (var key in new[] { "ruleSearch", "ruleBookInfo", "ruleToc", "ruleContent" }) source[key] = raw[key] is JsonObject rule ? rule.DeepClone() : new JsonObject();
        if (source.S("name") == "" || !Uri.TryCreate(source.S("url"), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("缺少名称或有效书源网址");
        return source;
    }

    public static List<ImportRow> Preview(string json, IEnumerable<JsonObject> existing)
    {
        JsonNode node;
        try { node = JsonNode.Parse(json.TrimStart('\uFEFF'), documentOptions: new() { MaxDepth = 128 }) ?? throw new InvalidDataException("内容为空"); }
        catch (System.Text.Json.JsonException) { throw new InvalidDataException("内容不是有效的书源 JSON"); }
        if (node is JsonObject obj)
            foreach (var key in new[] { "bookSource", "bookSources", "sources", "data", "bookSourceList" })
                if (obj[key] is JsonArray wrapped) { node = wrapped; break; }
        var inputs = node is JsonArray arr ? arr.ToArray() : [node];
        var identities = existing.Select(Identity).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ImportRow>();
        foreach (var input in inputs)
        {
            try
            {
                var source = Normalize(input as JsonObject ?? throw new InvalidDataException("书源必须是对象"));
                var duplicate = !identities.Add(Identity(source));
                rows.Add(new() { Source = source, Name = source.S("name"), Url = source.S("url"), Group = source.S("group"), Status = duplicate ? "重复" : "新增", Selected = !duplicate });
            }
            catch (InvalidDataException e) { rows.Add(new() { Name = input is JsonObject invalid ? invalid.S("bookSourceName", invalid.S("name", "无效条目")) : "无效条目", Status = e.Message }); }
        }
        if (rows.Count == 0) throw new InvalidDataException("没有识别到书源");
        return rows;
    }

    public static (int Added, int Updated) Apply(IEnumerable<ImportRow> rows, JsonArray sources, bool update)
    {
        var index = sources.Objects().GroupBy(Identity).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ids = sources.Objects().Select(s => s.S("id")).ToHashSet();
        int added = 0, updated = 0;
        foreach (var row in rows.Where(r => r.Selected && r.Source is not null))
        {
            var source = row.Source!.Copy();
            var key = Identity(source);
            if (index.TryGetValue(key, out var old))
            {
                if (!update) continue;
                source["id"] = old.S("id"); source["enabled"] = old.B("enabled", true);
                var position = sources.IndexOf(old); sources[position] = source; index[key] = source; updated++;
            }
            else
            {
                if (!ids.Add(source.S("id"))) { source["id"] = J.Id("S"); ids.Add(source.S("id")); }
                sources.Add(source); index[key] = source; added++;
            }
        }
        return (added, updated);
    }

    public static JsonObject ToLegado(JsonObject source)
    {
        var result = source.Copy();
        result["bookSourceName"] = source.S("name"); result["bookSourceUrl"] = source.S("url");
        result["bookSourceGroup"] = source.S("group"); result["bookSourceComment"] = source.S("comment");
        return result;
    }
}
