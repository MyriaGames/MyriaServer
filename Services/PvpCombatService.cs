using Myria.Lib.Core.Systems;
using System.Collections.Concurrent;

namespace Myria.Server.Realm.Services
{
    /// <summary>
    /// Live PvP fights: fightId -> encounter, and connection -> fightId for every participant's
    /// connection. Same shape and locking approach as GroupCombatService (multi-step operations
    /// under one lock, single reads lock-free), kept separate because the encounter type differs.
    /// </summary>
    public class PvpCombatService
    {
        private readonly ConcurrentDictionary<string, PvpEncounter> _fights = new();
        private readonly ConcurrentDictionary<string, string> _connToFight = new();
        private readonly object _lock = new();

        public string? GetFightId(string connId) =>
            _connToFight.TryGetValue(connId, out var id) ? id : null;

        public PvpEncounter? GetEncounter(string fightId) =>
            _fights.TryGetValue(fightId, out var enc) ? enc : null;

        public PvpEncounter? GetEncounterForConnection(string connId) =>
            GetFightId(connId) is { } id ? GetEncounter(id) : null;

        public bool IsInFight(string connId) => _connToFight.ContainsKey(connId);

        /// <summary>Registers a new fight. Fails (registering nothing) if any of the connections is
        /// already in a PvP fight - the caller's own busy checks run earlier, this closes the race
        /// of two challenges for the same player landing at once.</summary>
        public bool Start(string fightId, IReadOnlyCollection<string> conns, PvpEncounter encounter)
        {
            lock (_lock)
            {
                if (conns.Any(_connToFight.ContainsKey)) return false;
                if (!_fights.TryAdd(fightId, encounter)) return false;
                foreach (var c in conns) _connToFight[c] = fightId;
                return true;
            }
        }

        public IReadOnlyList<string> GetConnections(string fightId) =>
            _connToFight.Where(kv => kv.Value == fightId).Select(kv => kv.Key).ToList();

        /// <summary>A participant leaves the fight's broadcast list (fled/disconnected); the fight
        /// itself stays registered until <see cref="Remove"/>.</summary>
        public void RemoveConnection(string connId)
        {
            lock (_lock) _connToFight.TryRemove(connId, out _);
        }

        public PvpEncounter? Remove(string fightId)
        {
            lock (_lock)
            {
                _fights.TryRemove(fightId, out var enc);
                foreach (var c in _connToFight.Where(kv => kv.Value == fightId).Select(kv => kv.Key).ToList())
                    _connToFight.TryRemove(c, out _);
                return enc;
            }
        }

        // No ReassignConnection (unlike GroupCombatService): a disconnect forfeits the PvP fight
        // immediately (GameHub.OnDisconnectedAsync), so a reconnecting character never has a live
        // fight to be re-pointed at.
    }
}
