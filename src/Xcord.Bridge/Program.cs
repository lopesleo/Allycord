using System.Text.Json.Nodes;
using Xcord.Bridge;
using Xcord.Bridge.Discord;

// Launched by the widget through FullTrustProcessLauncher (packaged), or with --console for testing
// outside the package: prints state snapshots to stdout and reads "<method> [json args]" from stdin.
var consoleMode = args.Contains("--console");
var dataDir = ResolveDataDir();
Log.Init(dataDir);
Log.EchoToConsole = consoleMode;
Log.Info($"Bridge starting (console={consoleMode}, data={dataDir})");

using var lifetime = new CancellationTokenSource();
var session = new DiscordSession(new TokenStore(dataDir), new AppConfig(dataDir));
var discordLoop = session.RunAsync(lifetime.Token);

if (consoleMode)
{
    session.StateChanged += Console.WriteLine;
    _ = Task.Run(async () =>
    {
        while (Console.ReadLine() is { } line)
        {
            var parts = line.Trim().Split(' ', 2);
            if (parts[0].Length == 0) continue;
            try
            {
                var payload = parts.Length > 1 ? JsonNode.Parse(parts[1])!.AsObject() : new JsonObject();
                var result = await session.HandleAsync(parts[0], payload);
                Console.WriteLine($"<< {parts[0]}: {result?.ToJsonString() ?? "ok"}");
            }
            catch (Exception ex) { Console.WriteLine($"<< {parts[0]} ERROR: {ex.Message}"); }
        }
        lifetime.Cancel();
    });
}
else
{
    await new WidgetHost(session).RunAsync(lifetime);
}

try { await discordLoop; }
catch (OperationCanceledException) { }
Log.Info("Bridge exiting");

static string ResolveDataDir()
{
    try
    {
        // Packaged: the package's LocalState, shared with the widget.
        return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
    }
    catch
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xcord");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
