using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Legado.UI3.Core;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private static readonly string[] SortLabels = ["最近阅读", "最近加入", "书名", "作者", "阅读进度", "最近更新"];
    private static readonly string[] SortKeys = ["recent", "created", "name", "author", "progress", "updated"];
    private static readonly string[] SizeKeys = ["sm", "md", "lg"];
    private string shelfGroup = "", shelfKind = "";

    private FrameworkElement ShelfPage()
    {
        var books = store.Books.Objects().Select(b => new BookRow(b, store.Progress)).ToArray();
        var add = ActionButton("导入本地书", ImportBooksAsync, true);
        var tools = Ui.Row(Button("搜索", () => SelectPage("search")), add);
        if (books.Length == 0)
        {
            var empty = Ui.Stack(Ui.Text("书架里还没有书", 20), Ui.Text("将 TXT 或 EPUB 文件拖到这里，也可搜索在线书。", 14, true));
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            return PageLayout(PageHeader("书架", tools), empty);
        }

        var groupList = store.State["groups"].Objects().ToArray();
        var groupIndex = Array.FindIndex(groupList, g => g.S("id") == shelfGroup);
        var groups = Ui.Combo(new[] { "全部分组" }.Concat(groupList.Select(g => g.S("name"))), groupIndex + 1, 132);
        var kinds = Ui.Combo(["全部书籍", "本地", "在线"], shelfKind == "local" ? 1 : shelfKind == "online" ? 2 : 0);
        var sort = Ui.Combo(SortLabels, Math.Max(0, Array.IndexOf(SortKeys, store.Settings.S("shelfSort"))));
        var displayMenu = new MenuFlyout();
        var lists = store.Settings.B("shelfList");
        AddMenuAction(displayMenu, lists ? "封面网格" : "列表视图", () =>
        {
            store.Settings["shelfList"] = !lists;
            SaveSoon();
            Navigate("shelf");
            return Task.CompletedTask;
        });
        AddMenuAction(displayMenu, "新建分组", CreateGroupAsync);
        var filters = Ui.AdaptiveCommands(groups, kinds, sort, new DropDownButton { Content = "视图", Flyout = displayMenu });

        var coverSize = store.Settings.S("shelfSize") switch { "sm" => 116, "lg" => 184, _ => 148 };
        var covers = new GridView
        {
            IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = Ui.Template($$"""
                <StackPanel Width="{{coverSize}}" Spacing="7" Padding="4,6,4,12">
                  <Border Height="{{(coverSize - 8) * 3 / 2}}" CornerRadius="6" Background="{ThemeResource SubtleFillColorSecondaryBrush}" BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}" BorderThickness="1">
                    <Grid>
                      <TextBlock Text="{Binding Name}" FontFamily="SimSun" FontSize="18" TextWrapping="Wrap" Margin="12,18" TextTrimming="CharacterEllipsis" IsHitTestVisible="False"/>
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
            IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None,
            ItemTemplate = Ui.Template("""
                <Grid Padding="8,8" ColumnSpacing="12">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="34"/><ColumnDefinition Width="*"/><ColumnDefinition Width="65"/></Grid.ColumnDefinitions>
                  <Border Width="34" Height="50" CornerRadius="4" Background="{ThemeResource SubtleFillColorSecondaryBrush}"><Image Source="{Binding Cover}" Stretch="UniformToFill" /></Border>
                  <StackPanel Grid.Column="1" Spacing="4" VerticalAlignment="Center">
                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding Detail}" FontSize="13" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextTrimming="CharacterEllipsis"/>
                  </StackPanel>
                  <TextBlock Grid.Column="2" Text="{Binding Progress}" VerticalAlignment="Center" />
                </Grid>
                """)
        };
        ListViewBase view = lists ? list : covers;
        AutomationProperties.SetName(view, "书架书籍");
        view.ItemClick += (_, e) => { if (e.ClickedItem is BookRow row) _ = RunAsync(() => OpenReaderAsync(row.Data)); };
        view.ContainerContentChanging += (_, e) =>
        {
            if (!e.InRecycleQueue && e.Item is BookRow row) e.ItemContainer.ContextFlyout = BookMenu(row.Data);
        };
        var noMatch = Ui.Text("没有符合筛选条件的书籍", 15, true);
        noMatch.HorizontalAlignment = HorizontalAlignment.Center;
        noMatch.VerticalAlignment = VerticalAlignment.Center;
        var items = new Grid();
        items.Children.Add(view);
        items.Children.Add(noMatch);

        void Refresh()
        {
            shelfGroup = groups.SelectedIndex > 0 ? groupList[groups.SelectedIndex - 1].S("id") : "";
            shelfKind = kinds.SelectedIndex == 1 ? "local" : kinds.SelectedIndex == 2 ? "online" : "";
            var visible = books.Where(b => (shelfGroup == "" || b.Data.S("groupId", "g_default") == shelfGroup) && (shelfKind == "" || (b.Data.S("kind") == "local") == (shelfKind == "local")));
            var chinese = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
            var sorted = store.Settings.S("shelfSort") switch
            {
                "name" => visible.OrderBy(b => b.Name, chinese),
                "author" => visible.OrderBy(b => b.Author, chinese),
                "created" => visible.OrderByDescending(b => b.Data.D("createdAt")),
                "updated" => visible.OrderByDescending(b => b.Data.D("updatedAt")),
                "progress" => visible.OrderByDescending(b => b.Percent),
                _ => visible.OrderByDescending(b => store.Progress[b.Id].D("at")).ThenByDescending(b => b.Data.D("createdAt"))
            };
            var rows = sorted.ToArray();
            view.ItemsSource = rows;
            noMatch.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        groups.SelectionChanged += (_, _) => Refresh();
        kinds.SelectionChanged += (_, _) => Refresh();
        sort.SelectionChanged += (_, _) => { store.Settings["shelfSort"] = SortKeys[Math.Max(0, sort.SelectedIndex)]; SaveSoon(); Refresh(); };
        Refresh();

        var recent = books.Where(b => store.Progress[b.Id] is not null).MaxBy(b => store.Progress[b.Id].D("at"));
        if (recent is null) return PageLayout(PageHeader("书架", tools), filters, items);

        var cover = new Border { Width = 58, Height = 84, CornerRadius = new(4), Background = Ui.Brush("SubtleFillColorSecondaryBrush") };
        if (recent.Cover is { } uri)
            cover.Child = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(uri)), Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        else cover.Child = new TextBlock { Text = recent.Name, FontFamily = new("SimSun"), FontSize = 13, Margin = new(6), MaxLines = 3, TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Value = recent.Percent, Width = 156, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(progress, "全书阅读进度");
        var details = Ui.Stack(Ui.Text("最近阅读", 12, true), Ui.Text(recent.Name, 20), Ui.Text(recent.Chapter + "  " + recent.Progress, 13, true), progress);
        details.Spacing = 5;
        var hero = Ui.Panel(Ui.Columns("Auto,*,Auto", cover, details, ActionButton("继续阅读", () => OpenReaderAsync(recent.Data), true)));
        hero.Padding = new(16);
        return PageLayout(PageHeader("书架", tools), hero, filters, items);
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
