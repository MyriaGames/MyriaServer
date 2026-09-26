using System.ComponentModel.DataAnnotations;

namespace Myria.Server.Realm.Models
{
    // Bars an ACCOUNT from playing on this realm (hub connection + realm REST API). It lives on the
    // realm, keyed by username like Character.UserId, and has no counterpart in the auth service:
    // the account can still log in there and manage itself (password, username, deletion) - it just
    // gets refused here. Row exists = banned until BannedUntil; expired rows are simply ignored.
    public class AccountBan
    {
        public int Id { get; set; }

        [MaxLength(50)]
        public string Username { get; set; } = string.Empty;

        public DateTime BannedUntil { get; set; }

        [MaxLength(500)]
        public string? Reason { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class Bans
    {
        /// <summary>"Permanent" is stored as a far-future date, so every check stays a plain comparison.</summary>
        public static readonly DateTime Permanent = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        public static bool IsPermanent(DateTime until) => until >= Permanent;

        public static bool IsActive(DateTime? until, DateTime nowUtc) => until is not null && until > nowUtc;

        /// <summary>Null/non-positive minutes means permanent.</summary>
        public static DateTime UntilFromMinutes(int? minutes, DateTime nowUtc) =>
            minutes is > 0 ? nowUtc.AddMinutes(Math.Min(minutes.Value, 60 * 24 * 365 * 10)) : Permanent;
    }
}
