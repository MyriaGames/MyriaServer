using System.ComponentModel.DataAnnotations;

namespace Myria.Server.ClassicRealm.Models
{
    public enum FriendshipStatus { Pending, Accepted }

    public class Friendship
    {
        public int Id { get; set; }

        public int RequesterCharacterId { get; set; }
        public Character RequesterCharacter { get; set; } = null!;

        public int AddresseeCharacterId { get; set; }
        public Character AddresseeCharacter { get; set; } = null!;

        public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
