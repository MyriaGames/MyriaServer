using System.Collections.Concurrent;

namespace Myria.Server.Realm.Services
{
    public class PartyService
    {
        private class Party
        {
            public required string Id { get; init; }
            public string LeaderUsername { get; set; } = string.Empty;
            public List<string> Members { get; } = new();
        }

        private readonly ConcurrentDictionary<string, Party> _parties = new();
        private readonly ConcurrentDictionary<string, string> _memberToParty = new();
        private readonly object _lock = new();

        public const int MaxSize = 5;

        // Creates a new party for the leader, or returns their existing party.
        public (string partyId, IReadOnlyList<string> members, string leader) GetOrCreate(string leaderUsername)
        {
            lock (_lock)
            {
                if (_memberToParty.TryGetValue(leaderUsername, out var existingId))
                {
                    var existing = _parties[existingId];
                    return (existingId, existing.Members.ToList(), existing.LeaderUsername);
                }

                var partyId = Guid.NewGuid().ToString("N")[..8];
                var p = new Party { Id = partyId, LeaderUsername = leaderUsername };
                p.Members.Add(leaderUsername);
                _parties[partyId] = p;
                _memberToParty[leaderUsername] = partyId;
                return (partyId, p.Members.ToList(), leaderUsername);
            }
        }

        public (bool success, IReadOnlyList<string> members, string? leader) Join(string partyId, string username)
        {
            lock (_lock)
            {
                if (_memberToParty.ContainsKey(username)) return (false, [], null);
                if (!_parties.TryGetValue(partyId, out var p)) return (false, [], null);
                if (p.Members.Count >= MaxSize) return (false, [], null);
                p.Members.Add(username);
                _memberToParty[username] = partyId;
                return (true, p.Members.ToList(), p.LeaderUsername);
            }
        }

        // Returns remaining members, whether party was disbanded, and new leader if leadership transferred.
        public (IReadOnlyList<string> remaining, bool disbanded, string? newLeader, string partyId) Leave(string username)
        {
            lock (_lock)
            {
                if (!_memberToParty.TryRemove(username, out var partyId)) return ([], false, null, string.Empty);
                if (!_parties.TryGetValue(partyId, out var p)) return ([], false, null, partyId);

                p.Members.Remove(username);

                // Disband when fewer than 2 members remain — a solo "party" is a dead end.
                if (p.Members.Count < 2)
                {
                    foreach (var m in p.Members)
                        _memberToParty.TryRemove(m, out _);
                    _parties.TryRemove(partyId, out _);
                    return (p.Members.ToList(), true, null, partyId);
                }

                string? newLeader = null;
                if (p.LeaderUsername == username)
                {
                    newLeader = p.Members[0];
                    p.LeaderUsername = newLeader;
                }

                return (p.Members.ToList(), false, newLeader, partyId);
            }
        }

        // Kicks target from the party — caller must be the leader.
        // Returns (success, remainingMembers, disbanded, newLeader, partyId).
        public (bool success, IReadOnlyList<string> remaining, bool disbanded, string? leader, string partyId) Kick(string leaderUsername, string targetUsername)
        {
            lock (_lock)
            {
                if (!_memberToParty.TryGetValue(leaderUsername, out var partyId)) return (false, [], false, null, string.Empty);
                if (!_parties.TryGetValue(partyId, out var p))                    return (false, [], false, null, string.Empty);
                if (p.LeaderUsername != leaderUsername)                            return (false, [], false, null, string.Empty);
                if (!p.Members.Contains(targetUsername))                           return (false, [], false, null, string.Empty);
                if (targetUsername == leaderUsername)                              return (false, [], false, null, string.Empty);

                p.Members.Remove(targetUsername);
                _memberToParty.TryRemove(targetUsername, out _);

                if (p.Members.Count < 2)
                {
                    foreach (var m in p.Members) _memberToParty.TryRemove(m, out _);
                    _parties.TryRemove(partyId, out _);
                    return (true, p.Members.ToList(), true, null, partyId);
                }

                return (true, p.Members.ToList(), false, p.LeaderUsername, partyId);
            }
        }

        // Transfers leadership — caller must be current leader and target must be in the party.
        public (bool success, IReadOnlyList<string> members, string leader) TransferLeader(string currentLeader, string newLeader)
        {
            lock (_lock)
            {
                if (!_memberToParty.TryGetValue(currentLeader, out var partyId)) return (false, [], string.Empty);
                if (!_parties.TryGetValue(partyId, out var p))                   return (false, [], string.Empty);
                if (p.LeaderUsername != currentLeader)                            return (false, [], string.Empty);
                if (!p.Members.Contains(newLeader))                              return (false, [], string.Empty);

                p.LeaderUsername = newLeader;
                return (true, p.Members.ToList(), newLeader);
            }
        }

        public bool IsInParty(string username) => _memberToParty.ContainsKey(username);

        public string? GetPartyId(string username) =>
            _memberToParty.TryGetValue(username, out var id) ? id : null;

        public IReadOnlyList<string> GetMembers(string partyId)
        {
            lock (_lock)
                return _parties.TryGetValue(partyId, out var p) ? p.Members.ToList() : [];
        }

        public string? GetLeader(string partyId)
        {
            lock (_lock)
                return _parties.TryGetValue(partyId, out var p) ? p.LeaderUsername : null;
        }

        public static string PartyGroup(string partyId) => $"party_{partyId}";
    }
}
