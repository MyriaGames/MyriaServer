namespace Myria.Server.Realm.Models
{
    public class Block
    {
        public int Id { get; set; }

        public int BlockerCharacterId { get; set; }
        public Character BlockerCharacter { get; set; } = null!;

        public int BlockedCharacterId { get; set; }
        public Character BlockedCharacter { get; set; } = null!;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
