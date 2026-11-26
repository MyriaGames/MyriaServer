using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Myria.Server.ClassicRealm.Data;
using Myria.Server.ClassicRealm.Models;

namespace Myria.Server.ClassicRealm.Services
{
    // Per-account "tokens issued before X are no longer valid" (security review 2026-11-25, A07 F1).
    // Tokens are validated statelessly (no call back to the auth server), so without this a password
    // change or reset left every token issued with the old password working for its whole lifetime.
    //
    // Checked on every authenticated request and hub connect (JwtBearerEvents.OnTokenValidated in
    // Program.cs), so it answers from memory; the database copy (SessionRevocations) only exists so a
    // realm restart doesn't silently re-enable revoked tokens. Loaded once at startup.
    public class SessionRevocationService(IServiceScopeFactory scopes, IConfiguration config)
    {
        private readonly ConcurrentDictionary<string, DateTime> _validAfter = new(StringComparer.OrdinalIgnoreCase);

        // A revocation only matters while a token issued before it can still be unexpired.
        private TimeSpan KeepFor =>
            TimeSpan.FromHours(Math.Max(1, config.GetValue("Jwt:ExpirationHours", 24))) + TimeSpan.FromHours(1);

        public async Task LoadAsync()
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PurgeExpiredAsync(db);
            foreach (var r in await db.SessionRevocations.AsNoTracking().ToListAsync())
                _validAfter[r.Username] = DateTime.SpecifyKind(r.TokensValidAfter, DateTimeKind.Utc);
        }

        /// <summary>
        /// Rejects tokens of <paramref name="username"/> issued before <paramref name="validAfterUtc"/>.
        /// The time comes from the auth server - the same clock that writes the tokens' "iat" - so a
        /// fresh token issued right after the change is never rejected because of clock drift between
        /// machines. A later revocation never moves the cut-off backwards.
        /// </summary>
        public async Task RevokeAsync(string username, DateTime validAfterUtc)
        {
            var cutoff = TruncateToSeconds(validAfterUtc);

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PurgeExpiredAsync(db);

            var row = await db.SessionRevocations.FindAsync(username);
            if (row is null)
                db.SessionRevocations.Add(row = new SessionRevocation { Username = username, TokensValidAfter = cutoff });
            else if (cutoff > row.TokensValidAfter)
                row.TokensValidAfter = cutoff;
            await db.SaveChangesAsync();

            _validAfter.AddOrUpdate(username, row.TokensValidAfter,
                (_, existing) => existing > row.TokensValidAfter ? existing : row.TokensValidAfter);
        }

        /// <summary>
        /// False if the account's sessions were revoked after this token was issued. A token without an
        /// issued-at time (<paramref name="issuedAtUtc"/> null - everything issued before this check
        /// existed) is rejected once the account has any revocation, and accepted otherwise.
        /// </summary>
        public bool IsTokenAccepted(string? username, DateTime? issuedAtUtc)
        {
            if (string.IsNullOrEmpty(username) || !_validAfter.TryGetValue(username, out var validAfter))
                return true;
            return issuedAtUtc is not null && TruncateToSeconds(issuedAtUtc.Value) >= validAfter;
        }

        private async Task PurgeExpiredAsync(AppDbContext db)
        {
            var before = DateTime.UtcNow - KeepFor;
            var expired = await db.SessionRevocations.Where(r => r.TokensValidAfter < before).ToListAsync();
            if (expired.Count == 0) return;
            db.SessionRevocations.RemoveRange(expired);
            await db.SaveChangesAsync();
            foreach (var r in expired)
                _validAfter.TryRemove(r.Username, out _);
        }

        private static DateTime TruncateToSeconds(DateTime t)
        {
            var utc = t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
    }
}
