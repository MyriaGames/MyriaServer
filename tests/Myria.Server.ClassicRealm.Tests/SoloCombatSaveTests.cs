using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>
/// A finished solo fight is saved right away (MONO-004): XP and loot from a win used to live only in
/// the session until some unrelated save, so a realm crash or restart lost them.
/// </summary>
[Collection("RealmDb")]
public class SoloCombatSaveTests
{
    [Fact]
    public async Task WinningASoloFight_SavesTheCharacter()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        Assert.True(await realm.PlaceAsync(bob, 2));
        var player = realm.Session.Get(bob.ConnectionId)!;
        player.Stats.Strength = 500;                      // one or two hits end it
        player.Stats.BaseHealth = 100_000;                // and a lucky monster cant win it
        player.CurrentHealth = player.MaxHealth;

        var start = await realm.Hub(bob).StartCombat(2);
        Assert.True(start.Success, start.Reason);
        var turn = await realm.Hub(bob).CharacterAttack();
        for (int i = 0; i < 30 && !turn.Finished; i++) turn = await realm.Hub(bob).CharacterAttack();
        Assert.True(turn.Finished && turn.CharacterWon);

        await using var db = realm.NewDb();
        var saved = await db.Characters.SingleAsync(c => c.Name == "Bobchar");
        Assert.True(saved.Experience > 0 || saved.Level > 1, "the win's XP wasn't saved");
        Assert.Equal(player.Experience, saved.Experience);
    }
}
