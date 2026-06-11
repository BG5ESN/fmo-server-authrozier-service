# FMO Server Authorizer Service

> [中文](README.md)

**v1.0.1** | .NET 10.0 | Ed25519 | CBOR | Self-contained Single Binary

---

FMO (FM Over Internet) Server Authorizer Service is a standalone device authenticator designed specifically for the amateur radio FMO digital communication network.

It implements fully offline, decentralized device identity verification through a three-layer certificate chain: **Root CA → Intermediate CA → User/Device Certificate**. No real-time authoritative center is required — every amateur radio enthusiast can securely deploy their own server, building a true zero-trust authentication model in the ham radio world.

```
Device ──MQTT CONNECT──▶ EMQX Broker ──POST /auth──▶ SAS
  (username+password)         HTTP Callback          Cert Chain Verify + ACL
```

---

## Why Do You Need It?

Traditional digital communication networks (such as D-Star, YSF, etc.) typically rely on centralized authentication servers to verify device identity. This creates several pain points:

| Pain Point | Description |
|------------|-------------|
| **Single Point of Failure** | Central service goes down → all users locked out |
| **Ongoing Maintenance** | Requires 24/7 dedicated ops for auth infrastructure |
| **External Dependencies** | Individuals or small groups struggle to build independent trusted networks |

FMO Server Authorizer Service shifts the trust anchor from "a central server on the network" down to "the root certificate in your hands", making decentralized secure authentication a reality.

## Core Concept: Three-Layer Certificate Chain

This service adopts the classic PKI (Public Key Infrastructure) trust model:

```
Root Certificate (Root CA)
 └─ Intermediate Certificate (Intermediate CA)
     └─ User/Device Certificate
```

- **Root Certificate** – The ultimate trust anchor, securely stored offline by the network creator, typically does not directly issue user certificates.
- **Intermediate Certificate** – Issued by the Root CA, used for day-to-day user and device management, can be periodically rotated or issued per subnet.
- **User/Device Certificate** – Issued by the Intermediate CA, bound to a specific radio device or operator callsign, used for identity proof on every connection.

The verification process is entirely offline: the authenticator only needs to load root certificates at startup, then independently validates any FMO device's legitimate identity by checking the certificate chain's signatures, validity periods, and revocation status — no online database queries needed.

## Zero-Trust Authentication Model

Here "zero-trust" means **"never trust, always verify"**:

- Don't trust network location (whether the request comes from intranet or public internet)
- Don't trust session state (no "authenticate once, valid forever" tokens)
- Every connection request must carry a valid device certificate, independently verified by this service

Only devices holding legitimate certificates can establish communication. Security is guaranteed even in partially untrusted network environments.

## Features

- Fully Offline Verification – No internet connection needed, pure local certificate chain validation
- Zero-Trust Architecture – Every connection is forcibly verified, unauthorized devices cannot connect
- Lightweight & Easy to Deploy – Single binary, simple configuration, suitable for embedded or lightweight servers
- Three-Layer PKI Chain – Root CA → Intermediate CA → User Cert
- Ed25519 High-Performance Signatures – Modern elliptic curve, secure and fast
- CBOR Compact Encoding – More compact than JSON, suitable for embedded scenarios
- CRL Revocation Lists – Certificate revocation checking with periodic refresh
- EMQX HTTP Auth Integration – Plug-and-play as EMQX Broker authentication callback
- OTA Auto-Update – Built-in version checking and automatic upgrades
- Cross-Platform – Windows / Linux / macOS (x64 & ARM64)

## Typical Deployment Scenarios

### Personal FMO Gateway

Generate your own root certificate and issue certificates for your home FMO devices. Only your own devices can connect.

### Club Shared Reflector

The club administrator holds the root certificate, issues intermediate and user certificates for members. Members connect to club resources using their certificates.

### Emergency Communication Field Network

