using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Legado.UI3.Core;
using Windows.System;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private FrameworkElement AddonsPage()
    {
        var files = ActionButton("导入扩展", async () =>
        {
            foreach (var file in await PickFilesAsync(".json", ".html"))
            {
                if (new FileInfo(file.Path).Length > 512 * 1024) throw new InvalidDataException("扩展文件超过 512 KB");
                await InstallAddonAsync(await File.ReadAllTextAsync(file.Path), file.Name);
            }
        }, true);
        var paste = ActionButton("粘贴扩展", async () =>
        {
            var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 440, Height = 220, PlaceholderText = "扩展 JSON 或 HTML" };
            if (await DialogAsync("粘贴扩展", input, "导入") == ContentDialogResult.Primary) await InstallAddonAsync(input.Text, "");
        });
        var items = store.State["addons"].Objects().ToArray();
        var elements = new StackPanel { Spacing = 8 };
        if (items.Length == 0) elements.Children.Add(Ui.Text("没有已安装的扩展", 15, true));
        foreach (var addon in items)
        {
            var remove = ActionButton("移除", async () =>
            {
                if (!await ConfirmAsync("移除扩展", $"移除「{addon.S("name")}」？", "移除")) return;
                store.State["addons"]!.AsArray().Remove(addon);
                await store.SaveAsync();
                Navigate("addons");
            });
            elements.Children.Add(Ui.Columns("*,Auto,Auto", Ui.Text(addon.S("name"), 16), ActionButton("打开", () => OpenAddonAsync(addon)), remove));
        }
        return PageLayout(PageHeader("扩展", Ui.Row(paste, files)), Ui.Scroll(elements));
    }

    private async Task InstallAddonAsync(string text, string file)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 400 * 1024) throw new InvalidDataException("扩展内容为空或过大");
        var addon = text.StartsWith('{') ? JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("扩展 JSON 必须为对象")
            : text.StartsWith('<') ? new JsonObject
            {
                ["name"] = System.Net.WebUtility.HtmlDecode(Regex.Match(text, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase).Groups[1].Value.Trim()) is { Length: > 0 } title
                    ? title : file.Length > 0 ? Path.GetFileNameWithoutExtension(file) : "HTML 扩展",
                ["html"] = text
            }
            : throw new InvalidDataException("请选择扩展 JSON 或 HTML");
        var name = addon.S("name").Trim();
        if (name.Length == 0) throw new InvalidDataException("扩展缺少名称");
        addon["name"] = name.Length > 60 ? name[..60] : name;
        if (addon.S("html").Length == 0) { _ = Network.WebUri(addon.S("url")); addon["type"] = "link"; }
        else addon["type"] = "html";
        var installed = store.State["addons"]!.AsArray();
        var match = installed.Objects().FirstOrDefault(a => a.S("name") == addon.S("name") || (a.S("id") != "" && a.S("id") == addon.S("id")));
        if (match is not null)
        {
            if (!await ConfirmAsync("更新扩展", $"替换「{name}」的已安装版本？", "更新")) return;
            addon["id"] = match.S("id");
            installed[installed.IndexOf(match)] = addon;
        }
        else
        {
            if (addon.S("id").Length == 0) addon["id"] = J.Id("X");
            installed.Add(addon);
        }
        await store.SaveAsync();
        Navigate("addons");
        ShowNotice("扩展已导入");
    }

    private async Task OpenAddonAsync(JsonObject addon)
    {
        if (addon.S("type") == "link") { await Launcher.LaunchUriAsync(Network.WebUri(addon.S("url"))); return; }
        const string url = "https://addon.moread.local/addon.html";
        var view = new WebView2();
        page.Content = PageLayout(PageHeader(addon.S("name"), Button("返回扩展", () => Navigate("addons"))), view);
        bool closed = false;
        view.Unloaded += (_, _) => { closed = true; view.Close(); };
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(store.Root, "addon-webview"), new());
        if (closed) return;
        await view.EnsureCoreWebView2Async(env);
        if (closed) return;
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("addon.moread.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) => { if (e.Uri != url) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += (sender, e) =>
        {
            if (e.Source != url || closed) return;
            _ = RunAsync(async () =>
            {
                var message = JsonNode.Parse(e.WebMessageAsJson);
                switch (message.S("moread"))
                {
                    case "ready":
                        core.PostWebMessageAsJson(new JsonObject { ["html"] = addon.S("html") }.ToJsonString());
                        break;
                    case "context":
                        var context = new JsonObject
                        {
                            ["book"] = null, ["chapterTitle"] = "", ["theme"] = store.Settings.S("theme"),
                            ["fontSize"] = store.Settings.I("fontSize", 22), ["count"] = store.Books.Count
                        };
                        core.PostWebMessageAsJson(new JsonObject { ["moread"] = "context", ["data"] = context }.ToJsonString());
                        break;
                    case "shelf": SelectPage("shelf"); break;
                    case "toast":
                        var noticeText = message.S("text");
                        ShowNotice(noticeText.Length > 200 ? noticeText[..200] : noticeText);
                        break;
                    case "search":
                        searchKeyword = message.S("q").Trim();
                        startSearchOnOpen = searchKeyword.Length > 0;
                        SelectPage("search");
                        break;
                    case "openBook":
                        var book = store.Books.Objects().FirstOrDefault(b => b.S("id") == message.S("id"));
                        if (book is not null) await OpenReaderAsync(book, message.I("chapter"));
                        break;
                }
            });
        };
        core.Navigate(url);
    }
}
