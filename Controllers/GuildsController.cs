using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;
using Myria.Server.Realm.Services;

namespace Myria.Server.Realm.Controllers
{
    // ── Response DTOs ─────────────────────────────────────────────────────────────

    public record GuildMemberDto(
        int      CharacterId,
        string   CharacterName,
        string   Rank,
        DateTime JoinedAt);

    public record GuildRookieDto(
        int      CharacterId,
        string   CharacterName,
        string   RecruiterName,
        DateTime HiredAt);

    public record GuildSettingsDto(
        string ApplicationMode,
        string DepositWithdrawMinRank);

    public record GuildSummaryDto(
        int     Id,
        string  Name,
        string  Tag,
        string? Description,
        int     MemberCount,
        int     RookieCount);

    public record GuildDetailDto(
        int              Id,
        string           Name,
        string           Tag,
        string?          Description,
        GuildMemberDto[] Members,
        GuildRookieDto[] Rookies,
        GuildSettingsDto Settings,
        long             TreasuryBalance);

    public record GuildInviteDto(
        int      InviteId,
        int      GuildId,
        string   GuildName,
        string   GuildTag,
        bool     IsRookieInvite,
        DateTime ExpiresAt);

    public record GuildApplicationDto(
        int      ApplicationId,
        string   ApplicantName,
        string?  Message,
        DateTime CreatedAt);

    public record GuildListingDto(
        int      ListingId,
        int      GuildId,
        string   GuildName,
        string   GuildTag,
        long     AskingPrice,
        DateTime ListedAt);

    // ── Request bodies ────────────────────────────────────────────────────────────

    public record CreateGuildBody(
        string  CharacterName,
        string  Name,
        string  Tag,
        string? Description);

    public record DisbandBody(string CharacterName);

    public record SendInviteBody(
        string CharacterName,
        string InviteeCharacterName,
        bool   AsRookie);

    public record ApplyBody(
        string  CharacterName,
        int     GuildId,
        string? Message);

    public record CharacterTargetBody(
        string CharacterName,
        string TargetCharacterName);

    public record HireRookieBody(
        string CharacterName,
        string RookieCharacterName);

    public record UpdateSettingsBody(
        string CharacterName,
        string ApplicationMode,
        string DepositWithdrawMinRank);

    // ── Controller ────────────────────────────────────────────────────────────────

    // ── Property DTOs ─────────────────────────────────────────────────────────────

    public record GuildRoomDto(
        int      Id,
        int      RoomId,
        string   Name,
        string   Description,
        string   RoomType,
        string   MinRankRequired,
        bool     IsBuilt,
        DateTime? BuiltAt);

    public record GuildPropertyDto(
        int             Id,
        string          Type,
        int?            CityRoomId,
        int?            BaseAnchorRoomId,
        long?           PricePaid,
        DateTime        AcquiredAt,
        GuildRoomDto[]  Rooms,
        GuildListingDto? Listing);

    public record PurchaseHouseBody(string CharacterName, int CityRoomId);
    public record AddHouseRoomBody(string CharacterName, string RoomType);
    public record ListHouseBody(string CharacterName, long AskingPrice);
    public record EstablishBaseBody(string CharacterName, int AnchorRoomId);
    public record BuildBaseRoomBody(string CharacterName, string RoomType);
    public record UpdateRoomAccessBody(string CharacterName, int GuildRoomId, string MinRankRequired);

    [ApiController]
    [Route("api/guilds")]
    [Authorize]
    public class GuildsController(AppDbContext db, GuildService guilds, GuildPropertyService property) : ControllerBase
    {
        private Task<string?> GetUserAsync() => Task.FromResult(User.Identity!.Name);

        private async Task<Character?> GetMyCharacterAsync(string characterName, string me) =>
            await db.Characters.SingleOrDefaultAsync(c => c.UserId == me && c.Name == characterName);

        private async Task<Character?> GetCharacterByNameAsync(string name) =>
            await db.Characters.FirstOrDefaultAsync(c => c.Name == name);

        // ── Queries ───────────────────────────────────────────────────────────────

