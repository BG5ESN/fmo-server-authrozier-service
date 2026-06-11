using System.Formats.Cbor;

namespace Sas.certs;

public abstract class CertBase
{
    public abstract byte[] ToTbsCbor();

    public byte[] Fingerprint()
    {
        var tbs = ToTbsCbor();
        return System.Security.Cryptography.SHA256.HashData(tbs);
    }

    protected static void EncodeText(CborWriter w, string s) => w.WriteTextString(s);

    protected static void EncodeInt(CborWriter w, long v) => w.WriteInt64(v);

    protected static void EncodeBytes(CborWriter w, byte[] data) => w.WriteByteString(data);

    protected static void EncodeBool(CborWriter w, bool v) => w.WriteBoolean(v);

    protected static void EncodeTextArray(CborWriter w, string?[] items)
    {
        w.WriteStartArray(items.Length);
        foreach (var item in items)
            w.WriteTextString(item ?? "");
        w.WriteEndArray();
    }

    protected static void EncodeByteArray(CborWriter w, byte[][] items)
    {
        w.WriteStartArray(items.Length);
        foreach (var item in items)
            w.WriteByteString(item);
        w.WriteEndArray();
    }
}
