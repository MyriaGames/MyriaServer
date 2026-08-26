using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Myria.Server.Realm.Models
{
    /// <summary>One item stack in the character's inventory bag.</summary>
    public class CharacterInventoryItem
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string ItemId    { get; set; } = "";
        public int StackSize { get; set; } = 1;
        /// <summary>Position index in the flat Items list (0-based).</summary>
        public int SlotIndex { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;
    }

    /// <summary>A base skill the character has learned.</summary>
    public class CharacterSkill
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string SkillId { get; set; } = "";

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

    }

    /// <summary>An in-progress quest with per-player kill and item tracking.</summary>
    public class CharacterActiveQuest
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string QuestId { get; set; } = "";
        /// <summary>Maps to QuestStatus enum value.</summary>
        public int Status { get; set; }
        /// <summary>JSON of Dictionary&lt;int, int&gt; (monsterId → kill count).</summary>
        public string KillProgressJson { get; set; } = "{}";
        /// <summary>JSON of Dictionary&lt;string, int&gt; (itemId → count).</summary>
        public string ItemProgressJson  { get; set; } = "{}";

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

    }

    /// <summary>A quest the character has finished (ID only).</summary>
    public class CharacterCompletedQuest
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string QuestId { get; set; } = "";

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

    }

    /// <summary>Repeat-count and cooldown tracking for a repeatable quest.</summary>
    public class CharacterRepeatableQuest
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string QuestId          { get; set; } = "";
        public int        TimesCompleted   { get; set; }
        public int        CompletionsToday { get; set; }
        public DateTime?  LastCompletionDate { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

    }

    /// <summary>Job progress entry for one crafting/gathering discipline.</summary>
    public class CharacterJob
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string JobId          { get; set; } = "";
        public long SkillXp       { get; set; }
        public long KnowledgeXp   { get; set; }
        public long FameXp        { get; set; }
        public int  LastFameTickDay  { get; set; } = -1;
        public int  LastSkillUsedDay { get; set; } = -1;

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

    }

    /// <summary>One slot in the combat skill bar.</summary>
    public class CharacterSkillSlot
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        public int SlotIndex { get; set; }
        /// <summary>Maps to SlottedSkillSource enum value.</summary>
        public int Source  { get; set; }
        [MaxLength(100)] public string SkillId { get; set; } = "";

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

    }

    /// <summary>A fusion (composite) skill instance. Covers active and all per-class stashes.</summary>
    public class CharacterCompositeSkill
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        /// <summary>Guid string assigned at fusion time.</summary>
        [MaxLength(100)] public string InstanceId { get; set; } = "";
        public bool IsStashed { get; set; }
        /// <summary>When stashed, the class string ID it was stashed under; null otherwise.</summary>
        [MaxLength(50)] public string? StashedForClass { get; set; }
        /// <summary>True when this ID is in the player's ActiveCompositeSkillIds list.</summary>
        public bool IsActive { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

        public ICollection<CharacterCompositeSkillComponent> Components { get; set; } = new List<CharacterCompositeSkillComponent>();
    }

    /// <summary>One base-skill component of a composite fusion skill.</summary>
    public class CharacterCompositeSkillComponent
    {
        [Key] public int Id { get; set; }
        public int CompositeSkillId { get; set; }
        [MaxLength(100)] public string SkillId { get; set; } = "";

        [ForeignKey(nameof(CompositeSkillId))]
        public CharacterCompositeSkill CompositeSkill { get; set; } = null!;

    }

    /// <summary>A combined skill instance (2–5 base skills paired together).</summary>
    public class CharacterCombinedSkill
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string InstanceId { get; set; } = "";
        public bool IsStashed { get; set; }
        /// <summary>When stashed, the class string ID; null otherwise.</summary>
        [MaxLength(50)] public string? StashedForClass { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

        public ICollection<CharacterCombinedSkillInput> Inputs { get; set; } = new List<CharacterCombinedSkillInput>();
    }

    /// <summary>One input skill of a combined skill.</summary>
    public class CharacterCombinedSkillInput
    {
        [Key] public int Id { get; set; }
        public int CombinedSkillId { get; set; }
        [MaxLength(100)] public string SkillId { get; set; } = "";

        [ForeignKey(nameof(CombinedSkillId))]
        public CharacterCombinedSkill CombinedSkill { get; set; } = null!;

    }

    /// <summary>A composite rune instance the character has built.</summary>
    public class CharacterKnownRune
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string InstanceId  { get; set; } = "";
        [MaxLength(100)] public string BaseRuneId  { get; set; } = "";

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

        public ICollection<CharacterRuneAddedWord> AddedWords { get; set; } = new List<CharacterRuneAddedWord>();
    }

    /// <summary>One runic word added to a known composite rune.</summary>
    public class CharacterRuneAddedWord
    {
        [Key] public int Id { get; set; }
        public int KnownRuneId { get; set; }
        [MaxLength(100)] public string WordId { get; set; } = "";

        [ForeignKey(nameof(KnownRuneId))]
        public CharacterKnownRune KnownRune { get; set; } = null!;
    }

    /// <summary>The character's personal dictionary entry for a single runic word.</summary>
    public class CharacterRuneDictEntry
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(100)] public string  WordId             { get; set; } = "";
        [MaxLength(200)] public string? CharacterLabel        { get; set; }
        public bool IsOfficiallyLearned { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;
    }

    /// <summary>Last gather timestamp per room (cooldown tracking).</summary>
    public class CharacterRoomGathering
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        public int RoomId { get; set; }
        public DateTime LastGatheredAt { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;

    }

    /// <summary>Class XP accumulated in a specific player class.</summary>
    public class CharacterClassXp
    {
        [Key] public int Id { get; set; }
        public int CharacterId { get; set; }
        [MaxLength(50)] public string Class { get; set; } = "";
        public long Xp { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public Character Character { get; set; } = null!;
    }
}
