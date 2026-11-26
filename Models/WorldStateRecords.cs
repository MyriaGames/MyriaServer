using System.ComponentModel.DataAnnotations;

namespace Myria.Server.Realm.Models
{
    /// <summary>One NPC instance's stock of one item (v0.3 quest rework). Composite key
    /// (RoomId, NpcId, ItemId) - see Myria.Lib.Core.Systems.World.StockKey.</summary>
    public class WorldNpcStock
    {
        public int RoomId { get; set; }
        [MaxLength(100)] public string NpcId { get; set; } = "";
        [MaxLength(100)] public string ItemId { get; set; } = "";
        public int Quantity { get; set; }
    }

    /// <summary>One overworld room's population of one monster type. Composite key (RoomId, MonsterId).</summary>
    public class WorldRoomPopulation
    {
        public int RoomId { get; set; }
        public int MonsterId { get; set; }
        public int Count { get; set; }
    }
}
