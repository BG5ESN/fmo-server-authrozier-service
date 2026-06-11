using System.Formats.Cbor;
using System.Text.Json;

namespace Sas.certs;

public sealed class UserCert : CertBase
{
    public long IssuerSn { get; init; }
    public string Callsign { get; init; } = "";
    public long Uid { get; init; }
    public byte[] PublicKey { get; init; } = new byte[32];
    public long Iat { get; init; }
    public long Exp { get; init; }
    public byte[] Signature { get; init; } = new byte[64];

    public static UserCert FromJson(JsonElement json)
    {
        return new UserCert
        {
            IssuerSn = json.GetProperty("issuerSn").GetInt64(),
            Callsign = (json.GetProperty("subject").GetProperty("callsign").GetString() ?? "").ToUpperInvariant(),
            Uid = json.GetProperty("subject").GetProperty("uid").GetInt64(),
            PublicKey = Base64Url.Decode(json.GetProperty("subject").GetProperty("publicKey").GetString()!),
            Iat = json.GetProperty("iat").GetInt64(),
            Exp = json.GetProperty("exp").GetInt64(),
            Signature = Base64Url.Decode(json.GetProperty("signature").GetString()!)
        };
    }

    public override byte[] ToTbsCbor()
    {
        var w = new CborWriter(CborConformanceMode.Lax);

        w.WriteStartArray(9);
        EncodeText(w, "FMO");
        EncodeInt(w, 4);
        EncodeText(w, "userCert");
        EncodeInt(w, IssuerSn);
        EncodeText(w, Callsign);
        EncodeInt(w, Uid);
        EncodeBytes(w, PublicKey);
        EncodeInt(w, Iat);
        EncodeInt(w, Exp);
        w.WriteEndArray();

        var buf = new byte[w.BytesWritten];
        w.Encode(buf);
        return buf;
    }

    public bool VerifyBy(IntermediateCaCert issuer)
    {
        var tbs = ToTbsCbor();
        return Ed25519.Verify(issuer.SubjectPubKey, tbs, Signature);
    }

    public bool IsExpired(long nowUtc) => nowUtc >= Exp;
}
