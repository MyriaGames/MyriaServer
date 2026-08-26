using System.Collections.Concurrent;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Systems;

namespace Myria.Server.Realm.Services
{
    /// <summary>
    /// Holds server-authoritative player state for every active connection.
    /// Keyed by SignalR connection ID (string). All mutations are lock-protected per entry.
    /// </summary>
    public class CharacterSessionService
    {
        private readonly ConcurrentDictionary<string, Character> _players = new();
        // connectionId → (roomId → (date, remaining))
        private readonly ConcurrentDictionary<string, Dictionary<int, (DateTime Date, int Remaining)>> _gatherState = new();
        private readonly ConcurrentDictionary<string, CombatEncounter> _encounters = new();
        // characterName → the connectionId currently holding that character's live session.
        private readonly ConcurrentDictionary<string, string> _connectionByCharacter = new(StringComparer.OrdinalIgnoreCase);

        public bool TryAdd(string connectionId, Character player)
        {
            _gatherState[connectionId] = new Dictionary<int, (DateTime, int)>();
            _connectionByCharacter[player.Name] = connectionId;
            return _players.TryAdd(connectionId, player);
        }

        public Character? Get(string connectionId)
            => _players.TryGetValue(connectionId, out var p) ? p : null;

        /// <summary>The live connection currently holding this character's session, if any -
        /// used to target a push (e.g. CharacterUpdated) at a specific party member by name
        /// rather than the caller's own connection.</summary>
        public string? GetConnectionId(string characterName)
            => _connectionByCharacter.TryGetValue(characterName, out var connId) ? connId : null;

        /// <summary>Removes the session and returns the player for saving, or null if none existed.</summary>
        public Character? Remove(string connectionId)
        {
            _players.TryRemove(connectionId, out var p);
            _gatherState.TryRemove(connectionId, out _);
            _encounters.TryRemove(connectionId, out _);
            if (p != null)
                _connectionByCharacter.TryRemove(new KeyValuePair<string, string>(p.Name, connectionId));
            return p;
        }

        /// <summary>
        /// If this character already has a live session under a different connection - e.g. a
        /// SignalR auto-reconnect (very easy to trigger by pausing at a breakpoint past the
        /// keep-alive timeout) got a new connection ID before the old one's OnDisconnectedAsync
        /// finished cleaning up - moves that in-memory session onto the new connection instead of
        /// discarding it. A fresh database reload here would silently drop any XP/level/etc.
        /// gained since the last save. Returns the reattached character, or null if no live
        /// session exists for this character (caller should fall back to a DB load).
        /// </summary>
        public Character? TryReattach(string characterName, string newConnectionId)
        {
            if (!_connectionByCharacter.TryGetValue(characterName, out var oldConnectionId))
                return null;
            if (oldConnectionId == newConnectionId)
                return Get(newConnectionId);
            if (!_players.TryRemove(oldConnectionId, out var player))
                return null;

            _players[newConnectionId] = player;
            _connectionByCharacter[characterName] = newConnectionId;

            if (_encounters.TryRemove(oldConnectionId, out var encounter))
                _encounters[newConnectionId] = encounter;

            _gatherState[newConnectionId] = _gatherState.TryRemove(oldConnectionId, out var gather)
                ? gather
                : new Dictionary<int, (DateTime, int)>();

            return player;
        }

        /// <summary>
        /// Attempts to consume one gather charge for the given room.
        /// Resets the per-player daily counter when the UTC date has rolled over.
        /// </summary>
        public bool TryConsumeGather(string connectionId, int roomId, int dailyLimit, out int remaining)
        {
            var state = _gatherState.GetOrAdd(connectionId, _ => new());
            lock (state)
            {
                var today = DateTime.UtcNow.Date;
                if (!state.TryGetValue(roomId, out var entry) || entry.Date != today)
                    entry = (today, dailyLimit);

                if (entry.Remaining <= 0) { remaining = 0; return false; }
                int newRemaining = entry.Remaining - 1;
                state[roomId] = (entry.Date, newRemaining);
                remaining = newRemaining;
                return true;
            }
        }

        public CombatEncounter? GetEncounter(string connectionId)
            => _encounters.TryGetValue(connectionId, out var e) ? e : null;

        public void SetEncounter(string connectionId, CombatEncounter encounter)
            => _encounters[connectionId] = encounter;

        public void RemoveEncounter(string connectionId)
            => _encounters.TryRemove(connectionId, out _);
    }
}
