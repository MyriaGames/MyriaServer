using Myria.Server.ClassicRealm.Services;
using Xunit;

/// <summary>
/// The party loot master (v0.3): gets all loot from the party's dungeon fights and hands it out by
/// trade. Only the leader picks one; without a choice - or once that player has left - the leader
/// is the loot master.
/// </summary>
public class PartyLootMasterTests
{
    private static (PartyService party, string id) Trio()
    {
        var party = new PartyService();
        var (id, _, _) = party.GetOrCreate("alice");
        party.Join(id, "bob");
        party.Join(id, "carol");
        return (party, id);
    }

    [Fact]
    public void WithoutAChoice_TheLeaderIsTheLootMaster()
    {
        var (party, id) = Trio();
        Assert.Equal("alice", party.GetLootMaster(id));
    }

    [Fact]
    public void TheLeader_CanPickAMember_AndTakeItBack()
    {
        var (party, id) = Trio();

        var (ok, partyId, lootMaster) = party.SetLootMaster("alice", "bob");
        Assert.True(ok);
        Assert.Equal(id, partyId);
        Assert.Equal("bob", lootMaster);
        Assert.Equal("bob", party.GetLootMaster(id));

        Assert.True(party.SetLootMaster("alice", null).success);
        Assert.Equal("alice", party.GetLootMaster(id));
    }

    [Fact]
    public void OnlyTheLeader_PicksTheLootMaster_AndOnlyAMember()
    {
        var (party, id) = Trio();

        Assert.False(party.SetLootMaster("bob", "bob").success);          // not the leader
        Assert.False(party.SetLootMaster("alice", "mallory").success);    // not a member
        Assert.Equal("alice", party.GetLootMaster(id));
    }

    [Fact]
    public void WhenTheLootMasterLeaves_TheLeaderTakesOver_AndANewLeaderToo()
    {
        var (party, id) = Trio();
        party.SetLootMaster("alice", "bob");

        party.Leave("bob");
        Assert.Equal("alice", party.GetLootMaster(id));

        party.Leave("alice");                                             // carol leads now
        Assert.Equal(party.GetLeader(id), party.GetLootMaster(id));
    }
}
