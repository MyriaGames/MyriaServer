using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Entities.Items;
using GameJob = Myria.Lib.Core.Entities.Jobs.CharacterJob;
using Myria.Lib.Core.Entities.NPCs;
using GameChar               = Myria.Lib.Core.Entities.Characters.Character;
using MoneyBag               = Myria.Lib.Core.Entities.Characters.MoneyBag;
using Money                  = Myria.Lib.Core.Entities.Characters.Money;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Models.BaseModel;
using Myria.Lib.Core.Repositories;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Myria.Server.Realm.Data;
using DbRow = Myria.Server.Realm.Models.Character;
using Myria.Server.Realm.Models;

namespace Myria.Server.Realm.Repositories
{
    /// <summary>
    /// Server-side character repository backed by the normalised SQL schema.
    /// Reconstructs a full <see cref="Character"/> on load and maps it back to the
    /// relational tables on save — no JSON blob involved.
    /// </summary>
    public class SqlCharacterRepository(AppDbContext db) : ICharacterRepository
    {
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        // "username" is the join key for character ownership within this realm's own
        // database — realms don't hold a Users table anymore (users live in
        // MyriaAuthServer); the username comes from the caller's authenticated JWT
        // identity and is trusted as-is, same as every other controller in this project.
        public async Task<List<string>> GetNamesAsync(string username)
        {
            return await db.Characters
                .Where(c => c.UserId == username)
                .Select(c => c.Name)
                .ToListAsync();
        }

        public async Task<GameChar?> LoadAsync(string username, string characterName)
        {
            var c = await LoadWithIncludes(username, characterName);
            if (c is null) return null;

            return ReconstructCharacter(c);
        }

        public async Task SaveAsync(string username, GameChar character)
        {
            character.CurrentRoomId = character.CurrentRoom?.Id ?? character.CurrentRoomId;

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
                .SingleOrDefaultAsync(c => c.UserId == username && c.Name == character.Name);

            if (record is null)
            {
                record = new Character { UserId = username, Name = character.Name };
                db.Characters.Add(record);
            }

            MapCharacterToCharacter(character, record);
            await db.SaveChangesAsync();
        }

