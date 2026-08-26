using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Myria.Server.Realm.Services
{
    public class CharacterPresenceService
    {
        private record ConnectionState(string AccountUsername, string CharacterName, int? RoomId);
        private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();
        private readonly ConcurrentDictionary<string, HubCallerContext> _contexts = new();

        public void RegisterContext(string connectionId, HubCallerContext ctx) => _contexts[connectionId] = ctx;
        public void UnregisterContext(string connectionId) => _contexts.TryRemove(connectionId, out _);
        public HubCallerContext? GetContext(string connectionId) =>
            _contexts.TryGetValue(connectionId, out var ctx) ? ctx : null;

        public void Connect(string connectionId, string accountUsername) =>
            _connections[connectionId] = new ConnectionState(accountUsername, string.Empty, null);

        public void Disconnect(string connectionId) =>
            _connections.TryRemove(connectionId, out _);

        public void SetCharacterName(string connectionId, string characterName)
        {
            if (_connections.TryGetValue(connectionId, out var state))
                _connections[connectionId] = state with { CharacterName = characterName };
        }

        public void SetRoom(string connectionId, int roomId)
        {
            if (_connections.TryGetValue(connectionId, out var state))
                _connections[connectionId] = state with { RoomId = roomId };
        }

        public int GetTotalCount() => _connections.Count;

        // The in-game display name: character name if registered, otherwise account username.
        public string? GetDisplayName(string connectionId) =>
            _connections.TryGetValue(connectionId, out var s)
                ? (string.IsNullOrEmpty(s.CharacterName) ? s.AccountUsername : s.CharacterName)
                : null;

        public string? GetAccountUsername(string connectionId) =>
            _connections.TryGetValue(connectionId, out var s) ? s.AccountUsername : null;

        public int? GetRoom(string connectionId) =>
            _connections.TryGetValue(connectionId, out var s) ? s.RoomId : null;

        public IReadOnlyList<string> GetCharactersInRoom(int roomId) =>
            _connections.Values
                .Where(s => s.RoomId == roomId)
                .Select(s => string.IsNullOrEmpty(s.CharacterName) ? s.AccountUsername : s.CharacterName)
                .ToList();

        // Find connection by account username (used for friend requests, auth-level ops).
        public string? GetConnectionByAccount(string accountUsername)
        {
            foreach (var kv in _connections)
                if (string.Equals(kv.Value.AccountUsername, accountUsername, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            return null;
        }

        // Find connection by character name or account username (whisper, party invites).
        public string? GetConnectionByName(string name)
        {
            foreach (var kv in _connections)
                if (string.Equals(kv.Value.CharacterName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Value.AccountUsername, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            return null;
        }

        // Get online character name for a given account (used by FriendsController).
        public string? GetCharacterNameByAccount(string accountUsername)
        {
            foreach (var kv in _connections)
                if (string.Equals(kv.Value.AccountUsername, accountUsername, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrEmpty(kv.Value.CharacterName) ? null : kv.Value.CharacterName;
            return null;
        }

        public bool IsAccountOnline(string accountUsername) =>
            _connections.Values.Any(s =>
                string.Equals(s.AccountUsername, accountUsername, StringComparison.OrdinalIgnoreCase));

        public int? GetRoomByAccount(string accountUsername)
        {
            foreach (var kv in _connections)
                if (string.Equals(kv.Value.AccountUsername, accountUsername, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.RoomId;
            return null;
        }

        public bool IsCharacterOnline(string characterName) =>
            _connections.Values.Any(s =>
                string.Equals(s.CharacterName, characterName, StringComparison.OrdinalIgnoreCase));

        public int? GetRoomByCharacter(string characterName)
        {
            foreach (var kv in _connections)
                if (string.Equals(kv.Value.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.RoomId;
            return null;
        }
    }
}
