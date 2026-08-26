using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Services.Manager;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;
using GameCharacter = Myria.Lib.Core.Entities.Characters.Character;

namespace Myria.Server.Realm.Services
{
    /// <summary>
    /// Handles the two rookie mechanics: XP bonus and NPC purchase commission.
    /// All methods are no-ops when the character is not a guild rookie.
    /// </summary>
    public class RookieService(AppDbContext db, GuildConfig config)
    {
        // Resolves character name → DB ID → rookie guild ID (null if not a rookie).
        private async Task<int?> GetRookieGuildIdAsync(string characterName)
        {
            var charId = await db.Characters
                .Where(c => c.Name == characterName)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();

            if (charId == 0) return null;

            return await db.GuildRookies
                .Where(r => r.CharacterId == charId)
                .Select(r => (int?)r.GuildId)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// Applies a configured percentage of <paramref name="baseXp"/> as bonus character
        /// XP if the player is a guild rookie. The bonus is applied directly to the in-memory
        /// Character object via <see cref="Character.GainXp"/>.
        /// Returns the bonus amount (0 when not applicable).
        /// </summary>
        public async Task<long> ApplyXpBonusAsync(GameCharacter player, long baseXp)
        {
            if (baseXp <= 0) return 0;

            var guildId = await GetRookieGuildIdAsync(player.Name);
            if (guildId is null) return 0;

            long bonus = (long)(baseXp * config.RookieXpBoostPct);
            if (bonus <= 0) return 0;

            player.GainXp(bonus);
            return bonus;
        }

        /// <summary>
        /// Applies a configured percentage of <paramref name="baseJobXp"/> as bonus job XP
        /// if the player is a guild rookie. Uses <see cref="JobManager.GrantSkillXp"/>.
        /// Returns the bonus amount (0 when not applicable).
        /// </summary>
        public async Task<long> ApplyJobXpBonusAsync(GameCharacter player, string jobId, long baseJobXp)
        {
            if (baseJobXp <= 0 || string.IsNullOrEmpty(jobId)) return 0;

            var guildId = await GetRookieGuildIdAsync(player.Name);
            if (guildId is null) return 0;

            long bonus = (long)(baseJobXp * config.RookieXpBoostPct);
            if (bonus <= 0) return 0;

            JobManager.GrantSkillXp(player, jobId, bonus);
            return bonus;
        }

        /// <summary>
        /// Credits the rookie's guild treasury with a commission percentage of
        /// <paramref name="goldSpent"/>. No-op when the character is not a rookie.
        /// </summary>
        public async Task ApplyPurchaseCommissionAsync(GameCharacter player, long goldSpent)
        {
            if (goldSpent <= 0) return;

            var guildId = await GetRookieGuildIdAsync(player.Name);
            if (guildId is null) return;

            long commission = (long)(goldSpent * config.RookieCommissionPct);
            if (commission <= 0) return;

            var treasury = await db.GuildTreasuries.FindAsync(guildId.Value);
            if (treasury is null) return;

            treasury.Balance += commission;
            await db.SaveChangesAsync();
        }
    }
}
