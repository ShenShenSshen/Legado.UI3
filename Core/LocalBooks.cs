using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Legado.UI3.Core;

public sealed class LocalBooks(AppStore store, Func<string, JsonObject, Task<JsonNode?>> engine)
{
    public async Task<JsonObject> ImportAsync(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var parsed = await ParseAsync(file, ext);
        var chapters = parsed["chapters"]!.AsArray();
        var id = J.Id("B");
        var relative = "books/" + id + ext;
        var output = store.BookPath(relative);
        Directory.CreateDirectory(Path.Combine(store.Root, "books"));
        File.Copy(file, output);
        var book = new JsonObject
        {
            ["id"] = id, ["kind"] = "local", ["name"] = parsed.S("title", Path.GetFileNameWithoutExtension(file)), ["author"] = parsed.S("author"),
            ["localFile"] = relative, ["localType"] = ext[1..], ["ext"] = ext, ["groupId"] = "g_default",
            ["chapters"] = Metadata(chapters), ["total"] = chapters.Count, ["createdAt"] = J.Now, ["updatedAt"] = J.Now
        };
        try
        {
            await WriteParsedAsync(id, chapters, relative);
            store.Books.Add(book);
            await store.SaveAsync();
            return book;
        }
        catch
        {
            store.Books.Remove(book);
            File.Delete(output);
            File.Delete(ParsedPath(id, relative));
            throw;
        }
    }

    private async Task<JsonObject> ParseAsync(string file, string ext)
    {
        if (ext is not (".txt" or ".epub")) throw new InvalidDataException("请选择 TXT 或 EPUB 文件");
        if (new FileInfo(file).Length > 128 * 1024 * 1024) throw new InvalidDataException("本地书文件超过 128 MB");
        JsonNode? parsed;
        if (ext == ".txt")
            parsed = await engine("parseTxt", new() { ["text"] = Network.Decode(await File.ReadAllBytesAsync(file)), ["title"] = Path.GetFileNameWithoutExtension(file) });
        else
        {
            var entries = new JsonObject();
            using var archive = ZipFile.OpenRead(file);
            long total = 0;
            if (archive.Entries.Count > 10000) throw new InvalidDataException("EPUB 文件条目过多");
            foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                total += entry.Length;
                if (entry.Length > 24 * 1024 * 1024 || total > 160 * 1024 * 1024) throw new InvalidDataException("EPUB 解压内容过大");
                await using var stream = entry.Open();
                entries[entry.FullName] = Convert.ToBase64String(await Network.ReadLimitedAsync(stream, 24 * 1024 * 1024));
            }
            parsed = await engine("parseEpub", new() { ["entries"] = entries });
        }
        if (parsed is not JsonObject result || result["chapters"] is not JsonArray chapters || chapters.Count == 0) throw new InvalidDataException("文件里没有可读章节");
        return result;
    }

    private static JsonArray Metadata(JsonArray chapters) => new(chapters.Objects().Select((chapter, i) => (JsonNode)new JsonObject { ["title"] = chapter.S("title"), ["url"] = "local:" + i }).ToArray());
    private string ParsedPath(string id, string file) => store.DataPath("parsed/" + J.StableId("B", id + "\n" + file) + ".json");
    private async Task WriteParsedAsync(string id, JsonArray chapters, string file)
    {
        Directory.CreateDirectory(Path.Combine(store.Root, "parsed"));
        var path = ParsedPath(id, file);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, chapters.ToJsonString()); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<JsonArray> ChaptersAsync(JsonObject book)
    {
        var relative = book.S("localFile");
        var parsedPath = ParsedPath(book.S("id"), relative);
        if (File.Exists(parsedPath))
        {
            try { if (JsonNode.Parse(await File.ReadAllTextAsync(parsedPath)) is JsonArray data && data.Count > 0) return data; }
            catch (System.Text.Json.JsonException) { }
        }
        var original = store.BookPath(relative);
        if (!File.Exists(original)) throw new FileNotFoundException("本地书正文缺失，请重新导入原文件");
        var result = await ParseAsync(original, Path.GetExtension(original).ToLowerInvariant());
        var chapters = result["chapters"]!.AsArray();
        await WriteParsedAsync(book.S("id"), chapters, relative);
        book["chapters"] = Metadata(chapters);
        book["total"] = chapters.Count;
        await store.SaveAsync();
        return chapters;
    }
}
