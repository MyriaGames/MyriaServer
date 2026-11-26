using System.ComponentModel.DataAnnotations;

namespace Myria.Server.ClassicRealm.Models
{
    /// <summary>One player's copy of a dynamic quest (v0.3 quest rework) - see
    /// Myria.Lib.Core.Systems.World.QuestGrant. Completed grants are kept (Completed = true) but not
    /// loaded at startup.</summary>
    public class QuestGrantRecord
    {
        [Key, MaxLength(40)] public string Id { get; set; } = "";
        /// <summary>GrantKind: 0 = Fetch, 1 = Kill.</summary>
        public int Kind { get; set; }
        public int RoomId { get; set; }
        [MaxLength(100)] public string? NpcId { get; set; }
        [MaxLength(100)] public string? ItemId { get; set; }
        public int? MonsterId { get; set; }
        public int Target { get; set; }
        public int RewardXp { get; set; }
        public int RewardGold { get; set; }
        [MaxLength(64)] public string HolderName { get; set; } = "";
        public DateTime AcceptedUtc { get; set; }
        public bool Completed { get; set; }
    }
}
