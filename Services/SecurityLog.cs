using System.Text;
using Microsoft.EntityFrameworkCore;
using Myria.Server.ClassicRealm.Data;

namespace Myria.Server.ClassicRealm.Services
{
    /// <summary>
    /// Security events in the same fixed, parseable shape as the auth server (security review 2026-11-26,
    /// A09 F2): <c>security &lt;event&gt; user=&lt;name&gt; outcome=&lt;outcome&gt; detail=&lt;text&gt;</c>.
    /// Refusals are Warnings, allowed operator calls Information. Never tokens or secrets, no IP addresses
    /// (privacy policy §2.3). The admin site's dashboard counts these lines from the journal.
    /// </summary>
    public static class SecurityLog
    {
        public const string Template = "security {Event} user={User} outcome={Outcome} detail={Detail}";
        public const string Category = "Myria.Server.ClassicRealm.Security";

        private const char LineSeparator = (char)0x2028;
        private const char ParagraphSeparator = (char)0x2029;

        public static void Warn(ILogger? logger, string evt, string? user, string outcome, string? detail = null) =>
            logger?.LogWarning(Template, evt, Safe(user), Safe(outcome), Safe(detail));

        public static void Info(ILogger? logger, string evt, string? user, string outcome, string? detail = null) =>
            logger?.LogInformation(Template, evt, Safe(user), Safe(outcome), Safe(detail));

        /// <summary>The security logger of a request (null when there is none, e.g. in unit tests).</summary>
        public static ILogger? For(HttpContext? http) =>
            http?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(Category);

        /// <summary>
        /// Called when a caller asked for a character that isn't theirs: logs it if the character exists
        /// under another account (someone probing other players' characters), stays quiet for a plain typo.
        /// </summary>
        public static async Task NoteForeignCharacterAsync(AppDbContext db, ILogger? logger, string? user,
            string characterName, string source)
        {
            if (logger is null) return;
            if (await db.Characters.AnyAsync(c => c.Name == characterName && c.UserId != user))
                Warn(logger, "realm.character.not_owner", user, "refused", $"{source};{characterName}");
        }

        /// <summary>Short, fixed reason for a rejected JWT - never the token or the exception text.</summary>
        public static string TokenRejectionReason(Exception exception) => exception switch
        {
            Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException => "expired",
            Microsoft.IdentityModel.Tokens.SecurityTokenNotYetValidException => "not-yet-valid",
            Microsoft.IdentityModel.Tokens.SecurityTokenInvalidSignatureException
                or Microsoft.IdentityModel.Tokens.SecurityTokenSignatureKeyNotFoundException => "invalid-signature",
            Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException => "wrong-issuer",
            Microsoft.IdentityModel.Tokens.SecurityTokenInvalidAudienceException => "wrong-audience",
            _ => "malformed"
        };

        /// <summary>One token per field: no control characters, line breaks or whitespace, at most 64
        /// characters, "-" for empty.</summary>
        public static string Safe(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            var sb = new StringBuilder(Math.Min(value.Length, 65));
            foreach (var c in value)
            {
                if (sb.Length == 64) { sb.Append('~'); break; }
                var bad = char.IsControl(c) || char.IsWhiteSpace(c) || c == LineSeparator || c == ParagraphSeparator;
                sb.Append(bad ? '_' : c);
            }
            return sb.ToString();
        }
    }
}
