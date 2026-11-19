using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Legado.UI3.Core;
using Windows.System;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<SearchResult> searchResults = [];
    private readonly Dictionary<string, SearchResult> searchByName = new(StringComparer.OrdinalIgnoreCase);
    private string searchKeyword = "";
    private bool startSearchOnOpen;
    private bool focusSearchOnOpen;
    private CancellationTokenSource? currentSearch;

    private FrameworkElement SearchPage()
    {
        var mode = Ui.Combo(["书源", "网页"], store.Settings.S("searchMode") == "web" ? 1 : 0);
        var webEngine = Ui.Combo(["必应", "百度", "搜狗", "360"], Math.Clamp(store.Settings.I("searchEngine"), 0, 3));
        var query = new TextBox { Text = searchKeyword, PlaceholderText = "书名或作者" };
        var status = Ui.Text("", 13, true);
        var run = new Button { Content = "搜索", Style = (Style)Application.Current.Resources["LegadoPrimaryButtonStyle"] };
        var stop = Button("停止", () => currentSearch?.Cancel());
        stop.Visibility = Visibility.Collapsed;
        var list = new ListView
        {
            ItemsSource = searchResults, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = Ui.Template("""
                <Grid Padding="8,10" ColumnSpacing="12">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="40"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <Border Background="{ThemeResource SubtleFillColorSecondaryBrush}" CornerRadius="4"><Image Source="{Binding Cover}" Height="54" Stretch="UniformToFill"/></Border>
                  <StackPanel Grid.Column="1" Spacing="4" VerticalAlignment="Center">
                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding Detail}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" FontSize="13" TextWrapping="Wrap"/>
                  </StackPanel>
                </Grid>
                """)
        };
        list.ItemClick += (_, e) => { if (e.ClickedItem is SearchResult result) _ = RunAsync(() => PreviewResultAsync(result)); };
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.InRecycleQueue || e.Item is not SearchResult result) return;
            if (result.Hits[0].S("kind") != "web") { e.ItemContainer.ContextFlyout = null; return; }
            var menu = new MenuFlyout();
            AddMenuAction(menu, "打开网页", () => OpenWebPageAsync(Network.WebUri(result.Hits[0].S("bookUrl"))));
            e.ItemContainer.ContextFlyout = menu;
        };

        void UpdateMode()
        {
            webEngine.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            query.PlaceholderText = mode.SelectedIndex == 1 ? "书名、作者或网页链接" : "书名或作者";
        }
        mode.SelectionChanged += (_, _) =>
        {
            currentSearch?.Cancel();
            searchResults.Clear();
            searchByName.Clear();
            status.Text = "";
            store.Settings["searchMode"] = mode.SelectedIndex == 1 ? "web" : "src";
            SaveSoon();
            UpdateMode();
        };
        webEngine.SelectionChanged += (_, _) => { store.Settings["searchEngine"] = webEngine.SelectedIndex; SaveSoon(); };
        UpdateMode();
        async Task SearchNow()
        {
            searchKeyword = query.Text.Trim();
            if (searchKeyword == "") { query.Focus(FocusState.Programmatic); return; }
            currentSearch?.Cancel();
            using var cancellation = new CancellationTokenSource();
            currentSearch = cancellation;
            var token = cancellation.Token;
            stop.Visibility = Visibility.Visible;
            status.Text = "正在搜索…";
            searchResults.Clear();
            searchByName.Clear();
            try
            {
                await initialization;
                var keyword = searchKeyword;
                if (mode.SelectedIndex == 1)
                {
                    if (keyword.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) keyword = "https://" + keyword;
                    if (Uri.TryCreate(keyword, UriKind.Absolute, out var target) && target.Scheme is "http" or "https")
                    {
                        await OpenWebPageAsync(Network.WebUri(target.AbsoluteUri));
                        return;
                    }
                    else
                    {
                        var prefix = new[] { "https://www.bing.com/search?q=", "https://www.baidu.com/s?wd=", "https://www.sogou.com/web?query=", "https://www.so.com/s?q=" }[Math.Clamp(webEngine.SelectedIndex, 0, 3)];
                        var groups = await engine.RunAsync("webSearch", new() { ["url"] = prefix + Uri.EscapeDataString(keyword), ["key"] = keyword });
                        token.ThrowIfCancellationRequested();
                        foreach (var group in groups.Objects())
                            foreach (var site in group["sites"].Objects())
                                AddSearchHit(new() { ["name"] = group.S("name"), ["author"] = group.S("author"), ["bookUrl"] = site.S("url"), ["sourceName"] = site.S("label"), ["kind"] = "web" });
                    }
                }
                else
                {
                    var targets = SearchSources();
                    if (targets.Length == 0) { status.Text = "请先在「书源」中导入并启用可用书源"; return; }
                    int completed = 0;
                    var failures = await SearchAcrossAsync(targets, keyword, token, AddSearchHit, () => status.Text = $"已检索 {++completed} / {targets.Length} 个书源");
                    if (searchResults.Count == 0 && failures.Count == targets.Length)
                    {
                        status.Text = "书源请求失败：" + failures[0];
                        return;
                    }
                }
                status.Text = searchResults.Count == 0 ? "没有找到书籍" : $"{searchResults.Count} 本书";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { if (currentSearch == cancellation) status.Text = "已停止搜索"; }
            catch (Exception e) { if (!token.IsCancellationRequested) status.Text = e.Message; }
            finally { if (currentSearch == cancellation) { currentSearch = null; stop.Visibility = Visibility.Collapsed; } }
        }
        run.Click += (_, _) => _ = RunAsync(SearchNow);
        query.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = RunAsync(SearchNow); } };
        if (startSearchOnOpen)
        {
            startSearchOnOpen = false;
            query.Loaded += (_, _) => _ = RunAsync(SearchNow);
        }
        if (focusSearchOnOpen)
        {
            focusSearchOnOpen = false;
            query.Loaded += (_, _) => query.Focus(FocusState.Programmatic);
        }
        var filter = Ui.Columns("Auto,*,Auto", mode, query, Ui.Row(run, stop));
        return PageLayout(PageHeader("搜索"), filter, Ui.Row(webEngine, status), list);
    }

    private void AddSearchHit(JsonObject hit)
    {
        var name = hit.S("name");
        if (searchByName.TryGetValue(name, out var match)) match.Add(hit);
        else
        {
            var result = new SearchResult(hit);
            searchByName.Add(name, result);
            searchResults.Add(result);
        }
    }

    private JsonObject[] SearchSources(JsonObject? book = null)
    {
        var enabled = store.Sources.Objects().Where(s => s.B("enabled", true) && s.S("searchUrl") != "").ToArray();
        var configured = book is null ? store.Settings.I("searchLimit", 40) : store.Settings.I("switchLimit", 24);
        var available = configured != 0 && enabled.Any(s => s.S("quality") != "dead")
            ? enabled.Where(s => s.S("quality") != "dead") : enabled;
        var sourceId = book?.S("sourceId");
        var used = store.Books.Objects().Select(b => b.S("sourceId")).ToHashSet();
        var ordered = available.OrderBy(s => s.S("id") == sourceId ? 0 : used.Contains(s.S("id")) ? 1 : s.S("quality") switch
        {
            "great" or "ok" => 2, "dead" => 4, _ => 3
        });
        if (configured == 0) return ordered.ToArray();
        return ordered.Take(Math.Clamp(configured, 1, 120)).ToArray();
    }

    private async Task<List<string>> SearchAcrossAsync(JsonObject[] targets, string keyword, CancellationToken token, Action<JsonObject> onResult, Action onComplete)
    {
        int cursor = 0;
        var errors = new List<string>();
        async Task Worker()
        {
            while (cursor < targets.Length && !token.IsCancellationRequested)
            {
                var source = targets[cursor++];
                try
                {
                    var result = await engine.RunAsync("search", new() { ["source"] = source.DeepClone(), ["key"] = keyword, ["timeout"] = 9000 }, token);
                    if (token.IsCancellationRequested) return;
                    foreach (var hit in result.Objects()) onResult(hit);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception e) { errors.Add(source.S("name") + "：" + e.Message); }
                onComplete();
            }
        }
        int workers = Math.Min(targets.Length, Math.Clamp(store.Settings.I("maxConcurrent", 3), 1, 6));
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker()));
        token.ThrowIfCancellationRequested();
        return errors;
    }

    private async Task PreviewResultAsync(SearchResult group)
    {
        await initialization;
        var candidates = group.Hits.ToArray();
        JsonObject result = candidates[0];
        if (candidates.Length > 1)
        {
            var picker = Ui.Combo(candidates.Select(h => h.S("sourceName")), 0, 380);
            if (await DialogAsync("选择来源", picker, "查看") != ContentDialogResult.Primary) return;
            result = candidates[Math.Max(0, picker.SelectedIndex)];
        }
        var book = await BuildOnlineBookAsync(result);
        await ShowBookPreviewAsync(book);
    }

    private async Task ShowBookPreviewAsync(JsonObject book)
    {
        var contents = book["chapters"].Objects().Select(c => c.S("title")).ToArray();
        var chapters = new ListView { ItemsSource = contents, SelectionMode = ListViewSelectionMode.Single, SelectedIndex = contents.Length > 0 ? 0 : -1, Height = 220 };
        var details = Ui.Stack(Ui.Text(book.S("author", "佚名") + "  ·  " + book.S("sourceName"), 14, true));
        if (book.S("intro") is { Length: > 0 } intro) details.Children.Add(Ui.Text(intro.Length > 420 ? intro[..420] + "…" : intro, 13));
        details.Children.Add(contents.Length > 0 ? chapters : Ui.Text("暂时无法获取目录", 14, true));
        details.MaxWidth = 520;
        var action = await DialogAsync(book.S("name"), Ui.Scroll(details), "加入书架", contents.Length > 0 ? "试读" : null);
        if (action == ContentDialogResult.Primary)
        {
            var exists = store.Books.Objects().Any(b => b.S("bookUrl") == book.S("bookUrl"));
            if (exists) { ShowNotice("这本书已在书架"); return; }
            store.Books.Add(book);
            await store.SaveAsync();
            ShowNotice($"已加入《{book.S("name")}》");
        }
        else if (action == ContentDialogResult.Secondary) await OpenReaderAsync(book, Math.Max(0, chapters.SelectedIndex), 0);
    }

    private async Task<JsonObject> BuildOnlineBookAsync(JsonObject hit, JsonObject? inspected = null)
    {
        var web = hit.S("kind") == "web";
        var book = new JsonObject
        {
            ["id"] = J.Id("O"), ["kind"] = web ? "web" : "online", ["name"] = hit.S("name"),
            ["author"] = hit.S("author"), ["sourceId"] = hit.S("sourceId"), ["sourceName"] = hit.S("sourceName"),
            ["bookUrl"] = hit.S("bookUrl"), ["intro"] = hit.S("intro"), ["cover"] = hit.S("coverUrl"),
            ["groupId"] = "g_default", ["createdAt"] = J.Now, ["updatedAt"] = J.Now, ["chapters"] = new JsonArray()
        };
        if (web && inspected is not null) ApplyWebRules(book, inspected, Network.WebUri(book.S("bookUrl")), book.S("bookUrl"));
        else await FetchTocAsync(book);
        return book;
    }

    private JsonObject? SourceForBook(JsonObject book)
    {
        if (book.S("kind") is "web" or "comic")
        {
            var rules = book["webRule"] as JsonObject;
            var site = rules.S("siteUrl");
            if (site.Length == 0) site = Network.WebUri(book.S("bookUrl")).GetLeftPart(UriPartial.Authority);
            return new JsonObject
            {
                ["id"] = "__web", ["url"] = site, ["name"] = "网页抓取", ["headers"] = new JsonObject { ["Referer"] = site + "/" },
                ["ruleToc"] = rules?["toc"]?.DeepClone(), ["ruleContent"] = rules?["content"]?.DeepClone()
            };
        }
        var id = book.S("sourceId");
        var direct = store.Sources.Objects().FirstOrDefault(s => s.S("id") == id && id.Length > 0);
        if (direct is not null) return direct;
        var origin = book.S("origin", book.S("originRaw")).TrimEnd('/');
        var name = book.S("originName", book.S("sourceName"));
        return origin.Length > 0 ? store.Sources.Objects().FirstOrDefault(s => s.S("url").TrimEnd('/').Equals(origin, StringComparison.OrdinalIgnoreCase) && s.S("name") == name) : null;
    }

    private async Task<JsonArray> FetchTocAsync(JsonObject book)
    {
        var url = Network.WebUri(book.S("bookUrl"));
        JsonArray chapters;
        if (book.S("kind") is "web" or "comic")
        {
            var page = await engine.RunAsync("sniff", new() { ["url"] = url.AbsoluteUri });
            if (page.B("serp")) throw new IOException("请使用具体的书籍页面链接");
            var data = page?["d"] as JsonObject ?? throw new IOException("该页面无法读取");
            return ApplyWebRules(book, data, url, page.S("url", url.AbsoluteUri));
        }
        else
        {
            var source = SourceForBook(book) ?? throw new IOException("书源已移除或不可用，请换源");
            var request = new JsonObject { ["source"] = source.DeepClone(), ["url"] = url.AbsoluteUri, ["expect"] = book.I("total") };
            var selected = await engine.RunAsync("toc", request);
            var preferred = book.S("tocUrl");
            if (Uri.TryCreate(preferred, UriKind.Absolute, out var preferredUri) && preferredUri != url && (selected?["chapters"] as JsonArray)?.Count < book.I("total"))
            {
                try
                {
                    request["url"] = preferredUri.AbsoluteUri;
                    var fallback = await engine.RunAsync("toc", request);
                    if ((fallback?["chapters"] as JsonArray)?.Count > (selected?["chapters"] as JsonArray)?.Count) selected = fallback;
                }
                catch (IOException) { }
            }
            chapters = J.Array(selected?["chapters"].Objects() ?? []);
            IdentityDetails(book, selected?["info"]);
            book["tocUrl"] = selected.S("tocUrl", url.AbsoluteUri);
            book["origin"] = source.S("url");
            book["originName"] = source.S("name");
        }
        book["chapters"] = chapters;
        book["total"] = chapters.Count;
        book["updatedAt"] = J.Now;
        return chapters;
    }

    private static JsonArray ApplyWebRules(JsonObject book, JsonObject data, Uri url, string detectedUrl)
    {
        var info = data["info"];
        var toc = data["toc"];
        var content = data["content"];
        var chapters = J.Array(toc?["chapters"].Objects() ?? []);
        if (chapters.Count == 0 && (content is not null || data["comic"] is not null))
            chapters.Add(new JsonObject { ["url"] = detectedUrl, ["title"] = info.S("name", book.S("name")) });
        book["webRule"] = new JsonObject
        {
            ["siteUrl"] = url.GetLeftPart(UriPartial.Authority),
            ["toc"] = toc?["rule"]?.DeepClone(),
            ["content"] = new JsonObject { ["content"] = content.S("selector") }
        };
        IdentityDetails(book, info, replaceName: true);
        book["tocUrl"] = detectedUrl;
        book["chapters"] = chapters;
        book["total"] = chapters.Count;
        book["updatedAt"] = J.Now;
        return chapters;
    }

    private static void IdentityDetails(JsonObject book, JsonNode? info, bool replaceName = false)
    {
        if (replaceName && info.S("name") is { Length: > 0 } name) book["name"] = name;
        if (info.S("author") is { Length: > 0 } author) book["author"] = author;
        if (info.S("intro") is { Length: > 0 } intro) book["intro"] = intro;
        if (info.S("coverUrl", info.S("cover")) is { Length: > 0 } cover) book["cover"] = cover;
    }

    private async Task<int> ChapterMatchAsync(JsonArray chapters, string title, int previous, int oldTotal)
    {
        if (chapters.Count == 0) return 0;
        var args = new JsonObject { ["chapters"] = new JsonArray(chapters.Objects().Select(c => (JsonNode?)JsonValue.Create(c.S("title"))).ToArray()), ["title"] = title, ["index"] = previous, ["previousTotal"] = oldTotal };
        var match = await engine.RunAsync("matchChapter", args);
        return Math.Clamp(match?.GetValue<int>() ?? previous, 0, chapters.Count - 1);
    }

    private async Task<JsonArray> RefreshTocAsync(JsonObject book)
    {
        await initialization;
        var progress = store.Progress[book.S("id")];
        var oldTotal = book.I("total");
        var copy = book.Copy();
        var chapters = await FetchTocAsync(copy);
        if (chapters.Count == 0) throw new IOException("没有获取到目录");
        var match = progress is null ? 0 : await ChapterMatchAsync(chapters, progress.S("title"), progress.I("ci"), oldTotal);
        foreach (var (key, value) in copy) book[key] = value?.DeepClone();
        if (progress is JsonObject position)
        {
            position["ci"] = match;
            position["title"] = chapters[match].S("title");
        }
        if (book.Parent is not null) await store.SaveAsync();
        return (JsonArray)book["chapters"]!;
    }

    private async Task SwitchSourceAsync(JsonObject book)
    {
        await initialization;
        var targets = SearchSources(book);
        if (targets.Length == 0) { ShowNotice("先在「书源」中启用可用的网络书源", true); return; }
        var query = new TextBox { Header = "书名", Text = book.S("name"), MinWidth = 320 };
        var status = Ui.Text($"{targets.Length} 个候选书源", 13, true);
        var hits = new ObservableCollection<SwitchHit>();
        var list = new ListView { ItemsSource = hits, Height = 280, SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = Ui.Template("""<StackPanel Spacing="4" Padding="4,6"><TextBlock Text="{Binding Name}"/><TextBlock Text="{Binding Description}" FontSize="13" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/></StackPanel>""") };
        CancellationTokenSource? search = null;
        var run = Button("搜索", () => { _ = RunAsync(FindAsync); });
        var stop = Button("停止", () => search?.Cancel());
        async Task FindAsync()
        {
            search?.Cancel();
            using var cancellation = new CancellationTokenSource();
            search = cancellation;
            hits.Clear();
            int completed = 0;
            var keyword = query.Text.Trim();
            if (keyword == "") { search = null; return; }
            status.Text = "正在查找其他来源…";
            try
            {
                await SearchAcrossAsync(targets, keyword, cancellation.Token, h => hits.Add(new(h)), () => status.Text = $"已检索 {++completed} / {targets.Length} 个书源");
                status.Text = hits.Count == 0 ? "未找到可换的书源" : $"找到 {hits.Count} 个版本";
            }
            catch (OperationCanceledException) { if (search == cancellation) status.Text = "已停止搜索"; }
            finally { if (search == cancellation) search = null; }
        }
        var dialog = new ContentDialog
        {
            Title = "换源", Content = Ui.Stack(Ui.Columns("*,Auto", query, Ui.Row(run, stop)), status, list), XamlRoot = root.XamlRoot,
            PrimaryButtonText = "切换到所选", CloseButtonText = "取消", IsPrimaryButtonEnabled = false
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is SwitchHit;
        dialog.Opened += (_, _) => _ = RunAsync(FindAsync);
        var choice = await dialog.ShowAsync();
        search?.Cancel();
        if (choice != ContentDialogResult.Primary || list.SelectedItem is not SwitchHit hit) return;

        var previous = store.Progress[book.S("id")];
        int chapter = ReferenceEquals(readerBook, book) ? readingIndex : previous.I("ci");
        double position = ReferenceEquals(readerBook, book) ? readingPercent : previous.D("pct");
        var title = (book["chapters"] as JsonArray) is { } old && chapter >= 0 && chapter < old.Count ? old[chapter].S("title") : previous.S("title");
        var replacement = await BuildOnlineBookAsync(hit.Data);
        var next = (JsonArray)replacement["chapters"]!;
        if (next.Count == 0) throw new IOException("所选书源没有可用目录");
        var index = await ChapterMatchAsync(next, title, chapter, book.I("total"));
        replacement["id"] = book.S("id");
        _ = await LoadOnlineChapterAsync(replacement, next[index]!.AsObject());
        foreach (var key in new[] { "kind", "bookUrl", "tocUrl", "sourceId", "sourceName", "origin", "originName", "chapters", "total", "updatedAt" })
            book[key] = replacement[key]?.DeepClone();
        if (book.Parent is not null)
        {
            store.Progress[book.S("id")] = NewProgress(index, next[index].S("title"), position);
            await store.SaveAsync();
        }
        if (ReferenceEquals(readerBook, book)) await OpenReaderAsync(book, index, position);
        else if (currentPage == "shelf") Navigate("shelf");
        ShowNotice($"已切换到「{book.S("sourceName")}」");
    }
}

public sealed record SwitchHit(JsonObject Data)
{
    public string Name => Data.S("name");
    public string Description => Data.S("sourceName") + "  ·  " + Data.S("author");
}

public sealed class SearchResult(JsonObject hit) : INotifyPropertyChanged
{
    public List<JsonObject> Hits { get; } = [hit];
    public string Name => Hits[0].S("name");
    public string? Cover => Uri.TryCreate(Hits[0].S("coverUrl"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    public string Detail => string.Join("  ·  ", new[] { Hits[0].S("author"), Hits.Count > 1 ? $"{Hits.Count} 个来源" : Hits[0].S("sourceName") }.Where(v => v != ""));
    public void Add(JsonObject entry)
    {
        if (Hits.Any(h => h.S("bookUrl") == entry.S("bookUrl") && h.S("sourceId") == entry.S("sourceId"))) return;
        Hits.Add(entry);
        PropertyChanged?.Invoke(this, new(nameof(Detail)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
