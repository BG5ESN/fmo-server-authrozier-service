using System.Formats.Cbor;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sas.certs;

public sealed class RootCaCert : CertBase
{
    public long Sn { get; init; }
    public string IssuerName { get; init; } = "";
    public string IssuerEmail { get; init; } = "";
    public string SubjectName { get; init; } = "";
    public byte[] SubjectPubKey { get; init; } = new byte[32];
    public bool IsCA => true;
    public uint PathLen { get; init; } = 1;
    public string Crl { get; init; } = "";
    public string License { get; init; } = "";
    public string KeyId { get; init; } = "";
    public long Iat { get; init; }
    public long Exp { get; init; }
    public byte[] Signature { get; init; } = new byte[64];

    public static RootCaCert FromJson(JsonElement json)
    {
        return new RootCaCert
        {
            Sn = json.GetProperty("sn").GetInt64(),
            IssuerName = json.GetProperty("issuer").GetProperty("name").GetString() ?? "",
            IssuerEmail = json.GetProperty("issuer").GetProperty("email").GetString() ?? "",
            SubjectName = json.GetProperty("subject").GetProperty("name").GetString() ?? "",
            SubjectPubKey = Base64Url.Decode(json.GetProperty("subject").GetProperty("publicKey").GetString()!),
            Crl = json.GetProperty("extensions").GetProperty("crl").GetString() ?? "",
            License = json.GetProperty("extensions").GetProperty("license").GetString() ?? "",
            KeyId = json.GetProperty("extensions").GetProperty("keyId").GetString() ?? "",
            Iat = json.GetProperty("iat").GetInt64(),
            Exp = json.GetProperty("exp").GetInt64(),
            Signature = Base64Url.Decode(json.GetProperty("signature").GetString()!)
        };
    }

    public override byte[] ToTbsCbor()
    {
        var w = new CborWriter(CborConformanceMode.Lax);

        w.WriteStartArray(15);
        EncodeText(w, "FMO");
        EncodeInt(w, 4);
        EncodeText(w, "rootCA");
        EncodeInt(w, Sn);
        EncodeText(w, IssuerName);
        EncodeText(w, IssuerEmail);
        EncodeText(w, SubjectName);
        EncodeBytes(w, SubjectPubKey);
        EncodeBool(w, IsCA);
        EncodeInt(w, PathLen);
        EncodeText(w, Crl);
        EncodeText(w, License);
        EncodeText(w, KeyId);
        EncodeInt(w, Iat);
        EncodeInt(w, Exp);
        w.WriteEndArray();

        var buf = new byte[w.BytesWritten];
        w.Encode(buf);
        return buf;
    }

    public bool VerifySelfSignature()
    {
        var tbs = ToTbsCbor();
        return Ed25519.Verify(SubjectPubKey, tbs, Signature);
    }

    public bool IsExpired(long nowUtc) => nowUtc >= Exp;
}
