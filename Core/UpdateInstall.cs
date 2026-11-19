using System.Diagnostics;
using System.IO.Compression;

namespace Legado.UI3.Core;

internal static class UpdateInstall
{
    private const string Executable = "Legado.UI3.exe";
    internal const string Handoff = "--finish-update";

    internal static async Task<string> StageAsync(string dataRoot, UpdateRelease release, IProgress<string> progress,
        CancellationToken token, HttpClient? client = null)
    {
        var directory = Path.Combine(dataRoot, "updates", $"v{release.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var archivePath = directory + ".zip";
        var candidate = Path.Combine(directory, Executable);
        try
        {
            await Updates.DownloadAsync(release, archivePath, progress, token, client);
            progress.Report("校验完成，正在准备新版…");
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(archivePath);
                if (archive.Entries.Count > 5000 || archive.Entries.Sum(entry => (decimal)entry.Length) > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("更新包解压后超出允许大小。");
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    // The archive API performs its own path traversal checks.
                    if (entry.FullName.Contains(':')) throw new InvalidDataException("更新包包含无效路径。");
                }
                archive.ExtractToDirectory(directory);
                token.ThrowIfCancellationRequested();
                if (ReadVersion(candidate) != release.Version || !HasRuntime(directory))
                    throw new InvalidDataException("更新包缺少程序文件，或版本与发布信息不一致。");
            }, token);
            return candidate;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally { try { File.Delete(archivePath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal static void StartAfterExit(string candidate)
    {
        using var current = Process.GetCurrentProcess();
        var otherInstances = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable));
        try
        {
            if (otherInstances.Any(process => process.Id != current.Id && process.SessionId == current.SessionId))
                throw new IOException("请先关闭其他 Legado·UI3 窗口，再重试更新。");
        }
        finally { foreach (var process in otherInstances) process.Dispose(); }
        Start(candidate, Handoff, current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static bool WaitForPrevious(string[] args)
    {
        if (args.Length != 3 || args[0] != Handoff || !int.TryParse(args[1], out var id) ||
            !long.TryParse(args[2], out var started) || id == Environment.ProcessId) return false;
        try
        {
            using var previous = Process.GetProcessById(id);
            return previous.StartTime.ToUniversalTime().Ticks != started || previous.WaitForExit(30000);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    internal static bool RedirectToActive(string dataRoot, string[] args)
    {
        try
        {
            var candidate = ActiveExecutable(dataRoot);
            if (candidate is null || ReadVersion(candidate) is not { } version || version <= Updates.CurrentVersion) return false;
            Start(candidate, args);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    internal static void Activate(string dataRoot, string executable)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var folder = Path.GetFileName(directory);
        var expected = Resolve(dataRoot, folder);
        if (expected is null || !string.Equals(Path.GetFullPath(executable), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("新版程序位置无效。");
        var active = ActiveExecutable(dataRoot);
        if (active is not null && ReadVersion(active) is { } version && version > Updates.CurrentVersion) return;
        var marker = Path.Combine(dataRoot, "updates", "active.txt");
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, folder);
            File.Move(temporary, marker, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? ActiveExecutable(string root)
    {
        var marker = Path.Combine(root, "updates", "active.txt");
        return File.Exists(marker) ? Resolve(root, File.ReadAllText(marker)) : null;
    }

    private static string? Resolve(string root, string folder)
    {
        // The marker contains a version and GUID, never an arbitrary executable path.
        var separator = folder.IndexOf('-');
        if (!folder.StartsWith('v') || separator < 2 || !Version.TryParse(folder[1..separator], out var version) ||
            folder[..separator] != "v" + version || !Guid.TryParseExact(folder[(separator + 1)..], "N", out _)) return null;
        var directory = Path.GetFullPath(Path.Combine(root, "updates", folder));
        var path = Path.Combine(directory, Executable);
        return ReadVersion(path) == version && HasRuntime(directory) ? path : null;
    }

    private static bool HasRuntime(string directory) => File.Exists(Path.Combine(directory, "Legado.UI3.dll")) &&
        File.Exists(Path.Combine(directory, "Legado.UI3.pri")) && File.Exists(Path.Combine(directory, "App.xbf")) &&
        File.Exists(Path.Combine(directory, "Assets", "Legado.UI3.ico"));

    private static Version? ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == "Legado·UI3" ? new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) : null;
    }

    private static void Start(string executable, params string[] args)
    {
        var launch = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
        foreach (var arg in args) launch.ArgumentList.Add(arg);
        using var result = Process.Start(launch) ?? throw new IOException("未能启动新版程序。");
    }
}
