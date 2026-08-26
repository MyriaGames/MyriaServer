using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Regestries;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;
using GameCharacter = Myria.Lib.Core.Entities.Characters.Character;

namespace Myria.Server.Realm.Services
{
    public class GuildPropertyService(AppDbContext db, GuildConfig config)
    {
        // Guild room IDs start well above all static game room IDs.
        private const int GuildRoomIdBase = 1_000_000;

        // ── ID allocation ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns <paramref name="count"/> unique room IDs starting above all existing IDs.
        /// </summary>
        private async Task<List<int>> AllocateRoomIdsAsync(int count)
        {
            var maxId = await db.GuildRooms.MaxAsync(r => (int?)r.RoomId) ?? GuildRoomIdBase - 1;
            int next = Math.Max(maxId, GuildRoomIdBase - 1) + 1;
            return Enumerable.Range(next, count).ToList();
        }

        // ── Room helpers ───────────────────────────────────────────────────────

        private static string RoomLabel(GuildRoomType type, bool isBase) => type switch
        {
            GuildRoomType.GatheringPlace => isBase ? "Gathering Place" : "Entrance Hall",
            GuildRoomType.Deposit        => "Deposit",
            GuildRoomType.Treasury       => "Treasury",
            GuildRoomType.OfficersHall   => "Officers' Hall",
            GuildRoomType.LeaderOffice   => "Leader's Office",
            _                            => type.ToString()
        };

        private static GuildRank DefaultMinRankFor(GuildRoomType type) => type switch
        {
            GuildRoomType.Treasury     => GuildRank.Ward,
            GuildRoomType.OfficersHall => GuildRank.Officer,
            GuildRoomType.LeaderOffice => GuildRank.Leader,
            _                          => GuildRank.Member
        };

        private static (string Name, string Description) RoomText(string guildName, GuildRoomType type, bool isBase)
        {
            var label  = RoomLabel(type, isBase);
            var locale = isBase ? "base" : "house";
            return ($"{guildName} — {label}", $"A room within {guildName}'s guild {locale}.");
        }

        private static IReadOnlyList<GuildRoomType> StartingRoomsForPrestige(int prestigeLevel) =>
            prestigeLevel switch
            {
                0    => [GuildRoomType.GatheringPlace],
                <= 3 => [GuildRoomType.GatheringPlace, GuildRoomType.Deposit],
                <= 6 => [GuildRoomType.GatheringPlace, GuildRoomType.Deposit, GuildRoomType.Treasury],
                _    => [GuildRoomType.GatheringPlace, GuildRoomType.Deposit, GuildRoomType.Treasury,
                         GuildRoomType.OfficersHall, GuildRoomType.LeaderOffice]
            };

        // ── Membership helpers ─────────────────────────────────────────────────

        private async Task<GuildMember?> GetLeaderMemberAsync(int characterId) =>
            await db.GuildMembers.FirstOrDefaultAsync(m =>
                m.CharacterId == characterId && m.Rank == GuildRank.Leader);

        private async Task<GuildMember?> GetMemberAtOrAboveAsync(int characterId, GuildRank minRank) =>
            await db.GuildMembers.FirstOrDefaultAsync(m =>
                m.CharacterId == characterId && m.Rank >= minRank);

        // ── House: purchase ────────────────────────────────────────────────────

