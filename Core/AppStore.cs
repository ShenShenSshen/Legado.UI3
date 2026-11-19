using System.Text.Json.Nodes;

namespace Legado.UI3.Core;

public sealed class AppStore
{
    private readonly SemaphoreSlim writes = new(1);
    public string Root { get; }
    public JsonObject State { get; private set; }
    public JsonArray Books => (JsonArray)State["books"]!;
    public JsonArray Sources => (JsonArray)State["sources"]!;
    public JsonObject Progress => (JsonObject)State["progress"]!;
    public JsonObject Settings => (JsonObject)State["settings"]!;
    public string? LoadWarning { get; private set; }

    public AppStore(string? root = null)
    {
        Root = root ?? DefaultRoot();
        Directory.CreateDirectory(Root);
        State = Defaults();
        var file = Path.Combine(Root, "data.json");
        if (File.Exists(file))
        {
            try { State = Prepare(JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException("存档格式错误")); }
            catch (Exception e)
            {
                // Preserve the damaged original before any subsequent save can replace it.
                File.Copy(file, file + ".broken-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                LoadWarning = "存档未能读取，原文件已保留：" + e.Message;
            }
        }
    }

    internal static string DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable("LEGADO_UI3_DATA_DIR") ?? Environment.GetEnvironmentVariable("MOREAD_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, "Legado.UI3");
        var legacy = Path.Combine(local, "MoRead-WinUI3");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    public static JsonObject Defaults() => JsonNode.Parse("""
    {"version":4,"books":[],"sources":[],"groups":[{"id":"g_default","name":"默认分组"}],"progress":{},"bookmarks":[],"addons":[],"settings":{"fontSize":22,"lineHeight":1.7,"paraGapEm":0.5,"fontFamily":"serif","theme":"auto","pageMode":"scroll","readingWidth":760,"autoCacheNext":true,"shelfSort":"recent","shelfSize":"md","maxConcurrent":3,"searchLimit":40,"tts":{"rate":1,"pitch":1,"voiceURI":"","autoNext":true},"webdav":{"url":"https://dav.jianguoyun.com/dav/","user":"","pass":"","dir":"legado","autoBackup":false,"intervalHours":24,"keep":10}}}
    """)!.AsObject();

    public static JsonObject Prepare(JsonObject incoming)
    {
        var result = Defaults();
        foreach (var (key, value) in incoming) if (key is not ("stats" or "history")) result[key] = value?.DeepClone();
        foreach (var key in new[] { "books", "sources", "groups", "bookmarks", "addons" })
            if (result[key] is not JsonArray a || a.Any(v => v is not JsonObject)) throw new InvalidDataException($"存档的 {key} 格式错误");
        if (result["progress"] is not JsonObject) throw new InvalidDataException("存档进度格式错误");
        var settings = Defaults()["settings"]!.AsObject().Copy();
        if (incoming["settings"] is JsonObject inc)
            foreach (var (key, value) in inc)
            {
                if (settings[key] is JsonObject nested)
                {
                    if (value is JsonObject patch)
                        foreach (var (k, v) in patch) nested[k] = v?.DeepClone();
                }
                else settings[key] = value?.DeepClone();
            }
        // Older versions saved the default paper color even if it was never selected.
        if (incoming.I("version") < 4 && settings.S("theme") == "paper") settings["theme"] = "auto";
        result["settings"] = settings;
        result["version"] = 4;
        return result;
    }

    public async Task SaveAsync()
    {
        await writes.WaitAsync();
        try { await PersistAsync(State); }
        finally { writes.Release(); }
    }

    private async Task PersistAsync(JsonObject snapshot)
    {
        var file = Path.Combine(Root, "data.json");
        var temp = file + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, snapshot.ToJsonString(J.Pretty));
            if (File.Exists(file)) File.Replace(temp, file, file + ".previous", true);
            else File.Move(temp, file);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task RestoreAsync(JsonObject incoming, bool replace)
    {
        var prepared = Prepare(incoming);
        await writes.WaitAsync();
        try
        {
            var next = replace ? prepared : State.Copy();
            if (!replace)
            {
                var targetSources = next["sources"]!.AsArray();
                var incomingSources = prepared["sources"].Objects().ToArray();
                if (incomingSources.Length > 0)
                {
                    var rows = SourceImport.Preview(prepared["sources"]!.ToJsonString(), targetSources.Objects());
                    foreach (var row in rows) row.Selected = true;
                    SourceImport.Apply(rows, targetSources, true);
                }
                var byIdentity = targetSources.Objects().GroupBy(SourceImport.Identity).ToDictionary(g => g.Key, g => g.First().S("id"));
                var incomingIds = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in incomingSources)
                    if (byIdentity.TryGetValue(SourceImport.Identity(source), out var kept) && source.S("id") != "") incomingIds[source.S("id")] = kept;
                foreach (var book in prepared["books"].Objects())
                    if (incomingIds.TryGetValue(book.S("sourceId"), out var id)) book["sourceId"] = id;
                foreach (var key in new[] { "books", "groups", "bookmarks", "addons" })
                    MergeById(next[key]!.AsArray(), prepared[key].Objects());
                foreach (var (key, value) in prepared["progress"]!.AsObject()) next["progress"]![key] = value?.DeepClone();
            }
            await PersistAsync(next);
            State = next;
        }
        finally { writes.Release(); }
    }

    private static void MergeById(JsonArray target, IEnumerable<JsonObject> incoming)
    {
        var index = target.Objects().Select((obj, i) => (Id:obj.S("id"), Index:i)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Index);
        foreach (var item in incoming)
        {
            var id = item.S("id");
            if (id != "" && index.TryGetValue(id, out var existing)) target[existing] = item.DeepClone();
            else { if (id != "") index[id] = target.Count; target.Add(item.DeepClone()); }
        }
    }

    public Task<(int Added, int Updated)> ApplySourcesAsync(IEnumerable<ImportRow> rows, bool update) =>
        ChangeSourcesAsync(sources => SourceImport.Apply(rows, sources, update));

    public Task<int> DeleteSourcesAsync(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return ChangeSourcesAsync(sources =>
        {
            int removed = 0;
            for (int i = sources.Count - 1; i >= 0; i--)
                if (selected.Contains(sources[i].S("id"))) { sources.RemoveAt(i); removed++; }
            return removed;
        });
    }

    private async Task<T> ChangeSourcesAsync<T>(Func<JsonArray, T> edit)
    {
        await writes.WaitAsync();
        var old = Sources;
        try
        {
            var changed = (JsonArray)old.DeepClone();
            var result = edit(changed);
            State["sources"] = changed;
            await PersistAsync(State);
            return result;
        }
        catch { State["sources"] = old; throw; }
        finally { writes.Release(); }
    }

    public string DataPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("不允许的文件路径");
        var path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份路径超出数据目录");
        return path;
    }

    public string BookPath(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length != 2 || !parts[0].Equals("books", StringComparison.OrdinalIgnoreCase) ||
            parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetExtension(parts[1]).ToLowerInvariant() is not (".txt" or ".epub"))
            throw new InvalidDataException("本地书文件路径无效");
        return DataPath(relative);
    }
}
