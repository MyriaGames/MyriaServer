using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;

namespace Myria.Server.Realm.Services
{
    public record ActiveBan(DateTime Until, string? Reason);

    // Single place that answers "may this account/character play right now?" and records operator
    // bans. Deliberately uncached: it's one indexed lookup on a tiny table, and a ban has to bite
    // on the very next request/connect with no invalidation to get wrong.
    public class BanService(AppDbContext db)
    {
        public async Task<ActiveBan?> GetActiveAccountBanAsync(string username)
        {
            var now = DateTime.UtcNow;
            var ban = await db.AccountBans.AsNoTracking()
                .Where(b => b.Username == username && b.BannedUntil > now)
                .Select(b => new ActiveBan(b.BannedUntil, b.Reason))
                .FirstOrDefaultAsync();
            return ban;
        }

        public async Task<ActiveBan?> GetActiveCharacterBanAsync(string username, string characterName)
        {
            var now = DateTime.UtcNow;
            var row = await db.Characters.AsNoTracking()
                .Where(c => c.UserId == username && c.Name == characterName && c.BannedUntil > now)
                .Select(c => new { c.BannedUntil, c.BanReason })
                .FirstOrDefaultAsync();
            return row is null ? null : new ActiveBan(row.BannedUntil!.Value, row.BanReason);
        }

        /// <summary>Creates or replaces (extends/shortens) the account's ban.</summary>
        public async Task<ActiveBan> BanAccountAsync(string username, int? minutes, string? reason)
        {
            var until = Bans.UntilFromMinutes(minutes, DateTime.UtcNow);
            var ban = await db.AccountBans.SingleOrDefaultAsync(b => b.Username == username);
            if (ban is null)
                db.AccountBans.Add(ban = new AccountBan { Username = username });

            ban.BannedUntil = until;
            ban.Reason = Trim(reason);
            await db.SaveChangesAsync();
            return new ActiveBan(until, ban.Reason);
        }

        public async Task<bool> LiftAccountBanAsync(string username)
        {
            var ban = await db.AccountBans.SingleOrDefaultAsync(b => b.Username == username);
            if (ban is null) return false;
            db.AccountBans.Remove(ban);
            await db.SaveChangesAsync();
            return true;
        }

        /// <summary>Null when no such character exists.</summary>
        public async Task<(string Username, ActiveBan Ban)?> BanCharacterAsync(string characterName, int? minutes, string? reason)
        {
            var c = await db.Characters.SingleOrDefaultAsync(x => x.Name == characterName);
            if (c is null) return null;

            c.BannedUntil = Bans.UntilFromMinutes(minutes, DateTime.UtcNow);
            c.BanReason = Trim(reason);
            await db.SaveChangesAsync();
            return (c.UserId, new ActiveBan(c.BannedUntil.Value, c.BanReason));
        }

        /// <summary>Returns the owning username, or null when no such character exists.</summary>
        public async Task<string?> LiftCharacterBanAsync(string characterName)
        {
            var c = await db.Characters.SingleOrDefaultAsync(x => x.Name == characterName);
            if (c is null) return null;

            c.BannedUntil = null;
            c.BanReason = null;
            await db.SaveChangesAsync();
            return c.UserId;
        }

        private static string? Trim(string? reason)
        {
            reason = reason?.Trim();
            if (string.IsNullOrEmpty(reason)) return null;
            return reason.Length > 500 ? reason[..500] : reason;
        }
    }
}
