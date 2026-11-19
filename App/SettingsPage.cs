using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Legado.UI3.Core;
using Windows.Media.SpeechSynthesis;
using Windows.System;

namespace Legado.UI3;

public sealed record SpeechVoiceOption(string Id, string Label, HttpVoice? Network);

public sealed partial class MainWindow
{
    private bool cloudBackupRunning;
    private string ThemeKey => store.Settings.S("appTheme", "system");

    private FrameworkElement SettingsPage()
    {
        var header = PageHeader("设置");
        header.HorizontalAlignment = HorizontalAlignment.Center;
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 22, Padding = new(0, 0, 0, 28) };
        foreach (var (label, body) in new (string, FrameworkElement)[]
        {
            ("阅读", ReadingSettings()), ("朗读", SpeechSettings()), ("书架", ShelfSettings()),
            ("搜索与网络", SearchSettings()), ("数据与备份", BackupSettings()), ("云端同步", CloudSettings()),
            ("系统", SystemSettings()), ("关于", AboutSettings())
        })
        {
            var heading = Ui.Text(label, 18);
            heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            var section = Ui.Stack(heading, Ui.Panel(SettingsRows(body)));
            section.Spacing = 10;
            content.Children.Add(section);
        }
        var surface = new Grid();
        surface.Children.Add(content);
        var scroll = Ui.Scroll(surface);
        scroll.HorizontalScrollMode = ScrollMode.Disabled;
        scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        scroll.SizeChanged += (_, e) => header.Width = content.Width = Math.Min(720, Math.Max(0, e.NewSize.Width - 16));
        return PageLayout(header, scroll);
    }

    private static FrameworkElement SettingsRows(FrameworkElement body)
    {
        if (body is not StackPanel stack) return body;
        var fields = stack.Children.OfType<FrameworkElement>().ToArray();
        stack.Children.Clear();
        stack.Spacing = 8;
        foreach (var field in fields)
        {
            var input = field is Grid grid && grid.Children.FirstOrDefault() is Slider slider ? slider : field;
            var label = input switch
            {
                ComboBox combo => TakeHeader(combo.Header, () => combo.Header = null),
                Slider range => TakeHeader(range.Header, () => range.Header = null),
                ToggleSwitch toggle => TakeHeader(toggle.Header, () => toggle.Header = null),
                TextBox text => TakeHeader(text.Header, () => text.Header = null),
                PasswordBox password => TakeHeader(password.Header, () => password.Header = null),
                _ => ""
            };
            if (label.Length == 0) { stack.Children.Add(field); continue; }

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, label);
            field.Width = double.NaN;
            field.HorizontalAlignment = input is ToggleSwitch ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            field.VerticalAlignment = VerticalAlignment.Center;
            var row = Ui.Columns("*,*", Ui.Text(label), field);
            row.MinHeight = 42;
            stack.Children.Add(row);
        }
        return stack;
    }

    private static string TakeHeader(object? header, Action clear)
    {
        if (header is not string label) return "";
        clear();
        return label;
    }
    private FrameworkElement ReadingSettings(bool reader = false)
    {
        var body = Ui.Stack(
            ChoiceSetting("阅读方式", "pageMode", ["滚动", "翻页"], ["scroll", "paged"], () => RefreshReaderAppearanceAsync(true)),
            ChoiceSetting("背景", "theme", ["跟随应用", "纸白", "米黄", "浅绿", "夜间"], ["auto", "paper", "sepia", "green", "night"], RefreshReaderThemeAsync),
            ChoiceSetting("字体", "fontFamily", ["宋体", "微软雅黑", "JetBrains Mono"], ["serif", "sans", "jetbrains"], () => RefreshReaderAppearanceAsync(false)),
            SliderSetting("字号", "fontSize", 14, 42, 1, 22, () => RefreshReaderAppearanceAsync(false)),
            SliderSetting("行距", "lineHeight", 1.2, 2.6, 0.1, 1.7, () => RefreshReaderAppearanceAsync(false)),
            SliderSetting("段距", "paraGapEm", 0, 2, 0.1, 0.5, () => RefreshReaderAppearanceAsync(false)),
            SliderSetting("正文宽度", "readingWidth", 420, 1400, 20, 760, () => RefreshReaderAppearanceAsync(false)),
            ToggleSetting("滚动时加载下一章", "autoCacheNext", true));
        if (reader) body.MaxWidth = 372;
        return reader ? new ScrollViewer { Content = body, MaxHeight = 445, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } : body;
    }

    private FrameworkElement SpeechSettings(bool reader = false)
    {
        var preferences = store.Settings["tts"] as JsonObject ?? new JsonObject();
        if (preferences.Parent is null) store.Settings["tts"] = preferences;
        var system = SpeechSynthesizer.AllVoices;
        var current = Ui.Text("系统默认");
        current.TextWrapping = TextWrapping.NoWrap;
        current.TextTrimming = TextTrimming.CharacterEllipsis;
        var picker = new DropDownButton
        {
            Content = current, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
        };
        var label = Ui.Text("音色");
        FrameworkElement voiceField = reader ? new StackPanel { Spacing = 6, Children = { label, picker } } :
            Ui.Columns("*,*", label, picker);
        voiceField.MinHeight = 42;
        var filter = new TextBox { PlaceholderText = "筛选音色" };
        AutomationProperties.SetName(filter, "筛选音色");
        var list = new ListView
        {
            IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = Ui.Template("""<TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>""")
        };
        AutomationProperties.SetName(list, "可用音色");
        var noMatch = Ui.Text("没有匹配的音色", 13, true);
        noMatch.HorizontalAlignment = HorizontalAlignment.Center;
        var rows = new Grid { Children = { list, noMatch } };
        var pickerBody = Ui.Rows("Auto,*", filter, rows);
        pickerBody.RowSpacing = 8;
        var flyout = new Flyout { Content = pickerBody, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        picker.Flyout = flyout;
        var pitch = SliderSetting("音调", "pitch", .5, 2, .1, 1, data: preferences);
        var endpoint = Ui.Text("", 12, true);
        endpoint.Visibility = Visibility.Collapsed;
        var choices = new List<SpeechVoiceOption>();
        SpeechVoiceOption? selectedVoice = null;
        Button? remove = null;
        void ApplyFilter()
        {
            var query = filter.Text.Trim();
            var matches = query.Length == 0 ? choices.ToArray() :
                choices.Where(choice => choice.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = matches;
            list.SelectedItem = matches.Contains(selectedVoice) ? selectedVoice : null;
            noMatch.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void DescribeVoice()
        {
            current.Text = selectedVoice?.Label ?? "系统默认";
            AutomationProperties.SetName(picker, "选择音色，当前为 " + current.Text);
            var voice = selectedVoice?.Network;
            if (remove is not null) remove.Visibility = choices.Any(choice => choice.Network is not null) ? Visibility.Visible : Visibility.Collapsed;
            endpoint.Text = voice is not null && Uri.TryCreate(voice.Url, UriKind.Absolute, out var target)
                ? $"朗读时将正文发送至 {target.Host}" : "";
            endpoint.Visibility = endpoint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (pitch is Grid layout && layout.Children[0] is Slider slider)
                slider.IsEnabled = voice is null || voice.HasPitch;
        }
        void Reload()
        {
            var selected = preferences.S("voiceURI");
            List<HttpVoice> online;
            try { online = voiceLibrary.Load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                online = [];
                ShowNotice("未能读取本机网络语音配置，仍可使用系统语音", true);
            }
            choices.Clear();
            choices.Add(new("", "系统默认", null));
            choices.AddRange(system.Select(v => new SpeechVoiceOption(v.Id, v.DisplayName + " (" + v.Language + ")", null)));
            choices.AddRange(online.Select(v => new SpeechVoiceOption(v.Id, "网络 · " + v.Name, v)));
            if (selected.StartsWith("http:", StringComparison.Ordinal) && !choices.Any(c => c.Id == selected))
                choices.Add(new(selected, "网络语音（本机未配置）", null));
            selectedVoice = choices.FirstOrDefault(choice => choice.Id == selected) ?? choices[0];
            DescribeVoice();
        }
        filter.TextChanged += (_, _) => ApplyFilter();
        flyout.Opening += (_, _) =>
        {
            pickerBody.Width = Math.Max(200, Math.Min(DialogWidth(420), picker.ActualWidth + 24));
            pickerBody.Height = Math.Clamp(root.ActualHeight - 240, 156, 360);
            filter.Text = "";
            ApplyFilter();
        };
        flyout.Opened += (_, _) =>
        {
            if (selectedVoice is not null) list.ScrollIntoView(selectedVoice);
            filter.Focus(FocusState.Programmatic);
        };
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not SpeechVoiceOption choice) return;
            flyout.Hide();
            if (preferences.S("voiceURI") == choice.Id) return;
            selectedVoice = choice;
            preferences["voiceURI"] = choice.Id;
            DescribeVoice();
            SaveSoon();
        };
        remove = ActionButton("批量删除音色", async () =>
        {
            var imported = voiceLibrary.Load();
            if (imported.Count == 0) { Reload(); return; }
            var deleteList = new ListView
            {
                Width = DialogWidth(420), Height = Math.Clamp(root.ActualHeight - 220, 120, 360),
                SelectionMode = ListViewSelectionMode.Multiple, ItemsSource = imported,
                ItemTemplate = Ui.Template("""<TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>""")
            };
            AutomationProperties.SetName(deleteList, "选择要删除的网络音色");
            var dialog = new ContentDialog
            {
                Title = "批量删除音色", Content = deleteList, XamlRoot = root.XamlRoot,
                PrimaryButtonText = "删除", IsPrimaryButtonEnabled = false,
                CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            deleteList.SelectionChanged += (_, _) =>
            {
                var count = deleteList.SelectedItems.Count;
                dialog.IsPrimaryButtonEnabled = count > 0;
                dialog.PrimaryButtonText = count > 0 ? $"删除 {count} 个音色" : "删除";
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                var ids = deleteList.SelectedItems.Cast<HttpVoice>().Select(voice => voice.Id).ToHashSet(StringComparer.Ordinal);
                var count = await DeleteHttpVoicesAsync(ids);
                if (count > 0) ShowNotice($"已删除 {count} 个音色");
            }
            finally { Reload(); }
        });
        DescribeVoice();
        picker.Loaded += (_, _) => Reload();
        var import = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        AddMenuAction(import, "添加 Edge TTS", async () =>
        {
            var input = new TextBox { Header = "服务地址", Text = "http://127.0.0.1:1233", Width = DialogWidth(420) };
            var content = Ui.Stack(input, Ui.Text("填写已运行的 legado-edge-tts 服务地址。将添加云希和晓晓。", 13, true));
            if (await DialogAsync("添加 Edge TTS", content, "添加音色") != ContentDialogResult.Primary) return;
            var voices = HttpVoice.EdgePresets(input.Text);
            await voiceLibrary.MergeAsync(voices);
            Reload();
            ShowNotice("已添加云希和晓晓，可在音色列表选择");
        });
        AddMenuAction(import, "选择配置文件", async () =>
        {
            var files = await PickFilesAsync(".json", ".txt");
            if (files.Count == 0) return;
            var file = files[0].Path;
            if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("朗读配置超过 1 MB");
            await ImportHttpVoicesAsync(await File.ReadAllTextAsync(file));
            Reload();
        });
        AddMenuAction(import, "粘贴接口或 JSON", async () =>
        {
            var input = new TextBox { PlaceholderText = "https://服务地址/tts?text={{speakText}} 或 Legado 朗读 JSON",
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = DialogWidth(520), MinHeight = 146, MaxLength = 1024 * 1024 };
            if (await DialogAsync("导入网络语音", input, "解析") != ContentDialogResult.Primary) return;
            await ImportHttpVoicesAsync(input.Text);
            Reload();
        });
        return Ui.Stack(
            voiceField, endpoint,
            Ui.AdaptiveCommands(new DropDownButton { Content = "导入网络语音", Flyout = import }, ActionButton("试听", PreviewSpeechAsync), remove),
            SliderSetting("语速", "rate", .5, 2, .1, 1, data: preferences),
            pitch,
            ToggleSetting("读完自动读下一章", "autoNext", true, preferences));
    }

    private async Task ImportHttpVoicesAsync(string input)
    {
        var (voices, rejected) = HttpVoice.Parse(input);
        var errors = string.Join("\n", rejected.Take(4));
        if (voices.Count == 0) { ShowNotice(errors.Length > 0 ? errors : "没有识别到朗读配置", true); return; }
        var accepted = string.Join("\n", voices.Take(8).Select(v => v.Name));
        if (voices.Count > 8) accepted += $"\n另有 {voices.Count - 8} 个";
        if (rejected.Count > 0) accepted += $"\n\n跳过 {rejected.Count} 个\n{errors}";
        var summary = Ui.Scroll(Ui.Text(accepted));
        summary.MaxHeight = 330;
        if (await DialogAsync($"导入 {voices.Count} 个网络语音", summary, "导入") != ContentDialogResult.Primary) return;
        await voiceLibrary.MergeAsync(voices);
        ShowNotice($"已导入 {voices.Count} 个，可在音色列表选择");
    }

    private async Task<int> DeleteHttpVoicesAsync(IReadOnlySet<string> ids)
    {
        var preferences = store.Settings["tts"]!.AsObject();
        var id = preferences.S("voiceURI");
        if (ids.Contains(id))
        {
            preferences["voiceURI"] = "";
            try { await store.SaveAsync(); }
            catch { if (preferences.S("voiceURI") == "") preferences["voiceURI"] = id; throw; }
            StopSpeech();
        }
        return await voiceLibrary.RemoveAsync(ids);
    }
    private FrameworkElement ShelfSettings() => Ui.Stack(
        ChoiceSetting("排序", "shelfSort", SortLabels, SortKeys),
        ChoiceSetting("封面大小", "shelfSize", ["小", "中", "大"], SizeKeys));
    private FrameworkElement SearchSettings() => Ui.Stack(
        SliderSetting("搜索并发", "maxConcurrent", 1, 6, 1, 3),
        SearchLimitSetting("每次检索书源数", "searchLimit", 40, [10, 20, 40, 60, 80, 100, 120]),
        SearchLimitSetting("换源时检索书源数", "switchLimit", 24, [8, 16, 24, 32, 48, 64, 80, 120]));

    private FrameworkElement SearchLimitSetting(string label, string key, int fallback, int[] presets)
    {
        var saved = store.Settings.I(key, fallback);
        var limits = presets.Concat(saved > 0 ? [saved] : []).Distinct().Order().Append(0).ToArray();
        return ChoiceSetting(label, key, limits.Select(n => n == 0 ? "全部" : n.ToString()).ToArray(),
            limits.Select(n => n.ToString()).ToArray(), fallback: fallback.ToString());
    }
    private FrameworkElement SystemSettings() => Ui.Stack(
        ChoiceSetting("应用主题", "appTheme", ["跟随系统", "浅色", "深色"], ["system", "light", "dark"], () => { ApplyAppTheme(); return Task.CompletedTask; }),
        ToggleSetting("关闭窗口时保留在托盘", "closeToTray", false),
        ToggleSetting("最小化时保留在托盘", "minimizeToTray", false));
    private void ApplyAppTheme() => root.RequestedTheme = ThemeKey switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };

    private FrameworkElement AboutSettings() => Ui.Stack(Ui.Text("Legado·UI3", 22), Ui.Text("Windows x64  ·  开源阅读", 13, true),
        ActionButton("查看上游项目 Legado", async () => { await Launcher.LaunchUriAsync(new("https://github.com/gedoor/legado")); }));

    private FrameworkElement ChoiceSetting(string label, string key, string[] names, string[] values, Func<Task>? changed = null, string fallback = "")
    {
        var box = Ui.Combo(names, Math.Max(0, Array.IndexOf(values, store.Settings.S(key, fallback))), 220);
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.Header = label;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            store.Settings[key] = values[box.SelectedIndex];
            _ = RunAsync(async () => { if (changed is not null) await changed(); await store.SaveAsync(); });
        };
        return box;
    }
    private FrameworkElement SliderSetting(string label, string key, double min, double max, double step, double initial, Func<Task>? changed = null, JsonObject? data = null)
    {
        data ??= store.Settings;
        var value = Ui.Text("", 13, true);
        var slider = new Slider { Header = label, Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(data.D(key, initial), min, max), MinWidth = 154 };
        value.Text = slider.Value.ToString("0.##");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = RunAsync(async () => { if (changed is not null) await changed(); await store.SaveAsync(); });
        };
        slider.ValueChanged += (_, e) => { data[key] = e.NewValue; value.Text = e.NewValue.ToString("0.##"); timer.Stop(); timer.Start(); };
        var row = Ui.Columns("*,44", slider, value); row.Width = 286; row.HorizontalAlignment = HorizontalAlignment.Left;
        return row;
    }
    private FrameworkElement ToggleSetting(string label, string key, bool initial, JsonObject? data = null)
    {
        data ??= store.Settings;
        var toggle = new ToggleSwitch { Header = label, IsOn = data.B(key, initial), OnContent = "开", OffContent = "关" };
        toggle.Toggled += (_, _) => { data[key] = toggle.IsOn; SaveSoon(); };
        return toggle;
    }

    private FrameworkElement BackupSettings() => Ui.Stack(
        ActionButton("导出备份", ExportBackupAsync, true),
        ActionButton("从备份恢复", async () =>
        {
            var files = await PickFilesAsync("*");
            if (files.Count > 0) await RestoreBackupAsync(files[0].Path);
        }),
        ActionButton("打开数据目录", async () => { await Launcher.LaunchFolderPathAsync(store.Root); }),
        ActionButton("清理在线章节缓存", async () =>
        {
            if (!await ConfirmAsync("清理章节缓存", "清理在线章节缓存？之后打开未缓存的章节需要联网。", "清理")) return;
            var directory = Path.Combine(store.Root, "cache");
            if (Directory.Exists(directory))
                foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)) File.Delete(file);
            ShowNotice("章节缓存已清理");
        }));

    private async Task ExportBackupAsync()
    {
        var include = new CheckBox { Content = "包含本地书原文件", IsChecked = true };
        if (await DialogAsync("导出备份", include, "选择保存位置") != ContentDialogResult.Primary) return;
        var path = await SaveFileAsync("Legado.UI3-" + DateTime.Now.ToString("yyyyMMdd"), ".zip");
        if (path is not null) { await Backup.ExportAsync(store, path, include.IsChecked == true); ShowNotice("备份已导出"); }
    }
    private async Task RestoreBackupAsync(string path)
    {
        var preview = await Backup.ReadAsync(path);
        var summary = $"书籍 {preview.State["books"]!.AsArray().Count} 本，书源 {preview.State["sources"]!.AsArray().Count} 个，书签 {preview.State["bookmarks"]!.AsArray().Count} 条。";
        if (preview.Skipped > 0) summary += $"\n有 {preview.Skipped} 本手机本地书需重新导入原文件。";
        var result = await DialogAsync("恢复备份", Ui.Text(summary), "合并", "替换当前数据");
        if (result == ContentDialogResult.None) return;
        var replace = result == ContentDialogResult.Secondary;
        if (replace && !await ConfirmAsync("替换当前数据", "用此备份替换当前书架、书源和设置？", "替换")) return;
        await Backup.ApplyAsync(store, preview, replace);
        ApplyAppTheme();
        SelectPage("shelf");
        ShowNotice("备份已恢复");
    }

    private FrameworkElement CloudSettings()
    {
        var preferences = store.Settings["webdav"] as JsonObject ?? new JsonObject();
        if (preferences.Parent is null) store.Settings["webdav"] = preferences;
        var address = new TextBox { Header = "服务器地址", Text = preferences.S("url") };
        var user = new TextBox { Header = "账号", Text = preferences.S("user") };
        var password = new PasswordBox { Header = "应用密码", Password = preferences.S("pass") };
        var folder = new TextBox { Header = "远端目录", Text = preferences.S("dir", "legado") };
        async Task SaveConfig()
        {
            preferences["url"] = address.Text.Trim();
            preferences["user"] = user.Text;
            preferences["pass"] = password.Password;
            preferences["dir"] = folder.Text.Trim();
            await store.SaveAsync();
        }
        var actions = Ui.AdaptiveCommands(
            ActionButton("测试连接", async () => { await SaveConfig(); using var dav = Dav(); await dav.TestAsync(); ShowNotice("WebDAV 已连接"); }),
            ActionButton("立即备份", async () => { await SaveConfig(); await CloudBackupAsync(true); ShowNotice("云端备份已上传"); }, true),
            ActionButton("云端备份", async () => { await SaveConfig(); await CloudListAsync(); }));
        var body = Ui.Stack(address, user, password, folder, actions,
            ToggleSetting("自动备份", "autoBackup", false, preferences),
            SliderSetting("备份间隔（小时）", "intervalHours", 6, 72, 6, 24, data: preferences),
            SliderSetting("保留最近备份份数", "keep", 1, 30, 1, 10, data: preferences));
        return body;
    }
    private async Task CloudBackupAsync(bool include)
    {
        if (cloudBackupRunning) throw new IOException("正在执行另一份备份");
        cloudBackupRunning = true;
        var temp = Path.Combine(store.Root, "backup-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var dav = Dav();
            const string prefix = "legado-ui3-";
            var name = prefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip";
            await Backup.ExportAsync(store, temp, include);
            await dav.UploadAsync(name, temp);
            var config = store.Settings["webdav"]!.AsObject();
            config["lastBackupAt"] = J.Now;
            await store.SaveAsync();
            if (config.B("autoBackup"))
            {
                var backups = await dav.ListAsync();
                foreach (var old in backups.Where(b => b.Name.StartsWith(prefix, StringComparison.Ordinal)).Skip(Math.Clamp(config.I("keep", 10), 1, 30)))
                    await dav.DeleteAsync(old.Name);
            }
        }
        finally { cloudBackupRunning = false; if (File.Exists(temp)) File.Delete(temp); }
    }
    private async Task RunAutoBackupAsync()
    {
        var config = store.Settings["webdav"];
        if (cloudBackupRunning || !config.B("autoBackup") || config.S("user") == "") return;
        if (J.Now - config.D("lastBackupAt") < TimeSpan.FromHours(Math.Clamp(config.I("intervalHours", 24), 6, 72)).TotalMilliseconds) return;
        await RunAsync(() => CloudBackupAsync(true));
    }
    private async Task CloudListAsync()
    {
        using var dav = Dav();
        var entries = await dav.ListAsync();
        if (entries.Count == 0) { ShowNotice("云端没有备份"); return; }
        var list = Ui.Combo(entries.Select(e => e.Name), 0, 420);
        var choice = await DialogAsync("云端备份", list, "恢复", "删除");
        if (list.SelectedIndex < 0 || choice == ContentDialogResult.None) return;
        var entry = entries[list.SelectedIndex];
        if (choice == ContentDialogResult.Secondary)
        {
            if (await ConfirmAsync("删除云端备份", $"删除 {entry.Name}？")) { await dav.DeleteAsync(entry.Name); ShowNotice("云端备份已删除"); }
            return;
        }
        var temp = Path.Combine(store.Root, "download-" + Guid.NewGuid().ToString("N") + ".zip");
        try { await dav.DownloadAsync(entry.Name, temp); await RestoreBackupAsync(temp); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
