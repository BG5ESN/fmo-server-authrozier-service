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
catch (OperationCanceledException) { }
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
            ? u.GetString() : null;
        var notes = doc.TryGetProperty("notes", out var n)
            ? n.GetString() : null;

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
            try { System.Diagnostics.Process.Start("chmod", $"+x \"{scriptPath}\"")?.WaitForExit(); } catch { }
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
            [JsonPropertyName("uid")]
            public long Uid { get; set; }
            [JsonPropertyName("callsign")]
            public string Callsign { get; set; } = "";
            [JsonPropertyName("mqtt_host")]
            public string MqttHost { get; set; } = "";
            [JsonPropertyName("mqtt_port")]
            public int MqttPort { get; set; } = 1883;
            [JsonPropertyName("certFingerprint")]
            public string CertFingerprint { get; set; } = "";
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

        if (args.Any(a => a == "--help" || a == "-h"))
        {
            ShowHelp();
            Environment.Exit(0);
        }

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
                    Console.WriteLine($"Config updated and saved to {ConfigPath}");
                }
            }
            else if (HasInitArgsBeyondConfig(flatArgs))
            {
                config = BuildFromArgs(args);
                SaveToFile(config);
                Console.WriteLine($"Config saved to {ConfigPath}, edit it to change settings");
            }
            else
            {
                Fail($"Config file not found: {ConfigPath}");
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
                    Console.WriteLine($"Config updated and saved to {ConfigPath}");
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
                Console.WriteLine($"Config saved to {ConfigPath}, edit it to change settings");
            }
        }

        if (config.Server.Admins.Length == 0 && !string.IsNullOrEmpty(config.Server.CertFingerprint))
        {
            config.Server.Admins = [
                new AdminEntry { Uid = config.Server.Uid, CertFingerprint = config.Server.CertFingerprint, Role = "super" }
            ];
        }

        Logger.SetLevel(config.Log.Level);
        Validate(config);
        return config;
    }

    public static void AddAdminCommand(string[] args)
    {
        if (Console.IsInputRedirected)
            Fail("--add-admin requires an interactive terminal.");

        var config = LoadConfigForManagement(args);

        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine("  FMO SAS — 管理员配置 / Admin Management");
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine();

        if (config.Server.Uid == 0 || string.IsNullOrEmpty(config.Server.Callsign) || string.IsNullOrEmpty(config.Server.CertFingerprint))
            Fail("  ✗ 服务器基础配置不完整（uid/callsign/certFingerprint），请先运行 sas.exe 完成首次配置。");

        var admins = config.Server.Admins.ToList();

        if (admins.Count == 0)
        {
            Console.WriteLine("  当前无管理员配置。");
            Console.WriteLine("  将自动添加服务器自身为默认 super 管理员:");
            Console.WriteLine();
            var fpDisp = config.Server.CertFingerprint.Length > 12
                ? config.Server.CertFingerprint[..12] + "..."
                : config.Server.CertFingerprint;
            Console.WriteLine("  ┌─ 默认 Super 管理员 ────────────────────────");
            Console.WriteLine($"  │  UID:         {config.Server.Uid} (server.uid)");
            Console.WriteLine($"  │  Fingerprint: {fpDisp} (server.certFingerprint)");
            Console.WriteLine("  │  Role:        super");
            Console.WriteLine("  └─────────────────────────────────────────────");
            Console.WriteLine();

            admins.Add(new AdminEntry
            {
                Uid = config.Server.Uid,
                CertFingerprint = config.Server.CertFingerprint,
                Role = "super"
            });
        }
        else
        {
            Console.WriteLine($"  当前管理员 ({admins.Count} 位):");
            PrintAdminList(admins);
            Console.WriteLine();
        }

        Console.WriteLine("  ── 添加新管理员 / Add New Admin ──");
        Console.WriteLine();

        var uid = PromptLong("  Admin UID / 管理员 UID", 0, required: true);

        var existing = admins.FirstOrDefault(a => a.Uid == uid);
        if (existing != null)
        {
            Console.WriteLine($"  ⚠ UID={uid} 已存在于管理员列表中 (Role={existing.Role})");
            var cont = PromptChoice("  是否仍要添加? / Continue?", ["Yes / 是", "No / 否"]);
            if (cont == 2)
            {
                Console.WriteLine("  已取消。");
                return;
            }
        }

        var fp = PromptCertFingerprint("  Cert Fingerprint / 证书指纹 (base64url, 43字符)", "");
        while (string.IsNullOrEmpty(fp))
        {
            Console.WriteLine("  证书指纹为必填项。");
            fp = PromptCertFingerprint("  Cert Fingerprint / 证书指纹 (base64url, 43字符)", "");
        }

        Console.WriteLine("  Role / 角色:");
        var roleIdx = PromptChoice("  选择角色", ["super", "admin"]);
        var role = roleIdx == 1 ? "super" : "admin";

        Console.WriteLine();
        Console.WriteLine("  ┌─ 确认 / Confirm ───────────────────────────");
        Console.WriteLine($"  │  UID:         {uid}");
        Console.WriteLine($"  │  Fingerprint: {(fp.Length > 12 ? fp[..12] + "..." : fp)}");
        Console.WriteLine($"  │  Role:        {role}");
        Console.WriteLine("  └─────────────────────────────────────────────");
        Console.WriteLine();

        var save = PromptChoice("  Save? / 保存?", ["Yes / 是", "No / 否"]);
        if (save == 2)
        {
            Console.WriteLine("  已取消。");
            return;
        }

        admins.Add(new AdminEntry { Uid = uid, CertFingerprint = fp, Role = role });
        config.Server.Admins = admins.ToArray();
        SaveToFile(config);
        Console.WriteLine($"  ✓ 已保存到 {ConfigPath}（当前共 {admins.Count} 位管理员）");
    }

    public static void RemoveAdminCommand(string[] args)
    {
        if (Console.IsInputRedirected)
            Fail("--remove-admin requires an interactive terminal.");

        var config = LoadConfigForManagement(args);

        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine("  FMO SAS — 删除管理员 / Remove Admin");
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine();

        var admins = config.Server.Admins.ToList();

        if (admins.Count == 0)
        {
            Console.WriteLine("  当前无管理员配置。");
            Console.WriteLine("  提示：运行时 SAS 会自动将服务器自身作为 super 管理员。");
            Console.WriteLine("  如需显式配置，请使用 --add-admin。");
            return;
        }

        Console.WriteLine($"  当前管理员 ({admins.Count} 位):");
        PrintAdminList(admins);
        Console.WriteLine();

        int idx;
        while (true)
        {
            Console.Write($"  删除哪个? / Remove which? [1-{admins.Count}]: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (int.TryParse(input, out idx) && idx >= 1 && idx <= admins.Count)
                break;
            Console.WriteLine($"  无效选择，请输入 1-{admins.Count}。");
        }

        var target = admins[idx - 1];
        Console.WriteLine();

        if (string.Equals(target.Role, "super", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  ⚠ 警告：即将删除 super 管理员 (UID={target.Uid})");
            Console.WriteLine("    删除后如需重新配置 super，请再次运行 --add-admin");
            var confirm = PromptChoice("  确认删除? / Confirm?", ["Yes / 是", "No / 否"]);
            if (confirm == 2)
            {
                Console.WriteLine("  已取消。");
                return;
            }
        }
        else
        {
            var confirm = PromptChoice($"  确认删除 UID={target.Uid} ({target.Role})?", ["Yes / 是", "No / 否"]);
            if (confirm == 2)
            {
                Console.WriteLine("  已取消。");
                return;
            }
        }

        admins.RemoveAt(idx - 1);
        config.Server.Admins = admins.ToArray();
        SaveToFile(config);

        Console.WriteLine();
        if (admins.Count == 0)
        {
            Console.WriteLine($"  ✓ 已删除 UID={target.Uid}，管理员列表现为空");
            Console.WriteLine("  提示：运行时 SAS 将自动回退为服务器自身作为 super 管理员");
        }
        else
        {
            Console.WriteLine($"  ✓ 已删除 UID={target.Uid}，当前共 {admins.Count} 位管理员");
        }
        Console.WriteLine($"  ✓ 已保存到 {ConfigPath}");
    }

    public static void ListAdminsCommand(string[] args)
    {
        var config = LoadConfigForManagement(args);

        var admins = config.Server.Admins.ToList();

        if (admins.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  管理员列表为空。");
            Console.WriteLine($"  运行时 SAS 自动将服务器自身 (UID={config.Server.Uid}) 作为 super 管理员。");
            Console.WriteLine("  使用 --add-admin 显式配置管理员。");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  管理员列表 / Admin List (共 {admins.Count} 位):");
        PrintAdminList(admins);
        Console.WriteLine();
        Console.WriteLine("  提示：使用 --add-admin 添加，--remove-admin 删除");
    }

    private static Config LoadConfigForManagement(string[] args)
    {
        var flatArgs = FlattenEqArgs(args);
        var explicitPath = ExtractConfigArg(flatArgs);

        ConfigPath = explicitPath
            ?? Path.Combine(HomeDir, ".sas", "config.json");

        if (!File.Exists(ConfigPath))
            Fail($"  ✗ 配置文件不存在: {ConfigPath}\n    请先运行 sas.exe 完成首次配置，或指定 --config <path>");

        var json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config == null)
            Fail($"  ✗ 配置文件解析失败: {ConfigPath}");

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

    private static Config LoadFromFile()
    {
        var json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config == null)
            Fail("Failed to parse config.json");

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
            Fail("No config file found and terminal is non-interactive.\n" +
                 "  Use CLI args to initialize: sas.exe --server-uid 12345 --server-callsign BG5ESN --mqtt-host ... --cert-fingerprint ...\n" +
                 "  Or in Docker: docker run ... --roots-dir /data/sas/roots");

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
            config.Server.Callsign = PromptString("  Server Callsign / 服务器呼号", config.Server.Callsign, required: true);
            Console.WriteLine();
            Console.WriteLine("  MQTT Host — EMQX Broker 的主机名或 IP 地址。");
            Console.WriteLine("  不带协议前缀，如 fmo.example.com 或 192.168.1.100。");
            config.Mqtt.Host = PromptString("  MQTT Host / Broker 地址", config.Mqtt.Host, required: true);
            Console.WriteLine();
            Console.WriteLine("  MQTT Port — EMQX Broker 的 MQTT 监听端口。");
            config.Mqtt.Port = PromptInt("  MQTT Port / Broker 端口", config.Mqtt.Port);

            Console.WriteLine();
            Console.WriteLine("  ┌─ HTTP 认证器参数 ──────────────────────────");
            Console.WriteLine("  └─────────────────────────────────────────────");
            Console.WriteLine();
            Console.WriteLine("  HTTP Auth Port — EMQX Broker 回调 SAS 的端口。");
            Console.WriteLine("  EMQX 在客户端 MQTT CONNECT 时向此端口发起");
            Console.WriteLine("  POST /auth 请求以完成鉴权。");
            config.Http.Port = PromptInt("  HTTP Auth Port / 认证回调端口", config.Http.Port);
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
                Console.WriteLine("  │");
                Console.WriteLine("  │  集群节点请编辑 config.json 中的 mqtt.clusters 数组");
                Console.WriteLine("  │  Cluster nodes: edit 'mqtt.clusters' in config.json");
                Console.WriteLine("  │");
                Console.WriteLine("  │  配置将保存至 / Saved to:");
                Console.WriteLine("  │  ~/.sas/config.json");
                Console.WriteLine("  └─────────────────────────────────────────────");
                Console.WriteLine();
                return config;
            }
            if (confirm == 3)
            {
                Console.WriteLine("Setup cancelled.");
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
                Console.WriteLine("  This field is required, please enter a value.");
                continue;
            }
            return result;
        }
    }

    private static int PromptInt(string label, int defaultValue, bool required = false)
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
                    Console.WriteLine("  This field is required, please enter a valid number.");
                    continue;
                }
                return defaultValue;
            }
            if (int.TryParse(input, out var val))
                return val;
            Console.WriteLine("  Invalid number, try again.");
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
                    Console.WriteLine("  This field is required, please enter a valid number.");
                    continue;
                }
                return defaultValue;
            }
            if (long.TryParse(input, out var val))
                return val;
            Console.WriteLine("  Invalid number, try again.");
        }
    }

    private static string PromptCertFingerprint(string label, string defaultValue)
    {
        while (true)
        {
            var display = string.IsNullOrEmpty(defaultValue) ? "" : $" [{defaultValue}]";
            Console.Write($"  {label}{display}: ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(input))
                return defaultValue;
            if (Regex.IsMatch(input, @"^[A-Za-z0-9_-]{43}$"))
                return input;
            Console.WriteLine("  Invalid format. Expected: 43-character base64url string (no padding).");
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
            Console.WriteLine("  Invalid choice, enter 1-4.");
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
            Console.WriteLine($"  Invalid choice, enter 1-{options.Length}.");
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
            Logger.Error("Missing required configuration:");
            foreach (var m in missing)
                Logger.Error(m);
            Logger.Error("");
            Logger.Error($"Fix: edit {ConfigPath} to fill in the missing fields, or run");
            Logger.Error("  sas.exe without arguments to start the interactive setup wizard.");
            Logger.Error("  Help: sas.exe --help");
            Environment.Exit(1);
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("FMO V4.0 Server Authorizer Service");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  sas.exe                                  Interactive config setup");
        Console.WriteLine("  sas.exe --server-uid ... --cert-fingerprint ...   CLI init");
        Console.WriteLine("  sas.exe --update                         Check and apply updates");
        Console.WriteLine("  sas.exe --add-admin [--config <path>]    Add admin interactively");
        Console.WriteLine("  sas.exe --remove-admin [--config <path>] Remove admin interactively");
        Console.WriteLine("  sas.exe --list-admins [--config <path>]  List current admins");
        Console.WriteLine("  sas.exe --help                           Show this help");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  --server-uid <long>                      FMO 设备唯一数字 ID");
        Console.WriteLine("  --server-callsign <str>                  业余电台呼号(大写), 须与 FMO 注册一致");
        Console.WriteLine("  --mqtt-host <str>                        EMQX Broker 主机名或 IP, 不带协议前缀");
        Console.WriteLine("  --cert-fingerprint <str>                 服务器证书 SHA-256 指纹 (base64url)");
        Console.WriteLine();
        Console.WriteLine("HTTP auth:");
        Console.WriteLine("  --http-port <int>                        EMQX 回调 POST /auth 的端口 (default: 8080)");
        Console.WriteLine("  --http-addr <str>                        HTTP 绑定地址 (default: 0.0.0.0)");
        Console.WriteLine("  --http-ttl <int>                         会话有效期秒数 (default: 14400 = 4h)");
        Console.WriteLine();
        Console.WriteLine("Optional:");
        Console.WriteLine("  --mqtt-port <int>                        Broker MQTT 端口 (default: 1883)");
        Console.WriteLine("  --allow-issuer-sn <str>                  Intermediate CA 白名单, 逗号分隔 (default: 全部允许)");
        Console.WriteLine("  --issuer-sn <long>                       服务器 Intermediate CA 序列号");
        Console.WriteLine("  --crl-refresh <int>                      证书吊销列表拉取间隔, 秒 (default: 14400 = 4h)");
        Console.WriteLine("  --roots-dir <path>                       Root CA 证书目录 (default: ~/.sas/roots)");
        Console.WriteLine("  --log-level <str>                        日志级别: Debug/Info/Warn/Error (default: Info)");
        Console.WriteLine();
        Console.WriteLine($"After first run, config is saved to {ConfigPath}");
        Console.WriteLine("and subsequent restarts require no arguments.");
    }

    private static void Fail(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }
}
