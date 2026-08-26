using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;

namespace Myria.Server.Realm.Services
{
    // No display name here, by design - same convention as CharacterInventoryItem: only ItemId
    // is persisted/transmitted, the client resolves display name/icon/etc. from its own shared
    // item catalog (ItemFactory), same source of truth the server itself loads from.
    public record PlayerShopItemDto(string ItemId, int Quantity, long? Price);

    public record PlayerShopEffectiveState(bool Online, bool OwnerPresent, bool HasSeal, bool Visible, bool Remote);

    // Persistent, database-backed replacement for the old fully in-memory shop (which was keyed
    // by SignalR connection id and force-closed the instant the owner disconnected - see git
    // history). A shop now survives disconnects/reconnects on its own; GameHub is responsible
    // for deciding *when* to broadcast visibility changes (room entry/exit, connect/disconnect,
    // seal deposit/withdraw) using GetEffectiveState below, since only it knows about the SignalR
    // groups those broadcasts need to go to.
    public class PlayerShopService(AppDbContext db, CharacterPresenceService presence)
    {
        public const string SealItemId = "merchant_seal";

        // A "remote" sale (owner not physically in the shop's room, only kept open by the seal)
        // skims this fraction off the top before crediting the owner - zero when they're right there.
        public const double RemoteSaleFeeRate = 0.02;

        private Task<Character?> GetCharacterByNameAsync(string name) =>
            db.Characters.SingleOrDefaultAsync(c => c.Name == name);

        // Everyone whose shop should show up in this room's interactable list right now -
        // includes shops anchored here by a Merchant's Seal even though the owner is physically
        // somewhere else (or shops the owner is standing right in front of, same as always).
        public async Task<List<string>> GetVisibleShopOwnerNamesInRoomAsync(int roomId)
        {
            var shops = await db.PlayerShops.Include(s => s.Items).Include(s => s.OwnerCharacter)
                .Where(s => s.RoomId == roomId).ToListAsync();
            return shops
                .Where(s => GetEffectiveState(s, s.OwnerCharacter.Name).Visible)
                .Select(s => s.OwnerCharacter.Name)
                .ToList();
        }

        public Task<PlayerShop?> GetShopByOwnerNameAsync(string ownerName) =>
            db.PlayerShops.Include(s => s.Items)
                .Include(s => s.OwnerCharacter)
                .SingleOrDefaultAsync(s => s.OwnerCharacter.Name == ownerName);

        public static bool HasSeal(PlayerShop shop) =>
            shop.Items.Any(i => i.ItemId == SealItemId && i.Quantity > 0);

        // Whether other players should currently see this shop as open, and whether a sale right
        // now would count as "remote" (owner not physically present -> fee applies). Purely a
        // function of live presence state + the shop's stored items - nothing here is persisted.
        public PlayerShopEffectiveState GetEffectiveState(PlayerShop shop, string ownerName)
        {
            bool online = presence.IsCharacterOnline(ownerName);
            bool hasSeal = HasSeal(shop);
            if (!online)
                return new PlayerShopEffectiveState(false, false, hasSeal, false, false);

            bool present = presence.GetRoomByCharacter(ownerName) == shop.RoomId;
            bool visible = present || hasSeal;
            return new PlayerShopEffectiveState(true, present, hasSeal, visible, visible && !present);
        }

        public async Task<(bool ok, string error, PlayerShop? shop)> OpenShopAsync(string ownerName, int roomId)
        {
            var character = await GetCharacterByNameAsync(ownerName);
            if (character is null) return (false, "no_character", null);

            var existing = await db.PlayerShops.SingleOrDefaultAsync(s => s.OwnerCharacterId == character.Id);
            if (existing is not null) return (false, "already_open", null);

            var shop = new PlayerShop { OwnerCharacterId = character.Id, RoomId = roomId };
            db.PlayerShops.Add(shop);
            await db.SaveChangesAsync();
            shop.Items = new List<PlayerShopItem>();
            return (true, "", shop);
        }

        // Used both for an explicit "close my shop" action and for account/character deletion
        // cleanup. Returns the room it was in (for the caller to broadcast a close to), or null
        // if there was no shop to close.
        public async Task<int?> CloseShopAsync(string ownerName)
        {
            var shop = await db.PlayerShops.Include(s => s.Items)
                .Include(s => s.OwnerCharacter)
                .SingleOrDefaultAsync(s => s.OwnerCharacter.Name == ownerName);
            if (shop is null) return null;

            int roomId = shop.RoomId;
            db.PlayerShops.Remove(shop);
            await db.SaveChangesAsync();
            return roomId;
        }

