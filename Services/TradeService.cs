namespace Myria.Server.Realm.Services
{
    public record TradeItem(string ItemId, string Name, int Quantity);

    public record TradeSnapshot(
        List<TradeItem> MyItems,
        List<TradeItem> TheirItems,
        long MyGold,
        long TheirGold,
        bool ImReady,
        bool TheyAreReady);

    // Registered as a singleton (Program.cs) - these two dictionaries back *every* trade on the
    // server at once, not just one pair of players. A plain Dictionary<K,V> isn't thread-safe
    // even for writes to unrelated keys (concurrent inserts/removals from two completely
    // unrelated trade sessions can corrupt its internal bucket arrays), and every method here is
    // a multi-step check-then-act sequence that isn't atomic just by swapping in a
    // ConcurrentDictionary either. Every public method below is wrapped in a single lock, same
    // pattern as PartyService - see the 2026-09-10 security audit for the original finding.
    public class TradeService
    {
        private sealed class Session
        {
            public string TradeId  { get; } = Guid.NewGuid().ToString("N")[..8];
            public string ConnA    { get; init; } = "";
            public string ConnB    { get; init; } = "";
            public string NameA    { get; init; } = "";
            public string NameB    { get; init; } = "";

            public List<TradeItem> OffersA { get; } = new();
            public List<TradeItem> OffersB { get; } = new();
            public long GoldA { get; set; }
            public long GoldB { get; set; }
            public bool ReadyA { get; set; }
            public bool ReadyB { get; set; }
        }

        private readonly Dictionary<string, (string ProposerConn, string ProposerName)> _pending  = new();
        private readonly Dictionary<string, Session>                                     _byConn   = new();
        private readonly object _lock = new();

        // Returns (tradeId, ok). Fails if proposer is already in a trade.
        public (string TradeId, bool Ok) Propose(string proposerConn, string proposerName)
        {
            lock (_lock)
            {
                if (_byConn.ContainsKey(proposerConn)) return ("", false);
                var tradeId = Guid.NewGuid().ToString("N")[..8];
                _pending[tradeId] = (proposerConn, proposerName);
                return (tradeId, true);
            }
        }

        public void CancelPending(string tradeId)
        {
            lock (_lock)
                _pending.Remove(tradeId);
        }

        // Returns (ok, proposerConn, proposerName, session)
        public (bool Ok, string ProposerConn, string ProposerName, string TradeId) Accept(
            string acceptorConn, string acceptorName, string tradeId)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(tradeId, out var p)) return (false, "", "", "");
                if (_byConn.ContainsKey(acceptorConn) || _byConn.ContainsKey(p.ProposerConn))
                    return (false, "", "", "");

                _pending.Remove(tradeId);
                var session = new Session
                {
                    ConnA = p.ProposerConn,
                    ConnB = acceptorConn,
                    NameA = p.ProposerName,
                    NameB = acceptorName,
                };
                _byConn[p.ProposerConn] = session;
                _byConn[acceptorConn]   = session;
                return (true, p.ProposerConn, p.ProposerName, session.TradeId);
            }
        }

        // Returns null if not in a trade. Caller must already hold _lock.
        private Session? Get(string connId)
            => _byConn.TryGetValue(connId, out var s) ? s : null;

        // Returns snapshot from the perspective of the caller's connection.
        public TradeSnapshot? Snapshot(string connId)
        {
            lock (_lock)
            {
                var s = Get(connId);
                if (s is null) return null;
                bool isA = s.ConnA == connId;
                return new TradeSnapshot(
                    isA ? s.OffersA.ToList() : s.OffersB.ToList(),
                    isA ? s.OffersB.ToList() : s.OffersA.ToList(),
                    isA ? s.GoldA   : s.GoldB,
                    isA ? s.GoldB   : s.GoldA,
                    isA ? s.ReadyA  : s.ReadyB,
                    isA ? s.ReadyB  : s.ReadyA);
            }
        }

        public string? PartnerConn(string connId)
        {
            lock (_lock)
            {
                var s = Get(connId);
                if (s is null) return null;
                return s.ConnA == connId ? s.ConnB : s.ConnA;
            }
        }

        // Adds or merges an item into the caller's offer. Resets both ready flags.
        public bool AddItem(string connId, string itemId, string name, int quantity)
        {
            lock (_lock)
            {
                var s = Get(connId);
                if (s is null || quantity <= 0) return false;
                var offers = s.ConnA == connId ? s.OffersA : s.OffersB;
                var existing = offers.FirstOrDefault(o => o.ItemId == itemId);
                if (existing is not null)
                    offers[offers.IndexOf(existing)] = existing with { Quantity = existing.Quantity + quantity };
                else
                    offers.Add(new TradeItem(itemId, name, quantity));
                s.ReadyA = false;
                s.ReadyB = false;
                return true;
            }
        }

        // Removes an item from the caller's offer. Resets both ready flags.
        public bool RemoveItem(string connId, string itemId)
        {
            lock (_lock)
            {
                var s = Get(connId);
                if (s is null) return false;
                var offers = s.ConnA == connId ? s.OffersA : s.OffersB;
                var item = offers.FirstOrDefault(o => o.ItemId == itemId);
                if (item is null) return false;
                offers.Remove(item);
                s.ReadyA = false;
                s.ReadyB = false;
                return true;
            }
        }

        // Sets the gold offer for the caller. Resets both ready flags.
        public bool SetGold(string connId, long amount)
        {
            lock (_lock)
            {
                var s = Get(connId);
                if (s is null || amount < 0) return false;
                if (s.ConnA == connId) s.GoldA = amount;
                else                   s.GoldB = amount;
                s.ReadyA = false;
                s.ReadyB = false;
                return true;
            }
        }

        // Sets caller as ready. Returns true + bothReady when both confirmed.
        public (bool Ok, bool BothReady) Confirm(string connId)
        {
            lock (_lock)
            {
                var s = Get(connId);
                if (s is null) return (false, false);
                if (s.ConnA == connId) s.ReadyA = true;
                else                   s.ReadyB = true;
                return (true, s.ReadyA && s.ReadyB);
            }
        }

        // Removes both parties from the service. Call on cancel or completion.
        public (string ConnA, string ConnB) End(string connId)
        {
            lock (_lock)
            {
                var s = Get(connId);
                if (s is null) return ("", "");
                _byConn.Remove(s.ConnA);
                _byConn.Remove(s.ConnB);
                return (s.ConnA, s.ConnB);
            }
        }
    }
}
