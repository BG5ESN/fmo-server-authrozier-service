using System.Text.Json;
using Sas.certs;
using Sas.Logging;
using Sas.Messages;
using Sas.Trust;

namespace Sas.Auth;

sealed class HttpAuthHandler
{
    private readonly Config _config;
    private readonly RootCaStore _rootStore;
    private readonly CertVerifier _verifier;
    private readonly CrlManager _crlManager;
    private readonly AclStore _aclStore;

    public HttpAuthHandler(Config config, RootCaStore rootStore,
        CertVerifier verifier, CrlManager crlManager, AclStore aclStore)
    {
        _config = config;
        _rootStore = rootStore;
        _verifier = verifier;
        _crlManager = crlManager;
        _aclStore = aclStore;
    }

    public AuthResult Process(string username, string password)
    {
        HttpPasswordPayload payload;
        try
        {
            var passwordJson = System.Text.Encoding.UTF8.GetString(
                Base64Url.Decode(password));
            payload = JsonSerializer.Deserialize<HttpPasswordPayload>(passwordJson)
                ?? throw new InvalidOperationException("null payload");
        }
        catch (Exception ex)
        {
            Logger.Warn($"HTTP auth: failed to parse password payload: {ex.Message}");
            throw new AuthDeniedException("invalid password format");
        }

        IntermediateCaCert intermediate;
        UserCert user;
        try
        {
            intermediate = IntermediateCaCert.FromJson(payload.CertPackage.IntermediateCert);
            user = UserCert.FromJson(payload.CertPackage.UserCert);
        }
        catch (Exception ex)
        {
            Logger.Warn($"HTTP auth: failed to parse cert package: {ex.Message}");
            throw new AuthDeniedException("invalid certificate format");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var root = _rootStore.FindIssuer(intermediate);
        if (root == null)
        {
            Logger.Warn($"HTTP auth: no trusted root found for intermediate sn={intermediate.Sn}");
            throw new AuthDeniedException("chain invalid: no trusted root");
        }

        var vr = _verifier.VerifyFullChain(root, intermediate, user, now);
        if (vr != VerifyResult.OK)
        {
            Logger.Warn($"HTTP auth: chain validation failed: {CertVerifier.GetMessage(vr)}");
            throw new AuthDeniedException("chain invalid");
        }

        if (!string.Equals(username, user.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"HTTP auth: username mismatch: request={username} cert={user.Callsign}");
            throw new AuthDeniedException("username mismatch");
        }

        if (_config.Trust.AllowIssuerSn.Length > 0 &&
            !_config.Trust.AllowIssuerSn.Contains(intermediate.Sn))
        {
            Logger.Warn($"HTTP auth: intermediate sn={intermediate.Sn} not in allow list");
            throw new AuthDeniedException("intermediate not trusted");
        }

        _crlManager.RegisterIntermediate(intermediate);

        var rootOutdated = _crlManager.IsRootCrlOutdated(root.Sn, now);
        if (rootOutdated)
            Logger.Warn($"HTTP auth: root CRL for sn={root.Sn} may be outdated");

        if (_crlManager.IsIntermediateRevoked(intermediate.Sn, intermediate.Fingerprint(), out var intReason))
        {
            Logger.Warn($"HTTP auth: intermediate revoked: {intReason}");
            throw new AuthDeniedException("intermediate revoked");
        }

        if (_crlManager.IsUserRevoked(user.Uid, user.Fingerprint(), out var userReason))
        {
            Logger.Warn($"HTTP auth: user revoked: {userReason}");
            throw new AuthDeniedException("user revoked");
        }

        var role = DetermineRole(user);
        if (!string.IsNullOrEmpty(payload.Role) &&
            !string.Equals(payload.Role, role, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"HTTP auth: role mismatch: claimed={payload.Role} actual={role}");
            throw new AuthDeniedException("role mismatch");
        }

        if (string.IsNullOrEmpty(payload.TargetCallsign) ||
            !string.Equals(payload.TargetCallsign, _config.Server.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"HTTP auth: targetCallsign mismatch: got={payload.TargetCallsign} expected={_config.Server.Callsign}");
            throw new AuthDeniedException("server identity mismatch");
        }

        if (payload.TargetUID != _config.Server.Uid)
        {
            Logger.Warn($"HTTP auth: targetUID mismatch: got={payload.TargetUID} expected={_config.Server.Uid}");
            throw new AuthDeniedException("server identity mismatch");
        }

        if (!string.Equals(payload.TargetUrl, _config.Mqtt.Host, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"HTTP auth: targetUrl mismatch: got={payload.TargetUrl} expected={_config.Mqtt.Host}");
            throw new AuthDeniedException("server identity mismatch");
        }

        if (payload.TargetPort != _config.Mqtt.Port)
        {
            Logger.Warn($"HTTP auth: targetPort mismatch: got={payload.TargetPort} expected={_config.Mqtt.Port}");
            throw new AuthDeniedException("server identity mismatch");
        }

        byte[] serverFpBytes;
        try
        {
            serverFpBytes = Base64Url.Decode(payload.ServerFingerprint);
        }
        catch
        {
            Logger.Warn("HTTP auth: invalid serverFingerprint base64url");
            throw new AuthDeniedException("invalid server fingerprint");
        }

        if (string.IsNullOrEmpty(_config.Server.CertFingerprint))
        {
            Logger.Warn("HTTP auth: server.certFingerprint not configured, cannot verify server identity");
            throw new AuthDeniedException("server cert fingerprint not configured");
        }

        byte[] expectedFp;
        try { expectedFp = Base64Url.Decode(_config.Server.CertFingerprint); }
        catch
        {
            Logger.Warn("HTTP auth: invalid server.certFingerprint base64url");
            throw new AuthDeniedException("server cert fingerprint not configured");
        }

        if (expectedFp.Length != 32 || !serverFpBytes.AsSpan().SequenceEqual(expectedFp))
        {
            Logger.Warn("HTTP auth: serverFingerprint mismatch with configured certFingerprint");
            throw new AuthDeniedException("server identity mismatch");
        }

        if (!HttpProofVerifier.IsTimestampValid(payload.Timestamp, now))
        {
            Logger.Warn($"HTTP auth: timestamp drifted too far: payload={payload.Timestamp} server={now}");
            throw new AuthDeniedException("timestamp expired");
        }

        var proofOk = HttpProofVerifier.Verify(
            _config.Server.Uid, _config.Server.Callsign, _config.Server.Uid,
            _config.Mqtt.Host, _config.Mqtt.Port, serverFpBytes,
            payload.Timestamp, user, payload.Role, payload.Proof.Signature);

        if (!proofOk)
        {
            Logger.Warn($"HTTP auth: proof signature verification failed for {user.Callsign}");
            throw new AuthDeniedException("proof invalid");
        }

        var isSuperuser = role == "super";
        var (allDefs, pubDefs, subDefs) = _aclStore.GetPermissions(role);

        var allTopics = _aclStore.ResolveForUid(allDefs, user.Uid);
        var pubTopics = _aclStore.ResolveForUid(pubDefs, user.Uid);
        var subTopics = _aclStore.ResolveForUid(subDefs, user.Uid);

        var exp = now + _config.Http.TtlSec;

        Logger.Info($"HTTP auth: allowed {user.Callsign} uid={user.Uid} role={role}");

        return new AuthResult
        {
            Callsign = user.Callsign,
            Uid = user.Uid,
            Role = role,
            IsSuperuser = isSuperuser,
            IssuedAt = now,
            ExpireAt = exp,
            AllTopics = allTopics,
            PubTopics = pubTopics,
            SubTopics = subTopics
        };
    }

    private string DetermineRole(UserCert user)
    {
        if (_config.Server.Admins.Length == 0)
            return user.Uid == _config.Server.Uid ? "super" : "user";

        var userFp = user.Fingerprint();
        foreach (var admin in _config.Server.Admins)
        {
            if (admin.Uid != user.Uid)
                continue;

            byte[] adminFp;
            try { adminFp = Base64Url.Decode(admin.CertFingerprint); }
            catch { continue; }

            if (adminFp.AsSpan().SequenceEqual(userFp))
                return string.IsNullOrEmpty(admin.Role) ? "super" : admin.Role;
        }

        return "user";
    }
}

public sealed class AuthDeniedException : Exception
{
    public AuthDeniedException(string message) : base(message) { }
}
