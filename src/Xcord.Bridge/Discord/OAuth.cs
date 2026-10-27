using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Xcord.Bridge.Discord;

sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(5);
}

/// <summary>OAuth2 PKCE for a public client: no client secret, the code comes from the RPC AUTHORIZE command.</summary>
static class OAuth
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static (string Verifier, string Challenge) CreatePkcePair()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static Task<TokenSet> ExchangeCodeAsync(string clientId, string code, string verifier) =>
        RequestTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
        });

    public static Task<TokenSet> RefreshAsync(string clientId, string refreshToken) =>
        RequestTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        });

    static async Task<TokenSet> RequestTokenAsync(Dictionary<string, string> form)
    {
        using var response = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed ({(int)response.StatusCode}): {body}");

        var json = JsonNode.Parse(body)!;
        var access = (string?)json["access_token"] ?? throw new InvalidOperationException("Token response without access_token");
        var expiresIn = (int?)json["expires_in"] ?? 604800;
        return new TokenSet(access, (string?)json["refresh_token"], DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Persists the token in the bridge data folder (per-user, inside the package's LocalState when packaged).</summary>
sealed class TokenStore(string dataDir)
{
    readonly string _path = Path.Combine(dataDir, "discord_token.json");

    public TokenSet? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = JsonNode.Parse(File.ReadAllText(_path))!;
            var access = (string?)json["access_token"];
            if (string.IsNullOrEmpty(access)) return null;
            return new TokenSet(access, (string?)json["refresh_token"],
                DateTimeOffset.FromUnixTimeSeconds((long?)json["expires_at"] ?? 0));
        }
        catch (Exception ex)
        {
            Log.Error("Token load", ex);
            return null;
        }
    }

    public void Save(TokenSet token)
    {
        var json = new JsonObject
        {
            ["access_token"] = token.AccessToken,
            ["refresh_token"] = token.RefreshToken,
            ["expires_at"] = token.ExpiresAt.ToUnixTimeSeconds(),
        };
        File.WriteAllText(_path, json.ToJsonString());
    }

    public void Clear()
    {
        try { File.Delete(_path); } catch { }
    }
}
