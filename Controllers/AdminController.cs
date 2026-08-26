using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Data;

namespace Myria.Server.Realm.Controllers
{
    public class RenameUserRequest
    {
        public string OldUsername { get; set; } = string.Empty;
        public string NewUsername { get; set; } = string.Empty;
    }


    // Internal, service-to-service only — called exclusively by MyriaAuthServer when a
    // user deletes their account (GDPR Art. 17 right to erasure), never by game clients.
    // Not [Authorize]'d against player JWTs: it authenticates via a shared secret instead,
    // since the caller here is another backend service, not a logged-in player.
    [ApiController]
    [Route("api/admin")]
    public class AdminController(AppDbContext db, IConfiguration config) : ControllerBase
    {
        private const string SecretHeader = "X-Internal-Secret";

        private bool IsAuthorized()
        {
            var expected = config["Admin:InternalSecret"];
            if (string.IsNullOrWhiteSpace(expected))
                return false;

            var provided = Request.Headers[SecretHeader].ToString();
            return !string.IsNullOrEmpty(provided) &&
                   System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                       System.Text.Encoding.UTF8.GetBytes(provided),
                       System.Text.Encoding.UTF8.GetBytes(expected));
        }

        // ── DELETE /api/admin/characters/{username} — purge all characters for a user ──

        [HttpDelete("characters/{username}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> DeleteAllForUser(string username)
        {
            if (!IsAuthorized())
                return Forbid();

            var records = await db.Characters.Where(c => c.UserId == username).ToListAsync();
            if (records.Count > 0)
            {
                db.Characters.RemoveRange(records);
                await db.SaveChangesAsync();
            }

            return NoContent();
        }

        // ── PUT /api/admin/characters/rename — reassign all characters to a new username ──
        // Called by MyriaAuthServer when a user renames their account, since Character.UserId
        // is the plain username string, not a stable numeric id (see Character.cs).

        [HttpPut("characters/rename")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> RenameUser(RenameUserRequest req)
        {
            if (!IsAuthorized())
                return Forbid();

            var records = await db.Characters.Where(c => c.UserId == req.OldUsername).ToListAsync();
            foreach (var record in records)
                record.UserId = req.NewUsername;

            if (records.Count > 0)
                await db.SaveChangesAsync();

            return NoContent();
        }
    }
}