        // Moves an item from the owner's live inventory (caller already validated/removed it -
        // this only touches shop storage) into the shop. Stacks onto an existing storage row for
        // the same item if present; a newly-deposited stack is never listed for sale (Price null)
        // even if a previous stack of the same item was - the owner has to price it explicitly.
        public async Task<(bool ok, string error, List<PlayerShopItemDto>? items)> DepositAsync(
            string ownerName, string itemId, int quantity)
        {
            var shop = await db.PlayerShops.Include(s => s.Items)
                .Include(s => s.OwnerCharacter)
                .SingleOrDefaultAsync(s => s.OwnerCharacter.Name == ownerName);
            if (shop is null) return (false, "no_shop", null);
            if (quantity <= 0) return (false, "invalid_qty", null);

            var existing = shop.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (existing is not null)
            {
                existing.Quantity += quantity;
            }
            else
            {
                // Capped the same as a character's own inventory (one page's worth of distinct
                // slots) - restocking an item already in storage never needs a new slot, so only
                // a genuinely new item can hit this.
                if (shop.Items.Count >= Myria.Lib.Core.Entities.Characters.Inventory.PageSize)
                    return (false, "storage_full", null);

                shop.Items.Add(new PlayerShopItem { ItemId = itemId, Quantity = quantity, Price = null });
            }

            await db.SaveChangesAsync();
            return (true, "", ToDtos(shop.Items));
        }

        // Returns the quantity actually withdrawn (capped to what's stored) so the caller can
        // hand exactly that many back to the owner's live inventory - 0 if nothing was there.
        public async Task<(bool ok, string error, int withdrawn, List<PlayerShopItemDto>? items)> WithdrawAsync(
            string ownerName, string itemId, int quantity)
        {
            var shop = await db.PlayerShops.Include(s => s.Items)
                .Include(s => s.OwnerCharacter)
                .SingleOrDefaultAsync(s => s.OwnerCharacter.Name == ownerName);
            if (shop is null) return (false, "no_shop", 0, null);
            if (quantity <= 0) return (false, "invalid_qty", 0, null);

            var item = shop.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (item is null) return (false, "not_in_storage", 0, null);

            int withdrawn = Math.Min(quantity, item.Quantity);
            item.Quantity -= withdrawn;
            if (item.Quantity <= 0)
                shop.Items.Remove(item);

            await db.SaveChangesAsync();
            return (true, "", withdrawn, ToDtos(shop.Items));
        }

        public async Task<(bool ok, string error, List<PlayerShopItemDto>? items)> SetPriceAsync(
            string ownerName, string itemId, long? price)
        {
            if (itemId == SealItemId) return (false, "seal_not_listable", null);
            if (price is <= 0) return (false, "invalid_price", null);

            var shop = await db.PlayerShops.Include(s => s.Items)
                .Include(s => s.OwnerCharacter)
                .SingleOrDefaultAsync(s => s.OwnerCharacter.Name == ownerName);
            if (shop is null) return (false, "no_shop", null);

            var item = shop.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (item is null) return (false, "not_in_storage", null);

            item.Price = price;
            await db.SaveChangesAsync();
            return (true, "", ToDtos(shop.Items));
        }

        // Purchase, split validate/commit rather than reserve-then-maybe-roll-back: GameHub
        // calls ValidatePurchaseAsync first (read-only), then only after the buyer-side checks
        // it alone can make (gold, inventory, ItemFactory) all pass does it call CommitSaleAsync
        // to actually decrement storage - so there's never a reservation to undo if a later
        // check fails. Mirrors TradeService.ConfirmTrade's "re-validate at commit time" shape.
        public async Task<(PlayerShopItemDto? item, PlayerShopEffectiveState state)> ValidatePurchaseAsync(
            string ownerName, string itemId, int quantity)
        {
            var shop = await db.PlayerShops.Include(s => s.Items)
                .Include(s => s.OwnerCharacter)
                .SingleOrDefaultAsync(s => s.OwnerCharacter.Name == ownerName);
            var empty = new PlayerShopEffectiveState(false, false, false, false, false);
            if (shop is null) return (null, empty);

            var state = GetEffectiveState(shop, ownerName);
            if (!state.Visible) return (null, state);

            var item = shop.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (item is null || item.Price is null || item.Quantity < quantity) return (null, state);

            return (new PlayerShopItemDto(item.ItemId, quantity, item.Price), state);
        }

        // Only call once every buyer-side check has already passed - returns false (nothing
        // decremented) if stock changed out from under a concurrent purchase since validation.
        public async Task<bool> CommitSaleAsync(string ownerName, string itemId, int quantity)
        {
            var shop = await db.PlayerShops.Include(s => s.Items)
                .Include(s => s.OwnerCharacter)
                .SingleOrDefaultAsync(s => s.OwnerCharacter.Name == ownerName);
            var item = shop?.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (item is null || item.Quantity < quantity) return false;

            item.Quantity -= quantity;
            if (item.Quantity <= 0)
                shop!.Items.Remove(item);

            await db.SaveChangesAsync();
            return true;
        }

        private static List<PlayerShopItemDto> ToDtos(IEnumerable<PlayerShopItem> items) =>
            items.Select(i => new PlayerShopItemDto(i.ItemId, i.Quantity, i.Price)).ToList();
    }
}
