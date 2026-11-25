using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private static readonly string[] SortLabels = ["最近阅读", "最近加入", "书名", "作者", "阅读进度", "最近更新"];
    private static readonly string[] SortKeys = ["recent", "created", "name", "author", "progress", "updated"];
    private static readonly string[] SizeKeys = ["sm", "md", "lg"];

    private string shelfGroup = "", shelfKind = "", shelfSelected = "", shelfQuery = "";
    private Func<JsonObject, Action>? captureShelfPosition;
    private Action? returnToShelf;

    private FrameworkElement ShelfPage()
    {
        var books = store.Books.Objects().Select(book => new BookRow(book, store.Progress)).ToArray();
        var add = ActionButton("导入本地书", ImportBooksAsync, true);
        if (books.Length == 0)
        {
            var empty = Ui.Stack(Ui.Text("书架里还没有书", 20), Ui.Text("可拖入 TXT 或 EPUB 文件，也可以搜索在线书。", 14, true));
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            return PageLayout(PageHeader("书架", Ui.Row(Button("搜索", () => SelectPage("search")), add)), empty);
        }

        var search = new TextBox { Width = 190, PlaceholderText = "搜索书架", Text = shelfQuery };
        AutomationProperties.SetName(search, "搜索书架");
        var header = PageHeader("书架", Ui.Row(search, add));
        ((TextBlock)header.Children[0]).FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var groupList = store.State["groups"].Objects().ToArray();
        var groups = Ui.Combo(new[] { "全部分组" }.Concat(groupList.Select(group => group.S("name"))),
            Array.FindIndex(groupList, group => group.S("id") == shelfGroup) + 1);
        var kinds = Ui.Combo(["全部书籍", "本地", "在线"], shelfKind == "local" ? 1 : shelfKind == "online" ? 2 : 0);
        var sort = Ui.Combo(SortLabels, Math.Max(0, Array.IndexOf(SortKeys, store.Settings.S("shelfSort"))));
        AutomationProperties.SetName(groups, "书籍分组");
        AutomationProperties.SetName(kinds, "书籍类型");
        AutomationProperties.SetName(sort, "书架排序");
        var gridMode = ShelfViewButton("封面网格", "\uE80A");
        var listMode = ShelfViewButton("紧凑列表", "\uE8FD");
        var newGroup = ActionButton("新建分组", CreateGroupAsync);
        var filters = Ui.AdaptiveCommands(groups, kinds, sort, Ui.Row(gridMode, listMode), newGroup);

        var coverSize = store.Settings.S("shelfSize") switch { "sm" => 116, "lg" => 184, _ => 152 };
        var covers = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = Ui.Template($$"""
                <StackPanel Width="{{coverSize}}" Spacing="6" Padding="5,6,5,13">
                  <Border Height="{{(coverSize - 10) * 3 / 2}}" CornerRadius="6">
                    <Grid Background="{Binding CoverTint}">
                      <TextBlock Text="{Binding Name}" Foreground="White" FontFamily="SimSun" FontSize="19" MaxLines="4"
                                 TextWrapping="Wrap" TextTrimming="CharacterEllipsis" Margin="13,21" VerticalAlignment="Top"/>
                      <Image Source="{Binding Cover}" Stretch="UniformToFill" AutomationProperties.AccessibilityView="Raw"/>
                    </Grid>
                  </Border>
                  <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                  <TextBlock Text="{Binding Author}" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis"/>
                </StackPanel>
                """)
        };
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = Ui.Template("""
                <Grid Padding="8,5" ColumnSpacing="12" BorderThickness="0,0,0,1" BorderBrush="{ThemeResource DividerStrokeColorDefaultBrush}">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="98"/><ColumnDefinition Width="148"/><ColumnDefinition Width="62"/></Grid.ColumnDefinitions>
                  <Grid ColumnSpacing="10">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="26"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Border Height="36" CornerRadius="3" Background="{Binding CoverTint}"><Image Source="{Binding Cover}" Stretch="UniformToFill" AutomationProperties.AccessibilityView="Raw"/></Border>
                    <TextBlock Grid.Column="1" Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                  </Grid>
                  <TextBlock Grid.Column="1" Text="{Binding Author}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                  <TextBlock Grid.Column="2" Text="{Binding Chapter}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                  <TextBlock Grid.Column="3" Text="{Binding ProgressLabel}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                </Grid>
                """)
        };
        list.ItemContainerStyle = new Style(typeof(ListViewItem));
        list.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var listHead = Ui.Columns("*,98,148,62", Ui.Text("书名", 13, true), Ui.Text("作者", 13, true),
            Ui.Text("上次读到", 13, true), new TextBlock { Text = "进度", FontSize = 13, Opacity = .76, HorizontalAlignment = HorizontalAlignment.Right });
        listHead.Margin = new(20, 0, 20, 2);
        var compactList = Ui.Rows("Auto,*", listHead, list);
        compactList.RowSpacing = 4;

        var noMatch = Ui.Text("没有符合筛选条件的书籍", 15, true);
        noMatch.HorizontalAlignment = HorizontalAlignment.Center;
        var items = new Grid { Children = { covers, compactList, noMatch } };
        var details = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(details, "书籍详情面板");
        var dock = new Border
        {
            Width = 270, BorderThickness = new(1, 0, 0, 0), Padding = new(20, 0, 0, 0),
            BorderBrush = Ui.Brush("DividerStrokeColorDefaultBrush"), Child = Ui.Scroll(details)
        };
        BookRow? selected = null;
        var quickTitle = Ui.Text("");
        quickTitle.TextWrapping = TextWrapping.NoWrap;
        quickTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        var quickRead = ActionButton("继续阅读", () => selected is { } row ? OpenReaderAsync(row.Data) : Task.CompletedTask, true);
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight };
        var more = new Button { Content = "详情", Flyout = flyout };
        flyout.Opening += (_, _) =>
        {
            if (selected is not { } row) return;
            var sheet = Ui.Scroll(ShelfDetails(row, flyout.Hide));
            sheet.Width = DialogWidth(292);
            sheet.MaxHeight = Math.Clamp(root.ActualHeight - 140, 200, 480);
            flyout.Content = sheet;
        };
        // Keep a valid presenter before the first opening.
        flyout.Content = new Grid();
        var quick = Ui.Columns("*,Auto,Auto", quickTitle, more, quickRead);
        var collection = Ui.Rows("Auto,*,Auto", filters, items, quick);
        collection.RowSpacing = 12;
        var split = Ui.Columns("*,Auto", collection, dock);
        split.ColumnSpacing = 18;
        var listVisible = store.Settings.B("shelfList");
        var synchronizing = false;
        var updatingViewButtons = false;

        void ArrangeDetails()
        {
            var wide = split.ActualWidth >= 850;
            var dockVisibility = wide && selected is not null ? Visibility.Visible : Visibility.Collapsed;
            dock.Visibility = dockVisibility;
            split.ColumnSpacing = dockVisibility == Visibility.Visible ? 18 : 0;
            quick.Visibility = !wide && selected is not null ? Visibility.Visible : Visibility.Collapsed;
            if (dockVisibility == Visibility.Visible && selected is { } row && !ReferenceEquals(details.Tag, row))
            {
                details.Tag = row;
                details.Content = ShelfDetails(row);
            }
        }
        void SelectBook(BookRow? row)
        {
            selected = row;
            if (row is not null) shelfSelected = row.Id;
            synchronizing = true;
            try { covers.SelectedItem = row; list.SelectedItem = row; }
            finally { synchronizing = false; }
            quickTitle.Text = row?.Name ?? "";
            quickRead.Content = row?.ReadLabel ?? "开始阅读";
            ArrangeDetails();
        }
        void UpdateView()
        {
            covers.Visibility = listVisible ? Visibility.Collapsed : Visibility.Visible;
            compactList.Visibility = listVisible ? Visibility.Visible : Visibility.Collapsed;
            updatingViewButtons = true;
            try { gridMode.IsChecked = !listVisible; listMode.IsChecked = listVisible; }
            finally { updatingViewButtons = false; }
        }
        void SetView(bool showList)
        {
            if (listVisible != showList)
            {
                listVisible = showList;
                store.Settings["shelfList"] = showList;
                SaveSoon();
            }
            UpdateView();
            if (selected is { } row) ((ListViewBase)(showList ? list : covers)).ScrollIntoView(row);
        }
        void Refresh()
        {
            shelfGroup = groups.SelectedIndex > 0 ? groupList[groups.SelectedIndex - 1].S("id") : "";
            shelfKind = kinds.SelectedIndex == 1 ? "local" : kinds.SelectedIndex == 2 ? "online" : "";
            shelfQuery = search.Text;
            var query = shelfQuery.Trim();
            var visible = books.Where(book => (shelfGroup == "" || book.Data.S("groupId", "g_default") == shelfGroup) &&
                (shelfKind == "" || (book.Data.S("kind") == "local") == (shelfKind == "local")) &&
                (query.Length == 0 || book.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || book.Author.Contains(query, StringComparison.OrdinalIgnoreCase)));
            var chinese = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
            var sorted = store.Settings.S("shelfSort") switch
            {
                "name" => visible.OrderBy(book => book.Name, chinese),
                "author" => visible.OrderBy(book => book.Author, chinese),
                "created" => visible.OrderByDescending(book => book.Data.D("createdAt")),
                "updated" => visible.OrderByDescending(book => book.Data.D("updatedAt")),
                "progress" => visible.OrderByDescending(book => book.Percent),
                _ => visible.OrderByDescending(book => store.Progress[book.Id].D("at")).ThenByDescending(book => book.Data.D("createdAt"))
            };
            var rows = sorted.ToArray();
            synchronizing = true;
            try { covers.ItemsSource = rows; list.ItemsSource = rows; }
            finally { synchronizing = false; }
            SelectBook(rows.FirstOrDefault(book => book.Id == shelfSelected) ??
                rows.Where(book => store.Progress[book.Id] is not null).MaxBy(book => store.Progress[book.Id].D("at")) ?? rows.FirstOrDefault());
            noMatch.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var view in new ListViewBase[] { covers, list })
        {
            AutomationProperties.SetName(view, ReferenceEquals(view, covers) ? "封面书架" : "紧凑书架");
            view.SelectionChanged += (_, _) =>
            {
                if (!synchronizing && view.SelectedItem is BookRow row) SelectBook(row);
            };
            view.ContainerContentChanging += (_, e) =>
            {
                if (!e.InRecycleQueue && e.Item is BookRow row) e.ItemContainer.ContextFlyout = BookMenu(row.Data);
            };
            view.AddHandler(UIElement.DoubleTappedEvent, new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler((_, e) =>
            {
                for (var node = e.OriginalSource as DependencyObject; node is not null && node != view; node = VisualTreeHelper.GetParent(node))
                {
                    if (node is not Microsoft.UI.Xaml.Controls.Primitives.SelectorItem { Content: BookRow row }) continue;
                    e.Handled = true;
                    _ = RunAsync(() => OpenReaderAsync(row.Data));
                    break;
                }
            }), true);
            var enter = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, ScopeOwner = view };
            enter.Invoked += (sender, args) =>
            {
                if (view.SelectedItem is not BookRow row) return;
                args.Handled = true;
                _ = RunAsync(() => OpenReaderAsync(row.Data));
            };
            view.KeyboardAccelerators.Add(enter);
        }
        gridMode.Checked += (_, _) => { if (!updatingViewButtons) SetView(false); };
        listMode.Checked += (_, _) => { if (!updatingViewButtons) SetView(true); };
        gridMode.Unchecked += (_, _) => { if (!updatingViewButtons && !listVisible) UpdateView(); };
        listMode.Unchecked += (_, _) => { if (!updatingViewButtons && listVisible) UpdateView(); };
        groups.SelectionChanged += (_, _) => Refresh();
        kinds.SelectionChanged += (_, _) => Refresh();
        search.TextChanged += (_, _) => Refresh();
        sort.SelectionChanged += (_, _) => { store.Settings["shelfSort"] = SortKeys[Math.Max(0, sort.SelectedIndex)]; SaveSoon(); Refresh(); };
        split.SizeChanged += (_, _) => ArrangeDetails();
        UpdateView();
        Refresh();
        captureShelfPosition = book =>
        {
            ListViewBase view = listVisible ? list : covers;
            var scroll = ShelfScrollViewer(view);
            var offset = scroll?.VerticalOffset ?? 0;
            var width = view.ActualWidth;
            var firstIndex = view.ItemsPanelRoot switch
            {
                ItemsWrapGrid wrap => wrap.FirstVisibleIndex,
                ItemsStackPanel stack => stack.FirstVisibleIndex,
                _ => -1
            };
            var first = firstIndex >= 0 && firstIndex < view.Items.Count ? view.Items[firstIndex] : null;
            var inset = first is not null && scroll is not null && view.ContainerFromItem(first) is FrameworkElement firstContainer
                ? firstContainer.TransformToVisual(scroll).TransformPoint(default).Y : 0;

            return () =>
            {
                // Keep the visible order when returning. A new sort or filter still reads the updated progress.
                view.Items.OfType<BookRow>().FirstOrDefault(row => ReferenceEquals(row.Data, book))?.UpdateReadingProgress(store.Progress);
                details.Tag = null;
                SelectBook(selected);
                root.UpdateLayout();
                if (ShelfScrollViewer(view) is not { } target) return;
                var destination = offset;
                if (first is not null && Math.Abs(width - view.ActualWidth) > .5)
                {
                    view.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
                    view.UpdateLayout();
                    if (view.ContainerFromItem(first) is FrameworkElement container)
                        destination = target.VerticalOffset + container.TransformToVisual(target).TransformPoint(default).Y - inset;
                }
                target.ChangeView(null, Math.Clamp(destination, 0, target.ScrollableHeight), null, true);
                view.UpdateLayout();
                if (selected is not null && view.ContainerFromItem(selected) is Control item)
                {
                    var top = item.TransformToVisual(target).TransformPoint(default).Y;
                    if (top >= 0 && top + item.ActualHeight <= target.ViewportHeight)
                        item.Focus(FocusState.Programmatic);
                }
            };
        };
        return PageLayout(header, split);
    }

    private static ScrollViewer? ShelfScrollViewer(ListViewBase view)
    {
        for (DependencyObject? node = view.ItemsPanelRoot; node is not null && node != view; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroll) return scroll;
        return null;
    }

    private static Microsoft.UI.Xaml.Controls.Primitives.ToggleButton ShelfViewButton(string label, string glyph)
    {
        var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = new FontIcon { Glyph = glyph, FontSize = 16 } };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }

    private FrameworkElement ShelfDetails(BookRow row, Action? dismiss = null)
    {
        const double width = 144;
        var cover = new Border { Width = width, Height = 208, CornerRadius = new(7), Background = row.CoverTint };
        var coverTitle = new TextBlock
        {
            Text = row.Name, FontFamily = new("SimSun"), FontSize = 21, Margin = new(13, 23, 13, 10),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), MaxLines = 4, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var coverLayer = new Grid { Children = { coverTitle } };
        if (row.Cover is { } uri)
            coverLayer.Children.Add(new Image { Source = new BitmapImage(new Uri(uri)) { DecodePixelWidth = (int)width * 2 },
                Stretch = Stretch.UniformToFill });
        cover.Child = coverLayer;

        var title = Ui.Text(row.Name, 24);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var names = Ui.Stack(title, Ui.Text(row.Author, 14, true));
        names.Spacing = 4;
        var progress = new ProgressBar { Value = row.Percent, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(progress, "全书阅读进度");
        var chapter = Ui.Text(row.Chapter, 14, true);
        chapter.MaxLines = 2;
        chapter.TextTrimming = TextTrimming.CharacterEllipsis;
        var reading = Ui.Stack(chapter);
        reading.Spacing = 8;
        if (row.Progress.Length > 0) reading.Children.Add(Ui.Columns("*,Auto", progress, Ui.Text(row.Progress, 13, true)));
        var read = ActionButton(row.ReadLabel, () => { dismiss?.Invoke(); return OpenReaderAsync(row.Data); }, true);
        read.HorizontalAlignment = HorizontalAlignment.Stretch;
        read.HorizontalContentAlignment = HorizontalAlignment.Center;
        var content = Ui.Stack(cover, names, reading, read);
        content.Padding = new(2, 5, 4, 12);
        if (row.Data.S("intro") is { Length: > 0 } intro)
        {
            var summary = Ui.Text(intro.Trim(), 13, true);
            summary.MaxLines = 6;
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            content.Children.Add(summary);
        }
        return content;
    }
    private void AddMenuAction(MenuFlyout menu, string caption, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = caption };
        item.Click += (_, _) => _ = RunAsync(action);
        menu.Items.Add(item);
    }

    private MenuFlyout BookMenu(JsonObject book)
    {
        var menu = new MenuFlyout();
        AddMenuAction(menu, "阅读", () => OpenReaderAsync(book));
        var group = new MenuFlyoutSubItem { Text = "移动到分组" };
        foreach (var entry in store.State["groups"].Objects())
        {
            var item = new MenuFlyoutItem { Text = entry.S("name") };
            item.Click += (_, _) => _ = RunAsync(async () => { book["groupId"] = entry.S("id"); await store.SaveAsync(); Navigate("shelf"); });
            group.Items.Add(item);
        }
        menu.Items.Add(group);
        if (book.S("kind") == "online")
        {
            AddMenuAction(menu, "更新目录", async () =>
            {
                var updated = await RefreshTocAsync(book);
                Navigate("shelf");
                ShowNotice($"已更新目录，共 {updated.Count} 章");
            });
            AddMenuAction(menu, "换源", () => SwitchSourceAsync(book));
        }
        AddMenuAction(menu, "移出书架", async () =>
        {
            if (!await ConfirmAsync("移出书架", $"移出《{book.S("name")}》？", "移出")) return;
            store.Books.Remove(book);
            await store.SaveAsync();
            Navigate("shelf");
        });
        return menu;
    }

    private async Task CreateGroupAsync()
    {
        var input = new TextBox { Header = "分组名称", Width = 320, MaxLength = 40 };
        if (await DialogAsync("新建分组", input, "创建") != ContentDialogResult.Primary) return;
        var name = input.Text.Trim();
        if (name.Length == 0 || store.State["groups"].Objects().Any(g => g.S("name") == name))
        { ShowNotice("请输入一个未使用的分组名称", true); return; }
        store.State["groups"]!.AsArray().Add(new JsonObject { ["id"] = J.Id("G"), ["name"] = name });
        await store.SaveAsync();
        Navigate("shelf");
    }

}
