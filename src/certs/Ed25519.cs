namespace Sas.certs;

public static class Ed25519
{
    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        return Chaos.NaCl.Ed25519.Verify(signature, data, publicKey);
    }

    public static byte[] Sign(byte[] privateKey, byte[] data)
    {
        return Chaos.NaCl.Ed25519.Sign(data, privateKey);
    }
}
