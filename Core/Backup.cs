using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Legado.UI3.Core;

public sealed record BackupPreview(JsonObject State, Dictionary<string, byte[]> Files, int Skipped = 0)
{
    public IReadOnlyList<HttpVoice>? Voices { get; init; }
}

public static class Backup
{
    private const int EntryLimit = 128 * 1024 * 1024;
    private const long TotalLimit = 512L * 1024 * 1024;

    public static async Task<BackupPreview> ReadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > TotalLimit) throw new InvalidDataException("备份文件过大");
        var signature = new byte[4];
        _ = await stream.ReadAsync(signature); stream.Position = 0;
        if (signature[0] != 'P' || signature[1] != 'K')
        {
            using var reader = new StreamReader(stream);
            return ParseJson(await reader.ReadToEndAsync());
        }
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 15000) throw new InvalidDataException("备份包含过多文件");
        long total = 0;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            total += entry.Length;
            if (entry.Length > EntryLimit || total > TotalLimit) throw new InvalidDataException("备份解压内容过大");
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Any(p => p is ".." or "." || p.Contains(':'))) throw new InvalidDataException("备份包含不安全的文件路径");
            await using var content = entry.Open();
            if (!files.TryAdd(name, await Network.ReadLimitedAsync(content, EntryLimit))) throw new InvalidDataException("备份包含重名文件");
        }
        var manifestPath = files.Keys.FirstOrDefault(k => Path.GetFileName(k).Equals("legado-ui3.json", StringComparison.OrdinalIgnoreCase))
            ?? files.Keys.FirstOrDefault(k => Path.GetFileName(k).Equals("moread.json", StringComparison.OrdinalIgnoreCase));
        if (manifestPath is not null)
        {
            var manifest = JsonNode.Parse(Network.Decode(files[manifestPath])) as JsonObject ?? throw new InvalidDataException("Legado·UI3 备份清单格式错误");
            var preview = ParseJson(manifest.ToJsonString());
            var parent = manifestPath[..(manifestPath.LastIndexOf('/') + 1)];
            foreach (var book in preview.State["books"].Objects().Where(b => b.S("kind") == "local"))
            {
                var relative = book.S("localFile").Replace('\\', '/');
                var fileMap = manifest["fileMap"] as JsonObject;
                var oldName = fileMap?[book.S("id")].S("path").Replace('\\', '/');
                if (oldName is { Length: > 0 } && files.TryGetValue(parent + oldName, out var data) ||
                    files.TryGetValue(parent + "files/" + relative, out data))
                    preview.Files[relative] = data;
            }
            return preview;
        }
        JsonNode? Load(string name)
        {
            var key = files.Keys.FirstOrDefault(k => Path.GetFileName(k).Equals(name, StringComparison.OrdinalIgnoreCase));
            return key is not null ? JsonNode.Parse(Network.Decode(files[key])) : null;
        }
        var sources = Load("bookSource.json");
        var shelf = Load("bookshelf.json");
        if (sources is null && shelf is null)
        {
            var single = files.Where(p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (single.Length == 1) return ParseJson(Network.Decode(single[0].Value));
            throw new InvalidDataException("备份里没有书架或书源数据");
        }
        return FromLegado(sources, shelf, Load("bookmark.json"), Load("bookGroup.json"));
    }

    public static BackupPreview ParseJson(string json)
    {
        var node = JsonNode.Parse(json.TrimStart('\uFEFF')) ?? throw new InvalidDataException("备份为空");
        if (node is JsonObject root && ((root.S("app") is "legado-ui3" or "moread" && root["data"] is JsonObject) || root["books"] is JsonArray))
        {
            var state = root["data"] is JsonObject data ? data : root;
            var files = new Dictionary<string, byte[]>();
            if (root["files"] is JsonObject attached)
                foreach (var (id, value) in attached)
                {
                    var book = state["books"].Objects().FirstOrDefault(b => b.S("id") == id);
                    if (book is not null && value is JsonValue v && v.TryGetValue<string>(out var base64)) files[book.S("localFile")] = Convert.FromBase64String(base64);
                }
            IReadOnlyList<HttpVoice>? voices = null;
            if (root.S("app") == "legado-ui3" && root.ContainsKey("networkVoices"))
            {
                if (root["networkVoices"] is not JsonArray entries) throw new InvalidDataException("备份中的网络音色格式错误。");
                var (parsed, invalid) = HttpVoice.ParseEntries(entries);
                if (invalid.Count > 0) throw new InvalidDataException($"备份中有 {invalid.Count} 个网络音色配置无效，无法恢复。");
                voices = parsed;
            }
            return new(AppStore.Prepare(state), files) { Voices = voices };
        }
        if (node is JsonObject legacy && legacy["bookshelf"] is JsonArray)
            return FromLegado(legacy["bookSource"] ?? legacy["sources"], legacy["bookshelf"], legacy["bookmark"], legacy["bookGroup"]);
        var preview = SourceImport.Preview(node.ToJsonString(), []);
        var result = AppStore.Defaults();
        result["sources"] = J.Array(preview.Where(r => r.Source is not null).Select(r => r.Source!));
        if (result["sources"]!.AsArray().Count == 0) throw new InvalidDataException("没有识别到可导入的数据");
        return new(result, []);
    }

    private static IEnumerable<JsonObject> Unwrap(JsonNode? node, params string[] keys)
    {
        if (node is JsonObject obj)
            foreach (var key in keys)
                if (obj[key] is JsonArray nested) return nested.Objects();
        return node.Objects();
    }

    private static BackupPreview FromLegado(JsonNode? sources, JsonNode? shelf, JsonNode? bookmarks, JsonNode? groups)
    {
        var state = AppStore.Defaults();
        var input = Unwrap(sources, "bookSource", "bookSources", "sources", "bookSourceList", "data").ToArray();
        if (input.Length > 0)
        {
            var rows = SourceImport.Preview(J.Array(input).ToJsonString(), []);
            foreach (var row in rows.Where(r => r.Valid)) row.Source!["id"] = J.StableId("S", SourceImport.Identity(row.Source!));
            SourceImport.Apply(rows, state["sources"]!.AsArray(), false);
        }
        var sourceList = state["sources"].Objects().ToArray();
        var groupMap = new Dictionary<string, string>();
        foreach (var group in Unwrap(groups, "bookGroup", "groups"))
        {
            var name = group.S("groupName", group.S("name", "导入分组"));
            var id = name == "默认分组" ? "g_default" : J.StableId("G", name);
            groupMap[group.S("groupId", group.S("id"))] = id;
            if (id != "g_default" && !state["groups"].Objects().Any(g => g.S("id") == id))
                state["groups"]!.AsArray().Add(new JsonObject { ["id"] = id, ["name"] = name });
        }
        int skipped = 0;
        var imported = new HashSet<string>();
        foreach (var item in Unwrap(shelf, "bookshelf", "books"))
        {
            var url = item.S("bookUrl");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || item.S("origin") == "local") { skipped++; continue; }
            var id = J.StableId("B", url);
            if (!imported.Add(id)) continue;
            var source = sourceList.FirstOrDefault(s => s.S("url").TrimEnd('/') == item.S("origin").TrimEnd('/') && s.S("name") == item.S("originName"))
                ?? sourceList.FirstOrDefault(s => s.S("url").TrimEnd('/') == item.S("origin").TrimEnd('/'));
            var book = item.Copy();
            book["id"] = id; book["kind"] = "online"; book["name"] = item.S("name", item.S("bookName"));
            book["cover"] = item.S("coverUrl"); book["sourceId"] = source?.S("id") ?? ""; book["sourceName"] = item.S("originName");
            book["tocUrl"] = item.S("tocUrl"); book["total"] = item.I("totalChapterNum", item.I("chapterListSize"));
            book["groupId"] = groupMap.GetValueOrDefault(item.S("group"), "g_default"); book["chapters"] = new JsonArray(); book["createdAt"] = J.Now;
            state["books"]!.AsArray().Add(book);
            state["progress"]![id] = new JsonObject { ["ci"] = item.I("durChapterIndex"), ["title"] = item.S("durChapterTitle"), ["pct"] = 0, ["at"] = item.D("durChapterTime") };
        }
        JsonObject? FindBook(JsonObject item) => state["books"].Objects().FirstOrDefault(b => b.S("name") == item.S("bookName") &&
            (item.S("bookAuthor", item.S("author")) == "" || b.S("author") == item.S("bookAuthor", item.S("author"))));
        foreach (var item in Unwrap(bookmarks, "bookmark", "bookmarks"))
        {
            var book = FindBook(item);
            if (book is null) continue;
            state["bookmarks"]!.AsArray().Add(new JsonObject { ["id"] = J.StableId("M", book.S("id") + item.S("createTime") + item.S("chapterIndex")), ["bookId"] = book.S("id"), ["name"] = book.S("name"), ["ci"] = item.I("chapterIndex"), ["title"] = item.S("chapterTitle"), ["pct"] = 0, ["text"] = item.S("bookText"), ["at"] = item.D("createTime") });
        }
        return new(state, [], skipped);
    }

    public static async Task ApplyAsync(AppStore store, BackupPreview preview, bool replace)
    {
        // Validate every destination before creating any file or changing the current shelf.
        var next = preview.State.Copy();
        var copies = new List<(JsonObject Book, string Destination, byte[] Bytes)>();
        foreach (var book in next["books"].Objects().Where(b => b.S("kind") == "local"))
        {
            var relative = book.S("localFile").Replace('\\', '/');
            if (relative == "") continue;
            var destination = store.BookPath(relative);
            if (preview.Files.TryGetValue(relative, out var bytes) || preview.Files.TryGetValue(book.S("localFile"), out bytes)) copies.Add((book, destination, bytes));
        }
        var written = new List<string>();
        try
        {
            foreach (var (book, savedPath, bytes) in copies)
            {
                var destination = savedPath;
                if (File.Exists(destination))
                {
                    if (new FileInfo(destination).Length == bytes.Length && (await File.ReadAllBytesAsync(destination)).AsSpan().SequenceEqual(bytes)) continue;
                    var relative = "books/" + J.Id("B") + Path.GetExtension(destination);
                    destination = store.BookPath(relative);
                    book["localFile"] = relative;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var file = new FileStream(destination, FileMode.CreateNew);
                written.Add(destination);
                await file.WriteAsync(bytes);
            }
            var selection = preview.Voices is null ? null : next["settings"]?["tts"].S("voiceURI");
            if (selection?.StartsWith("http:", StringComparison.Ordinal) == true &&
                !preview.Voices!.Any(voice => voice.Id == selection)) selection = "";
            if (preview.Voices is { Count: > 0 } voices)
                await store.Voices.RestoreAsync(voices, () => store.RestoreAsync(next, replace, selection));
            else
                await store.RestoreAsync(next, replace, selection);
        }
        catch
        {
            foreach (var path in written) File.Delete(path);
            throw;
        }
    }

    public static async Task ExportAsync(AppStore store, string path, bool includeFiles, bool includeVoices = true)
    {
        var state = store.State.Copy();
        var voices = includeVoices ? J.Array(store.Voices.Load().Select(voice => voice.BackupEntry())) : null;
        if (!includeVoices) state["settings"]!["tts"]!.AsObject().Remove("voiceURI");
        state["settings"]!["webdav"]!.AsObject().Remove("includeVoices");
        var books = state["books"].Objects().ToArray();
        var sources = state["sources"].Objects().ToArray();
        var groups = state["groups"].Objects().ToArray();
        var temp = path + ".tmp";
        try
        {
            await using (var file = File.Create(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                async Task Write(string name, string content) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); await using var stream = entry.Open(); await using var writer = new StreamWriter(stream); await writer.WriteAsync(content); }
                var fileMap = new JsonObject();
                var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (includeFiles)
                    foreach (var book in books.Where(b => b.S("kind") == "local"))
                    {
                        var relative = book.S("localFile");
                        if (relative == "") continue;
                        var source = store.BookPath(relative);
                        if (!File.Exists(source)) continue;
                        var zipPath = "files/" + Path.GetFileName(source);
                        if (writtenFiles.Add(zipPath))
                        {
                            var entry = zip.CreateEntry(zipPath, CompressionLevel.Optimal);
                            await using var target = entry.Open(); await using var input = File.OpenRead(source); await input.CopyToAsync(target);
                        }
                        fileMap[book.S("id")] = new JsonObject { ["path"] = zipPath, ["ext"] = Path.GetExtension(source), ["localFile"] = relative };
                    }
                var manifest = new JsonObject { ["app"] = "legado-ui3", ["schema"] = 3,
                    ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["data"] = state, ["fileMap"] = fileMap };
                if (voices is not null) manifest["networkVoices"] = voices;
                await Write("legado-ui3.json", manifest.ToJsonString(J.Pretty));
                await Write("bookSource.json", J.Array(sources.Select(SourceImport.ToLegado)).ToJsonString(J.Pretty));
                var shelf = books.Select(book =>
                {
                    var progress = state["progress"]![book.S("id")];
                    var source = sources.FirstOrDefault(s => s.S("id") == book.S("sourceId"));
                    var result = book.Copy();
                    var isLocal = book.S("kind") == "local";
                    result["bookUrl"] = isLocal ? "file:///" + book.S("localFile") : book.S("bookUrl");
                    result["origin"] = isLocal ? "local" : source?.S("url") ?? book.S("origin"); result["originName"] = isLocal ? "本地" : source?.S("name") ?? book.S("sourceName");
                    result["group"] = Math.Max(0, Array.FindIndex(groups, group => group.S("id") == book.S("groupId")));
                    result["type"] = isLocal ? 1 : 0;
                    result["totalChapterNum"] = book.I("total");
                    result["coverUrl"] = book.S("cover"); result["durChapterIndex"] = progress.I("ci"); result["durChapterTitle"] = progress.S("title"); result["durChapterTime"] = progress.D("at");
                    return result;
                });
                await Write("bookshelf.json", J.Array(shelf).ToJsonString(J.Pretty));
                await Write("bookmark.json", J.Array(state["bookmarks"].Objects().Select(b => new JsonObject { ["bookName"] = b.S("name"), ["chapterIndex"] = b.I("ci"), ["chapterTitle"] = b.S("title"), ["bookText"] = b.S("text"), ["createTime"] = b.D("at") })).ToJsonString());
                await Write("bookGroup.json", J.Array(groups.Select((g, i) => new JsonObject { ["groupId"] = i, ["groupName"] = g.S("name") })).ToJsonString());
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
