using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Legado.UI3.Core;

namespace Legado.UI3;

static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static TextBlock Text(string value, double size = 14, bool muted = false) => new()
    {
        Text = value, FontSize = size, Opacity = muted ? 0.76 : 1, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    public static StackPanel Stack(params UIElement[] children) { var panel = new StackPanel { Spacing = 16 }; foreach (var child in children) panel.Children.Add(child); return panel; }
    public static StackPanel Row(params UIElement[] children) { var row = Stack(children); row.Orientation = Orientation.Horizontal; row.Spacing = 8; return row; }
    public static Grid Columns(string widths, params UIElement[] children)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        foreach (var width in widths.Split(',')) grid.ColumnDefinitions.Add(new() { Width = width == "*" ? new GridLength(1, GridUnitType.Star) : width == "Auto" ? GridLength.Auto : new GridLength(double.Parse(width)) });
        for (int i = 0; i < children.Length; i++) { Grid.SetColumn((FrameworkElement)children[i], i); grid.Children.Add(children[i]); }
        return grid;
    }
    public static Grid Rows(string heights, params UIElement[] children)
    {
        var grid = new Grid { RowSpacing = 16 };
        foreach (var height in heights.Split(',')) grid.RowDefinitions.Add(new() { Height = height == "*" ? new GridLength(1, GridUnitType.Star) : height == "Auto" ? GridLength.Auto : new GridLength(double.Parse(height)) });
        for (int i = 0; i < children.Length; i++) { Grid.SetRow((FrameworkElement)children[i], i); grid.Children.Add(children[i]); }
        return grid;
    }
    public static ComboBox Combo(IEnumerable<string> items, int selected = 0, double width = double.NaN)
    {
        var box = new ComboBox { Width = width, MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;
        return box;
    }
    public static DataTemplate Template(string body) => (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" + body + "</DataTemplate>");
    public static FrameworkElement AdaptiveCommands(params UIElement[] commands)
    {
        var row = Row(commands);
        var scroll = new ScrollViewer
        {
            Content = row, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        return scroll;
    }
    public static Border Panel(UIElement child)
    {
        var border = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Padding="20" CornerRadius="8" BorderThickness="1"
                    Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"/>
            """);
        border.Child = child;
        return border;
    }
    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public static string Quality(JsonObject data) => data.S("quality") switch { "great" => "优质", "ok" => "可用", "poor" => "较差", "dead" => "失效", _ => "未检测" };
}

public sealed class BookRow : INotifyPropertyChanged
{
    private static readonly SolidColorBrush[] CoverPalette =
    [
        new(Windows.UI.Color.FromArgb(255, 39, 67, 95)),
        new(Windows.UI.Color.FromArgb(255, 43, 76, 73)),
        new(Windows.UI.Color.FromArgb(255, 77, 65, 98)),
        new(Windows.UI.Color.FromArgb(255, 94, 67, 55)),
        new(Windows.UI.Color.FromArgb(255, 49, 73, 97))
    ];
    public JsonObject Data { get; }
    public string Id => Data.S("id");
    public string Name => Data.S("name", "未命名");
    public string Author => Data.S("author") is { Length: > 0 } author ? author : "佚名";
    public string? Cover => Uri.TryCreate(Data.S("cover"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    public string Chapter { get; private set; } = "";
    public string Progress { get; private set; } = "";
    public string ProgressLabel => Progress.Length > 0 ? Progress : "未读";
    public string ReadLabel => Progress.Length > 0 ? "继续阅读" : "开始阅读";
    public Brush CoverTint => CoverPalette[Name.Take(32).Sum(character => character) % CoverPalette.Length];
    public double Percent { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public BookRow(JsonObject data, JsonObject progress)
    {
        Data = data;
        UpdateReadingProgress(progress);
    }
    public void UpdateReadingProgress(JsonObject progress)
    {
        var current = progress[Id];
        Chapter = current.S("title", "尚未阅读");
        Percent = Data.I("total") > 0 ? Math.Clamp((current.I("ci") + current.D("pct")) / Data.I("total") * 100, 0, 100) : 0;
        Progress = current is not null ? $"{Percent:0}%" : "";
        foreach (var name in new[] { nameof(Chapter), nameof(Progress), nameof(ProgressLabel), nameof(ReadLabel), nameof(Percent) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class SourceRow : INotifyPropertyChanged
{
    public JsonObject Data { get; }
    private readonly Action save;
    public string Id => Data.S("id");
    public string Name => Data.S("name");
    public string Url => Data.S("url");
    public string Group => Data.S("group", "未分组");
    public string Quality => Ui.Quality(Data);
    public string Note => Data.S("qNote", Data.S("lastError"));
    public bool Enabled { get => Data.B("enabled", true); set { Data["enabled"] = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); save(); } }
    public SourceRow(JsonObject data, Action save) { Data = data; this.save = save; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
