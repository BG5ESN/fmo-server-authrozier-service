using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sas.Auth;
using Sas.Logging;
using Sas.Server;
using Sas.Trust;

var foundCommands = args.Where(a => Config.ManagementCommands.Contains(a)).ToArray();

if (args.Any(a => a == "--help" || a == "-h"))
{
    Config.ShowHelp();
    Environment.Exit(0);
}

if (foundCommands.Length > 1)
{
    Console.Error.WriteLine("错误：不能同时使用多个管理命令。 / Cannot use multiple management commands at once.");
    Console.Error.WriteLine($"  你输入了 / You entered: {string.Join(" ", foundCommands)}");
    Console.Error.WriteLine("  请一次只运行一个命令 / Please run one command at a time:");
    Console.Error.WriteLine("    sas --add-cluster");
    Console.Error.WriteLine("    sas --list-clusters");
    Environment.Exit(1);
}

if (foundCommands.Length > 0 && args.Any(a => a == "--update"))
{
    Console.Error.WriteLine("错误：--update 不能与管理命令同时使用。 / --update cannot be used with management commands.");
    Console.Error.WriteLine("  请分开执行 / Please run them separately:");
    Console.Error.WriteLine("    sas --update");
    Console.Error.WriteLine($"    sas {foundCommands[0]}");
    Environment.Exit(1);
}

if (args.Any(a => a == "--update"))
{
    await CheckUpdateCommand();
    return;
}

if (args.Any(a => a == "--add-admin"))
{
    Config.AddAdminCommand(args);
    return;
}

if (args.Any(a => a == "--remove-admin"))
{
    Config.RemoveAdminCommand(args);
    return;
}

if (args.Any(a => a == "--list-admins"))
{
    Config.ListAdminsCommand(args);
    return;
}

if (args.Any(a => a == "--add-cluster"))
{
    Config.AddClusterCommand(args);
    return;
}

if (args.Any(a => a == "--remove-cluster"))
{
    Config.RemoveClusterCommand(args);
    return;
}

if (args.Any(a => a == "--list-clusters"))
{
    Config.ListClustersCommand(args);
    return;
}

var config = Config.Load(args);

var version = Assembly.GetEntryAssembly()?
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";

Logger.Info("        _____ __  __  ___");
Logger.Info("       |  ___|  \\/  |/ _ \\");
Logger.Info("       | |_  | |\\/| | | | |");
Logger.Info("       |  _| | |  | | |_| |");
Logger.Info("       |_|   |_|  |_|\\___/");
Logger.Info("");
Logger.Info("    Server Authorizer Service");
Logger.Info("");

Logger.Info($"Config: {Config.ConfigFilePath}");
Logger.Info($"SAS HTTP auth mode");
Logger.Info($"  MQTT: {config.Mqtt.Host}:{config.Mqtt.Port}");
if (config.Mqtt.Clusters.Length > 0)
{
    Logger.Info($"  Clusters: {config.Mqtt.Clusters.Length} entries");
    foreach (var c in config.Mqtt.Clusters)
        Logger.Info($"    - uid={c.Uid} callsign={c.Callsign} mqtt={c.MqttHost}:{c.MqttPort}");
}

Logger.Info($"  Server: uid={config.Server.Uid} callsign={config.Server.Callsign} certFingerprint={(string.IsNullOrEmpty(config.Server.CertFingerprint) ? "(not set)" : config.Server.CertFingerprint)}");
var adminsCount = config.Server.Admins.Length;
var isAutoAdmin = adminsCount == 1
                  && config.Server.Admins[0].Uid == config.Server.Uid
                  && string.Equals(config.Server.Admins[0].CertFingerprint, config.Server.CertFingerprint, StringComparison.Ordinal);
if (!isAutoAdmin)
{
    var adminsList = adminsCount == 0
        ? "(none)"
        : string.Join(", ", config.Server.Admins.Select(a =>
            $"uid={a.Uid} fp={a.CertFingerprint}"));
    Logger.Info($"  Admins: {adminsCount} {adminsList}");
}

Logger.Info($"  Version: {version}");

var rootStore = new RootCaStore(config.Trust.RootsDir);
var verifier = new CertVerifier();
var crlManager = new CrlManager(config.Trust.RootsDir, config.Crl.RefreshSec);
var aclStore = new AclStore(config.Trust.RootsDir);

Logger.Info("SAS HTTP auth listening on:");
if (config.Http.Addr == "0.0.0.0" || config.Http.Addr == "+")
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
        {
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                Logger.Info($"  http://{ua.Address}:{config.Http.Port}/auth");
        }
    }
}
else
{
    Logger.Info($"  http://{config.Http.Addr}:{config.Http.Port}/auth");
}

crlManager.Initialize(rootStore.AllRoots.ToArray());

var httpHandler = new HttpAuthHandler(config, rootStore, verifier, crlManager, aclStore);
var template = CreateResponseTemplate(config.Http.ResponseTemplate);
var httpServer = new HttpServer(config.Http.Addr, config.Http.Port, config.Http.MaxBodyBytes, config.Http.MaxConcurrent, httpHandler, template);
httpServer.Start();

if (config.Update.Enabled)
    _ = CheckVersionAsync(version);

using var cts = new CancellationTokenSource();

try
{
    await httpServer.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}
finally
{
    httpServer.Stop();
}

Logger.Info("SAS stopped.");
Logger.Flush();

return;

static IResponseTemplate CreateResponseTemplate(string name)
{
    return name.ToLowerInvariant() switch
    {
        "emqx" => new EmqxResponseTemplate(),
        _ => new EmqxResponseTemplate()
    };
}

static bool IsRunningInDocker()
{
    return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
           || File.Exists("/.dockerenv");
}

static bool TryParseCleanVersion(string version, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Version? result)
{
    var idx = version.IndexOf('+');
    return Version.TryParse(idx >= 0 ? version[..idx] : version, out result);
}

static string GetCurrentVersion()
{
    return Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "unknown";
}

static async Task CheckVersionAsync(string currentVersion)
{
    try
    {
        Logger.Info("─── Checking for updates ───");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var json = await http.GetStringAsync("https://bg5esn.com/share/fmo/sas.json");
        var doc = JsonDocument.Parse(json).RootElement;
        var latest = doc.GetProperty("version").GetString() ?? "";

        if (!TryParseCleanVersion(latest, out var remote) ||
            !TryParseCleanVersion(currentVersion, out var local))
        {
            Logger.Info("  Update check skipped (failed to parse version).");
            return;
        }

        if (remote <= local)
        {
            Logger.Info("  Already up to date.");
            return;
        }

        var url = doc.TryGetProperty("url", out var u)
            ? u.GetString()
            : null;
        var notes = doc.TryGetProperty("notes", out var n)
            ? n.GetString()
            : null;

        Logger.Warn("┌─────────────────────────────────────────────────────────────────────────────");
        Logger.Warn($"│  New SAS version available: {latest}");
        if (url != null)
            Logger.Warn($"│  {url}");
        if (!string.IsNullOrWhiteSpace(notes))
            Logger.Warn($"│  {notes}");

        if (IsRunningInDocker())
        {
            Logger.Warn("│  Update: docker pull fmo/sas:latest && docker compose up -d");
        }
        else
        {
            Logger.Warn("│  Update now: sas --update");
        }

        Logger.Warn("└─────────────────────────────────────────────────────────────────────────────");
    }
    catch (Exception ex)
    {
        Logger.Info($"  Update check failed: {ex.Message}");
    }
}

