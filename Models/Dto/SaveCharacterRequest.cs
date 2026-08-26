using System.ComponentModel.DataAnnotations;

namespace Myria.Server.Realm.Models.Dto
{
    /// <summary>Full player state sent by the client when saving a character.</summary>
    public class SaveCharacterRequest
    {
        // Letters/digits/underscore/hyphen only - this name is also used as a segment of a
        // locally-constructed save-file path on the client (see Myria.Lib's SafeFileName), so
        // rejecting path-traversal/invalid-filename characters here (rather than relying on the
        // client to sanitize) removes the actual root cause instead of just the symptom.
        [Required, MaxLength(50), RegularExpression(@"^[\p{L}\p{N}_-]+$")]
        public string Name { get; set; } = string.Empty;

        // ── Core progression ─────────────────────────────────────────────────────
        public int  Level                { get; set; } = 1;
        public long Experience           { get; set; }
        public long ExpForNextLvl        { get; set; }
        public int  PotionTierAvailable  { get; set; } = 1;

        // ── Identity ─────────────────────────────────────────────────────────────
        public string Class        { get; set; } = "Fighter";
        public string Race         { get; set; } = "Myralu";
        public bool RaceSelected { get; set; }

        // ── Location ─────────────────────────────────────────────────────────────
        public int  CurrentRoomId     { get; set; }
        public int? LastHealerRoomId  { get; set; }

        // ── Current state ────────────────────────────────────────────────────────
        public int CurrentHealth { get; set; }
        public int CurrentMana   { get; set; }

        // ── Base stats ───────────────────────────────────────────────────────────
        public int StatStrength     { get; set; } = 10;
        public int StatDexterity    { get; set; } = 10;
        public int StatEndurance    { get; set; } = 10;
        public int StatIntelligence { get; set; } = 10;
        public int StatSpirit       { get; set; } = 10;

        // ── Invested stat points ─────────────────────────────────────────────────
        public int StatStrengthBonus     { get; set; }
        public int StatDexterityBonus    { get; set; }
        public int StatEnduranceBonus    { get; set; }
        public int StatIntelligenceBonus { get; set; }
        public int StatSpiritBonus       { get; set; }
        public int StatUnusedPoints      { get; set; }

        // ── HP / MP pool ─────────────────────────────────────────────────────────
        public int StatBaseHealth { get; set; } = 30;
        public int StatBaseMana   { get; set; } = 30;

        // ── Equipment ────────────────────────────────────────────────────────────
        public string? WeaponItemId    { get; set; }
        public string? ArmorItemId     { get; set; }
        public string? AccessoryItemId { get; set; }

        // ── Money ────────────────────────────────────────────────────────────────
        public long MoneyBronze   { get; set; }
        public long MoneyCapacity { get; set; } = 300_000;

        // ── Inventory ────────────────────────────────────────────────────────────
        public int InventoryPages { get; set; } = 1;

        // ── Class tracking ───────────────────────────────────────────────────────
        public DateTime LastClassPenaltyApplied { get; set; } = DateTime.MinValue;
        public DateTime LastClassChanged        { get; set; } = DateTime.MinValue;

        // ── Jobs ─────────────────────────────────────────────────────────────────
        public string?  ActiveJobId    { get; set; }
        public DateTime LastJobChanged { get; set; } = DateTime.MinValue;

        // ── Collections ──────────────────────────────────────────────────────────
        public List<CharSaveInventoryItem>    InventoryItems      { get; set; } = new();
        public List<string>                   SkillIds            { get; set; } = new();
        public List<CharSaveActiveQuest>      ActiveQuests        { get; set; } = new();
        public List<string>                   CompletedQuestIds   { get; set; } = new();
        public List<CharSaveRepeatableQuest>  RepeatableQuests    { get; set; } = new();
        public List<CharSaveJob>              Jobs                { get; set; } = new();
        public List<CharSaveSkillSlot>        SkillSlots          { get; set; } = new();
        public List<CharSaveCompositeSkill>   CompositeSkills     { get; set; } = new();
        public List<CharSaveCombinedSkill>    CombinedSkills      { get; set; } = new();
        public List<CharSaveKnownRune>        KnownRunes          { get; set; } = new();
        public List<CharSaveRuneDictEntry>    RuneDictionary      { get; set; } = new();
        public List<CharSaveRoomGathering>    RoomGatheringStatus { get; set; } = new();
        public List<CharSaveClassXp>          ClassXp             { get; set; } = new();
    }

    // ── Sub-DTOs (shared between SaveCharacterRequest and CharacterLoadResponse) ─

    public class CharSaveInventoryItem
    {
        public string ItemId    { get; set; } = "";
        public int    StackSize { get; set; } = 1;
        public int    SlotIndex { get; set; }
    }

    public class CharSaveActiveQuest
    {
        public string QuestId          { get; set; } = "";
        /// <summary>QuestStatus enum value.</summary>
        public int    Status           { get; set; }
        /// <summary>JSON of Dictionary&lt;int, int&gt; (monsterId → kill count).</summary>
        public string KillProgressJson { get; set; } = "{}";
        /// <summary>JSON of Dictionary&lt;string, int&gt; (itemId → count).</summary>
        public string ItemProgressJson { get; set; } = "{}";
    }

    public class CharSaveRepeatableQuest
    {
        public string    QuestId            { get; set; } = "";
        public int       TimesCompleted     { get; set; }
        public int       CompletionsToday   { get; set; }
        public DateTime? LastCompletionDate { get; set; }
    }

    public class CharSaveJob
    {
        public string JobId          { get; set; } = "";
        public long   SkillXp        { get; set; }
        public long   KnowledgeXp    { get; set; }
        public long   FameXp         { get; set; }
        public int    LastFameTickDay  { get; set; } = -1;
        public int    LastSkillUsedDay { get; set; } = -1;
    }

    public class CharSaveSkillSlot
    {
        public int    SlotIndex { get; set; }
        /// <summary>SlottedSkillSource enum value.</summary>
        public int    Source    { get; set; }
        public string SkillId   { get; set; } = "";
    }

    public class CharSaveCompositeSkill
    {
        public string       InstanceId     { get; set; } = "";
        public List<string> ComponentIds   { get; set; } = new();
        public bool         IsStashed      { get; set; }
        
        public string?      StashedForClass { get; set; }
        /// <summary>True when this ID is in the player's active fusion slots.</summary>
        public bool         IsActive       { get; set; }
    }

    public class CharSaveCombinedSkill
    {
        public string       InstanceId      { get; set; } = "";
        public List<string> SkillIds        { get; set; } = new();
        public bool         IsStashed       { get; set; }
        
        public string?      StashedForClass { get; set; }
    }

    public class CharSaveKnownRune
    {
        public string       InstanceId  { get; set; } = "";
        public string       BaseRuneId  { get; set; } = "";
        public List<string> AddedWordIds { get; set; } = new();
    }

    public class CharSaveRuneDictEntry
    {
        public string  WordId              { get; set; } = "";
        public string? CharacterLabel         { get; set; }
        public bool    IsOfficiallyLearned { get; set; }
    }

    public class CharSaveRoomGathering
    {
        public int      RoomId        { get; set; }
        public DateTime LastGatheredAt { get; set; }
    }

    public class CharSaveClassXp
    {
        public string Class { get; set; } = "";
        public long   Xp    { get; set; }
    }
}
