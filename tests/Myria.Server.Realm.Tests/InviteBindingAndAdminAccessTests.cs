using System.Net;
using Myria.Server.Realm.Controllers;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// Security review 2026-11-25:
///  - A01 F5: joining a party / accepting or cancelling a trade needs to be the invited character,
///    not just someone holding the 8-character id;
///  - A01 F4: the realm's operator API accepts calls only from this machine (loopback, or a peer
///    that connected to its own address) - never via the public reverse proxy.
/// </summary>
public class InviteBindingAndAdminAccessTests
{
    // ---- Party invites ------------------------------------------------------------------------

    [Fact]
    public void AcceptInvite_WithoutAnInvite_Fails()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");

        Assert.False(party.AcceptInvite(partyId, "mallory").success);
        Assert.Null(party.GetPartyId("mallory"));
    }

    [Fact]
    public void AcceptInvite_ByTheInvitedCharacter_Joins_IgnoringCase()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Invite(partyId, "Bob");

        var (success, members, _) = party.AcceptInvite(partyId, "bob");

        Assert.True(success);
        Assert.Contains("bob", members);
    }

    [Fact]
    public void AcceptInvite_InviteForSomeoneElse_Fails()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Invite(partyId, "bob");

        Assert.False(party.AcceptInvite(partyId, "mallory").success);
    }

    [Fact]
    public void AcceptInvite_IsUsedUp_AndDeclineRemovesIt()
    {
        var party = new PartyService();
        var (partyId, _, _) = party.GetOrCreate("alice");
        party.Invite(partyId, "bob");
        party.Invite(partyId, "carol");

        Assert.True(party.AcceptInvite(partyId, "bob").success);
        party.Leave("bob");
        Assert.False(party.AcceptInvite(partyId, "bob").success); // used up

        party.DeclineInvite(partyId, "carol");
        Assert.False(party.AcceptInvite(partyId, "carol").success);
    }

    // ---- Trade proposals ----------------------------------------------------------------------

    [Fact]
    public void AcceptTrade_ByAnyoneButTheTarget_Fails()
    {
        var trade = new TradeService();
        var (tradeId, _) = trade.Propose("conn-a", "alice", "bob");

        Assert.False(trade.Accept("conn-m", "mallory", tradeId).Ok);
        Assert.True(trade.Accept("conn-b", "Bob", tradeId).Ok); // still open for the real target
    }

    [Fact]
    public void CancelPending_ByAThirdParty_DoesNothing()
    {
        var trade = new TradeService();
        var (tradeId, _) = trade.Propose("conn-a", "alice", "bob");

        trade.CancelPending(tradeId, "mallory");

        Assert.True(trade.Accept("conn-b", "bob", tradeId).Ok);
    }

    [Fact]
    public void CancelPending_ByTheTarget_Declines()
    {
        var trade = new TradeService();
        var (tradeId, _) = trade.Propose("conn-a", "alice", "bob");

        trade.CancelPending(tradeId, "bob");

        Assert.False(trade.Accept("conn-b", "bob", tradeId).Ok);
    }

    // ---- Operator API: this machine only ------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]    // admin site / auth server via InternalUrl
    [InlineData("::1", "::1", true)]
    [InlineData("192.168.1.1", "192.168.1.1", true)] // own address, connected to itself
    [InlineData("192.168.1.2", "127.0.0.1", false)]  // via the reverse proxy (X-Forwarded-For resolved)
    [InlineData("93.184.216.34", "127.0.0.1", false)] // internet visitor via the proxy
    [InlineData("192.168.1.50", "192.168.1.1", false)] // another LAN device, direct
    public void OperatorApi_OnlyFromThisMachine(string remote, string local, bool allowed)
    {
        Assert.Equal(allowed, AdminController.IsOnThisMachine(IPAddress.Parse(remote), IPAddress.Parse(local)));
    }

    [Fact]
    public void OperatorApi_UnknownPeer_IsDenied() =>
        Assert.False(AdminController.IsOnThisMachine(null, IPAddress.Loopback));
}
