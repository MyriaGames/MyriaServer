using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services.Builder;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models.Dto.Public;

namespace Myria.Server.Realm.Controllers
{
    /// <summary>
    /// Public, unauthenticated character registry — anyone can browse basic character
    /// info and equipped gear. Unlike <see cref="CharactersController"/> (which is
    /// locked to the owning player's JWT and used by the game clients), this sector
    /// of the API has no [Authorize] and is meant for tools like MyriaWeb. Rate limited
    /// (see Program.cs's "public" policy) so it can't be used to bulk-scrape the registry.
    /// </summary>
    [ApiController]
    [Route("api/public/characters")]
    [EnableRateLimiting("public")]
    public class PublicCharactersController(AppDbContext db) : ControllerBase
    {
        // ── GET /api/public/characters — registry list ─────────────────────────────

        [HttpGet]
        [ProducesResponseType(typeof(List<PublicCharacterListItem>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAll()
        {
            var characters = await db.Characters
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new PublicCharacterListItem
                {
                    Id        = c.Id,
                    Name      = c.Name,
                    Level     = c.Level,
                    Class     = c.Class,
                    Race      = c.Race,
                    LastSaved = c.LastSaved
                })
                .ToListAsync();

            return Ok(characters);
        }

        // ── GET /api/public/characters/{id} — full public profile ──────────────────

        [HttpGet("{id:int}")]
        [ProducesResponseType(typeof(PublicCharacterDetail), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetById(int id)
        {
            var c = await db.Characters
                .AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == id);

            if (c is null) return NotFound();

            // Equipped-gear stats come from the JSON-loaded item catalog (same source
            // Myria.Lib.Core/the WPF client use), keyed by the character's WeaponItemId etc. —
            // not a SQL join, since item data isn't duplicated into the database.
            static PublicItemDto? ToDto(string? itemId)
            {
                if (itemId is null || !ItemFactory.TryCreateItem(itemId, out var item) || item is not EquipmentItem eq)
                    return null;

                return new PublicItemDto
                {
                    Id               = eq.Id,
                    Name             = eq.Name,
                    Description      = eq.Description,
                    Rarity           = eq.Rarity,
                    BaseBonusATK     = eq.BaseStats.ATK,
                    BaseBonusDEF     = eq.BaseStats.DEF,
                    BaseBonusMATK    = eq.BaseStats.MATK,
                    BaseBonusMDEF    = eq.BaseStats.MDEF,
                    BaseBonusHP      = eq.BaseStats.HP,
                    BaseBonusMP      = eq.BaseStats.MP,
                    BaseBonusSTR     = eq.BaseStats.STR,
                    BaseBonusDEX     = eq.BaseStats.DEX,
                    BaseBonusEND     = eq.BaseStats.END,
                    BaseBonusINT     = eq.BaseStats.INT,
                    BaseBonusSPR     = eq.BaseStats.SPR,
                    BaseBonusEvasion = eq.BaseStats.Evasion
                };
            }

            var resp = new PublicCharacterDetail
            {
                Id                    = c.Id,
                Name                  = c.Name,
                Level                 = c.Level,
                Class                 = c.Class,
                Race                  = c.Race,
                LastSaved             = c.LastSaved,
                StatBaseHealth        = c.StatBaseHealth,
                StatBaseMana          = c.StatBaseMana,
                StatStrength          = c.StatStrength,
                StatDexterity         = c.StatDexterity,
                StatEndurance         = c.StatEndurance,
                StatIntelligence      = c.StatIntelligence,
                StatSpirit            = c.StatSpirit,
                StatStrengthBonus     = c.StatStrengthBonus,
                StatDexterityBonus    = c.StatDexterityBonus,
                StatEnduranceBonus    = c.StatEnduranceBonus,
                StatIntelligenceBonus = c.StatIntelligenceBonus,
                StatSpiritBonus       = c.StatSpiritBonus,
                Weapon                = ToDto(c.WeaponItemId),
                Armor                 = ToDto(c.ArmorItemId),
                Accessory             = ToDto(c.AccessoryItemId)
            };

            return Ok(resp);
        }
    }
}
