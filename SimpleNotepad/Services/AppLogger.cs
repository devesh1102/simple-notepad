using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace SimpleNotepad.Services;

/// <summary>
/// Minimal, dependency-free, thread-safe file logger. Writes daily-rolling log files under
/// %LOCALAPPDATA%\SimpleNotepad\logs and keeps a bounded number of days so logs never grow
/// without limit. Logging happens on a background thread so the UI never blocks on disk I/O,
/// preserving the app's fast, lightweight feel.
/// </summary>
public static class AppLogger
{
    private const string AppFolderName = "SimpleNotepad";
    private const string LogsFolderName = "logs";
    private const int RetentionDays = 7;

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly string LogDirectory;
    private static readonly Lazy<Thread> Worker = new(StartWorker);
    private static volatile bool _enabled = true;

    static AppLogger()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LogDirectory = Path.Combine(localAppData, AppFolderName, LogsFolderName);
    }

    /// <summary>Folder that contains the rolling log files.</summary>
    public static string LogFolder => LogDirectory;

    /// <summary>Enables or disables logging at runtime (e.g. from a user setting).</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        if (!_enabled)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try
        {
            _ = Worker.Value; // ensure the worker thread is running
            Queue.Add(line);
        }
        catch (InvalidOperationException)
        {
            // Queue completed during shutdown; drop the message.
        }
    }

    /// <summary>Flushes pending entries and stops the worker. Call on app exit.</summary>
    public static void Shutdown()
    {
        try
        {
            Queue.CompleteAdding();
            if (Worker.IsValueCreated)
            {
                Worker.Value.Join(TimeSpan.FromSeconds(2));
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Thread StartWorker()
    {
        var thread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "AppLogger",
        };
        thread.Start();
        return thread;
    }

    private static void ProcessQueue()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CleanupOldLogs();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