        // GET /api/guilds/me?characterName=...
        [HttpGet("me")]
        public async Task<IActionResult> GetMyGuild([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var membership = await guilds.GetMemberRecordAsync(myChar.Id);
            int? guildId   = membership?.GuildId
                ?? (await guilds.GetRookieRecordAsync(myChar.Id))?.GuildId;

            if (guildId is null) return NotFound(new { message = "You are not in a guild." });

            var guild = await db.Guilds
                .Include(g => g.Settings)
                .Include(g => g.Treasury)
                .Include(g => g.Members).ThenInclude(m => m.Character)
                .Include(g => g.Rookies).ThenInclude(r => r.Character)
                .Include(g => g.Rookies).ThenInclude(r => r.RecruiterCharacter)
                .FirstOrDefaultAsync(g => g.Id == guildId);

            if (guild is null) return NotFound();

            return Ok(ToDetailDto(guild));
        }

        // GET /api/guilds/{id}
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var guild = await db.Guilds
                .Include(g => g.Settings)
                .Include(g => g.Treasury)
                .Include(g => g.Members).ThenInclude(m => m.Character)
                .Include(g => g.Rookies).ThenInclude(r => r.Character)
                .Include(g => g.Rookies).ThenInclude(r => r.RecruiterCharacter)
                .FirstOrDefaultAsync(g => g.Id == id);

            if (guild is null) return NotFound();
            return Ok(ToDetailDto(guild));
        }

        // GET /api/guilds/search?q=...
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return BadRequest(new { message = "Search query is required." });

            var upper = q.ToUpperInvariant();
            var results = await db.Guilds
                .Where(g => g.Name.Contains(q) || g.Tag == upper)
                .Select(g => new GuildSummaryDto(
                    g.Id,
                    g.Name,
                    g.Tag,
                    g.Description,
                    g.Members.Count,
                    g.Rookies.Count))
                .ToListAsync();

            return Ok(results);
        }

