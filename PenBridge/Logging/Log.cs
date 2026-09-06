namespace PenBridge.Logging;

public enum LogLevel { Info, Warn, Error }

public interface ILog
{
    event Action<LogLevel, string>? Logged;
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>Minimal leveled logger: appends to %LOCALAPPDATA%\PenBridge\Logs\penbridge.log (one
/// backup kept via simple size-based rotation) and raises Logged so the UI can mirror it live.
/// Callers must keep sensitive values out of the messages they pass in.</summary>
public sealed class FileLog : ILog
{
    private const long MaxBytesBeforeRotation = 1_000_000;
    private readonly string _path;
    private readonly object _gate = new();

    public event Action<LogLevel, string>? Logged;

    public FileLog()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenBridge", "Logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "penbridge.log");
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    private void Write(LogLevel level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytesBeforeRotation)
                {
                    string backup = _path + ".old";
                    File.Delete(backup);
                    File.Move(_path, backup);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Best-effort file logging: a disk/permission hiccup here must never crash the app.
            }
        }
        Logged?.Invoke(level, message);
    }
}
