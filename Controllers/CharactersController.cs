using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Models;
using Myria.Lib.Core.Services.Builder;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;
using Myria.Server.Realm.Models.Dto;
using Myria.Server.Realm.Repositories;

namespace Myria.Server.Realm.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    [EnableRateLimiting("authenticated")]
    public class CharactersController(AppDbContext db) : ControllerBase
    {
        // Character ownership is keyed by username (the JWT's authenticated identity name),
        // not a numeric auth-service user id — Character.UserId lives in this realm's own
        // database and no longer joins against a Users table (users now live in MyriaAuthServer).
        private Task<string?> GetUserAsync() => Task.FromResult(User.Identity!.Name);

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

            var c = await db.Characters
                .Include(c => c.InventoryItems)
                .Include(c => c.Skills)
                .Include(c => c.ActiveQuests)
                .Include(c => c.CompletedQuests)
                .Include(c => c.RepeatableQuests)
                .Include(c => c.Jobs)
                .Include(c => c.SkillSlots)
                .Include(c => c.CompositeSkills).ThenInclude(cs => cs.Components)
                .Include(c => c.CombinedSkills).ThenInclude(cs => cs.Inputs)
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
                MoneyBronze             = c.MoneyBronze,
                MoneyCapacity           = c.MoneyCapacity,
                InventoryPages          = c.InventoryPages,
                LastClassPenaltyApplied = c.LastClassPenaltyApplied,
                LastClassChanged        = c.LastClassChanged,
                ActiveJobId             = c.ActiveJobId,
                LastJobChanged          = c.LastJobChanged,

                InventoryItems = c.InventoryItems
                    .OrderBy(i => i.SlotIndex)
                    .Select(i => new CharSaveInventoryItem { ItemId = i.ItemId, StackSize = i.StackSize, SlotIndex = i.SlotIndex })
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

                CompositeSkills = c.CompositeSkills
                    .Select(cs => new CharSaveCompositeSkill
                    {
                        InstanceId      = cs.InstanceId,
                        ComponentIds    = cs.Components.Select(comp => comp.SkillId).ToList(),
                        IsStashed       = cs.IsStashed,
                        StashedForClass = cs.StashedForClass,
                        IsActive        = cs.IsActive
                    })
                    .ToList(),

                CombinedSkills = c.CombinedSkills
                    .Select(cs => new CharSaveCombinedSkill
                    {
                        InstanceId      = cs.InstanceId,
                        SkillIds        = cs.Inputs.Select(inp => inp.SkillId).ToList(),
                        IsStashed       = cs.IsStashed,
                        StashedForClass = cs.StashedForClass
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

        // ── POST /api/characters — upsert full character state ────────────────────

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Save(SaveCharacterRequest req)
        {
            var user = await GetUserAsync();
            if (user is null) return Unauthorized();

            // [Range]/[Required] on SaveCharacterRequest's scalar fields (incl. InventoryItems'
            // StackSize/SlotIndex) are already enforced automatically by [ApiController]'s model
            // validation before this method even runs. What that can't check is anything that
            // depends on looking an id up in the server's own item registry - validate that here
            // so a crafted request can't reference a nonexistent item, or stack one past what it
            // actually supports (see the 2026-09-10 security audit: this endpoint previously
            // wrote every client-supplied field straight to the DB unchecked).
            foreach (var i in req.InventoryItems)
            {
                if (!ItemFactory.TryCreateItem(i.ItemId, out var invItem) || invItem is null)
                    return BadRequest(new { message = $"Unknown item id: {i.ItemId}" });
                if (i.StackSize > invItem.MaxStackSize)
                    return BadRequest(new { message = $"Stack size {i.StackSize} exceeds max {invItem.MaxStackSize} for {i.ItemId}" });
            }

            foreach (var equippedId in new[] { req.WeaponItemId, req.ArmorItemId, req.AccessoryItemId })
            {
                if (equippedId is not null && !ItemFactory.TryCreateItem(equippedId, out _))
                    return BadRequest(new { message = $"Unknown item id: {equippedId}" });
            }

            // [Range(0, long.MaxValue)] on MoneyBronze alone would let a request set it past its
            // own MoneyCapacity - that's a cross-field constraint [Range] can't express, so it's
            // checked here instead.
            if (req.MoneyBronze > req.MoneyCapacity)
                return BadRequest(new { message = $"MoneyBronze ({req.MoneyBronze}) exceeds MoneyCapacity ({req.MoneyCapacity})" });

            // Load existing record with first-level child collections (ThenInclude not
            // needed for save — SQL Server cascade-delete handles grandchildren).
            var record = await db.Characters
                .Include(c => c.InventoryItems)
                .Include(c => c.Skills)
                .Include(c => c.ActiveQuests)
                .Include(c => c.CompletedQuests)
                .Include(c => c.RepeatableQuests)
                .Include(c => c.Jobs)
                .Include(c => c.SkillSlots)
                .Include(c => c.CompositeSkills)
                .Include(c => c.CombinedSkills)
                .Include(c => c.KnownRunes)
                .Include(c => c.RuneDictionary)
                .Include(c => c.RoomGatheringStatus)
                .Include(c => c.ClassXp)
                .SingleOrDefaultAsync(c => c.UserId == user && c.Name == req.Name);

            if (record is null)
            {
                // Character.Name is a global unique constraint (see AppDbContext) - check
                // before insert so a name collision with another account returns a friendly
                // Conflict instead of a raw DB exception, matching how
                // GuildService.CreateGuildAsync guards guild-name collisions.
                if (await db.Characters.AnyAsync(c => c.Name == req.Name))
                    return Conflict(new { message = "That character name is already taken." });

                // Per-account-per-realm cap - each realm has its own DB (see appsettings.json's
                // _RealmDeploymentNote), so this naturally scopes per realm already. Only gates
                // creating a *new* character; updates to one of the account's existing characters
                // always go through. UserAccount.MaxCharacters is also what the character-selection
                // UI's fixed 5-slot design assumes, so this keeps the two in sync.
                int existingCount = await db.Characters.CountAsync(c => c.UserId == user);
                if (existingCount >= UserAccount.MaxCharacters)
                    return Conflict(new { message = $"You already have the maximum of {UserAccount.MaxCharacters} characters on this realm." });

                record = new Character { UserId = user, Name = req.Name };
                db.Characters.Add(record);
            }

            // ── Scalar fields ────────────────────────────────────────────────────
            record.LastSaved                = DateTime.UtcNow;
            record.Level                    = req.Level;
            record.Experience               = req.Experience;
            record.ExpForNextLvl            = req.ExpForNextLvl;
            record.PotionTierAvailable      = req.PotionTierAvailable;
            record.Class                    = req.Class;
            record.Race                     = req.Race;
            record.RaceSelected             = req.RaceSelected;
            record.CurrentRoomId            = req.CurrentRoomId;
            record.LastHealerRoomId         = req.LastHealerRoomId;
            record.CurrentHealth            = req.CurrentHealth;
            record.CurrentMana              = req.CurrentMana;
            record.StatStrength             = req.StatStrength;
            record.StatDexterity            = req.StatDexterity;
            record.StatEndurance            = req.StatEndurance;
            record.StatIntelligence         = req.StatIntelligence;
            record.StatSpirit               = req.StatSpirit;
            record.StatStrengthBonus        = req.StatStrengthBonus;
            record.StatDexterityBonus       = req.StatDexterityBonus;
            record.StatEnduranceBonus       = req.StatEnduranceBonus;
            record.StatIntelligenceBonus    = req.StatIntelligenceBonus;
            record.StatSpiritBonus          = req.StatSpiritBonus;
            record.StatUnusedPoints         = req.StatUnusedPoints;
            record.StatBaseHealth           = req.StatBaseHealth;
            record.StatBaseMana             = req.StatBaseMana;
            record.WeaponItemId             = req.WeaponItemId;
            record.ArmorItemId              = req.ArmorItemId;
            record.AccessoryItemId          = req.AccessoryItemId;
            record.MoneyBronze              = req.MoneyBronze;
            record.MoneyCapacity            = req.MoneyCapacity;
            record.InventoryPages           = req.InventoryPages;
            record.LastClassPenaltyApplied  = req.LastClassPenaltyApplied;
            record.LastClassChanged         = req.LastClassChanged;
            record.ActiveJobId              = req.ActiveJobId;
            record.LastJobChanged           = req.LastJobChanged;

            // ── Replace child collections ─────────────────────────────────────────
            // EF marks cleared entities as Deleted; grandchildren are removed via
            // database-level cascade delete configured in OnModelCreating.

            record.InventoryItems.Clear();
            foreach (var i in req.InventoryItems)
                record.InventoryItems.Add(new CharacterInventoryItem { ItemId = i.ItemId, StackSize = i.StackSize, SlotIndex = i.SlotIndex });

            record.Skills.Clear();
            foreach (var sk in req.SkillIds)
                record.Skills.Add(new CharacterSkill { SkillId = sk });

            record.ActiveQuests.Clear();
            foreach (var q in req.ActiveQuests)
                record.ActiveQuests.Add(new CharacterActiveQuest
                {
                    QuestId          = q.QuestId,
                    Status           = q.Status,
                    KillProgressJson = q.KillProgressJson,
                    ItemProgressJson = q.ItemProgressJson
                });

            record.CompletedQuests.Clear();
            foreach (var qId in req.CompletedQuestIds)
                record.CompletedQuests.Add(new CharacterCompletedQuest { QuestId = qId });

            record.RepeatableQuests.Clear();
            foreach (var rq in req.RepeatableQuests)
                record.RepeatableQuests.Add(new CharacterRepeatableQuest
                {
                    QuestId            = rq.QuestId,
                    TimesCompleted     = rq.TimesCompleted,
                    CompletionsToday   = rq.CompletionsToday,
                    LastCompletionDate = rq.LastCompletionDate
                });

            record.Jobs.Clear();
            foreach (var j in req.Jobs)
                record.Jobs.Add(new CharacterJob
                {
                    JobId           = j.JobId,
                    SkillXp         = j.SkillXp,
                    KnowledgeXp     = j.KnowledgeXp,
                    FameXp          = j.FameXp,
                    LastFameTickDay  = j.LastFameTickDay,
                    LastSkillUsedDay = j.LastSkillUsedDay
                });

            record.SkillSlots.Clear();
            foreach (var slot in req.SkillSlots)
                record.SkillSlots.Add(new CharacterSkillSlot { SlotIndex = slot.SlotIndex, Source = slot.Source, SkillId = slot.SkillId });

            record.CompositeSkills.Clear();
            foreach (var cs in req.CompositeSkills)
            {
                var dbCs = new CharacterCompositeSkill
                {
                    InstanceId      = cs.InstanceId,
                    IsStashed       = cs.IsStashed,
                    StashedForClass = cs.StashedForClass,
                    IsActive        = cs.IsActive
                };
                foreach (var comp in cs.ComponentIds)
                    dbCs.Components.Add(new CharacterCompositeSkillComponent { SkillId = comp });
                record.CompositeSkills.Add(dbCs);
            }

            record.CombinedSkills.Clear();
            foreach (var cs in req.CombinedSkills)
            {
                var dbCs = new CharacterCombinedSkill
                {
                    InstanceId      = cs.InstanceId,
                    IsStashed       = cs.IsStashed,
                    StashedForClass = cs.StashedForClass
                };
                foreach (var sk in cs.SkillIds)
                    dbCs.Inputs.Add(new CharacterCombinedSkillInput { SkillId = sk });
                record.CombinedSkills.Add(dbCs);
            }

            record.KnownRunes.Clear();
            foreach (var rune in req.KnownRunes)
            {
                var dbRune = new CharacterKnownRune { InstanceId = rune.InstanceId, BaseRuneId = rune.BaseRuneId };
                foreach (var word in rune.AddedWordIds)
                    dbRune.AddedWords.Add(new CharacterRuneAddedWord { WordId = word });
                record.KnownRunes.Add(dbRune);
            }

            record.RuneDictionary.Clear();
            foreach (var e in req.RuneDictionary)
                record.RuneDictionary.Add(new CharacterRuneDictEntry
                {
                    WordId              = e.WordId,
                    CharacterLabel         = e.CharacterLabel,
                    IsOfficiallyLearned = e.IsOfficiallyLearned
                });

            record.RoomGatheringStatus.Clear();
            foreach (var rg in req.RoomGatheringStatus)
                record.RoomGatheringStatus.Add(new CharacterRoomGathering { RoomId = rg.RoomId, LastGatheredAt = rg.LastGatheredAt });

            record.ClassXp.Clear();
            foreach (var cx in req.ClassXp)
                record.ClassXp.Add(new CharacterClassXp { Class = cx.Class, Xp = cx.Xp });

            // Anti-cheat (TODO.md item MP21): every other field on this endpoint got validated
            // by item 54's fix, but CurrentHealth/CurrentMana were still only lower-bounded
            // (SaveCharacterRequest's [Range(0, int.MaxValue)]) - a crafted request could still
            // set either to an arbitrary huge number and have it persist as-is, since nothing
            // clamped them against what this character's own Stats+Equipment+Class/ClassXp
            // actually derive as MaxHealth/MaxMana (both level-and-class-progression-dependent
            // via ClassManager.GetClassBonusForStat/GetClassHpBonus, not a fixed formula computable
            // from Stats alone - hence reusing the full reconstruction below rather than a
            // lighter, easier-to-get-subtly-wrong reimplementation). record now has every field
            // (including ClassXp, just above) that reconstruction depends on, so this can run
            // right before the save that persists CurrentHealth/CurrentMana. The reconstructed
            // character itself is discarded immediately after - only its two derived properties
            // are used, no DB write happens through it.
            var reconstructed = SqlCharacterRepository.ReconstructCharacter(record);
            record.CurrentHealth = Math.Min(record.CurrentHealth, reconstructed.MaxHealth);
            record.CurrentMana   = Math.Min(record.CurrentMana, reconstructed.MaxMana);

            await db.SaveChangesAsync();
            return Ok();
        }

        // ── DELETE /api/characters/{name} ─────────────────────────────────────────

        [HttpDelete("{name}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Delete(string name)
        {
            var user = await GetUserAsync();
            if (user is null) return Unauthorized();

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
