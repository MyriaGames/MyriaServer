using Myria.Lib.Core.Systems;
using System.Collections.Concurrent;
using System.Linq;

namespace Myria.Server.Realm.Services
{
    public class GroupCombatService
    {
        private readonly ConcurrentDictionary<string, GroupCombatEncounter> _byParty = new();
        private readonly ConcurrentDictionary<string, string> _connToParty = new();

        // ConcurrentDictionary makes each individual dictionary operation atomic, but several
        // methods below are check-then-act sequences spanning two or more operations (e.g.
        // AddConnection's "is the fight still there" check, then a separate write) - not
        // automatically atomic just because the underlying type is thread-safe. The clearest
        // instance (found in the 2026-09-10 security/robustness audit): AddConnection checks
        // _byParty.ContainsKey(fightId) and then, as a *separate* step, writes to _connToParty;
        // if RemoveByParty (removing the fight and cleaning up every connection pointed at it)
        // runs on another connection's hub call in between those two steps, AddConnection's write
        // lands *after* the cleanup already ran, orphaning the joiner's connId pointing at a
        // fightId whose _byParty entry no longer exists - GetEncounter then returns null for that
        // connection's every subsequent GroupCharacterAttack/CastSkill call, silently soft-locking
        // their turn (same failure class as item 52's fix, just a narrower window). Wrapping the
        // multi-step methods in one lock closes this; the plain single-dictionary-read getters
        // below (HasEncounter, GetPartyId, GetEncounter, GetConnections) don't need it - a single
        // ConcurrentDictionary read is already atomic on its own.
        private readonly object _lock = new();

        public bool HasEncounter(string partyId) => _byParty.ContainsKey(partyId);

        public string? GetPartyId(string connId) =>
            _connToParty.TryGetValue(connId, out var id) ? id : null;

        public GroupCombatEncounter? GetEncounter(string partyId) =>
            _byParty.TryGetValue(partyId, out var enc) ? enc : null;

        public bool Start(string partyId, IEnumerable<string> memberConns, GroupCombatEncounter encounter)
        {
            lock (_lock)
            {
                if (!_byParty.TryAdd(partyId, encounter)) return false;
                foreach (var c in memberConns)
                    _connToParty[c] = partyId;
                return true;
            }
        }

        /// <summary>
        /// Registers one more connection against an already-running fight — used when a party
        /// member joins an in-progress encounter (rather than starting a new one) because they
        /// pressed "Start Group Fight" while their party was already fighting in the same room.
        /// No-ops if the fight itself doesn't exist (caller should check first).
        /// </summary>
        public void AddConnection(string fightId, string connId)
        {
            lock (_lock)
            {
                if (_byParty.ContainsKey(fightId))
                    _connToParty[connId] = fightId;
            }
        }

        /// <summary>All connections currently registered to the given fight, for broadcasting to
        /// exactly the players actually in it — not the party's broader, room-independent group.</summary>
        public IReadOnlyList<string> GetConnections(string fightId) =>
            _connToParty.Where(kv => kv.Value == fightId).Select(kv => kv.Key).ToList();

        /// <summary>
        /// Re-points a (re)connected connection at the party fight its character is already
        /// participating in, if any. A fresh connection ID (from a SignalR auto-reconnect, or
        /// simply calling LoadCharacter again) is never automatically re-associated with an
        /// in-progress GroupCombatEncounter - without this, the character keeps receiving
        /// GroupCombatUpdated broadcasts fine (SignalR group membership is re-added elsewhere),
        /// but every GroupCharacterAttack/CastSkill call from the new connection silently no-ops
        /// via GetPartyId returning null, making that player's turn look permanently stuck.
        /// </summary>
        public void ReassignConnection(string characterName, string newConnId)
        {
            lock (_lock)
            {
                foreach (var (partyId, enc) in _byParty)
                {
                    if (enc.Characters.Any(c => string.Equals(c.Name, characterName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _connToParty[newConnId] = partyId;
                        return;
                    }
                }
            }
        }

        public GroupCombatEncounter? RemoveByParty(string partyId)
        {
            lock (_lock)
            {
                _byParty.TryRemove(partyId, out var enc);
                var stale = _connToParty.Where(kv => kv.Value == partyId).Select(kv => kv.Key).ToList();
                foreach (var c in stale) _connToParty.TryRemove(c, out _);
                return enc;
            }
        }

        public string? RemoveConn(string connId)
        {
            lock (_lock)
            {
                if (!_connToParty.TryRemove(connId, out var partyId)) return null;
                bool anyLeft = _connToParty.Values.Contains(partyId);
                if (!anyLeft) _byParty.TryRemove(partyId, out _);
                return partyId;
            }
        }
    }
}
