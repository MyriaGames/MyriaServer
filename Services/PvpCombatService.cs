using Myria.Lib.Core.Systems;
using System.Collections.Concurrent;

namespace Myria.Server.ClassicRealm.Services
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
        // characterName -> (fightId, since, account): participants whose connection dropped mid-fight
        // (v0.3). The account is kept because saving looks it up through the (now gone) connection.
        private readonly ConcurrentDictionary<string, (string FightId, DateTime Since, string? Account)> _away = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How long a participant who lost their connection may take to come back before it
        /// counts as a forfeit (v0.3; was an immediate forfeit).</summary>
        public static TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromMinutes(2);
        public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

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

        // ── Lost connections (v0.3) ─────────────────────────────────────────

        /// <summary>Takes a dropped connection out of its fight's broadcast list and starts the
        /// participant's grace period. False if the connection wasn't in a fight.</summary>
        public bool MarkAway(string connId, string characterName, string? account)
        {
            lock (_lock)
            {
                if (!_connToFight.TryRemove(connId, out var fightId)) return false;
                _away[characterName] = (fightId, Clock(), account);
                return true;
            }
        }

        /// <summary>The account of a character who is away (for saving them without a connection).</summary>
        public string? GetAwayAccount(string characterName) =>
            _away.TryGetValue(characterName, out var a) ? a.Account : null;

        public string? GetAwayFight(string characterName) =>
            _away.TryGetValue(characterName, out var a) ? a.FightId : null;

        public bool IsAwayExpired(string characterName) =>
            _away.TryGetValue(characterName, out var a) && Clock() - a.Since >= ReconnectGrace;

        /// <summary>Back within the grace period: the new connection joins the fight again. Returns
        /// the fight id, or null if the character wasn't away or the fight is over.</summary>
        public string? Rejoin(string characterName, string newConnId)
        {
            lock (_lock)
            {
                if (!_away.TryRemove(characterName, out var a) || !_fights.ContainsKey(a.FightId)) return null;
                _connToFight[newConnId] = a.FightId;
                return a.FightId;
            }
        }

        public void ClearAway(string characterName) => _away.TryRemove(characterName, out _);

        /// <summary>Everyone currently away from this fight.</summary>
        public IReadOnlyList<string> AwayIn(string fightId) =>
            _away.Where(kv => kv.Value.FightId == fightId).Select(kv => kv.Key).ToList();

        /// <summary>Everyone away from this fight for longer than <see cref="ReconnectGrace"/>.</summary>
        public IReadOnlyList<string> ExpiredAwayIn(string fightId) =>
            _away.Where(kv => kv.Value.FightId == fightId && Clock() - kv.Value.Since >= ReconnectGrace)
                 .Select(kv => kv.Key).ToList();

        public PvpEncounter? Remove(string fightId)
        {
            lock (_lock)
            {
                _fights.TryRemove(fightId, out var enc);
                foreach (var name in _away.Where(kv => kv.Value.FightId == fightId).Select(kv => kv.Key).ToList())
                    _away.TryRemove(name, out _);
                foreach (var c in _connToFight.Where(kv => kv.Value == fightId).Select(kv => kv.Key).ToList())
                    _connToFight.TryRemove(c, out _);
                return enc;
            }
        }

        // Reconnecting works through MarkAway/Rejoin (GameHub.OnDisconnectedAsync / LoadCharacter):
        // the character's live session is kept during the grace period, so the encounter's
        // Character instance stays the session's.
    }
}
