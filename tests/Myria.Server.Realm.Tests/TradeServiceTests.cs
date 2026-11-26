using Myria.Server.Realm.Services;
using Xunit;

/// <summary>
/// TradeService holds the state behind player-to-player trading. The rule that matters most here
/// is that <em>any</em> change to either side's offer clears both ready flags: without it, a
/// scammer can wait for their partner to confirm, then swap in a worthless item and commit — the
/// classic trade-window scam. Every mutator is checked for that below.
///
/// Snapshots are also perspective-dependent (each side sees their own offer as "mine"), which is
/// easy to get backwards and would show players each other's inventories as their own.
/// </summary>
public class TradeServiceTests
{
    /// <summary>Sets up an accepted, in-progress trade and returns both connection ids.</summary>
    private static (TradeService Trade, string Proposer, string Acceptor) StartedTrade()
    {
        var trade = new TradeService();
        var (tradeId, ok) = trade.Propose("conn-a", "alice", "bob");
        Assert.True(ok);

        var accepted = trade.Accept("conn-b", "bob", tradeId);
        Assert.True(accepted.Ok);

        return (trade, "conn-a", "conn-b");
    }

    [Fact]
    public void Propose_IssuesATradeIdForAFreeConnection()
    {
        var trade = new TradeService();

        var (tradeId, ok) = trade.Propose("conn-a", "alice", "bob");

        Assert.True(ok);
        Assert.NotEmpty(tradeId);
    }

    [Fact]
    public void Propose_IsRefusedWhileAlreadyTrading()
    {
        var (trade, proposer, _) = StartedTrade();

        var (_, ok) = trade.Propose(proposer, "alice", "bob");

        Assert.False(ok);
    }

    [Fact]
    public void Accept_PairsBothConnectionsIntoOneSession()
    {
        var (trade, proposer, acceptor) = StartedTrade();

        Assert.Equal(acceptor, trade.PartnerConn(proposer));
        Assert.Equal(proposer, trade.PartnerConn(acceptor));
        Assert.NotNull(trade.Snapshot(proposer));
        Assert.NotNull(trade.Snapshot(acceptor));
    }

    [Fact]
    public void Accept_RejectsAnUnknownTradeId()
    {
        var trade = new TradeService();

        var (ok, _, _, _) = trade.Accept("conn-b", "bob", "not-a-real-id");

        Assert.False(ok);
        Assert.Null(trade.Snapshot("conn-b"));
    }

    /// <summary>A proposal can only be taken up once; the second acceptor must be turned away.</summary>
    [Fact]
    public void Accept_RejectsASecondAcceptorForTheSameProposal()
    {
        var trade = new TradeService();
        var (tradeId, _) = trade.Propose("conn-a", "alice", "bob");
        trade.Accept("conn-b", "bob", tradeId);

        var (ok, _, _, _) = trade.Accept("conn-c", "carol", tradeId);

        Assert.False(ok);
        Assert.Null(trade.Snapshot("conn-c"));
    }

    [Fact]
    public void Accept_RejectsAnAcceptorWhoIsAlreadyTrading()
    {
        var (trade, _, acceptor) = StartedTrade();
        var (otherTradeId, _) = trade.Propose("conn-c", "carol", "bob");

        var (ok, _, _, _) = trade.Accept(acceptor, "bob", otherTradeId);

        Assert.False(ok);
    }

    [Fact]
    public void CancelPending_WithdrawsAnUnacceptedProposal()
    {
        var trade = new TradeService();
        var (tradeId, _) = trade.Propose("conn-a", "alice", "bob");

        trade.CancelPending(tradeId, "alice"); // the proposer withdraws it

        Assert.False(trade.Accept("conn-b", "bob", tradeId).Ok);
    }

    // ---- Offer contents ------------------------------------------------------------------

    [Fact]
    public void AddItem_LandsOnTheCallersSideOfTheSnapshot()
    {
        var (trade, proposer, acceptor) = StartedTrade();

        trade.AddItem(proposer, "sword", "Sword", 1);

        var mine = trade.Snapshot(proposer)!;
        var theirs = trade.Snapshot(acceptor)!;

        Assert.Equal("sword", Assert.Single(mine.MyItems).ItemId);
        Assert.Empty(mine.TheirItems);
        Assert.Equal("sword", Assert.Single(theirs.TheirItems).ItemId);
        Assert.Empty(theirs.MyItems);
    }

