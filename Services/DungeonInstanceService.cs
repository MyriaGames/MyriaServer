using System.Collections.Concurrent;
using Myria.Lib.Core.Systems.Dungeons;

namespace Myria.Server.Realm.Services
{
    /// <summary>
    /// Dungeon instances (v0.3): one <see cref="DungeonRun"/> per owner (a party, or a player on
    /// their own) per dungeon, so groups never share - or steal - each other's rooms. A run is
    /// dropped once none of its owner's members is inside that dungeon any more (left, or died and
    /// respawned outside); going back in starts fresh. In memory only: a restart resets every run.
    /// </summary>
    public class DungeonInstanceService
    {
        private readonly ConcurrentDictionary<string, DungeonRun> _runs = new(StringComparer.OrdinalIgnoreCase);

        public static string PartyOwner(string partyId) => "party:" + partyId;
        public static string SoloOwner(string characterName) => "solo:" + characterName;

        private static string Key(string owner, string dungeonId) => owner + "|" + dungeonId;

        public DungeonRun GetOrCreate(string owner, string dungeonId) =>
            _runs.GetOrAdd(Key(owner, dungeonId), _ => new DungeonRun(dungeonId));

        public DungeonRun? Get(string owner, string dungeonId) =>
            _runs.TryGetValue(Key(owner, dungeonId), out var run) ? run : null;

        public void Reset(string owner, string dungeonId) => _runs.TryRemove(Key(owner, dungeonId), out _);

        public int Count => _runs.Count;
    }
}
