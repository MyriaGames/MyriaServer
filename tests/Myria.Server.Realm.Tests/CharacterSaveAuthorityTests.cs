using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Controllers;
using Myria.Server.Realm.Models.Dto;
using Myria.Server.Realm.Repositories;
using Myria.Server.Realm.Services;
using Xunit;
using LibCharacter = Myria.Lib.Core.Entities.Characters.Character;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// Security review 2026-11-26, A06 F1: POST /api/characters may create a character from name, race
/// and class, and make the two one-time choices - nothing else the client sends may stick.
/// </summary>
[Collection("RealmDb")]
public class CharacterSaveAuthorityTests : IDisposable
{
    private readonly RealmDbHost _db = new();
    private readonly CharacterSessionService _session = new();

    public void Dispose() => _db.Dispose();

    private CharactersController Controller(string user = "alice")
    {
        var db = _db.NewContext();
        var c = new CharactersController(db, new BanService(db), _session, new SqlCharacterRepository(db));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test"))
            }
        };
        return c;
    }

    private static SaveCharacterRequest Cheating(string name, string race = "Myralu", string cls = "Fighter") => new()
    {
        Name = name, Race = race, Class = cls, RaceSelected = true,
        Level = 99, Experience = 9_999_999, MoneyCapacity = long.MaxValue, MoneyBronze = 9_000_000_000,
        StatStrength = 999,
        InventoryItems = [new CharSaveInventoryItem { ItemId = "iron_ore", StackSize = 99, SlotIndex = 0 }],
        CompletedQuestIds = ["co1_first_day"]
    };

    private Myria.Server.Realm.Models.Character Row(string name) =>
        _db.NewContext().Characters.Include(c => c.InventoryItems).Include(c => c.CompletedQuests).Single(c => c.Name == name);

    [Fact]
    public async Task NewCharacter_GetsTheServersStartingState_NotTheRequestsValues()
    {
        Assert.IsType<OkResult>(await Controller().Save(Cheating("Alicia")));

        var expected = CharacterRules.CreateNew("Alicia", "Myralu", "Fighter");
        var row = Row("Alicia");
        Assert.Equal(1, row.Level);
        Assert.Equal(expected.Stats.Strength, row.StatStrength);
        Assert.True(row.MoneyBronze < 9_000_000_000);
        Assert.NotEqual(long.MaxValue, row.MoneyCapacity);
        Assert.Empty(row.CompletedQuests);
        Assert.DoesNotContain(row.InventoryItems, i => i.ItemId == "iron_ore" && i.StackSize == 99);
        Assert.Equal("Fighter", row.Class);
    }

    [Fact]
    public async Task ExistingCharacter_ProgressionFieldsInTheRequest_AreIgnored()
    {
        await Controller().Save(new SaveCharacterRequest { Name = "Alicia", Race = "Myralu", Class = "Fighter", RaceSelected = true });
        var before = Row("Alicia");

        Assert.IsType<OkResult>(await Controller().Save(Cheating("Alicia")));

        var after = Row("Alicia");
        Assert.Equal(before.Level, after.Level);
        Assert.Equal(before.MoneyBronze, after.MoneyBronze);
        Assert.Equal(before.StatStrength, after.StatStrength);
        Assert.Empty(after.CompletedQuests);
    }

    [Theory]
    [InlineData("NoSuchRace", "Fighter")]
    [InlineData("Myralu", "RunicMage")]   // removed class can't be chosen any more
    [InlineData("Myralu", "NoSuchClass")]
    public async Task NewCharacter_WithInvalidRaceOrClass_IsRejected(string race, string cls)
    {
        Assert.IsType<BadRequestObjectResult>(await Controller().Save(
            new SaveCharacterRequest { Name = "Alicia", Race = race, Class = cls, RaceSelected = true }));
    }

    [Fact]
    public async Task RaceChoice_OnlyWhileNoRaceIsSelected()
    {
        var old = new LibCharacter("Oldie", new Myria.Lib.Core.Entities.Stats()) { Race = "Myralu", RaceSelected = false };
        await new SqlCharacterRepository(_db.NewContext()).SaveAsync("alice", old);
        var otherRace = Myria.Lib.Core.Entities.Characters.RaceProfile.All.Keys.First(r => r != "Myralu");

        await Controller().Save(new SaveCharacterRequest { Name = "Oldie", Race = otherRace, Class = old.Class, RaceSelected = true });
        Assert.Equal(otherRace, Row("Oldie").Race);
        Assert.True(Row("Oldie").RaceSelected);

        await Controller().Save(new SaveCharacterRequest { Name = "Oldie", Race = "Myralu", Class = old.Class, RaceSelected = true });
        Assert.Equal(otherRace, Row("Oldie").Race);   // a second choice is ignored
    }

    [Fact]
    public async Task ClassChange_OnlyAwayFromRunicMage()
    {
        var mage = new LibCharacter("Runey", new Myria.Lib.Core.Entities.Stats()) { Race = "Myralu", RaceSelected = true, Class = "RunicMage" };
        await new SqlCharacterRepository(_db.NewContext()).SaveAsync("alice", mage);
        var target = Myria.Lib.Core.Entities.Characters.ClassProfile.All.Keys
            .First(c => c != "RunicMage" && Myria.Lib.Core.Services.Manager.ClassManager.IsClassAllowed("Myralu", c));

        await Controller().Save(new SaveCharacterRequest { Name = "Runey", Race = "Myralu", Class = target, RaceSelected = true });
        Assert.Equal(target, Row("Runey").Class);

        var another = Myria.Lib.Core.Entities.Characters.ClassProfile.All.Keys
            .First(c => c != "RunicMage" && c != target && Myria.Lib.Core.Services.Manager.ClassManager.IsClassAllowed("Myralu", c));
        await Controller().Save(new SaveCharacterRequest { Name = "Runey", Race = "Myralu", Class = another, RaceSelected = true });
        Assert.Equal(target, Row("Runey").Class);    // normal class changes go through the hub, with cooldown
    }

    [Fact]
    public async Task CharacterInPlay_IsNotWrittenOver()
    {
        await Controller().Save(new SaveCharacterRequest { Name = "Alicia", Race = "Myralu", Class = "Fighter", RaceSelected = true });
        _session.TryAdd("conn-1", CharacterRules.CreateNew("Alicia", "Myralu", "Fighter"));

        Assert.IsType<ConflictObjectResult>(await Controller().Save(Cheating("Alicia")));
    }
}
