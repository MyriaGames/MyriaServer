#!/usr/bin/env bash
# Runs MyriaAuthServer and MyriaServer (this realm) together in Production mode so both
# pick up their respective appsettings.Production.json (real DB paths, real secrets, the
# 0.0.0.0 Kestrel bind, and — for MyriaServer — the real Realms URLs).
#
# Layout expected in this directory:
#   ./MyriaServer            (this realm's self-contained binary, published at the zip root)
#   ./auth/MyriaAuthServer   (the shared auth service, published into an "auth" subfolder)
#
# Usage: ./run-production.sh
# To keep it running after you log out / survive reboots, either run it inside a
# `screen`/`tmux` session, or set it up as a systemd service.

cd "$(dirname "$0")"

# SQLite files hold password hashes (auth) / character data (realm) — make sure anything
# either process creates from here on is owner-only, and lock down anything already there
# from a previous run (in case the process/umask wasn't restrictive before).
umask 077

mkdir -p Storage
chmod +x ./MyriaServer 2>/dev/null
chmod 600 Storage/*.db 2>/dev/null
# TLS private key lives in the .pfx — lock it down the same way. Place it here yourself
# before the first run (see Myria.Server.Realm/appsettings.Production.json's Kestrel:
# Endpoints:Https:Certificate:Path) — it's never part of the published zip.
chmod 600 certs/*.pfx 2>/dev/null

export ASPNETCORE_ENVIRONMENT=Production

AUTH_PID=""
if [ -x "./auth/MyriaAuthServer" ] || [ -f "./auth/MyriaAuthServer" ]; then
    mkdir -p ./auth/Storage
    chmod +x ./auth/MyriaAuthServer 2>/dev/null
    chmod 600 ./auth/Storage/*.db 2>/dev/null
    chmod 600 ./auth/certs/*.pfx 2>/dev/null
    ( cd ./auth && ASPNETCORE_ENVIRONMENT=Production ./MyriaAuthServer ) &
    AUTH_PID=$!
    echo "MyriaAuthServer started (pid $AUTH_PID)"
else
    echo "WARNING: ./auth/MyriaAuthServer not found — starting MyriaServer only." \
         "Realms won't be able to validate JWTs from a running auth service unless" \
         "it's started separately (or bundled here at ./auth/)." >&2
fi

cleanup() {
    if [ -n "$AUTH_PID" ] && kill -0 "$AUTH_PID" 2>/dev/null; then
        echo "Stopping MyriaAuthServer (pid $AUTH_PID)..."
        kill "$AUTH_PID" 2>/dev/null
        wait "$AUTH_PID" 2>/dev/null
    fi
}
trap cleanup EXIT INT TERM

./MyriaServer
