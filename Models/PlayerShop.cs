using System.ComponentModel.DataAnnotations;

namespace Myria.Server.Realm.Models
{
    // Persistent player-run shop, anchored to the room it was opened in. Whether it's
    // currently *visible* to other players is computed on the fly (see PlayerShopService),
    // not stored — it depends on the owner's live online/room state plus whether a
    // Merchant's Seal sits in Items, none of which belongs in the database.
    public class PlayerShop
    {
        public int Id { get; set; }

        public int OwnerCharacterId { get; set; }
        public Character OwnerCharacter { get; set; } = null!;

        // Plain unconstrained room id, same convention as GuildProperty.CityRoomId — validated
        // against the shared JSON room catalog at runtime, not an EF FK.
        public int RoomId { get; set; }

        public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

        public ICollection<PlayerShopItem> Items { get; set; } = new List<PlayerShopItem>();
    }

    // A single stack of an item sitting in the shop's own storage. Price is null for an item
    // that's merely stored (not for sale) - most notably the Merchant's Seal, which is never
    // listed, just needs to be present. Backed by shop storage rather than the owner's live
    // inventory, so a listed item can never be double-sold or used elsewhere while listed -
    // the old in-memory shop's actual bug (see git history on PlayerShopService.cs).
    public class PlayerShopItem
    {
        public int Id { get; set; }

        public int PlayerShopId { get; set; }
        public PlayerShop PlayerShop { get; set; } = null!;

        [MaxLength(100)]
        public string ItemId { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public long? Price { get; set; }
    }
}