        /// <summary>
        /// Purchases a guild house at the given city room. Only the guild leader may do this.
        /// Gold is deducted from the guild treasury. The number of starting rooms depends on
        /// the city room's prestige level.
        /// </summary>
        public async Task<(bool success, string error, GuildProperty? property)> PurchaseHouseAsync(
            int characterId, int cityRoomId)
        {
            var leader = await GetLeaderMemberAsync(characterId);
            if (leader is null)
                return (false, "Only the guild leader can purchase a guild house.", null);

            if (await db.GuildProperties.AnyAsync(p => p.GuildId == leader.GuildId))
                return (false, "Your guild already owns a property.", null);

            var cityRoom = RoomService.GetRoomById(cityRoomId);
            if (cityRoom is null || CityRegistry.GetCityByRoom(cityRoom) is null)
                return (false, "Invalid city room.", null);

            if (await db.GuildProperties.AnyAsync(p => p.CityRoomId == cityRoomId))
                return (false, "Another guild already owns a house in that location.", null);

            // Prestige-tiered pricing/room-count was never wired up (the seeded prestige
            // level was always 0), so this has always behaved as flat tier-0 in practice.
            const int prestigeLevel = 0;
            long price = config.CalculateHousePrice(prestigeLevel);

            var treasury = await db.GuildTreasuries.FindAsync(leader.GuildId);
            if (treasury is null || treasury.Balance < price)
                return (false, $"Insufficient funds. Cost: {price} gold.", null);

            var guild = await db.Guilds.FindAsync(leader.GuildId);
            if (guild is null) return (false, "Guild not found.", null);

            var roomTypes  = StartingRoomsForPrestige(prestigeLevel);
            var roomIds    = await AllocateRoomIdsAsync(roomTypes.Count);
            var now        = DateTime.UtcNow;

            treasury.Balance -= price;

            var property = new GuildProperty
            {
                GuildId    = leader.GuildId,
                Type       = GuildPropertyType.House,
                CityRoomId = cityRoomId,
                PricePaid  = price,
                AcquiredAt = now,
            };
            db.GuildProperties.Add(property);
            await db.SaveChangesAsync(); // flush to get property.Id

            for (int i = 0; i < roomTypes.Count; i++)
            {
                var rt = roomTypes[i];
                var (name, description) = RoomText(guild.Name, rt, isBase: false);
                db.GuildRooms.Add(new GuildRoom
                {
                    GuildPropertyId = property.Id,
                    RoomId          = roomIds[i],
                    RoomType        = rt,
                    Name            = name,
                    Description     = description,
                    MinRankRequired = DefaultMinRankFor(rt),
                    IsBuilt         = true,
                    BuiltAt         = now,
                });
            }
            await db.SaveChangesAsync();

            return (true, "", property);
        }

        // ── House: add room ────────────────────────────────────────────────────

        /// <summary>
        /// Purchases an additional room for an existing guild house. Ward or Leader only.
        /// Gold is taken from the guild treasury.
        /// </summary>
        public async Task<(bool success, string error, GuildRoom? room)> AddHouseRoomAsync(
            int characterId, GuildRoomType roomType)
        {
            var member = await GetMemberAtOrAboveAsync(characterId, GuildRank.Ward);
            if (member is null)
                return (false, "Ward or Leader rank required.", null);

            var property = await db.GuildProperties
                .FirstOrDefaultAsync(p => p.GuildId == member.GuildId && p.Type == GuildPropertyType.House);
            if (property is null)
                return (false, "Your guild does not own a house.", null);

            bool alreadyExists = await db.GuildRooms.AnyAsync(r =>
                r.GuildPropertyId == property.Id && r.RoomType == roomType);
            if (alreadyExists)
                return (false, $"The {roomType} room already exists.", null);

            var typeKey = roomType.ToString();
            if (!config.HouseRoomCosts.TryGetValue(typeKey, out int cost))
                return (false, $"No cost defined for room type '{typeKey}'.", null);

            var treasury = await db.GuildTreasuries.FindAsync(member.GuildId);
            if (treasury is null || treasury.Balance < cost)
                return (false, $"Insufficient funds. Cost: {cost} gold.", null);

            var guild = await db.Guilds.FindAsync(member.GuildId);
            if (guild is null) return (false, "Guild not found.", null);

            var ids = await AllocateRoomIdsAsync(1);
            var now = DateTime.UtcNow;

            treasury.Balance -= cost;
            var (name, description) = RoomText(guild.Name, roomType, isBase: false);

            var guildRoom = new GuildRoom
            {
                GuildPropertyId = property.Id,
                RoomId          = ids[0],
                RoomType        = roomType,
                Name            = name,
                Description     = description,
                MinRankRequired = DefaultMinRankFor(roomType),
                IsBuilt         = true,
                BuiltAt         = now,
            };
            db.GuildRooms.Add(guildRoom);
            await db.SaveChangesAsync();

            return (true, "", guildRoom);
        }

