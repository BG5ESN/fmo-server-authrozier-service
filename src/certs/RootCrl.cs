using System.Formats.Cbor;
using System.Text.Json;

namespace Sas.certs;

public sealed class RootCrl
{
    public long IssuerSn { get; init; }
    public uint CrlNumber { get; init; }
    public long ThisUpdate { get; init; }
    public long NextUpdate { get; init; }
    public RootCrlEntry[] Entries { get; init; } = [];
    public byte[] Signature { get; init; } = new byte[64];

    public static RootCrl FromJson(JsonElement json)
    {
        var entries = json.GetProperty("entries")
            .EnumerateArray()
            .Select(e => new RootCrlEntry
            {
                Sn = e.GetProperty("sn").GetInt64(),
                CertFingerprint = Base64Url.Decode(e.GetProperty("certFingerprint").GetString()!),
                RevokedAt = e.GetProperty("revokedAt").GetInt64(),
                Reason = e.GetProperty("reason").GetInt32()
            })
            .OrderBy(e => e.Sn)
            .ThenBy(e => Convert.ToHexString(e.CertFingerprint))
            .ToArray();

        return new RootCrl
        {
            IssuerSn = json.GetProperty("issuerSn").GetInt64(),
            CrlNumber = (uint)json.GetProperty("crlNumber").GetInt64(),
            ThisUpdate = json.GetProperty("thisUpdate").GetInt64(),
            NextUpdate = json.GetProperty("nextUpdate").GetInt64(),
            Entries = entries,
            Signature = Base64Url.Decode(json.GetProperty("signature").GetString()!)
        };
    }

    public byte[] ToTbsCbor()
    {
        var w = new CborWriter(CborConformanceMode.Lax);

        w.WriteStartArray(8);
        w.WriteTextString("FMO");
        w.WriteInt64(4);
        w.WriteTextString("rootCRL");
        w.WriteInt64(IssuerSn);
        w.WriteInt64(CrlNumber);
        w.WriteInt64(ThisUpdate);
        w.WriteInt64(NextUpdate);

        w.WriteStartArray(Entries.Length);
        foreach (var e in Entries)
        {
            w.WriteStartArray(4);
            w.WriteInt64(e.Sn);
            w.WriteByteString(e.CertFingerprint);
            w.WriteInt64(e.RevokedAt);
            w.WriteInt32(e.Reason);
            w.WriteEndArray();
        }
        w.WriteEndArray();
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

    public bool IsRevoked(long sn, byte[] fingerprint)
    {
        return Entries.Any(e =>
            e.Sn == sn &&
            e.CertFingerprint.AsSpan().SequenceEqual(fingerprint));
    }

    public bool IsOutdated(long nowUtc) => nowUtc > NextUpdate;
}

public sealed class RootCrlEntry
{
    public long Sn { get; init; }
    public byte[] CertFingerprint { get; init; } = [];
    public long RevokedAt { get; init; }
    public int Reason { get; init; }
}
