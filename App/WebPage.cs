using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Legado.UI3.Core;
using Windows.System;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private const int VisiblePageLimit = 3 * 1024 * 1024;
    private WebView2? webPageView;
    private FrameworkElement? webPageLayout;
    private Grid? webPageSlot, webReadingHost;
    private Button? webPageReturn;
    private Task? webPageInitialization;
    private bool webPageFromShelf;

    private async Task<WebView2> OpenWebPageAsync(Uri target, bool fromShelf = false)
    {
        webPageFromShelf = fromShelf;
        var view = await PrepareWebPageAsync(forReading: false);
        if (ReferenceEquals(page.Content, webPageLayout) && !closing) view.CoreWebView2.Navigate(target.AbsoluteUri);
        return view;
    }

    private async Task<WebView2> PrepareWebPageAsync(bool forReading)
    {
        var initialization = webPageInitialization ??= InitializeWebPageAsync(forReading);
        PlaceWebPage(forReading);
        try { await initialization; }
        catch
        {
            if (ReferenceEquals(webPageInitialization, initialization))
            {
                webPageInitialization = null;
                (webPageView?.Parent as Panel)?.Children.Remove(webPageView!);
                webPageView?.Close();
            }
            throw;
        }
        return webPageView!;
    }

    private void PlaceWebPage(bool forReading)
    {
        if (forReading && webReadingHost is null)
        {
            webReadingHost = new Grid { Opacity = 0, IsHitTestVisible = false };
            Grid.SetRow(webReadingHost, 2);
            root.Children.Add(webReadingHost);
        }
        var view = webPageView!;
        var slot = forReading ? webReadingHost! : webPageSlot!;
        if (!slot.Children.Contains(view))
        {
            (forReading ? webPageSlot : webReadingHost)?.Children.Remove(view);
            slot.Children.Add(view);
        }
        view.Opacity = forReading ? 0 : 1;
        view.IsTabStop = !forReading;
        if (forReading) return;
        webPageReturn!.Content = webPageFromShelf ? "返回书架" : "返回搜索";
        page.Content = webPageLayout;
    }

    private async Task InitializeWebPageAsync(bool forReading)
    {
        // Keep the controller alive between visits so session-only site cookies survive navigation to the shelf.
        var view = webPageView = new WebView2();
        AutomationProperties.SetName(view, "网页浏览");
        var address = new TextBox { PlaceholderText = "网页地址" };
        AutomationProperties.SetName(address, "网页地址");
        var back = Button("后退", () => view.GoBack());
        var forward = Button("前进", () => view.GoForward());
        var refresh = Button("刷新", () => view.Reload());
        back.IsEnabled = forward.IsEnabled = refresh.IsEnabled = false;
        var browse = Button("打开", () =>
        {
            var input = address.Text.Trim();
            if (input.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) input = "https://" + input;
            if (view.CoreWebView2 is { } initialized) initialized.Navigate(Network.WebUri(input).AbsoluteUri);
        });
        var inspect = ActionButton("识别书籍", () => InspectVisiblePageAsync(view), true);
        var toolbar = Ui.Columns("Auto,*,Auto", Ui.Row(back, forward, refresh), address, Ui.Row(browse, inspect));
        webPageReturn = Button("返回搜索", () =>
        {
            if (webPageFromShelf) SelectPage("shelf");
            else Navigate("search");
        });
        webPageSlot = new Grid();
        webPageLayout = PageLayout(PageHeader("网页", webPageReturn), toolbar, webPageSlot);
        PlaceWebPage(forReading);
        address.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            _ = RunAsync(() => { view.CoreWebView2?.Navigate(Network.WebUri(address.Text.Trim()).AbsoluteUri); return Task.CompletedTask; });
        };
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(store.Root, "browse-webview"), new());
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) e.Cancel = true;
        };
        core.NewWindowRequested += (sender, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                sender.Navigate(uri.AbsoluteUri);
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationCompleted += (_, e) =>
        {
            address.Text = core.Source;
            back.IsEnabled = core.CanGoBack;
            forward.IsEnabled = core.CanGoForward;
            inspect.IsEnabled = e.IsSuccess;
            if (!e.IsSuccess && ReferenceEquals(page.Content, webPageLayout)) ShowNotice("网页未能打开：" + e.WebErrorStatus, true);
        };
        refresh.IsEnabled = true;
    }

    private async Task InspectVisiblePageAsync(WebView2 view)
    {
        var url = Network.WebUri(view.Source?.AbsoluteUri ?? "");
        var result = await view.ExecuteScriptAsync($"(() => {{ const html=document.documentElement.outerHTML; return html.length <= {VisiblePageLimit} ? html : null; }})()");
        var html = JsonSerializer.Deserialize<string>(result) ?? throw new InvalidDataException("页面过大，无法识别");
        var data = await engine.RunAsync("inspectWeb", new() { ["html"] = html, ["url"] = url.AbsoluteUri }) as JsonObject ?? throw new IOException("页面无法识别");
        var options = new List<(JsonObject Hit, JsonObject? Visible)>();
        if (!data.B("serp") && (data["toc"] is not null || data["content"] is not null))
        {
            var title = data["info"].S("name", data.S("title", url.Host));
            options.Add((new JsonObject { ["name"] = title, ["kind"] = "web", ["bookUrl"] = url.AbsoluteUri, ["sourceName"] = url.Host }, data));
        }
        foreach (var link in data["links"].Objects().Take(24))
        {
            if (!Uri.TryCreate(link.S("url"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri == url) continue;
            var title = link.S("title");
            if (title.Length < 2) continue;
            options.Add((new JsonObject { ["name"] = title, ["kind"] = "web", ["bookUrl"] = uri.AbsoluteUri, ["sourceName"] = uri.Host }, null));
        }
        if (options.Count == 0) { ShowNotice("当前网页没有识别到可阅读内容"); return; }
        int index = 0;
        if (options.Count > 1)
        {
            var picker = Ui.Combo(options.Select(o => o.Hit.S("name")), 0, DialogWidth(466));
            if (await DialogAsync("选择书籍页面", picker, "预览目录") != ContentDialogResult.Primary) return;
            index = Math.Max(0, picker.SelectedIndex);
        }
        var selected = options[index];
        var book = await BuildOnlineBookAsync(selected.Hit, selected.Visible);
        await ShowBookPreviewAsync(book);
    }
}