static async Task CheckUpdateCommand()
{
    var version = GetCurrentVersion();
    Console.WriteLine($"SAS version: {version}");

    if (IsRunningInDocker())
    {
        Console.WriteLine();
        Console.WriteLine("Running in Docker. Update with:");
        Console.WriteLine("  docker pull fmo/sas:latest && docker compose up -d");
        return;
    }

    Console.Write("Checking for updates... ");

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync("https://bg5esn.com/share/fmo/sas.json");
        var doc = JsonDocument.Parse(json).RootElement;
        var latest = doc.GetProperty("version").GetString() ?? "";

        if (!TryParseCleanVersion(latest, out var remote) ||
            !TryParseCleanVersion(version, out var local))
        {
            Console.WriteLine("Failed to parse version.");
            return;
        }

        if (remote <= local)
        {
            Console.WriteLine("Already up to date.");
            return;
        }

        Console.WriteLine($"v{latest} available!");

        if (!doc.TryGetProperty("assets", out var assets))
        {
            Console.WriteLine("No download assets found in sas.json.");
            return;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!assets.TryGetProperty(rid, out var downloadUrl))
        {
            Console.WriteLine($"No asset for platform: {rid}");
            return;
        }

        var dl = downloadUrl.GetString() ?? "";
        Console.WriteLine($"Downloading {dl}...");

        var tempDir = Path.Combine(Path.GetTempPath(), "sas_update");
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        var ext = dl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";
        var archivePath = Path.Combine(tempDir, $"update{ext}");

        using (var response = await http.GetAsync(dl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fs = File.Create(archivePath);
            var buffer = new byte[8192];
            long read = 0;
            int len;
            while ((len = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, len));
                read += len;
                if (total > 0)
                {
                    var pct = (int)(read * 100 / total);
                    var bar = new string('#', pct / 2) + new string(' ', 50 - pct / 2);
                    Console.Write($"\r  [{bar}] {pct,3}% {read / 1024,5:N0}KB / {total / 1024,5:N0}KB");
                }
            }

            Console.WriteLine();
        }

        Console.WriteLine("Extracting...");
        if (ext == ".zip")
            ZipFile.ExtractToDirectory(archivePath, tempDir);
        else
            ExtractTarGz(archivePath, tempDir);
        File.Delete(archivePath);

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Sas.exe" : "Sas";
        var newExe = Directory.GetFiles(tempDir, exeName, SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new InvalidOperationException($"{exeName} not found in archive");

        Console.WriteLine("Ready to update. Replacing binary...");

        var exePath = Environment.ProcessPath ?? "";
        var scriptExt = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bat" : "sh";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"sas_update.{scriptExt}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.WriteAllText(scriptPath, $"""
                                           @echo off
                                           setlocal
                                           :wait
                                           timeout /t 1 /nobreak >nul
                                           tasklist /fi "PID eq {Environment.ProcessId}" 2>nul | findstr "{Environment.ProcessId}" >nul
                                           if not errorlevel 1 goto wait
                                           move /y "{newExe}" "{exePath}"
                                           rmdir /s /q "{tempDir}"
                                           echo SAS updated to v{latest}. Run 'sas' to start.
                                           pause
                                           del "%~f0"
                                           """);
        }
        else
        {
            var pid = Environment.ProcessId;
            File.WriteAllText(scriptPath,
                $"#!/bin/sh\n" +
                $"while kill -0 {pid} 2>/dev/null; do sleep 1; done\n" +
                $"mv \"{newExe}\" \"{exePath}\"\n" +
                $"chmod +x \"{exePath}\"\n" +
                $"rm -rf \"{tempDir}\"\n" +
                $"echo \"SAS updated to v{latest}. Run 'sas' to start.\"\n" +
                $"rm \"$0\"\n");
            try
            {
                System.Diagnostics.Process.Start("chmod", $"+x \"{scriptPath}\"")?.WaitForExit();
            }
            catch
            {
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            System.Diagnostics.Process.Start("cmd", $"/c \"{scriptPath}\"");
        else
            System.Diagnostics.Process.Start("sh", scriptPath);

        Environment.Exit(0);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Update failed: {ex.Message}");
    }
}

static void ExtractTarGz(string archivePath, string destDir)
{
    System.Diagnostics.Process.Start("tar", $"-xzf \"{archivePath}\" -C \"{destDir}\"")
        ?.WaitForExit(30_000);
}


sealed class Config
{
    private static readonly string HomeDir =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ConfigPath = null!;

    internal static readonly string[] ManagementCommands =
    {
        "--add-admin", "--remove-admin", "--list-admins",
        "--add-cluster", "--remove-cluster", "--list-clusters"
    };

    public static string ConfigFilePath => ConfigPath;

    public ServerConfig Server { get; set; } = new();
    public MqttConfig Mqtt { get; set; } = new();
    public TrustConfig Trust { get; set; } = new();
    public CrlConfig Crl { get; set; } = new();
    public LogConfig Log { get; set; } = new();
    public HttpConfig Http { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();

    public sealed class ServerConfig
    {
        public long Uid { get; set; }
        public string Callsign { get; set; } = "";
        public long IssuerSn { get; set; }
        public string CertFingerprint { get; set; } = "";
        public AdminEntry[] Admins { get; set; } = [];
    }

    public sealed class AdminEntry
    {
        public long Uid { get; set; }
        public string CertFingerprint { get; set; } = "";
        public string Role { get; set; } = "";
    }

    public sealed class MqttConfig
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 1883;
        public ClusterEntry[] Clusters { get; set; } = [];

        public sealed class ClusterEntry
        {
            [JsonPropertyName("uid")] public long Uid { get; set; }
            [JsonPropertyName("callsign")] public string Callsign { get; set; } = "";
            [JsonPropertyName("mqtt_host")] public string MqttHost { get; set; } = "";
            [JsonPropertyName("mqtt_port")] public int MqttPort { get; set; } = 1883;
            [JsonPropertyName("certFingerprint")] public string CertFingerprint { get; set; } = "";
        }
    }

    public sealed class TrustConfig
    {
        public long[] AllowIssuerSn { get; set; } = [];

        public string RootsDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sas", "roots");
    }

    public sealed class CrlConfig
    {
        public int RefreshSec { get; set; } = 14400;
    }

    public sealed class LogConfig
    {
        public string Level { get; set; } = "Info";
    }

    public sealed class HttpConfig
    {
        public string Addr { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 8080;
        public int TtlSec { get; set; } = 14400;
        public string ResponseTemplate { get; set; } = "emqx";
        public int MaxBodyBytes { get; set; } = 65536;
        public int MaxConcurrent { get; set; } = 128;
    }

    public sealed class UpdateConfig
    {
        public bool Enabled { get; set; } = true;
    }

    public static Config Load(string[] args)
    {
        Config config = null!;

        var flatArgs = FlattenEqArgs(args);
        var explicitConfigPath = ExtractConfigArg(flatArgs);

        if (explicitConfigPath != null)
        {
            ConfigPath = explicitConfigPath;
            if (File.Exists(ConfigPath))
            {
                config = LoadFromFile();
                if (HasInitArgsBeyondConfig(flatArgs))
                {
                    ApplyArgsToConfig(config, args);
                    SaveToFile(config);
                    Console.WriteLine($"配置已更新并保存到 {ConfigPath}。 / Config updated and saved.");
                }
            }
            else if (HasInitArgsBeyondConfig(flatArgs))
            {
                config = BuildFromArgs(args);
                SaveToFile(config);
                Console.WriteLine($"配置已保存到 {ConfigPath}，编辑此文件可修改设置。 / Config saved.");
            }
            else
            {
                Fail($"配置文件不存在：{ConfigPath} / Config file not found.");
            }
        }
        else
        {
            ConfigPath = Path.Combine(HomeDir, ".sas", "config.json");
            if (File.Exists(ConfigPath))
            {
                config = LoadFromFile();
                if (HasInitArgsBeyondConfig(flatArgs))
                {
                    ApplyArgsToConfig(config, args);
                    SaveToFile(config);
                    Console.WriteLine($"配置已更新并保存到 {ConfigPath}。 / Config updated and saved.");
                }
            }
            else if (args.Length == 0)
            {
                config = LoadInteractive();
                SaveToFile(config);
            }
            else
            {
                config = BuildFromArgs(args);
                SaveToFile(config);
                Console.WriteLine($"配置已保存到 {ConfigPath}，编辑此文件可修改设置。 / Config saved.");
            }
        }

        Logger.SetLevel(config.Log.Level);
        Validate(config);
        return config;
    }

    public static void AddAdminCommand(string[] args)
    {
        if (Console.IsInputRedirected)
            Fail("--add-admin 需要交互式终端。 / --add-admin requires an interactive terminal.");

        var config = LoadConfigForManagement(args);

        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine("  FMO SAS — 管理员配置 / Admin Management");
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine();

        if (config.Server.Uid == 0 || string.IsNullOrEmpty(config.Server.Callsign) || string.IsNullOrEmpty(config.Server.CertFingerprint))
            Fail("服务器基础配置不完整（uid/callsign/certFingerprint），请先运行 sas 完成首次配置。 / Server config incomplete, run sas first.");

        var admins = config.Server.Admins.ToList();

        if (admins.Count == 0)
        {
            Console.WriteLine("  当前无管理员配置。 / No admins configured.");
        }
        else
        {
            Console.WriteLine($"  当前管理员 / Current admins ({admins.Count}):");
            PrintAdminList(admins);
            Console.WriteLine();
        }

        Console.WriteLine("  ── Add New Admin / 添加新管理员 ──");
        Console.WriteLine();

        var uid = PromptLong("  Admin UID / 管理员 UID", 0, required: true);

        var existing = admins.FirstOrDefault(a => a.Uid == uid);
        if (existing != null)
        {
            Console.WriteLine($"  警告：UID={uid} 已存在于管理员列表中 (Role={existing.Role}) / Warning: UID already exists");
            var cont = PromptChoice("  是否仍要添加？ / Still add?", ["是 / Yes", "否 / No"]);
            if (cont == 2)
            {
                Console.WriteLine("  已取消。 / Cancelled.");
                return;
            }
        }

        var fp = PromptCertFingerprint("  Cert Fingerprint / 证书指纹 (base64url, 43字符)", "");

        // Console.WriteLine("  Role / 角色:");
        // var roleIdx = PromptChoice("  Select role / 选择角色", ["super", "admin"]);
        // var role = roleIdx == 1 ? "super" : "admin";
        var role = "admin";

        Console.WriteLine();
        Console.WriteLine("  ┌─ 确认 / Confirm ───────────────────────────");
        Console.WriteLine($"  │  UID:         {uid}");
        Console.WriteLine($"  │  Fingerprint: {(fp.Length > 12 ? fp[..12] + "..." : fp)}");
        Console.WriteLine($"  │  Role:        {role}");
        Console.WriteLine("  └─────────────────────────────────────────────");
        Console.WriteLine();

        var save = PromptChoice("  保存？ / Save?", ["是 / Yes", "否 / No"]);
        if (save == 2)
        {
            Console.WriteLine("  已取消。 / Cancelled.");
            return;
        }

        admins.Add(new AdminEntry { Uid = uid, CertFingerprint = fp, Role = role });
        config.Server.Admins = admins.ToArray();
        SaveToFile(config);
        Console.WriteLine($"  已保存到 {ConfigPath}（当前共 {admins.Count} 位管理员） / Saved ({admins.Count} admin(s))");
    }

    public static void RemoveAdminCommand(string[] args)
    {
        if (Console.IsInputRedirected)
            Fail("--remove-admin 需要交互式终端。 / --remove-admin requires an interactive terminal.");

        var config = LoadConfigForManagement(args);

        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine("  FMO SAS — 删除管理员 / Remove Admin");
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine();

        var admins = config.Server.Admins.ToList();

        if (admins.Count == 0)
        {
            Console.WriteLine("  当前无管理员配置。 / No admins configured.");
            Console.WriteLine("  提示：运行时 SAS 会自动将服务器自身作为 super 管理员。 / Tip: SAS auto-adds server itself as super admin.");
            Console.WriteLine("  如需显式配置，请使用 --add-admin。 / Use --add-admin to configure admins.");
            return;
        }

        Console.WriteLine($"  当前管理员 / Current admins ({admins.Count}):");
        PrintAdminList(admins);
        Console.WriteLine();

        int idx;
        while (true)
        {
            Console.Write($"  删除哪个？ / Remove which? [1-{admins.Count}]: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (int.TryParse(input, out idx) && idx >= 1 && idx <= admins.Count)
                break;
            Console.WriteLine($"  无效选择，请输入 1-{admins.Count}。 / Invalid choice.");
        }

        var target = admins[idx - 1];
        Console.WriteLine();

        var confirm = PromptChoice($"  确认删除 UID={target.Uid} ({target.Role})？ / Confirm remove?", ["是 / Yes", "否 / No"]);
        if (confirm == 2)
        {
            Console.WriteLine("  已取消。 / Cancelled.");
            return;
        }

        admins.RemoveAt(idx - 1);
        config.Server.Admins = admins.ToArray();
        SaveToFile(config);

        Console.WriteLine();
        if (admins.Count == 0)
        {
            Console.WriteLine($"  已删除 UID={target.Uid}，管理员列表现为空。 / Removed, admin list is now empty.");
            Console.WriteLine("  提示：运行时 SAS 将自动回退为服务器自身作为 super 管理员。 / Tip: SAS will auto-fallback to server itself.");
        }
        else
        {
            Console.WriteLine($"  已删除 UID={target.Uid}，当前共 {admins.Count} 位管理员。 / Removed ({admins.Count} remaining).");
        }

        Console.WriteLine($"  已保存到 {ConfigPath}。 / Saved.");
    }

    public static void ListAdminsCommand(string[] args)
    {
        var config = LoadConfigForManagement(args);

        var admins = config.Server.Admins.ToList();

        if (admins.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  管理员列表为空。 / Admin list is empty.");
            Console.WriteLine($"  运行时 SAS 自动将服务器自身 (UID={config.Server.Uid}) 作为 super 管理员。 / SAS auto-adds server itself as super admin.");
            Console.WriteLine("  使用 --add-admin 显式配置管理员。 / Use --add-admin to configure admins.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  管理员列表 / Admin List ({admins.Count}):");
        PrintAdminList(admins);
        Console.WriteLine();
        Console.WriteLine("  提示：使用 --add-admin 添加，--remove-admin 删除。 / Tip: use --add-admin to add, --remove-admin to remove.");
    }

    public static void AddClusterCommand(string[] args)
    {
        if (Console.IsInputRedirected)
            Fail("--add-cluster 需要交互式终端。 / --add-cluster requires an interactive terminal.");

        var config = LoadConfigForManagement(args);

        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine("  FMO SAS — 集群节点配置 / Cluster Management");
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine();

        if (config.Server.Uid == 0 || string.IsNullOrEmpty(config.Server.Callsign))
            Fail("服务器基础配置不完整（uid/callsign），请先运行 sas 完成首次配置。 / Server config incomplete, run sas first.");

        var clusters = config.Mqtt.Clusters.ToList();

        if (clusters.Count > 0)
        {
            Console.WriteLine($"  当前集群节点 / Current cluster entries ({clusters.Count}):");
            PrintClusterList(clusters);
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("  当前无集群节点配置。 / No cluster entries configured.");
            Console.WriteLine();
        }

        Console.WriteLine("  ── 添加集群节点 / Add Cluster Entry ──");
        Console.WriteLine();

        var cUid = PromptLong("  UID / 集群节点 UID", 0, required: true);

        if (cUid == config.Server.Uid)
        {
            Console.WriteLine($"  警告：UID={cUid} 与服务器自身 UID 相同，这可能不是预期的。 / Warning: UID same as server UID, this may not be intended.");
            var contSelf = PromptChoice("  是否仍要添加？ / Still add?", ["是 / Yes", "否 / No"]);
            if (contSelf == 2)
            {
                Console.WriteLine("  已取消。 / Cancelled.");
                return;
            }
        }

        var existing = clusters.FirstOrDefault(c => c.Uid == cUid);
        if (existing != null)
        {
            Console.WriteLine($"  警告：UID={cUid} 已存在于集群列表中 (Callsign={existing.Callsign}) / Warning: UID already exists");
            var cont = PromptChoice("  是否仍要添加？ / Still add?", ["是 / Yes", "否 / No"]);
            if (cont == 2)
            {
                Console.WriteLine("  已取消。 / Cancelled.");
                return;
            }
        }

        var cCallsign = PromptString("  Callsign / 集群节点呼号", "", required: true).ToUpperInvariant();
        var cHost = PromptString("  MQTT Host / Broker 地址", "", required: true);
        var cPort = PromptInt("  MQTT Port / Broker 端口", 1883, min: 1, max: 65535);
        var cFp = PromptCertFingerprint("  Cert Fingerprint / 证书指纹 (base64url, 43字符)", "");

        Console.WriteLine();
        Console.WriteLine("  ┌─ 确认 / Confirm ───────────────────────────");
        Console.WriteLine($"  │  UID:         {cUid}");
        Console.WriteLine($"  │  Callsign:    {cCallsign}");
        Console.WriteLine($"  │  MQTT Host:   {cHost}:{cPort}");
        Console.WriteLine($"  │  Fingerprint: {(cFp.Length > 12 ? cFp[..12] + "..." : cFp)}");
        Console.WriteLine("  └─────────────────────────────────────────────");
        Console.WriteLine();

        var save = PromptChoice("  保存？ / Save?", ["是 / Yes", "否 / No"]);
        if (save == 2)
        {
            Console.WriteLine("  已取消。 / Cancelled.");
            return;
        }

        clusters.Add(new MqttConfig.ClusterEntry
        {
            Uid = cUid,
            Callsign = cCallsign,
            MqttHost = cHost,
            MqttPort = cPort,
            CertFingerprint = cFp
        });
        config.Mqtt.Clusters = clusters.ToArray();
        SaveToFile(config);
        Console.WriteLine($"  已保存到 {ConfigPath}（当前共 {clusters.Count} 个集群节点） / Saved ({clusters.Count} cluster(s))");
    }

    public static void RemoveClusterCommand(string[] args)
    {
        if (Console.IsInputRedirected)
            Fail("--remove-cluster 需要交互式终端。 / --remove-cluster requires an interactive terminal.");

        var config = LoadConfigForManagement(args);

        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine("  FMO SAS — 删除集群节点 / Remove Cluster Entry");
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine();

        var clusters = config.Mqtt.Clusters.ToList();

        if (clusters.Count == 0)
        {
            Console.WriteLine("  当前无集群节点配置。 / No cluster entries configured.");
            Console.WriteLine("  使用 --add-cluster 添加集群节点。 / Use --add-cluster to add entries.");
            return;
        }

        Console.WriteLine($"  当前集群节点 / Current cluster entries ({clusters.Count}):");
        PrintClusterList(clusters);
        Console.WriteLine();

        int idx;
        while (true)
        {
            Console.Write($"  删除哪个？ / Remove which? [1-{clusters.Count}]: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (int.TryParse(input, out idx) && idx >= 1 && idx <= clusters.Count)
                break;
            Console.WriteLine($"  无效选择，请输入 1-{clusters.Count}。 / Invalid choice.");
        }

        var target = clusters[idx - 1];
        Console.WriteLine();

        var confirm = PromptChoice($"  确认删除 {target.Callsign} (UID={target.Uid})？ / Confirm remove?", ["是 / Yes", "否 / No"]);
        if (confirm == 2)
        {
            Console.WriteLine("  已取消。 / Cancelled.");
            return;
        }

        clusters.RemoveAt(idx - 1);
        config.Mqtt.Clusters = clusters.ToArray();
        SaveToFile(config);

        Console.WriteLine();
        Console.WriteLine($"  已删除 {target.Callsign} (UID={target.Uid})。 / Removed.");
        Console.WriteLine($"  已保存到 {ConfigPath}（当前共 {clusters.Count} 个集群节点） / Saved.");
    }

    public static void ListClustersCommand(string[] args)
    {
        var config = LoadConfigForManagement(args);

        var clusters = config.Mqtt.Clusters.ToList();

        if (clusters.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  集群节点列表为空。 / Cluster list is empty.");
            Console.WriteLine("  使用 --add-cluster 添加集群节点。 / Use --add-cluster to add entries.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  集群节点列表 / Cluster List ({clusters.Count}):");
        PrintClusterList(clusters);
        Console.WriteLine();
        Console.WriteLine("  提示：使用 --add-cluster 添加，--remove-cluster 删除。 / Tip: use --add-cluster to add, --remove-cluster to remove.");
    }

    private static Config LoadConfigForManagement(string[] args)
    {
        var flatArgs = FlattenEqArgs(args);
        var explicitPath = ExtractConfigArg(flatArgs);

        // 检测不被接受的参数
        var allowedArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--config", "--add-admin", "--remove-admin", "--list-admins",
            "--add-cluster", "--remove-cluster", "--list-clusters"
        };
        var unknownArgs = flatArgs
            .Where(a => a.StartsWith("--") && !allowedArgs.Contains(a))
            .ToArray();

        if (unknownArgs.Length > 0)
        {
            var cmd = args.FirstOrDefault(a => ManagementCommands.Contains(a)) ?? "(unknown)";
            Console.Error.WriteLine("错误：管理命令不接受以下参数。 / Management commands do not accept these arguments:");
            foreach (var arg in unknownArgs)
                Console.Error.WriteLine($"  - {arg}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  管理命令只接受 --config <路径>。 / Management commands only accept --config <path>.");
            Console.Error.WriteLine("  如需修改配置参数，请直接编辑配置文件 / To update config, edit the file directly:");
            Console.Error.WriteLine($"    {explicitPath ?? Path.Combine(HomeDir, ".sas", "config.json")}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  或者分两步操作 / Or run in two steps:");
            Console.Error.WriteLine("    1. sas --mqtt-host xxx ...  （更新配置 / update config）");
            Console.Error.WriteLine($"    2. sas {cmd}  （管理集群/管理员 / manage clusters/admins）");
            Environment.Exit(1);
        }

        ConfigPath = explicitPath
                     ?? Path.Combine(HomeDir, ".sas", "config.json");

        if (!File.Exists(ConfigPath))
            Fail($"配置文件不存在：{ConfigPath}\n  请先运行 sas 完成首次配置，或指定 --config <路径>。 / Config file not found.");

        var json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config == null)
            Fail($"配置文件解析失败：{ConfigPath} / Failed to parse config.");

        config!.Http ??= new();
        config!.Server ??= new();
        config!.Mqtt ??= new();
        config!.Trust ??= new();
        config!.Crl ??= new();
        config!.Log ??= new();
        config!.Update ??= new();

        return config!;
    }

    private static void PrintAdminList(List<AdminEntry> admins)
    {
        Console.WriteLine("  ──────────────────────────────────────────────");
        for (int i = 0; i < admins.Count; i++)
        {
            var a = admins[i];
            var fpDisp = a.CertFingerprint.Length > 12
                ? a.CertFingerprint[..12] + "..."
                : a.CertFingerprint;
            Console.WriteLine($"  [{i + 1}] UID={a.Uid,-8} Role={a.Role,-6} FP={fpDisp}");
        }

        Console.WriteLine("  ──────────────────────────────────────────────");
    }

    private static void PrintClusterList(List<MqttConfig.ClusterEntry> clusters)
    {
        Console.WriteLine("  ──────────────────────────────────────────────");
        for (int i = 0; i < clusters.Count; i++)
        {
            var c = clusters[i];
            var fpDisp = c.CertFingerprint.Length > 12
                ? c.CertFingerprint[..12] + "..."
                : c.CertFingerprint;
            Console.WriteLine($"  [{i + 1}] UID={c.Uid,-8} Callsign={c.Callsign,-10} MQTT={c.MqttHost}:{c.MqttPort,-6} FP={fpDisp}");
        }

        Console.WriteLine("  ──────────────────────────────────────────────");
    }

    private static Config LoadFromFile()
    {
        var json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config == null)
            Fail("配置文件解析失败。 / Failed to parse config.json.");

        config!.Http ??= new();
        config!.Server ??= new();
        config!.Mqtt ??= new();
        config!.Trust ??= new();
        config!.Crl ??= new();
        config!.Log ??= new();
        config!.Update ??= new();

        return config!;
    }

    private static Config BuildFromArgs(string[] args)
    {
        var config = new Config();
        var flatArgs = FlattenEqArgs(args);

        for (int i = 0; i < flatArgs.Count; i++)
        {
            switch (flatArgs[i])
            {
                case "--config" when i + 1 < flatArgs.Count:
                    ConfigPath = flatArgs[++i];
                    break;
                case "--server-uid" when i + 1 < flatArgs.Count:
                    config.Server.Uid = long.Parse(flatArgs[++i]);
                    break;
                case "--server-callsign" when i + 1 < flatArgs.Count:
                    config.Server.Callsign = flatArgs[++i];
                    break;
                case "--mqtt-host" when i + 1 < flatArgs.Count:
                    config.Mqtt.Host = flatArgs[++i];
                    break;
                case "--mqtt-port" when i + 1 < flatArgs.Count:
                    config.Mqtt.Port = int.Parse(flatArgs[++i]);
                    break;
                case "--allow-issuer-sn" when i + 1 < flatArgs.Count:
                    config.Trust.AllowIssuerSn = flatArgs[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(long.Parse)
                        .ToArray();
                    break;
                case "--roots-dir" when i + 1 < flatArgs.Count:
                    config.Trust.RootsDir = flatArgs[++i];
                    break;
                case "--issuer-sn" when i + 1 < flatArgs.Count:
                    config.Server.IssuerSn = long.Parse(flatArgs[++i]);
                    break;
                case "--cert-fingerprint" when i + 1 < flatArgs.Count:
                    config.Server.CertFingerprint = flatArgs[++i];
                    break;
                case "--crl-refresh" when i + 1 < flatArgs.Count:
                    config.Crl.RefreshSec = int.Parse(flatArgs[++i]);
                    break;
                case "--log-level" when i + 1 < flatArgs.Count:
                    config.Log.Level = flatArgs[++i];
                    break;
                case "--http-port" when i + 1 < flatArgs.Count:
                    config.Http.Port = int.Parse(flatArgs[++i]);
                    break;
                case "--http-addr" when i + 1 < flatArgs.Count:
                    config.Http.Addr = flatArgs[++i];
                    break;
                case "--http-ttl" when i + 1 < flatArgs.Count:
                    config.Http.TtlSec = int.Parse(flatArgs[++i]);
                    break;
            }
        }

        return config;
    }

    private static void ApplyArgsToConfig(Config config, string[] args)
    {
        var flatArgs = FlattenEqArgs(args);

        for (int i = 0; i < flatArgs.Count; i++)
        {
            switch (flatArgs[i])
            {
                case "--server-uid" when i + 1 < flatArgs.Count:
                    config.Server.Uid = long.Parse(flatArgs[++i]);
                    break;
                case "--server-callsign" when i + 1 < flatArgs.Count:
                    config.Server.Callsign = flatArgs[++i];
                    break;
                case "--mqtt-host" when i + 1 < flatArgs.Count:
                    config.Mqtt.Host = flatArgs[++i];
                    break;
                case "--mqtt-port" when i + 1 < flatArgs.Count:
                    config.Mqtt.Port = int.Parse(flatArgs[++i]);
                    break;
                case "--allow-issuer-sn" when i + 1 < flatArgs.Count:
                    config.Trust.AllowIssuerSn = flatArgs[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(long.Parse)
                        .ToArray();
                    break;
                case "--roots-dir" when i + 1 < flatArgs.Count:
                    config.Trust.RootsDir = flatArgs[++i];
                    break;
                case "--issuer-sn" when i + 1 < flatArgs.Count:
                    config.Server.IssuerSn = long.Parse(flatArgs[++i]);
                    break;
                case "--cert-fingerprint" when i + 1 < flatArgs.Count:
                    config.Server.CertFingerprint = flatArgs[++i];
                    break;
                case "--crl-refresh" when i + 1 < flatArgs.Count:
                    config.Crl.RefreshSec = int.Parse(flatArgs[++i]);
                    break;
                case "--log-level" when i + 1 < flatArgs.Count:
                    config.Log.Level = flatArgs[++i];
                    break;
                case "--http-port" when i + 1 < flatArgs.Count:
                    config.Http.Port = int.Parse(flatArgs[++i]);
                    break;
                case "--http-addr" when i + 1 < flatArgs.Count:
                    config.Http.Addr = flatArgs[++i];
                    break;
                case "--http-ttl" when i + 1 < flatArgs.Count:
                    config.Http.TtlSec = int.Parse(flatArgs[++i]);
                    break;
            }
        }
    }

    private static List<string> FlattenEqArgs(string[] args)
    {
        var result = new List<string>();
        foreach (var arg in args)
        {
            if (arg.StartsWith("--") && arg.Contains('='))
            {
                var eq = arg.IndexOf('=');
                result.Add(arg[..eq]);
                result.Add(arg[(eq + 1)..]);
            }
            else
            {
                result.Add(arg);
            }
        }

        return result;
    }

    private static string? ExtractConfigArg(List<string> flatArgs)
    {
        for (int i = 0; i < flatArgs.Count; i++)
        {
            if (flatArgs[i] == "--config" && i + 1 < flatArgs.Count)
                return flatArgs[i + 1];
        }

        return null;
    }

    private static readonly HashSet<string> InitArgNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "--server-uid", "--server-callsign", "--mqtt-host", "--mqtt-port",
        "--cert-fingerprint", "--issuer-sn", "--allow-issuer-sn", "--roots-dir",
        "--crl-refresh", "--http-port", "--http-addr", "--http-ttl", "--log-level"
    };

    private static bool HasInitArgsBeyondConfig(List<string> flatArgs)
    {
        for (int i = 0; i < flatArgs.Count; i++)
        {
            if (InitArgNames.Contains(flatArgs[i]))
                return true;
        }

        return false;
    }

    private static Config LoadInteractive()
    {
        if (Console.IsInputRedirected)
            Fail("未找到配置文件且终端非交互式。\n" +
                 "  请使用 CLI 参数初始化 / Use CLI args to initialize:\n" +
                 "    sas.exe --server-uid 12345 --server-callsign BG5ESN --mqtt-host ... --cert-fingerprint ...\n" +
                 "  或在 Docker 中挂载 / Or in Docker: docker run ... --roots-dir /data/sas/roots");

        while (true)
        {
            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.WriteLine("  FMO V4.0  Server Authorizer Service (SAS)");
            Console.WriteLine("  服务器授权服务 — HTTP 认证网关");
            Console.WriteLine("══════════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("  SAS verifies FMO device identity via Ed25519 certificate");
            Console.WriteLine("  chains and provides real-time HTTP auth responses to");
            Console.WriteLine("  the EMQX Broker.");
            Console.WriteLine();
            Console.WriteLine("  SAS 通过 Ed25519 证书链验证 FMO 设备身份，为 EMQX");
            Console.WriteLine("  Broker 提供实时 HTTP 鉴权响应。");
            Console.WriteLine();
            Console.WriteLine("  This wizard will guide you through first-time setup.");
            Console.WriteLine("  此向导将引导你完成首次配置。");
            Console.WriteLine();

            var config = new Config();

            Console.WriteLine("  ┌─ 必填参数 / Required ──────────────────────");
            Console.WriteLine("  └─────────────────────────────────────────────");
            Console.WriteLine();
            Console.WriteLine("  Server UID — 你的 FMO 设备唯一数字 ID。");
            Console.WriteLine("  可在设备后台页面的「证书信息」中查看。");
            config.Server.Uid = PromptLong("  Server UID / FMO 设备 UID", config.Server.Uid, required: true);
            Console.WriteLine();
            Console.WriteLine("  Server Callsign — 你的业余无线电台呼号，大写。");
            Console.WriteLine("  必须与 FMO 注册时的呼号完全一致。");
            config.Server.Callsign = PromptString("  Server Callsign / 服务器呼号", config.Server.Callsign, required: true).ToUpperInvariant();
            Console.WriteLine();
            Console.WriteLine("  MQTT Host — EMQX Broker 的主机名或 IP 地址。");
            Console.WriteLine("  不带协议前缀，如 fmo.example.com 或 192.168.1.100。");
            config.Mqtt.Host = PromptString("  MQTT Host / Broker 地址", config.Mqtt.Host, required: true);
            Console.WriteLine();
            Console.WriteLine("  MQTT Port — EMQX Broker 的 MQTT 监听端口。");
            config.Mqtt.Port = PromptInt("  MQTT Port / Broker 端口", config.Mqtt.Port, min: 1, max: 65535);

            Console.WriteLine();
            Console.WriteLine("  ┌─ HTTP 认证器参数 ──────────────────────────");
            Console.WriteLine("  └─────────────────────────────────────────────");
            Console.WriteLine();
            Console.WriteLine("  HTTP Auth Port — EMQX Broker 回调 SAS 的端口。");
            Console.WriteLine("  EMQX 在客户端 MQTT CONNECT 时向此端口发起");
            Console.WriteLine("  POST /auth 请求以完成鉴权。");
            config.Http.Port = PromptInt("  HTTP Auth Port / 认证回调端口", config.Http.Port, min: 1, max: 65535);
            Console.WriteLine();
            Console.WriteLine("  HTTP Bind Addr — SAS HTTP 服务的监听地址。");
            Console.WriteLine("  本机部署建议用 127.0.0.1（仅 EMQX 可访问），");
            Console.WriteLine("  Docker 部署用 0.0.0.0（允许容器端口映射）。");
            config.Http.Addr = PromptString("  HTTP Bind Addr / 绑定地址", config.Http.Addr);
            Console.WriteLine();
            Console.WriteLine("  Session TTL — EMQX 客户端会话有效期（秒）。");
            Console.WriteLine("  到期后 EMQX 强制断开客户端重新认证。");
            config.Http.TtlSec = PromptInt("  Session TTL / 会话有效期 (seconds)", config.Http.TtlSec);

            Console.WriteLine();
            Console.WriteLine("  ┌─ 证书参数 / Certificate Params ────────────");
            Console.WriteLine("  └─────────────────────────────────────────────");
            Console.WriteLine();
            Console.WriteLine("  Cert Fingerprint — 服务器设备证书的 SHA-256 指纹。");
            Console.WriteLine("  用于 proof 签名绑定，防服务器身份伪造和跨服务器重放。");
            Console.WriteLine("  可在 FMO 后台页面的「证书指纹」中复制。");
            Console.WriteLine("  格式：base64url (43 字符，不含 padding)。");
            config.Server.CertFingerprint = PromptCertFingerprint("  Cert Fingerprint / 证书指纹", config.Server.CertFingerprint);
            Console.WriteLine();

            Console.WriteLine("  ┌─ 可选参数 / Optional ──────────────────────");
            Console.WriteLine("  └─────────────────────────────────────────────");
            Console.WriteLine();
            Console.WriteLine("  Allow Issuer SN — 限制可被接受的 Intermediate CA。");
            Console.WriteLine("  只允许列表中的 CA 签发的设备证书登录。");
            Console.WriteLine("  用逗号分隔，如 1001,2001。留空 = 信任所有挂到");
            Console.WriteLine("  Root CA 的中级证书。");
            var snInput = PromptString("  Allow Issuer SN / 签发者白名单（留空 = 全部信任）", "");
            if (!string.IsNullOrWhiteSpace(snInput))
            {
                config.Trust.AllowIssuerSn = snInput
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(long.Parse)
                    .ToArray();
            }

            Console.WriteLine();
            Console.WriteLine("  Roots Dir — Root CA 证书存放目录。");
            Console.WriteLine("  SAS 启动时从此目录加载所有 Root CA 作为信任锚。");
            Console.WriteLine("  默认路径：~/.sas/roots");
            config.Trust.RootsDir = PromptString("  Roots Dir / Root 证书目录", config.Trust.RootsDir);
            Console.WriteLine();
            Console.WriteLine("  CRL Refresh — 证书吊销列表(CRL)的拉取频率。");
            Console.WriteLine("  SAS 定期从 CA 服务器下载最新 CRL 检查证书是否");
            Console.WriteLine("  已被吊销。默认 4 小时适合大多数场景。");
            config.Crl.RefreshSec = PromptInt("  CRL Refresh / CRL 刷新间隔 (seconds)", config.Crl.RefreshSec);
            Console.WriteLine();
            Console.WriteLine("  Log Level — 日志输出级别。");
            Console.WriteLine("  Debug: 详细调试信息  Info: 常规运行信息");
            Console.WriteLine("  Warn: 仅警告+错误  Error: 仅错误");
            config.Log.Level = PromptLogLevel("  Log Level / 日志级别", config.Log.Level);
            Console.WriteLine();
            //
            // Console.WriteLine("  ┌─ 集群配置 / Cluster Config (可选) ─────────");
            // Console.WriteLine("  └─────────────────────────────────────────────");
            // Console.WriteLine();
            // Console.WriteLine("  Cluster — 如果你有多个 MQTT Broker 共用同一个 SAS，");
            // Console.WriteLine("  可以在这里添加额外的 Broker 节点。");
            // Console.WriteLine("  添加后，连接到这些 Broker 的设备也能通过本 SAS 认证。");
            // Console.WriteLine();
            // var clusters = new List<Config.MqttConfig.ClusterEntry>();
            // while (true)
            // {
            //     var addCluster = PromptChoice("  是否添加集群节点? / Add cluster entry?", ["否 / No", "是 / Yes"]);
            //     if (addCluster != 2) break;
            //
            //     Console.WriteLine();
            //     Console.WriteLine("  ── 新集群节点 / New Cluster Entry ──");
            //     var cUid = PromptLong("    UID / 集群节点 UID", 0, required: true);
            //     var cCallsign = PromptString("    Callsign / 集群节点呼号", "", required: true);
            //     var cHost = PromptString("    MQTT Host / Broker 地址", "", required: true);
            //     var cPort = PromptInt("    MQTT Port / Broker 端口", 1883);
            //     var cFp = PromptCertFingerprint("    Cert Fingerprint / 证书指纹 (base64url, 43字符)", "");
            //     while (string.IsNullOrEmpty(cFp))
            //     {
            //         Console.WriteLine("    证书指纹为必填项。");
            //         cFp = PromptCertFingerprint("    Cert Fingerprint / 证书指纹 (base64url, 43字符)", "");
            //     }
            //
            //     clusters.Add(new Config.MqttConfig.ClusterEntry
            //     {
            //         Uid = cUid,
            //         Callsign = cCallsign,
            //         MqttHost = cHost,
            //         MqttPort = cPort,
            //         CertFingerprint = cFp
            //     });
            //     Console.WriteLine($"    ✓ 已添加 {cCallsign} (uid={cUid}, {cHost}:{cPort})");
            //     Console.WriteLine();
            // }
            // config.Mqtt.Clusters = clusters.ToArray();
            // Console.WriteLine();

            Console.WriteLine("  ┌─ 配置预览 / Configuration Preview ──────────");
            Console.WriteLine($"  │  Server UID:   {config.Server.Uid}");
            Console.WriteLine($"  │  Callsign:     {config.Server.Callsign}");
            var fpDisp = string.IsNullOrEmpty(config.Server.CertFingerprint) ? "(not set)" : config.Server.CertFingerprint[..Math.Min(12, config.Server.CertFingerprint.Length)] + "...";
            Console.WriteLine($"  │  Cert FP:      {fpDisp}");
            Console.WriteLine($"  │  MQTT Host:    {config.Mqtt.Host}:{config.Mqtt.Port}");
            Console.WriteLine($"  │  HTTP Bind:    {config.Http.Addr}:{config.Http.Port}");
            Console.WriteLine($"  │  Session TTL:  {config.Http.TtlSec}s");
            Console.WriteLine($"  │  Roots Dir:    {config.Trust.RootsDir}");
            Console.WriteLine($"  │  CRL Refresh:  {config.Crl.RefreshSec}s");
            Console.WriteLine($"  │  Log Level:    {config.Log.Level}");
            var allowIssuer = config.Trust.AllowIssuerSn.Length == 0 ? "(all)" : string.Join(",", config.Trust.AllowIssuerSn);
            Console.WriteLine($"  │  Allow Issuer: {allowIssuer}");
            Console.WriteLine($"  │  Admins:       {config.Server.Admins.Length} entry(s)");
            Console.WriteLine($"  │  Clusters:     {config.Mqtt.Clusters.Length} entry(s)");
            foreach (var c in config.Mqtt.Clusters)
                Console.WriteLine($"  │    - {c.Callsign} uid={c.Uid} {c.MqttHost}:{c.MqttPort}");
            Console.WriteLine("  └─────────────────────────────────────────────");
            Console.WriteLine();

            var confirm = PromptChoice("Save and start? / 保存并启动?", ["Yes", "No (重新配置)", "Quit (退出)"]);
            if (confirm == 1)
            {
                Console.WriteLine();
                Console.WriteLine("  ┌─ 提示 / Tip ────────────────────────────────");
                Console.WriteLine("  │  多管理员请编辑 config.json 中的 admins 数组");
                Console.WriteLine("  │  Multi-admin: edit 'admins' array in config.json");
                Console.WriteLine("  │  或 / or");
                Console.WriteLine("  │  添加管理员 / Add admin:     sas --add-admin");
                Console.WriteLine("  │  删除管理员 / Remove admin:  sas --remove-admin");
                Console.WriteLine("  │  查看管理员 / List admins:   sas --list-admins");
                Console.WriteLine("  │");
                Console.WriteLine("  │  集群节点请编辑 config.json 中的 mqtt.clusters 数组");
                Console.WriteLine("  │  Cluster nodes: edit 'mqtt.clusters' in config.json");
                Console.WriteLine("  │  或 / or");
                Console.WriteLine("  │  添加集群节点 / Add cluster:    sas --add-cluster");
                Console.WriteLine("  │  删除集群节点 / Remove cluster: sas --remove-cluster");
                Console.WriteLine("  │  查看集群节点 / List clusters:  sas --list-clusters");
                Console.WriteLine("  │");
                Console.WriteLine("  │  配置已保存至 / Saved to:");
                Console.WriteLine($"  │  {ConfigPath}");
                Console.WriteLine("  └─────────────────────────────────────────────");
                Console.WriteLine();
                return config;
            }

            if (confirm == 3)
            {
                Console.WriteLine("配置已取消。 / Setup cancelled.");
                Environment.Exit(0);
            }

            Console.WriteLine();
        }
    }

    private static string PromptString(string label, string defaultValue, bool required = false)
    {
        while (true)
        {
            var display = string.IsNullOrEmpty(defaultValue) ? "" : $" [{defaultValue}]";
            Console.Write($"  {label}{display}: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            var result = string.IsNullOrEmpty(input) ? defaultValue : input;
            if (required && string.IsNullOrEmpty(result))
            {
                Console.WriteLine("  此项为必填。 / This field is required.");
                continue;
            }

            return result;
        }
    }

    private static int PromptInt(string label, int defaultValue, bool required = false, int min = int.MinValue, int max = int.MaxValue)
    {
        while (true)
        {
            var display = $" [{defaultValue}]";
            Console.Write($"  {label}{display}: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
            {
                if (required && defaultValue <= 0)
                {
                    Console.WriteLine("  此项为必填，请输入有效数字。 / This field is required, enter a valid number.");
                    continue;
                }

                return defaultValue;
            }

            if (int.TryParse(input, out var val) && val >= min && val <= max)
                return val;
            if (min != int.MinValue || max != int.MaxValue)
                Console.WriteLine($"  无效数字，请输入 {min}-{max}。 / Invalid number, enter {min}-{max}.");
            else
                Console.WriteLine("  无效数字，请重试。 / Invalid number, try again.");
        }
    }

    private static long PromptLong(string label, long defaultValue, bool required = false)
    {
        while (true)
        {
            var display = defaultValue != 0 ? $" [{defaultValue}]" : "";
            Console.Write($"  {label}{display}: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
            {
                if (required && defaultValue <= 0)
                {
                    Console.WriteLine("  此项为必填，请输入有效数字。 / This field is required, please enter a valid number.");
                    continue;
                }

                return defaultValue;
            }

            if (long.TryParse(input, out var val))
                return val;
            Console.WriteLine("  无效数字，请重试。 / Invalid number, try again.");
        }
    }

    private static string PromptCertFingerprint(string label, string defaultValue)
    {
        while (true)
        {
            var display = string.IsNullOrEmpty(defaultValue) ? "" : $" [{defaultValue}]";
            Console.Write($"  {label}{display}: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            var result = string.IsNullOrEmpty(input) ? defaultValue : input;
            if (Regex.IsMatch(result, @"^[A-Za-z0-9_-]{43}$"))
                return result;
            Console.WriteLine("  格式无效，需要 43 字符 base64url 字符串（不含 padding）。 / Invalid format. Expected: 43-character base64url string (no padding).");
        }
    }

    private static string PromptLogLevel(string label, string defaultValue)
    {
        Console.WriteLine($"  {label}");
        Console.WriteLine("    [1] Debug    [2] Info    [3] Warn    [4] Error");
        var levelMap = new Dictionary<int, string> { [1] = "Debug", [2] = "Info", [3] = "Warn", [4] = "Error" };
        var defaultIdx = levelMap.FirstOrDefault(kv => string.Equals(kv.Value, defaultValue, StringComparison.OrdinalIgnoreCase)).Key;
        if (defaultIdx == 0) defaultIdx = 2;
        while (true)
        {
            Console.Write($"  Select / 选择 (1-4) [{defaultIdx}]: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
                return levelMap[defaultIdx];
            if (int.TryParse(input, out var val) && levelMap.ContainsKey(val))
                return levelMap[val];
            Console.WriteLine("  无效选择，请输入 1-4。 / Invalid choice, enter 1-4.");
        }
    }

    private static int PromptChoice(string label, string[] options)
    {
        for (int i = 0; i < options.Length; i++)
            Console.WriteLine($"    [{i + 1}] {options[i]}");
        while (true)
        {
            Console.Write($"  {label}: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (int.TryParse(input, out var val) && val >= 1 && val <= options.Length)
                return val;
            Console.WriteLine($"  无效选择，请输入 1-{options.Length}。 / Invalid choice, enter 1-{options.Length}.");
        }
    }

    private static void SaveToFile(Config config)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ConfigPath, json);
    }

    private static void Validate(Config cfg)
    {
        var missing = new List<string>();

        if (cfg.Server.Uid == 0)
            missing.Add("  server.uid             (required) Server UID");
        if (string.IsNullOrWhiteSpace(cfg.Server.Callsign))
            missing.Add("  server.callsign        (required) Server callsign");
        if (string.IsNullOrWhiteSpace(cfg.Mqtt.Host))
            missing.Add("  mqtt.host              (required) MQTT broker hostname");
        if (string.IsNullOrWhiteSpace(cfg.Server.CertFingerprint))
            missing.Add("  server.certFingerprint (required) Server UserCert fingerprint");

        if (cfg.Http.Port <= 0)
            missing.Add("  http.port              (required) HTTP listen port");
        if (cfg.Http.MaxBodyBytes <= 0)
            missing.Add("  http.maxBodyBytes      (required) must be > 0, e.g. 65536");
        if (cfg.Http.MaxConcurrent <= 0)
            missing.Add("  http.maxConcurrent     (required) must be > 0, e.g. 128");

        if (missing.Count > 0)
        {
            Logger.Error("缺少必填配置 / Missing required configuration:");
            foreach (var m in missing)
                Logger.Error(m);
            Logger.Error("");
            Logger.Error($"请编辑 {ConfigPath} 补充以下字段，或运行 / Fix: edit config file, or run");
            Logger.Error("  sas.exe（无参数）启动交互式配置向导。 / sas.exe without arguments to start setup wizard.");
            Logger.Error("  帮助：sas.exe --help / Help: sas.exe --help");
            Environment.Exit(1);
        }
    }

    internal static void ShowHelp()
    {
        Console.WriteLine("FMO V4.0 Server Authorizer Service / 服务器授权服务");
        Console.WriteLine();
        Console.WriteLine("用法 / Usage:");
        Console.WriteLine("  sas.exe                                  交互式配置向导 / Interactive setup");
        Console.WriteLine("  sas.exe --server-uid ... --cert-fingerprint ...   命令行初始化 / CLI init");
        Console.WriteLine("  sas.exe --update                         检查并应用更新 / Check and apply updates");
        Console.WriteLine("  sas.exe --help                           显示此帮助 / Show this help");
        Console.WriteLine();
        Console.WriteLine("管理员管理 / Admin Management:");
        Console.WriteLine("  sas.exe --add-admin [--config <path>]    添加管理员 / Add admin");
        Console.WriteLine("  sas.exe --remove-admin [--config <path>] 删除管理员 / Remove admin");
        Console.WriteLine("  sas.exe --list-admins [--config <path>]  查看管理员列表 / List admins");
        Console.WriteLine();
        Console.WriteLine("集群管理 / Cluster Management:");
        Console.WriteLine("  sas.exe --add-cluster [--config <path>]  添加集群节点 / Add cluster node");
        Console.WriteLine("  sas.exe --remove-cluster [--config <path>] 删除集群节点 / Remove cluster node");
        Console.WriteLine("  sas.exe --list-clusters [--config <path>] 查看集群节点 / List cluster nodes");
        Console.WriteLine();
        Console.WriteLine("必填参数 / Required:");
        Console.WriteLine("  --server-uid <long>                      FMO 设备唯一数字 ID / FMO device UID");
        Console.WriteLine("  --server-callsign <str>                  业余电台呼号（大写）/ Amateur radio callsign");
        Console.WriteLine("  --mqtt-host <str>                        EMQX Broker 地址（不含协议前缀）/ Broker hostname or IP");
        Console.WriteLine("  --cert-fingerprint <str>                 服务器证书 SHA-256 指纹 / Server cert fingerprint (base64url)");
        Console.WriteLine();
        Console.WriteLine("HTTP 认证 / HTTP Auth:");
        Console.WriteLine("  --http-port <int>                        EMQX 回调端口 / Callback port (default: 8080)");
        Console.WriteLine("  --http-addr <str>                        HTTP 绑定地址 / Bind address (default: 0.0.0.0)");
        Console.WriteLine("  --http-ttl <int>                         会话有效期（秒）/ Session TTL in seconds (default: 14400)");
        Console.WriteLine();
        Console.WriteLine("可选参数 / Optional:");
        Console.WriteLine("  --mqtt-port <int>                        Broker MQTT 端口 / Broker port (default: 1883)");
        Console.WriteLine("  --allow-issuer-sn <str>                  签发者白名单，逗号分隔 / Issuer SN whitelist (default: 全部信任)");
        Console.WriteLine("  --issuer-sn <long>                       服务器 CA 序列号 / Server issuer SN");
        Console.WriteLine("  --crl-refresh <int>                      CRL 刷新间隔（秒）/ CRL refresh interval (default: 14400)");
        Console.WriteLine("  --roots-dir <path>                       Root CA 证书目录 / Root CA directory (default: ~/.sas/roots)");
        Console.WriteLine("  --log-level <str>                        日志级别 / Log level: Debug/Info/Warn/Error (default: Info)");
        Console.WriteLine();
        Console.WriteLine($"首次运行后配置保存至 / Config saved to: {ConfigPath}");
        Console.WriteLine("后续启动无需参数。 / Subsequent restarts require no arguments.");
    }

    private static void Fail(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }
}