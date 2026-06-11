using System.Formats.Cbor;
using Sas.certs;

namespace Sas.Auth;

public static class HttpProofVerifier
{
    private const int MaxTimestampDriftSec = 120;

    public static bool Verify(
        long serverUid,
        string targetCallsign,
        long targetUID,
        string targetUrl,
        int targetPort,
        byte[] serverFingerprintBytes,
        long timestamp,
        UserCert user,
        string role,
        string proofSigBase64Url)
    {
        if (user.PublicKey == null || user.PublicKey.Length != 32)
            return false;
        if (serverFingerprintBytes == null || serverFingerprintBytes.Length != 32)
            return false;

        var claims = new Dictionary<string, string?>
        {
            ["serverUid"] = serverUid.ToString(),
            ["targetCallsign"] = targetCallsign,
            ["targetUID"] = targetUID.ToString(),
            ["targetUrl"] = targetUrl,
            ["targetPort"] = targetPort.ToString()
        };

        try
        {
            var tbs = BuildTbs(serverUid, targetCallsign, targetUID,
                               targetUrl, targetPort, serverFingerprintBytes,
                               timestamp, user, role);
            var sig = Base64Url.Decode(proofSigBase64Url);
            if (sig == null || sig.Length != 64)
                return false;

            return Ed25519.Verify(user.PublicKey, tbs, sig);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsTimestampValid(long timestamp, long nowUtc)
    {
        return Math.Abs(nowUtc - timestamp) <= MaxTimestampDriftSec;
    }

    private static byte[] BuildTbs(
        long serverUid, string targetCallsign, long targetUID,
        string targetUrl, int targetPort,
        byte[] serverFingerprintBytes,
        long timestamp, UserCert user, string role)
    {
        var csUpper = targetCallsign.ToUpperInvariant();
        var userFp = user.Fingerprint();

        var w = new CborWriter(CborConformanceMode.Lax);
        w.WriteStartArray(12);
        w.WriteTextString("FMO");
        w.WriteInt64(4);
        w.WriteTextString("serverAuthorizerReqHttp");
        w.WriteInt64(serverUid);
        w.WriteTextString(csUpper);
        w.WriteInt64(targetUID);
        w.WriteTextString(role);
        w.WriteTextString(targetUrl);
        w.WriteInt64(targetPort);
        w.WriteByteString(serverFingerprintBytes);
        w.WriteInt64(timestamp);
        w.WriteByteString(userFp);
        w.WriteEndArray();

        var buf = new byte[w.BytesWritten];
        w.Encode(buf);
        return buf;
    }
}