        // GET /api/guilds/invites?characterName=...
        [HttpGet("invites")]
        public async Task<IActionResult> GetInvites([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var invites = await guilds.GetPendingInvitesAsync(myChar.Id);

            return Ok(invites.Select(i => new GuildInviteDto(
                i.Id,
                i.GuildId,
                i.Guild.Name,
                i.Guild.Tag,
                i.IsRookieInvite,
                i.ExpiresAt)).ToList());
        }

        // GET /api/guilds/applications?characterName=...
        [HttpGet("applications")]
        public async Task<IActionResult> GetApplications([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, apps) = await guilds.GetApplicationsAsync(myChar.Id);
            if (!success) return Forbid();

            return Ok(apps.Select(a => new GuildApplicationDto(
                a.Id,
                a.ApplicantCharacter.Name,
                a.Message,
                a.CreatedAt)).ToList());
        }

        // GET /api/guilds/listings
        [HttpGet("listings")]
        public async Task<IActionResult> GetListings()
        {
            var listings = await guilds.GetHouseListingsAsync();

            return Ok(listings.Select(l => new GuildListingDto(
                l.Id,
                l.SellerGuildId,
                l.SellerGuild.Name,
                l.SellerGuild.Tag,
                l.AskingPrice,
                l.ListedAt)).ToList());
        }

        // ── Guild lifecycle ───────────────────────────────────────────────────────

        // POST /api/guilds
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateGuildBody body)
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Guild name is required." });
            if (string.IsNullOrWhiteSpace(body.Tag) || body.Tag.Length > 5)
                return BadRequest(new { message = "Tag must be 1–5 characters." });

            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, guild) = await guilds.CreateGuildAsync(
                myChar.Id, body.Name, body.Tag, body.Description);

            if (!success) return Conflict(new { message = error });

            return Ok(new GuildSummaryDto(guild!.Id, guild.Name, guild.Tag, guild.Description, 1, 0));
        }

        // DELETE /api/guilds
        [HttpDelete]
        public async Task<IActionResult> Disband([FromBody] DisbandBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.DisbandGuildAsync(myChar.Id);
            if (!success) return BadRequest(new { message = error });

            return NoContent();
        }

        // ── Invites ───────────────────────────────────────────────────────────────

        // POST /api/guilds/invites
        [HttpPost("invites")]
        public async Task<IActionResult> SendInvite([FromBody] SendInviteBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _, _) = await guilds.InviteAsync(
                myChar.Id, body.InviteeCharacterName, body.AsRookie);

            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/invites/{id}/accept?characterName=...
        [HttpPost("invites/{id:int}/accept")]
        public async Task<IActionResult> AcceptInvite(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.AcceptInviteAsync(myChar.Id, id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/invites/{id}/decline?characterName=...
        [HttpPost("invites/{id:int}/decline")]
        public async Task<IActionResult> DeclineInvite(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _) = await guilds.DeclineInviteAsync(myChar.Id, id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // DELETE /api/guilds/invites/{id}?characterName=...
        [HttpDelete("invites/{id:int}")]
        public async Task<IActionResult> RevokeInvite(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.RevokeInviteAsync(myChar.Id, id);
            if (!success) return BadRequest(new { message = error });
            return NoContent();
        }

        // ── Applications ──────────────────────────────────────────────────────────

        // POST /api/guilds/apply
        [HttpPost("apply")]
        public async Task<IActionResult> Apply([FromBody] ApplyBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error) = await guilds.ApplyAsync(myChar.Id, body.GuildId, body.Message);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/applications/{id}/approve?characterName=...
        [HttpPost("applications/{id:int}/approve")]
        public async Task<IActionResult> ApproveApplication(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.ApproveApplicationAsync(myChar.Id, id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/applications/{id}/reject?characterName=...
        [HttpPost("applications/{id:int}/reject")]
        public async Task<IActionResult> RejectApplication(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.RejectApplicationAsync(myChar.Id, id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // ── Membership ────────────────────────────────────────────────────────────

        // POST /api/guilds/leave?characterName=...
        [HttpPost("leave")]
        public async Task<IActionResult> Leave([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _) = await guilds.LeaveGuildAsync(myChar.Id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/members/kick
        [HttpPost("members/kick")]
        public async Task<IActionResult> Kick([FromBody] CharacterTargetBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var target = await GetCharacterByNameAsync(body.TargetCharacterName);
            if (target is null) return NotFound(new { message = "Target character not found." });

            var (success, error, _, _) = await guilds.KickMemberAsync(myChar.Id, target.Id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/members/promote
        [HttpPost("members/promote")]
        public async Task<IActionResult> Promote([FromBody] CharacterTargetBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var target = await GetCharacterByNameAsync(body.TargetCharacterName);
            if (target is null) return NotFound(new { message = "Target character not found." });

            var (success, error, _, newRank, _, _) = await guilds.PromoteMemberAsync(myChar.Id, target.Id);
            if (!success) return BadRequest(new { message = error });
            return Ok(new { newRank = newRank.ToString() });
        }

        // POST /api/guilds/members/demote
        [HttpPost("members/demote")]
        public async Task<IActionResult> Demote([FromBody] CharacterTargetBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var target = await GetCharacterByNameAsync(body.TargetCharacterName);
            if (target is null) return NotFound(new { message = "Target character not found." });

            var (success, error, _, newRank, _, _) = await guilds.DemoteMemberAsync(myChar.Id, target.Id);
            if (!success) return BadRequest(new { message = error });
            return Ok(new { newRank = newRank.ToString() });
        }

        // POST /api/guilds/members/transfer-leadership
        [HttpPost("members/transfer-leadership")]
        public async Task<IActionResult> TransferLeadership([FromBody] CharacterTargetBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var target = await GetCharacterByNameAsync(body.TargetCharacterName);
            if (target is null) return NotFound(new { message = "Target character not found." });

            var (success, error, _) = await guilds.TransferLeadershipAsync(myChar.Id, target.Id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // ── Rookies ───────────────────────────────────────────────────────────────

        // POST /api/guilds/rookies/hire
        [HttpPost("rookies/hire")]
        public async Task<IActionResult> HireRookie([FromBody] HireRookieBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.HireRookieAsync(myChar.Id, body.RookieCharacterName);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/rookies/{rookieCharId}/promote?characterName=...
        [HttpPost("rookies/{rookieCharId:int}/promote")]
        public async Task<IActionResult> PromoteRookie(int rookieCharId, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.PromoteRookieToMemberAsync(myChar.Id, rookieCharId);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // DELETE /api/guilds/rookies/{rookieCharId}?characterName=...
        [HttpDelete("rookies/{rookieCharId:int}")]
        public async Task<IActionResult> FireRookie(int rookieCharId, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await guilds.FireRookieAsync(myChar.Id, rookieCharId);
            if (!success) return BadRequest(new { message = error });
            return NoContent();
        }

        // ── Settings ──────────────────────────────────────────────────────────────

        // PUT /api/guilds/settings
        [HttpPut("settings")]
        public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            if (!Enum.TryParse<GuildApplicationMode>(body.ApplicationMode, out var appMode))
                return BadRequest(new { message = $"Invalid ApplicationMode: '{body.ApplicationMode}'." });

            if (!Enum.TryParse<GuildRank>(body.DepositWithdrawMinRank, out var depositRank))
                return BadRequest(new { message = $"Invalid rank: '{body.DepositWithdrawMinRank}'." });

            var (success, error) = await guilds.UpdateSettingsAsync(myChar.Id, appMode, depositRank);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // ── Property ─────────────────────────────────────────────────────────────

        // GET /api/guilds/property?characterName=...
        [HttpGet("property")]
        public async Task<IActionResult> GetProperty([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var membership = await guilds.GetMemberRecordAsync(myChar.Id);
            if (membership is null) return NotFound(new { message = "You are not a full member of a guild." });

            var prop = await property.GetPropertyByGuildAsync(membership.GuildId);
            if (prop is null) return NotFound(new { message = "Your guild does not own a property." });

            return Ok(ToPropertyDto(prop));
        }

        // POST /api/guilds/property/house
        [HttpPost("property/house")]
        public async Task<IActionResult> PurchaseHouse([FromBody] PurchaseHouseBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, prop) = await property.PurchaseHouseAsync(myChar.Id, body.CityRoomId);
            if (!success) return BadRequest(new { message = error });

            return Ok(new { prop!.Id, prop.AcquiredAt });
        }

        // POST /api/guilds/property/house/rooms
        [HttpPost("property/house/rooms")]
        public async Task<IActionResult> AddHouseRoom([FromBody] AddHouseRoomBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            if (!Enum.TryParse<GuildRoomType>(body.RoomType, out var roomType))
                return BadRequest(new { message = $"Invalid room type '{body.RoomType}'." });

            var (success, error, room) = await property.AddHouseRoomAsync(myChar.Id, roomType);
            if (!success) return BadRequest(new { message = error });

            return Ok(ToRoomDto(room!));
        }

        // POST /api/guilds/property/house/list
        [HttpPost("property/house/list")]
        public async Task<IActionResult> ListHouseForSale([FromBody] ListHouseBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error) = await property.ListHouseForSaleAsync(myChar.Id, body.AskingPrice);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // DELETE /api/guilds/property/house/list?characterName=...
        [HttpDelete("property/house/list")]
        public async Task<IActionResult> DelistHouse([FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error) = await property.DelistHouseAsync(myChar.Id);
            if (!success) return BadRequest(new { message = error });
            return NoContent();
        }

        // POST /api/guilds/listings/{id}/buy?characterName=...
        [HttpPost("listings/{id:int}/buy")]
        public async Task<IActionResult> BuyListedHouse(int id, [FromQuery] string characterName)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(characterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, _, _) = await property.BuyListedHouseAsync(myChar.Id, id);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // POST /api/guilds/property/base
        [HttpPost("property/base")]
        public async Task<IActionResult> EstablishBase([FromBody] EstablishBaseBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            var (success, error, prop) = await property.EstablishBaseAsync(myChar.Id, body.AnchorRoomId);
            if (!success) return BadRequest(new { message = error });

            return Ok(new { prop!.Id, prop.AcquiredAt });
        }

        // PUT /api/guilds/property/rooms/access
        [HttpPut("property/rooms/access")]
        public async Task<IActionResult> UpdateRoomAccess([FromBody] UpdateRoomAccessBody body)
        {
            var me = await GetUserAsync();
            if (me is null) return Unauthorized();

            var myChar = await GetMyCharacterAsync(body.CharacterName, me);
            if (myChar is null) return BadRequest(new { message = "Character not found." });

            if (!Enum.TryParse<GuildRank>(body.MinRankRequired, out var rank))
                return BadRequest(new { message = $"Invalid rank '{body.MinRankRequired}'." });

            var (success, error) = await property.UpdateRoomAccessAsync(myChar.Id, body.GuildRoomId, rank);
            if (!success) return BadRequest(new { message = error });
            return Ok();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static GuildDetailDto ToDetailDto(Guild g) => new(
            g.Id,
            g.Name,
            g.Tag,
            g.Description,
            g.Members.Select(m => new GuildMemberDto(
                m.CharacterId,
                m.Character.Name,
                m.Rank.ToString(),
                m.JoinedAt)).ToArray(),
            g.Rookies.Select(r => new GuildRookieDto(
                r.CharacterId,
                r.Character.Name,
                r.RecruiterCharacter.Name,
                r.HiredAt)).ToArray(),
            new GuildSettingsDto(
                g.Settings.ApplicationMode.ToString(),
                g.Settings.DepositWithdrawMinRank.ToString()),
            g.Treasury.Balance);

        private static GuildRoomDto ToRoomDto(GuildRoom r) => new(
            r.Id,
            r.RoomId,
            r.Name,
            r.Description,
            r.RoomType.ToString(),
            r.MinRankRequired.ToString(),
            r.IsBuilt,
            r.BuiltAt);

        private static GuildPropertyDto ToPropertyDto(GuildProperty p) => new(
            p.Id,
            p.Type.ToString(),
            p.CityRoomId,
            p.BaseAnchorRoomId,
            p.PricePaid,
            p.AcquiredAt,
            p.Rooms.Select(ToRoomDto).ToArray(),
            p.Listing is null ? null : new GuildListingDto(
                p.Listing.Id,
                p.Listing.SellerGuildId,
                "", "",   // guild name/tag not loaded in this context
                p.Listing.AskingPrice,
                p.Listing.ListedAt));
    }
}
