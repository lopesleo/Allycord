namespace Allycord.Bridge;

/// <summary>Append-only log file in the bridge data folder (bridge.log), rotated at 1 MB.</summary>
static class Log
{
    static readonly object Gate = new();
    static string? _path;

    public static bool EchoToConsole { get; set; }

    public static void Init(string dataDir)
    {
        _path = Path.Combine(dataDir, "bridge.log");
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > 1_000_000)
                File.Move(_path, _path + ".old", overwrite: true);
        }
        catch { }
    }

    public static void Info(string message) => Write("INF", message);
    public static void Error(string message) => Write("ERR", message);
    public static void Error(string message, Exception ex) => Write("ERR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        if (EchoToConsole) Console.Error.WriteLine(line);
        if (_path is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
    }
}
