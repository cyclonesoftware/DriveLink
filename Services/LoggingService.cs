using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DriveLink.Models;
using Microsoft.Win32;

namespace DriveLink.Services;

public static class LoggingService
{
    private const long MaxLogBytes = 1_048_576;
    public static int RetentionDays { get; set; } = 7;
    private static readonly object Lock = new();
    private static bool _cleanedThisSession;
    private static readonly Regex SecretPattern = new(
        @"(?i)(password|passphrase|privatekeypassphraseencrypted|passwordencrypted)\s*[:=]\s*[^;\r\n]+",
        RegexOptions.Compiled);

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Branding.DataFolderName,
        "Logs");

    private static string CurrentLogPath => Path.Combine(
        LogDirectory,
        $"drivelink-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string eventName, string message, ConnectionProfile? profile = null) =>
        Write("INFO", eventName, message, profile);

    public static void Warn(string eventName, string message, ConnectionProfile? profile = null) =>
        Write("WARN", eventName, message, profile);

    public static void Error(string eventName, string message, Exception? exception = null, ConnectionProfile? profile = null)
    {
        var detail = exception is null
            ? message
            : $"{message}; {exception.GetType().Name}: {exception.Message}";
        Write("ERROR", eventName, detail, profile);
    }

    public static string BuildSupportDetails(ConfigMergeService config, ConnectionProfile? selectedProfile)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName();
        var sb = new StringBuilder();

        sb.AppendLine($"{config.AppName} support details");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("Environment");
        sb.AppendLine($"- App version: {assembly.Version?.ToString(3) ?? "unknown"}");
        sb.AppendLine($"- Windows: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"- 64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"- Process architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"- WinFsp: {DetectWinFsp()}");
        sb.AppendLine($"- Log folder: {LogDirectory}");
        sb.AppendLine();
        sb.AppendLine("Configuration");
        sb.AppendLine($"- Connections loaded: {config.MergedConnections.Count}");
        sb.AppendLine($"- Remote config URL configured: {config.HasRemoteConfigUrl}");
        sb.AppendLine($"- Hide dot-files default: {config.HideDotFiles}");
        sb.AppendLine($"- Log retention days: {config.LogRetentionDays}");
        sb.AppendLine($"- User drive-letter overrides allowed: {config.SystemConfig.AllowUserDriveLetterOverride}");

        if (selectedProfile != null)
        {
            sb.AppendLine();
            sb.AppendLine("Selected connection");
            sb.AppendLine($"- Name: {selectedProfile.DisplayName}");
            sb.AppendLine($"- Id: {selectedProfile.Id}");
            sb.AppendLine($"- Host: {selectedProfile.Host}");
            sb.AppendLine($"- Port: {selectedProfile.Port}");
            sb.AppendLine($"- Username: {selectedProfile.Username}");
            sb.AppendLine($"- Auth mode: {selectedProfile.AuthMode}");
            sb.AppendLine($"- Secret storage: {selectedProfile.SecretStorageMode}");
            sb.AppendLine($"- Remote path: {selectedProfile.RemotePath}");
            sb.AppendLine($"- Preferred drive: {(selectedProfile.PreferredDriveLetter.HasValue ? $"{selectedProfile.PreferredDriveLetter.Value}:" : "auto")}");
            sb.AppendLine($"- Auto-connect: {selectedProfile.AutoConnect}");
            sb.AppendLine($"- System managed: {selectedProfile.IsSystem}");
            sb.AppendLine($"- Host key trusted: {!string.IsNullOrWhiteSpace(selectedProfile.HostKeyFingerprint)}");
            sb.AppendLine($"- Read-only mount: {selectedProfile.ReadOnlyMount}");
            sb.AppendLine($"- Cache seconds: {selectedProfile.CacheDurationSeconds}");
            sb.AppendLine($"- Connect timeout seconds: {selectedProfile.ConnectionTimeoutSeconds}");
            sb.AppendLine($"- Operation timeout seconds: {selectedProfile.OperationTimeoutSeconds}");
            sb.AppendLine($"- Dot-file override: {selectedProfile.HideDotFilesOverride?.ToString() ?? "inherit"}");
            sb.AppendLine($"- Private key configured: {!string.IsNullOrWhiteSpace(selectedProfile.PrivateKeyPath)}");
        }

        sb.AppendLine();
        sb.AppendLine("Recent logs");
        foreach (var line in RecentLines(80))
            sb.AppendLine(line);

        sb.AppendLine();
        sb.AppendLine("Privacy note: support details include hostnames, usernames, connection names, and recent redacted log messages. Passwords, passphrases, encrypted secret blobs, and private key contents are not included.");

        return Redact(sb.ToString());
    }

    public static IReadOnlyList<string> RecentLines(int maxLines)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var takeCount = Math.Max(0, maxLines);
            var lines = Directory.GetFiles(LogDirectory, "*.log")
                .OrderBy(File.GetLastWriteTimeUtc)
                .TakeLast(3)
                .SelectMany(path =>
                {
                    var all = File.ReadAllLines(path);
                    var start = Math.Max(0, all.Length - takeCount);
                    return all[start..];
                })
                .TakeLast(takeCount)
                .Select(Redact)
                .ToList();

            return lines.Count == 0 ? ["No recent log entries."] : lines;
        }
        catch
        {
            return ["Recent logs could not be read."];
        }
    }

    private static void Write(string level, string eventName, string message, ConnectionProfile? profile)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (!_cleanedThisSession)
                {
                    CleanupOldLogs();
                    _cleanedThisSession = true;
                }

                RotateIfNeeded();

                var profileText = profile is null ? string.Empty : " " + ProfileContext(profile);
                var line = $"{DateTimeOffset.Now:O} [{level}] {Safe(eventName)} {Redact(Safe(message))}{profileText}";
                File.AppendAllText(CurrentLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(CurrentLogPath)) return;
        var info = new FileInfo(CurrentLogPath);
        if (info.Length < MaxLogBytes) return;

        var archivePath = Path.Combine(LogDirectory, $"drivelink-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.Move(CurrentLogPath, archivePath, overwrite: true);
    }

    private static void CleanupOldLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var path in Directory.GetFiles(LogDirectory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
            catch { }
        }
    }

    private static string ProfileContext(ConnectionProfile profile) =>
        $"connection=\"{Safe(profile.DisplayName)}\" id=\"{Safe(profile.Id)}\" host=\"{Safe(profile.Host)}\" port={profile.Port} auth={profile.AuthMode} system={profile.IsSystem}";

    private static string Safe(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();

    private static string Redact(string value) =>
        SecretPattern.Replace(value, "$1=[redacted]");

    private static string DetectWinFsp()
    {
        foreach (var path in new[]
        {
            @"SOFTWARE\WinFsp",
            @"SOFTWARE\WOW6432Node\WinFsp",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WinFsp",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\WinFsp"
        })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;
                var version = key.GetValue("Version") as string
                           ?? key.GetValue("DisplayVersion") as string
                           ?? key.GetValue("ProductVersion") as string;
                return string.IsNullOrWhiteSpace(version) ? "installed" : $"installed ({version})";
            }
            catch { }
        }

        return "not detected";
    }
}
