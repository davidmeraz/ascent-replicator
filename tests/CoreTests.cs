using System;
using System.Collections.Generic;
using System.Linq;
using AscentReplicator;

internal sealed class FakeGateway : IOrderGateway
{
    public readonly Dictionary<string, OrderSnapshot> Orders = new Dictionary<string, OrderSnapshot>();
    public readonly Dictionary<string, string> Ocos = new Dictionary<string, string>();
    public readonly List<string> Calls = new List<string>();
    public bool Online = true;
    public bool ThrowSubmit;
    public bool Connected(string account) { return Online; }
    public string Create(string account, OrderSnapshot source, string oco)
    {
        string key = account + "/" + Orders.Count;
        Orders.Add(key, source.Clone());
        Ocos.Add(key, oco);
        return key;
    }
    public void Submit(string account, string key)
    {
        Calls.Add("submit:" + key);
        if (ThrowSubmit) throw new InvalidOperationException("timeout");
    }
    public void Change(string account, string key, OrderSnapshot desired)
    {
        Calls.Add("change:" + key);
        Orders[key] = desired.Clone();
    }
    public void Cancel(string account, string key) { Calls.Add("cancel:" + key); }
}

internal sealed class Fixture
{
    public readonly FakeGateway Gateway = new FakeGateway();
    public readonly List<string> Reports = new List<string>();
    public DateTime Time = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
    public readonly CopierCore Core;
    public Fixture(params string[] accounts)
    {
        Core = new CopierCore(Gateway, accounts.Length == 0 ? new[] { "A" } : accounts,
            Reports.Add, () => Time);
    }
    public void Ack(string key, CopyState state, int filled = 0)
    {
        var snapshot = Gateway.Orders[key].Clone();
        snapshot.Key = key;
        snapshot.State = state;
        snapshot.Filled = filled;
        Core.OnFollower(key.Split('/')[0], snapshot);
    }
    public void Advance(double seconds) { Time = Time.AddSeconds(seconds); Core.Tick(); }
}

