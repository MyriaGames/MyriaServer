using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>
/// v0.3 PvP through the real hub methods and real game data: room 73 is the Hydea arena (the only
/// IsPvpRoom room, access level 10), room 1 is an ordinary room. Covers where PvP is allowed, who
/// can be challenged, and that every way a fight ends settles rating/currency, restores pre-fight
/// HP/MP and persists the result.
/// </summary>
[Collection("RealmDb")]
public class PvpHubTests
{
    private const int Arena = 73;

    private sealed record Duel(HubHarness Realm, FakeCallerContext Alice, FakeCallerContext Bob, Character A, Character B) : IDisposable
    {
        public void Dispose() => Realm.Dispose();
    }

    private static async Task<Duel> BothInRoomAsync(int roomId)
    {
        var realm = await HubHarness.CreateAsync();
        var alice = realm.Connect("alice");
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(alice).LoadCharacter("Alicechar"));
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        var a = realm.Session.Get(alice.ConnectionId)!;
        var b = realm.Session.Get(bob.ConnectionId)!;
        foreach (var c in new[] { a, b })
        {
            c.Level = 10;                    // Hydea's access level
            c.Stats.BaseHealth = 1_000_000;  // survive any number of turns
            c.CurrentHealth = 5_000;
        }
        Assert.True(await realm.PlaceAsync(alice, roomId));
        Assert.True(await realm.PlaceAsync(bob, roomId));
        return new Duel(realm, alice, bob, a, b);
    }

    private static async Task<int> SavedRatingAsync(HubHarness realm, string name)
    {
        await using var db = realm.NewDb();
        return (await db.Characters.SingleAsync(c => c.Name == name)).PvpRating;
    }

    // ── Where and whom ────────────────────────────────────────────────────

    [Fact]
    public async Task OutsideAnArena_NobodyIsListed_AndAChallengeIsRefused()
    {
        using var d = await BothInRoomAsync(1);

        Assert.Empty(await d.Realm.Hub(d.Alice).GetPvpTargets());
        var result = await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", partyFight: false);

        Assert.False(result.Success);
        Assert.Equal("not_pvp_room", result.Reason);
    }

    [Fact]
    public async Task InTheArena_OthersAreListedAndCanBeChallenged()
    {
        using var d = await BothInRoomAsync(Arena);

        var targets = await d.Realm.Hub(d.Alice).GetPvpTargets();
        Assert.Equal("Bobchar", Assert.Single(targets).Name);

        var result = await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", partyFight: false);
        Assert.True(result.Success, result.Reason);
        Assert.True(d.Realm.PvpCombat.IsInFight(d.Alice.ConnectionId));
        Assert.True(d.Realm.PvpCombat.IsInFight(d.Bob.ConnectionId));
    }

    [Fact]
    public async Task PartyMembers_AreNeitherListedNorChallengeable()
    {
        using var d = await BothInRoomAsync(Arena);
        var (partyId, _, _) = d.Realm.Party.GetOrCreate("Alicechar");
        d.Realm.Party.Join(partyId, "Bobchar");

        Assert.Empty(await d.Realm.Hub(d.Alice).GetPvpTargets());
        var result = await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", partyFight: false);
        Assert.Equal("same_party", result.Reason);
    }

    [Fact]
    public async Task DuringAPvpFight_NoMonsterFightCanStart_AndTheTargetIsBusy()
    {
        using var d = await BothInRoomAsync(Arena);
        Assert.True((await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", false)).Success);

        var pve = await d.Realm.Hub(d.Bob).StartGroupCombat(Arena, soloOnly: true);
        Assert.Equal("already_in_combat", pve.Reason);
        Assert.Empty(await d.Realm.Hub(d.Bob).GetPvpTargets()); // Alice is busy now
    }

    // ── How fights end ────────────────────────────────────────────────────

    [Fact]
    public async Task Fleeing_IsALossForTheFleer_AWinForTheOther_AndEveryoneIsRestoredAndSaved()
    {
        using var d = await BothInRoomAsync(Arena);
        int aHp = d.A.CurrentHealth, bHp = d.B.CurrentHealth;
        Assert.True((await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", false)).Success);
        d.A.CurrentHealth = 7; d.B.CurrentHealth = 9; // mid-fight damage

        await d.Realm.Hub(d.Bob).PvpFlee();

        Assert.False(d.Realm.PvpCombat.IsInFight(d.Alice.ConnectionId));
        Assert.True(d.A.PvpRating > PvpRatingService.StartingRating);
        Assert.True(d.B.PvpRating < PvpRatingService.StartingRating);
        Assert.Equal((1, 0), (d.A.PvpWins, d.A.PvpLosses));
        Assert.Equal((0, 1), (d.B.PvpWins, d.B.PvpLosses));
        Assert.Equal(PvpRatingService.WinCurrency, d.A.PvpCurrency);
        Assert.Equal(aHp, d.A.CurrentHealth);
        Assert.Equal(bHp, d.B.CurrentHealth);
        Assert.Equal(d.A.PvpRating, await SavedRatingAsync(d.Realm, "Alicechar"));
        Assert.Equal(d.B.PvpRating, await SavedRatingAsync(d.Realm, "Bobchar"));
    }

    [Fact]
    public async Task FightingItOut_SettlesTheResult_AndRevivesTheLoserAtTheirPreFightHp()
    {
        using var d = await BothInRoomAsync(Arena);
        int aHp = d.A.CurrentHealth, bHp = d.B.CurrentHealth;
        var enc = (await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", false)).Success
            ? d.Realm.PvpCombat.GetEncounterForConnection(d.Alice.ConnectionId)!
            : throw new Xunit.Sdk.XunitException("fight didn't start");
        d.A.CurrentHealth = 1; d.B.CurrentHealth = 1; // the first hit that lands decides it

        for (int i = 0; i < 200 && d.Realm.PvpCombat.IsInFight(d.Alice.ConnectionId); i++)
        {
            var actor = enc.CurrentTurnCharacterName == "Alicechar" ? d.Alice : d.Bob;
            await d.Realm.Hub(actor).PvpAttack(0);
        }

        Assert.True(enc.IsFinished);
        var (winner, loser) = enc.WinningSide == 1 ? (d.A, d.B) : (d.B, d.A);
        Assert.True(winner.PvpRating > PvpRatingService.StartingRating);
        Assert.True(loser.PvpRating < PvpRatingService.StartingRating);
        Assert.True(loser.IsAlive);                         // never sent to a healer / left dead
        Assert.Equal(aHp, d.A.CurrentHealth);
        Assert.Equal(bHp, d.B.CurrentHealth);
        Assert.Equal(loser.PvpRating, await SavedRatingAsync(d.Realm, loser.Name));
    }

    [Fact]
    public async Task Disconnecting_ForfeitsTheFight_AndSavesTheLoss()
    {
        using var d = await BothInRoomAsync(Arena);
        Assert.True((await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", false)).Success);

        await d.Realm.Hub(d.Bob).OnDisconnectedAsync(null);

        Assert.False(d.Realm.PvpCombat.IsInFight(d.Alice.ConnectionId));
        Assert.True(d.A.PvpRating > PvpRatingService.StartingRating);
        Assert.True(await SavedRatingAsync(d.Realm, "Bobchar") < PvpRatingService.StartingRating);
    }

    [Fact]
    public async Task OnlyTheCurrentParticipant_CanAct()
    {
        using var d = await BothInRoomAsync(Arena);
        Assert.True((await d.Realm.Hub(d.Alice).StartPvpFight("Bobchar", false)).Success);
        var enc = d.Realm.PvpCombat.GetEncounterForConnection(d.Alice.ConnectionId)!;
        var waiting = enc.CurrentTurnCharacterName == "Alicechar" ? d.Bob : d.Alice;

        Assert.Null(await d.Realm.Hub(waiting).PvpAttack(0));
    }
}
