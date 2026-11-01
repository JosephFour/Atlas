using System.Text;

namespace Atlas.Core;

/// <summary>Tiny thread-safe file logger with size-based rotation.</summary>
public static class Log
{
    static readonly object Gate = new();
    const long MaxBytes = 5 * 1024 * 1024;

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas", "Logs");
    public static string FilePath => Path.Combine(Dir, "atlas.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Dir, "atlas.old.log"), overwrite: true);
                // Shared append: the self-test and a running Atlas may log at the same time.
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