internal static class CoreTests
{
    private static int passed;
    private static OrderSnapshot Source(string key = "source", string oco = "")
    {
        return new OrderSnapshot { Key = key, Instrument = "MES 12-26", Action = "Buy",
            Type = "Limit", TimeInForce = "Day", Quantity = 2, LimitPrice = 6000,
            Gtd = DateTime.MaxValue, State = CopyState.Submitted, Manual = true, Oco = oco };
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Test(string name, Action test)
    {
        test(); passed++; Console.WriteLine("PASS " + name);
    }
    public static int Main()
    {
        try
        {
            Test("one submission per source/account despite duplicate events", () =>
            {
                var f = new Fixture("A", "B", "A"); var s = Source();
                f.Core.OnSource(s); f.Core.OnSource(s); s.State = CopyState.Working; f.Core.OnSource(s);
                Assert(f.Gateway.Calls.Count == 2, "duplicate submission");
                Assert(f.Gateway.Orders.Values.All(o => o.Quantity == 2 && o.LimitPrice == 6000), "terms changed");
            });
            Test("unknown terminal cancellations are never opened", () =>
            {
                var f = new Fixture(); var s = Source(); s.State = CopyState.Cancelled;
                f.Core.OnSource(s); Assert(f.Gateway.Calls.Count == 0, "copied cancelled order");
            });
            Test("automated source halts copying", () =>
            {
                var f = new Fixture(); var s = Source(); s.Manual = false; f.Core.OnSource(s);
                Assert(f.Core.Faulted && f.Gateway.Calls.Count == 0, "automated source copied");
            });
            Test("disconnected follower prevents sending", () =>
            {
                var f = new Fixture(); f.Gateway.Online = false; f.Core.OnSource(Source());
                Assert(f.Core.Faulted && f.Gateway.Calls.Count == 0, "sent offline");
            });
            Test("submission exceptions are never retried", () =>
            {
                var f = new Fixture(); f.Gateway.ThrowSubmit = true;
                f.Core.OnSource(Source()); f.Core.OnSource(Source());
                Assert(f.Core.Faulted && f.Gateway.Calls.Count == 1, "submission retried");
            });
            Test("pending submission retains latest confirmed change", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s);
                s.State = CopyState.Working; s.LimitPrice = 6001; f.Core.OnSource(s);
                Assert(f.Gateway.Calls.Count == 1, "changed before ack");
                f.Ack("A/0", CopyState.Working);
                Assert(f.Gateway.Calls.Last() == "change:A/0" && f.Gateway.Orders["A/0"].LimitPrice == 6001, "lost change");
            });
            Test("changes serialize and coalesce while awaiting acknowledgment", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                s.State = CopyState.Working; s.LimitPrice = 6001; f.Core.OnSource(s);
                s.LimitPrice = 6002; f.Core.OnSource(s); s.LimitPrice = 6003; f.Core.OnSource(s);
                Assert(f.Gateway.Calls.Count == 2, "overlapping changes");
                f.Ack("A/0", CopyState.Working);
                Assert(f.Gateway.Calls.Count == 3 && f.Gateway.Orders["A/0"].LimitPrice == 6003, "lost final price");
            });
            Test("unconfirmed source changes are not mirrored", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                s.State = CopyState.ChangePending; s.LimitPrice = 6100; f.Core.OnSource(s);
                Assert(f.Gateway.Calls.Count == 1, "copied unconfirmed change");
            });
            Test("cancellation waits for initial acknowledgment", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s);
                s.State = CopyState.Cancelled; f.Core.OnSource(s); Assert(f.Gateway.Calls.Count == 1, "early cancel");
                f.Ack("A/0", CopyState.Working); f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                Assert(f.Gateway.Calls.Count(c => c.StartsWith("cancel:")) == 1, "duplicate or missing cancel");
            });
            Test("OCO is unique per account and shared between sibling orders", () =>
            {
                var f = new Fixture("A", "B"); f.Core.OnSource(Source("stop", "group"));
                f.Core.OnSource(Source("target", "group"));
                Assert(f.Gateway.Ocos["A/0"] == f.Gateway.Ocos["A/2"], "siblings unlinked");
                Assert(f.Gateway.Ocos["A/0"] != f.Gateway.Ocos["B/1"] && f.Gateway.Ocos["A/0"] != "group", "shared OCO accounts");
            });
            Test("leader OCO cancellation does not remove follower protection before sibling fills", () =>
            {
                var f = new Fixture(); var stop = Source("stop", "group"); var target = Source("target", "group");
                f.Core.OnSource(stop); f.Core.OnSource(target); f.Ack("A/0", CopyState.Working); f.Ack("A/1", CopyState.Working);
                stop.State = CopyState.Cancelled; f.Core.OnSource(stop);
                target.State = CopyState.Filled; target.Filled = 2; f.Core.OnSource(target);
                f.Advance(0.1); f.Advance(0.4);
                Assert(!f.Gateway.Calls.Any(c => c.StartsWith("cancel:")), "premature OCO cancellation");
                f.Ack("A/1", CopyState.Filled, 2); f.Ack("A/0", CopyState.Cancelled);
                f.Advance(0.1); Assert(!f.Core.Faulted, "normal OCO fault");
            });
            Test("explicit OCO cancellation settles event ordering before cancelling", () =>
            {
                var f = new Fixture(); var s = Source("stop", "group"); f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                s.State = CopyState.Cancelled; f.Core.OnSource(s); f.Core.Tick(); f.Advance(0.4);
                Assert(f.Gateway.Calls.Last() == "cancel:A/0", "explicit OCO not cancelled");
            });
            Test("partial fills do not generate duplicate market orders", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                s.State = CopyState.PartFilled; s.Filled = 1; f.Core.OnSource(s); f.Ack("A/0", CopyState.PartFilled, 1);
                f.Advance(4); Assert(!f.Core.Faulted && f.Gateway.Calls.Count == 1, "partial fill copied twice");
            });
            Test("divergent fills halt after grace period", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                s.State = CopyState.Filled; s.Filled = 2; f.Core.OnSource(s); f.Core.Tick(); f.Advance(3.1);
                Assert(f.Core.Faulted && f.Gateway.Calls.Count == 1, "divergence ignored or resent");
            });
            Test("follower rejection stops all future source submissions", () =>
            {
                var f = new Fixture(); f.Core.OnSource(Source()); f.Ack("A/0", CopyState.Rejected);
                f.Core.OnSource(Source("second")); Assert(f.Core.Faulted && f.Gateway.Calls.Count == 1, "continued after rejection");
            });
            Test("terminal source cannot resurrect after stale events", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                s.State = CopyState.Filled; s.Filled = 2; f.Core.OnSource(s); f.Ack("A/0", CopyState.Filled, 2);
                s.State = CopyState.Working; s.LimitPrice = 6100; f.Core.OnSource(s);
                Assert(f.Gateway.Calls.Count == 1, "resurrected terminal order");
            });
            Test("initial filled event still mirrors total order once", () =>
            {
                var f = new Fixture(); var s = Source(); s.State = CopyState.Filled; s.Filled = 2;
                f.Core.OnSource(s); f.Core.OnSource(s); Assert(f.Gateway.Calls.Count == 1, "lost fast fill or duplicate");
            });
            Test("unacknowledged submission times out without retry", () =>
            {
                var f = new Fixture(); f.Core.OnSource(Source()); f.Advance(10.1);
                Assert(f.Core.Faulted && f.Gateway.Calls.Count == 1, "timeout unhandled");
            });
            Test("foreign filled follower order halts copying", () =>
            {
                var f = new Fixture(); var s = Source("foreign"); s.State = CopyState.Filled;
                f.Core.OnFollower("A", s); Assert(f.Core.Faulted, "foreign fill ignored");
            });
            Test("quantity reduction below follower fill is blocked", () =>
            {
                var f = new Fixture(); var s = Source(); f.Core.OnSource(s); f.Ack("A/0", CopyState.PartFilled, 2);
                s.State = CopyState.Working; s.Quantity = 1; f.Core.OnSource(s);
                Assert(f.Core.Faulted && f.Gateway.Calls.Count == 1, "invalid reduction sent");
            });
            Test("MIT unsupported order is blocked before sending", () =>
            {
                var f = new Fixture(); var s = Source(); s.Type = "MIT"; f.Core.OnSource(s);
                Assert(f.Core.Faulted && f.Gateway.Calls.Count == 0, "unsupported order sent");
            });
            Test("stop and quantity changes preserve exact terms", () =>
            {
                var f = new Fixture(); var s = Source(); s.Type = "StopLimit"; s.StopPrice = 6001;
                f.Core.OnSource(s); f.Ack("A/0", CopyState.Working);
                s.State = CopyState.Working; s.Quantity = 3; s.StopPrice = 6002; s.LimitPrice = 6003;
                f.Core.OnSource(s); f.Ack("A/0", CopyState.Working); f.Advance(1);
                Assert(!f.Core.Faulted && f.Gateway.Orders["A/0"].Quantity == 3
                    && f.Gateway.Orders["A/0"].StopPrice == 6002 && f.Gateway.Orders["A/0"].LimitPrice == 6003,
                    "stop/quantity not copied");
            });
            Test("follower event from wrong account is rejected", () =>
            {
                var f = new Fixture(); f.Core.OnSource(Source());
                var s = f.Gateway.Orders["A/0"].Clone(); s.Key = "A/0"; s.State = CopyState.Working;
                f.Core.OnFollower("B", s); Assert(f.Core.Faulted, "wrong account accepted");
            });
            Test("initial source error never submits a follower", () =>
            {
                var f = new Fixture(); var s = Source(); s.Error = "custom order unsupported";
                f.Core.OnSource(s); Assert(f.Core.Faulted && f.Gateway.Calls.Count == 0, "source error copied");
            });
            Console.WriteLine(passed + " tests passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
