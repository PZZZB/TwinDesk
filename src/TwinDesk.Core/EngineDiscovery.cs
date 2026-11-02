namespace TwinDesk.Core;

public static class EngineDiscovery
{
    public const string SupportedVersion = "1.27.0";
    public const string WindowsPackageSha256 = "8d19004cc3625623489d96580c1b58fd13ee78185689907fd4478595f93fab9a";
    public const string WindowsPackageUrl = "https://github.com/deskflow/deskflow/releases/download/v1.27.0/deskflow-1.27.0-win-x64-portable.zip";

    public static string? Find()
    {
        var explicitPath = Environment.GetEnvironmentVariable("TWINDESK_ENGINE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && IsUsable(explicitPath)) return Path.GetFullPath(explicitPath);

        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        roots.Add(Path.GetFullPath(AppContext.BaseDirectory));
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(start);
            for (var i = 0; current is not null && i < 8; i++, current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "TwinDesk.sln")))
                    roots.Add(current.FullName);
            }
        }
        var binary = OperatingSystem.IsWindows() ? "deskflow-core.exe" : "deskflow-core";
        foreach (var root in roots)
        {
            foreach (var candidate in new[] { Path.Combine(root, binary), Path.Combine(root, "engine", binary) })
                if (IsUsable(candidate)) return candidate;
            var engineDirectory = Path.Combine(root, "engine");
            if (!Directory.Exists(engineDirectory)) continue;
            foreach (var child in Directory.EnumerateDirectories(engineDirectory).Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(child, binary);
                if (IsUsable(candidate)) return candidate;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            const string macPath = "/Applications/Deskflow.app/Contents/MacOS/deskflow-core";
            if (File.Exists(macPath)) return macPath;
        }
        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var candidate = Path.Combine(path, binary);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static bool IsWindowsPortable(string path) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "settings", "Deskflow.conf"));

    private static bool IsUsable(string path) => File.Exists(path) && (!OperatingSystem.IsWindows() || IsWindowsPortable(path));
}
