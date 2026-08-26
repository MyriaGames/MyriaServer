using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;

namespace Myria.Server.Realm.Controllers
{
    public record BlockDto(int BlockId, string Username);
    public record BlockRequest(string CharacterName, string FromCharacterName);

    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class BlocksController(AppDbContext db) : ControllerBase
    {
        private Task<string?> GetUserAsync() => Task.FromResult(User.Identity!.Name);

        private async Task<Character?> GetMyCharacterAsync(string characterName, string me) =>
            await db.Characters.SingleOrDefaultAsync(c => c.UserId == me && c.Name == characterName);

        // GET /api/blocks?characterName=...
        [HttpGet]
        public async Task<IActionResult> GetBlocks([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var rows = await db.Blocks
                .Include(b => b.BlockedCharacter)
                .Where(b => b.BlockerCharacterId == myChar.Id)
                .ToListAsync();

            return Ok(rows.Select(b => new BlockDto(b.Id, b.BlockedCharacter.Name)).ToList());
        }

        // POST /api/blocks  body: { "characterName": "...", "fromCharacterName": "..." }
        [HttpPost]
        public async Task<IActionResult> BlockCharacter([FromBody] BlockRequest body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            if (string.IsNullOrWhiteSpace(body.FromCharacterName))
                return BadRequest(new { message = "Source character name is required." });
            if (string.IsNullOrWhiteSpace(body.CharacterName))
                return BadRequest(new { message = "Target character name is required." });

            var myChar = await GetMyCharacterAsync(body.FromCharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Your character was not found." });

            var targetChar = await db.Characters
                .FirstOrDefaultAsync(c => c.Name == body.CharacterName);

            if (targetChar is null)
                return NotFound(new { message = $"No character named '{body.CharacterName}' found." });

            if (myChar.Id == targetChar.Id)
                return BadRequest(new { message = "You cannot block yourself." });

            var exists = await db.Blocks.AnyAsync(b =>
                b.BlockerCharacterId == myChar.Id && b.BlockedCharacterId == targetChar.Id);
            if (exists)
                return Conflict(new { message = "Character is already blocked." });

            db.Blocks.Add(new Block { BlockerCharacterId = myChar.Id, BlockedCharacterId = targetChar.Id });
            await db.SaveChangesAsync();
            return Ok();
        }

        // DELETE /api/blocks/{id}?characterName=...
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Unblock(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest();

            var row = await db.Blocks.FindAsync(id);
            if (row is null || row.BlockerCharacterId != myChar.Id) return NotFound();

            db.Blocks.Remove(row);
            await db.SaveChangesAsync();
            return NoContent();
        }
    }
}
