using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Hubs;
using Myria.Server.Realm.Models;
using Myria.Server.Realm.Models.Dto;
using Myria.Server.Realm.Services;

namespace Myria.Server.Realm.Controllers
{
    public class RenameUserRequest
    {
        public string OldUsername { get; set; } = string.Empty;
        public string NewUsername { get; set; } = string.Empty;
    }

    public class RevokeSessionsRequest
    {
        /// <summary>Unix seconds (auth server clock): tokens issued before this are rejected.</summary>
        public long TokensValidAfter { get; set; }
    }


    // Internal, service-to-service only — called by MyriaAuthServer (account delete/rename, GDPR
    // Art. 17) and by the localhost admin site (Myria.Server.Admin: bans, kicks, lookups), never by
    // game clients. Not [Authorize]'d against player JWTs: it authenticates via a shared secret
    // instead, since the caller here is another backend service, not a logged-in player.
    [ApiController]
    [Route("api/admin")]
    public class AdminController(
        AppDbContext db,
        IConfiguration config,
        BanService bans,
        CharacterPresenceService presence,
        SessionRevocationService revocations,
        IHubContext<GameHub> hub,
        CharacterSessionService session,
        Myria.Lib.Core.Repositories.ICharacterRepository repo,
        CharacterLocks locks) : ControllerBase
    {
        private const string SecretHeader = "X-Internal-Secret";

        // Two independent gates, both required - same as the auth server's InternalAdminOnlyAttribute:
        // 1. the caller is on this machine: loopback, or the peer address equals the address it
        //    connected to. Behind the same-host reverse proxy, RemoteIpAddress is the real visitor's
        //    IP (ForwardedHeaders, see Program.cs), so proxied requests never count - callers must
        //    use the realm's internal address (auth server / admin site: Realms[].InternalUrl).
        //    The secret alone used to be enough, from anywhere (security review 2026-11-25, A01 F4).
        // 2. the shared Admin:InternalSecret, compared in constant time.
        // Not rate limited: only this machine can get past gate 1, and a per-IP limit would just
        // throttle the operator's own admin site.
        public static bool IsOnThisMachine(System.Net.IPAddress? remote, System.Net.IPAddress? local) =>
            remote is not null &&
            (System.Net.IPAddress.IsLoopback(remote) || (local is not null && remote.Equals(local)));

        // Refusals and allowed changes are logged (security review 2026-11-26, A09 F2) - this API can
        // delete and rename accounts' characters, ban and kick.
        private bool IsAuthorized()
        {
            var log = SecurityLog.For(HttpContext);
            var call = $"{Request.Method}:{Request.Path}";
            if (!IsOnThisMachine(HttpContext.Connection.RemoteIpAddress, HttpContext.Connection.LocalIpAddress))
            {
                SecurityLog.Warn(log, "realm.admin.denied", null, "refused", $"not-on-this-machine;{call}");
                return false;
            }

            var expected = config["Admin:InternalSecret"];
            var provided = Request.Headers[SecretHeader].ToString();
            var ok = !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrEmpty(provided) &&
                     System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                         System.Text.Encoding.UTF8.GetBytes(provided),
                         System.Text.Encoding.UTF8.GetBytes(expected));
            if (!ok)
            {
                SecurityLog.Warn(log, "realm.admin.denied", null, "refused",
                    $"{(string.IsNullOrEmpty(provided) ? "no-secret" : "wrong-secret")};{call}");
                return false;
            }

            // Reads (lookups, ban lists) happen on every admin-site page view; only changes get a line.
            if (!HttpMethods.IsGet(Request.Method))
                SecurityLog.Info(log, "realm.admin.call", null, "allowed", call);
            return true;
        }

        // ── DELETE /api/admin/characters/{username} — purge all characters for a user ──
        // Deliberately leaves any AccountBan row alone: deleting the account (which a banned player
        // can do themselves via the auth service) must not be a way to shed the ban and re-register
        // the same username. It lapses on its own or an operator lifts it.

