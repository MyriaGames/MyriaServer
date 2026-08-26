using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Systems.Enums;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;
using Myria.Server.Realm.Services;

namespace Myria.Server.Realm.Controllers
{
    // ── Response DTOs ────────────────────────────────────────────────────────
    public record FriendDto(
        int     FriendshipId,
        string  CharacterName,
        bool    IsOnline,
        int     Level,
        string  ClassName,
        int?    CurrentRoomId,
        bool    InParty
    );
    public record FriendRequestDto(int FriendshipId, string FromCharacterName);

    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class FriendsController(
        AppDbContext          db,
        CharacterPresenceService presence,
        PartyService          party) : ControllerBase
    {
        private Task<string?> GetUserAsync() => Task.FromResult(User.Identity!.Name);

        private async Task<Character?> GetMyCharacterAsync(string characterName, string me) =>
            await db.Characters.SingleOrDefaultAsync(c => c.UserId == me && c.Name == characterName);

        // GET /api/friends?characterName=...
        [HttpGet]
        public async Task<IActionResult> GetFriends([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var rows = await db.Friendships
                .Include(f => f.RequesterCharacter)
                .Include(f => f.AddresseeCharacter)
                .Where(f => (f.RequesterCharacterId == myChar.Id || f.AddresseeCharacterId == myChar.Id)
                         && f.Status == FriendshipStatus.Accepted)
                .ToListAsync();

            var result = new List<FriendDto>();
            foreach (var f in rows)
            {
                var friendChar = f.RequesterCharacterId == myChar.Id
                    ? f.AddresseeCharacter
                    : f.RequesterCharacter;

                var isOnline  = presence.IsCharacterOnline(friendChar.Name);
                var roomId    = presence.GetRoomByCharacter(friendChar.Name) ?? (int?)friendChar.CurrentRoomId;
                var className = friendChar.Class;
                var inParty   = party.IsInParty(friendChar.Name);

                result.Add(new FriendDto(f.Id, friendChar.Name, isOnline, friendChar.Level, className, roomId, inParty));
            }

            return Ok(result);
        }

        // GET /api/friends/requests?characterName=...
        [HttpGet("requests")]
        public async Task<IActionResult> GetRequests([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var rows = await db.Friendships
                .Include(f => f.RequesterCharacter)
                .Where(f => f.AddresseeCharacterId == myChar.Id && f.Status == FriendshipStatus.Pending)
                .ToListAsync();

            return Ok(rows.Select(f => new FriendRequestDto(f.Id, f.RequesterCharacter.Name)).ToList());
        }

        // POST /api/friends/request  body: { "characterName": "...", "fromCharacterName": "..." }
        [HttpPost("request")]
        public async Task<IActionResult> SendRequest([FromBody] SendFriendRequestBody body)
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
                return NotFound(new { message = $"No character named '{body.CharacterName}' was found." });

            if (myChar.Id == targetChar.Id)
                return BadRequest(new { message = "You cannot add yourself." });

            var exists = await db.Friendships.AnyAsync(f =>
                (f.RequesterCharacterId == myChar.Id && f.AddresseeCharacterId == targetChar.Id) ||
                (f.RequesterCharacterId == targetChar.Id && f.AddresseeCharacterId == myChar.Id));
            if (exists)
                return Conflict(new { message = "A friend request already exists or you are already friends." });

            var blocked = await db.Blocks.AnyAsync(b =>
                b.BlockerCharacterId == targetChar.Id && b.BlockedCharacterId == myChar.Id);
            if (blocked)
                return NotFound(new { message = $"No character named '{body.CharacterName}' was found." });

            db.Friendships.Add(new Friendship
            {
                RequesterCharacterId = myChar.Id,
                AddresseeCharacterId = targetChar.Id,
                Status               = FriendshipStatus.Pending
            });
            await db.SaveChangesAsync();
            return Ok();
        }

        // POST /api/friends/{id}/accept?characterName=...
        [HttpPost("{id:int}/accept")]
        public async Task<IActionResult> Accept(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest();

            var row = await db.Friendships.FindAsync(id);
            if (row is null || row.AddresseeCharacterId != myChar.Id) return NotFound();
            if (row.Status != FriendshipStatus.Pending) return BadRequest();

            row.Status = FriendshipStatus.Accepted;
            await db.SaveChangesAsync();
            return Ok();
        }

        // DELETE /api/friends/{id}?characterName=...
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Remove(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest();

            var row = await db.Friendships.FindAsync(id);
            if (row is null) return NotFound();
            if (row.RequesterCharacterId != myChar.Id && row.AddresseeCharacterId != myChar.Id)
                return Forbid();

            db.Friendships.Remove(row);
            await db.SaveChangesAsync();
            return NoContent();
        }
    }

    public record SendFriendRequestBody(string CharacterName, string FromCharacterName);
}
