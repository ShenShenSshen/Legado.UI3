using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private async Task RunSelfTestIfRequestedAsync()
    {
        if (!File.Exists(Path.Combine(store.Root, ".smoke-test"))) return;
        var report = new List<string>();
        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException(name);
            report.Add("PASS " + name);
        }
        try
        {
            await initialization;
            Check(true, "WinUI and both WebView2 hosts initialized");
            foreach (var route in new[] { "search", "explore", "sources", "addons", "settings", "shelf" }) SelectPage(route);
            await Task.Delay(200);
            Check(true, "all navigation pages constructed");
            await TestUpdateDownloadAsync(Check);
            SelectPage("addons");
            await OpenAddonAsync(new JsonObject
            {
                ["name"] = "扩展验证", ["type"] = "html",
                ["html"] = "<script>parent.postMessage({moread:'toast',text:'扩展桥接通过'}, '*');</script>"
            });
            await WaitForAsync(() => Task.FromResult(notice.Message == "扩展桥接通过"));
            Check(true, "legacy HTML extension runs in its isolated panel");
            SelectPage("shelf");
            if (Environment.GetEnvironmentVariable("LEGADO_UI3_CHECK_GITHUB") == "1")
            {
                using var network = new Network();
                const string file = "RoutineMiku/MoRead-Windows";
                const string revision = "ade98202cf77ed05b43788789fa21c2d057d6c13";
                const string fixture = "test/ud-test/data.json";
                var fromFilePage = await network.DownloadSourceAsync($"https://github.com/{file}/blob/{revision}/{fixture}", CancellationToken.None);
                var fromRaw = await network.DownloadSourceAsync($"https://raw.githubusercontent.com/{file}/{revision}/{fixture}", CancellationToken.None);
                var candidates = SourceImport.Preview(fromFilePage, []);
                Check(fromFilePage == fromRaw && candidates.Count == 1 && candidates[0].Valid &&
                      candidates[0].Url == "https://novel.example.com", "actual GitHub file page and Raw both produce a valid source preview");
            }

            var raw = """{"bookSourceName":"样例 A","bookSourceUrl":"https://example.org/","enabled":false,"searchUrl":"/search?q={{key}}"}""";
            var variant = raw.Replace("样例 A", "样例 B", StringComparison.Ordinal);
            var rows = SourceImport.Preview("[" + raw + "," + variant + "," + raw + ",42]", store.Sources.Objects());
            Check(rows.Count == 4 && rows[0].Selected && rows[1].Selected && !rows[2].Selected && !rows[3].Valid, "preview, invalid and duplicate classification");
            Check(await store.ApplySourcesAsync(rows, false) == (2, 0), "two rules on one site imported");
            var updated = SourceImport.Preview(raw.Replace("false", "true"), store.Sources.Objects());
            updated[0].Selected = true;
            var sourceId = store.Sources[0].S("id");
            Check(await store.ApplySourcesAsync(updated, true) == (0, 1) && store.Sources[0].S("id") == sourceId && !store.Sources[0].B("enabled"), "update retains ID and enabled state");

            SelectPage("sources");
            ToggleSourceSelection();
            sourceList!.SelectAll();
            Check(sourceList.SelectedItems.Count == 2, "native multi-selection selects filtered sources");
            sourceQuery = "样例 A";
            RefreshSources();
            Check(sourceList.SelectedItems.Count == 0 && sourceList.Items.Count == 1, "filter clears hidden selections");
            sourceQuery = "";
            var readFixture = Path.Combine(store.Root, "长篇示例.txt");
            var body = string.Join("\n", Enumerable.Range(1, 110).Select(i => $"{i:000} 页上的文字需要留在原来的顺序。Legado·UI3正在测试一段比较长的正文，让读者可以翻页和继续看下一章。"));
            await File.WriteAllTextAsync(readFixture, "第一章 山路\n" + body + "\n第二章 灯火\n" + body);
            var localBook = await local.ImportAsync(readFixture);
            Check(localBook.I("total") == 2, "TXT imported and split into chapters");
            if (Environment.GetEnvironmentVariable("LEGADO_UI3_EPUB_FIXTURE") is { Length: > 0 } epub)
            {
                var ebook = await local.ImportAsync(epub);
                Check(ebook.I("total") > 0, "upstream EPUB parsed with the native ZIP / WebView2 parser");
            }

            var readingWindowSize = AppWindow.Size;
            var readingWindowPosition = AppWindow.Position;
            await OpenReaderAsync(localBook);
            await Task.Delay(250);
            Check(readingToken.Length > 0 && (await readerWeb!.ExecuteScriptAsync("document.querySelectorAll('.chapter p').length")).Trim() == "110", "native reader opens TXT in scroll mode");
            Check(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { HasTitleBar: false, HasBorder: true } &&
                root.RowDefinitions[0].Height.Value == 0 && AppWindow.Size.Equals(readingWindowSize) &&
                AppWindow.Position.Equals(readingWindowPosition), "entering the reader hides the Windows title bar without changing the window bounds");
            SetReaderBars(false);
            ShowNotice("验证通知栏布局");
            root.UpdateLayout();
            Check(root.RowDefinitions[1].ActualHeight > 0, "an open notification still has room to display");
            notice.IsOpen = false;
            root.UpdateLayout();
            var readerTopEdge = readerWeb!.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()).Y;
            Check(root.RowDefinitions[1].ActualHeight == 0 && readerTopEdge == 0,
                $"closed notification leaves the reading surface at the top edge (row {root.RowDefinitions[1].ActualHeight}, reader {readerTopEdge})");
            SetReaderBars(true);
            Check(await readerWeb!.ExecuteScriptAsync("""
                (() => {
                    const view = document.getElementById('viewport');
                    const style = getComputedStyle(view);
                    return view.scrollHeight > view.clientHeight && style.overflowY === 'auto' &&
                        style.scrollbarWidth === 'none' && view.offsetWidth === view.clientWidth;
                })()
                """) == "true", "scroll mode hides the scrollbar without disabling scrolling");
            SetFullScreen(true);
            Check(AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen, "fullscreen remains available by request");
            SetFullScreen(false);
            Check(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { HasTitleBar: false },
                "returning to windowed reading keeps the title bar hidden");
            var initialFont = store.Settings.S("fontFamily", "serif");
            store.Settings["fontFamily"] = "jetbrains";
            await RefreshReaderAppearanceAsync(false);
            await WaitForAsync(async () => await readerWeb!.ExecuteScriptAsync("""
                getComputedStyle(document.querySelector('#book')).fontFamily.includes('JetBrains Mono') &&
                [...document.fonts].some(face => face.family.includes('JetBrains Mono') && face.status === 'loaded')
                """) == "true");
            Check(true, "reader loads the bundled JetBrains Mono font without a system installation");
            store.Settings["fontFamily"] = initialFont;
            await RefreshReaderAppearanceAsync(false);
            await readerWeb!.ExecuteScriptAsync("reader.highlight(0,60)");
            await Task.Delay(360);
            Check(readingPercent > .3 && readingPercent < .8, "scroll position tracked");
            await AddBookmarkAsync();
            var saved = readingPercent;
            await CloseReaderAsync();
            Check(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { HasTitleBar: true } &&
                root.RowDefinitions[0].Height.Value == 39, "leaving the reader restores the Windows title bar");
            await OpenReaderAsync(localBook);
            await Task.Delay(320);
            Check(Math.Abs(saved - readingPercent) < .08, "scroll position restored");
            await readerWeb.ExecuteScriptAsync("const v=document.getElementById('viewport'); v.scrollTop=v.scrollHeight;");
            await WaitForAsync(async () => await readerWeb.ExecuteScriptAsync("!!document.querySelector('.chapter[data-ci=\"1\"]')") == "true");
            Check(true, "scroll mode appends next chapter");

            store.Settings["pageMode"] = "paged";
            await ReadChapterAsync(0);
            await Task.Delay(230);
            var firstPage = readerPosition?.Text;
            await StepReaderAsync(1);
            await Task.Delay(150);
            Check(firstPage is not null && firstPage.Contains("页") && readerPosition?.Text != firstPage, "paged reader advances through real pages");
            var preservedPage = readerPosition?.Text;
            var savedAppTheme = store.Settings.S("appTheme", "system");
            store.Settings["appTheme"] = "dark";
            ApplyAppTheme();
            await WaitForAsync(async () => await readerWeb.ExecuteScriptAsync("document.documentElement.style.getPropertyValue('--paper')") == "\"#202020\"");
            Check(readerPosition?.Text == preservedPage, "night colors follow the app without moving the current page");
            store.Settings["theme"] = "sepia";
            await RefreshReaderThemeAsync();
            store.Settings["appTheme"] = "light";
            ApplyAppTheme();
            await Task.Delay(100);
            Check(await readerWeb.ExecuteScriptAsync("document.documentElement.style.getPropertyValue('--paper')") == "\"#f5efdf\"", "manual reading color remains independent of app theme");
            store.Settings["theme"] = "auto";
            await RefreshReaderThemeAsync();
            Check(await readerWeb.ExecuteScriptAsync("document.documentElement.style.getPropertyValue('--paper')") == "\"#fafafa\"", "automatic reading color returns to paper in light mode");
            store.Settings["appTheme"] = savedAppTheme;
            ApplyAppTheme();
            await CloseReaderAsync();

            var server = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            server.Start();
            using var cancelServer = new CancellationTokenSource();
            var baseUrl = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
            var serverTask = ServeSmokePagesAsync(server, cancelServer.Token);
            try
            {
                await TestNetworkVoicesAsync(baseUrl, Check);
                var source = SourceImport.Normalize(new JsonObject
                {
                    ["name"] = "测试在线源", ["url"] = baseUrl, ["searchUrl"] = "/search?q={{key}}",
                    ["ruleSearch"] = new JsonObject { ["bookList"] = ".result", ["name"] = "a@text", ["bookUrl"] = "a@href" },
                    ["ruleToc"] = new JsonObject { ["chapterList"] = "#list a", ["chapterName"] = "@text", ["chapterUrl"] = "@href" },
                    ["ruleContent"] = new JsonObject { ["content"] = "article" },
                });
                store.Sources.Add(source);
                await TestExploreAsync(source, Check);
                var result = await engine.RunAsync("search", new() { ["source"] = source.DeepClone(), ["key"] = "纸上旅程" });
                Check(result.Objects().Any(h => h.S("name") == "纸上旅程"), "source engine searches through native network bridge");
                var scripted = source.Copy();
                scripted["searchUrl"] = "@js: java.ajax(baseUrl + '/script-url')";
                var scriptResult = await engine.RunAsync("search", new() { ["source"] = scripted, ["key"] = "纸上旅程" });
                Check(scriptResult.Objects().Any(), "book source JavaScript keeps its synchronous HTTP helpers");
                await DetectOneAsync(source, "纸上旅程", deep: false);
                Check(source.S("quality") == "ok", "detection writes a usable source result");
                var book = await BuildOnlineBookAsync(result.Objects().First());
                Check(book.I("total") >= 3, "source engine obtains the online table of contents");
                store.Books.Add(book);
                await OpenReaderAsync(book, 0);
                await Task.Delay(150);
                var displayed = System.Text.Json.JsonSerializer.Deserialize<string>(await readerWeb.ExecuteScriptAsync("document.querySelector('#book').innerText"));
                Check(readingToken.Length > 0 && displayed?.Contains("样例章节正文") == true, "online chapter rendered");
                await CloseReaderAsync();
                var caches = Directory.GetFiles(Path.Combine(store.Root, "cache")).Length;
                await store.DeleteSourcesAsync([source.S("id"), sourceId]);
                Check(store.Sources.Count == 1 && store.Books.Contains(book) && store.Progress[book.S("id")] is not null && Directory.GetFiles(Path.Combine(store.Root, "cache")).Length == caches, "bulk delete preserves books, progress and chapter cache");
                await OpenReaderAsync(book, 0);
                Check(readingToken.Length > 0, "cached online chapter reads after deleting its source");
                await CloseReaderAsync();
                SelectPage("search");
                var visible = await OpenWebPageAsync(new Uri(baseUrl + "/book"));
                await WaitForAsync(async () => await visible.ExecuteScriptAsync("!!document.getElementById('list')") == "true");
                var inspected = await engine.RunAsync("inspectWeb", new()
                {
                    ["html"] = System.Text.Json.JsonSerializer.Deserialize<string>(await visible.ExecuteScriptAsync("document.documentElement.outerHTML")),
                    ["url"] = baseUrl + "/book"
                });
                Check(inspected?["toc"]?["chapters"] is JsonArray { Count: >= 3 }, "normal web navigation and page capture can identify a book");
                var webUrl = new Uri(baseUrl + "/web-chapter/68");
                await visible.ExecuteScriptAsync("document.cookie='web_fixture=1; Path=/'");
                visible.CoreWebView2.Navigate(webUrl.AbsoluteUri);
                await WaitForAsync(async () => await visible.ExecuteScriptAsync("document.body?.innerText.includes('网页正常加载的章节正文')") == "true");
                var captured = (await engine.RunAsync("inspectWeb", new()
                {
                    ["html"] = System.Text.Json.JsonSerializer.Deserialize<string>(await visible.ExecuteScriptAsync("document.documentElement.outerHTML")),
                    ["url"] = webUrl.AbsoluteUri
                }))!.AsObject();
                var webBook = await BuildOnlineBookAsync(new() { ["kind"] = "web", ["name"] = "动态章节测试", ["bookUrl"] = webUrl.AbsoluteUri }, captured);
                store.Books.Add(webBook);
                var previousMode = store.Settings.S("pageMode");
                var previousAutoAppend = store.Settings.B("autoCacheNext", true);
                store.Settings["pageMode"] = "scroll";
                store.Settings["autoCacheNext"] = false;
                await store.SaveAsync();
                SelectPage("shelf");
                await OpenReaderAsync(webBook);
                async Task CheckDynamicChapterAsync(int number)
                {
                    var text = await readerWeb!.ExecuteScriptAsync("document.querySelector('#book').innerText");
                    Check(ReferenceEquals(readerBook, webBook) && readingToken.Length > 0 &&
                        text.Contains($"第{number}章 动态章节测试") && text.Contains("网页正常加载的章节正文") &&
                        !text.Contains("需要订阅") && !text.Contains("不可见测试正文"), $"dynamic chapter {number} renders only visible text in the native reader");
                }
                await CheckDynamicChapterAsync(68);
                Check(readerPrevious!.IsEnabled && readerNext!.IsEnabled, "dynamic chapter discovers both adjacent chapters without an imported directory");
                await StepReaderAsync(-1);
                await CheckDynamicChapterAsync(67);
                await StepReaderAsync(1);
                await CheckDynamicChapterAsync(68);

                var cacheIdentity = webBook.S("id") + "\n" + webBook.S("sourceId") + "\n" + webUrl.AbsoluteUri;
                var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)));
                var cachePath = Path.Combine(store.Root, "cache", cacheKey + ".json");
                Check(!File.Exists(cachePath), "web access prompts are not cached as chapter content");
                await File.WriteAllTextAsync(cachePath, """["此章为VIP章节，需要订阅后才能继续阅读"]""");
                webBook["chapters"]!.AsArray().Objects().Single(ch => ch.S("url") == webUrl.AbsoluteUri).Remove("browserRendered");
                await CloseReaderAsync();
                await OpenReaderAsync(webBook);
                await CheckDynamicChapterAsync(68);
                var voices = HttpVoice.Parse(baseUrl + "/dynamic-tts.wav?text={{speakText}}").Voices;
                await voiceLibrary.SaveAsync(voices);
                var tts = store.Settings["tts"]!.AsObject();
                tts["voiceURI"] = voices.Single().Id;
                var dynamicIndex = readingIndex;
                try
                {
                    await ReadChapterAsync(dynamicIndex, 1);
                    await ToggleSpeechAsync();
                    var firstSpeech = speech;
                    await WaitForAsync(() => Task.FromResult(speechPlayer?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing));
                    Check(true, "native narration plays the already rendered dynamic chapter");
                    await WaitForAsync(async () => readingIndex == dynamicIndex + 1 && speech is not null &&
                        !ReferenceEquals(speech, firstSpeech) && await readerWeb!.ExecuteScriptAsync("""
                            document.querySelector('.speaking')?.closest('.chapter').querySelector('h1').textContent.startsWith('第69章')
                            """) == "true");
                    Check(true, "finishing narration continues speaking the next dynamic chapter automatically");
                }
                finally { StopSpeech(); tts["voiceURI"] = ""; }

                await ReadChapterAsync(dynamicIndex);
                store.Settings["autoCacheNext"] = true;
                await readerWeb!.ExecuteScriptAsync("""
                    requestAnimationFrame(() => {
                        const viewport = document.getElementById('viewport');
                        viewport.scrollTop = viewport.scrollHeight;
                        reader.report();
                    });
                    void 0;
                    """);
                await WaitForAsync(async () => await readerWeb.ExecuteScriptAsync("""
                    [...document.querySelectorAll('.chapter h1')].map(h => h.textContent).join('|').includes('第68章 动态章节测试|第69章 动态章节测试')
                    """) == "true");
                Check(true, "scrolling through a dynamic chapter appends the next chapter in the same reader");
                store.Settings["autoCacheNext"] = false;
                await ReadChapterAsync(dynamicIndex + 1);
                await CheckDynamicChapterAsync(69);
                await StepReaderAsync(1);
                var error = (StackPanel)readerError!;
                Check(error.Visibility == Microsoft.UI.Xaml.Visibility.Visible && readingToken == "" &&
                    error.Children.OfType<TextBlock>().Any(text => text.Text == "网页需要登录、订阅或验证") &&
                    error.Children.OfType<ScrollViewer>().Any(scroll => scroll.Content is StackPanel actions &&
                        actions.Children.OfType<Button>().Any(button => button.Content is "打开原网页")),
                    "protected dynamic chapter keeps the website access prompt and offers the original page");
                await CloseReaderAsync();
                store.Books.Remove(webBook);
                store.Settings["pageMode"] = previousMode;
                store.Settings["autoCacheNext"] = previousAutoAppend;
                Navigate("search");
            }
            finally { cancelServer.Cancel(); server.Stop(); await serverTask; }

            var zip = Path.Combine(store.Root, "backup-test.zip");
            await Backup.ExportAsync(store, zip, true);
            var preview = await Backup.ReadAsync(zip);
            var restored = new AppStore(Path.Combine(store.Root, "recovered"));
            await Backup.ApplyAsync(restored, preview, true);
            Check(restored.Books.Count == store.Books.Count && File.Exists(restored.DataPath(localBook.S("localFile"))) &&
                  restored.State["bookmarks"]!.AsArray().Count == 1 && restored.State["stats"] is null, "full ZIP round trip keeps books, files and bookmarks");
            var old = Backup.ParseJson("""{"app":"moread","data":{"stats":{"yesterday":{"sec":12}},"history":[{"ci":1}],"books":[],"progress":{"book":{"ci":2}}}}""");
            Check(old.State["stats"] is null && !old.State.ContainsKey("history") && old.State["progress"]?["book"].I("ci") == 2,
                "legacy reading history and stats are discarded while progress is preserved");
            var legacyZip = Path.Combine(store.Root, "legacy.zip");
            using (var oldZip = System.IO.Compression.ZipFile.Open(legacyZip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var legacy = AppStore.Defaults();
                var entry = localBook.Copy();
                entry["localFile"] = entry.S("localFile").Replace('/', '\\');
                legacy["books"]!.AsArray().Add(entry);
                legacy["stats"] = new JsonObject { ["data"] = 1 };
                var mapping = new JsonObject { [entry.S("id")] = new JsonObject { ["path"] = "files/book.txt", ["localFile"] = entry.S("localFile") } };
                using (var writer = new StreamWriter(oldZip.CreateEntry("moread.json").Open()))
                    writer.Write(new JsonObject { ["app"] = "moread", ["schema"] = 2, ["data"] = legacy, ["fileMap"] = mapping }.ToJsonString());
                using var input = File.OpenRead(readFixture);
                using var output = oldZip.CreateEntry("files/book.txt").Open();
                input.CopyTo(output);
            }
            var oldStore = new AppStore(Path.Combine(store.Root, "old-backup-recovered"));
            await Backup.ApplyAsync(oldStore, await Backup.ReadAsync(legacyZip), false);
            Check(oldStore.Books.Count == 1 && File.Exists(oldStore.BookPath(oldStore.Books[0].S("localFile"))) && oldStore.State["stats"] is null, "old Electron fileMap restores local books without statistics");

            if (Environment.GetEnvironmentVariable("LEGADO_UI3_MOCK_DAV") is { Length: > 0 } davUrl)
            {
                using var dav = new WebDav(new JsonObject { ["url"] = davUrl, ["user"] = "tester@example.com", ["pass"] = "app-pass-123", ["dir"] = "smoke" });
                await dav.TestAsync();
                await dav.UploadAsync("smoke.zip", zip);
                var entries = await dav.ListAsync();
                var downloaded = Path.Combine(store.Root, "download-test.zip");
                await dav.DownloadAsync("smoke.zip", downloaded);
                await dav.UploadSourcesAsync(store.Sources.Objects());
                var downloadedSources = await dav.DownloadSourcesAsync();
                await dav.DeleteAsync("smoke.zip");
                Check(entries.Any(e => e.Name == "smoke.zip") && (await Backup.ReadAsync(downloaded)).State["books"]!.AsArray().Count == store.Books.Count &&
                    SourceImport.Preview(downloadedSources, []).Count == 1, "WebDAV mock supports upload, list, restore and source sync");
            }

            var large = Enumerable.Range(0, 3200).Select(i => new JsonObject
            {
                ["bookSourceName"] = "书源 " + i, ["bookSourceUrl"] = $"https://example.org/{i}", ["searchUrl"] = "/search?q={{key}}",
                ["quality"] = i == 0 ? "dead" : "", ["enabled"] = i != 1
            });
            var batch = SourceImport.Preview(J.Array(large).ToJsonString(), store.Sources.Objects());
            SourceImport.Apply(batch, store.Sources, false);
            var numeric = SearchSources();
            store.Settings["searchLimit"] = 0;
            store.Settings["switchLimit"] = "0";
            var unlimited = SearchSources();
            var eligible = store.Sources.Objects().Count(s => s.B("enabled", true) && s.S("searchUrl") != "");
            Check(numeric.Length == 40 && unlimited.Length == eligible && eligible > 3000 &&
                  unlimited.Any(s => s.S("quality") == "dead") && unlimited.All(s => s.B("enabled", true)), "all-source search removes the cap but respects disabled sources");
            Check(SearchSources(new JsonObject()).Length == eligible, "all-source source switching also removes the cap");
            SelectPage("settings");
            await WaitForAsync(() => Task.FromResult(VisualChildren<ComboBox>((Microsoft.UI.Xaml.DependencyObject)page.Content)
                .Count(c => c.SelectedItem is string name && name == "全部") == 2));
            var settingsScroll = VisualChildren<ScrollViewer>((Microsoft.UI.Xaml.DependencyObject)page.Content).First();
            Check(settingsScroll.ExtentHeight > settingsScroll.ViewportHeight &&
                VisualChildren<Microsoft.UI.Xaml.Controls.TextBlock>((Microsoft.UI.Xaml.DependencyObject)page.Content).Any(t => t.Text == "云端同步"),
                "settings are expanded together in one scrollable page");
            SelectPage("sources");
            ToggleSourceSelection();
            sourceList!.SelectAll();
            Check(sourceList.SelectedItems.Count == 3201, "virtualized native list supports 3,201 selected rows");
            var originalSize = AppWindow.Size;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(680, 630));
            store.Settings["appTheme"] = "dark";
            ApplyAppTheme();
            await Task.Delay(350);
            Check(root.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark && navigation.DisplayMode != NavigationViewDisplayMode.Expanded &&
                sourceList.ActualWidth > 280, "native layout adapts to a narrow window and dark theme");
            Check(navigation.OpenPaneLength == 168, "expanded navigation has the narrower width");
            Check(!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported() ||
                new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ||
                AppWindow.TitleBar.ButtonForegroundColor == Microsoft.UI.Colors.White, "dark theme keeps caption buttons readable");
            store.Settings["appTheme"] = "system";
            ApplyAppTheme();
            AppWindow.Resize(originalSize);
            await store.SaveAsync();
            await File.WriteAllLinesAsync(Path.Combine(store.Root, "smoke-result.txt"), report);
            if (Environment.GetEnvironmentVariable("LEGADO_UI3_KEEP_TEST_UI") == "1")
            {
                ToggleSourceSelection();
                SelectPage("shelf");
                return;
            }
            Close();
        }
        catch (Exception e)
        {
            report.Add("FAIL " + e);
            await File.WriteAllLinesAsync(Path.Combine(store.Root, "smoke-result.txt"), report);
            Close();
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> check)
    {
        for (int attempt = 0; attempt < 50; attempt++) { if (await check()) return; await Task.Delay(100); }
        throw new InvalidOperationException("Reader state did not change");
    }

    private static IEnumerable<T> VisualChildren<T>(Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualChildren<T>(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private async Task TestExploreAsync(JsonObject source, Action<bool, string> check)
    {
        source["exploreUrl"] = "点击榜::/rank?page={{page}}&&单页::/rank?page=1";
        source["ruleExplore"] = new JsonObject { ["bookList"] = ".rank", ["name"] = "a@text", ["bookUrl"] = "a@href", ["coverUrl"] = "img@src" };
        var switched = source.Copy();
        switched["id"] = "browse-alt";
        switched["name"] = "Z 另一个源";
        switched["group"] = "模拟榜单分组";
        switched["exploreUrl"] = """[{"title":"人气排行","url":"/rank?other=1"}]""";
        var slowServer = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        slowServer.Start();
        var slow = source.Copy();
        slow["id"] = "browse-slow";
        slow["name"] = "Z 慢速源";
        slow["exploreUrl"] = "<js>java.ajax('http://127.0.0.1:" + ((IPEndPoint)slowServer.LocalEndpoint).Port + "/categories')</js>";
        store.Sources.Add(slow);
        store.Sources.Add(switched);
        async Task<(Flyout Picker, ListView List)> OpenPickerAsync()
        {
            var button = VisualChildren<DropDownButton>((Microsoft.UI.Xaml.DependencyObject)page.Content).First();
            var picker = (Flyout)button.Flyout;
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOpened(object? _, object args) => opened.TrySetResult();
            picker.Opened += OnOpened;
            try
            {
                picker.ShowAt(button);
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { picker.Opened -= OnOpened; }
            return (picker, VisualChildren<ListView>(picker.Content).Single());
        }

        async Task ChooseAsync(string name)
        {
            var (picker, list) = await OpenPickerAsync();
            var choice = list.Items.OfType<ExploreSourceOption>().Single(item => item.Name == name);
            list.ScrollIntoView(choice);
            await WaitForAsync(() => Task.FromResult(list.ContainerFromItem(choice) is ListViewItem));
            var container = (ListViewItem)list.ContainerFromItem(choice);
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(container);
            var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnClosed(object? _, object args) => closed.TrySetResult();
            picker.Closed += OnClosed;
            try
            {
                invoke.Invoke();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { picker.Closed -= OnClosed; }
        }
        try
        {
            var categories = (await engine.RunAsync("exploreCategories", new() { ["source"] = source.DeepClone() })).Objects().ToArray();
            check(categories.Length == 2 && categories[0].S("title") == "点击榜" && categories[0].B("paged"), "Legado discovery categories and pagination recognized");
            var page2 = (await engine.RunAsync("exploreBooks", new() { ["source"] = source.DeepClone(), ["url"] = categories[0].S("url"), ["page"] = 2 })).Objects().ToArray();
            check(page2.Select(b => b.S("name")).SequenceEqual(new[] { "第二页首位", "第二页次位" }) && page2[0].S("coverUrl").EndsWith("/cover.jpg"),
                "ranking retains source order, resolves its cover, and requests page 2");
            var script = source.Copy();
            script["exploreUrl"] = """<js>const rows=[{title:'动态榜单',url:'/rank?page={{page}}'}]; JSON.stringify(rows)</js>""";
            check((await engine.RunAsync("exploreCategories", new() { ["source"] = script })).Objects().Single().S("title") == "动态榜单",
                "dynamic discovery categories use the existing rule engine");
            lastExploreSource = source.S("id");
            SelectPage("explore");
            await WaitForAsync(() => Task.FromResult(VisualChildren<ListView>((Microsoft.UI.Xaml.DependencyObject)page.Content)
                .Any(l => l.Items.OfType<ExploreBookRow>().Any(r => r.Name == "点击首位"))));
            await ChooseAsync("Z 另一个源");
            await WaitForAsync(() => Task.FromResult(VisualChildren<ListView>((Microsoft.UI.Xaml.DependencyObject)page.Content)
                .Any(l => l.Items.OfType<ExploreBookRow>().Any(r => r.Name == "另一榜单"))));
            check(true, "native browse page can switch to another source and display its ranking");
            await ChooseAsync("Z 慢速源");
            using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var stalled = await slowServer.AcceptTcpClientAsync(waiting.Token);
            await ChooseAsync("Z 另一个源");
            await WaitForAsync(() => Task.FromResult(VisualChildren<ListView>((Microsoft.UI.Xaml.DependencyObject)page.Content)
                .Any(l => l.Items.OfType<ExploreBookRow>().Any(r => r.Name == "另一榜单"))));
            check(true, "switching sources cancels the previous synchronous network request");

            var extraSources = Enumerable.Range(1, 1642).Select(i => new JsonObject
            {
                ["id"] = $"browse-fixture-{i}", ["name"] = $"书源 {i:D4}", ["exploreUrl"] = "/rank?page=1"
            }).ToArray();
            foreach (var extra in extraSources) store.Sources.Add(extra);
            try
            {
                SelectPage("explore");
                await WaitForAsync(() => Task.FromResult(VisualChildren<ListView>((Microsoft.UI.Xaml.DependencyObject)page.Content)
                    .Any(l => l.Items.OfType<ExploreBookRow>().Any(r => r.Name == "另一榜单"))));
                var (picker, list) = await OpenPickerAsync();
                var count = list.Items.Count;
                await WaitForAsync(() => Task.FromResult(list.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 }));
                var panel = (ItemsStackPanel)list.ItemsPanelRoot;
                var scroll = VisualChildren<ScrollViewer>(list).First();
                foreach (var position in new[] { .25, .5, 1, 0 })
                {
                    scroll.ChangeView(null, scroll.ScrollableHeight * position, null, disableAnimation: true);
                    await WaitForAsync(() => Task.FromResult(panel.FirstVisibleIndex >= 0 &&
                        Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight * position) < 2 &&
                        (position != 0 || panel.FirstVisibleIndex == 0) && (position != 1 || panel.LastVisibleIndex == count - 1)));
                    check(VisualChildren<ListViewItem>(list).Count() < 100, $"browse picker recycles rows at {position:P0} of {count} sources");
                }
                var filter = VisualChildren<TextBox>(picker.Content).Single();
                filter.Text = "模拟榜单分组";
                await WaitForAsync(() => Task.FromResult(list.Items.Count == 1));
                check(count >= 1645 && list.SelectedItem is ExploreSourceOption { Name: "Z 另一个源" },
                    $"browse picker filters {count} sources by group and retains the active source");
                picker.Hide();
            }
            finally { foreach (var extra in extraSources) store.Sources.Remove(extra); }
        }
        finally { slowServer.Stop(); SelectPage("shelf"); store.Sources.Remove(slow); store.Sources.Remove(switched); }
    }

    private async Task TestUpdateDownloadAsync(Action<bool, string> check)
    {
        var package = "portable archive"u8.ToArray();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package));
        var newer = new Version(Updates.CurrentVersion.Major, Updates.CurrentVersion.Minor, Updates.CurrentVersion.Build + 1);
        var name = $"Legado.UI3-v{newer}-win-x64.zip";
        var metadata = JsonNode.Parse($$"""
            {"tag_name":"v{{newer}}","assets":[{"name":"{{name}}","size":{{package.Length}},
            "digest":"sha256:{{digest}}","browser_download_url":"{{Updates.ReleasePage}}/download/v{{newer}}/{{name}}"}]}
            """);
        var release = Updates.Parse(metadata);
        check(release.Version == newer && release.Version > Updates.CurrentVersion && release.Size == package.Length,
            "update metadata accepts the official Windows x64 ZIP and compares versions");
        metadata!["assets"]![0]!["browser_download_url"] = "https://example.test/update.zip";
        var rejected = false;
        try { Updates.Parse(metadata); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "update metadata rejects an asset outside the official repository");
        var hosts = new List<string>();
        using var client = new HttpClient(new UpdateTestHandler(uri =>
        {
            hosts.Add(uri.Host);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(uri.Host == "github.com" ? package : new byte[package.Length]) };
        }));
        var path = Path.Combine(store.Root, release.FileName);
        await Updates.DownloadAsync(release, path, new Progress<string>(), CancellationToken.None, client);
        check(hosts.SequenceEqual(new[] { "gh-proxy.org", "ghfast.top", "github.com" }) &&
            (await File.ReadAllBytesAsync(path)).SequenceEqual(package) && !Directory.EnumerateFiles(store.Root, "*.part").Any(),
            "update download skips corrupted mirrors, verifies the GitHub fallback and removes temporary files");

        var fixtureRoot = Path.Combine(store.Root, "update-fixture");
        Directory.CreateDirectory(fixtureRoot);
        var marker = Path.Combine(fixtureRoot, "updates", "active.txt");
        async Task<string> StageFixtureAsync(bool traversal)
        {
            using var contents = new MemoryStream();
            using (var zip = new ZipArchive(contents, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntryFromFile(Environment.ProcessPath!, "Legado.UI3.exe");
                foreach (var file in new[] { "App.xbf", "Legado.UI3.pri", "Legado.UI3.dll", "Assets/Legado.UI3.ico" })
                    zip.CreateEntry(file);
                if (traversal) zip.CreateEntry("../../escaped.txt");
            }
            var bytes = contents.ToArray();
            using var fixtureClient = new HttpClient(new UpdateTestHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            var fixture = release with { Version = Updates.CurrentVersion, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            return await UpdateInstall.StageAsync(fixtureRoot, fixture, new Progress<string>(), CancellationToken.None, fixtureClient);
        }
        var staged = await StageFixtureAsync(traversal: false);
        check(File.Exists(staged) && !File.Exists(marker) && !Directory.EnumerateFiles(Path.GetDirectoryName(marker)!, "*.zip").Any(),
            "update staging verifies and extracts the package without activating it");
        UpdateInstall.Activate(fixtureRoot, staged);
        var active = await File.ReadAllTextAsync(marker);
        rejected = false;
        try { await StageFixtureAsync(traversal: true); } catch (IOException) { rejected = true; }
        check(rejected && await File.ReadAllTextAsync(marker) == active && !File.Exists(Path.Combine(fixtureRoot, "escaped.txt")),
            "a traversal package is rejected and leaves the last active version intact");
    }

    private sealed class UpdateTestHandler(Func<Uri, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request.RequestUri!));
    }

    private async Task TestNetworkVoicesAsync(string baseUrl, Action<bool, string> check)
    {
        const string spoken = "朗读 & 试听😀";
        var edge = HttpVoice.EdgePresets(baseUrl + "/api/rap/");
        using var yunxi = edge[0].BuildRequest(spoken, 1, 1);
        using var xiaoxiao = edge[1].BuildRequest(spoken, 1.5, 1);
        var edgeText = "text=" + Uri.EscapeDataString(spoken);
        check(HttpVoice.EdgePresets(baseUrl).Select(voice => voice.Id).SequenceEqual(edge.Select(voice => voice.Id)) &&
            edge.All(voice => voice.HasRate && !voice.HasPitch) && yunxi.RequestUri?.AbsolutePath == "/api/rap" &&
            yunxi.Method == HttpMethod.Post && yunxi.Content!.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded" &&
            await yunxi.Content.ReadAsStringAsync() == edgeText + "&rate=25&voice=zh-CN-YunxiNeural" &&
            await xiaoxiao.Content!.ReadAsStringAsync() == edgeText + "&rate=75&voice=zh-CN-XiaoxiaoNeural",
            "Edge TTS presets normalize the service address and send the expected voice, rate and form text");
        var (direct, rejected) = HttpVoice.Parse(baseUrl + "/tts.wav?text={{speakText}}");
        check(direct.Count == 1 && rejected.Count == 0, "direct network voice URL imports");
        var legacy = new JsonObject
        {
            ["name"] = "Legado 表单",
            ["url"] = baseUrl + "/tts.wav,{\"method\":\"POST\",\"body\":\"tex={{java.encodeURI(java.encodeURI(speakText))}}&spd={{String((speakSpeed+5)/10+4)}}\"}",
            ["contentType"] = "audio/wav"
        };
        var invalid = new JsonObject { ["name"] = "不支持的脚本", ["url"] = "@js:loadSomething(speakText)" };
        var (batch, skipped) = HttpVoice.Parse(J.Array([legacy, invalid]).ToJsonString());
        using var post = batch.Single().BuildRequest(spoken, 1, 1);
        var expected = "tex=" + Uri.EscapeDataString(Uri.EscapeDataString(spoken)) + "&spd=6.5";
        check(await post.Content!.ReadAsStringAsync() == expected && skipped.Count == 1 && post.Method == HttpMethod.Post,
            "Legado POST templates encode text and reject JavaScript");
        var native = new JsonObject
        {
            ["name"] = "JSON 接口", ["url"] = baseUrl + "/tts.wav", ["method"] = "POST",
            ["body"] = new JsonObject { ["input"] = "{{speakText}}", ["speed"] = "{{rate}}" }
        };
        using var json = HttpVoice.Parse(native.ToJsonString()).Voices.Single().BuildRequest("引号\"\n换行", 1.25, 1);
        var rendered = JsonNode.Parse(await json.Content!.ReadAsStringAsync());
        check(rendered.S("input") == "引号\"\n换行" && rendered.D("speed") == 1.25, "JSON voice safely escapes text and preserves numeric rates");
        await voiceLibrary.SaveAsync(direct);
        var preferences = store.Settings["tts"]!.AsObject();
        preferences["voiceURI"] = direct[0].Id;
        try
        {
            var selected = SelectedHttpVoice()!;
            var audio = await selected.SynthesizeAsync(spoken, 1, 1, CancellationToken.None);
            check(audio.Mime == "audio/wav" && audio.Bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8), "selected voice receives audio over HTTP");
            using var player = new Windows.Media.Playback.MediaPlayer { Volume = 0 };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await SpeakAsync(null, selected, player, spoken, timeout.Token);
            check(true, "custom network audio plays through the native reader");
            bool redirected = false;
            try { await (selected with { Url = baseUrl + "/tts-redirect?text={{speakText}}" }).SynthesizeAsync(spoken, 1, 1, timeout.Token); }
            catch (IOException e) when (e.Message.Contains("307")) { redirected = true; }
            check(redirected, "network voice does not follow redirects with reading text");
        }
        finally { preferences["voiceURI"] = ""; }
        await TestVoicePickerAsync(direct.Single(), check);
        var retired = AppStore.Prepare(JsonNode.Parse("""{"version":3,"settings":{"theme":"paper"}}""")!.AsObject());
        var manual = AppStore.Prepare(JsonNode.Parse("""{"version":4,"settings":{"theme":"paper"}}""")!.AsObject());
        check(retired["settings"].S("theme") == "auto" && manual["settings"].S("theme") == "paper",
            "upgrading default paper switches to automatic while new manual choices persist");
    }

    private async Task TestVoicePickerAsync(HttpVoice sample, Action<bool, string> check)
    {
        var voices = Enumerable.Range(1, 84).Select(i => sample with { Name = $"测试音色 {i:D3}" }).ToArray();
        await voiceLibrary.SaveAsync(voices);
        var preferences = store.Settings["tts"]!.AsObject();
        SelectPage("settings");
        DropDownButton? button = null;
        await WaitForAsync(() => Task.FromResult((button = VisualChildren<DropDownButton>((Microsoft.UI.Xaml.DependencyObject)page.Content)
            .FirstOrDefault(item => item.IsLoaded &&
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(item).StartsWith("选择音色", StringComparison.Ordinal))) is not null));
        var picker = (Flyout)button!.Flyout;
        var remove = VisualChildren<Button>((Microsoft.UI.Xaml.DependencyObject)page.Content)
            .Single(item => item.Content is "批量删除音色");
        check(remove.Visibility == Microsoft.UI.Xaml.Visibility.Visible, "batch voice deletion is available while using the system default");
        static void Invoke(Control control)
        {
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(control);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        async Task<ContentDialog> ConfirmDeleteAsync()
        {
            Invoke(remove);
            ContentDialog? dialog = null;
            await WaitForAsync(() => Task.FromResult((dialog = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .Where(popup => popup.Child is not null).SelectMany(popup => VisualChildren<ContentDialog>(popup.Child))
                .FirstOrDefault(item => item.Title is "批量删除音色" && VisualChildren<Button>(item).Any(child => child.Content is "删除"))) is not null));
            return dialog!;
        }
        void SelectVoices(ContentDialog dialog, params HttpVoice[] selected)
        {
            var list = (ListView)dialog.Content;
            foreach (var voice in selected)
                list.SelectedItems.Add(list.Items.Cast<HttpVoice>().Single(item => item.Id == voice.Id));
        }
        try
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            picker.Opened += (_, _) => opened.TrySetResult();
            picker.ShowAt(button);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var list = VisualChildren<ListView>(picker.Content).Single();
            await WaitForAsync(() => Task.FromResult(list.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 }));
            var panel = (ItemsStackPanel)list.ItemsPanelRoot;
            var scroll = VisualChildren<ScrollViewer>(list).First();
            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            await WaitForAsync(() => Task.FromResult(panel.LastVisibleIndex == list.Items.Count - 1));
            check(list.Items.Count >= 85 && VisualChildren<ListViewItem>(list).Count() < list.Items.Count && preferences.S("voiceURI") == "",
                "voice picker scrolls to the last of 84 imported voices without selecting or instantiating the full list");
            var filter = VisualChildren<TextBox>(picker.Content).Single();
            filter.Text = "测试音色 084";
            await WaitForAsync(() => Task.FromResult(list.Items.Count == 1 && list.ContainerFromIndex(0) is ListViewItem));
            check(list.Items[0] is SpeechVoiceOption choice && choice.Id == voices[^1].Id && preferences.S("voiceURI") == "",
                "voice filtering preserves the saved selection until the user chooses a row");
            Invoke((ListViewItem)list.ContainerFromIndex(0));
            await WaitForAsync(() => Task.FromResult(!picker.IsOpen && preferences.S("voiceURI") == voices[^1].Id &&
                remove.Visibility == Microsoft.UI.Xaml.Visibility.Visible));
            check(((Microsoft.UI.Xaml.Controls.TextBlock)button.Content).Text.Contains("测试音色 084"), "chosen network voice is saved and shown on the button");
            var dialog = await ConfirmDeleteAsync();
            check(dialog.Content is ListView { SelectionMode: ListViewSelectionMode.Multiple } batch &&
                batch.Items.Count == voices.Length && batch.Items.OfType<HttpVoice>().Count() == voices.Length && !dialog.IsPrimaryButtonEnabled,
                "batch deletion lists imported voices only and requires a selection");
            SelectVoices(dialog, voices[0], voices[^1]);
            dialog.Hide();
            await WaitForAsync(() => Task.FromResult(remove.IsEnabled));
            check(voiceLibrary.Load().Count == voices.Length && preferences.S("voiceURI") == voices[^1].Id,
                "canceling voice deletion preserves the current voice and library");
            dialog = await ConfirmDeleteAsync();
            SelectVoices(dialog, voices[0], voices[1]);
            await WaitForAsync(() => Task.FromResult(VisualChildren<Button>(dialog).Any(child => child.Content is "删除 2 个音色" && child.IsEnabled)));
            Invoke(VisualChildren<Button>(dialog).Single(child => child.Content is "删除 2 个音色"));
            await WaitForAsync(() => Task.FromResult(remove.IsEnabled && voiceLibrary.Load().Count == voices.Length - 2));
            check(voiceLibrary.Load().Select(voice => voice.Id).SequenceEqual(voices[2..].Select(voice => voice.Id)) &&
                preferences.S("voiceURI") == voices[^1].Id, "batch deletion removes only selected voices and retains the active voice when unchecked");
            dialog = await ConfirmDeleteAsync();
            SelectVoices(dialog, voices[2], voices[^1]);
            await WaitForAsync(() => Task.FromResult(VisualChildren<Button>(dialog).Any(child => child.Content is "删除 2 个音色" && child.IsEnabled)));
            Invoke(VisualChildren<Button>(dialog).Single(child => child.Content is "删除 2 个音色"));
            await WaitForAsync(() => Task.FromResult(remove.IsEnabled && voiceLibrary.Load().Count == voices.Length - 4));
            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(store.Root, "data.json")));
            check(voiceLibrary.Load().Select(voice => voice.Id).SequenceEqual(voices[3..^1].Select(voice => voice.Id)) &&
                preferences.S("voiceURI") == "" && persisted?["settings"]?["tts"].S("voiceURI") == "" &&
                ((Microsoft.UI.Xaml.Controls.TextBlock)button.Content).Text == "系统默认",
                "batch deleting the active voice persists the removals and restores the system default");
        }
        finally
        {
            picker.Hide();
            SelectPage("shelf");
            preferences["voiceURI"] = "";
            await voiceLibrary.SaveAsync([sample]);
        }
    }

    private static async Task ServeSmokePagesAsync(System.Net.Sockets.TcpListener server, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await server.AcceptTcpClientAsync(token);
                await using var wire = client.GetStream();
                using var request = new StreamReader(wire, Encoding.ASCII, leaveOpen: true);
                var line = await request.ReadLineAsync(token) ?? "";
                while (await request.ReadLineAsync(token) is { Length: > 0 }) { }
                var path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                if (path.StartsWith("/tts-redirect?"))
                {
                    await wire.WriteAsync("HTTP/1.1 307 Temporary Redirect\r\nLocation: /tts.wav\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                    continue;
                }
                if (path == "/tts.wav?text=" + Uri.EscapeDataString("朗读 & 试听😀") ||
                    path.StartsWith("/dynamic-tts.wav?text=" + Uri.EscapeDataString("网页正常加载的章节正文")))
                {
                    using var wave = new MemoryStream();
                    using (var writer = new BinaryWriter(wave, Encoding.ASCII, leaveOpen: true))
                    {
                        const int frames = 3200;
                        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
                        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                        writer.Write("data"u8); writer.Write(frames * 2); writer.Write(new byte[frames * 2]);
                    }
                    await wire.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length}\r\nConnection: close\r\n\r\n"), token);
                    await wire.WriteAsync(wave.ToArray(), token);
                    continue;
                }
                var chapterNumber = path.StartsWith("/web-chapter/") && int.TryParse(path[13..], out var number) ? number : 0;
                var page = chapterNumber > 0 ? $$"""
                    <h1>第{{chapterNumber}}章 动态章节测试</h1>
                    <nav hidden><a href="/web-chapter/{{chapterNumber - 1}}" rel="prev">上一章</a><a href="/web-chapter/{{chapterNumber + 1}}" rel="next">下一章</a></nav>
                    <article id="reading"><div role="status">正文加载中...</div></article>
                    <div hidden><h3>此章为VIP章节，需要订阅后才能继续阅读</h3></div>
                    <script>
                    const reading = document.querySelector('#reading');
                    if ({{chapterNumber}} < 70 && document.cookie.includes('web_fixture=1')) {
                        reading.innerHTML = '<p style="opacity:0">不可见测试正文</p>' +
                            '<p>网页正常加载的章节正文。这是公开样例，用于验证书架打开网页后的正文显示。</p>'.repeat(16);
                    } else {
                        reading.hidden = true;
                        reading.nextElementSibling.hidden = false;
                    }
                    </script>
                    """ :
                    path.StartsWith("/rank?") ? "<div class='rank'><a href='/book'>" + (path.Contains("other=1") ? "另一榜单" : path.Contains("page=2") ? "第二页首位" : "点击首位") +
                    "</a><img src='/cover.jpg'></div><div class='rank'><a href='/book-two'>" + (path.Contains("page=2") ? "第二页次位" : "点击次位") + "</a></div>" :
                    path.StartsWith("/search") ? "<div class='result'><a href='/book'>纸上旅程</a></div>" :
                    path.StartsWith("/chapter") ? "<article><p>" + string.Concat(Enumerable.Repeat("样例章节正文按原来的顺序显示。", 80)) + "</p></article>" :
                    "<h1>纸上旅程</h1><div id='list'><a href='/chapter/1'>第一章 山路</a><a href='/chapter/2'>第二章 灯火</a>" +
                    "<a href='/chapter/3'>第三章 清晨</a><a href='/chapter/4'>第四章 垂柳</a><a href='/chapter/5'>第五章 归途</a></div>";
                var host = ((IPEndPoint)server.LocalEndpoint).Port;
                var body = path == "/script-url" ? $"http://127.0.0.1:{host}/search?q=%E7%BA%B8" : "<html><meta charset='utf-8'><body>" + page + "</body></html>";
                var bytes = Encoding.UTF8.GetBytes(body);
                try
                {
                    await wire.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), token);
                    await wire.WriteAsync(bytes, token);
                }
                catch (IOException e) when (e.InnerException is System.Net.Sockets.SocketException socket &&
                    socket.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionAborted or System.Net.Sockets.SocketError.ConnectionReset)
                {
                    // Navigating away can cancel pending page assets.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (System.Net.Sockets.SocketException) when (token.IsCancellationRequested) { }
    }
}
