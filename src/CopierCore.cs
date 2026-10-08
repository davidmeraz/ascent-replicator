using System;
using System.Collections.Generic;
using System.Linq;

namespace AscentReplicator
{
    // All methods are called on one dispatcher. The platform adapter snapshots
    // event arguments before posting; mutable NinjaTrader Orders stay in the adapter.
    public enum CopyState
    {
        Initialized, Submitted, Accepted, Working, TriggerPending, PartFilled,
        Filled, Cancelled, Rejected, ChangePending, ChangeSubmitted,
        CancelPending, CancelSubmitted, Suspended, Unknown, AcceptedByRisk
    }

    public sealed class OrderSnapshot
    {
        public string Key;
        public string Instrument;
        public string Action;
        public string Type;
        public string TimeInForce;
        public string Oco;
        public DateTime Gtd;
        public int Quantity;
        public int Filled;
        public double LimitPrice;
        public double StopPrice;
        public CopyState State;
        public bool Manual;
        public string Error;

        public OrderSnapshot Clone() { return (OrderSnapshot)MemberwiseClone(); }
        public bool SameTerms(OrderSnapshot other)
        {
            return Quantity == other.Quantity && LimitPrice == other.LimitPrice
                && StopPrice == other.StopPrice;
        }
        public static bool Terminal(CopyState state)
        {
            return state == CopyState.Filled || state == CopyState.Cancelled || state == CopyState.Rejected;
        }
        public static bool Stable(CopyState state)
        {
            return state == CopyState.Accepted || state == CopyState.Working
                || state == CopyState.TriggerPending || state == CopyState.PartFilled;
        }
    }

    public interface IOrderGateway
    {
        bool Connected(string account);
        string Create(string account, OrderSnapshot source, string oco);
        void Submit(string account, string key);
        void Change(string account, string key, OrderSnapshot desired);
        void Cancel(string account, string key);
    }

    public sealed class CopierCore
    {
        private sealed class Link
        {
            public string Account;
            public string Key;
            public OrderSnapshot Desired;
            public OrderSnapshot Sent;
            public CopyState State = CopyState.Initialized;
            public int Filled;
            public bool Pending;
            public bool CancelSent;
            public DateTime PendingSince;
        }
        private sealed class Source
        {
            public OrderSnapshot Latest;
            public readonly List<Link> Links = new List<Link>();
        }

        private readonly IOrderGateway gateway;
        private readonly string[] followers;
        private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>();
        private readonly Dictionary<string, Source> reverse = new Dictionary<string, Source>();
        private readonly Dictionary<string, string> ocoMap = new Dictionary<string, string>();
        private readonly Action<string> report;
        private readonly Func<DateTime> now;
        public bool Faulted { get; private set; }
        public string FaultReason { get; private set; }

        public CopierCore(IOrderGateway gateway, IEnumerable<string> followers,
            Action<string> report, Func<DateTime> now)
        {
            this.gateway = gateway;
            this.followers = followers.Distinct().ToArray();
            if (this.followers.Length == 0) throw new ArgumentException("Select at least one follower account.");
            this.report = report;
            this.now = now;
        }

        public void Fault(string reason)
        {
            if (Faulted) return;
            Faulted = true;
            FaultReason = reason;
            report("ERROR: " + reason + " Copying stopped; review positions and orders in every account.");
        }

        private string MapOco(string account, string original)
        {
            if (String.IsNullOrEmpty(original)) return String.Empty;
            string key = account + "|" + original;
            string result;
            if (!ocoMap.TryGetValue(key, out result))
            {
                result = Guid.NewGuid().ToString("N");
                ocoMap.Add(key, result);
            }
            return result;
        }

