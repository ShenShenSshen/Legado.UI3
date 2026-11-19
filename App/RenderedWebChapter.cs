using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim webChapterNavigation = new(1);
    private string? webChapterScript;

    private async Task<JsonObject> ReadRenderedWebChapterAsync(Uri url, string selector, int session)
    {
        await webChapterNavigation.WaitAsync();
        try
        {
            void CheckSession()
            {
                if (closing || readerSession != session) throw new OperationCanceledException();
            }
            CheckSession();
            var view = await PrepareWebPageAsync(forReading: true);
            CheckSession();
            var core = view.CoreWebView2;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong? navigationId = null;
            void Started(CoreWebView2 _, CoreWebView2NavigationStartingEventArgs args)
            {
                if (navigationId is null && SameWebChapter(args.Uri, url)) navigationId = args.NavigationId;
            }
            void Completed(CoreWebView2 _, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (args.NavigationId == navigationId) completed.TrySetResult(args.IsSuccess);
            }
            core.NavigationStarting += Started;
            core.NavigationCompleted += Completed;
            try
            {
                core.Navigate(url.AbsoluteUri);
                if (!await completed.Task.WaitAsync(TimeSpan.FromSeconds(15))) throw new IOException("原网页未能加载");
            }
            catch (TimeoutException) { throw new IOException("原网页加载超时"); }
            finally
            {
                core.NavigationStarting -= Started;
                core.NavigationCompleted -= Completed;
            }

            webChapterScript ??= await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "web-chapter.js"));
            var script = webChapterScript + "(" + JsonValue.Create(selector)!.ToJsonString() + ")";
            for (var attempt = 0; attempt < 24; attempt++)
            {
                CheckSession();
                if (!SameWebChapter(core.Source, url)) break;
                var raw = await view.ExecuteScriptAsync(script).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                CheckSession();
                if (!SameWebChapter(core.Source, url)) break;
                if (JsonNode.Parse(raw) is JsonObject result)
                {
                    if (result.B("blocked")) throw new IOException("网页需要登录、订阅或验证");
                    if (result["paras"] is JsonArray { Count: > 0 }) return result;
                }
                await Task.Delay(250);
            }
            throw new IOException("原网页暂未显示可读正文");
        }
        finally { webChapterNavigation.Release(); }
    }

    private static bool SameWebChapter(string address, Uri chapter) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        Uri.Compare(uri, chapter, UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

    private int UpdateRenderedChapter(JsonObject book, JsonObject chapter, int index, Uri url, JsonObject rendered)
    {
        chapter["browserRendered"] = true;
        if (rendered.S("title") is { Length: > 0 } title) chapter["title"] = title;
        var chapters = (JsonArray)book["chapters"]!;
        var previous = Adjacent("previous", "上一章");
        if (index == 0 && previous is not null)
        {
            chapters.Insert(0, previous);
            index++;
            ShiftChapterPositions(book);
        }
        var next = Adjacent("next", "下一章");
        if (index == chapters.Count - 1 && next is not null) chapters.Add(next);
        readerChapters = chapters.Objects().ToArray();
        book["total"] = chapters.Count;
        return index;

        JsonObject? Adjacent(string direction, string label)
        {
            var link = rendered[direction];
            if (!Uri.TryCreate(link.S("url"), UriKind.Absolute, out var neighbor) ||
                neighbor.Scheme != url.Scheme || neighbor.Authority != url.Authority || neighbor.UserInfo.Length > 0 ||
                new Uri(neighbor, ".").AbsolutePath != new Uri(url, ".").AbsolutePath ||
                SameWebChapter(neighbor.AbsoluteUri, url) ||
                chapters.Objects().Any(item => SameWebChapter(item.S("url"), neighbor))) return null;
            return new() { ["url"] = neighbor.GetLeftPart(UriPartial.Query), ["title"] = link.S("title", label), ["browserRendered"] = true };
        }
    }

    private void ShiftChapterPositions(JsonObject book)
    {
        if (book.Parent is null) return;
        var id = book.S("id");
        var entries = store.State["bookmarks"].Objects().Where(entry => entry.S("bookId") == id);
        foreach (var entry in entries) entry["ci"] = entry.I("ci") + 1;
        if (store.Progress[id] is JsonObject progress) progress["ci"] = progress.I("ci") + 1;
    }
}
