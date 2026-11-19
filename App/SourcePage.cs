using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private ListView? sourceList;
    private TextBlock? sourceCount;
    private TextBlock? selectedCount;
    private Button? deleteSelected;
    private Grid? sourceHeader;
    private StackPanel? selectionBar;
    private const string AllQuality = "全部质量";
    private string sourceQuery = "", sourceGroup = "全部分组", sourceStatus = "全部状态", sourceQuality = AllQuality;
    private bool sourceSelecting;
    private CancellationTokenSource? detectCancellation;

    private UIElement SourcesPage()
    {
        sourceSelecting = false;
        var importMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var (label, action) in new (string, Func<Task>)[] { ("网络链接", ImportNetworkAsync), ("本地文件", ImportSourceFileAsync), ("粘贴 JSON", ImportPasteAsync) })
        {
            var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => _ = RunAsync(action); importMenu.Items.Add(item);
        }
        var import = new Button { Content = "导入 ▾", Flyout = importMenu, Style = (Style)Application.Current.Resources["LegadoPrimaryButtonStyle"] };
        AutomationProperties.SetName(import, "导入书源");
        var more = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var (label, action) in new (string, Func<Task>)[] { ("新建书源", () => EditSourceAsync(null)), ("导出全部", ExportSourcesAsync), ("上传书源到 WebDAV", UploadSourcesAsync), ("从 WebDAV 导入书源", DownloadSourcesAsync) })
        { var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => _ = RunAsync(action); more.Items.Add(item); }
        more.Items.Add(new MenuFlyoutSeparator());
        AddMenuAction(more, "按检测结果分组", () => ReclassifySourcesAsync(restore: false));
        AddMenuAction(more, "恢复检测前分组", () => ReclassifySourcesAsync(restore: true));
        AddMenuAction(more, "停用失效源", async () =>
        {
            var unavailable = store.Sources.Objects().Where(s => s.S("quality") == "dead" && s.B("enabled", true)).ToArray();
            foreach (var source in unavailable) source["enabled"] = false;
            if (unavailable.Length > 0) await store.SaveAsync();
            RefreshSources();
            ShowNotice($"已停用 {unavailable.Length} 个失效源");
        });
        var actions = Ui.AdaptiveCommands(import, Button("批量选择", ToggleSourceSelection), ActionButton("检测", DetectSourcesAsync), new DropDownButton { Content = "更多", Flyout = more });
        var query = new TextBox { PlaceholderText = "搜索名称或网址", Text = sourceQuery, MinWidth = 135 };
        AutomationProperties.SetName(query, "搜索书源名称、网址或分组");
        var groups = new[] { "全部分组" }.Concat(store.Sources.Objects().Select(s => s.S("group", "未分组")).Distinct().Order()).ToArray();
        var group = Ui.Combo(groups, Math.Max(0, Array.IndexOf(groups, sourceGroup)), 120);
        sourceGroup = (string)group.SelectedItem;
        string[] sourceStates = ["全部状态", "已启用", "已停用", "上次失败"];
        string[] qualityLevels = [AllQuality, "优质", "可用", "较差", "失效", "未检测"];
        var status = Ui.Combo(sourceStates, Array.IndexOf(sourceStates, sourceStatus));
        var quality = Ui.Combo(qualityLevels, Array.IndexOf(qualityLevels, sourceQuality), 126);
        var filter = Ui.Columns("*,Auto,Auto,Auto", query, group, status, quality);
        filter.RowDefinitions.Add(new() { Height = GridLength.Auto });
        filter.RowDefinitions.Add(new() { Height = GridLength.Auto });
        filter.SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < 532;
            Grid.SetColumnSpan(query, stacked ? 4 : 1);
            foreach (var element in new[] { group, status, quality }) Grid.SetRow(element, stacked ? 1 : 0);
            filter.RowSpacing = stacked ? 8 : 0;
        };
        sourceCount = Ui.Text("", 13, true);
        query.TextChanged += (_, _) => { sourceQuery = query.Text.Trim(); RefreshSources(); };
        group.SelectionChanged += (_, _) => { sourceGroup = (string)group.SelectedItem; RefreshSources(); };
        status.SelectionChanged += (_, _) => { sourceStatus = (string)status.SelectedItem; RefreshSources(); };
        quality.SelectionChanged += (_, _) => { sourceQuality = (string)quality.SelectedItem; RefreshSources(); };
        selectedCount = Ui.Text("已选 0 项");
        var selectionActions = new MenuFlyout();
        AddMenuAction(selectionActions, "启用所选", () => { SetSelectedSourcesEnabled(true); return Task.CompletedTask; });
        AddMenuAction(selectionActions, "停用所选", () => { SetSelectedSourcesEnabled(false); return Task.CompletedTask; });
        deleteSelected = ActionButton("删除所选", DeleteSelectedSourcesAsync);
        deleteSelected.IsEnabled = false;
        selectionBar = Ui.Row(selectedCount, Button("全选筛选结果", () => sourceList!.SelectAll()), deleteSelected, new DropDownButton { Content = "更多", Flyout = selectionActions }, Button("取消", ToggleSourceSelection));
        selectionBar.Visibility = Visibility.Collapsed;
        sourceList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = Ui.Template("""
            <Grid Padding="8,7" ColumnSpacing="16" BorderThickness="0,0,0,1" BorderBrush="{ThemeResource DividerStrokeColorDefaultBrush}">
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="90"/><ColumnDefinition Width="64"/><ColumnDefinition Width="70"/></Grid.ColumnDefinitions>
              <StackPanel Spacing="4"><TextBlock Text="{Binding Name}" FontSize="15" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding Url}" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis"/></StackPanel>
              <TextBlock Grid.Column="1" Text="{Binding Group}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
              <TextBlock Grid.Column="2" Text="{Binding Quality}" VerticalAlignment="Center"/>
              <ToggleSwitch Grid.Column="3" IsOn="{Binding Enabled,Mode=TwoWay}" OnContent="" OffContent="" MinWidth="60" VerticalAlignment="Center" AutomationProperties.Name="{Binding Name}"/>
            </Grid>
            """)
        };
        sourceList.ItemClick += (_, e) => { if (!sourceSelecting && e.ClickedItem is SourceRow row) _ = RunAsync(() => EditSourceAsync(row.Data)); };
        sourceList.SelectionChanged += (_, _) => UpdateSourceSelectionCount();
        sourceHeader = Ui.Columns("*,90,64,70", Ui.Text("名称 / 网址", 13, true), Ui.Text("分组", 13, true), Ui.Text("检测结果", 13, true), Ui.Text("启用", 13, true));
        sourceHeader.ColumnSpacing = 16;
        sourceHeader.Margin = new Thickness(20, 0, 22, 3);
        var empty = Ui.Text("没有符合筛选条件的书源", 14, true);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;
        sourceEmpty = empty;
        var body = new Grid();
        body.Children.Add(sourceList);
        body.Children.Add(empty);
        RefreshSources();
        var labels = Ui.Stack(Ui.AdaptiveCommands(selectionBar), sourceCount, sourceHeader);
        labels.Spacing = 8;
        return PageLayout(PageHeader("书源", actions), filter, labels, body);
    }

    private List<JsonObject> FilteredSources() => store.Sources.Objects().Where(s =>
        (sourceQuery == "" || (s.S("name") + " " + s.S("url") + " " + s.S("group")).Contains(sourceQuery, StringComparison.OrdinalIgnoreCase)) &&
        (sourceGroup == "全部分组" || s.S("group", "未分组") == sourceGroup) &&
        (sourceStatus switch { "已启用" => s.B("enabled", true), "已停用" => !s.B("enabled", true), "上次失败" => s.S("lastError") != "" || s.S("quality") == "dead", _ => true }) &&
        (sourceQuality == AllQuality || Ui.Quality(s) == sourceQuality)).ToList();

    private void RefreshSources()
    {
        if (sourceList is null) return;
        if (sourceList.SelectionMode == ListViewSelectionMode.Multiple) sourceList.SelectedItems.Clear();
        var filtered = FilteredSources();
        sourceList.ItemsSource = filtered.Select(s => new SourceRow(s, SaveSoon)).ToList();
        if (sourceCount is not null) sourceCount.Text = $"{filtered.Count} / {store.Sources.Count} 个书源";
        if (sourceEmpty is not null)
        {
            sourceEmpty.Text = store.Sources.Count == 0 ? "还没有书源。可从网络链接、文件或 JSON 导入。" : "没有符合筛选条件的书源";
            sourceEmpty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (sourceHeader is not null) sourceHeader.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceSelectionCount();
    }
    private TextBlock? sourceEmpty;
    private void UpdateSourceSelectionCount()
    {
        var count = sourceList?.SelectionMode == ListViewSelectionMode.Multiple ? sourceList.SelectedItems.Count : 0;
        if (selectedCount is not null) selectedCount.Text = $"已选 {count} 项";
        if (deleteSelected is not null) deleteSelected.IsEnabled = count > 0;
        if (sourceHeader is not null) sourceHeader.Margin = new Thickness(sourceSelecting ? 54 : 20, 0, 22, 3);
    }
    private void ToggleSourceSelection()
    {
        if (sourceSelecting) sourceList!.SelectedItems.Clear();
        sourceSelecting = !sourceSelecting;
        sourceList!.SelectionMode = sourceSelecting ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        sourceList.IsItemClickEnabled = !sourceSelecting;
        selectionBar!.Visibility = sourceSelecting ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceSelectionCount();
    }
    private async Task DeleteSelectedSourcesAsync()
    {
        var ids = sourceList!.SelectedItems.OfType<SourceRow>().Select(r => r.Id).ToHashSet();
        if (ids.Count == 0) { ShowNotice("先选择要删除的书源"); return; }
        try { await DeleteSourcesAsync(ids); }
        finally { DispatcherQueue.TryEnqueue(UpdateSourceSelectionCount); }
    }
    private async Task DeleteSourcesAsync(HashSet<string> ids)
    {
        var affected = store.Books.Objects().Count(b => ids.Contains(b.S("sourceId")));
        var message = $"删除选中的 {ids.Count} 个书源？" + (affected > 0 ? $"\n书架中有 {affected} 本书正在使用它们。" : "") + "\n书籍、缓存、进度和书签会保留，需要联网时可以换源。";
        if (!await ConfirmAsync("删除书源", message)) return;
        var removed = await store.DeleteSourcesAsync(ids);
        RefreshSources(); ShowNotice($"已删除 {removed} 个书源");
    }
    private void SetSelectedSourcesEnabled(bool enabled)
    {
        foreach (var row in sourceList!.SelectedItems.OfType<SourceRow>().ToArray()) row.Data["enabled"] = enabled;
        SaveSoon(); RefreshSources();
    }

    private async Task ImportNetworkAsync()
    {
        var input = new TextBox { PlaceholderText = "GitHub 文件页、Raw 或 HTTPS JSON 链接", Width = DialogWidth(480), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 72 };
        if (await DialogAsync("网络导入", input, "获取书源") != ContentDialogResult.Primary) return;
        using var cancellation = new CancellationTokenSource();
        notice.ActionButton = Button("取消获取", cancellation.Cancel); ShowNotice("正在获取书源…");
        string text;
        try { using var network = new Network(); text = await network.DownloadSourceAsync(input.Text, cancellation.Token); }
        catch (OperationCanceledException) { ShowNotice("已取消获取"); return; }
        finally { notice.ActionButton = null; }
        await ImportPreviewAsync(text);
    }
    private async Task ImportSourceFileAsync()
    {
        var files = await PickFilesAsync(".json", ".txt", ".bak", "*");
        foreach (var file in files)
        {
            if (new FileInfo(file.Path).Length > Network.Limit) throw new InvalidDataException("书源文件过大");
            await ImportPreviewAsync(Network.Decode(await File.ReadAllBytesAsync(file.Path)));
        }
    }
    private async Task ImportPasteAsync()
    {
        var input = new TextBox { PlaceholderText = "粘贴 Legado 书源 JSON", Width = DialogWidth(520), Height = 260, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        if (await DialogAsync("粘贴导入", input, "预览") == ContentDialogResult.Primary) await ImportPreviewAsync(input.Text);
    }
    private async Task ImportPreviewAsync(string text)
    {
        var rows = SourceImport.Preview(text, store.Sources.Objects());
        var update = new CheckBox { Content = "更新已勾选的重复书源", IsChecked = false };
        var select = Button("选择全部有效项", () => { foreach (var row in rows) row.Selected = row.Valid; });
        var list = new ListView { ItemsSource = rows, SelectionMode = ListViewSelectionMode.None, Height = 320, Width = DialogWidth(540), ItemTemplate = Ui.Template("""
        <Grid Padding="0,8" ColumnSpacing="12"><Grid.ColumnDefinitions><ColumnDefinition Width="32"/><ColumnDefinition Width="*"/><ColumnDefinition Width="110"/></Grid.ColumnDefinitions>
        <CheckBox IsChecked="{Binding Selected,Mode=TwoWay}" IsEnabled="{Binding Valid}" AutomationProperties.Name="{Binding Name}"/>
        <StackPanel Grid.Column="1" Spacing="4"><TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding Url}" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding Group}" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis"/></StackPanel>
        <TextBlock Grid.Column="2" Text="{Binding Status}" TextWrapping="Wrap" VerticalAlignment="Center"/></Grid>
        """) };
        var content = Ui.Stack(Ui.Text($"新增 {rows.Count(r => r.Status == "新增")}，重复 {rows.Count(r => r.Status == "重复")}，无效 {rows.Count(r => !r.Valid)}", 13, true), Ui.AdaptiveCommands(select, update), list);
        if (await DialogAsync("预览书源", content, "导入所选") != ContentDialogResult.Primary) return;
        var result = await store.ApplySourcesAsync(rows, update.IsChecked == true);
        SelectPage("sources");
        ShowNotice($"导入完成：新增 {result.Added}，更新 {result.Updated}");
    }
    private async Task ExportSourcesAsync()
    {
        var path = await SaveFileAsync("bookSource", ".json");
        if (path is not null) { await File.WriteAllTextAsync(path, J.Array(store.Sources.Objects().Select(SourceImport.ToLegado)).ToJsonString(J.Pretty)); ShowNotice("书源已导出"); }
    }
    private async Task EditSourceAsync(JsonObject? source)
    {
        var original = source;
        source ??= new JsonObject { ["name"] = "", ["url"] = "https://", ["group"] = "未分组", ["enabled"] = true, ["searchUrl"] = "", ["ruleSearch"] = new JsonObject(), ["ruleBookInfo"] = new JsonObject(), ["ruleToc"] = new JsonObject(), ["ruleContent"] = new JsonObject() };
        var name = new TextBox { Header = "名称", Text = source.S("name") };
        var url = new TextBox { Header = "网址", Text = source.S("url") };
        var group = new TextBox { Header = "分组", Text = source.S("group", "未分组") };
        var showExplore = new ToggleSwitch { Header = "在浏览页显示", IsOn = source.B("enabledExplore", true) };
        var json = new TextBox { Header = "书源 JSON", Text = source.ToJsonString(J.Pretty), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 220, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        var details = new Pivot { Width = DialogWidth(540), Height = 380 };
        details.Items.Add(new PivotItem { Header = "基本", Content = Ui.Stack(name, url, group, showExplore) });
        details.Items.Add(new PivotItem { Header = "规则", Content = Ui.Scroll(json) });
        details.Items.Add(new PivotItem { Header = "检测", Content = Ui.Text(source.S("qNote", source.S("lastError", "尚未检测"))) });
        var result = await DialogAsync(original is null ? "新建书源" : source.S("name"), details, "保存", original is null ? null : "删除");
        if (result == ContentDialogResult.Secondary && original is not null) { await DeleteSourcesAsync([original.S("id")]); return; }
        if (result != ContentDialogResult.Primary) return;
        var edited = JsonNode.Parse(json.Text) as JsonObject ?? throw new InvalidDataException("书源 JSON 必须是对象");
        // Basic fields win only when edited; otherwise preserve changes made in the JSON tab.
        if (name.Text != source.S("name")) { edited["name"] = name.Text; edited.Remove("bookSourceName"); }
        if (url.Text != source.S("url")) { edited["url"] = url.Text; edited.Remove("bookSourceUrl"); }
        if (group.Text != source.S("group", "未分组")) { edited["group"] = group.Text; edited.Remove("bookSourceGroup"); }
        if (showExplore.IsOn != source.B("enabledExplore", true)) edited["enabledExplore"] = showExplore.IsOn;
        edited = SourceImport.Normalize(edited);
        if (original is not null) { edited["id"] = original.S("id"); store.Sources[store.Sources.IndexOf(original)] = edited; }
        else { edited["id"] = J.Id("S"); store.Sources.Add(edited); }
        await store.SaveAsync(); RefreshSources();
    }

    private async Task DetectSourcesAsync()
    {
        if (detectCancellation is not null) { detectCancellation.Cancel(); return; }
        await initialization;
        var targets = sourceSelecting ? sourceList!.SelectedItems.OfType<SourceRow>().Select(s => s.Data).ToList() : FilteredSources();
        if (targets.Count == 0) { ShowNotice("没有可检测的书源"); return; }
        var key = new TextBox { Header = "搜索关键词", Text = "我的", Width = 400 };
        var deep = new CheckBox { Content = "检查目录与正文", IsChecked = true };
        if (await DialogAsync($"检测 {targets.Count} 个书源", Ui.Stack(key, deep), "开始检测") != ContentDialogResult.Primary) return;
        using var cancellation = new CancellationTokenSource(); detectCancellation = cancellation;
        notice.ActionButton = Button("停止检测", cancellation.Cancel);
        int done = 0, cursor = 0;
        try
        {
            async Task Worker()
            {
                while (cursor < targets.Count && !cancellation.IsCancellationRequested)
                {
                    var source = targets[cursor++];
                    try
                    {
                        await DetectOneAsync(source, key.Text, deep.IsChecked == true, cancellation.Token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { if (store.Sources.Contains(source)) source["lastError"] = e.Message; }
                    done++; ShowNotice($"检测 {done} / {targets.Count}：{source.S("name")}"); await store.SaveAsync();
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(3, targets.Count)).Select(_ => Worker()));
            ShowNotice(cancellation.IsCancellationRequested ? $"已停止，完成 {done} 个" : $"检测完成：{done} 个书源");
        }
        finally { detectCancellation = null; notice.ActionButton = null; if (currentPage == "sources") RefreshSources(); }
    }
    private async Task DetectOneAsync(JsonObject source, string keyword, bool deep, CancellationToken token = default)
    {
        await initialization;
        var result = await engine.RunAsync("detect", new() { ["source"] = source.DeepClone(), ["key"] = keyword, ["deep"] = deep }, token);
        var current = store.Sources.Objects().FirstOrDefault(s => s.S("id") == source.S("id"));
        if (current is null || result is not JsonObject detection) return;
        current["quality"] = detection.S("quality");
        current["qNote"] = detection.S("note");
        current["qAt"] = J.Now;
        current["qScore"] = detection.D("score");
        current["lastError"] = detection.S("quality") == "dead" ? detection.S("note") : "";
    }
    private async Task ReclassifySourcesAsync(bool restore)
    {
        int count = 0;
        foreach (var source in store.Sources.Objects())
        {
            if (restore)
            {
                if (source["originalGroup"] is null) continue;
                source["group"] = source.S("originalGroup", "未分组");
                source.Remove("originalGroup");
            }
            else
            {
                var quality = Ui.Quality(source);
                if (quality == "未检测") continue;
                if (source["originalGroup"] is null) source["originalGroup"] = source.S("group", "未分组");
                source["group"] = quality + "源";
            }
            if (source["bookSourceGroup"] is not null) source["bookSourceGroup"] = source.S("group");
            count++;
        }
        if (count > 0) await store.SaveAsync();
        Navigate("sources");
        ShowNotice(restore ? $"已恢复 {count} 个书源的分组" : $"已重新分组 {count} 个书源");
    }
    private WebDav Dav() => new(store.Settings["webdav"]!.AsObject());
    private async Task UploadSourcesAsync() { using var dav = Dav(); await dav.UploadSourcesAsync(store.Sources.Objects()); ShowNotice("书源已上传"); }
    private async Task DownloadSourcesAsync() { using var dav = Dav(); await ImportPreviewAsync(await dav.DownloadSourcesAsync()); }
}
