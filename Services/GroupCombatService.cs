using Myria.Lib.Core.Systems;
using System.Collections.Concurrent;
using System.Linq;

namespace Myria.Server.Realm.Services
{
    public class GroupCombatService
    {
        private readonly ConcurrentDictionary<string, GroupCombatEncounter> _byParty = new();
        private readonly ConcurrentDictionary<string, string> _connToParty = new();

        public bool HasEncounter(string partyId) => _byParty.ContainsKey(partyId);

        public string? GetPartyId(string connId) =>
            _connToParty.TryGetValue(connId, out var id) ? id : null;

        public GroupCombatEncounter? GetEncounter(string partyId) =>
            _byParty.TryGetValue(partyId, out var enc) ? enc : null;

        public bool Start(string partyId, IEnumerable<string> memberConns, GroupCombatEncounter encounter)
        {
            if (!_byParty.TryAdd(partyId, encounter)) return false;
            foreach (var c in memberConns)
                _connToParty[c] = partyId;
            return true;
        }

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
            foreach (var (partyId, enc) in _byParty)
            {
                if (enc.Characters.Any(c => string.Equals(c.Name, characterName, StringComparison.OrdinalIgnoreCase)))
                {
                    _connToParty[newConnId] = partyId;
                    return;
                }
            }
        }

        public GroupCombatEncounter? RemoveByParty(string partyId)
        {
            _byParty.TryRemove(partyId, out var enc);
            var stale = _connToParty.Where(kv => kv.Value == partyId).Select(kv => kv.Key).ToList();
            foreach (var c in stale) _connToParty.TryRemove(c, out _);
            return enc;
        }

        public string? RemoveConn(string connId)
        {
            if (!_connToParty.TryRemove(connId, out var partyId)) return null;
            bool anyLeft = _connToParty.Values.Contains(partyId);
            if (!anyLeft) _byParty.TryRemove(partyId, out _);
            return partyId;
        }
    }
}
