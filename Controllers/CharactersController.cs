using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Models;
using Myria.Lib.Core.Services.Builder;
using Myria.Server.ClassicRealm.Data;
using Myria.Server.ClassicRealm.Models;
using Myria.Server.ClassicRealm.Models.Dto;
using Myria.Server.ClassicRealm.Repositories;
using Myria.Server.ClassicRealm.Services;

namespace Myria.Server.ClassicRealm.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    [EnableRateLimiting("authenticated")]
    public class CharactersController(AppDbContext db, BanService bans, CharacterSessionService session, Myria.Lib.Core.Repositories.ICharacterRepository repo) : ControllerBase
    {
        // Character ownership is keyed by username (the JWT's authenticated identity name),
        // not a numeric auth-service user id — Character.UserId lives in this realm's own
        // database and no longer joins against a Users table (users now live in MyriaAuthServer).
        private Task<string?> GetUserAsync() => Task.FromResult(User.Identity!.Name);

        // Per-character operator ban (see BanService): the character can't be loaded, saved over or
        // deleted (deleting would be a way to dodge the ban) until it expires. The account's other
        // characters are unaffected.
        private async Task<IActionResult?> RejectIfCharacterBannedAsync(string user, string characterName)
        {
            if (await bans.GetActiveCharacterBanAsync(user, characterName) is not { } ban)
                return null;

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "CharacterBanned",
                message = "This character is banned" +
                          (Models.Bans.IsPermanent(ban.Until) ? "." : $" until {ban.Until:u}.") +
                          (string.IsNullOrEmpty(ban.Reason) ? "" : $" Reason: {ban.Reason}"),
                bannedUntil = Models.Bans.IsPermanent(ban.Until) ? (DateTime?)null : ban.Until,
                reason = ban.Reason
            });
        }

        // ── GET /api/characters — list of character names ─────────────────────────

        [HttpGet]
        [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetNames()
        {
            var user = await GetUserAsync();
            if (user is null) return Unauthorized();

            var names = await db.Characters
                .Where(c => c.UserId == user)
                .Select(c => c.Name)
                .ToListAsync();

            return Ok(names);
        }

        // ── GET /api/characters/{name} — full character data ──────────────────────

        [HttpGet("{name}")]
        [ProducesResponseType(typeof(CharacterLoadResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetByName(string name)
        {
            var user = await GetUserAsync();
            if (user is null) return Unauthorized();
            if (await RejectIfCharacterBannedAsync(user, name) is { } banned) return banned;

            var c = await db.Characters
                .Include(c => c.InventoryItems)
                .Include(c => c.Skills)
                .Include(c => c.ActiveQuests)
                .Include(c => c.CompletedQuests)
                .Include(c => c.RepeatableQuests)
                .Include(c => c.Jobs)
                .Include(c => c.SkillSlots)
                .Include(c => c.SkillProgress).ThenInclude(sp => sp.PurchasedUpgrades)
                .Include(c => c.KnownRunes).ThenInclude(r => r.AddedWords)
                .Include(c => c.RuneDictionary)
                .Include(c => c.RoomGatheringStatus)
                .Include(c => c.ClassXp)
                .SingleOrDefaultAsync(c => c.UserId == user && c.Name == name);

            if (c is null) return NotFound();

            var resp = new CharacterLoadResponse
            {
                Name                    = c.Name,
                Level                   = c.Level,
                Experience              = c.Experience,
                ExpForNextLvl           = c.ExpForNextLvl,
                PotionTierAvailable     = c.PotionTierAvailable,
                Class                   = c.Class,
                Race                    = c.Race,
                RaceSelected            = c.RaceSelected,
                CurrentRoomId           = c.CurrentRoomId,
                LastHealerRoomId        = c.LastHealerRoomId,
                CurrentHealth           = c.CurrentHealth,
                CurrentMana             = c.CurrentMana,
                StatStrength            = c.StatStrength,
                StatDexterity           = c.StatDexterity,
                StatEndurance           = c.StatEndurance,
                StatIntelligence        = c.StatIntelligence,
                StatSpirit              = c.StatSpirit,
                StatStrengthBonus       = c.StatStrengthBonus,
                StatDexterityBonus      = c.StatDexterityBonus,
                StatEnduranceBonus      = c.StatEnduranceBonus,
                StatIntelligenceBonus   = c.StatIntelligenceBonus,
                StatSpiritBonus         = c.StatSpiritBonus,
                StatUnusedPoints        = c.StatUnusedPoints,
                StatBaseHealth          = c.StatBaseHealth,
                StatBaseMana            = c.StatBaseMana,
                WeaponItemId            = c.WeaponItemId,
                ArmorItemId             = c.ArmorItemId,
                AccessoryItemId         = c.AccessoryItemId,
                WeaponUpgradeLevel      = c.WeaponUpgradeLevel,
                WeaponCraftQuality      = c.WeaponCraftQuality,
                ArmorUpgradeLevel       = c.ArmorUpgradeLevel,
                ArmorCraftQuality       = c.ArmorCraftQuality,
                AccessoryUpgradeLevel   = c.AccessoryUpgradeLevel,
                AccessoryCraftQuality   = c.AccessoryCraftQuality,
                MoneyBronze             = c.MoneyBronze,
                MoneyCapacity           = c.MoneyCapacity,
                InventoryPages          = c.InventoryPages,
                LastClassPenaltyApplied = c.LastClassPenaltyApplied,
                LastClassChanged        = c.LastClassChanged,
                ActiveJobId             = c.ActiveJobId,
                LastJobChanged          = c.LastJobChanged,
                PvpRating               = c.PvpRating,
                PvpCurrency             = c.PvpCurrency,
                PvpWins                 = c.PvpWins,
                PvpLosses               = c.PvpLosses,
                AdventurersGuildMember  = c.AdventurersGuildMember,
                DynamicQuests = c.ActiveQuests
                    .Where(q => Myria.Lib.Core.Systems.World.DynamicQuests.IsDynamicQuestId(q.QuestId))
                    .Select(q => Myria.Lib.Core.Services.Manager.QuestManager.GetQuestById(q.QuestId))
                    .Where(q => q is not null)
                    .Select(q => Myria.Lib.Core.Systems.World.DynamicQuests.ToDto(q!))
                    .ToList(),

                InventoryItems = c.InventoryItems
                    .OrderBy(i => i.SlotIndex)
                    .Select(i => new CharSaveInventoryItem { ItemId = i.ItemId, StackSize = i.StackSize, SlotIndex = i.SlotIndex, UpgradeLevel = i.UpgradeLevel, CraftQuality = i.CraftQuality })
                    .ToList(),

                SkillIds = c.Skills.Select(s => s.SkillId).ToList(),

                ActiveQuests = c.ActiveQuests
                    .Select(q => new CharSaveActiveQuest
                    {
                        QuestId          = q.QuestId,
                        Status           = q.Status,
                        KillProgressJson = q.KillProgressJson,
                        ItemProgressJson = q.ItemProgressJson
                    })
                    .ToList(),

                CompletedQuestIds = c.CompletedQuests.Select(q => q.QuestId).ToList(),

                RepeatableQuests = c.RepeatableQuests
                    .Select(rq => new CharSaveRepeatableQuest
                    {
                        QuestId            = rq.QuestId,
                        TimesCompleted     = rq.TimesCompleted,
                        CompletionsToday   = rq.CompletionsToday,
                        LastCompletionDate = rq.LastCompletionDate
                    })
                    .ToList(),

                Jobs = c.Jobs
                    .Select(j => new CharSaveJob
                    {
                        JobId           = j.JobId,
                        SkillXp         = j.SkillXp,
                        KnowledgeXp     = j.KnowledgeXp,
                        FameXp          = j.FameXp,
                        LastFameTickDay  = j.LastFameTickDay,
                        LastSkillUsedDay = j.LastSkillUsedDay
                    })
                    .ToList(),

                SkillSlots = c.SkillSlots
                    .OrderBy(s => s.SlotIndex)
                    .Select(s => new CharSaveSkillSlot { SlotIndex = s.SlotIndex, Source = s.Source, SkillId = s.SkillId })
                    .ToList(),

                SkillProgress = c.SkillProgress
                    .Select(sp => new CharSaveSkillProgress
                    {
                        SkillId             = sp.SkillId,
                        UsageCount          = sp.UsageCount,
                        Level               = sp.Level,
                        UnspentPoints       = sp.UnspentPoints,
                        PurchasedUpgradeIds = sp.PurchasedUpgrades.Select(u => u.UpgradeId).ToList()
                    })
                    .ToList(),

                KnownRunes = c.KnownRunes
                    .Select(r => new CharSaveKnownRune
                    {
                        InstanceId   = r.InstanceId,
                        BaseRuneId   = r.BaseRuneId,
                        AddedWordIds = r.AddedWords.Select(w => w.WordId).ToList()
                    })
                    .ToList(),

                RuneDictionary = c.RuneDictionary
                    .Select(e => new CharSaveRuneDictEntry
                    {
                        WordId              = e.WordId,
                        CharacterLabel         = e.CharacterLabel,
                        IsOfficiallyLearned = e.IsOfficiallyLearned
                    })
                    .ToList(),

                RoomGatheringStatus = c.RoomGatheringStatus
                    .Select(rg => new CharSaveRoomGathering { RoomId = rg.RoomId, LastGatheredAt = rg.LastGatheredAt })
                    .ToList(),

                ClassXp = c.ClassXp
                    .Select(cx => new CharSaveClassXp { Class = cx.Class, Xp = cx.Xp })
                    .ToList()
            };

            return Ok(resp);
        }

        // ── POST /api/characters — create a character, or the two allowed one-time choices ──
        // Security review 2026-11-26, A06 F1: this used to upsert the WHOLE character state from the
        // request (level, stats, inventory, gold, quests, ...) for new and existing characters alike -
        // one request made any character as strong and rich as wanted. Now (see CharacterRules):
        //   - new character: only Name, Race, Class are taken; the starting state is built here,
        //     exactly as the client's creation page builds it;
        //   - existing character: only the one-time race choice (while RaceSelected is false) and
        //     the forced change away from RunicMage are applied - every other field is ignored.
        // The request shape is unchanged, so installed clients (which still send everything) work.
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Save(SaveCharacterRequest req)
        {
            var user = await GetUserAsync();
            if (user is null) return Unauthorized();
            if (await RejectIfCharacterBannedAsync(user, req.Name) is { } banned) return banned;

            // A live game session holds its own copy and would overwrite whatever is written here.
            if (session.GetConnectionId(req.Name) is not null)
                return Conflict(new { message = "This character is currently in play." });

            var exists = await db.Characters.AnyAsync(c => c.UserId == user && c.Name == req.Name);
            if (!exists)
            {
                // Character.Name is a global unique constraint (see AppDbContext) - check first so a
                // name collision with another account returns a friendly Conflict.
                if (await db.Characters.AnyAsync(c => c.Name == req.Name))
                    return Conflict(new { message = "That character name is already taken." });

                // Per-account-per-realm cap (UserAccount.MaxCharacters = the selection page's 5 slots).
                int existingCount = await db.Characters.CountAsync(c => c.UserId == user);
                if (existingCount >= UserAccount.MaxCharacters)
                    return Conflict(new { message = $"You already have the maximum of {UserAccount.MaxCharacters} characters on this realm." });

                if (!CharacterRules.IsSelectableRace(req.Race))
                    return BadRequest(new { message = "Unknown race." });
                if (!CharacterRules.IsSelectableClass(req.Race, req.Class))
                    return BadRequest(new { message = "This class can't be chosen (for this race)." });

                await repo.SaveAsync(user, CharacterRules.CreateNew(req.Name, req.Race, req.Class));
                return Ok();
            }

            var character = await repo.LoadAsync(user, req.Name);
            if (character is null) return NotFound();

            bool changed = false;
            if (req.RaceSelected && !character.RaceSelected)
                changed |= CharacterRules.TryChooseRace(character, req.Race);
            if (!string.Equals(req.Class, character.Class, StringComparison.OrdinalIgnoreCase))
                changed |= CharacterRules.TryLeaveRunicMage(character, req.Class);

            if (changed)
                await repo.SaveAsync(user, character);
            // Anything else in the request is deliberately ignored - progress only comes from play.
            return Ok();
        }

        // ── DELETE /api/characters/{name} ─────────────────────────────────────────

        [HttpDelete("{name}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Delete(string name)
        {
            var user = await GetUserAsync();
            if (user is null) return Unauthorized();
            if (await RejectIfCharacterBannedAsync(user, name) is { } banned) return banned;

            var record = await db.Characters
                .SingleOrDefaultAsync(c => c.UserId == user && c.Name == name);

            if (record is not null)
            {
                db.Characters.Remove(record);
                await db.SaveChangesAsync();
            }

            return NoContent();
        }
    }
}
