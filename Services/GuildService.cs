using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;

namespace Myria.Server.Realm.Services
{
    public class GuildService(AppDbContext db)
    {
        public const int MaxMembers = 50;
        public const int MaxRookies = 50;

        // ── Helpers ───────────────────────────────────────────────────────────────

        private async Task<bool> IsInGuildOrRookieAsync(int characterId) =>
            await db.GuildMembers.AnyAsync(m => m.CharacterId == characterId) ||
            await db.GuildRookies.AnyAsync(r => r.CharacterId == characterId);

        private async Task<GuildMember?> GetMembershipAsync(int characterId) =>
            await db.GuildMembers.FirstOrDefaultAsync(m => m.CharacterId == characterId);

        private async Task<GuildRookie?> GetRookieMembershipAsync(int characterId) =>
            await db.GuildRookies.FirstOrDefaultAsync(r => r.CharacterId == characterId);

        public static string GuildGroup(int guildId) => $"guild_{guildId}";

        // ── Guild lifecycle ───────────────────────────────────────────────────────

        /// <summary>Creates a new guild. Character must not already be in any guild or rookie program.</summary>
        public async Task<(bool success, string error, Guild? guild)> CreateGuildAsync(
            int characterId, string name, string tag, string? description)
        {
            if (await IsInGuildOrRookieAsync(characterId))
                return (false, "You are already in a guild.", null);

            var normalizedTag = tag.ToUpperInvariant();

            if (await db.Guilds.AnyAsync(g => g.Name == name))
                return (false, "A guild with that name already exists.", null);

            if (await db.Guilds.AnyAsync(g => g.Tag == normalizedTag))
                return (false, "A guild with that tag already exists.", null);

            var guild = new Guild
            {
                Name              = name,
                Tag               = normalizedTag,
                Description       = description,
                LeaderCharacterId = characterId,
                CreatedAt         = DateTime.UtcNow,
            };
            db.Guilds.Add(guild);
            await db.SaveChangesAsync();

            db.GuildSettings.Add(new GuildSettings   { GuildId = guild.Id });
            db.GuildTreasuries.Add(new GuildTreasury { GuildId = guild.Id });
            db.GuildMembers.Add(new GuildMember
            {
                GuildId     = guild.Id,
                CharacterId = characterId,
                Rank        = GuildRank.Leader,
                JoinedAt    = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            return (true, "", guild);
        }

        /// <summary>
        /// Disbands the guild. Returns the member and rookie character IDs so the hub can
        /// notify all of them before the records are gone.
        /// </summary>
        public async Task<(bool success, string error, int guildId, IReadOnlyList<int> allCharIds)> DisbandGuildAsync(
            int characterId)
        {
            var membership = await GetMembershipAsync(characterId);
            if (membership is null || membership.Rank != GuildRank.Leader)
                return (false, "Only the guild leader can disband the guild.", 0, []);

            var guildId = membership.GuildId;

            var memberIds = await db.GuildMembers
                .Where(m => m.GuildId == guildId)
                .Select(m => m.CharacterId)
                .ToListAsync();

            var rookieIds = await db.GuildRookies
                .Where(r => r.GuildId == guildId)
                .Select(r => r.CharacterId)
                .ToListAsync();

            var guild = await db.Guilds.FindAsync(guildId);
            if (guild is null) return (false, "Guild not found.", 0, []);

            db.Guilds.Remove(guild);
            await db.SaveChangesAsync();

            return (true, "", guildId, memberIds.Concat(rookieIds).ToList());
        }

        // ── Invites ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends an invite to a character. <paramref name="asRookie"/> determines whether
        /// it is a rookie hire invite or a full-member invite.
        /// Minimum rank to invite as member: Officer. Minimum rank to hire as rookie: Elite.
        /// </summary>
        public async Task<(bool success, string error, int inviteId, int inviteeCharId, int guildId)> InviteAsync(
            int inviterCharId, string inviteeCharName, bool asRookie)
        {
            var membership = await GetMembershipAsync(inviterCharId);
            if (membership is null)
                return (false, "You are not in a guild.", 0, 0, 0);

            var minRank = asRookie ? GuildRank.Elite : GuildRank.Officer;
            if (membership.Rank < minRank)
                return (false, $"You need at least {minRank} rank to do this.", 0, 0, 0);

            var invitee = await db.Characters.FirstOrDefaultAsync(c => c.Name == inviteeCharName);
            if (invitee is null)
                return (false, $"No character named '{inviteeCharName}' was found.", 0, 0, 0);

            if (await IsInGuildOrRookieAsync(invitee.Id))
                return (false, $"{inviteeCharName} is already in a guild.", 0, 0, 0);

            if (asRookie && invitee.Level >= 20)
                return (false, "Only characters below level 20 can be invited as rookies.", 0, 0, 0);

            var alreadyInvited = await db.GuildInvites.AnyAsync(i =>
                i.GuildId == membership.GuildId && i.InviteeCharacterId == invitee.Id);
            if (alreadyInvited)
                return (false, $"{inviteeCharName} already has a pending invite from your guild.", 0, 0, 0);

            if (!asRookie)
            {
                var memberCount = await db.GuildMembers.CountAsync(m => m.GuildId == membership.GuildId);
                if (memberCount >= MaxMembers)
                    return (false, "The guild is full.", 0, 0, 0);
            }
            else
            {
                var rookieCount = await db.GuildRookies.CountAsync(r => r.GuildId == membership.GuildId);
                if (rookieCount >= MaxRookies)
                    return (false, "The guild's rookie roster is full.", 0, 0, 0);
            }

            var invite = new GuildInvite
            {
                GuildId            = membership.GuildId,
                InviterCharacterId = inviterCharId,
                InviteeCharacterId = invitee.Id,
                IsRookieInvite     = asRookie,
                CreatedAt          = DateTime.UtcNow,
                ExpiresAt          = DateTime.UtcNow.AddDays(7)
            };
            db.GuildInvites.Add(invite);
            await db.SaveChangesAsync();

            return (true, "", invite.Id, invitee.Id, membership.GuildId);
        }

        /// <summary>
        /// Accepts a pending invite. Adds the character as a member or rookie depending on
        /// the invite type. Returns the guild for hub broadcasting.
        /// </summary>
        public async Task<(bool success, string error, int guildId, bool joinedAsRookie)> AcceptInviteAsync(
            int characterId, int inviteId)
        {
            var invite = await db.GuildInvites.FindAsync(inviteId);
            if (invite is null || invite.InviteeCharacterId != characterId)
                return (false, "Invite not found.", 0, false);

            if (invite.ExpiresAt < DateTime.UtcNow)
            {
                db.GuildInvites.Remove(invite);
                await db.SaveChangesAsync();
                return (false, "This invite has expired.", 0, false);
            }

            if (await IsInGuildOrRookieAsync(characterId))
                return (false, "You are already in a guild.", 0, false);

            if (invite.IsRookieInvite)
            {
                var rookieCount = await db.GuildRookies.CountAsync(r => r.GuildId == invite.GuildId);
                if (rookieCount >= MaxRookies)
                    return (false, "The guild's rookie roster is now full.", 0, false);

                db.GuildRookies.Add(new GuildRookie
                {
                    GuildId              = invite.GuildId,
                    CharacterId          = characterId,
                    RecruiterCharacterId = invite.InviterCharacterId,
                    HiredAt              = DateTime.UtcNow
                });
            }
            else
            {
                var memberCount = await db.GuildMembers.CountAsync(m => m.GuildId == invite.GuildId);
                if (memberCount >= MaxMembers)
                    return (false, "The guild is now full.", 0, false);

                db.GuildMembers.Add(new GuildMember
                {
                    GuildId     = invite.GuildId,
                    CharacterId = characterId,
                    Rank        = GuildRank.Member,
                    JoinedAt    = DateTime.UtcNow
                });
            }

            db.GuildInvites.Remove(invite);
            await db.SaveChangesAsync();

            return (true, "", invite.GuildId, invite.IsRookieInvite);
        }

        /// <summary>Declines and removes a pending invite addressed to this character.</summary>
        public async Task<(bool success, string error, int guildId)> DeclineInviteAsync(int characterId, int inviteId)
        {
            var invite = await db.GuildInvites.FindAsync(inviteId);
            if (invite is null || invite.InviteeCharacterId != characterId)
                return (false, "Invite not found.", 0);

            var guildId = invite.GuildId;
            db.GuildInvites.Remove(invite);
            await db.SaveChangesAsync();

            return (true, "", guildId);
        }

        /// <summary>Revokes an outgoing invite. Caller must be Officer+ in the guild the invite belongs to.</summary>
        public async Task<(bool success, string error, int inviteeCharId, int guildId)> RevokeInviteAsync(
            int revokerCharId, int inviteId)
        {
            var invite = await db.GuildInvites.FindAsync(inviteId);
            if (invite is null) return (false, "Invite not found.", 0, 0);

            var membership = await GetMembershipAsync(revokerCharId);
            if (membership is null || membership.GuildId != invite.GuildId || membership.Rank < GuildRank.Officer)
                return (false, "You do not have permission to revoke this invite.", 0, 0);

            var inviteeId = invite.InviteeCharacterId;
            db.GuildInvites.Remove(invite);
            await db.SaveChangesAsync();

            return (true, "", inviteeId, invite.GuildId);
        }

        // ── Applications ──────────────────────────────────────────────────────────

        /// <summary>Submits an application to join a guild. The guild must allow applications.</summary>
        public async Task<(bool success, string error)> ApplyAsync(int characterId, int guildId, string? message)
        {
            if (await IsInGuildOrRookieAsync(characterId))
                return (false, "You are already in a guild.");

            var settings = await db.GuildSettings.FindAsync(guildId);
            if (settings is null) return (false, "Guild not found.");

            if (settings.ApplicationMode == GuildApplicationMode.InviteOnly)
                return (false, "This guild is not accepting applications.");

            var alreadyApplied = await db.GuildApplications.AnyAsync(a =>
                a.GuildId == guildId && a.ApplicantCharacterId == characterId);
            if (alreadyApplied)
                return (false, "You already have a pending application to this guild.");

            db.GuildApplications.Add(new GuildApplication
            {
                GuildId              = guildId,
                ApplicantCharacterId = characterId,
                Message              = message,
                CreatedAt            = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            return (true, "");
        }

        /// <summary>Approves an application. Caller must be Officer+ in the guild.</summary>
        public async Task<(bool success, string error, int applicantCharId, int guildId)> ApproveApplicationAsync(
            int approverCharId, int applicationId)
        {
            var membership = await GetMembershipAsync(approverCharId);
            if (membership is null || membership.Rank < GuildRank.Officer)
                return (false, "Insufficient rank.", 0, 0);

            var application = await db.GuildApplications.FindAsync(applicationId);
            if (application is null || application.GuildId != membership.GuildId)
                return (false, "Application not found.", 0, 0);

            if (await IsInGuildOrRookieAsync(application.ApplicantCharacterId))
            {
                db.GuildApplications.Remove(application);
                await db.SaveChangesAsync();
                return (false, "Applicant has since joined another guild. Application removed.", 0, 0);
            }

            var memberCount = await db.GuildMembers.CountAsync(m => m.GuildId == membership.GuildId);
            if (memberCount >= MaxMembers)
                return (false, "The guild is full.", 0, 0);

            db.GuildMembers.Add(new GuildMember
            {
                GuildId     = membership.GuildId,
                CharacterId = application.ApplicantCharacterId,
                Rank        = GuildRank.Member,
                JoinedAt    = DateTime.UtcNow
            });

            var charId = application.ApplicantCharacterId;
            db.GuildApplications.Remove(application);
            await db.SaveChangesAsync();

            return (true, "", charId, membership.GuildId);
        }

        /// <summary>Rejects an application. Caller must be Officer+ in the guild.</summary>
        public async Task<(bool success, string error, int applicantCharId, int guildId)> RejectApplicationAsync(
            int approverCharId, int applicationId)
        {
            var membership = await GetMembershipAsync(approverCharId);
            if (membership is null || membership.Rank < GuildRank.Officer)
                return (false, "Insufficient rank.", 0, 0);

            var application = await db.GuildApplications.FindAsync(applicationId);
            if (application is null || application.GuildId != membership.GuildId)
                return (false, "Application not found.", 0, 0);

            var charId = application.ApplicantCharacterId;
            db.GuildApplications.Remove(application);
            await db.SaveChangesAsync();

            return (true, "", charId, membership.GuildId);
        }

        // ── Membership operations ─────────────────────────────────────────────────

        /// <summary>Removes a member from the guild voluntarily. The leader must transfer or disband first.</summary>
        public async Task<(bool success, string error, int guildId)> LeaveGuildAsync(int characterId)
        {
            var membership = await GetMembershipAsync(characterId);
            if (membership is null) return (false, "You are not in a guild.", 0);

            if (membership.Rank == GuildRank.Leader)
                return (false, "Transfer leadership or disband the guild before leaving.", 0);

            var guildId = membership.GuildId;
            db.GuildMembers.Remove(membership);
            await db.SaveChangesAsync();

            return (true, "", guildId);
        }

        /// <summary>
        /// Kicks a member from the guild. Caller must be Officer+, and can only kick members
        /// of strictly lower rank than themselves.
        /// </summary>
        public async Task<(bool success, string error, int targetCharId, int guildId)> KickMemberAsync(
            int kickerCharId, int targetCharId)
        {
            var kicker = await GetMembershipAsync(kickerCharId);
            if (kicker is null || kicker.Rank < GuildRank.Officer)
                return (false, "Insufficient rank to kick.", 0, 0);

            var target = await db.GuildMembers.FirstOrDefaultAsync(m =>
                m.GuildId == kicker.GuildId && m.CharacterId == targetCharId);

            if (target is null)
                return (false, "That character is not in your guild.", 0, 0);

            if (target.Rank >= kicker.Rank)
                return (false, "You cannot kick someone of equal or higher rank.", 0, 0);

            db.GuildMembers.Remove(target);
            await db.SaveChangesAsync();

            return (true, "", targetCharId, kicker.GuildId);
        }

        // ── Rookie operations ─────────────────────────────────────────────────────

        /// <summary>Hires a character as a rookie. Caller must be Elite+.</summary>
        public async Task<(bool success, string error, int rookieCharId, int guildId)> HireRookieAsync(
            int recruiterCharId, string rookieCharName)
        {
            var membership = await GetMembershipAsync(recruiterCharId);
            if (membership is null || membership.Rank < GuildRank.Elite)
                return (false, "Insufficient rank to hire rookies.", 0, 0);

            var rookieCount = await db.GuildRookies.CountAsync(r => r.GuildId == membership.GuildId);
            if (rookieCount >= MaxRookies)
                return (false, "The guild's rookie roster is full.", 0, 0);

            var rookie = await db.Characters.FirstOrDefaultAsync(c => c.Name == rookieCharName);
            if (rookie is null)
                return (false, $"No character named '{rookieCharName}' was found.", 0, 0);

            if (await IsInGuildOrRookieAsync(rookie.Id))
                return (false, $"{rookieCharName} is already in a guild or rookie program.", 0, 0);

            if (rookie.Level >= 20)
                return (false, "Only characters below level 20 can be hired as rookies.", 0, 0);

            db.GuildRookies.Add(new GuildRookie
            {
                GuildId              = membership.GuildId,
                CharacterId          = rookie.Id,
                RecruiterCharacterId = recruiterCharId,
                HiredAt              = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            return (true, "", rookie.Id, membership.GuildId);
        }

        /// <summary>Promotes a rookie to a full Member. Caller must be Officer+.</summary>
        public async Task<(bool success, string error, int rookieCharId, int guildId)> PromoteRookieToMemberAsync(
            int promoterCharId, int rookieCharId)
        {
            var membership = await GetMembershipAsync(promoterCharId);
            if (membership is null || membership.Rank < GuildRank.Officer)
                return (false, "Insufficient rank.", 0, 0);

            var rookieEntry = await db.GuildRookies.FirstOrDefaultAsync(r =>
                r.GuildId == membership.GuildId && r.CharacterId == rookieCharId);
            if (rookieEntry is null)
                return (false, "That character is not a rookie in your guild.", 0, 0);

            var memberCount = await db.GuildMembers.CountAsync(m => m.GuildId == membership.GuildId);
            if (memberCount >= MaxMembers)
                return (false, "The guild is full.", 0, 0);

            db.GuildRookies.Remove(rookieEntry);
            db.GuildMembers.Add(new GuildMember
            {
                GuildId     = membership.GuildId,
                CharacterId = rookieCharId,
                Rank        = GuildRank.Member,
                JoinedAt    = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            return (true, "", rookieCharId, membership.GuildId);
        }

        /// <summary>Removes a rookie from the guild. Caller must be Elite+.</summary>
        public async Task<(bool success, string error, int rookieCharId, int guildId)> FireRookieAsync(
            int firerCharId, int rookieCharId)
        {
            var membership = await GetMembershipAsync(firerCharId);
            if (membership is null || membership.Rank < GuildRank.Elite)
                return (false, "Insufficient rank.", 0, 0);

            var rookieEntry = await db.GuildRookies.FirstOrDefaultAsync(r =>
                r.GuildId == membership.GuildId && r.CharacterId == rookieCharId);
            if (rookieEntry is null)
                return (false, "That character is not a rookie in your guild.", 0, 0);

            db.GuildRookies.Remove(rookieEntry);
            await db.SaveChangesAsync();

            return (true, "", rookieCharId, membership.GuildId);
        }

        // ── Rank management ───────────────────────────────────────────────────────

        /// <summary>
        /// Promotes a member by one rank step.
        /// Officers can promote Member → Elite only.
        /// Leaders can promote any rank up to (but not including) Leader.
        /// </summary>
        public async Task<(bool success, string error, GuildRank oldRank, GuildRank newRank, int targetCharId, int guildId)>
            PromoteMemberAsync(int promoterCharId, int targetCharId)
        {
            var promoter = await GetMembershipAsync(promoterCharId);
            if (promoter is null || promoter.Rank < GuildRank.Officer)
                return (false, "Insufficient rank.", default, default, 0, 0);

            var target = await db.GuildMembers.FirstOrDefaultAsync(m =>
                m.GuildId == promoter.GuildId && m.CharacterId == targetCharId);
            if (target is null)
                return (false, "That character is not in your guild.", default, default, 0, 0);

            GuildRank newRank;

            if (promoter.Rank == GuildRank.Officer)
            {
                if (target.Rank != GuildRank.Member)
                    return (false, "Officers can only promote Members to Elite.", default, default, 0, 0);
                newRank = GuildRank.Elite;
            }
            else // Leader
            {
                newRank = (GuildRank)((int)target.Rank + 1);
                if (newRank >= GuildRank.Leader)
                    return (false, "Use 'Transfer Leadership' to make someone the new leader.", default, default, 0, 0);
            }

            var oldRank = target.Rank;
            target.Rank = newRank;
            await db.SaveChangesAsync();

            return (true, "", oldRank, newRank, targetCharId, promoter.GuildId);
        }

        /// <summary>Demotes a member by one rank step. Only the Leader can demote.</summary>
        public async Task<(bool success, string error, GuildRank oldRank, GuildRank newRank, int targetCharId, int guildId)>
            DemoteMemberAsync(int demoterCharId, int targetCharId)
        {
            var demoter = await GetMembershipAsync(demoterCharId);
            if (demoter is null || demoter.Rank != GuildRank.Leader)
                return (false, "Only the guild leader can demote members.", default, default, 0, 0);

            var target = await db.GuildMembers.FirstOrDefaultAsync(m =>
                m.GuildId == demoter.GuildId && m.CharacterId == targetCharId);
            if (target is null)
                return (false, "That character is not in your guild.", default, default, 0, 0);

            if (target.Rank == GuildRank.Leader)
                return (false, "Cannot demote the guild leader.", default, default, 0, 0);

            if (target.Rank == GuildRank.Member)
                return (false, "Cannot demote below Member. Use 'Kick' to remove someone.", default, default, 0, 0);

            var oldRank = target.Rank;
            target.Rank = (GuildRank)((int)target.Rank - 1);
            await db.SaveChangesAsync();

            return (true, "", oldRank, target.Rank, targetCharId, demoter.GuildId);
        }

        /// <summary>
        /// Transfers leadership to another member. The current leader drops to Officer rank.
        /// </summary>
        public async Task<(bool success, string error, int guildId)> TransferLeadershipAsync(
            int currentLeaderCharId, int newLeaderCharId)
        {
            var currentLeader = await GetMembershipAsync(currentLeaderCharId);
            if (currentLeader is null || currentLeader.Rank != GuildRank.Leader)
                return (false, "Only the guild leader can transfer leadership.", 0);

            var newLeader = await db.GuildMembers.FirstOrDefaultAsync(m =>
                m.GuildId == currentLeader.GuildId && m.CharacterId == newLeaderCharId);
            if (newLeader is null)
                return (false, "That character is not in your guild.", 0);

            var guild = await db.Guilds.FindAsync(currentLeader.GuildId);
            if (guild is null) return (false, "Guild not found.", 0);

            currentLeader.Rank        = GuildRank.Officer;
            newLeader.Rank            = GuildRank.Leader;
            guild.LeaderCharacterId   = newLeaderCharId;
            await db.SaveChangesAsync();

            return (true, "", currentLeader.GuildId);
        }

        // ── Settings ──────────────────────────────────────────────────────────────

        /// <summary>Updates guild settings. Caller must be Ward or Leader.</summary>
        public async Task<(bool success, string error)> UpdateSettingsAsync(
            int characterId, GuildApplicationMode applicationMode, GuildRank depositWithdrawMinRank)
        {
            var membership = await GetMembershipAsync(characterId);
            if (membership is null || membership.Rank < GuildRank.Ward)
                return (false, "Insufficient rank. Ward or Leader required.");

            if (depositWithdrawMinRank is < GuildRank.Member or > GuildRank.Leader)
                return (false, "Invalid deposit withdraw rank.");

            var settings = await db.GuildSettings.FindAsync(membership.GuildId);
            if (settings is null) return (false, "Guild settings not found.");

            settings.ApplicationMode       = applicationMode;
            settings.DepositWithdrawMinRank = depositWithdrawMinRank;
            await db.SaveChangesAsync();

            return (true, "");
        }

        // ── Queries ───────────────────────────────────────────────────────────────

        /// <summary>Returns the full guild record (with settings, roster, rookies) for a character's guild.</summary>
        public async Task<Guild?> GetGuildByCharacterAsync(int characterId)
        {
            var membership = await GetMembershipAsync(characterId);
            int? guildId = membership?.GuildId
                ?? (await GetRookieMembershipAsync(characterId))?.GuildId;

            if (guildId is null) return null;

            return await db.Guilds
                .Include(g => g.Settings)
                .Include(g => g.Treasury)
                .Include(g => g.Members)
                .Include(g => g.Rookies)
                .FirstOrDefaultAsync(g => g.Id == guildId);
        }

        /// <summary>Returns a character's full member record, or null if not a full member.</summary>
        public Task<GuildMember?> GetMemberRecordAsync(int characterId) =>
            GetMembershipAsync(characterId);

        /// <summary>Returns a character's rookie record, or null if not a rookie.</summary>
        public Task<GuildRookie?> GetRookieRecordAsync(int characterId) =>
            GetRookieMembershipAsync(characterId);

        /// <summary>Returns all pending invites addressed to the given character.</summary>
        public async Task<List<GuildInvite>> GetPendingInvitesAsync(int characterId) =>
            await db.GuildInvites
                .Include(i => i.Guild)
                .Where(i => i.InviteeCharacterId == characterId && i.ExpiresAt > DateTime.UtcNow)
                .ToListAsync();

        /// <summary>Returns all pending applications for a guild. Caller must be Officer+.</summary>
        public async Task<(bool success, string error, List<GuildApplication> applications)>
            GetApplicationsAsync(int characterId)
        {
            var membership = await GetMembershipAsync(characterId);
            if (membership is null || membership.Rank < GuildRank.Officer)
                return (false, "Insufficient rank.", []);

            var apps = await db.GuildApplications
                .Include(a => a.ApplicantCharacter)
                .Where(a => a.GuildId == membership.GuildId)
                .ToListAsync();

            return (true, "", apps);
        }

        /// <summary>Returns all guild house listings visible to any player.</summary>
        public async Task<List<GuildHouseListing>> GetHouseListingsAsync() =>
            await db.GuildHouseListings
                .Include(l => l.SellerGuild)
                .Include(l => l.GuildProperty)
                .ToListAsync();
    }
}
