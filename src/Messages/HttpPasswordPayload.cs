using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sas.Messages;

public sealed class HttpPasswordPayload
{
    [JsonPropertyName("certPackage")]
    public CertPackage CertPackage { get; init; } = new();

    [JsonPropertyName("targetCallsign")]
    public string TargetCallsign { get; init; } = "";

    [JsonPropertyName("targetUID")]
    public long TargetUID { get; init; }

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; init; } = "";

    [JsonPropertyName("targetPort")]
    public int TargetPort { get; init; }

    [JsonPropertyName("serverFingerprint")]
    public string ServerFingerprint { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("proof")]
    public HttpProof Proof { get; init; } = new();
}

public sealed class HttpProof
{
    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";
}

public sealed class CertPackage
{
    [JsonPropertyName("intermediateCert")]
    public JsonElement IntermediateCert { get; init; }

    [JsonPropertyName("userCert")]
    public JsonElement UserCert { get; init; }
}