        public void OnSource(OrderSnapshot snapshot)
        {
            if (Faulted) return;
            Source source;
            if (!sources.TryGetValue(snapshot.Key, out source))
            {
                if (OrderSnapshot.Terminal(snapshot.State) && snapshot.State != CopyState.Filled) return;
                if (snapshot.State != CopyState.Submitted && snapshot.State != CopyState.Filled
                    && !OrderSnapshot.Stable(snapshot.State)) return;
                if (!String.IsNullOrEmpty(snapshot.Error))
                {
                    Fault("Leader order error: " + snapshot.Error);
                    return;
                }
                if (!snapshot.Manual)
                {
                    Fault("The leader account received an automated order. Use manual entries.");
                    return;
                }
                if (snapshot.Quantity <= 0
                    || !(new[] { "Market", "Limit", "StopMarket", "StopLimit" }).Contains(snapshot.Type)
                    || !(new[] { "Day", "Gtc", "Gtd" }).Contains(snapshot.TimeInForce))
                {
                    Fault("Unsupported order type or time in force: " + snapshot.Type + " / " + snapshot.TimeInForce);
                    return;
                }
                // Reserve source identity BEFORE creating/submitting any follower order.
                source = new Source { Latest = snapshot.Clone() };
                sources.Add(snapshot.Key, source);
                try
                {
                    // Check the whole group before the first send. Providers still cannot
                    // participate in an atomic transaction; a later send can fail independently.
                    foreach (string account in followers)
                        if (!gateway.Connected(account)) { Fault("Account disconnected: " + account); return; }
                    foreach (string account in followers)
                    {
                        if (!gateway.Connected(account)) { Fault("Account disconnected: " + account); return; }
                        string key = gateway.Create(account, snapshot, MapOco(account, snapshot.Oco));
                        Link link = new Link { Account = account, Key = key,
                            Desired = snapshot.Clone(), Sent = snapshot.Clone(),
                            Pending = true, PendingSince = now() };
                        source.Links.Add(link);
                        reverse.Add(key, source);
                        gateway.Submit(account, key);
                        report("Submitted " + snapshot.Action + " " + snapshot.Quantity + " "
                            + snapshot.Instrument + " to " + account);
                    }
                }
                catch (Exception ex) { Fault("Could not submit the copied order: " + ex.Message); }
                return;
            }

            if (!String.IsNullOrEmpty(snapshot.Error)) { Fault("Leader order error: " + snapshot.Error); return; }
            if (OrderSnapshot.Terminal(source.Latest.State)) return; // Never resurrect a terminal order.
            if (snapshot.Instrument != source.Latest.Instrument || snapshot.Action != source.Latest.Action
                || snapshot.Type != source.Latest.Type || snapshot.Oco != source.Latest.Oco
                || snapshot.TimeInForce != source.Latest.TimeInForce || snapshot.Gtd != source.Latest.Gtd)
            {
                Fault("A leader order's identity or time in force changed.");
                return;
            }
            // Price/quantity requests are mirrored only after the source acknowledges them.
            if (!OrderSnapshot.Stable(snapshot.State) && !OrderSnapshot.Terminal(snapshot.State)) return;
            source.Latest = snapshot.Clone();
            foreach (Link link in source.Links)
            {
                link.Desired = snapshot.Clone();
                Pump(source, link);
                if (Faulted) break;
            }
        }

        public void OnFollower(string account, OrderSnapshot snapshot)
        {
            if (Faulted) return;
            Source source;
            if (!reverse.TryGetValue(snapshot.Key, out source))
            {
                Fault("An unrelated order was detected in follower account " + account);
                return;
            }
            Link link = source.Links.First(l => l.Key == snapshot.Key);
            if (link.Account != account)
            {
                Fault("An order update was received for a different account.");
                return;
            }
            if (snapshot.State == CopyState.Rejected || !String.IsNullOrEmpty(snapshot.Error))
            {
                Fault("Order rejected or reported an error in " + account + ": " + snapshot.Error);
                return;
            }
            if (OrderSnapshot.Terminal(link.State)) return;
            link.State = snapshot.State;
            link.Filled = snapshot.Filled;
            if (OrderSnapshot.Stable(snapshot.State) && snapshot.SameTerms(link.Sent)) link.Pending = false;
            if (OrderSnapshot.Terminal(snapshot.State)) link.Pending = false;
            Pump(source, link);
        }

