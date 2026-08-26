namespace Myria.Server.Realm.Models.Dto
{
    /// <summary>Full character state returned by GET /api/characters/{name}.</summary>
    public class CharacterLoadResponse
    {
        public string Name { get; set; } = string.Empty;

        // ── Core progression ─────────────────────────────────────────────────────
        public int  Level               { get; set; }
        public long Experience          { get; set; }
        public long ExpForNextLvl       { get; set; }
        public int  PotionTierAvailable { get; set; }

        // ── Identity ─────────────────────────────────────────────────────────────
        public string Class        { get; set; }
        public string Race         { get; set; }
        public bool RaceSelected { get; set; }

        // ── Location ─────────────────────────────────────────────────────────────
        public int  CurrentRoomId    { get; set; }
        public int? LastHealerRoomId { get; set; }

        // ── Current state ────────────────────────────────────────────────────────
        public int CurrentHealth { get; set; }
        public int CurrentMana   { get; set; }

        // ── Base stats ───────────────────────────────────────────────────────────
        public int StatStrength     { get; set; }
        public int StatDexterity    { get; set; }
        public int StatEndurance    { get; set; }
        public int StatIntelligence { get; set; }
        public int StatSpirit       { get; set; }

        // ── Invested stat points ─────────────────────────────────────────────────
        public int StatStrengthBonus     { get; set; }
        public int StatDexterityBonus    { get; set; }
        public int StatEnduranceBonus    { get; set; }
        public int StatIntelligenceBonus { get; set; }
        public int StatSpiritBonus       { get; set; }
        public int StatUnusedPoints      { get; set; }

        // ── HP / MP pool ─────────────────────────────────────────────────────────
        public int StatBaseHealth { get; set; }
        public int StatBaseMana   { get; set; }

        // ── Equipment ────────────────────────────────────────────────────────────
        public string? WeaponItemId    { get; set; }
        public string? ArmorItemId     { get; set; }
        public string? AccessoryItemId { get; set; }

        // ── Money ────────────────────────────────────────────────────────────────
        public long MoneyBronze   { get; set; }
        public long MoneyCapacity { get; set; }

        // ── Inventory ────────────────────────────────────────────────────────────
        public int InventoryPages { get; set; }

        // ── Class tracking ───────────────────────────────────────────────────────
        public DateTime LastClassPenaltyApplied { get; set; }
        public DateTime LastClassChanged        { get; set; }

        // ── Jobs ─────────────────────────────────────────────────────────────────
        public string?  ActiveJobId    { get; set; }
        public DateTime LastJobChanged { get; set; }

        // ── Collections ──────────────────────────────────────────────────────────
        public List<CharSaveInventoryItem>   InventoryItems      { get; set; } = new();
        public List<string>                  SkillIds            { get; set; } = new();
        public List<CharSaveActiveQuest>     ActiveQuests        { get; set; } = new();
        public List<string>                  CompletedQuestIds   { get; set; } = new();
        public List<CharSaveRepeatableQuest> RepeatableQuests    { get; set; } = new();
        public List<CharSaveJob>             Jobs                { get; set; } = new();
        public List<CharSaveSkillSlot>       SkillSlots          { get; set; } = new();
        public List<CharSaveCompositeSkill>  CompositeSkills     { get; set; } = new();
        public List<CharSaveCombinedSkill>   CombinedSkills      { get; set; } = new();
        public List<CharSaveKnownRune>       KnownRunes          { get; set; } = new();
        public List<CharSaveRuneDictEntry>   RuneDictionary      { get; set; } = new();
        public List<CharSaveRoomGathering>   RoomGatheringStatus { get; set; } = new();
        public List<CharSaveClassXp>         ClassXp             { get; set; } = new();
    }
}
