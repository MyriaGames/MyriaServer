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
        // Range bounds below aren't "correct earned progression" checks (that would need
        // re-deriving the whole XP/stat curve server-side) - they're a floor against a crafted
        // request setting these to negative/absurd values that would corrupt downstream math
        // (e.g. Level 0 dividing-by-zero in monster XP scaling: baseXp * (monsterLvl/playerLvl)²)
        // or blow past what the client UI could ever produce (see the 2026-09-10 security audit).
        [Range(1, int.MaxValue)]
        public int  Level                { get; set; } = 1;
        [Range(0, long.MaxValue)]
        public long Experience           { get; set; }
        [Range(0, long.MaxValue)]
        public long ExpForNextLvl        { get; set; }
        [Range(1, int.MaxValue)]
        public int  PotionTierAvailable  { get; set; } = 1;

        // ── Identity ─────────────────────────────────────────────────────────────
        public string Class        { get; set; } = "Fighter";
        public string Race         { get; set; } = "Myralu";
        public bool RaceSelected { get; set; }

        // ── Location ─────────────────────────────────────────────────────────────
        public int  CurrentRoomId     { get; set; }
        public int? LastHealerRoomId  { get; set; }

        // ── Current state ────────────────────────────────────────────────────────
        [Range(0, int.MaxValue)]
        public int CurrentHealth { get; set; }
        [Range(0, int.MaxValue)]
        public int CurrentMana   { get; set; }

        // ── Base stats ───────────────────────────────────────────────────────────
        [Range(0, int.MaxValue)] public int StatStrength     { get; set; } = 10;
        [Range(0, int.MaxValue)] public int StatDexterity    { get; set; } = 10;
        [Range(0, int.MaxValue)] public int StatEndurance    { get; set; } = 10;
        [Range(0, int.MaxValue)] public int StatIntelligence { get; set; } = 10;
        [Range(0, int.MaxValue)] public int StatSpirit       { get; set; } = 10;

        // ── Invested stat points ─────────────────────────────────────────────────
        [Range(0, int.MaxValue)] public int StatStrengthBonus     { get; set; }
        [Range(0, int.MaxValue)] public int StatDexterityBonus    { get; set; }
        [Range(0, int.MaxValue)] public int StatEnduranceBonus    { get; set; }
        [Range(0, int.MaxValue)] public int StatIntelligenceBonus { get; set; }
        [Range(0, int.MaxValue)] public int StatSpiritBonus       { get; set; }
        [Range(0, int.MaxValue)] public int StatUnusedPoints      { get; set; }

        // ── HP / MP pool ─────────────────────────────────────────────────────────
        [Range(1, int.MaxValue)]
        public int StatBaseHealth { get; set; } = 30;
        [Range(1, int.MaxValue)]
        public int StatBaseMana   { get; set; } = 30;

        // ── Equipment ────────────────────────────────────────────────────────────
        public string? WeaponItemId    { get; set; }
        public string? ArmorItemId     { get; set; }
        public string? AccessoryItemId { get; set; }

        // ── Money ────────────────────────────────────────────────────────────────
        // MoneyBag.Capacity defaults to 300_000 and there is currently no feature anywhere in
        // the codebase that ever raises it past that default (grepped - no "IncreaseCapacity" or
        // similar exists yet), so a client sending anything else is definitionally either a bug
        // or an attempt to bypass the money cap. Tighten this range if/when a capacity-upgrade
        // feature is actually added.
        [Range(0, long.MaxValue)]
        public long MoneyBronze   { get; set; }
        [Range(1, 300_000)]
        public long MoneyCapacity { get; set; } = 300_000;

        // ── Inventory ────────────────────────────────────────────────────────────
        // No hard page cap exists in the game design today; 50 is a generous sanity ceiling
        // (the client only ever renders a handful of pages) against a crafted request, not a
        // real gameplay limit - raise it if a legitimate feature ever needs more.
        [Range(1, 50)]
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
        [Required]
        public string ItemId    { get; set; } = "";
        // Upper bound isn't enforced here since it depends on the specific item's MaxStackSize -
        // CharactersController.Save cross-checks that per-entry against ItemFactory.
        [Range(1, int.MaxValue)]
        public int    StackSize { get; set; } = 1;
        [Range(0, int.MaxValue)]
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
