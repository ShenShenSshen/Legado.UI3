using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private string lastExploreSource = "";
    private static bool CanExplore(JsonObject source) => source.B("enabled", true) &&
        source.B("enabledExplore", true) && (source["exploreUrl"] is JsonArray { Count: > 0 } || !string.IsNullOrWhiteSpace(source.S("exploreUrl")));

    private FrameworkElement ExplorePage()
    {
        var sources = store.Sources.Objects().Where(CanExplore).Select(source => new ExploreSourceOption(source))
            .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var manage = Button("管理书源", () => SelectPage("sources"));
        var header = PageHeader("浏览", manage);
        if (sources.Length == 0)
            return PageLayout(header, Ui.Text("暂无可浏览的书源。请导入或启用带发现规则的书源。", 15, true));

        var selectedSource = sources.FirstOrDefault(source => source.Data.S("id") == lastExploreSource) ?? sources[0];
        var sourceName = Ui.Text(selectedSource.Name);
        sourceName.TextWrapping = TextWrapping.NoWrap;
        sourceName.TextTrimming = TextTrimming.CharacterEllipsis;
        var sourceButton = new DropDownButton
        {
            Content = sourceName, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(sourceButton, "选择书源，当前为 " + selectedSource.Name);
        var filter = new TextBox { PlaceholderText = "筛选书源名称或分组" };
        AutomationProperties.SetName(filter, "筛选浏览书源");
        var sourceList = new ListView
        {
            IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single, ItemsSource = sources,
            ItemTemplate = Ui.Template("""<TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>""")
        };
        AutomationProperties.SetName(sourceList, "可浏览书源");
        var emptySources = Ui.Text("没有匹配的书源", 13, true);
        emptySources.HorizontalAlignment = HorizontalAlignment.Center;
        emptySources.Visibility = Visibility.Collapsed;
        var sourceRows = new Grid();
        sourceRows.Children.Add(sourceList);
        sourceRows.Children.Add(emptySources);
        // A bounded viewport lets ListView recycle rows while the popup scrolls.
        var pickerBody = Ui.Rows("Auto,*", filter, sourceRows);
        pickerBody.RowSpacing = 8;
        var sourcePicker = new Flyout { Content = pickerBody, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        sourceButton.Flyout = sourcePicker;
        filter.TextChanged += (_, _) =>
        {
            var query = filter.Text.Trim();
            var matches = query.Length == 0 ? sources : sources.Where(source =>
                source.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                source.Group.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            sourceList.ItemsSource = matches;
            sourceList.SelectedItem = matches.Contains(selectedSource) ? selectedSource : null;
            emptySources.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        sourcePicker.Opening += (_, _) =>
        {
            pickerBody.Width = Math.Max(200, Math.Min(DialogWidth(420), sourceButton.ActualWidth + 24));
            pickerBody.Height = Math.Clamp(root.ActualHeight - 240, 156, 380);
            filter.Text = "";
            sourceList.SelectedItem = selectedSource;
        };
        sourcePicker.Opened += (_, _) =>
        {
            sourceList.ScrollIntoView(selectedSource);
            filter.Focus(FocusState.Programmatic);
        };
        var categoryBox = Ui.Combo([], -1); categoryBox.Header = "分类或榜单";
        var status = Ui.Text("", 13, true);
        var results = new ListView
        {
            IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = Ui.Template("""
                <Grid Padding="8,10" ColumnSpacing="12">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="32"/><ColumnDefinition Width="42"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <TextBlock Text="{Binding Rank}" VerticalAlignment="Center" Opacity="0.65"/>
                  <Border Grid.Column="1" Background="{ThemeResource SubtleFillColorSecondaryBrush}" CornerRadius="4">
                    <Image Source="{Binding Cover}" Height="56" Stretch="UniformToFill"/>
                  </Border>
                  <StackPanel Grid.Column="2" Spacing="5" VerticalAlignment="Center">
                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding Detail}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis"/>
                  </StackPanel>
                </Grid>
                """)
        };
        results.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is ExploreBookRow row) _ = RunAsync(() => PreviewResultAsync(row.Book));
        };
        JsonObject[] categories = [];
        CancellationTokenSource? request = null;
        int pageNumber = 1;
        int resultOffset = 0;
        int previousCount = 0;
        bool settingChoices = false;
        var previous = new Button { Content = "上一页" };
        var next = new Button { Content = "下一页" };
        var pageLabel = Ui.Text("第 1 页", 13, true);
        var paging = Ui.Row(previous, pageLabel, next);
        previous.Click += (_, _) => _ = LoadBooksAsync(pageNumber - 1);
        next.Click += (_, _) => _ = LoadBooksAsync(pageNumber + 1);
        var retry = Button("刷新", () =>
        {
            if (categoryBox.SelectedIndex < 0) _ = LoadCategoriesAsync();
            else _ = LoadBooksAsync(pageNumber);
        });
        previous.IsEnabled = next.IsEnabled = false;
        paging.Visibility = Visibility.Collapsed;

        async Task LoadCategoriesAsync()
        {
            request?.Cancel();
            settingChoices = true;
            categoryBox.Items.Clear();
            categoryBox.IsEnabled = false;
            settingChoices = false;
            results.ItemsSource = null;
            paging.Visibility = Visibility.Collapsed;
            var source = selectedSource.Data;
            lastExploreSource = source.S("id");
            using var loading = new CancellationTokenSource();
            request = loading;
            status.Text = "正在读取分类…";
            try
            {
                await initialization;
                loading.Token.ThrowIfCancellationRequested();
                var reply = await engine.RunAsync("exploreCategories", new() { ["source"] = source.DeepClone() }, loading.Token);
                if (loading.IsCancellationRequested) return;
                categories = reply.Objects().ToArray();
                if (categories.Length == 0) { status.Text = "此书源没有可用的分类入口"; return; }
                settingChoices = true;
                foreach (var item in categories) categoryBox.Items.Add(item.S("title"));
                categoryBox.SelectedIndex = 0;
                categoryBox.IsEnabled = true;
                settingChoices = false;
            }
            catch (Exception e) { if (!loading.IsCancellationRequested) status.Text = e.Message; return; }
            finally { if (request == loading) request = null; }
            await LoadBooksAsync(1);
        }

        async Task LoadBooksAsync(int target)
        {
            if (categoryBox.SelectedIndex < 0 || target < 1) return;
            request?.Cancel();
            using var loading = new CancellationTokenSource();
            request = loading;
            var category = categories[categoryBox.SelectedIndex];
            var source = selectedSource.Data;
            previous.IsEnabled = next.IsEnabled = false;
            status.Text = "正在加载书单…";
            results.ItemsSource = null;
            try
            {
                var reply = await engine.RunAsync("exploreBooks", new()
                {
                    ["source"] = source.DeepClone(), ["url"] = category.S("url"), ["page"] = target
                }, loading.Token);
                if (loading.IsCancellationRequested) return;
                var books = reply.Objects().ToArray();
                if (target == 1) resultOffset = 0;
                else if (target > pageNumber) resultOffset += previousCount;
                else if (target < pageNumber) resultOffset = Math.Max(0, resultOffset - books.Length);
                pageNumber = target;
                previousCount = books.Length;
                results.ItemsSource = books.Select((hit, i) => new ExploreBookRow(new SearchResult(hit), resultOffset + i + 1)).ToArray();
                status.Text = books.Length > 0 ? $"{books.Length} 本书" : "此页没有书籍";
                paging.Visibility = category.B("paged") ? Visibility.Visible : Visibility.Collapsed;
                pageLabel.Text = $"第 {target} 页";
                previous.IsEnabled = target > 1;
                next.IsEnabled = books.Length > 0;
            }
            catch (Exception e)
            {
                if (loading.IsCancellationRequested) return;
                status.Text = e.Message;
                previous.IsEnabled = pageNumber > 1;
                next.IsEnabled = paging.Visibility == Visibility.Visible;
            }
            finally { if (request == loading) request = null; }
        }

        sourceList.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not ExploreSourceOption choice) return;
            sourcePicker.Hide();
            if (ReferenceEquals(selectedSource, choice)) return;
            selectedSource = choice;
            sourceName.Text = choice.Name;
            AutomationProperties.SetName(sourceButton, "选择书源，当前为 " + choice.Name);
            _ = LoadCategoriesAsync();
        };
        categoryBox.SelectionChanged += (_, _) => { if (!settingChoices && categoryBox.SelectedIndex >= 0) _ = LoadBooksAsync(1); };
        var sourceField = Ui.Stack(Ui.Text("书源"), sourceButton);
        sourceField.Spacing = 6;
        var filters = Ui.Columns("*,*,Auto", sourceField, categoryBox, retry);
        var layout = PageLayout(header, filters, Ui.Columns("*,Auto", status, paging), results);
        layout.Loaded += (_, _) => _ = LoadCategoriesAsync();
        layout.Unloaded += (_, _) => request?.Cancel();
        return layout;
    }
}

public sealed record ExploreSourceOption(JsonObject Data)
{
    public string Name => Data.S("name");
    public string Group => Data.S("group");
}

public sealed record ExploreBookRow(SearchResult Book, int Rank)
{
    public string Name => Book.Name;
    public string? Cover => Book.Cover;
    public string Detail => string.Join("  ·  ", new[] { Book.Hits[0].S("author"), Book.Hits[0].S("kind"), Book.Hits[0].S("lastChapter") }.Where(s => s.Length > 0));
}
