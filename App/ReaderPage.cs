using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private const string ReaderUrl = "https://reader.moread.local/reader.html";
    private WebView2? readerWeb;
    private Task? readerInitialization;
    private Grid? readerSurface;
    private SplitView? readerSplit;
    private FrameworkElement? readerTop, readerBottom, readerError;
    private TextBlock? readerTitle, readerPosition;
    private Button? readerPrevious, readerNext, readerSpeech, readerSwitch, readerFullScreen;
    private JsonObject? readerBook;
    private JsonObject[] readerChapters = [];
    private JsonArray? localChapters;
    private int readingIndex, requestedChapter, readerSession;
    private double readingPercent;
    private string readingToken = "";
    private bool readerBusy, progressDirty, appending, prefetchFailed, readerWasFullScreen;
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    private async Task InitializeReaderAsync()
    {
        readerWeb = new WebView2 { Opacity = 0 };
        AutomationProperties.SetName(readerWeb, "章节正文");
        readerTitle = Ui.Text("", 16);
        readerTitle.MaxLines = 1;
        readerTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        readerPosition = Ui.Text("", 13, true);
        var directory = Button("目录", ToggleDirectory);
        var bookmark = ActionButton("加书签", AddBookmarkAsync);
        readerSwitch = ActionButton("换源", async () => { if (readerBook is { } book) await SwitchSourceAsync(book); });
        var readerSettings = new Pivot { Width = 350, Height = 430 };
        readerSettings.Items.Add(new PivotItem { Header = "排版", Content = ReadingSettings(reader: true) });
        readerSettings.Items.Add(new PivotItem { Header = "朗读", Content = Ui.Scroll(SpeechSettings(reader: true)) });
        var typography = new Button { Content = "阅读设置", Flyout = new Flyout { Content = readerSettings, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight } };
        readerSpeech = ActionButton("朗读", ToggleSpeechAsync);
        readerFullScreen = Button(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ? "退出全屏" : "全屏", ToggleFullScreen);
        readerTop = Ui.Columns("Auto,*,Auto", ActionButton("返回书架", CloseReaderAsync), readerTitle, Ui.AdaptiveCommands(directory, bookmark, readerSwitch, typography, readerSpeech, readerFullScreen));
        readerTop.Margin = new(16, 8, 16, 6);
        readerPrevious = ActionButton("上一章", () => StepReaderAsync(-1));
        readerNext = ActionButton("下一章", () => StepReaderAsync(1));
        readerPosition.HorizontalAlignment = HorizontalAlignment.Center;
        readerBottom = Ui.Columns("Auto,*,Auto", readerPrevious, readerPosition, readerNext);
        readerBottom.Margin = new(16, 6, 16, 8);

        var readerBody = new Grid();
        readerBody.Children.Add(readerWeb);
        readerError = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        readerBody.Children.Add(readerError);
        var content = Ui.Rows("Auto,*,Auto", readerTop, readerBody, readerBottom);
        content.RowSpacing = 0;
        readerSplit = new SplitView { DisplayMode = SplitViewDisplayMode.Overlay, PanePlacement = SplitViewPanePlacement.Left, OpenPaneLength = 318, Content = content };
        readerSurface = new Grid();
        readerSurface.Children.Add(readerSplit);
        Grid.SetRow(readerSurface, 2);
        root.Children.Add(readerSurface);
        readerSurface.Visibility = Visibility.Visible;

        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(store.Root, "reader-webview"), new());
        await readerWeb.EnsureCoreWebView2Async(env);
        var core = readerWeb.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("reader.moread.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) => { if (e.Uri != ReaderUrl) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.WebMessageReceived += (_, e) =>
        {
            if (e.Source != ReaderUrl) return;
            try
            {
                var message = JsonNode.Parse(e.WebMessageAsJson);
                if (message.B("ready")) { ready.TrySetResult(); return; }
                if (readerBook is not null && message.S("token") == readingToken) HandleReaderMessage(message!);
            }
            catch (Exception ex) { ReportError(ex); }
        };
        core.ProcessFailed += (_, _) => { if (readerBook is not null) ShowReaderError("阅读界面已停止。关闭应用后可重新打开。"); };
        core.Navigate(ReaderUrl);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        progressTimer.Tick += (_, _) => { if (progressDirty && !readerBusy) _ = RunAsync(FlushReadingProgressAsync); };
        progressTimer.Start();
    }

    private async Task OpenReaderAsync(JsonObject book, int? chapter = null, double? percent = null)
    {
        if (readerBook is null)
            readerWasFullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        if (readerBook is not null && !ReferenceEquals(readerBook, book))
            await FlushReadingProgressAsync();
        StopSpeech();
        ResetRenderedChapters();
        var session = ++readerSession;
        readingToken = "";
        readerBook = book;
        readerChapters = [];
        localChapters = null;
        progressDirty = false;
        prefetchFailed = false;
        appending = false;
        readingIndex = 0;
        readingPercent = 0;
        readerBusy = true;
        var saved = store.Progress[book.S("id")];
        var progress = percent ?? (chapter is null ? saved.D("pct") : 0);
        requestedChapter = chapter ?? saved.I("ci");

        try
        {
            UpdateWindowChrome();
            navigation.Visibility = Visibility.Collapsed;
            readerInitialization ??= InitializeReaderAsync();
            if (readerSurface is not null) readerSurface.Visibility = Visibility.Visible;
            await readerInitialization;
            if (session != readerSession) return;
            readerSplit!.IsPaneOpen = false;
            readerSwitch!.Visibility = book.S("kind") == "local" ? Visibility.Collapsed : Visibility.Visible;
            readerTitle!.Text = book.S("name");
            SetReaderBars(true);
            ShowReaderLoading();
            await initialization;
            if (book.S("kind") == "local") localChapters = await local.ChaptersAsync(book);
            if (!(book["chapters"] is JsonArray cached && cached.Count > 0))
                await RefreshTocAsync(book);
            if (session != readerSession) return;
            readerChapters = book["chapters"].Objects().ToArray();
            if (chapter is null && saved is not null && book.S("kind") != "local")
            {
                var savedIndex = Math.Clamp(saved.I("ci"), 0, readerChapters.Length - 1);
                if (saved.S("title") is { Length: > 0 } title && title != readerChapters[savedIndex].S("title"))
                    requestedChapter = await ChapterMatchAsync((JsonArray)book["chapters"]!, title, savedIndex, book.I("total"));
                else requestedChapter = savedIndex;
            }
            if (session == readerSession) await ReadChapterAsync(requestedChapter, progress);
        }
        catch (Exception e)
        {
            if (session != readerSession) return;
            if (readerSurface is null || readerWeb?.CoreWebView2 is null)
            {
                navigation.Visibility = Visibility.Visible;
                if (readerSurface is not null) readerSurface.Visibility = Visibility.Collapsed;
                readerBook = null;
                SetFullScreen(readerWasFullScreen);
                throw;
            }
            ShowReaderError(e.Message, () => OpenReaderAsync(book, chapter, percent));
        }
        finally { if (session == readerSession) readerBusy = false; }
    }

    private async Task<JsonArray> LoadOnlineChapterAsync(JsonObject book, JsonObject chapter)
    {
        var webBook = book.S("kind") is "web" or "comic";
        var webSelector = book["webRule"]?["content"].S("content") ?? "";
        if (webBook && (chapter.B("browserRendered") || webSelector.Contains(".readChapterPrimaryBlock", StringComparison.Ordinal)))
            throw new ChapterNeedsBrowserException();
        var identity = book.S("id") + "\n" + book.S("sourceId") + "\n" + chapter.S("url");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var path = Path.Combine(store.Root, "cache", key + ".json");
        if (File.Exists(path))
        {
            try
            {
                if (JsonNode.Parse(await File.ReadAllTextAsync(path)) is JsonArray existing && existing.Count > 0)
                {
                    if (webBook && ContainsVipTemplate(existing)) throw new ChapterNeedsBrowserException();
                    return existing;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        var source = SourceForBook(book) ?? throw new IOException("这个书源已移除，换源后可继续阅读");
        var content = await engine.RunAsync("content", new() { ["source"] = source.DeepClone(), ["url"] = chapter.S("url") });
        if (content.B("browserRequired")) throw new ChapterNeedsBrowserException();
        if (content.B("blocked")) throw new IOException("站点需要验证，请重试或换源");
        if (content?["paras"] is not JsonArray paragraphs || paragraphs.Count == 0) throw new IOException("没有获取到章节正文，请重试或换源");
        if (webBook && ContainsVipTemplate(paragraphs)) throw new ChapterNeedsBrowserException();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, paragraphs.ToJsonString());
            File.Move(temp, path, true);
        }
        catch (IOException) { /* A chapter remains readable if its cache cannot be saved. */ }
        return paragraphs;
    }

    private static bool ContainsVipTemplate(JsonArray paragraphs) => paragraphs.Any(paragraph =>
        paragraph is JsonValue value && value.TryGetValue<string>(out var text) &&
        text.Length < 160 && text.Contains("此章为VIP章节，需要订阅后才能继续阅读", StringComparison.OrdinalIgnoreCase));

    private sealed class ChapterNeedsBrowserException : IOException { }

    private async Task<JsonArray> LoadChapterAsync(JsonObject book, int index, JsonObject chapter, JsonArray? parsed)
    {
        if (book.S("kind") != "local") return await LoadOnlineChapterAsync(book, chapter);
        if (parsed is null || index >= parsed.Count || parsed[index]?["text"] is not JsonArray paragraphs) throw new IOException("找不到该章节正文");
        return paragraphs;
    }

    private async Task<(JsonArray Text, int Index)> LoadChapterForDisplayAsync(JsonObject book, int index, JsonObject chapter, int session)
    {
        try { return (await LoadChapterAsync(book, index, chapter, localChapters), index); }
        catch (ChapterNeedsBrowserException)
        {
            if (session != readerSession) throw new OperationCanceledException();
            var url = Network.WebUri(chapter.S("url"));
            try
            {
                var selector = book["webRule"]?["content"].S("content") ?? "";
                var rendered = await GetRenderedChapterAsync(url, selector);
                if (session != readerSession) throw new OperationCanceledException();
                return ((JsonArray)rendered["paras"]!, UpdateRenderedChapter(book, chapter, index, url, rendered));
            }
            catch (Exception e) when (e is not OperationCanceledException) { throw new RenderedChapterException(url, e); }
        }
    }

    private sealed class RenderedChapterException(Uri url, Exception cause) : IOException(cause.Message, cause)
    {
        public Uri Url { get; } = url;
    }

    private async Task ReadChapterAsync(int index, double percent = 0)
    {
        var book = readerBook;
        if (book is null || readerChapters.Length == 0) throw new IOException("目录为空，请重试或换源");
        if (index < 0 || index >= readerChapters.Length) { ShowNotice(index < 0 ? "已经是第一章" : "已经是最后一章"); return; }
        StopSpeech();
        var session = ++readerSession;
        var displayedToken = readingToken;
        readingToken = "";
        requestedChapter = index;
        readerBusy = true;
        appending = false;
        prefetchFailed = false;
        var chapter = readerChapters[index];
        try
        {
            var (paragraphs, resolvedIndex) = await LoadChapterForDisplayAsync(book, index, chapter, session);
            if (session != readerSession) return;
            index = readingIndex = requestedChapter = resolvedIndex;
            readingPercent = Math.Clamp(percent, 0, 1);
            readingToken = Guid.NewGuid().ToString("N");
            var display = ChapterPayload(index, chapter, paragraphs);
            readerWeb!.Visibility = Visibility.Visible;
            readerError!.Visibility = Visibility.Collapsed;
            UpdateReaderControls();
            await ReaderScriptAsync("set", display, ReadingAppearance(), JsonValue.Create(readingToken), JsonValue.Create(readingPercent));
            if (session != readerSession) return;
            readerWeb.Opacity = 1;
            progressDirty = true;
            readerBusy = false;
            await ReaderScriptAsync("report");
            await FlushReadingProgressAsync();
        }
        catch (Exception e)
        {
            if (session != readerSession) return;
            var originalPage = (e as RenderedChapterException)?.Url;
            readingToken = originalPage is null ? displayedToken : "";
            ShowReaderError(e.Message, () => ReadChapterAsync(index, percent), originalPage);
        }
        finally { if (session == readerSession) readerBusy = false; }
    }

    private static JsonObject ChapterPayload(int index, JsonObject chapter, JsonArray paragraphs) => new() { ["ci"] = index, ["title"] = chapter.S("title"), ["paras"] = paragraphs.DeepClone() };
    private async Task<string> ReaderScriptAsync(string function, params JsonNode?[] args) =>
        await readerWeb!.ExecuteScriptAsync($"window.reader.{function}({string.Join(",", args.Select(a => a?.ToJsonString() ?? "null"))})");

    private void HandleReaderMessage(JsonNode message)
    {
        switch (message.S("type"))
        {
            case "progress" when !readerBusy:
                var index = message.I("ci", readingIndex);
                if (index < 0 || index >= readerChapters.Length) return;
                var percent = Math.Clamp(message.D("pct"), 0, 1);
                if (index != readingIndex || Math.Abs(readingPercent - percent) > 0.0001)
                { readingIndex = index; readingPercent = percent; progressDirty = true; }
                UpdateReaderControls(message.I("page"), message.I("pages"));
                break;
            case "needNext" when !appending && !prefetchFailed: _ = RunAsync(() => AppendChapterAsync(message.I("ci") + 1)); break;
            case "retryNext": prefetchFailed = false; _ = RunAsync(() => AppendChapterAsync(message.I("ci") + 1)); break;
            case "chapter" when !readerBusy: _ = RunAsync(() => ReadChapterAsync(readingIndex + Math.Sign(message.I("direction")))); break;
            case "escape": HandleReaderEscape(); break;
            case "toc": ToggleDirectory(); break;
            case "bookmark": _ = RunAsync(AddBookmarkAsync); break;
            case "fullscreen": ToggleFullScreen(); break;
            case "toggleBars": SetReaderBars(readerTop!.Visibility != Visibility.Visible); break;
            case "font": _ = RunAsync(() => AdjustReaderFontAsync(Math.Sign(message.I("direction")))); break;
        }
    }

    private async Task AppendChapterAsync(int next)
    {
        var book = readerBook;
        if (book is null || readerBusy || appending || prefetchFailed || !store.Settings.B("autoCacheNext", true) || store.Settings.S("pageMode") == "paged" || next < 0 || next >= readerChapters.Length) return;
        var session = readerSession;
        appending = true;
        try
        {
            var chapter = readerChapters[next];
            var (text, index) = await LoadChapterForDisplayAsync(book, next, chapter, session);
            if (session == readerSession && !closing)
                await ReaderScriptAsync("append", ChapterPayload(index, chapter, text));
        }
        catch (Exception)
        {
            if (session != readerSession || closing) return;
            prefetchFailed = true;
            await ReaderScriptAsync("showNextRetry");
        }
        finally { if (session == readerSession) appending = false; }
    }

    private void ShowReaderLoading() => SetReaderStatus(Ui.Text("正在读取章节…", 15, true));

    private void ShowReaderError(string description, Func<Task>? retry = null, Uri? originalPage = null)
    {
        var actions = new List<UIElement>();
        if (retry is not null) actions.Add(ActionButton("重试", retry));
        if (originalPage is not null) actions.Add(ActionButton("打开原网页", async () =>
        {
            await CloseReaderAsync();
            await OpenWebPageAsync(originalPage, fromShelf: true);
        }));
        if (readerBook is { } book && book.S("kind") != "local") actions.Add(ActionButton("换源", () => SwitchSourceAsync(book)));
        actions.Add(ActionButton("返回书架", CloseReaderAsync));
        SetReaderStatus(Ui.Text(description), Ui.AdaptiveCommands(actions.ToArray()));
    }
    private void SetReaderStatus(params UIElement[] children)
    {
        var panel = (StackPanel)readerError!;
        panel.Children.Clear();
        panel.Spacing = 16;
        panel.MaxWidth = 480;
        panel.Margin = new(18);
        foreach (var child in children) panel.Children.Add(child);
        panel.Visibility = Visibility.Visible;
        readerWeb!.Visibility = Visibility.Collapsed;
    }

    private void UpdateReaderControls(int page = 0, int pages = 0)
    {
        if (readerBook is null || readingIndex >= readerChapters.Length) return;
        var inPages = store.Settings.S("pageMode") == "paged";
        readerPrevious!.Content = inPages ? "上一页" : "上一章";
        readerNext!.Content = inPages ? "下一页" : "下一章";
        readerPrevious.IsEnabled = inPages || readingIndex > 0;
        readerNext.IsEnabled = inPages || readingIndex + 1 < readerChapters.Length;
        readerTitle!.Text = readerBook.S("name") + "  ·  " + readerChapters[readingIndex].S("title");
        readerPosition!.Text = inPages && pages > 0 ? $"第 {page} / {pages} 页" :
            readerChapters[readingIndex].B("browserRendered") ? $"{readingPercent:P0}" : $"第 {readingIndex + 1} / {readerChapters.Length} 章";
    }
    private async Task StepReaderAsync(int direction)
    {
        if (readerBook is null || readerBusy) return;
        if (store.Settings.S("pageMode") == "paged") await ReaderScriptAsync("step", JsonValue.Create(direction));
        else await ReadChapterAsync(readingIndex + direction);
    }
    private void SetReaderBars(bool visible)
    {
        readerTop!.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        readerBottom!.Visibility = readerTop.Visibility;
    }
    private void HandleReaderEscape()
    {
        if (readerSplit?.IsPaneOpen == true) readerSplit.IsPaneOpen = false;
        else if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ToggleFullScreen();
        else _ = RunAsync(CloseReaderAsync);
    }

    private void ToggleDirectory()
    {
        if (readerSplit is null || readerBook is null) return;
        if (readerSplit.IsPaneOpen) { readerSplit.IsPaneOpen = false; return; }
        var tabs = new Pivot { Margin = new(8, 4, 8, 0) };
        var titles = readerChapters.Select(c => c.S("title")).ToArray();
        var toc = new ListView { ItemsSource = titles, SelectedIndex = titles.Length > readingIndex ? readingIndex : -1, IsItemClickEnabled = true };
        toc.ItemClick += (_, e) =>
        {
            var index = e.ClickedItem is string title ? Array.FindIndex(titles, s => ReferenceEquals(title, s)) : -1;
            if (index < 0) index = toc.SelectedIndex;
            readerSplit.IsPaneOpen = false;
            if (index >= 0) _ = RunAsync(() => ReadChapterAsync(index));
        };
        if (toc.SelectedItem is not null) toc.Loaded += (_, _) => toc.ScrollIntoView(toc.SelectedItem);
        tabs.Items.Add(new PivotItem { Header = "目录", Content = toc });
        var items = store.State["bookmarks"].Objects().Where(b => b.S("bookId") == readerBook.S("id")).OrderByDescending(b => b.D("at")).ToArray();
        var bookmarks = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, ItemsSource = items.Select(b => new BookmarkRow(b)).ToArray(),
            ItemTemplate = Ui.Template("""<TextBlock Text="{Binding Title}" Padding="6,9" TextWrapping="Wrap"/>""") };
        bookmarks.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not BookmarkRow row) return;
            readerSplit.IsPaneOpen = false;
            _ = RunAsync(async () =>
            {
                var chapter = await ChapterMatchAsync((JsonArray)readerBook["chapters"]!, row.Bookmark.S("title"), row.Bookmark.I("ci"), readerChapters.Length);
                await ReadChapterAsync(chapter, row.Bookmark.D("pct"));
            });
        };
        bookmarks.ContainerContentChanging += (_, e) =>
        {
            if (e.InRecycleQueue || e.Item is not BookmarkRow row) return;
            var menu = new MenuFlyout();
            AddMenuAction(menu, "删除书签", async () =>
            {
                store.State["bookmarks"]!.AsArray().Remove(row.Bookmark);
                await store.SaveAsync();
                readerSplit.IsPaneOpen = false;
                ToggleDirectory();
                if (readerSplit.Pane is Pivot pivot) pivot.SelectedIndex = 1;
            });
            e.ItemContainer.ContextFlyout = menu;
        };
        tabs.Items.Add(new PivotItem { Header = "书签", Content = items.Length == 0 ? Ui.Text("还没有书签", 14, true) : bookmarks });
        readerSplit.Pane = tabs;
        readerSplit.IsPaneOpen = true;
    }

    private async Task AddBookmarkAsync()
    {
        if (readerBook is null || readerBusy || readerChapters.Length <= readingIndex) return;
        if (readerBook.Parent is null) { ShowNotice("加入书架后可添加书签"); return; }
        var id = readerBook.S("id");
        var entries = store.State["bookmarks"]!.AsArray();
        if (entries.Objects().Any(b => b.S("bookId") == id && b.I("ci") == readingIndex && Math.Abs(b.D("pct") - readingPercent) < 0.015))
        { ShowNotice("当前位置已有书签"); return; }
        entries.Add(new JsonObject
        {
            ["id"] = J.Id("M"), ["bookId"] = id, ["name"] = readerBook.S("name"), ["ci"] = readingIndex,
            ["title"] = readerChapters[readingIndex].S("title"), ["pct"] = readingPercent, ["at"] = J.Now
        });
        await store.SaveAsync();
        ShowNotice("已添加书签");
    }

    private static JsonObject NewProgress(int index, string title, double percent) => new() { ["ci"] = index, ["title"] = title, ["pct"] = percent, ["at"] = J.Now };
    private async Task FlushReadingProgressAsync()
    {
        if (!progressDirty || readerBook is null || readerBook.Parent is null || readingIndex >= readerChapters.Length) return;
        progressDirty = false;
        store.Progress[readerBook.S("id")] = NewProgress(readingIndex, readerChapters[readingIndex].S("title"), readingPercent);
        try { await store.SaveAsync(); }
        catch { progressDirty = true; throw; }
    }
    private async Task CloseReaderAsync()
    {
        if (readerBook is null) return;
        StopSpeech();
        await FlushReadingProgressAsync();
        ++readerSession;
        ResetRenderedChapters();
        readingToken = "";
        readerBook = null;
        readerBusy = false;
        readerChapters = [];
        localChapters = null;
        readerSurface!.Visibility = Visibility.Collapsed;
        SetFullScreen(readerWasFullScreen);
        navigation.Visibility = Visibility.Visible;
        SelectPage("shelf");
    }

    private JsonObject ReadingAppearance() => new()
    {
        ["fontSize"] = Math.Clamp(store.Settings.D("fontSize", 22), 14, 42),
        ["lineHeight"] = Math.Clamp(store.Settings.D("lineHeight", 1.7), 1.2, 2.6),
        ["paraGapEm"] = Math.Clamp(store.Settings.D("paraGapEm", .5), 0, 2),
        ["readingWidth"] = Math.Clamp(store.Settings.D("readingWidth", 760), 420, 1400),
        ["fontFamily"] = store.Settings.S("fontFamily", "serif"),
        ["theme"] = ResolveReaderTheme(),
        ["pageMode"] = store.Settings.S("pageMode", "scroll"),
        ["webChapterPrefetch"] = readerBook?.S("kind") is "web" or "comic"
    };
    private string ResolveReaderTheme() => store.Settings.S("theme", "auto") switch
    {
        "paper" => "paper",
        "sepia" => "sepia",
        "green" => "green",
        "night" => "night",
        _ => root.ActualTheme == ElementTheme.Dark ? "night" : "paper"
    };

    private async Task RefreshReaderThemeAsync()
    {
        if (readerBook is null || readingToken == "") return;
        await ReaderScriptAsync("theme", JsonValue.Create(ResolveReaderTheme()));
    }
    private async Task AdjustReaderFontAsync(int direction)
    {
        store.Settings["fontSize"] = Math.Clamp(store.Settings.I("fontSize", 22) + direction, 14, 42);
        await RefreshReaderAppearanceAsync(false);
        await store.SaveAsync();
    }
    private async Task RefreshReaderAppearanceAsync(bool modeChanged)
    {
        if (readerBook is null || readingToken == "") return;
        if (modeChanged) await ReadChapterAsync(readingIndex, readingPercent);
        else await ReaderScriptAsync("settings", ReadingAppearance());
    }
}

public sealed record BookmarkRow(JsonObject Bookmark)
{
    public string Title => Bookmark.S("title") + $"  ·  {Bookmark.D("pct"):P0}";
}
