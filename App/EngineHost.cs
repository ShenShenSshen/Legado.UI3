using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Legado.UI3.Core;

namespace Legado.UI3;

sealed class EngineHost(string dataRoot, bool parser = false) : IDisposable
{
    public WebView2 View { get; } = new() { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
    private readonly Network network = new();
    private readonly Dictionary<string, (TaskCompletionSource<JsonNode?> Completion, CancellationToken Token)> jobs = [];
    private readonly TaskCompletionSource ready = new();
    private readonly string origin = parser ? "https://parser.moread.local/" : "https://engine.moread.local/";
    private string Entry => origin + (parser ? "parser.html" : "engine.html");

    public async Task InitializeAsync()
    {
        try { await OpenAsync(); }
        catch (Exception e) { ready.TrySetException(e); throw; }
    }

    private async Task OpenAsync()
    {
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(dataRoot, parser ? "parser-webview" : "engine-webview"), new CoreWebView2EnvironmentOptions());
        await View.EnsureCoreWebView2Async(env);
        var core = View.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("LEGADO_UI3_DEVTOOLS") == "1";
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) => { if (e.Uri != Entry) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        if (parser)
            core.SetVirtualHostNameToFolderMapping(new Uri(origin).Host, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Deny);
        else
        {
            // Virtual mappings bypass request interception, which the rule network bridge needs.
            core.AddWebResourceRequestedFilter(origin + "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
            core.WebResourceRequested += ServeEngineRequest;
        }
        core.WebMessageReceived += (_, e) =>
        {
            if (e.Source != Entry) return;
            try
            {
                var node = JsonNode.Parse(e.WebMessageAsJson)!;
                if (node.B("ready")) { ready.TrySetResult(); return; }
                if (jobs.Remove(node.S("id"), out var job))
                {
                    if (node.S("error") is { Length: > 0 } message) job.Completion.TrySetException(new IOException(message));
                    else job.Completion.TrySetResult(node["value"]?.DeepClone());
                }
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        };
        core.ProcessFailed += (_, _) =>
        {
            foreach (var job in jobs.Values) job.Completion.TrySetException(new IOException("书源引擎已停止，请重新打开应用"));
            jobs.Clear();
        };
        core.Navigate(Entry);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async void ServeEngineRequest(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        var uri = new Uri(args.Request.Uri);
        var path = uri.AbsolutePath;
        byte[] bytes;
        string contentType;
        int status = 200;
        try
        {
            if (path == "/__net" && args.Request.Method == "POST" && args.Request.Content is not null)
            {
                if (!jobs.TryGetValue(uri.Query.TrimStart('?'), out var job)) throw new OperationCanceledException("已取消");
                job.Token.ThrowIfCancellationRequested();
                using var requestBody = args.Request.Content.AsStreamForRead();
                var input = await Network.ReadLimitedAsync(requestBody, 2 * 1024 * 1024, job.Token);
                var options = JsonNode.Parse(input) as JsonObject ?? throw new InvalidDataException("无效的请求参数");
                var result = await network.FetchAsync(options, job.Token);
                bytes = Encoding.UTF8.GetBytes(result.ToJsonString());
                contentType = "application/json";
            }
            else if (args.Request.Method == "GET" && path is "/engine.html" or "/engine.js" or "/detect.js" or "/engine-host.js")
            {
                bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", path[1..]));
                contentType = path == "/engine.html" ? "text/html" : "text/javascript";
            }
            else { bytes = []; contentType = "text/plain"; status = 404; }
        }
        catch (Exception e)
        {
            bytes = Encoding.UTF8.GetBytes(new JsonObject { ["ok"] = false, ["error"] = e.Message }.ToJsonString());
            contentType = "application/json";
        }
        args.Response = sender.Environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false).AsRandomAccessStream(), status, status == 200 ? "OK" : "Not Found",
            $"Content-Type: {contentType}; charset=utf-8\r\nCache-Control: no-store");
    }

    public async Task<JsonNode?> RunAsync(string operation, JsonObject payload, CancellationToken cancellation = default)
    {
        await ready.Task.WaitAsync(cancellation);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lifetime.CancelAfter(TimeSpan.FromSeconds(90));
        var token = lifetime.Token;
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs[id] = (completion, token);
        try
        {
            var job = new JsonObject { ["id"] = id, ["op"] = operation, ["payload"] = payload.DeepClone() };
            if (parser) await View.ExecuteScriptAsync("window.runEngine(" + job.ToJsonString() + ");void 0;");
            else View.CoreWebView2.PostWebMessageAsJson(job.ToJsonString());
            return await completion.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && token.IsCancellationRequested)
        {
            throw new TimeoutException("书源规则运行超时");
        }
        finally
        {
            jobs.Remove(id);
            lifetime.Cancel();
        }
    }

    public void Dispose() { network.Dispose(); View.Close(); }
}
