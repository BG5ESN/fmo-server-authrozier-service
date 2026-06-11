namespace Sas.Auth;

public sealed class AuthResult
{
    public string Callsign { get; init; } = "";
    public long Uid { get; init; }
    public string Role { get; init; } = "user";
    public bool IsSuperuser { get; init; }
    public long IssuedAt { get; init; }
    public long ExpireAt { get; init; }
    public string[] AllTopics { get; init; } = [];
    public string[] PubTopics { get; init; } = [];
    public string[] SubTopics { get; init; } = [];
}
