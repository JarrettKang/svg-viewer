using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace SvgViewer;

internal static class GhostscriptLocator
{
    private static readonly string[] Executables = ["gswin64c.exe", "gswin32c.exe", "gs.exe"];
    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SvgViewer", "ghostscript.json");

    public static string? Find()
    {
        string? forced = Environment.GetEnvironmentVariable("SVGVIEWER_GHOSTSCRIPT");
        if (!string.IsNullOrWhiteSpace(forced)) return File.Exists(forced) ? Path.GetFullPath(forced) : null;
        try
        {
            if (File.Exists(SettingsFile))
            {
                string? saved = JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsFile));
                if (File.Exists(saved)) return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (FindInBin(dir.Trim('"')) is { } fromPath) return fromPath;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\GPL Ghostscript");
                if (key == null) continue;
                foreach (string version in key.GetSubKeyNames().OrderDescending())
                {
                    using var item = key.OpenSubKey(version);
                    if (item?.GetValue("GS_DLL") is string dll && FindInBin(Path.GetDirectoryName(dll)!) is { } registered) return registered;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Apps"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)"));
            roots.Add(drive.RootDirectory.FullName);
        }
        foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            foreach (string gsRoot in new[] { Path.Combine(root, "gs"), Path.Combine(root, "Ghostscript") })
            foreach (string install in Directories(gsRoot).Prepend(gsRoot))
                if (FindInBin(Path.Combine(install, "bin")) is { } gs) return gs;
            foreach (string lyx in Directories(root).Where(d => Path.GetFileName(d).StartsWith("lyx", StringComparison.OrdinalIgnoreCase)))
                if (FindInBin(Path.Combine(lyx, "ghostscript", "bin")) is { } gs) return gs;
            foreach (string texRoot in new[] { Path.Combine(root, "texlive"), Path.Combine(root, "texlive", "texlive") })
            foreach (string tex in Directories(texRoot).Prepend(texRoot))
                if (FindInBin(Path.Combine(tex, "tlpkg", "tlgs", "bin")) is { } gs) return gs;
        }
        return null;
    }

    private static IEnumerable<string> Directories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root).OrderDescending().ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? FindInBin(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (string name in Executables)
        {
            try { string path = Path.Combine(directory, name); if (File.Exists(path)) return Path.GetFullPath(path); }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static async Task<string> CheckVersionAsync(string executable)
    {
        if (!Executables.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 gswin64c.exe 或 gswin32c.exe 命令行程序。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Ghostscript。");
        var stdout = EpsRenderer.ReadLogAsync(process.StandardOutput);
        var stderr = EpsRenderer.ReadLogAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw new IOException("Ghostscript 版本检测超时。"); }
        string version = (await stdout).Trim();
        await stderr;
        if (process.ExitCode != 0 || !Version.TryParse(version, out var number) || number < new Version(9, 50))
            throw new IOException("请使用 Ghostscript 9.50 或更新版本。");
        return version;
    }

    public static void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Path.GetFullPath(path)));
    }
}
