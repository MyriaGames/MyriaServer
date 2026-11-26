using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.World;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;

namespace Myria.Server.Realm.Services
{
    /// <summary>
    /// Dynamic quests (v0.3 quest rework): computes what an NPC or guild house offers from the live
    /// world state, hands out grants, and tracks/persists them. Every open grant's quest is
    /// registered with QuestManager, so character loading and the normal quest flow resolve
    /// "dyn.*" ids like any other quest.
    /// <para>
    /// The rule that decides how widely a quest is handed out: an offer exists while the gap (NPC
    /// stock below target, room population above rest level) minus what's already promised by
    /// outstanding grants is still positive. A grant older than a week stops counting as promised,
    /// so its share is offered again - the holder's copy stays completable. One open grant per
    /// player per gap, so a single player can't take the whole gap.
    /// </para>
    /// </summary>
    public class DynamicQuestService(IServiceScopeFactory scopeFactory, ILogger<DynamicQuestService> logger)
    {
        private readonly object _sync = new();
        private readonly List<QuestGrant> _open = new();

        /// <summary>Settable for tests (staleness depends on it).</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        public async Task LoadAsync()
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.QuestGrants.AsNoTracking().Where(g => !g.Completed).ToListAsync();
            lock (_sync)
            {
                _open.Clear();
                foreach (var row in rows)
                {
                    var grant = FromRecord(row);
                    _open.Add(grant);
                    QuestManager.RegisterDynamic(DynamicQuests.BuildQuest(grant));
                }
            }
            logger.LogInformation("Dynamic quests loaded: {Count} open grants", rows.Count);
        }

        public QuestGrant? Get(string grantId)
        {
            lock (_sync) return _open.FirstOrDefault(g => g.Id == grantId);
        }

        // ── Offers ────────────────────────────────────────────────────────────

        /// <summary>Fetch quests an NPC instance is offering <paramref name="holder"/> right now.</summary>
        public List<(string Token, Quest Preview)> FetchOffers(WorldState world, int roomId, string npcId, string holder)
        {
            lock (_sync)
                return world.StockKeys
                    .Where(k => k.RoomId == roomId && k.NpcId == npcId)
                    .Select(k => (k, size: DynamicQuests.GrantSize(Remaining(world, k), GrantKind.Fetch)))
                    .Where(x => x.size > 0 && !HoldsOpen(holder, x.k))
                    .Select(x => (DynamicQuests.Token(x.k), Preview(GrantKind.Fetch, x.k.RoomId, x.k.NpcId, x.k.ItemId, null, x.size)))
                    .ToList();
        }

        /// <summary>Bounties a guild house in <paramref name="guildRoomId"/> is offering: overrun
        /// overworld rooms within <see cref="DynamicQuestRules.GuildRadius"/> exits.</summary>
        public List<(string Token, Quest Preview)> BountyOffers(WorldState world, int guildRoomId, string holder)
        {
            var near = DynamicQuests.RoomsWithin(guildRoomId, DynamicQuestRules.GuildRadius);
            lock (_sync)
                return world.PopulationKeys
                    .Where(k => near.Contains(k.RoomId))
                    .Select(k => (k, size: DynamicQuests.GrantSize(Remaining(world, k), GrantKind.Kill)))
                    .Where(x => x.size > 0 && !HoldsOpen(holder, x.k))
                    .Select(x => (DynamicQuests.Token(x.k), Preview(GrantKind.Kill, x.k.RoomId, null, null, x.k.MonsterId, x.size)))
                    .ToList();
        }

        // ── Accept / complete ─────────────────────────────────────────────────

