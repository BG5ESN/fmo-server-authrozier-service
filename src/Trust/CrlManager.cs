using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Sas.certs;
using Sas.Logging;

namespace Sas.Trust;

public sealed class CrlManager : IDisposable
{
    private readonly string _crlDir;
    private readonly int _refreshSec;
    private readonly HttpClient _http;

    private RootCaCert[] _roots = [];
    private readonly ConcurrentDictionary<long, RootCrl> _rootCrls = new();
    private readonly ConcurrentDictionary<long, IntermediateCrl> _intermediateCrls = new();
    private readonly ConcurrentDictionary<long, IntermediateCaCert> _knownIntermediates = new();
    private readonly ConcurrentDictionary<long, Task> _pendingFetches = new();

    private Timer? _timer;
    private int _refreshing;

    public CrlManager(string rootsDir, int refreshSec = 14400)
    {
        _refreshSec = refreshSec;
        _crlDir = Path.Combine(Path.GetDirectoryName(rootsDir)!, "crl");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Initialize(RootCaCert[] roots)
    {
        _roots = roots;
        LoadCachedCrls();

        _ = RefreshAllAsync().ContinueWith(t =>
            Logger.Warn($"CRL initial refresh error: {t.Exception?.InnerException?.Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

        _timer = new Timer(_ => _ = OnTimerTickAsync(), null,
            TimeSpan.FromSeconds(_refreshSec),
            TimeSpan.FromSeconds(_refreshSec));
        Logger.Info($"CRL manager started: {_roots.Length} root(s), refresh every {_refreshSec}s");
    }

    public void RegisterIntermediate(IntermediateCaCert intermediate)
    {
        if (string.IsNullOrWhiteSpace(intermediate.Crl))
            return;

        _knownIntermediates.TryAdd(intermediate.Sn, intermediate);

        if (_intermediateCrls.ContainsKey(intermediate.Sn))
            return;

        var cachePath = GetIntermediateCachePath(intermediate.Sn);
        if (File.Exists(cachePath))
        {
            try
            {
                var cachedJson = File.ReadAllText(cachePath);
                var doc = JsonDocument.Parse(cachedJson).RootElement;
                if (!doc.TryGetProperty("issuerSn", out _) || !doc.TryGetProperty("entries", out _))
                    throw new InvalidDataException("incomplete CRL cache");
                var crl = IntermediateCrl.FromJson(doc);
                if (crl.VerifyBy(intermediate) && crl.IssuerSn == intermediate.Sn)
                {
                    _intermediateCrls.TryAdd(intermediate.Sn, crl);
                    Logger.Info($"Loaded cached intermediate CRL: issuerSn={intermediate.Sn} crlNumber={crl.CrlNumber}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load cached intermediate CRL for sn={intermediate.Sn}: {ex.Message}");
            }
        }

        _pendingFetches.GetOrAdd(intermediate.Sn, sn =>
        {
            var fetchTask = RefreshIntermediateCrlAsync(intermediate);
            fetchTask.ContinueWith(t =>
            {
                _pendingFetches.TryRemove(sn, out _);
                if (t.IsFaulted)
                    Logger.Warn($"Background CRL fetch failed for sn={sn}: {t.Exception?.InnerException?.Message}");
            });
            return fetchTask;
        });
    }

    public bool IsIntermediateRevoked(long sn, byte[] fingerprint, out string? reason)
    {
        reason = null;

        foreach (var crl in _rootCrls.Values)
        {
            if (crl.IsRevoked(sn, fingerprint))
            {
                reason = $"intermediate CA sn={sn} revoked by root CRL";
                return true;
            }
        }

        return false;
    }

    public bool IsUserRevoked(long uid, byte[] fingerprint, out string? reason)
    {
        reason = null;

        foreach (var crl in _intermediateCrls.Values)
        {
            if (crl.IsRevoked(uid, fingerprint))
            {
                reason = $"user cert uid={uid} revoked by intermediate CRL";
                return true;
            }
        }

        return false;
    }

    public bool IsRootCrlOutdated(long rootSn, long nowUtc)
    {
        if (_rootCrls.TryGetValue(rootSn, out var crl))
            return crl.IsOutdated(nowUtc);
        return false;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http?.Dispose();
    }

    private async Task OnTimerTickAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;
        try
        {
            Logger.Debug("CRL refresh cycle started");
            await RefreshAllAsync();
            Logger.Debug("CRL refresh cycle completed");
        }
        catch (Exception ex)
        {
            Logger.Warn($"CRL refresh cycle error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task RefreshAllAsync()
    {
        var rootTasks = _roots.Select(RefreshRootCrlAsync);
        var intTasks = _knownIntermediates.Values.Select(RefreshIntermediateCrlAsync);
        await Task.WhenAll(rootTasks.Concat(intTasks));
    }

    private async Task RefreshRootCrlAsync(RootCaCert root)
    {
        if (string.IsNullOrWhiteSpace(root.Crl))
            return;

        try
        {
            var json = await _http.GetStringAsync(root.Crl);
            var doc = JsonDocument.Parse(json).RootElement;

            if (!doc.TryGetProperty("issuerSn", out _) || !doc.TryGetProperty("entries", out _))
            {
                Logger.Info($"Root CRL not published yet: {root.Crl}");
                return;
            }

            var crl = RootCrl.FromJson(doc);

            if (!crl.VerifyBy(root))
            {
                Logger.Error($"Root CRL signature invalid: {root.Crl}");
                return;
            }

            if (crl.IssuerSn != root.Sn)
            {
                Logger.Error($"Root CRL issuerSn mismatch: crl.issuerSn={crl.IssuerSn} root.sn={root.Sn}");
                return;
            }

            var updated = false;
            _rootCrls.AddOrUpdate(root.Sn,
                _ => { updated = true; return crl; },
                (_, existing) =>
                {
                    if (crl.CrlNumber > existing.CrlNumber) { updated = true; return crl; }
                    return existing;
                });

            if (updated)
            {
                SaveCachedRootCrl(root.Sn, json);
                Logger.Info($"Root CRL updated: sn={root.Sn} crlNumber={crl.CrlNumber}");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Logger.Info($"No root CRL available for {root.Crl} (404 Not Found)");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to refresh root CRL ({root.Crl}): {ex.Message}");
        }
    }

    private async Task RefreshIntermediateCrlAsync(IntermediateCaCert issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer.Crl))
            return;

        try
        {
            var json = await _http.GetStringAsync(issuer.Crl);
            var doc = JsonDocument.Parse(json).RootElement;

            if (!doc.TryGetProperty("issuerSn", out _) || !doc.TryGetProperty("entries", out _))
            {
                Logger.Info($"Intermediate CRL not published yet: {issuer.Crl}");
                return;
            }

            var crl = IntermediateCrl.FromJson(doc);

            if (!crl.VerifyBy(issuer))
            {
                Logger.Error($"Intermediate CRL signature invalid: {issuer.Crl}");
                return;
            }

            if (crl.IssuerSn != issuer.Sn)
            {
                Logger.Error($"Intermediate CRL issuerSn mismatch: crl.issuerSn={crl.IssuerSn} issuer.sn={issuer.Sn}");
                return;
            }

            var updated = false;
            _intermediateCrls.AddOrUpdate(issuer.Sn,
                _ => { updated = true; return crl; },
                (_, existing) =>
                {
                    if (crl.CrlNumber > existing.CrlNumber) { updated = true; return crl; }
                    return existing;
                });

            if (updated)
            {
                SaveCachedIntermediateCrl(issuer.Sn, json);
                Logger.Info($"Intermediate CRL loaded: issuerSn={issuer.Sn} crlNumber={crl.CrlNumber}");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Logger.Info($"No intermediate CRL available for {issuer.Crl} (404 Not Found)");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to refresh intermediate CRL ({issuer.Crl}): {ex.Message}");
        }
    }

    private void LoadCachedCrls()
    {
        if (!Directory.Exists(_crlDir))
            return;

        foreach (var root in _roots)
        {
            var rootCachePath = GetRootCachePath(root.Sn);
            if (!File.Exists(rootCachePath))
                continue;
            try
            {
                var json = File.ReadAllText(rootCachePath);
                var doc = JsonDocument.Parse(json).RootElement;
                if (!doc.TryGetProperty("issuerSn", out _) || !doc.TryGetProperty("entries", out _))
                    continue;
                var crl = RootCrl.FromJson(doc);
                if (crl.VerifyBy(root) && crl.IssuerSn == root.Sn)
                {
                    _rootCrls.TryAdd(root.Sn, crl);
                    Logger.Info($"Loaded cached root CRL: sn={root.Sn} crlNumber={crl.CrlNumber}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load cached root CRL for sn={root.Sn}: {ex.Message}");
            }
        }

        // Intermediate CRLs are loaded on-demand in RegisterIntermediate
        // because _knownIntermediates is empty at startup.
    }

    private string GetRootCachePath(long sn) =>
        Path.Combine(_crlDir, sn.ToString(), "root.json");

    private string GetIntermediateCachePath(long sn) =>
        Path.Combine(_crlDir, sn.ToString(), "intermediate.json");

    private void SaveCachedRootCrl(long sn, string rawJson)
    {
        try
        {
            var dir = Path.Combine(_crlDir, sn.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllText(GetRootCachePath(sn), rawJson);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to cache root CRL for sn={sn}: {ex.Message}");
        }
    }

    private void SaveCachedIntermediateCrl(long sn, string rawJson)
    {
        try
        {
            var dir = Path.Combine(_crlDir, sn.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllText(GetIntermediateCachePath(sn), rawJson);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to cache intermediate CRL for sn={sn}: {ex.Message}");
        }
    }
}
