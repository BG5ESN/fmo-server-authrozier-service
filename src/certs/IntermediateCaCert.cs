using System.Formats.Cbor;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sas.certs;

public sealed class IntermediateCaCert : CertBase
{
    public long Sn { get; init; }
    public long IssuerSn { get; init; }
    public string IssuerName { get; init; } = "";
    public byte[] IssuerPubKey { get; init; } = new byte[32];
    public string SubjectName { get; init; } = "";
    public string SubjectEmail { get; init; } = "";
    public byte[] SubjectPubKey { get; init; } = new byte[32];
    public bool IsCA => true;
    public uint PathLen { get; init; }
    public string KeyId { get; init; } = "";
    public string Crl { get; init; } = "";
    public string License { get; init; } = "";
    public long UidRangeStart { get; init; }
    public long UidRangeEnd { get; init; }
    public string[] IssuingCountries { get; init; } = [];
    public long Iat { get; init; }
    public long Exp { get; init; }
    public byte[] Signature { get; init; } = new byte[64];

    public static IntermediateCaCert FromJson(JsonElement json)
    {
        var ext = json.GetProperty("extensions");
        var countries = ext.GetProperty("issuingCountries")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        foreach (var c in countries)
        {
            if (!Regex.IsMatch(c, @"^[A-Z]{2}$"))
            {
                throw new InvalidOperationException(
                    $"Invalid issuingCountries entry: '{c}' (expected 2 uppercase letters)");
            }
        }

        var sorted = countries
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        return new IntermediateCaCert
        {
            Sn = json.GetProperty("sn").GetInt64(),
            IssuerSn = json.GetProperty("issuer").GetProperty("sn").GetInt64(),
            IssuerName = json.GetProperty("issuer").GetProperty("name").GetString() ?? "",
            IssuerPubKey = Base64Url.Decode(json.GetProperty("issuer").GetProperty("publicKey").GetString()!),
            SubjectName = json.GetProperty("subject").GetProperty("name").GetString() ?? "",
            SubjectEmail = json.GetProperty("subject").GetProperty("email").GetString() ?? "",
            SubjectPubKey = Base64Url.Decode(json.GetProperty("subject").GetProperty("publicKey").GetString()!),
            KeyId = ext.GetProperty("keyId").GetString() ?? "",
            Crl = ext.GetProperty("crl").GetString() ?? "",
            License = ext.GetProperty("license").GetString() ?? "",
            UidRangeStart = ext.GetProperty("uidRange").GetProperty("start").GetInt64(),
            UidRangeEnd = ext.GetProperty("uidRange").GetProperty("end").GetInt64(),
            IssuingCountries = sorted,
            Iat = json.GetProperty("iat").GetInt64(),
            Exp = json.GetProperty("exp").GetInt64(),
            Signature = Base64Url.Decode(json.GetProperty("signature").GetString()!)
        };
    }

    public override byte[] ToTbsCbor()
    {
        var w = new CborWriter(CborConformanceMode.Lax);

        w.WriteStartArray(20);
        EncodeText(w, "FMO");
        EncodeInt(w, 4);
        EncodeText(w, "intermediateCA");
        EncodeInt(w, Sn);
        EncodeInt(w, IssuerSn);
        EncodeText(w, IssuerName);
        EncodeBytes(w, IssuerPubKey);
        EncodeText(w, SubjectName);
        EncodeText(w, SubjectEmail);
        EncodeBytes(w, SubjectPubKey);
        EncodeBool(w, IsCA);
        EncodeInt(w, PathLen);
        EncodeText(w, KeyId);
        EncodeText(w, Crl);
        EncodeText(w, License);
        EncodeInt(w, UidRangeStart);
        EncodeInt(w, UidRangeEnd);
        EncodeTextArray(w, IssuingCountries);
        EncodeInt(w, Iat);
        EncodeInt(w, Exp);
        w.WriteEndArray();

        var buf = new byte[w.BytesWritten];
        w.Encode(buf);
        return buf;
    }

    public bool VerifyBy(RootCaCert root)
    {
        var tbs = ToTbsCbor();
        return Ed25519.Verify(root.SubjectPubKey, tbs, Signature);
    }

    public bool CanIssueFor(long uid) => uid >= UidRangeStart && uid <= UidRangeEnd;

    public bool IsExpired(long nowUtc) => nowUtc >= Exp;
}
