using System.ComponentModel.DataAnnotations;

namespace Myria.Server.Realm.Models.Dto
{
    // Wire shapes of the internal operator API (Controllers/AdminController), consumed by the
    // localhost admin site. Permanent bans are reported as Permanent = true rather than leaking the
    // far-future sentinel date.
    public record RealmBanInfo(DateTime Until, bool Permanent, string? Reason)
    {
        public static RealmBanInfo? From(DateTime? until, string? reason, DateTime nowUtc) =>
            Bans.IsActive(until, nowUtc) ? new RealmBanInfo(until!.Value, Bans.IsPermanent(until.Value), reason) : null;
    }

    public record RealmCharacterInfo(
        string Name, string Username, int Level, string Class, string Race,
        DateTime LastSaved, bool Online, RealmBanInfo? Ban);

    public record RealmAccountInfo(
        string Username, bool Online, string? OnlineCharacter,
        RealmBanInfo? Ban, List<RealmCharacterInfo> Characters);

    public record AccountBanInfo(string Username, RealmBanInfo Ban);

    public record CharacterBanInfo(string Character, string Username, RealmBanInfo Ban);

    public record RealmBansInfo(List<AccountBanInfo> Accounts, List<CharacterBanInfo> Characters);

    public record OnlinePlayer(string Account, string? Character);

    public class BanRequest
    {
        /// <summary>Ban length in minutes; omitted/0 = permanent.</summary>
        [Range(0, 60 * 24 * 365 * 10)]
        public int? Minutes { get; set; }

        [MaxLength(500)]
        public string? Reason { get; set; }
    }
}