        private void Pump(Source source, Link link)
        {
            if (Faulted) return;
            if (link.Desired.State == CopyState.Cancelled || link.Desired.State == CopyState.Rejected)
            {
                if (OrderSnapshot.Terminal(link.State))
                {
                    if (link.State == CopyState.Filled)
                        Fault("The leader cancelled an order already filled in " + link.Account);
                    return;
                }
                if (link.Pending || link.CancelSent || !OrderSnapshot.Stable(link.State)) return;
                // The leader's OCO cancellation can arrive BEFORE a sibling Filled event.
                // Let each follower's own OCO resolve first; audit unresolved cancellation.
                if (!String.IsNullOrEmpty(link.Desired.Oco))
                {
                    Source sibling = sources.Values.FirstOrDefault(s => s != source
                        && s.Latest.Oco == source.Latest.Oco && s.Latest.State == CopyState.Filled);
                    if (sibling != null)
                    {
                        Link other = sibling.Links.FirstOrDefault(l => l.Account == link.Account);
                        if (other == null || other.State != CopyState.Filled) return;
                    }
                    else return; // Tick handles explicit OCO cancellations after event ordering settles.
                }
                SendCancel(link);
                return;
            }
            if (OrderSnapshot.Terminal(link.State)) return;
            if (link.Pending || !OrderSnapshot.Stable(link.State) || link.Desired.SameTerms(link.Sent)) return;
            if (link.Desired.Quantity <= 0 || link.Desired.Quantity < link.Filled)
            {
                Fault("Requested quantity is incompatible with partial fills in " + link.Account);
                return;
            }
            try
            {
                if (!gateway.Connected(link.Account)) { Fault("Account disconnected: " + link.Account); return; }
                link.Sent = link.Desired.Clone();
                link.Pending = true;
                link.PendingSince = now();
                gateway.Change(link.Account, link.Key, link.Sent);
                report("Requested order change in " + link.Account);
            }
            catch (Exception ex) { Fault("Could not change an order in " + link.Account + ": " + ex.Message); }
        }

        private void SendCancel(Link link)
        {
            try
            {
                if (!gateway.Connected(link.Account)) { Fault("Account disconnected: " + link.Account); return; }
                link.CancelSent = true;
                link.Pending = true;
                link.PendingSince = now();
                gateway.Cancel(link.Account, link.Key);
                report("Requested cancellation in " + link.Account);
            }
            catch (Exception ex) { Fault("Could not cancel an order in " + link.Account + ": " + ex.Message); }
        }

        private readonly Dictionary<string, DateTime> mismatchSince = new Dictionary<string, DateTime>();
        public void Tick()
        {
            if (Faulted) return;
            foreach (Source source in sources.Values)
            {
                foreach (Link link in source.Links)
                {
                    if (!gateway.Connected(link.Account)) { Fault("Account disconnected: " + link.Account); return; }
                    if (link.Pending && now() - link.PendingSince > TimeSpan.FromSeconds(10))
                    {
                        Fault("No order acknowledgment received within 10 seconds in " + link.Account);
                        return;
                    }
                    bool mismatch = source.Latest.Filled != link.Filled
                        || (source.Latest.State == CopyState.Cancelled && !OrderSnapshot.Terminal(link.State))
                        || (link.State == CopyState.Cancelled && !OrderSnapshot.Terminal(source.Latest.State));
                    DateTime since;
                    if (!mismatch) { mismatchSince.Remove(link.Key); continue; }
                    if (!mismatchSince.TryGetValue(link.Key, out since))
                    {
                        mismatchSince.Add(link.Key, now());
                        continue;
                    }
                    // Wait briefly for asynchronously delivered OCO sibling events.
                    if (now() - since > TimeSpan.FromMilliseconds(250)
                        && source.Latest.State == CopyState.Cancelled && !String.IsNullOrEmpty(source.Latest.Oco)
                        && !link.Pending && !link.CancelSent && OrderSnapshot.Stable(link.State))
                    {
                        bool filledSibling = sources.Values.Any(s => s != source
                            && s.Latest.Oco == source.Latest.Oco && s.Latest.State == CopyState.Filled);
                        if (!filledSibling) SendCancel(link);
                        else Pump(source, link);
                    }
                    if (now() - since > TimeSpan.FromSeconds(3))
                    {
                        Fault("Fills or cancellations differ in " + link.Account);
                        return;
                    }
                }
            }
        }
    }
}
