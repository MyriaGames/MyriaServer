#!/usr/bin/env bash
# Updates this realm (and its bundled ./auth/MyriaAuthServer, if present) to the latest
# published release, without touching any existing SQLite databases or local production
# secrets. Safe to re-run any time; it only replaces application files.
#
# What it does:
#   1. Queries this repo's own GitHub Releases API (MyriaGames/MyriaServer) for the latest
#      release tag, then downloads that tag's MyriaServer_linux-x64_<version>.zip asset.
#   2. Downloads and extracts that zip into a temp staging directory.
#   3. Copies everything from staging over this directory EXCEPT:
#        - Storage/            (this realm's character/guild database)
#        - auth/Storage/       (the auth service's account database)
#        - appsettings.Production.json / auth/appsettings.Production.json (your real secrets —
#          these are excluded from the published zip in the first place, but the check is kept
#          here too in case that ever changes)
#        - certs/ / auth/certs/ (your TLS certificate/key — not part of the published zip either)
#        - logs/               (local log files, e.g. from a cron job redirect)
#   4. Restarts the systemd services (user services myria-auth + myria-realm, or a combined
#      myriarpg.service as user or system service); otherwise tells you to restart
#      run-production.sh yourself.
#
# Usage: ./update-production.sh [version]
#   No argument -> updates to the latest published release.
#   A version like "0.2.20" -> updates to that specific tag instead.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Previously this read a hand-maintained version.json from a separate rllyben/MyriaRPG-releases
# repo. Releases are now published directly on this repo's own GitHub Releases (matching the WPF
# client's UpdateService.cs, which made the same switch to MyriaGames/MyriaRPG) - the Releases API
# is the source of truth instead, no separate manifest file to keep in sync.
GITHUB_REPO="MyriaGames/MyriaServer"
REQUESTED_VERSION="${1:-}"

if [ -z "$REQUESTED_VERSION" ]; then
    echo "Checking latest release..."
    LATEST_JSON="$(curl -fsSL -H "Accept: application/vnd.github+json" \
        "https://api.github.com/repos/$GITHUB_REPO/releases/latest")" || {
        echo "ERROR: could not reach the GitHub Releases API for $GITHUB_REPO" >&2
        exit 1
    }
    # Tiny inline JSON field extraction (no jq dependency assumed) - only the top-level
    # "tag_name" field is needed; the asset URL is synthesized below from the known filename
    # convention rather than parsed out of the (nested, harder to grep reliably) assets array.
    TAG_NAME="$(echo "$LATEST_JSON" | grep -o '"tag_name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*:[[:space:]]*"([^"]*)"/\1/')"
    if [ -z "$TAG_NAME" ]; then
        echo "ERROR: could not parse the latest release's tag_name (got: $LATEST_JSON)" >&2
        exit 1
    fi
    TARGET_VERSION="${TAG_NAME#v}"
else
    TARGET_VERSION="$REQUESTED_VERSION"
    TAG_NAME="v$TARGET_VERSION"
fi

LINUX_URL="https://github.com/$GITHUB_REPO/releases/download/$TAG_NAME/MyriaServer_linux-x64_${TARGET_VERSION}.zip"

# MyriaServer.csproj's own <Version> isn't bumped in step with releases (releases are
# versioned off the WPF client instead), so it can't be used to detect what's installed here.
# This script stamps its own marker file after every successful update instead.
VERSION_MARKER=".installed_version"
CURRENT_VERSION="unknown"
[ -f "$VERSION_MARKER" ] && CURRENT_VERSION="$(cat "$VERSION_MARKER")"

echo "Currently installed: $CURRENT_VERSION"
echo "Target version:      $TARGET_VERSION"

if [ "$CURRENT_VERSION" = "$TARGET_VERSION" ]; then
    echo "Already up to date. Nothing to do (pass a version explicitly to force a re-install)."
    exit 0
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

ZIP_PATH="$WORKDIR/server.zip"
STAGE_DIR="$WORKDIR/stage"
mkdir -p "$STAGE_DIR"

echo "Downloading $LINUX_URL ..."
curl -fsSL -o "$ZIP_PATH" "$LINUX_URL"

echo "Extracting..."
unzip -q "$ZIP_PATH" -d "$STAGE_DIR"

echo "Applying update (Storage/, Production secrets, and certs/ are preserved)..."
rsync -a --delete \
    --exclude 'Storage' \
    --exclude 'Storage/***' \
    --exclude 'auth/Storage' \
    --exclude 'auth/Storage/***' \
    --exclude 'appsettings.Production.json' \
    --exclude 'auth/appsettings.Production.json' \
    --exclude 'certs' \
    --exclude 'certs/***' \
    --exclude 'auth/certs' \
    --exclude 'auth/certs/***' \
    --exclude '.installed_version' \
    --exclude 'auth/.installed_version' \
    --exclude 'logs' \
    --exclude 'logs/***' \
    "$STAGE_DIR"/ "$SCRIPT_DIR"/

chmod +x ./MyriaServer 2>/dev/null || true
chmod +x ./auth/MyriaAuthServer 2>/dev/null || true
chmod +x ./run-production.sh ./update-production.sh 2>/dev/null || true

echo "$TARGET_VERSION" > "$VERSION_MARKER"
# MyriaAuthServer runs with its own AppContext.BaseDirectory (./auth/), so it needs its own
# copy of the marker to log its version at startup - not just the top-level one.
[ -d "./auth" ] && echo "$TARGET_VERSION" > "./auth/$VERSION_MARKER"
echo "Updated to $TARGET_VERSION."

# Restart whichever service layout this host uses. Preferred: separate systemd *user* services
# for auth and realm (each restarted independently if it crashes; no sudo needed, so this also
# works from a timer/cron job). Older layouts with one combined myriarpg.service still work.
if systemctl --user is-enabled --quiet myria-realm.service 2>/dev/null; then
    echo "Restarting myria-auth.service + myria-realm.service (systemd user services)..."
    systemctl --user restart myria-auth.service myria-realm.service
    echo "Done. Check status with: systemctl --user status myria-auth myria-realm"
elif systemctl --user is-enabled --quiet myriarpg.service 2>/dev/null; then
    echo "Restarting myriarpg.service (systemd user service)..."
    systemctl --user restart myriarpg.service
    echo "Done. Check status with: systemctl --user status myriarpg.service"
elif systemctl is-enabled --quiet myriarpg.service 2>/dev/null; then
    echo "Restarting myriarpg.service (system service)..."
    sudo systemctl restart myriarpg.service
    echo "Done. Check status with: systemctl status myriarpg.service"
else
    echo "No Myria systemd service found - restart manually: stop the running ./run-production.sh and start it again."
fi
