using System.ComponentModel.DataAnnotations;

namespace Myria.Server.Realm.Models
{
    public class Character
    {
        public int Id { get; set; }

        // References MyriaAuthServer's User.Id (from the JWT sub claim) — a plain
        // string, not an EF FK, since users now live in a separate service/database.
        public string UserId { get; set; } = string.Empty;

        [MaxLength(50)]
        public string Name { get; set; } = string.Empty;

        public DateTime LastSaved { get; set; } = DateTime.UtcNow;

        // ── Core progression ─────────────────────────────────────────────────────
        public int Level { get; set; } = 1;
        public long Experience { get; set; }
        public long ExpForNextLvl { get; set; }
        public int PotionTierAvailable { get; set; } = 1;

        // ── Identity ─────────────────────────────────────────────────────────────
        [MaxLength(50)]
        public string Class { get; set; } = "Fighter";
        [MaxLength(50)]
        public string Race  { get; set; } = "Myralu";
        public bool RaceSelected { get; set; }

        // ── Location ─────────────────────────────────────────────────────────────
        public int CurrentRoomId { get; set; }
        public int? LastHealerRoomId { get; set; }

        // ── Current state ────────────────────────────────────────────────────────
        public int CurrentHealth { get; set; }
        public int CurrentMana { get; set; }

        // ── Base stats (race baseline + level growth) ────────────────────────────
        public int StatStrength { get; set; } = 10;
        public int StatDexterity { get; set; } = 10;
        public int StatEndurance { get; set; } = 10;
        public int StatIntelligence { get; set; } = 10;
        public int StatSpirit { get; set; } = 10;

        // ── Invested stat points ─────────────────────────────────────────────────
        public int StatStrengthBonus { get; set; }
        public int StatDexterityBonus { get; set; }
        public int StatEnduranceBonus { get; set; }
        public int StatIntelligenceBonus { get; set; }
        public int StatSpiritBonus { get; set; }
        public int StatUnusedPoints { get; set; }

        // ── HP / MP base ─────────────────────────────────────────────────────────
        public int StatBaseHealth { get; set; } = 30;
        public int StatBaseMana { get; set; } = 30;

        // ── Equipment (item IDs; resolved from game data at load time) ────────────
        [MaxLength(100)] public string? WeaponItemId { get; set; }
        [MaxLength(100)] public string? ArmorItemId { get; set; }
        [MaxLength(100)] public string? AccessoryItemId { get; set; }

        // ── Money ────────────────────────────────────────────────────────────────
        public long MoneyBronze { get; set; }
        public long MoneyCapacity { get; set; } = 300_000;

        // ── Inventory ────────────────────────────────────────────────────────────
        public int InventoryPages { get; set; } = 1;

        // ── Class tracking ───────────────────────────────────────────────────────
        public DateTime LastClassPenaltyApplied { get; set; } = DateTime.MinValue;
        public DateTime LastClassChanged { get; set; } = DateTime.MinValue;

        // ── Jobs ─────────────────────────────────────────────────────────────────
        [MaxLength(100)] public string? ActiveJobId { get; set; }
        public DateTime LastJobChanged { get; set; } = DateTime.MinValue;

        // ── Navigation properties (child tables) ─────────────────────────────────
        public ICollection<CharacterInventoryItem>  InventoryItems      { get; set; } = new List<CharacterInventoryItem>();
        public ICollection<CharacterSkill>          Skills              { get; set; } = new List<CharacterSkill>();
        public ICollection<CharacterActiveQuest>    ActiveQuests        { get; set; } = new List<CharacterActiveQuest>();
        public ICollection<CharacterCompletedQuest> CompletedQuests     { get; set; } = new List<CharacterCompletedQuest>();
        public ICollection<CharacterRepeatableQuest>RepeatableQuests    { get; set; } = new List<CharacterRepeatableQuest>();
        public ICollection<CharacterJob>            Jobs                { get; set; } = new List<CharacterJob>();
        public ICollection<CharacterSkillSlot>      SkillSlots          { get; set; } = new List<CharacterSkillSlot>();
        public ICollection<CharacterCompositeSkill> CompositeSkills     { get; set; } = new List<CharacterCompositeSkill>();
        public ICollection<CharacterCombinedSkill>  CombinedSkills      { get; set; } = new List<CharacterCombinedSkill>();
        public ICollection<CharacterKnownRune>      KnownRunes          { get; set; } = new List<CharacterKnownRune>();
        public ICollection<CharacterRuneDictEntry>  RuneDictionary      { get; set; } = new List<CharacterRuneDictEntry>();
        public ICollection<CharacterRoomGathering>  RoomGatheringStatus { get; set; } = new List<CharacterRoomGathering>();
        public ICollection<CharacterClassXp>        ClassXp             { get; set; } = new List<CharacterClassXp>();
    }
}
