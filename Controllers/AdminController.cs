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
        IHubContext<GameHub> hub) : ControllerBase
    {
        private const string SecretHeader = "X-Internal-Secret";

        private bool IsAuthorized()
        {
            var expected = config["Admin:InternalSecret"];
            if (string.IsNullOrWhiteSpace(expected))
                return false;

            var provided = Request.Headers[SecretHeader].ToString();
            return !string.IsNullOrEmpty(provided) &&
                   System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                       System.Text.Encoding.UTF8.GetBytes(provided),
                       System.Text.Encoding.UTF8.GetBytes(expected));
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
                .Select(c => new { c.Name, c.Level, c.Class, c.Race, c.LastSaved, c.BannedUntil, c.BanReason })
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
                    RealmBanInfo.From(c.BannedUntil, c.BanReason, now))).ToList()));
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
