using System.Text.Json;
using Sas.certs;
using Sas.Logging;

namespace Sas.Trust;

public sealed class RootCaStore
{
    private readonly List<RootCaCert> _roots = new();

    public IReadOnlyList<RootCaCert> AllRoots => _roots;

    public RootCaStore(string rootsDir)
    {
        TrySeedBuiltinRoots(rootsDir);
        LoadRoots(rootsDir);
    }

    private static void TrySeedBuiltinRoots(string rootsDir)
    {
        var builtinDir = FindBuiltinDir();
        if (builtinDir == null)
            return;

        if (!Directory.Exists(builtinDir))
            return;

        Directory.CreateDirectory(rootsDir);

        var anyCopied = false;
        foreach (var file in Directory.GetFiles(builtinDir, "*.json"))
        {
            var dest = Path.Combine(rootsDir, Path.GetFileName(file));
            if (!File.Exists(dest))
            {
                File.Copy(file, dest);
                Logger.Info($"Seeded Root CA: {Path.GetFileName(dest)}");
                anyCopied = true;
            }
        }

        if (anyCopied)
            Logger.Info("Built-in Root CA(s) copied to roots directory.");
    }

    private static string? FindBuiltinDir()
    {
        var candidates = new List<string>();

        // Docker image location
        candidates.Add("/app/builtin/roots");

        // Extracted alongside executable (single-file publish)
        var baseDir = AppContext.BaseDirectory;
        if (baseDir != null)
        {
            candidates.Add(Path.Combine(baseDir, "builtin", "roots"));
        }

        // Working directory (dev mode)
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "builtin", "roots"));

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
                return dir;
        }

        return null;
    }

    private void LoadRoots(string rootsDir)
    {
        if (!Directory.Exists(rootsDir))
        {
            Logger.Warn($"Roots directory not found: {rootsDir}");
            return;
        }

        var files = Directory.GetFiles(rootsDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                var root = RootCaCert.FromJson(json);

                if (!root.VerifySelfSignature())
                {
                    Logger.Error($"Root CA self-signature failed: {file}");
                    continue;
                }

                _roots.Add(root);
                Logger.Info($"Loaded Root CA: sn={root.Sn} keyId={root.KeyId}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load Root CA {file}: {ex.Message}");
            }
        }

        Logger.Info($"Root CA store initialized with {_roots.Count} root(s)");
    }

    public RootCaCert? FindIssuer(IntermediateCaCert intermediate)
    {
        foreach (var root in _roots)
        {
            if (intermediate.VerifyBy(root))
                return root;
        }
        return null;
    }
}