        /// <summary>
        /// Hands out a grant for the gap a token names, sized against what's remaining *now* (others
        /// may have accepted since the offer was shown). Null if nothing is left or the holder already
        /// has one for this gap. The caller checks that the holder is allowed to take it here.
        /// </summary>
        public async Task<QuestGrant?> AcceptAsync(WorldState world, string token, string holder)
        {
            if (!DynamicQuests.TryParseToken(token, out var stock, out var pop)) return null;

            QuestGrant grant;
            lock (_sync)
            {
                GrantKind kind = stock is not null ? GrantKind.Fetch : GrantKind.Kill;
                int remaining = stock is { } s ? Remaining(world, s) : Remaining(world, pop!.Value);
                int size = DynamicQuests.GrantSize(remaining, kind);
                bool held = stock is { } hs ? HoldsOpen(holder, hs) : HoldsOpen(holder, pop!.Value);
                if (size <= 0 || held) return null;

                var (xp, gold) = DynamicQuests.Rewards(kind, size, stock?.ItemId, pop?.MonsterId);
                grant = new QuestGrant
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = kind,
                    RoomId = stock?.RoomId ?? pop!.Value.RoomId,
                    NpcId = stock?.NpcId,
                    ItemId = stock?.ItemId,
                    MonsterId = pop?.MonsterId,
                    Target = size,
                    RewardXp = xp,
                    RewardGold = gold,
                    HolderName = holder,
                    AcceptedUtc = Clock(),
                };
                _open.Add(grant); // counted as promised from this moment, before the save
            }

            QuestManager.RegisterDynamic(DynamicQuests.BuildQuest(grant));
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.QuestGrants.Add(ToRecord(grant));
            await db.SaveChangesAsync();
            return grant;
        }

        /// <summary>Marks a grant done (handed in): it stops counting as promised, and its quest
        /// definition is no longer registered.</summary>
        public async Task CompleteAsync(string grantId)
        {
            lock (_sync)
            {
                var grant = _open.FirstOrDefault(g => g.Id == grantId);
                if (grant is null) return;
                grant.Completed = true;
                _open.Remove(grant);
            }
            QuestManager.UnregisterDynamic(DynamicQuests.QuestIdPrefix + grantId);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.QuestGrants.Where(g => g.Id == grantId).ExecuteUpdateAsync(s => s.SetProperty(g => g.Completed, true));
        }

        // ── Internals (callers hold _sync) ────────────────────────────────────

        private int Remaining(WorldState world, StockKey k) =>
            world.StockGap(k) - DynamicQuests.Promised(_open, k, Clock());

        private int Remaining(WorldState world, PopulationKey k) =>
            world.PopulationGap(k) - DynamicQuests.Promised(_open, k, Clock());

        private bool HoldsOpen(string holder, StockKey k) =>
            _open.Any(g => g.StockKey == k && string.Equals(g.HolderName, holder, StringComparison.OrdinalIgnoreCase));

        private bool HoldsOpen(string holder, PopulationKey k) =>
            _open.Any(g => g.PopulationKey == k && string.Equals(g.HolderName, holder, StringComparison.OrdinalIgnoreCase));

        private static Quest Preview(GrantKind kind, int roomId, string? npcId, string? itemId, int? monsterId, int size)
        {
            var (xp, gold) = DynamicQuests.Rewards(kind, size, itemId, monsterId);
            return DynamicQuests.BuildQuest(new QuestGrant
            {
                Id = "preview", Kind = kind, RoomId = roomId, NpcId = npcId, ItemId = itemId, MonsterId = monsterId,
                Target = size, RewardXp = xp, RewardGold = gold, HolderName = "", AcceptedUtc = DateTime.MinValue,
            });
        }

        private static QuestGrant FromRecord(QuestGrantRecord r) => new()
        {
            Id = r.Id, Kind = (GrantKind)r.Kind, RoomId = r.RoomId, NpcId = r.NpcId, ItemId = r.ItemId,
            MonsterId = r.MonsterId, Target = r.Target, RewardXp = r.RewardXp, RewardGold = r.RewardGold,
            HolderName = r.HolderName, AcceptedUtc = r.AcceptedUtc, Completed = r.Completed,
        };

        private static QuestGrantRecord ToRecord(QuestGrant g) => new()
        {
            Id = g.Id, Kind = (int)g.Kind, RoomId = g.RoomId, NpcId = g.NpcId, ItemId = g.ItemId,
            MonsterId = g.MonsterId, Target = g.Target, RewardXp = g.RewardXp, RewardGold = g.RewardGold,
            HolderName = g.HolderName, AcceptedUtc = g.AcceptedUtc, Completed = g.Completed,
        };
    }
}