        [HttpDelete("characters/{username}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DeleteAllForUser(string username)
        {
            if (!IsAuthorized())
                return Forbid();

            var records = await db.Characters.Where(c => c.UserId == username).ToListAsync();
            if (records.Count > 0)
            {
                db.Characters.RemoveRange(records);
                await db.SaveChangesAsync();
            }

            return NoContent();
        }

        // ── PUT /api/admin/characters/rename — reassign all characters to a new username ──
        // Called by MyriaAuthServer when a user renames their account, since Character.UserId
        // is the plain username string, not a stable numeric id (see Character.cs).

        [HttpPut("characters/rename")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> RenameUser(RenameUserRequest req)
        {
            if (!IsAuthorized())
                return Forbid();
            if (string.IsNullOrWhiteSpace(req.OldUsername) || string.IsNullOrWhiteSpace(req.NewUsername))
                return BadRequest();

            // A character of this account in play: save it under the OLD name and end its session
            // first. Otherwise the live session later saved under the old name after its rows had
            // moved - the save failed and the progress since the last save was lost (security review
            // 2026-11-26, A10 F4). The connection is dropped after the rename (ForceLogout); the
            // client logs in again with the token issued for the new name.
            var liveConn = presence.GetConnectionByAccount(req.OldUsername);
            if (liveConn is not null && session.Get(liveConn) is { } live)
            {
                using (await locks.AcquireAsync(live.Name))
                {
                    await repo.SaveAsync(req.OldUsername, live);
                    session.Remove(liveConn);
                }
            }

            // One transaction: the stale-ban removal and the move used to be two separate saves.
            await using var tx = await db.Database.BeginTransactionAsync();
            var records = await db.Characters.Where(c => c.UserId == req.OldUsername).ToListAsync();
            foreach (var record in records)
                record.UserId = req.NewUsername;

            // The account ban follows the account: without this a banned player could just rename
            // themselves (which the auth service still allows) to walk out of it. A stale ban that
            // already sits on the new name (left behind by a deleted account) is replaced.
            var ban = await db.AccountBans.SingleOrDefaultAsync(b => b.Username == req.OldUsername);
            if (ban is not null)
            {
                var stale = await db.AccountBans.SingleOrDefaultAsync(b => b.Username == req.NewUsername);
                if (stale is not null)
                {
                    db.AccountBans.Remove(stale);
                    await db.SaveChangesAsync(); // unique index: the stale row has to go first
                }
                ban.Username = req.NewUsername;
            }

            if (records.Count > 0 || ban is not null)
                await db.SaveChangesAsync();
            await tx.CommitAsync();

            if (liveConn is not null)
                await KickAsync(req.OldUsername, "ForceLogout");
            return NoContent();
        }

        // ── Operator API (localhost admin site) ─────────────────────────────────

        [HttpGet("online")]
        public IActionResult Online() =>
            IsAuthorized()
                ? Ok(presence.GetOnline().Select(p => new OnlinePlayer(p.Account, p.Character)))
                : Forbid();

        [HttpGet("accounts/{username}")]
        public async Task<IActionResult> GetAccount(string username)
        {
            if (!IsAuthorized()) return Forbid();

            var now = DateTime.UtcNow;
            var rows = await db.Characters.AsNoTracking()
                .Where(c => c.UserId == username)
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    c.Name, c.Level, c.Class, c.Race, c.LastSaved, c.BannedUntil, c.BanReason,
                    Membership = db.GuildMembers.Where(m => m.CharacterId == c.Id)
                        .Select(m => new { m.Guild.Name, m.Rank }).FirstOrDefault()
                })
                .ToListAsync();
            var accountBan = await db.AccountBans.AsNoTracking().SingleOrDefaultAsync(b => b.Username == username);
            var onlineCharacter = presence.GetCharacterNameByAccount(username);

            return Ok(new RealmAccountInfo(
                username,
                presence.IsAccountOnline(username),
                onlineCharacter,
                accountBan is null ? null : RealmBanInfo.From(accountBan.BannedUntil, accountBan.Reason, now),
                rows.Select(c => new RealmCharacterInfo(
                    c.Name, username, c.Level, c.Class, c.Race, c.LastSaved,
                    string.Equals(c.Name, onlineCharacter, StringComparison.OrdinalIgnoreCase),
                    RealmBanInfo.From(c.BannedUntil, c.BanReason, now),
                    c.Membership is null ? null : $"{c.Membership.Name} ({c.Membership.Rank})")).ToList()));
        }

        [HttpGet("characters/search")]
        public async Task<IActionResult> SearchCharacters(string q, int take = 25)
        {
            if (!IsAuthorized()) return Forbid();
            if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<RealmCharacterInfo>());

            q = q.Trim();
            var now = DateTime.UtcNow;
            var rows = await db.Characters.AsNoTracking()
                .Where(c => c.Name.Contains(q))
                .OrderBy(c => c.Name)
                .Take(Math.Clamp(take, 1, 100))
                .Select(c => new { c.Name, c.UserId, c.Level, c.Class, c.Race, c.LastSaved, c.BannedUntil, c.BanReason })
                .ToListAsync();

            return Ok(rows.Select(c => new RealmCharacterInfo(
                c.Name, c.UserId, c.Level, c.Class, c.Race, c.LastSaved,
                presence.IsCharacterOnline(c.Name),
                RealmBanInfo.From(c.BannedUntil, c.BanReason, now))));
        }