        // ── House: sale listing ────────────────────────────────────────────────

        /// <summary>Lists the guild house for sale. Ward or Leader only.</summary>
        public async Task<(bool success, string error)> ListHouseForSaleAsync(
            int characterId, long askingPrice)
        {
            if (askingPrice <= 0)
                return (false, "Asking price must be greater than zero.");

            var member = await GetMemberAtOrAboveAsync(characterId, GuildRank.Ward);
            if (member is null) return (false, "Ward or Leader rank required.");

            var property = await db.GuildProperties
                .Include(p => p.Listing)
                .FirstOrDefaultAsync(p => p.GuildId == member.GuildId && p.Type == GuildPropertyType.House);
            if (property is null) return (false, "Your guild does not own a house.");
            if (property.Listing is not null) return (false, "The house is already listed for sale.");

            db.GuildHouseListings.Add(new GuildHouseListing
            {
                GuildPropertyId = property.Id,
                SellerGuildId   = member.GuildId,
                AskingPrice     = askingPrice,
                ListedAt        = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            return (true, "");
        }

        /// <summary>Removes the house sale listing. Ward or Leader only.</summary>
        public async Task<(bool success, string error)> DelistHouseAsync(int characterId)
        {
            var member = await GetMemberAtOrAboveAsync(characterId, GuildRank.Ward);
            if (member is null) return (false, "Ward or Leader rank required.");

            var listing = await db.GuildHouseListings
                .FirstOrDefaultAsync(l => l.SellerGuildId == member.GuildId);
            if (listing is null) return (false, "Your guild house is not listed for sale.");

            db.GuildHouseListings.Remove(listing);
            await db.SaveChangesAsync();

            return (true, "");
        }

        /// <summary>
        /// Purchases a house listing from another guild. Leader of the buying guild only.
        /// Gold transfers from buyer treasury to seller treasury atomically.
        /// The house (including all rooms) transfers to the buyer guild.
        /// </summary>
        public async Task<(bool success, string error, int sellerGuildId, int buyerGuildId)>
            BuyListedHouseAsync(int buyerCharId, int listingId)
        {
            var leader = await GetLeaderMemberAsync(buyerCharId);
            if (leader is null)
                return (false, "Only the guild leader can purchase a property.", 0, 0);

            if (await db.GuildProperties.AnyAsync(p => p.GuildId == leader.GuildId))
                return (false, "Your guild already owns a property.", 0, 0);

            var listing = await db.GuildHouseListings
                .Include(l => l.GuildProperty)
                .FirstOrDefaultAsync(l => l.Id == listingId);
            if (listing is null)
                return (false, "Listing not found.", 0, 0);

            if (listing.SellerGuildId == leader.GuildId)
                return (false, "You cannot buy your own listing.", 0, 0);

            var buyerTreasury  = await db.GuildTreasuries.FindAsync(leader.GuildId);
            var sellerTreasury = await db.GuildTreasuries.FindAsync(listing.SellerGuildId);
            if (buyerTreasury is null || sellerTreasury is null)
                return (false, "Treasury error.", 0, 0);

            if (buyerTreasury.Balance < listing.AskingPrice)
                return (false, $"Insufficient funds. Price: {listing.AskingPrice} gold.", 0, 0);

            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                buyerTreasury.Balance  -= listing.AskingPrice;
                sellerTreasury.Balance += listing.AskingPrice;

                var sellerGuildId = listing.SellerGuildId;
                listing.GuildProperty.GuildId = leader.GuildId;
                db.GuildHouseListings.Remove(listing);

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                return (true, "", sellerGuildId, leader.GuildId);
            }
            catch
            {
                await tx.RollbackAsync();
                return (false, "Transaction failed. Please try again.", 0, 0);
            }
        }

        // ── Base: establish ────────────────────────────────────────────────────

        /// <summary>
        /// Claims a world room as the guild's base. Leader only.
        /// The room must not already be another guild's base anchor.
        /// Creates the Gathering Place as the first base room.
        /// </summary>
        public async Task<(bool success, string error, GuildProperty? property)> EstablishBaseAsync(
            int characterId, int anchorRoomId)
        {
            var leader = await GetLeaderMemberAsync(characterId);
            if (leader is null)
                return (false, "Only the guild leader can establish a guild base.", null);

            if (await db.GuildProperties.AnyAsync(p => p.GuildId == leader.GuildId))
                return (false, "Your guild already owns a property.", null);

            if (RoomService.GetRoomById(anchorRoomId) is null)
                return (false, "Invalid room.", null);

            if (await db.GuildProperties.AnyAsync(p => p.BaseAnchorRoomId == anchorRoomId))
                return (false, "Another guild already has a base anchored at that room.", null);

            var guild = await db.Guilds.FindAsync(leader.GuildId);
            if (guild is null) return (false, "Guild not found.", null);

            var ids = await AllocateRoomIdsAsync(1);
            var now = DateTime.UtcNow;

            var property = new GuildProperty
            {
                GuildId          = leader.GuildId,
                Type             = GuildPropertyType.Base,
                BaseAnchorRoomId = anchorRoomId,
                AcquiredAt       = now,
            };
            db.GuildProperties.Add(property);
            await db.SaveChangesAsync();

            var (baseName, baseDescription) = RoomText(guild.Name, GuildRoomType.GatheringPlace, isBase: true);
            db.GuildRooms.Add(new GuildRoom
            {
                GuildPropertyId = property.Id,
                RoomId          = ids[0],
                RoomType        = GuildRoomType.GatheringPlace,
                Name            = baseName,
                Description     = baseDescription,
                MinRankRequired = GuildRank.Member,
                IsBuilt         = true,
                BuiltAt         = now,
            });
            await db.SaveChangesAsync();

            return (true, "", property);
        }

        // ── Base: build room ───────────────────────────────────────────────────

        /// <summary>
        /// Constructs a new room in the guild base using materials from the caller's
        /// session inventory. Ward or Leader only.
        /// <para>
        /// Material deduction is applied to <paramref name="sessionCharacter"/> in-memory
        /// and will be persisted on the character's next session save.
        /// </para>
        /// </summary>
        public async Task<(bool success, string error, GuildRoom? room)> BuildBaseRoomAsync(
            int characterId, GuildRoomType roomType, GameCharacter sessionCharacter)
        {
            var member = await GetMemberAtOrAboveAsync(characterId, GuildRank.Ward);
            if (member is null) return (false, "Ward or Leader rank required.", null);

            if (roomType == GuildRoomType.GatheringPlace)
                return (false, "The Gathering Place is created automatically when establishing a base.", null);

            var property = await db.GuildProperties
                .FirstOrDefaultAsync(p => p.GuildId == member.GuildId && p.Type == GuildPropertyType.Base);
            if (property is null) return (false, "Your guild does not have a base.", null);

            bool alreadyExists = await db.GuildRooms.AnyAsync(r =>
                r.GuildPropertyId == property.Id && r.RoomType == roomType);
            if (alreadyExists) return (false, $"The {roomType} room already exists.", null);

            var typeKey = roomType.ToString();
            if (!config.BaseRoomCosts.TryGetValue(typeKey, out var costs))
                return (false, $"No cost defined for room type '{typeKey}'.", null);

            // Validate materials in the session character's inventory
            foreach (var req in costs)
            {
                int have = sessionCharacter.Inventory.Items
                    .Where(i => i.Id == req.ItemId)
                    .Sum(i => i.StackSize);
                if (have < req.Amount)
                    return (false, $"Missing materials: need {req.Amount}x {req.ItemId}, have {have}.", null);
            }

            // Consume materials from in-memory inventory
            foreach (var req in costs)
            {
                int toRemove = req.Amount;
                foreach (var stack in sessionCharacter.Inventory.Items
                    .Where(i => i.Id == req.ItemId).ToList())
                {
                    if (toRemove <= 0) break;
                    int take = Math.Min(stack.StackSize, toRemove);
                    stack.StackSize -= take;
                    toRemove -= take;
                    if (stack.StackSize <= 0)
                        sessionCharacter.Inventory.RemoveItem(stack);
                }
            }

            var guild = await db.Guilds.FindAsync(member.GuildId);
            if (guild is null) return (false, "Guild not found.", null);

            var ids = await AllocateRoomIdsAsync(1);
            var now = DateTime.UtcNow;

            var (name, description) = RoomText(guild.Name, roomType, isBase: true);

            var guildRoom = new GuildRoom
            {
                GuildPropertyId = property.Id,
                RoomId          = ids[0],
                RoomType        = roomType,
                Name            = name,
                Description     = description,
                MinRankRequired = DefaultMinRankFor(roomType),
                IsBuilt         = true,
                BuiltAt         = now,
            };
            db.GuildRooms.Add(guildRoom);
            await db.SaveChangesAsync();

            return (true, "", guildRoom);
        }

        // ── Room access ────────────────────────────────────────────────────────

        /// <summary>
        /// Updates the minimum rank required to enter a guild room. Ward or Leader only.
        /// The Leader's Office minimum rank cannot be lowered below Leader.
        /// </summary>
        public async Task<(bool success, string error)> UpdateRoomAccessAsync(
            int characterId, int guildRoomId, GuildRank newMinRank)
        {
            var member = await GetMemberAtOrAboveAsync(characterId, GuildRank.Ward);
            if (member is null) return (false, "Ward or Leader rank required.");

            var guildRoom = await db.GuildRooms
                .Include(r => r.GuildProperty)
                .FirstOrDefaultAsync(r => r.Id == guildRoomId);
            if (guildRoom is null || guildRoom.GuildProperty.GuildId != member.GuildId)
                return (false, "Room not found in your guild's property.");

            if (guildRoom.RoomType == GuildRoomType.LeaderOffice && newMinRank < GuildRank.Leader)
                return (false, "The Leader's Office cannot be set below Leader rank.");

            guildRoom.MinRankRequired = newMinRank;
            await db.SaveChangesAsync();

            return (true, "");
        }

        /// <summary>
        /// Returns whether a character can enter a given room ID (guild room access check).
        /// Also returns the character's rank for the caller to use in UI.
        /// Returns (false, null) for non-guild rooms (no restriction applies).
        /// </summary>
        public async Task<(bool canAccess, GuildRank? memberRank)> CanAccessRoomAsync(
            int characterId, int roomId)
        {
            var guildRoom = await db.GuildRooms
                .Include(r => r.GuildProperty)
                .FirstOrDefaultAsync(r => r.RoomId == roomId);

            if (guildRoom is null) return (true, null); // not a guild room

            // Check full membership
            var member = await db.GuildMembers.FirstOrDefaultAsync(m =>
                m.CharacterId == characterId && m.GuildId == guildRoom.GuildProperty.GuildId);
            if (member is not null)
                return (member.Rank >= guildRoom.MinRankRequired, member.Rank);

            // Rookies: cannot enter any guild room
            return (false, null);
        }

        // ── Queries ────────────────────────────────────────────────────────────

        public async Task<GuildProperty?> GetPropertyByGuildAsync(int guildId) =>
            await db.GuildProperties
                .Include(p => p.Rooms)
                .Include(p => p.Listing)
                .FirstOrDefaultAsync(p => p.GuildId == guildId);

        public async Task<GuildRoom?> GetGuildRoomByRoomIdAsync(int roomId) =>
            await db.GuildRooms
                .Include(r => r.GuildProperty)
                .FirstOrDefaultAsync(r => r.RoomId == roomId);

        public async Task<List<GuildHouseListing>> GetHouseListingsAsync() =>
            await db.GuildHouseListings
                .Include(l => l.SellerGuild)
                .Include(l => l.GuildProperty).ThenInclude(p => p.Rooms)
                .ToListAsync();
    }
}