    [Fact]
    public void AddItem_MergesRepeatedOffersOfTheSameItem()
    {
        var (trade, proposer, _) = StartedTrade();

        trade.AddItem(proposer, "potion", "Potion", 3);
        trade.AddItem(proposer, "potion", "Potion", 2);

        var offered = Assert.Single(trade.Snapshot(proposer)!.MyItems);
        Assert.Equal(5, offered.Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddItem_RejectsNonPositiveQuantities(int quantity)
    {
        var (trade, proposer, _) = StartedTrade();

        Assert.False(trade.AddItem(proposer, "potion", "Potion", quantity));
        Assert.Empty(trade.Snapshot(proposer)!.MyItems);
    }

    [Fact]
    public void AddItem_FailsForAConnectionNotInATrade()
    {
        var trade = new TradeService();

        Assert.False(trade.AddItem("conn-nobody", "potion", "Potion", 1));
    }

    [Fact]
    public void RemoveItem_TakesTheItemBackOffTheTable()
    {
        var (trade, proposer, _) = StartedTrade();
        trade.AddItem(proposer, "sword", "Sword", 1);

        Assert.True(trade.RemoveItem(proposer, "sword"));
        Assert.Empty(trade.Snapshot(proposer)!.MyItems);
    }

    [Fact]
    public void RemoveItem_FailsForAnItemThatWasNeverOffered()
    {
        var (trade, proposer, _) = StartedTrade();

        Assert.False(trade.RemoveItem(proposer, "sword"));
    }

    [Fact]
    public void SetGold_RecordsTheAmountOnTheCallersSide()
    {
        var (trade, proposer, acceptor) = StartedTrade();

        Assert.True(trade.SetGold(proposer, 250));

        Assert.Equal(250, trade.Snapshot(proposer)!.MyGold);
        Assert.Equal(250, trade.Snapshot(acceptor)!.TheirGold);
        Assert.Equal(0, trade.Snapshot(acceptor)!.MyGold);
    }

    [Fact]
    public void SetGold_RejectsANegativeAmount()
    {
        var (trade, proposer, _) = StartedTrade();

        Assert.False(trade.SetGold(proposer, -1));
        Assert.Equal(0, trade.Snapshot(proposer)!.MyGold);
    }

    // ---- Ready-flag invariants (anti-scam) --------------------------------------------------

    [Fact]
    public void Confirm_ReportsBothReadyOnlyAfterBothSidesConfirm()
    {
        var (trade, proposer, acceptor) = StartedTrade();

        var first = trade.Confirm(proposer);
        Assert.True(first.Ok);
        Assert.False(first.BothReady);

        var second = trade.Confirm(acceptor);
        Assert.True(second.Ok);
        Assert.True(second.BothReady);
    }

    [Fact]
    public void Confirm_FailsForAConnectionNotInATrade()
    {
        var trade = new TradeService();

        var (ok, bothReady) = trade.Confirm("conn-nobody");

        Assert.False(ok);
        Assert.False(bothReady);
    }

    /// <summary>
    /// The core anti-scam rule. After both sides are ready, adding an item must drop <em>both</em>
    /// confirmations — not just the changer's — so the partner has to look at the new offer and
    /// confirm again. Same for removing an item and for changing gold.
    /// </summary>
    [Fact]
    public void AddItem_ClearsBothConfirmationsSoThePartnerMustReviewTheChange()
    {
        var (trade, proposer, acceptor) = StartedTrade();
        trade.Confirm(proposer);
        trade.Confirm(acceptor);

        trade.AddItem(proposer, "rock", "Worthless Rock", 1);

        var snapshot = trade.Snapshot(proposer)!;
        Assert.False(snapshot.ImReady);
        Assert.False(snapshot.TheyAreReady);
    }

    [Fact]
    public void RemoveItem_ClearsBothConfirmations()
    {
        var (trade, proposer, acceptor) = StartedTrade();
        trade.AddItem(proposer, "sword", "Sword", 1);
        trade.Confirm(proposer);
        trade.Confirm(acceptor);

        trade.RemoveItem(proposer, "sword");

        var snapshot = trade.Snapshot(acceptor)!;
        Assert.False(snapshot.ImReady);
        Assert.False(snapshot.TheyAreReady);
    }

    [Fact]
    public void SetGold_ClearsBothConfirmations()
    {
        var (trade, proposer, acceptor) = StartedTrade();
        trade.SetGold(proposer, 1000);
        trade.Confirm(proposer);
        trade.Confirm(acceptor);

        trade.SetGold(proposer, 1);

        var snapshot = trade.Snapshot(acceptor)!;
        Assert.False(snapshot.ImReady);
        Assert.False(snapshot.TheyAreReady);
    }

    /// <summary>
    /// A rejected change must not clear confirmations either — otherwise spamming an invalid
    /// quantity becomes a way to keep resetting a partner who is trying to commit.
    /// </summary>
    [Fact]
    public void ARejectedChangeLeavesConfirmationsIntact()
    {
        var (trade, proposer, acceptor) = StartedTrade();
        trade.Confirm(proposer);
        trade.Confirm(acceptor);

        trade.AddItem(proposer, "potion", "Potion", 0);
        trade.SetGold(proposer, -5);
        trade.RemoveItem(proposer, "never-offered");

        var snapshot = trade.Snapshot(proposer)!;
        Assert.True(snapshot.ImReady);
        Assert.True(snapshot.TheyAreReady);
    }

    // ---- Teardown ---------------------------------------------------------------------------

    [Fact]
    public void End_ReleasesBothConnectionsSoTheyCanTradeAgain()
    {
        var (trade, proposer, acceptor) = StartedTrade();

        var (connA, connB) = trade.End(proposer);

        Assert.Equal(proposer, connA);
        Assert.Equal(acceptor, connB);
        Assert.Null(trade.Snapshot(proposer));
        Assert.Null(trade.Snapshot(acceptor));
        Assert.True(trade.Propose(proposer, "alice", "bob").Ok);
    }

    /// <summary>Either side can end the trade, and both are released regardless of who called.</summary>
    [Fact]
    public void End_WorksFromTheAcceptorSideToo()
    {
        var (trade, proposer, acceptor) = StartedTrade();

        var (connA, connB) = trade.End(acceptor);

        Assert.Equal(proposer, connA);
        Assert.Equal(acceptor, connB);
        Assert.Null(trade.Snapshot(proposer));
        Assert.Null(trade.Snapshot(acceptor));
    }

    [Fact]
    public void End_IsANoOpForAConnectionNotInATrade()
    {
        var trade = new TradeService();

        var (connA, connB) = trade.End("conn-nobody");

        Assert.Equal(string.Empty, connA);
        Assert.Equal(string.Empty, connB);
    }

    [Fact]
    public void PartnerConn_IsNullOutsideATrade()
    {
        var trade = new TradeService();
        trade.Propose("conn-a", "alice", "bob");

        Assert.Null(trade.PartnerConn("conn-a"));
    }
}
