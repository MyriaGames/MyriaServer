using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// JoinRoom only walks through an exit of the character's current room (MONO-003): a modified
/// client could jump to any room it met the requirements for. Joining the room the character is
/// already in stays free - that's the placement after login, a character switch or a respawn.
/// Room 1 (Royas) has exits north to 2 and south to 22; Hydea (room 9) is far away.
/// </summary>
[Collection("RealmDb")]
public class RoomMovementTests
{
    private static async Task<(HubHarness Realm, FakeCallerContext Bob, Myria.Lib.Core.Entities.Characters.Character Player)> BobInRoyasAsync()
    {
        var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        var player = realm.Session.Get(bob.ConnectionId)!;
        player.Level = 30;                                    // no level gate gets in the way
        Assert.True(await realm.PlaceAsync(bob, 1));
        return (realm, bob, player);
    }

    [Fact]
    public async Task WalkingThroughAnExit_Works()
    {
        var (realm, bob, player) = await BobInRoyasAsync();
        using var _ = realm;

        Assert.True(await realm.Hub(bob).JoinRoom(2));
        Assert.True(await realm.Hub(bob).JoinRoom(1));       // and back
        Assert.Equal(1, player.CurrentRoomId);
    }

    [Fact]
    public async Task JumpingToARoomWithoutAnExit_IsRefused()
    {
        var (realm, bob, player) = await BobInRoyasAsync();
        using var _ = realm;

        Assert.False(await realm.Hub(bob).JoinRoom(9));
        Assert.Equal(1, player.CurrentRoomId);
        Assert.Equal(1, realm.Presence.GetRoom(bob.ConnectionId));
    }

    [Fact]
    public async Task JoiningTheRoomTheCharacterIsIn_IsAlwaysAllowed()
    {
        var (realm, bob, player) = await BobInRoyasAsync();
        using var _ = realm;

        Assert.True(await realm.Hub(bob).JoinRoom(1));       // re-join after a reconnect

        // As after a respawn or a character switch on this connection: the character is somewhere
        // the connection's last room has no exit to, and the client joins that room.
        player.CurrentRoomId = 9;
        player.CurrentRoom = Myria.Lib.Core.Services.RoomService.GetRoomById(9);
        Assert.True(await realm.Hub(bob).JoinRoom(9));
        Assert.Equal(9, realm.Presence.GetRoom(bob.ConnectionId));
    }
}
