#!/usr/bin/env bash
# Updates this realm (and its bundled ./auth/MyriaAuthServer, if present) to the latest
# published release, without touching any existing SQLite databases or local production
# secrets. Safe to re-run any time; it only replaces application files.
#
# What it does:
#   1. Reads the latest version from the Myria release server
#      (https://myria.myria-games.duckdns.org/downloads/latest.json, or $MYRIA_RELEASES_URL),
#      then downloads that version's MyriaServer_linux-x64_<version>.zip.
#   2. Verifies the zip against the release's SHA256SUMS and extracts it into a temp staging
#      directory.
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
#   No argument -> updates to the latest published release (skipped if already installed).
#   A version like "0.2.20" -> installs that specific release, even if it's already installed.
#
# Environment:
#   MYRIA_RELEASES_URL  release server base URL (default: the official Myria server). Set it on
#                       the release server itself if it can't reach its own public address
#                       (e.g. https://myria.myria-games.duckdns.org:8443/downloads behind NAT).
#   MYRIA_UPDATE_NO_RESTART  set (any value) to skip the service restart - for testing in a copy.
#
# Release signature: once ./release-signers exists (installed by the first signed release), every
# update must come with SHA256SUMS.sig made by that key, or nothing is installed.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Releases are published by Setup/release.ps1 to the game's own server (no longer GitHub):
#   $RELEASES_URL/latest.json                newest release ("version" is its first field)
#   $RELEASES_URL/<version>/<files>          release files
#   $RELEASES_URL/<version>/SHA256SUMS       checksums of those files (sha256sum format)
RELEASES_URL="${MYRIA_RELEASES_URL:-https://myria.myria-games.duckdns.org/downloads}"
RELEASES_URL="${RELEASES_URL%/}"
REQUESTED_VERSION="${1:-}"

if [ -z "$REQUESTED_VERSION" ]; then
    echo "Checking latest release at $RELEASES_URL ..."
    LATEST_JSON="$(curl -fsSL "$RELEASES_URL/latest.json")" || {
        echo "ERROR: could not read $RELEASES_URL/latest.json" >&2
        exit 1
    }
    # Tiny inline JSON field extraction (no jq dependency assumed): "version" is the manifest's
    # first top-level field and appears nowhere else in it.
    TARGET_VERSION="$(echo "$LATEST_JSON" | grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*:[[:space:]]*"([^"]*)"/\1/')"
else
    TARGET_VERSION="${REQUESTED_VERSION#v}"
fi
if ! [[ "$TARGET_VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
    echo "ERROR: invalid release version '$TARGET_VERSION'" >&2
    exit 1
fi

ZIP_NAME="MyriaServer_linux-x64_${TARGET_VERSION}.zip"
LINUX_URL="$RELEASES_URL/$TARGET_VERSION/$ZIP_NAME"
SUMS_URL="$RELEASES_URL/$TARGET_VERSION/SHA256SUMS"

# MyriaServer.csproj's own <Version> isn't bumped in step with releases (releases are
# versioned off the WPF client instead), so it can't be used to detect what's installed here.
# This script stamps its own marker file after every successful update instead.
VERSION_MARKER=".installed_version"
CURRENT_VERSION="unknown"
[ -f "$VERSION_MARKER" ] && CURRENT_VERSION="$(cat "$VERSION_MARKER")"

echo "Currently installed: $CURRENT_VERSION"
echo "Target version:      $TARGET_VERSION"

if [ "$CURRENT_VERSION" = "$TARGET_VERSION" ] && [ -z "$REQUESTED_VERSION" ]; then
    echo "Already up to date. Nothing to do (pass the version explicitly to re-install it)."
    exit 0
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

ZIP_PATH="$WORKDIR/$ZIP_NAME"
STAGE_DIR="$WORKDIR/stage"
mkdir -p "$STAGE_DIR"

echo "Downloading $LINUX_URL ..."
curl -fsSL -o "$ZIP_PATH" "$LINUX_URL"
curl -fsSL -o "$WORKDIR/SHA256SUMS" "$SUMS_URL"

# Who made this release? SHA256SUMS comes from the same server as the zip, so on its own it only
# proves the download isn't corrupted. Releases are signed on the developer's PC (release.ps1,
# `ssh-keygen -Y sign`, namespace myria-release); the public key is ./release-signers, installed
# with the first signed release and never replaced by an update (security review 2026-11-26, A08 F2).
SIGNERS="$SCRIPT_DIR/release-signers"
if [ -f "$SIGNERS" ]; then
    echo "Verifying release signature..."
    curl -fsSL -o "$WORKDIR/SHA256SUMS.sig" "$SUMS_URL.sig" || {
        echo "ERROR: release $TARGET_VERSION has no signature (SHA256SUMS.sig) - not installing" >&2
        exit 1
    }
    ssh-keygen -Y verify -f "$SIGNERS" -I myria-release -n myria-release \
        -s "$WORKDIR/SHA256SUMS.sig" < "$WORKDIR/SHA256SUMS" > /dev/null || {
        echo "ERROR: SHA256SUMS is not signed by the trusted release key ($SIGNERS) - not installing" >&2
        exit 1
    }
    echo "Release signature OK."
else
    echo "WARNING: no $SIGNERS installed yet - the release signature can't be checked this time." >&2
    echo "         The key that comes with this release will be trusted for every later update." >&2
fi

echo "Verifying checksum..."
grep -E "^[0-9a-f]{64}  $ZIP_NAME\$" "$WORKDIR/SHA256SUMS" > "$WORKDIR/zip.sha256" || {
    echo "ERROR: $ZIP_NAME is not listed in SHA256SUMS" >&2
    exit 1
}
( cd "$WORKDIR" && sha256sum -c --strict zip.sha256 ) || {
    echo "ERROR: checksum mismatch for $ZIP_NAME - not installing" >&2
    exit 1
}

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
    --exclude 'release-signers' \
    "$STAGE_DIR"/ "$SCRIPT_DIR"/

# Trust on first install: the release key is taken from the first release that brings one and then
# kept - an update can neither replace nor delete it (rsync excludes it above). To change the key,
# replace ./release-signers by hand.
if [ ! -f "$SIGNERS" ] && [ -f "$STAGE_DIR/release-signers" ]; then
    cp "$STAGE_DIR/release-signers" "$SIGNERS"
    echo "Installed the release signing key ($SIGNERS)."
fi

chmod +x ./MyriaServer 2>/dev/null || true
chmod +x ./auth/MyriaAuthServer 2>/dev/null || true
chmod +x ./run-production.sh ./update-production.sh 2>/dev/null || true

echo "$TARGET_VERSION" > "$VERSION_MARKER"
# MyriaAuthServer runs with its own AppContext.BaseDirectory (./auth/), so it needs its own
# copy of the marker to log its version at startup - not just the top-level one.
[ -d "./auth" ] && echo "$TARGET_VERSION" > "./auth/$VERSION_MARKER"
echo "Updated to $TARGET_VERSION."

# For testing the download/verify/apply steps in a scratch copy without touching running services.
if [ -n "${MYRIA_UPDATE_NO_RESTART:-}" ]; then
    echo "(test mode: services not restarted)"
    exit 0
fi

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
