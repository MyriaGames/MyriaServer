using Myria.Server.Realm.Services;
using Xunit;

/// <summary>
/// PartyService is pure in-memory state with no dependencies, but it owns rules that are easy to
/// break and hard to notice in play: who ends up leading after someone leaves, when a party
/// collapses, and — most importantly — that a player is never registered in two parties at once.
/// A leak in the member→party index strands someone in a party that no longer exists, and the
/// only symptom in-game is "you are already in a party" forever.
/// </summary>
public class PartyServiceTests
{
    [Fact]
    public void GetOrCreate_StartsAPartyLedByTheCaller()
    {
        var party = new PartyService();

        var (partyId, members, leader) = party.GetOrCreate("alice");

        Assert.NotEmpty(partyId);
        Assert.Equal(["alice"], members);
        Assert.Equal("alice", leader);
        Assert.True(party.IsInParty("alice"));
    }

    /// <summary>Calling it twice must return the same party, not silently orphan the first one.</summary>
    [Fact]
    public void GetOrCreate_IsIdempotentForSomeoneAlreadyInAParty()
    {
        var party = new PartyService();
        var (firstId, _, _) = party.GetOrCreate("alice");

        var (secondId, members, leader) = party.GetOrCreate("alice");

        Assert.Equal(firstId, secondId);
        Assert.Equal(["alice"], members);
        Assert.Equal("alice", leader);
    }

    [Fact]
    public void Join_AddsAMemberAndKeepsTheOriginalLeader()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");

        var (success, members, leader) = party.Join(partyId, "bob");