When setting up a temporary emergency network, the field commander quickly generates a certificate hierarchy. All participating stations securely mesh using pre-provisioned certificates, with no dependence on external networks.

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10.0 (self-contained, no installation required) |
| Signature Algorithm | Ed25519 ([Chaos.NaCl](https://www.nuget.org/packages/Chaos.NaCl.Standard)) |
| Serialization | CBOR ([System.Formats.Cbor](https://www.nuget.org/packages/System.Formats.Cbor)) |
| HTTP Server | HttpListener (built-in) |
| Distribution | Single-file self-contained binary (PublishSingleFile) |

---

## Installation

### From Device Dashboard (Recommended)

The FMO device management dashboard provides one-click installation scripts with pre-configured parameters for your platform.

### Download Pre-built Binaries

Download for your platform from Releases:

| Platform | File | Notes |
|----------|------|-------|
| Windows x64 | `sas-win-x64.zip` | Extract and double-click `Sas.exe` |
| Linux x64 | `sas-linux-x64.tar.gz` | `tar xzf` then run `./Sas` |
| Linux ARM64 | `sas-linux-arm64.tar.gz` | Raspberry Pi, etc. |
| macOS Apple Silicon | `sas-osx-arm64.tar.gz` | M1/M2/M3/M4, run `./Sas` in terminal |
| macOS Intel | `sas-osx-x64.tar.gz` | Intel Mac |

No .NET installation required — extract and run.

### Build from Source

```bash
git clone https://code.xhh.ink/r/FMO/fmo-server-authorizer-service.git
cd fmo-server-authorizer-service

# Build all platforms (outputs to bin/)
./scripts/publish.ps1

# Build single platform (self-contained, no .NET runtime needed)
dotnet publish src/Sas.csproj -c Release -r <RID> --self-contained -p:PublishSingleFile=true -o out
```

Supported RIDs (Runtime Identifiers):

| RID | Platform |
|-----|----------|
| `win-x64` | Windows x64 |
| `linux-x64` | Linux x64 |
| `linux-arm64` | Linux ARM64 |
| `osx-arm64` | macOS Apple Silicon |
| `osx-x64` | macOS Intel |

---

## Quick Start

### Windows Desktop

```
Double-click Sas.exe → Interactive setup → Auto-start
```

Subsequent launches start directly without reconfiguration.

### Linux Command Line

```bash
# First run (writes config)
./Sas --server-uid 12345 --server-callsign BG5ESN \
      --mqtt-host your-mqtt-broker.com --cert-fingerprint gjJGc7...base64url...xY

# Subsequent runs (config persisted at ~/.sas/config.json)
./Sas
```

### Verify Service is Running

```bash
curl -X POST http://127.0.0.1:8080/auth \
  -H "Content-Type: application/json" \
  -d '{"username":"BG6VMZ","password":"...base64url..."}'
# → {"result":"deny"} (denied without valid cert = service is working)
```

---

## EMQX Configuration

Configure HTTP Password Authentication in EMQX Dashboard:

```
Auth Type: Password-Based
Auth Mechanism: HTTP
Method: POST
URL: http://<sas-host>:8080/auth
Headers: { "content-type": "application/json" }
Body: { "username": "${username}", "password": "${password}" }
```

---

## Configuration Reference

On first run, SAS writes configuration to `~/.sas/config.json` (Windows: `%USERPROFILE%\.sas\config.json`). This file is the single source of truth — no arguments needed on subsequent launches.

### config.json Structure

```json
{
  "server": {
    "uid": 0,
    "callsign": "",
    "issuerSn": 0,
    "certFingerprint": "",
    "admins": [{ "uid": 0, "certFingerprint": "", "role": "super" }]
  },
  "mqtt": { "host": "", "port": 1883 },
  "trust": { "allowIssuerSn": [], "rootsDir": "~/.sas/roots" },
  "crl": { "refreshSec": 14400 },
  "http": {
    "addr": "0.0.0.0",
    "port": 8080,
    "ttlSec": 14400,
    "responseTemplate": "emqx",
    "maxBodyBytes": 65536,
    "maxConcurrent": 128
  },
  "update": { "enabled": true },
  "log": { "level": "Info" }
}
```

### Required Fields

| Field | Description | Source |
|-------|-------------|--------|
| `server.uid` | Server unique ID | FMO management dashboard |
| `server.callsign` | Server callsign | Same, e.g. `BG5ESN` |
| `mqtt.host` | MQTT Broker address | Your Broker IP or domain |
| `server.certFingerprint` | Server cert SHA-256 fingerprint (base64url) | FMO dashboard "Certificate Fingerprint" |

### CLI Arguments

| Argument | Config Field | Default |
|----------|-------------|:-------:|
| `--server-uid` | `server.uid` | Required |
| `--server-callsign` | `server.callsign` | Required |
| `--mqtt-host` | `mqtt.host` | Required |
| `--cert-fingerprint` | `server.certFingerprint` | Required |
| `--mqtt-port` | `mqtt.port` | 1883 |
| `--http-port` | `http.port` | 8080 |
| `--http-addr` | `http.addr` | 0.0.0.0 |
| `--http-ttl` | `http.ttlSec` | 14400 |
| `--allow-issuer-sn` | `trust.allowIssuerSn` | All |
| `--roots-dir` | `trust.rootsDir` | `~/.sas/roots` |
| `--issuer-sn` | `server.issuerSn` | 0 |
| `--crl-refresh` | `crl.refreshSec` | 14400 |
| `--log-level` | `log.level` | Info |

---

## OTA Auto-Update

SAS has built-in version checking and auto-update:

```bash
# Manually check and update to the latest version
sas --update
```

- **Auto-check**: Automatically checks for new versions on startup, prompts upgrade command when available
- **Disable auto-check**: Set `"update": { "enabled": false }` in `config.json`
- **Docker environment**: Automatically detects Docker runtime, suggests `docker pull` update method

---

## Testing

The `test-case/` directory contains integration test tools and scripts:

| Tool/Script | Purpose |
|-------------|---------|
| `FingerprintTool/` | Certificate fingerprint calculation tool |
| `HttpChecker/` | HTTP auth request simulator |
| `run-test.ps1` | Positive tests (valid cert passes auth) |
| `run-test-negative.ps1` | Negative tests (invalid/expired certs rejected) |
| `run-user-sim.ps1` | User simulation test |
| `send-auth.ps1` | Send single auth request |

Run tests:

```powershell
cd test-case
./run-test.ps1
./run-test-negative.ps1
```

---

## Directory Structure

```
fmo-server-authorizer-service/
├── src/                            ← Source code
│   ├── Sas.csproj                  ← Project file (.NET 10.0)
│   ├── Program.cs                  ← Entry: CLI parsing + interactive config + startup
│   ├── Server/HttpServer.cs        ← HTTP listener + /auth endpoint
│   ├── Messages/                   ← HTTP auth payload DTOs
│   ├── Trust/                      ← Certificate verification + CRL management
│   │   ├── RootCaStore.cs          ← Root CA loading
│   │   ├── CertVerifier.cs         ← Certificate chain verification
│   │   └── CrlManager.cs           ← CRL download/cache/revocation check
│   ├── Auth/                       ← Auth processing + ACL
│   │   ├── HttpAuthHandler.cs      ← Core auth logic
│   │   ├── HttpProofVerifier.cs    ← Ed25519 signature verification
│   │   ├── AclStore.cs             ← Role permissions (super/admin/user)
│   │   └── EmqxResponseTemplate.cs ← EMQX response format
│   ├── certs/                      ← Cert data structures + Ed25519 + Base64Url
│   ├── Logging/Logger.cs           ← Logging module
│   └── builtin/                    ← Built-in data
│       ├── roots/bg5esn.json       ← Built-in Root CA
│       └── roles/                  ← Role permission definitions (super/admin/user)
├── scripts/
│   ├── publish.ps1                 ← Multi-platform build & package
│   ├── install.sh                  ← Linux/macOS install script
│   └── install.ps1                 ← Windows install script
├── config/
│   └── config.example.json         ← Example configuration
├── test-case/                      ← Integration tests
├── docs/                           ← Detailed documentation
├── Sas.sln                         ← Visual Studio solution
└── README.md
```

### Runtime Directory

```
~/.sas/
├── config.json            ← Configuration (single source of truth)
├── roots/                 ← Root CA certificates
│   └── bg5esn.json
├── roles/                 ← Role permission definitions
│   ├── super.json
│   ├── admin.json
│   └── user.json
└── crl/                   ← CRL cache (auto-refreshed)
    ├── 1/
    └── 1001/
```

---

## Troubleshooting

| Symptom | Check |
|---------|-------|
| Exits immediately after start | Run `sas --help` to verify arguments; check for missing required fields |
| `Roots directory not found` | Root CA not installed, verify built-in certs exist |
| `Root CA self-signature failed` | Certificate file corrupted, re-obtain |
| `curl` returns empty or connection refused | Check if port 8080 is blocked by firewall |
| CRL refresh 404 | Normal — CRL URL temporarily unavailable, doesn't affect service |
| Auth always returns deny | Check certificate chain completeness, expiration, or revocation |

---

## Important Notes

1. **No Sensitive Data**: All fields in `config.json` are public information (callsigns, certificate fingerprints, network addresses, etc.) — no private keys or passwords are stored. Security is guaranteed by the device-side private key.
2. **Internal Deployment**: HTTP endpoint should listen on `127.0.0.1` or internal addresses, never exposed directly to the public internet.
3. **Source of Truth**: After initial setup, modify configuration by editing `config.json` and restarting.
4. **Upgrades**: `sas --update` automatically downloads the latest version and restarts (disable: `update.enabled = false`).
5. **Device Side**: FMO device firmware must use SAS HTTP authenticator mode (carrying certificate info in username/password during MQTT CONNECT).

---

## Documentation

- [SAS HTTP Authentication](docs/V4.0%20SAS%20HTTP%20Authentication.md)
- [SAS Development Guide](docs/V4.0%20SAS.NET%20Development.md)
- [SAS OTA Update](docs/V4.0%20SAS%20OTA%20Update.md)
- [V4 Signatures & Certificates Protocol](docs/V4.0%20Protocol%20-%20Signatures%20%26%20Certificates.md)
- [V4 Root CRL Format](docs/V4.0%20Protocol%20Root%20CRL%20Formate.md)
- [V4 Intermediate CRL Format](docs/V4.0%20Protocol%20Intermediate%20CRL%20Formate.md)

---

## License

This project is licensed under the [GPL-3.0](LICENSE) License.
