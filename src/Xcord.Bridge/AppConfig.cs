using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Xcord.Bridge;

/// <summary>
/// User settings (settings.json in the data folder). Each user brings their own Discord application:
/// RPC voice scopes are private beta and only the app's owner/testers can authorize, so a shared
/// Client ID can't work for the public.
/// </summary>
sealed partial class AppConfig(string dataDir)
{
    readonly string _path = Path.Combine(dataDir, "settings.json");
    string? _clientId;
    bool _loaded;

    public string? ClientId
    {
        get
        {
            if (!_loaded) Load();
            return _clientId;
        }
    }

    public static bool IsValidClientId(string? value) => value is not null && SnowflakeRegex().IsMatch(value);

    public void SetClientId(string clientId)
    {
        if (!IsValidClientId(clientId)) throw new ArgumentException("Client ID inválido");
        _clientId = clientId;
        _loaded = true;
        File.WriteAllText(_path, new JsonObject { ["client_id"] = clientId }.ToJsonString());
    }

    void Load()
    {
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            var id = (string?)JsonNode.Parse(File.ReadAllText(_path))?["client_id"];
            _clientId = IsValidClientId(id) ? id : null;
        }
        catch (Exception ex)
        {
            Log.Error("Settings load", ex);
        }
    }

    [GeneratedRegex(@"^\d{17,20}$")]
    private static partial Regex SnowflakeRegex();
}
