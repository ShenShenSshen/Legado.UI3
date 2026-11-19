using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Legado.UI3.Core;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Legado.UI3;

public sealed partial class MainWindow : Window
{
    private readonly AppStore store = new();
    private readonly Grid root = new();
    private readonly NavigationView navigation = new();
    private readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly InfoBar notice = new() { Visibility = Visibility.Collapsed, IsClosable = true, Margin = new Thickness(16, 0, 16, 8) };
    private readonly ProgressBar busy = new() { IsIndeterminate = true, Height = 2, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Top };
    private readonly EngineHost engine;
    private readonly EngineHost parser;
    private readonly LocalBooks local;
    private readonly HttpVoiceLibrary voiceLibrary;
    private Task initialization = Task.CompletedTask;
    private int operations;
    private string currentPage = "shelf";
    private readonly DispatcherTimer autoBackupTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Tray tray;
    private bool closing;
    private AppWindowPresenter? windowedPresenter;

    public MainWindow()
    {
        Title = "Legado·UI3";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Legado.UI3.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 640;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        var titleBar = new Grid { Height = 39, Background = new SolidColorBrush(Colors.Transparent), Margin = new Thickness(0, 0, 138, 0) };
        root.RowDefinitions.Add(new() { Height = new GridLength(39) });
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(titleBar);
        notice.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
            notice.Visibility = notice.IsOpen ? Visibility.Visible : Visibility.Collapsed);
        Grid.SetRow(notice, 1); root.Children.Add(notice);
        navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        navigation.OpenPaneLength = 168;
        navigation.CompactModeThresholdWidth = 640;
        navigation.ExpandedModeThresholdWidth = 1000;
        navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        foreach (var (tag, label, glyph) in new[] { ("shelf", "书架", "\uE8F1"), ("search", "搜索", "\uE721"), ("explore", "浏览", "\uE707"), ("sources", "书源", "\uE8A5"), ("addons", "扩展", "\uEA86") })
            navigation.MenuItems.Add(new NavigationViewItem { Content = label, Tag = tag, Icon = new FontIcon { Glyph = glyph } });
        navigation.Content = page;
        navigation.SelectionChanged += (_, e) =>
        {
            var tag = e.IsSettingsSelected ? "settings" : (e.SelectedItem as NavigationViewItem)?.Tag?.ToString();
            if (tag is not null) Navigate(tag);
        };
        Grid.SetRow(navigation, 2); root.Children.Add(navigation);
        Grid.SetRow(busy, 2); root.Children.Add(busy);
        engine = new EngineHost(store.Root);
        parser = new EngineHost(store.Root, parser: true);
        local = new(store, (operation, payload) => parser.RunAsync(operation, payload));
        voiceLibrary = new(store.Root);
        Grid.SetRow(engine.View, 2); engine.View.HorizontalAlignment = HorizontalAlignment.Left; engine.View.VerticalAlignment = VerticalAlignment.Bottom; root.Children.Add(engine.View);
        Grid.SetRow(parser.View, 2); parser.View.HorizontalAlignment = HorizontalAlignment.Left; parser.View.VerticalAlignment = VerticalAlignment.Bottom; root.Children.Add(parser.View);
        Content = root;
        SetTitleBar(titleBar);
        root.ActualThemeChanged += OnWindowThemeChanged;
        ApplyAppTheme();
        root.Loaded += (_, _) =>
        {
            ApplyCaptionColors();
            initialization = Task.WhenAll(engine.InitializeAsync(), parser.InitializeAsync());
            _ = RunAsync(async () => { await initialization; if (store.LoadWarning is { } warning) ShowNotice(warning, true); });
#if LEGADO_UI3_SELF_TEST
            _ = RunSelfTestIfRequestedAsync();
#endif
        };
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => { if (readerBook is null) { focusSearchOnOpen = true; SelectPage("search"); } });
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = RunAsync(ImportBooksAsync));
        AddShortcut(VirtualKey.F11, VirtualKeyModifiers.None, ToggleFullScreen);
        AddShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () => { if (readerBook is not null) HandleReaderEscape(); });
        root.AllowDrop = true;
        root.DragOver += (_, e) => { if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; };
        root.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var files = await e.DataView.GetStorageItemsAsync();
            await RunAsync(async () => { await initialization; foreach (var file in files.OfType<StorageFile>()) await ImportDroppedAsync(file.Path); Navigate("shelf"); });
        };
        autoBackupTimer.Tick += (_, _) => _ = RunAutoBackupAsync(); autoBackupTimer.Start();
        tray = new Tray(WinRT.Interop.WindowNative.GetWindowHandle(this), AppWindow,
            () => store.Settings.B("minimizeToTray"), () => DispatcherQueue.TryEnqueue(() => _ = ExitAsync()));
        AppWindow.Closing += (sender, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            if (!store.Settings.B("closeToTray") || !tray.Hide()) _ = ExitAsync();
            else _ = RunAsync(FlushReadingProgressAsync);
        };
        Closed += (_, _) => { root.ActualThemeChanged -= OnWindowThemeChanged; autoBackupTimer.Stop(); progressTimer.Stop(); StopSpeech(); tray.Dispose(); readerWeb?.Close(); webPageView?.Close(); engine.Dispose(); parser.Dispose(); };
        navigation.SelectedItem = navigation.MenuItems[0];
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { action(); e.Handled = true; };
        root.KeyboardAccelerators.Add(accelerator);
    }

    private void Navigate(string tag)
    {
        currentPage = tag;
        page.Content = tag switch
        {
            "shelf" => ShelfPage(), "search" => SearchPage(), "explore" => ExplorePage(), "sources" => SourcesPage(), "addons" => AddonsPage(), _ => SettingsPage()
        };
    }
    private void SelectPage(string tag)
    {
        if (tag == "settings") { navigation.SelectedItem = navigation.SettingsItem; return; }
        var item = navigation.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag?.ToString() == tag);
        if (ReferenceEquals(navigation.SelectedItem, item)) Navigate(tag);
        else navigation.SelectedItem = item;
    }
    private Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center };
        if (primary) button.Style = (Style)Application.Current.Resources["LegadoPrimaryButtonStyle"];
        button.Click += async (_, _) => { button.IsEnabled = false; try { await RunAsync(action); } finally { button.IsEnabled = true; } };
        return button;
    }
    private Button Button(string label, Action action) => ActionButton(label, () => { action(); return Task.CompletedTask; });
    private static Grid PageHeader(string title, UIElement? actions = null) => Ui.Columns("*,Auto", Ui.Text(title, 28), actions ?? new StackPanel());
    private static Grid PageLayout(params UIElement[] rows)
    {
        var heights = string.Join(',', Enumerable.Range(0, rows.Length).Select(i => i == rows.Length - 1 ? "*" : "Auto"));
        var grid = Ui.Rows(heights, rows); grid.Margin = new Thickness(28, 16, 28, 24); return grid;
    }
    public void ReportError(Exception error)
    {
        ShowNotice(error.Message, true);
        try { File.AppendAllText(Path.Combine(store.Root, "errors.log"), $"{DateTimeOffset.Now:O} {error}\n"); } catch (IOException) { }
    }
    private void ShowNotice(string message, bool error = false)
    {
        notice.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        notice.Message = message; notice.IsOpen = true;
    }
    private async Task ExitAsync()
    {
        if (closing) return;
        closing = true;
        try { await FlushReadingProgressAsync(); await store.SaveAsync(); }
        catch (Exception e) { closing = false; ReportError(e); return; }
        Close();
    }
    private async Task RunAsync(Func<Task> action)
    {
        operations++; busy.Visibility = Visibility.Visible;
        try { await action(); }
        catch (Exception e) { ReportError(e); }
        finally { operations--; if (operations == 0) busy.Visibility = Visibility.Collapsed; }
    }
    private void SaveSoon() => _ = RunAsync(store.SaveAsync);
    private async Task<ContentDialogResult> DialogAsync(string title, UIElement content, string primary = "确定", string? secondary = null, ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = primary, SecondaryButtonText = secondary ?? "", CloseButtonText = "取消", DefaultButton = defaultButton, XamlRoot = root.XamlRoot };
        return await dialog.ShowAsync();
    }
    private double DialogWidth(double desired) => Math.Clamp(root.ActualWidth - 80, 248, desired);
    private async Task<bool> ConfirmAsync(string title, string message, string confirm = "删除") =>
        await DialogAsync(title, Ui.Text(message), confirm, defaultButton: ContentDialogButton.Close) == ContentDialogResult.Primary;
    private async Task<IReadOnlyList<StorageFile>> PickFilesAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return await picker.PickMultipleFilesAsync();
    }
    private async Task<string?> SaveFileAsync(string name, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = name };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add(extension.ToUpperInvariant(), new List<string> { extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }
    private void ToggleFullScreen() => SetFullScreen(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);

    private void SetFullScreen(bool fullscreen)
    {
        if (fullscreen != (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen))
        {
            if (fullscreen)
            {
                windowedPresenter = AppWindow.Presenter;
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                if (windowedPresenter is { } previous) AppWindow.SetPresenter(previous);
                else AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                windowedPresenter = null;
            }
        }
        UpdateWindowChrome();
    }

    private void UpdateWindowChrome()
    {
        var fullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        var showTitleBar = !fullscreen && readerBook is null;
        root.RowDefinitions[0].Height = new GridLength(showTitleBar ? 39 : 0);
        if (readerFullScreen is not null) readerFullScreen.Content = fullscreen ? "退出全屏" : "全屏";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.HasTitleBar != showTitleBar) presenter.SetBorderAndTitleBar(presenter.HasBorder || showTitleBar, showTitleBar);
            presenter.PreferredMinimumWidth = 640;
        }
        if (showTitleBar) ApplyCaptionColors();
    }
    private void OnWindowThemeChanged(FrameworkElement sender, object args)
    {
        if (AppWindow is null) return;
        ApplyCaptionColors();
        if (store.Settings.S("theme", "auto") == "auto") _ = RunAsync(RefreshReaderThemeAsync);
    }

    private void ApplyCaptionColors()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || AppWindow?.TitleBar is not { } titleBar) return;
        var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var dark = root.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;
        titleBar.ButtonForegroundColor = highContrast ? null : foreground;
        titleBar.ButtonInactiveForegroundColor = highContrast ? null : dark ? Colors.LightGray : Colors.DimGray;
        titleBar.ButtonHoverForegroundColor = highContrast ? null : foreground;
        titleBar.ButtonPressedForegroundColor = highContrast ? null : foreground;
        titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = highContrast ? null : Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(32, foreground.R, foreground.G, foreground.B);
        titleBar.ButtonPressedBackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(56, foreground.R, foreground.G, foreground.B);
    }
    private async Task ImportBooksAsync()
    {
        var files = await PickFilesAsync(".txt", ".epub"); if (files.Count == 0) return;
        await initialization;
        var failures = new List<string>(); int imported = 0;
        foreach (var file in files) { try { await local.ImportAsync(file.Path); imported++; } catch (Exception e) { failures.Add(file.Name + "：" + e.Message); } }
        SelectPage("shelf"); ShowNotice($"已导入 {imported} 本" + (failures.Count > 0 ? "；" + string.Join("；", failures) : ""), failures.Count > 0);
    }
    private async Task ImportDroppedAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".txt" or ".epub") await local.ImportAsync(path);
        else if (ext is ".json") await ImportPreviewAsync(await File.ReadAllTextAsync(path));
        else if (ext is ".zip" or ".bak") await RestoreBackupAsync(path);
    }
}