        [HttpGet("bans")]
        public async Task<IActionResult> ActiveBans()
        {
            if (!IsAuthorized()) return Forbid();

            var now = DateTime.UtcNow;
            var accounts = await db.AccountBans.AsNoTracking()
                .Where(b => b.BannedUntil > now).OrderBy(b => b.Username).ToListAsync();
            var characters = await db.Characters.AsNoTracking()
                .Where(c => c.BannedUntil > now).OrderBy(c => c.Name)
                .Select(c => new { c.Name, c.UserId, c.BannedUntil, c.BanReason }).ToListAsync();

            return Ok(new RealmBansInfo(
                accounts.Select(b => new AccountBanInfo(
                    b.Username, new RealmBanInfo(b.BannedUntil, Bans.IsPermanent(b.BannedUntil), b.Reason))).ToList(),
                characters.Select(c => new CharacterBanInfo(
                    c.Name, c.UserId, new RealmBanInfo(c.BannedUntil!.Value, Bans.IsPermanent(c.BannedUntil.Value), c.BanReason))).ToList()));
        }

        // Bans the whole account from this realm and kicks it if online. The auth service is not
        // involved: the account can still log in there and manage itself.
        [HttpPut("accounts/{username}/ban")]
        public async Task<IActionResult> BanAccount(string username, BanRequest req)
        {
            if (!IsAuthorized()) return Forbid();

            var ban = await bans.BanAccountAsync(username, req.Minutes, req.Reason);
            var kicked = await KickAsync(username, "AccountBanned",
                Bans.IsPermanent(ban.Until) ? (DateTime?)null : ban.Until, ban.Reason);
            return Ok(new { ban = new RealmBanInfo(ban.Until, Bans.IsPermanent(ban.Until), ban.Reason), kicked });
        }

        [HttpDelete("accounts/{username}/ban")]
        public async Task<IActionResult> LiftAccountBan(string username) =>
            !IsAuthorized() ? Forbid() : await bans.LiftAccountBanAsync(username) ? NoContent() : NotFound();

        // Bans one character; the account's other characters stay playable. If that character is
        // the one currently loaded, the connection is dropped (the player can reconnect on another).
        [HttpPut("characters/{name}/ban")]
        public async Task<IActionResult> BanCharacter(string name, BanRequest req)
        {
            if (!IsAuthorized()) return Forbid();

            if (await bans.BanCharacterAsync(name, req.Minutes, req.Reason) is not var (username, ban))
                return NotFound();

            var kicked = false;
            if (string.Equals(presence.GetCharacterNameByAccount(username), name, StringComparison.OrdinalIgnoreCase))
                kicked = await KickAsync(username, "CharacterBanned", name,
                    Bans.IsPermanent(ban.Until) ? (DateTime?)null : ban.Until, ban.Reason);

            return Ok(new { ban = new RealmBanInfo(ban.Until, Bans.IsPermanent(ban.Until), ban.Reason), kicked });
        }

        [HttpDelete("characters/{name}/ban")]
        public async Task<IActionResult> LiftCharacterBan(string name) =>
            !IsAuthorized() ? Forbid() : await bans.LiftCharacterBanAsync(name) is null ? NotFound() : NoContent();

        // ── POST /api/admin/accounts/{username}/revoke-sessions ──
        // Called by the auth server after a password change/reset or a forced password change: every
        // token of the account issued before TokensValidAfter (auth server's clock, Unix seconds) stops
        // working here, and the live game connection - which may belong to whoever had the old
        // password - is dropped. The owner's own client isn't connected at that moment (the password is
        // changed from the menu or the website), and reconnects with the fresh token it got.
        [HttpPost("accounts/{username}/revoke-sessions")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> RevokeSessions(string username, RevokeSessionsRequest req)
        {
            if (!IsAuthorized())
                return Forbid();

            // Not in the future beyond normal clock drift: a bogus far-future value would lock the
            // account out of this realm until the row expires.
            if (req.TokensValidAfter <= 0 || req.TokensValidAfter > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                return BadRequest();
            var validAfter = DateTimeOffset.FromUnixTimeSeconds(req.TokensValidAfter).UtcDateTime;
            if (string.IsNullOrWhiteSpace(username) || validAfter > DateTime.UtcNow.AddMinutes(5))
                return BadRequest();

            await revocations.RevokeAsync(username, validAfter);
            return Ok(new { kicked = await KickAsync(username, "ForceLogout") });
        }

        [HttpPost("accounts/{username}/kick")]
        public async Task<IActionResult> Kick(string username) =>
            !IsAuthorized() ? Forbid() : Ok(new { kicked = await KickAsync(username, "ForceLogout") });

        // Same mechanism GameHub.OnConnectedAsync uses to drop a stale duplicate connection: tell the
        // client why, then abort the transport. The hub's OnDisconnectedAsync still auto-saves the
        // character, so a kick never loses progress.
        private async Task<bool> KickAsync(string username, string clientEvent, params object?[] args)
        {
            var conn = presence.GetConnectionByAccount(username);
            if (conn is null) return false;

            await hub.Clients.Client(conn).SendCoreAsync(clientEvent, args);
            await Task.Delay(100); // let the message flush before the transport is torn down
            presence.GetContext(conn)?.Abort();
            return true;
        }
    }
}