        public async Task DeleteAsync(string username, string characterName)
        {
            var record = await db.Characters
                .SingleOrDefaultAsync(c => c.UserId == username && c.Name == characterName);
            if (record is not null)
            {
                db.Characters.Remove(record);
                await db.SaveChangesAsync();
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private async Task<DbRow?> LoadWithIncludes(string userId, string name) =>
            await db.Characters
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
                .SingleOrDefaultAsync(c => c.UserId == userId && c.Name == name);

        private static GameChar ReconstructCharacter(DbRow c)
        {
            var stats = new Myria.Lib.Core.Entities.Stats
            {
                Strength          = c.StatStrength,
                Dexterity         = c.StatDexterity,
                Endurance         = c.StatEndurance,
                Intelligence      = c.StatIntelligence,
                Spirit            = c.StatSpirit,
                StrengthBonus     = c.StatStrengthBonus,
                DexterityBonus    = c.StatDexterityBonus,
                EnduranceBonus    = c.StatEnduranceBonus,
                IntelligenceBonus = c.StatIntelligenceBonus,
                SpiritBonus       = c.StatSpiritBonus,
                UnusedPoints      = c.StatUnusedPoints,
                BaseHealth        = c.StatBaseHealth,
                BaseMana          = c.StatBaseMana
            };

            var character = new GameChar(c.Name, stats)
            {
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
                LastClassPenaltyApplied = c.LastClassPenaltyApplied,
                LastClassChanged        = c.LastClassChanged,
                ActiveJobId             = c.ActiveJobId,
                LastJobChanged          = c.LastJobChanged
            };

            character.CurrentRoom = RoomService.AllRooms.FirstOrDefault(r => r.Id == c.CurrentRoomId);

            // Money
            character.Money = new MoneyBag
            {
                Balance  = new Money(c.MoneyBronze),
                Capacity = c.MoneyCapacity
            };

            // Inventory
            character.Inventory.Pages = c.InventoryPages;
            character.Inventory.Items.Clear();
            foreach (var ii in c.InventoryItems.OrderBy(x => x.SlotIndex))
            {
                var item = ItemFactory.CreateItem(ii.ItemId, ii.StackSize);
                if (item is not null)
                    character.Inventory.Items.Add(item);
            }

            // Equipment
            if (!string.IsNullOrEmpty(c.WeaponItemId))
                character.WeaponSlot = ItemFactory.CreateItem(c.WeaponItemId) as EquipmentItem;
            if (!string.IsNullOrEmpty(c.ArmorItemId))
                character.ArmorSlot = ItemFactory.CreateItem(c.ArmorItemId) as EquipmentItem;
            if (!string.IsNullOrEmpty(c.AccessoryItemId))
                character.AccessorySlot = ItemFactory.CreateItem(c.AccessoryItemId) as EquipmentItem;

            // Active Quests
            character.ActiveQuests.Clear();
            foreach (var aq in c.ActiveQuests)
            {
                var template = QuestManager.GetQuestById(aq.QuestId);
                if (template is null) continue;
                var quest = template.Clone();
                quest.Status = (QuestStatus)aq.Status;
                try
                {
                    quest.KillProgress = (JsonSerializer.Deserialize<Dictionary<string, int>>(aq.KillProgressJson, _jsonOpts) ?? new())
                        .ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
                }
                catch { quest.KillProgress = new(); }
                try
                {
                    quest.ItemProgress = JsonSerializer.Deserialize<Dictionary<string, int>>(aq.ItemProgressJson, _jsonOpts) ?? new();
                }
                catch { quest.ItemProgress = new(); }
                character.ActiveQuests.Add(quest);
            }

            // Completed Quests
            character.CompletedQuests.Clear();
            foreach (var cq in c.CompletedQuests)
            {
                var template = QuestManager.GetQuestById(cq.QuestId);
                if (template is not null)
                    character.CompletedQuests.Add(template.Clone());
            }

            // Repeatable Quest Records
            character.RepeatableQuestRecords.Clear();
            foreach (var rq in c.RepeatableQuests)
                character.RepeatableQuestRecords[rq.QuestId] = new RepeatRecord
                {
                    TimesCompleted     = rq.TimesCompleted,
                    CompletionsToday   = rq.CompletionsToday,
                    LastCompletionDate = rq.LastCompletionDate
                };

            // Jobs
            character.Jobs.Clear();
            foreach (var j in c.Jobs)
                character.Jobs.Add(new GameJob
                {
                    JobId           = j.JobId,
                    SkillXp         = j.SkillXp,
                    KnowledgeXp     = j.KnowledgeXp,
                    FameXp          = j.FameXp,
                    LastFameTickDay  = j.LastFameTickDay,
                    LastSkillUsedDay = j.LastSkillUsedDay
                });

            // Skill Slots
            character.SkillSlots.Clear();
            foreach (var slot in c.SkillSlots.OrderBy(s => s.SlotIndex))
                character.SkillSlots.Add(new SkillSlot
                {
                    Source  = (SlottedSkillSource)slot.Source,
                    SkillId = slot.SkillId
                });

            // Composite Skills
            character.CompositeSkills.Clear();
            character.ActiveCompositeSkillIds.Clear();
            character.StashedCompositeSkills.Clear();
            foreach (var cs in c.CompositeSkills)
            {
                var composite = new CompositeSkill
                {
                    Id           = cs.InstanceId,
                    ComponentIds = cs.Components.Select(x => x.SkillId).ToList()
                };
                if (cs.IsStashed && !string.IsNullOrEmpty(cs.StashedForClass))
                {
                    var cls = cs.StashedForClass;
                    if (!character.StashedCompositeSkills.ContainsKey(cls))
                        character.StashedCompositeSkills[cls] = new();
                    character.StashedCompositeSkills[cls].Add(composite);
                }
                else
                {
                    character.CompositeSkills.Add(composite);
                    if (cs.IsActive)
                        character.ActiveCompositeSkillIds.Add(cs.InstanceId);
                }
            }

            // Combined Skills
            character.CombinedSkills.Clear();
            character.StashedCombinedSkills.Clear();
            foreach (var cs in c.CombinedSkills)
            {
                var combined = new CombinedSkill
                {
                    Id       = cs.InstanceId,
                    SkillIds = cs.Inputs.Select(x => x.SkillId).ToList()
                };
                if (cs.IsStashed && !string.IsNullOrEmpty(cs.StashedForClass))
                {
                    var cls = cs.StashedForClass;
                    if (!character.StashedCombinedSkills.ContainsKey(cls))
                        character.StashedCombinedSkills[cls] = new();
                    character.StashedCombinedSkills[cls].Add(combined);
                }
                else
                {
                    character.CombinedSkills.Add(combined);
                }
            }

            // Known Runes
            character.KnownRunes.Clear();
            foreach (var r in c.KnownRunes)
                character.KnownRunes.Add(new CompositeRune
                {
                    Id           = r.InstanceId,
                    BaseRuneId   = r.BaseRuneId,
                    AddedWordIds = r.AddedWords.Select(w => w.WordId).ToList()
                });

            // Rune Dictionary
            character.RuneDictionary.Clear();
            foreach (var e in c.RuneDictionary)
                character.RuneDictionary.Add(new CharacterRuneWordEntry
                {
                    WordId              = e.WordId,
                    CharacterLabel         = e.CharacterLabel,
                    IsOfficiallyLearned = e.IsOfficiallyLearned
                });

            // Room Gathering Status
            character.RoomGatheringStatus.Clear();
            foreach (var rg in c.RoomGatheringStatus)
                character.RoomGatheringStatus[rg.RoomId] = rg.LastGatheredAt;

            // Class XP
            character.ClassXp.Clear();
            foreach (var cx in c.ClassXp)
                character.ClassXp[cx.Class] = cx.Xp;

            // Post-load
            character.Inventory.Items.RemoveAll(i => i is null);
            character.RecalculateUnusedPoints();
            character.ValidateQuestStatuses();
            SkillFactory.UpdateSkills(character);
            BaseRuneService.ResolveRunes(character);
            SkillFusionSystem.ResolveCompositeSkills(character);
            SkillCombinationService.ResolveCombinedSkills(character);
            SkillSlotService.ResolveSlots(character);
            SkillSlotService.MigrateIfEmpty(character);

            return character;
        }

        private static void MapCharacterToCharacter(GameChar character, Character record)
        {
            record.LastSaved                = DateTime.UtcNow;
            record.Level                    = character.Level;
            record.Experience               = character.Experience;
            record.ExpForNextLvl            = character.ExpForNextLvl;
            record.PotionTierAvailable      = character.PotionTierAvailable;
            record.Class                    = character.Class;
            record.Race                     = character.Race;
            record.RaceSelected             = character.RaceSelected;
            record.CurrentRoomId            = character.CurrentRoomId;
            record.LastHealerRoomId         = character.LastHealerRoomId;
            record.CurrentHealth            = character.CurrentHealth;
            record.CurrentMana              = character.CurrentMana;
            record.StatStrength             = character.Stats.Strength;
            record.StatDexterity            = character.Stats.Dexterity;
            record.StatEndurance            = character.Stats.Endurance;
            record.StatIntelligence         = character.Stats.Intelligence;
            record.StatSpirit               = character.Stats.Spirit;
            record.StatStrengthBonus        = character.Stats.StrengthBonus;
            record.StatDexterityBonus       = character.Stats.DexterityBonus;
            record.StatEnduranceBonus       = character.Stats.EnduranceBonus;
            record.StatIntelligenceBonus    = character.Stats.IntelligenceBonus;
            record.StatSpiritBonus          = character.Stats.SpiritBonus;
            record.StatUnusedPoints         = character.Stats.UnusedPoints;
            record.StatBaseHealth           = character.Stats.BaseHealth;
            record.StatBaseMana             = character.Stats.BaseMana;
            record.WeaponItemId             = character.WeaponSlot?.Id;
            record.ArmorItemId              = character.ArmorSlot?.Id;
            record.AccessoryItemId          = character.AccessorySlot?.Id;
            record.MoneyBronze              = character.Money.Balance.BronzeTotal;
            record.MoneyCapacity            = character.Money.Capacity;
            record.InventoryPages           = character.Inventory.Pages;
            record.LastClassPenaltyApplied  = character.LastClassPenaltyApplied;
            record.LastClassChanged         = character.LastClassChanged;
            record.ActiveJobId              = character.ActiveJobId;
            record.LastJobChanged           = character.LastJobChanged;

            record.InventoryItems.Clear();
            for (int i = 0; i < character.Inventory.Items.Count; i++)
            {
                var item = character.Inventory.Items[i];
                record.InventoryItems.Add(new CharacterInventoryItem { ItemId = item.Id, StackSize = item.StackSize, SlotIndex = i });
            }

            record.Skills.Clear();
            foreach (var sk in character.Skills)
                record.Skills.Add(new CharacterSkill { SkillId = sk.Id });

            record.ActiveQuests.Clear();
            foreach (var q in character.ActiveQuests)
                record.ActiveQuests.Add(new CharacterActiveQuest
                {
                    QuestId          = q.Id,
                    Status           = (int)q.Status,
                    KillProgressJson = JsonSerializer.Serialize(q.KillProgress.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)),
                    ItemProgressJson = JsonSerializer.Serialize(q.ItemProgress)
                });

            record.CompletedQuests.Clear();
            foreach (var q in character.CompletedQuests)
                record.CompletedQuests.Add(new CharacterCompletedQuest { QuestId = q.Id });

            record.RepeatableQuests.Clear();
            foreach (var (questId, rec) in character.RepeatableQuestRecords)
                record.RepeatableQuests.Add(new CharacterRepeatableQuest
                {
                    QuestId            = questId,
                    TimesCompleted     = rec.TimesCompleted,
                    CompletionsToday   = rec.CompletionsToday,
                    LastCompletionDate = rec.LastCompletionDate
                });

            record.Jobs.Clear();
            foreach (var j in character.Jobs)
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
            for (int i = 0; i < character.SkillSlots.Count; i++)
            {
                var slot = character.SkillSlots[i];
                record.SkillSlots.Add(new CharacterSkillSlot { SlotIndex = i, Source = (int)slot.Source, SkillId = slot.SkillId });
            }

            record.CompositeSkills.Clear();
            foreach (var cs in character.CompositeSkills)
            {
                var dbCs = new CharacterCompositeSkill
                {
                    InstanceId      = cs.Id,
                    IsStashed       = false,
                    StashedForClass = null,
                    IsActive        = character.ActiveCompositeSkillIds.Contains(cs.Id)
                };
                foreach (var comp in cs.ComponentIds)
                    dbCs.Components.Add(new CharacterCompositeSkillComponent { SkillId = comp });
                record.CompositeSkills.Add(dbCs);
            }
            foreach (var (cls, list) in character.StashedCompositeSkills)
                foreach (var cs in list)
                {
                    var dbCs = new CharacterCompositeSkill
                    {
                        InstanceId      = cs.Id,
                        IsStashed       = true,
                        StashedForClass = cls,
                        IsActive        = false
                    };
                    foreach (var comp in cs.ComponentIds)
                        dbCs.Components.Add(new CharacterCompositeSkillComponent { SkillId = comp });
                    record.CompositeSkills.Add(dbCs);
                }

            record.CombinedSkills.Clear();
            foreach (var cs in character.CombinedSkills)
            {
                var dbCs = new CharacterCombinedSkill { InstanceId = cs.Id, IsStashed = false, StashedForClass = null };
                foreach (var sk in cs.SkillIds)
                    dbCs.Inputs.Add(new CharacterCombinedSkillInput { SkillId = sk });
                record.CombinedSkills.Add(dbCs);
            }
            foreach (var (cls, list) in character.StashedCombinedSkills)
                foreach (var cs in list)
                {
                    var dbCs = new CharacterCombinedSkill { InstanceId = cs.Id, IsStashed = true, StashedForClass = cls };
                    foreach (var sk in cs.SkillIds)
                        dbCs.Inputs.Add(new CharacterCombinedSkillInput { SkillId = sk });
                    record.CombinedSkills.Add(dbCs);
                }

            record.KnownRunes.Clear();
            foreach (var r in character.KnownRunes)
            {
                var dbRune = new CharacterKnownRune { InstanceId = r.Id, BaseRuneId = r.BaseRuneId };
                foreach (var word in r.AddedWordIds)
                    dbRune.AddedWords.Add(new CharacterRuneAddedWord { WordId = word });
                record.KnownRunes.Add(dbRune);
            }

            record.RuneDictionary.Clear();
            foreach (var e in character.RuneDictionary)
                record.RuneDictionary.Add(new CharacterRuneDictEntry
                {
                    WordId              = e.WordId,
                    CharacterLabel         = e.CharacterLabel,
                    IsOfficiallyLearned = e.IsOfficiallyLearned
                });

            record.RoomGatheringStatus.Clear();
            foreach (var (roomId, dt) in character.RoomGatheringStatus)
                record.RoomGatheringStatus.Add(new CharacterRoomGathering { RoomId = roomId, LastGatheredAt = dt });

            record.ClassXp.Clear();
            foreach (var (cls, xp) in character.ClassXp)
                record.ClassXp.Add(new CharacterClassXp { Class = cls, Xp = xp });
        }
    }
}
