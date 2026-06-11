using System.Text.Json;
using Sas.Logging;

namespace Sas.Auth;

public sealed class AclStore
{
    private readonly Dictionary<string, RoleAcl> _roles = new();

    public AclStore(string rootsDir)
    {
        var parentDir = Path.GetDirectoryName(rootsDir)!;
        var rolesDir = Path.Combine(parentDir, "roles");

        SeedBuiltinRoles(rolesDir);
        LoadRoles(rolesDir);

        if (_roles.Count == 0)
        {
            Logger.Warn("No role definitions loaded, using built-in defaults");
            LoadBuiltinDefaults();
        }
    }

    public (string[] all, string[] pub, string[] sub) GetPermissions(string role)
    {
        if (!string.IsNullOrEmpty(role) && _roles.TryGetValue(role, out var acl))
            return (acl.All, acl.Pub, acl.Sub);

        if (_roles.TryGetValue("user", out var userAcl))
            return (userAcl.All, userAcl.Pub, userAcl.Sub);

        return BuiltinUserAcl();
    }

    public string[] ResolveForUid(string[] topics, long uid)
    {
        return topics.Select(t => t.Replace("{uid}", uid.ToString())).ToArray();
    }

    private void SeedBuiltinRoles(string rolesDir)
    {
        var builtinDir = FindBuiltinDir();
        if (builtinDir == null || !Directory.Exists(builtinDir))
            return;

        Directory.CreateDirectory(rolesDir);

        foreach (var file in Directory.GetFiles(builtinDir, "*.json"))
        {
            var dest = Path.Combine(rolesDir, Path.GetFileName(file));
            if (!File.Exists(dest))
            {
                File.Copy(file, dest);
                Logger.Info($"Seeded role: {Path.GetFileName(dest)}");
            }
        }
    }

    private void LoadRoles(string rolesDir)
    {
        if (!Directory.Exists(rolesDir))
            return;

        foreach (var file in Directory.GetFiles(rolesDir, "*.json"))
        {
            try
            {
                var acl = JsonSerializer.Deserialize<RoleAcl>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (acl != null && !string.IsNullOrEmpty(acl.Role))
                {
                    _roles[acl.Role] = acl;
                    Logger.Info($"Loaded role: {acl.Role} ({Path.GetFileName(file)})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load role {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private void LoadBuiltinDefaults()
    {
        _roles["super"] = new RoleAcl
        {
            Role = "super",
            All = ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
            Pub = ["FMO/QSO/+", "FMO/LATE/UID_V1/{uid}", "FMO/PROFILE", "FMO/MUTELIST", "FMO/REMOTE_CONTROL", "FMO/SERVER_INFO"],
            Sub = ["FMO/QSO/UID/{uid}", "FMO/LATE/UID_V1/{uid}"]
        };
        _roles["admin"] = new RoleAcl
        {
            Role = "admin",
            All = ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
            Pub = ["FMO/QSO/+", "FMO/LATE/UID_V1/{uid}", "FMO/MUTELIST"],
            Sub = ["FMO/QSO/UID/{uid}", "FMO/LATE/UID_V1/{uid}", "FMO/PROFILE", "FMO/REMOTE_CONTROL", "FMO/SERVER_INFO"]
        };
        _roles["user"] = new RoleAcl { Role = "user",
            All = ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
            Pub = ["FMO/QSO/+", "FMO/LATE/UID_V1/{uid}"],
            Sub = ["FMO/QSO/UID/{uid}", "FMO/LATE/UID_V1/{uid}", "FMO/PROFILE", "FMO/MUTELIST", "FMO/REMOTE_CONTROL", "FMO/SERVER_INFO"]
        };
    }

    private static (string[] All, string[] Pub, string[] Sub) BuiltinUserAcl() => (
        ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
        ["FMO/QSO/+", "FMO/LATE/UID_V1/{uid}"],
        ["FMO/QSO/UID/{uid}", "FMO/LATE/UID_V1/{uid}", "FMO/PROFILE", "FMO/MUTELIST", "FMO/REMOTE_CONTROL", "FMO/SERVER_INFO"]
    );

    private static string? FindBuiltinDir()
    {
        var candidates = new List<string>
        {
            "/app/builtin/roles",
            Path.Combine(AppContext.BaseDirectory, "builtin", "roles"),
            Path.Combine(Directory.GetCurrentDirectory(), "builtin", "roles")
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
                return dir;
        }

        return null;
    }

    private sealed class RoleAcl
    {
        public string Role { get; set; } = "";
        public string[] All { get; set; } = [];
        public string[] Pub { get; set; } = [];
        public string[] Sub { get; set; } = [];
    }
}
