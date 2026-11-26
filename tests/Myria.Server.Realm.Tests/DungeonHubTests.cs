using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Systems.Dungeons;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// v0.3 dungeons through the real hub and game data: per-party instances of the Hollow Crypt
/// (level 40; entrance room 64, reached from cave room 21; 65 is the next room in).
/// </summary>
[Collection("RealmDb")]
public class DungeonHubTests
{
    private const string Crypt = "hollow_crypt";

    private sealed record Realm2(HubHarness Realm, FakeCallerContext Alice, FakeCallerContext Bob, Character A, Character B) : IDisposable
    {
        public void Dispose() => Realm.Dispose();
    }

    /// <summary>Both level 40, standing in the cave room outside the crypt.</summary>
    private static async Task<Realm2> AtTheCryptAsync()
    {
        var realm = await HubHarness.CreateAsync();
        var alice = realm.Connect("alice");
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(alice).LoadCharacter("Alicechar"));
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        var a = realm.Session.Get(alice.ConnectionId)!;
        var b = realm.Session.Get(bob.ConnectionId)!;
        a.Level = b.Level = 40;
        Assert.True(await realm.PlaceAsync(alice, 21));
        Assert.True(await realm.PlaceAsync(bob, 21));
        return new Realm2(realm, alice, bob, a, b);
    }

    private static DungeonRun SoloRun(Realm2 r, string name) => r.Realm.Dungeons.Get(DungeonInstanceService.SoloOwner(name), Crypt)!;

    private static void Clear(DungeonRun run, int roomId)
    {
        var room = RoomService.AllRooms.First(x => x.Id == roomId);
        foreach (var m in run.Remaining(room)) run.RecordKill(roomId, m);
    }

    [Fact]
    public async Task GoingDeeper_NeedsTheRoomCleared_AndSkippingInIsRejected()
    {
        using var r = await AtTheCryptAsync();
        var hub = r.Realm.Hub(r.Alice);

        Assert.False(await hub.JoinRoom(68));                    // no skipping past the entrance
        Assert.True(await hub.JoinRoom(64));
        Assert.False(await hub.JoinRoom(65));                    // 64 still holds its pack

        Clear(SoloRun(r, "Alicechar"), 64);
        Assert.True(await hub.JoinRoom(65));
        Assert.True(await hub.JoinRoom(64));                     // falling back into a cleared room
    }

    [Fact]
    public async Task EachPlayer_HasTheirOwnCopy_UnlessTheyreInAParty()
    {
        using var r = await AtTheCryptAsync();
        Assert.True(await r.Realm.Hub(r.Alice).JoinRoom(64));
        Assert.True(await r.Realm.Hub(r.Bob).JoinRoom(64));
        Clear(SoloRun(r, "Alicechar"), 64);

        Assert.True((await r.Realm.Hub(r.Alice).GetDungeonState())!.Cleared);
        Assert.False((await r.Realm.Hub(r.Bob).GetDungeonState())!.Cleared); // Bob's crypt is untouched
        Assert.False(await r.Realm.Hub(r.Bob).JoinRoom(65));
    }

    [Fact]
    public async Task APartyShares_OneRun_AndKeepsItWhileAnyMemberIsInside()
    {
        using var r = await AtTheCryptAsync();
        var (partyId, _, _) = r.Realm.Party.GetOrCreate("Alicechar");
        r.Realm.Party.Join(partyId, "Bobchar");
        Assert.True(await r.Realm.Hub(r.Alice).JoinRoom(64));
        Assert.True(await r.Realm.Hub(r.Bob).JoinRoom(64));

        var run = r.Realm.Dungeons.Get(DungeonInstanceService.PartyOwner(partyId), Crypt)!;
        Clear(run, 64);
        Assert.True(await r.Realm.Hub(r.Bob).JoinRoom(65));      // the party's clear counts for Bob too

        Assert.True(await r.Realm.Hub(r.Alice).JoinRoom(21));    // Alice leaves; Bob is still inside
        Assert.Same(run, r.Realm.Dungeons.Get(DungeonInstanceService.PartyOwner(partyId), Crypt));

        Assert.True(await r.Realm.Hub(r.Bob).JoinRoom(64));
        Assert.True(await r.Realm.Hub(r.Bob).JoinRoom(21));      // last one out
        Assert.Null(r.Realm.Dungeons.Get(DungeonInstanceService.PartyOwner(partyId), Crypt));
    }

    [Fact]
    public async Task LeavingAlone_ResetsTheRun()
    {
        using var r = await AtTheCryptAsync();
        var hub = r.Realm.Hub(r.Alice);
        Assert.True(await hub.JoinRoom(64));
        Clear(SoloRun(r, "Alicechar"), 64);

        Assert.True(await hub.JoinRoom(21));
        Assert.Null(r.Realm.Dungeons.Get(DungeonInstanceService.SoloOwner("Alicechar"), Crypt));

        Assert.True(await hub.JoinRoom(64));
        Assert.False((await hub.GetDungeonState())!.Cleared);    // a fresh pack
    }

    [Fact]
    public async Task AFight_TakesItsPackFromTheRun_AndAClearedRoomHasNoFight()
    {
        using var r = await AtTheCryptAsync();
        var hub = r.Realm.Hub(r.Alice);
        Assert.True(await hub.JoinRoom(64));
        var pack = (await hub.GetDungeonState())!.Remaining;

        var start = await hub.StartGroupCombat(64, soloOnly: true);
        Assert.True(start.Success);
        Assert.Equal(Math.Min(pack.Count, DungeonRules.SoloPackPerFight), start.Monsters.Count);
        Assert.Equal(pack.Select(m => m.Name).Take(start.Monsters.Count), start.Monsters.Select(m => m.Name));

        r.Realm.GroupCombat.RemoveConn(r.Alice.ConnectionId);      // the fight ends without a kill
        Clear(SoloRun(r, "Alicechar"), 64);
        var again = await hub.StartGroupCombat(64, soloOnly: true);
        Assert.False(again.Success);
        Assert.Equal("room_cleared", again.Reason);
        Assert.Null(await r.Realm.Hub(r.Bob).GetDungeonState()); // Bob is outside
    }
}
