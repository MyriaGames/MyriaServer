using System.ComponentModel.DataAnnotations;

namespace Myria.Server.ClassicRealm.Models
{
    // "Tokens of this account issued before TokensValidAfter are no longer accepted here." Set by the
    // auth server after a password change or reset (and when an operator forces a password change),
    // so a session someone else opened with the old password ends at once instead of living on for
    // the token's remaining lifetime (security review 2026-11-25, A07 F1). Rows older than the token
    // lifetime are pointless (every token they could reject has expired anyway) and get purged.
    public class SessionRevocation
    {
        // Case-insensitive (NOCASE, see AppDbContext): usernames are unique case-insensitively on the
        // auth server, so "Benn" and "benn" are the same account.
        [Key, MaxLength(50)]
        public string Username { get; set; } = string.Empty;

        // Whole seconds (UTC), the same resolution as a JWT's "iat" claim.
        public DateTime TokensValidAfter { get; set; }
    }
}
