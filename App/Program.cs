using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Legado.UI3;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new ReaderApplication();
            });
        }
        catch (Exception e) { LogStartup(e); throw; }
    }

    internal static void LogStartup(Exception e)
    {
        try
        {
            var directory = Core.AppStore.DefaultRoot();
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup-error.txt"), $"{DateTimeOffset.Now:O} {e}\n");
        }
        catch (IOException) { }
    }
}

public sealed partial class ReaderApplication : Application
{
    private MainWindow? window;
    public ReaderApplication()
    {
        UnhandledException += (_, e) =>
        {
            if (window is null) { Program.LogStartup(e.Exception); return; }
            window.ReportError(e.Exception);
            e.Handled = true;
        };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception e) { Program.LogStartup(e); Exit(); }
    }
}