        Assert.True(success);
        Assert.Equal(["alice", "bob"], members);
        Assert.Equal("alice", leader);
    }

    [Fact]
    public void Join_RejectsSomeoneAlreadyInAnotherParty()
    {
        var party = new PartyService();
        var (aliceParty, _, _) = party.GetOrCreate("alice");
        var (bobParty, _, _) = party.GetOrCreate("bob");

        var (success, _, _) = party.Join(aliceParty, "bob");

        Assert.False(success);
        Assert.Equal(bobParty, party.GetPartyId("bob"));
    }

    [Fact]
    public void Join_RejectsAnUnknownPartyId()
    {
        var party = new PartyService();

        var (success, _, _) = party.Join("does-not-exist", "alice");

        Assert.False(success);
        Assert.False(party.IsInParty("alice"));
    }

    [Fact]
    public void Join_RefusesToExceedMaxSize()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("leader");
        for (int i = 1; i < PartyService.MaxSize; i++)
            Assert.True(party.Join(partyId, $"member{i}").success);

        var (success, _, _) = party.Join(partyId, "one-too-many");

        Assert.False(success);
        Assert.Equal(PartyService.MaxSize, party.GetMembers(partyId).Count);
        Assert.False(party.IsInParty("one-too-many"));
    }

    /// <summary>
    /// A two-person party has nowhere to go when one leaves, so it disbands — and the member who
    /// stayed behind must be released from the index too, or they can never join another party.
    /// </summary>
    [Fact]
    public void Leave_DisbandsATwoPersonPartyAndReleasesBothMembers()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");

        var (_, disbanded, newLeader, _) = party.Leave("alice");

        Assert.True(disbanded);
        Assert.Null(newLeader);
        Assert.False(party.IsInParty("alice"));
        Assert.False(party.IsInParty("bob"));
        Assert.Empty(party.GetMembers(partyId));
    }

    [Fact]
    public void Leave_PromotesTheNextMemberWhenTheLeaderLeaves()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");
        party.Join(partyId, "carol");

        var (remaining, disbanded, newLeader, _) = party.Leave("alice");

        Assert.False(disbanded);
        Assert.Equal("bob", newLeader);
        Assert.Equal("bob", party.GetLeader(partyId));
        Assert.Equal(["bob", "carol"], remaining);
    }

    [Fact]
    public void Leave_KeepsTheLeaderWhenANonLeaderLeaves()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");
        party.Join(partyId, "carol");

        var (remaining, disbanded, newLeader, _) = party.Leave("carol");

        Assert.False(disbanded);
        Assert.Null(newLeader);
        Assert.Equal("alice", party.GetLeader(partyId));
        Assert.Equal(["alice", "bob"], remaining);
    }

    [Fact]
    public void Leave_IsANoOpForSomeoneNotInAParty()
    {
        var party = new PartyService();

        var (remaining, disbanded, newLeader, partyId) = party.Leave("nobody");

        Assert.Empty(remaining);
        Assert.False(disbanded);
        Assert.Null(newLeader);
        Assert.Equal(string.Empty, partyId);
    }

    [Fact]
    public void Kick_RemovesTheTargetAndFreesThemToJoinElsewhere()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");
        party.Join(partyId, "carol");

        var (success, remaining, disbanded, leader, _) = party.Kick("alice", "carol");

        Assert.True(success);
        Assert.False(disbanded);
        Assert.Equal("alice", leader);
        Assert.Equal(["alice", "bob"], remaining);
        Assert.False(party.IsInParty("carol"));
    }

    [Fact]
    public void Kick_IsRefusedForANonLeader()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");
        party.Join(partyId, "carol");

        var (success, _, _, _, _) = party.Kick("bob", "carol");

        Assert.False(success);
        Assert.True(party.IsInParty("carol"));
    }

    /// <summary>A leader kicking themselves would disband the party through the wrong door.</summary>
    [Fact]
    public void Kick_IsRefusedWhenTheLeaderTargetsThemselves()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");

        var (success, _, _, _, _) = party.Kick("alice", "alice");

        Assert.False(success);
        Assert.True(party.IsInParty("alice"));
    }

    [Fact]
    public void Kick_IsRefusedForSomeoneOutsideTheParty()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");

        var (success, _, _, _, _) = party.Kick("alice", "stranger");

        Assert.False(success);
    }

    [Fact]
    public void Kick_DisbandsWhenItWouldLeaveOnlyOneMember()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");

        var (success, _, disbanded, _, _) = party.Kick("alice", "bob");

        Assert.True(success);
        Assert.True(disbanded);
        Assert.False(party.IsInParty("alice"));
        Assert.False(party.IsInParty("bob"));
    }

    [Fact]
    public void TransferLeader_HandsOverToAnotherMember()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");

        var (success, members, leader) = party.TransferLeader("alice", "bob");

        Assert.True(success);
        Assert.Equal("bob", leader);
        Assert.Equal("bob", party.GetLeader(partyId));
        Assert.Equal(["alice", "bob"], members);
    }

    [Fact]
    public void TransferLeader_IsRefusedFromANonLeader()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");
        party.Join(partyId, "carol");

        var (success, _, _) = party.TransferLeader("bob", "carol");

        Assert.False(success);
        Assert.Equal("alice", party.GetLeader(partyId));
    }

    [Fact]
    public void TransferLeader_IsRefusedForSomeoneOutsideTheParty()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");

        var (success, _, _) = party.TransferLeader("alice", "stranger");

        Assert.False(success);
        Assert.Equal("alice", party.GetLeader(partyId));
    }

    /// <summary>
    /// The SignalR group name is how party messages actually get routed — pinning the format keeps
    /// a rename from silently splitting senders and receivers onto different groups.
    /// </summary>
    [Fact]
    public void PartyGroup_UsesThePartyIdPrefixedForm()
    {
        Assert.Equal("party_abc123", PartyService.PartyGroup("abc123"));
    }

    /// <summary>After a party disbands its id must resolve to nothing, not to a stale roster.</summary>
    [Fact]
    public void GetMembers_ReturnsEmptyForADisbandedParty()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Join(partyId, "bob");
        party.Leave("alice");

        Assert.Empty(party.GetMembers(partyId));
        Assert.Null(party.GetLeader(partyId));
    }

    /// <summary>
    /// Full cycle: someone who leaves a party must be able to start a fresh one. This is the
    /// regression guard for the member→party index leaking entries.
    /// </summary>
    [Fact]
    public void APlayerCanFormANewPartyAfterLeavingTheOldOne()
    {
        var party = new PartyService();
        var (firstId, _, _) = party.GetOrCreate("alice");
        party.Join(firstId, "bob");
        party.Join(firstId, "carol");
        party.Leave("alice");

        var (secondId, members, leader) = party.GetOrCreate("alice");

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(["alice"], members);
        Assert.Equal("alice", leader);
    }
}
