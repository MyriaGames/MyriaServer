# Setup & Deployment Guide — Myria.Server.Realm

This guide walks through deploying **one realm** (this service, `MyriaServer`) together with
**the shared account service** (`MyriaAuthServer`) in Production. The two are typically deployed
together: the official release zip bundles `MyriaAuthServer` inside a realm's own package (see
[Layout](#layout) below), and `run-production.sh` in this repo starts both from that single
directory. If you're only interested in `MyriaAuthServer`'s own configuration reference (for a
standalone deployment, or to understand what it needs), see
[Myria.Server.Auth/SETUP.md](https://github.com/MyriaGames/MyriaAuthServer/blob/master/SETUP.md) —
this guide focuses on the combined, actually-used deployment path.

This is written for **Linux** (systemd), which is what the production tooling in this repo
(`run-production.sh`, `update-production.sh`) targets. A `run-production.ps1` also exists for
quick local Windows testing in Production mode — see [Windows note](#windows-note) at the end.

## Prerequisites

- A Linux host (any distro with systemd for the service-management steps; the published binary
  is self-contained, so no separate .NET runtime install is required).
- `openssl` (certificate generation), `curl` and `unzip` (used by `update-production.sh`),
  `rsync` (also used by `update-production.sh`).
- A way for players to reach the server: a public IP with open ports, or a tunneling service
  (the reference deployment uses [playit.gg](https://playit.gg) since it doesn't have a static
  public IP — see this repo's `Legal/Datenschutzerklaerung.md` §2.6 for how that's described to
  players).

## Layout

A published release (see [Getting the binaries](#getting-the-binaries) if you're not using a
prebuilt one) looks like this:

```
myria/
├── MyriaServer                 # this realm's self-contained binary
├── run-production.sh
├── update-production.sh
├── appsettings.json            # ships with placeholders — do not edit, override instead
├── Storage/                    # this realm's SQLite database (created on first run)
├── certs/                      # put your TLS certificate here (you create this)
│   └── myria.pfx
├── appsettings.Production.json # your real secrets (you create this — never commit it)
└── auth/
    ├── MyriaAuthServer         # the shared account service, bundled alongside this realm
    ├── appsettings.json
    ├── Storage/
    ├── certs/
    │   └── myria.pfx
    └── appsettings.Production.json
```

`run-production.sh` expects exactly this shape: `./MyriaServer` at the root, and
`./auth/MyriaAuthServer` in an `auth/` subfolder. If `./auth/MyriaAuthServer` isn't present, it
still starts this realm alone (with a warning) — useful if you're running `MyriaAuthServer`
separately or on another host.

## Getting the binaries

**Option A — download a prebuilt release (recommended):** grab the latest
`MyriaServer_linux-x64_<version>.zip` from the
[MyriaRPG-releases](https://github.com/rllyben/MyriaRPG-releases) GitHub Releases page and
extract it — it already has the `auth/` subfolder bundled in.

**Option B — build from source:**

```bash
# From this repo:
dotnet publish Myria.Server.Realm.csproj -c Release -r linux-x64 --self-contained true -o out/realm

# From a checkout of MyriaGames/MyriaAuthServer, published into an auth/ subfolder of the same output:
dotnet publish Myria.Server.Auth.csproj -c Release -r linux-x64 --self-contained true -o out/realm/auth
```

Copy `run-production.sh` and `update-production.sh` from this repo's root into `out/realm/` if
you built from source (they aren't part of `dotnet publish`'s output).

## 1. Generate a TLS certificate

Both services refuse to start in Production without HTTPS (see [Program.cs's startup
checks](Program.cs) — this is enforced, not optional). Without a domain, a self-signed
certificate is normal for this project (see `Legal/Datenschutzerklaerung.md` §2.7) — every Myria
client trusts it via trust-on-first-use, the same way SSH trusts a host key on first connect.

```bash
mkdir -p certs
openssl req -x509 -newkey rsa:2048 -sha256 -days 825 -nodes \
  -keyout /tmp/myria.key -out /tmp/myria.crt \
  -subj "/CN=your-domain-or-ip"
openssl pkcs12 -export -out certs/myria.pfx \
  -inkey /tmp/myria.key -in /tmp/myria.crt \
  -password pass:CHOOSE_A_PFX_PASSWORD
rm /tmp/myria.key /tmp/myria.crt
```

Replace `your-domain-or-ip` with whatever address players will actually connect to (a domain if
you have one, otherwise the IP or tunnel hostname). One certificate can be reused for both this
realm and `MyriaAuthServer` — copy `certs/myria.pfx` into `auth/certs/myria.pfx` too, or generate
a second one for `auth/` if you'd rather keep them independent.

## 2. Generate shared secrets

Three values must be **byte-identical** between this realm and `MyriaAuthServer` (see the
`_JwtSyncNote`/`_AdminSyncNote` comments in each service's `appsettings.json`) — if they drift,
every token this realm issues or validates will silently fail:

```bash
JWT_KEY=$(openssl rand -base64 48)
ADMIN_SECRET=$(openssl rand -base64 32)
PEPPER=$(openssl rand -base64 32)   # only used by MyriaAuthServer, generate it anyway now
echo "Jwt:Key           = $JWT_KEY"
echo "Admin:InternalSecret = $ADMIN_SECRET"
echo "Security:Pepper   = $PEPPER"
```

Save these somewhere safe (a password manager, not a chat log) — you'll paste them into both
services' config in the next step.

## 3. Configure secrets

Create `appsettings.Production.json` next to `MyriaServer` (**never** commit this file — it's
already gitignored):

```json
{
  "Jwt": {
    "Key": "<JWT_KEY from step 2>",
    "Issuer": "MyriaServer",
    "Audience": "MyriaClient",
    "ExpirationHours": 24
  },
  "Admin": {
    "InternalSecret": "<ADMIN_SECRET from step 2>"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=Storage/myria.db"
  },
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:5001",
        "Certificate": {
          "Path": "certs/myria.pfx",
          "Password": "<the pfx password from step 1>"
        }
      }
    }
  }
}
```

And `auth/appsettings.Production.json` (same `Jwt`/`Admin` values — this is the byte-identical
part):

```json
{
  "Jwt": {
    "Key": "<same JWT_KEY as above>",
    "Issuer": "MyriaServer",
    "Audience": "MyriaClient",
    "ExpirationHours": 24
  },
  "Security": {
    "Pepper": "<PEPPER from step 2>"
  },
  "Admin": {
    "InternalSecret": "<same ADMIN_SECRET as above>"
  },
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:5050",
        "Certificate": {
          "Path": "certs/myria.pfx",
          "Password": "<pfx password>"
        }
      }
    }
  },
  "Realms": [
    { "Id": "luria", "Name": "Luria", "Url": "https://your-domain-or-ip:5001" }
  ]
}
```

`Kestrel:Endpoints:Https:Url` binds `0.0.0.0` (all interfaces) rather than `localhost`, since
this is what actually needs to be reachable from outside the machine. The committed
`appsettings.json` only defines dev-mode `localhost` endpoints (in `appsettings.Development.json`,
which is *not* loaded in Production) — Production genuinely serves nothing over plain HTTP once
this file is in place (verified: with only an `Https` endpoint configured here, Production binds
**only** HTTPS, not HTTPS-plus-still-open-HTTP).

`Realms` is the list clients fetch via `GET /api/realms` to populate the realm-selection screen —
add one entry per realm you run, each with the address players actually connect to (not
`localhost`). This only needs to exist in `auth/appsettings.Production.json`; each realm itself
doesn't need to know about the others.

**Alternative: environment variables instead of a file.** Everything above can also be set via
`Section__Key`-style environment variables (e.g. `Jwt__Key`, `Admin__InternalSecret`,
`Kestrel__Endpoints__Https__Url`) if you'd rather not have a secrets file on disk at all — useful
for container/systemd `EnvironmentFile=` setups. The `Realms` array is awkward to express this
way (`Realms__0__Id`, `Realms__0__Name`, `Realms__0__Url`, `Realms__1__...`), so a file is usually
easier for that one setting specifically.

## 4. First run

```bash
chmod +x run-production.sh update-production.sh
./run-production.sh
```

You should see both services log their startup version banner and `Now listening on:
https://0.0.0.0:...` for each. `run-production.sh` locks down file permissions on the SQLite
databases and the `.pfx` (owner-read/write only) every time it starts, and stops
`MyriaAuthServer` cleanly if you interrupt it (Ctrl+C).

Verify from another machine (replace with your real address):

```bash
curl -k https://your-domain-or-ip:5050/api/realms
```

If you see your realm listed, both services are up and trusting each other's secrets correctly.
If a `Realms` entry you configured points at `http://` instead of `https://` (only relevant if
you deliberately haven't put a realm behind TLS yet), `MyriaAuthServer` will print an
impossible-to-miss red console warning at startup about the internal secret transiting in the
clear — that's intentional and described in this repo's `README.md` Configuration section, not a
bug.

## 5. Running as a systemd service

```ini
# /etc/systemd/system/myriarpg.service
[Unit]
Description=Myria RPG server (Realm + bundled Auth)
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/myria
ExecStart=/opt/myria/run-production.sh
Restart=on-failure
RestartSec=5
User=myria
Group=myria

[Install]
WantedBy=multi-user.target
```

The service **must** be named `myriarpg.service` — `update-production.sh` looks for exactly that
name to restart automatically after an update. Create a dedicated non-root user, make it the
owner of the deployment directory, then:

```bash
sudo useradd --system --no-create-home myria
sudo chown -R myria:myria /opt/myria
sudo systemctl daemon-reload
sudo systemctl enable --now myriarpg.service
sudo systemctl status myriarpg.service
```

## 6. Updating

```bash
./update-production.sh          # latest release
./update-production.sh 0.2.14   # a specific version
```

This downloads the latest (or specified) `linux-x64` release and replaces application files only
— `Storage/`, `auth/Storage/`, both `appsettings.Production.json` files, and both `certs/`
folders are preserved untouched. It restarts `myriarpg.service` automatically if that systemd
unit exists; otherwise it just tells you to restart `run-production.sh` yourself.

## Running multiple realms

Each additional realm is a **separate deployment directory** with its own port, its own
`Storage/` (own SQLite file), and its own `certs/` — but only **one** running
`MyriaAuthServer` for the whole cluster. For a second realm:

1. Publish/extract a fresh copy of this repo's binary into its own directory.
2. Give it a different `Kestrel:Endpoints:Https:Url` port and a different
   `ConnectionStrings:DefaultConnection` path in its own `appsettings.Production.json`.
3. Use the **same** `Jwt:Key`/`Admin:InternalSecret` as every other realm and `MyriaAuthServer`.
4. Don't start a second `MyriaAuthServer` — either omit `auth/MyriaAuthServer` from this realm's
   directory entirely, or don't start it if it's present (`run-production.sh` only warns, it
   doesn't require `auth/` to be absent).
5. Add this realm to the **one** running `MyriaAuthServer`'s `Realms` array in its
   `appsettings.Production.json`, then restart it so `GET /api/realms` reflects the new entry.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Service throws `Jwt:Key is still the dev placeholder (or unset)...` at startup | `appsettings.Production.json` (or the env vars) isn't being picked up — check `ASPNETCORE_ENVIRONMENT=Production` is actually set (it is, if launched via `run-production.sh`) and the file is in the right directory. |
| Service throws `No Kestrel:Endpoints:Https...configured` | Same as above, or the `Https` block is malformed/missing a key. |
| Clients can log in but every gameplay action on a realm fails / tokens rejected | `Jwt:Key`/`Issuer`/`Audience` don't match exactly between `MyriaAuthServer` and this realm — re-copy the value, don't retype it. |
| Account deletion/rename from a client fails with a server error | `Admin:InternalSecret` mismatch between `MyriaAuthServer` and this realm, or this realm isn't reachable at the URL configured in `MyriaAuthServer`'s `Realms` list. |
| `update-production.sh` says "No myriarpg.service found" | Expected if you're not using systemd — just stop and re-run `run-production.sh` yourself. |

## Windows note

For quick local testing of Production behavior (not for real hosting — Windows isn't the target
for `update-production.sh`/systemd), `run-production.ps1` runs this realm alone with
`ASPNETCORE_ENVIRONMENT=Production` set:

```powershell
.\run-production.ps1
```

You'd still need `appsettings.Production.json` in place as described above, and start
`MyriaAuthServer` separately (there's no bundled-auth equivalent of `run-production.sh` on
Windows).

## Security notes

This repo's `README.md` documents the exact Production configuration keys and the plain-HTTP
realm warning behavior in more detail. Character session ownership checks, globally-unique
character names, timing-safe password verification, and HTTPS-only enforcement in Production are
all already handled by the code — nothing further to configure for those.
