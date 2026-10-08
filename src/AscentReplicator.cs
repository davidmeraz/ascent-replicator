using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AscentReplicator;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;

namespace NinjaTrader.NinjaScript.AddOns
{
    public class AscentReplicator : AddOnBase
    {
        private NTMenuItem menu;
        private NTMenuItem parent;
        private AscentReplicatorWindow copierWindow;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Ascent Replicator";
                Description = "Manual 1:1 order replication for accounts connected to NinjaTrader 8.";
            }
            else if (State == State.Terminated && copierWindow != null)
            {
                AscentReplicatorWindow closingWindow = copierWindow;
                closingWindow.Dispatcher.BeginInvoke(new Action(() => closingWindow.Shutdown()));
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter control = window as ControlCenter;
            if (control == null || menu != null) return;
            parent = control.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (parent == null) return;
            menu = new NTMenuItem { Header = "Ascent Replicator" };
            menu.Click += OpenCopier;
            parent.Items.Add(menu);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (!(window is ControlCenter)) return;
            if (menu != null)
            {
                menu.Click -= OpenCopier;
                if (parent != null) parent.Items.Remove(menu);
                menu = null;
                parent = null;
            }
            if (copierWindow != null) copierWindow.Shutdown();
        }

        private void OpenCopier(object sender, RoutedEventArgs e)
        {
            if (copierWindow == null)
            {
                copierWindow = new AscentReplicatorWindow();
                copierWindow.Closed += (s, args) => copierWindow = null;
                copierWindow.Show();
            }
            else copierWindow.Activate();
        }
    }

    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T x, T y) { return Object.ReferenceEquals(x, y); }
        public int GetHashCode(T obj) { return RuntimeHelpers.GetHashCode(obj); }
    }

    internal sealed class AccountChoice
    {
        public Account Account;
        public string Key;
        public override string ToString()
        {
            string connection = Account.Connection == null ? "disconnected" : Account.Connection.Options.Name;
            return Account.Name + " | " + connection + " | " + Account.ConnectionStatus;
        }
    }

    internal sealed class NinjaGateway : IOrderGateway
    {
        private readonly Dictionary<string, Account> accounts;
        private readonly Dictionary<Order, string> keys = new Dictionary<Order, string>(new ReferenceComparer<Order>());
        private readonly Dictionary<string, Order> orders = new Dictionary<string, Order>();
        private readonly object identityGate = new object();
        public NinjaGateway(IEnumerable<AccountChoice> choices)
        {
            accounts = choices.ToDictionary(c => c.Key, c => c.Account);
        }

        public bool Connected(string account)
        {
            return accounts[account].ConnectionStatus == ConnectionStatus.Connected;
        }

        public string Key(Order order)
        {
            lock (identityGate)
            {
                string key;
                if (!keys.TryGetValue(order, out key))
                {
                    key = Guid.NewGuid().ToString("N");
                    keys.Add(order, key);
                    orders.Add(key, order);
                }
                return key;
            }
        }

        public OrderSnapshot Snapshot(OrderEventArgs e)
        {
            CopyState state;
            if (!Enum.TryParse(e.OrderState.ToString(), out state)) state = CopyState.Unknown;
            return new OrderSnapshot
            {
                Key = Key(e.Order), Instrument = e.Order.Instrument.FullName,
                Action = e.Order.OrderAction.ToString(), Type = e.Order.OrderType.ToString(),
                TimeInForce = e.Order.TimeInForce.ToString(), Gtd = e.Order.Gtd,
                Oco = e.Order.Oco, Manual = e.Order.OrderEntry == OrderEntry.Manual,
                Quantity = e.Quantity, Filled = e.Filled, LimitPrice = e.LimitPrice,
                StopPrice = e.StopPrice, State = state,
                Error = e.Error != ErrorCode.NoError ? e.Error + ": " + e.Comment
                    : e.Order.IsSimulatedStop ? "Simulated stops are not supported in this prototype."
                    : e.Order.CustomOrder != null ? "Custom orders are not supported in this prototype." : null
            };
        }

        public string Create(string account, OrderSnapshot source, string oco)
        {
            Order order = accounts[account].CreateOrder(Instrument.GetInstrument(source.Instrument),
                (OrderAction)Enum.Parse(typeof(OrderAction), source.Action),
                (OrderType)Enum.Parse(typeof(OrderType), source.Type), OrderEntry.Automated,
                (TimeInForce)Enum.Parse(typeof(TimeInForce), source.TimeInForce), source.Quantity,
                source.LimitPrice, source.StopPrice, oco,
                "AR_" + Guid.NewGuid().ToString("N"), source.Gtd, null);
            return Key(order);
        }

        private Order Find(string account, string key)
        {
            lock (identityGate)
            {
                Order order = orders[key];
                if (!Object.ReferenceEquals(order.Account, accounts[account]))
                    throw new InvalidOperationException("The order does not belong to this account.");
                return order;
            }
        }

        public void Submit(string account, string key)
        {
            accounts[account].Submit(new[] { Find(account, key) });
        }
        public void Change(string account, string key, OrderSnapshot desired)
        {
            Order order = Find(account, key);
            order.QuantityChanged = desired.Quantity;
            order.LimitPriceChanged = desired.LimitPrice;
            order.StopPriceChanged = desired.StopPrice;
            accounts[account].Change(new[] { order });
        }
        public void Cancel(string account, string key)
        {
            accounts[account].Cancel(new[] { Find(account, key) });
        }
    }

    internal sealed class AscentReplicatorWindow : NTWindow
    {
        private readonly ComboBox leaderBox = new ComboBox();
        private readonly ListBox followerBox = new ListBox { SelectionMode = SelectionMode.Multiple, MinHeight = 130 };
        private readonly CheckBox simOnly = new CheckBox { Content = "Local simulation accounts only", IsChecked = true };
        private readonly Button refresh = new Button { Content = "Refresh accounts" };
        private readonly Button start = new Button { Content = "Start 1:1 copying" };
        private readonly Button stop = new Button { Content = "Stop copying", IsEnabled = false };
        private readonly TextBlock status = new TextBlock { Text = "STOPPED", FontWeight = FontWeights.Bold };
        private readonly TextBox log = new TextBox { IsReadOnly = true, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 140 };
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private List<AccountChoice> choices = new List<AccountChoice>();
        private AccountChoice leader;
        private List<AccountChoice> followers;
        private volatile NinjaGateway gateway;
        private CopierCore core;
        private volatile int generation;
        private bool subscribed;
        private bool shuttingDown;
        private string logPath;

        public AscentReplicatorWindow()
        {
            Caption = "Ascent Replicator " + ProductInfo.Version + " | prototype";
            Width = 730;
            Height = 720;
            MinWidth = 540;
            MinHeight = 620;
            var layout = new Grid { Margin = new Thickness(18) };
            for (int i = 0; i < 11; i++) layout.RowDefinitions.Add(new RowDefinition
                { Height = i == 9 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Put(layout, new TextBlock { Text = "Manual 1:1 copying", FontSize = 24, FontWeight = FontWeights.Bold }, 0);
            Put(layout, new TextBlock { Text = "Same contract, quantity, and prices. Start with all accounts flat and no active orders.",
                TextWrapping = TextWrapping.Wrap }, 1);
            Put(layout, new TextBlock { Text = "Leader account" }, 2);
            Put(layout, leaderBox, 3);
            Put(layout, new TextBlock { Text = "Follower accounts (click to select multiple accounts)" }, 4);
            Put(layout, followerBox, 5);
            Put(layout, simOnly, 6);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (Button button in new[] { refresh, start, stop })
            {
                button.Margin = new Thickness(0, 0, 12, 0);
                button.Padding = new Thickness(12, 7, 12, 7);
                actions.Children.Add(button);
            }
            Put(layout, actions, 7);
            Put(layout, status, 8);
            Put(layout, log, 9);
            Put(layout, new TextBlock { Text = "Stopping or closing leaves existing orders and positions under your control. Errors suspend all further order requests.",
                TextWrapping = TextWrapping.Wrap }, 10);
            Content = layout;
            refresh.Click += (s, e) => RefreshAccounts();
            start.Click += (s, e) => StartCopying();
            stop.Click += (s, e) => StopByUser();
            timer.Tick += (s, e) => Audit();
            Closing += (s, e) =>
            {
                if (!shuttingDown && subscribed && HasExposure() && MessageBox.Show(this,
                    "There are open positions or active orders. Closing stops copying changes, stops, and exits. "
                    + "You will need to manage each account manually. Close this window?", "Stop replication",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                { e.Cancel = true; return; }
                Detach();
            };
            RefreshAccounts();
        }

        private static void Put(Grid grid, FrameworkElement child, int row)
        {
            child.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(child, row);
            grid.Children.Add(child);
        }
        private static bool LocalSim(Account account)
        {
            return account.Provider == Provider.Simulator || account.Provider == Provider.Playback;
        }
        private void RefreshAccounts()
        {
            lock (Account.All)
                choices = Account.All.Where(a => a.ConnectionStatus == ConnectionStatus.Connected)
                    .Select(a => new AccountChoice { Account = a, Key = Guid.NewGuid().ToString("N") }).ToList();
            leaderBox.ItemsSource = choices;
            followerBox.ItemsSource = choices;
            Report(choices.Count + " connected accounts. Select a leader and followers.");
        }
        private static bool Exposed(Account account)
        {
            lock (account.Positions)
                if (account.Positions.Any(p => p.MarketPosition != MarketPosition.Flat || p.Quantity != 0)) return true;
            lock (account.Orders)
                return account.Orders.Any(o => o.OrderState != OrderState.Filled
                    && o.OrderState != OrderState.Cancelled && o.OrderState != OrderState.Rejected);
        }
        private bool HasExposure()
        {
            return leader != null && new[] { leader }.Concat(followers ?? new List<AccountChoice>())
                .Any(c => Exposed(c.Account));
        }
        private void Validate(IEnumerable<AccountChoice> selected)
        {
            foreach (AccountChoice choice in selected)
            {
                if (choice.Account.ConnectionStatus != ConnectionStatus.Connected)
                    throw new InvalidOperationException("Account disconnected: " + choice.Account.Name);
                if (choice.Account.AccountStatus != AccountStatus.Enabled)
                    throw new InvalidOperationException("Account is not enabled: " + choice.Account.Name);
                if (simOnly.IsChecked == true && !LocalSim(choice.Account))
                    throw new InvalidOperationException("Local simulation mode requires Sim or Playback accounts.");
                if (Exposed(choice.Account))
                    throw new InvalidOperationException("The account must be flat with no active orders: " + choice.Account.Name);
            }
        }
        private void StartCopying()
        {
            if (subscribed) return;
            try
            {
                leader = leaderBox.SelectedItem as AccountChoice;
                followers = followerBox.SelectedItems.Cast<AccountChoice>().ToList();
                if (leader == null || followers.Count == 0)
                    throw new InvalidOperationException("Select a leader and at least one follower account.");
                if (followers.Contains(leader))
                    throw new InvalidOperationException("The leader cannot also be a follower.");
                var selected = new[] { leader }.Concat(followers).ToArray();
                Validate(selected);
                gateway = new NinjaGateway(selected);
                core = new CopierCore(gateway, followers.Select(c => c.Key), Report, () => DateTime.UtcNow);
                // A second validation after subscriptions closes the startup observation gap.
                generation++;
                subscribed = true;
                foreach (AccountChoice choice in selected) choice.Account.OrderUpdate += OnOrder;
                Connection.ConnectionStatusUpdate += OnConnection;
                Validate(selected);
                string directory = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "log", "AscentReplicator");
                Directory.CreateDirectory(directory);
                logPath = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
                EnableSelectors(false);
                status.Text = "ACTIVE | 1:1 | " + leader.Account.Name + " -> " + followers.Count + " accounts";
                status.Foreground = Brushes.ForestGreen;
                timer.Start();
                Report("Started 1:1 copying. Leader: " + leader.Account.Name + "; followers: "
                    + String.Join(", ", followers.Select(c => c.Account.Name)));
            }
            catch (Exception ex)
            {
                Detach();
                Report("Could not start copying: " + ex.Message);
            }
        }
        private void OnOrder(object sender, OrderEventArgs e)
        {
            // Capture the session and immutable event values on the callback thread.
            // Generation prevents queued events from a previous session entering a new one.
            NinjaGateway currentGateway = gateway;
            int currentGeneration = generation;
            if (currentGateway == null) return;
            OrderSnapshot snapshot;
            try { snapshot = currentGateway.Snapshot(e); }
            catch (Exception ex)
            {
                string reason = ex.Message;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (subscribed && generation == currentGeneration)
                        core.Fault("Could not read an order update: " + reason);
                }));
                return;
            }
            Account account = e.Order.Account;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!subscribed || generation != currentGeneration) return;
                if (Object.ReferenceEquals(account, leader.Account)) core.OnSource(snapshot);
                else
                {
                    AccountChoice choice = followers.FirstOrDefault(c => Object.ReferenceEquals(c.Account, account));
                    if (choice != null) core.OnFollower(choice.Key, snapshot);
                }
                ShowFault();
            }));
        }
        private void OnConnection(object sender, ConnectionStatusEventArgs e)
        {
            Connection connection = e.Connection;
            ConnectionStatus connectionStatus = e.Status;
            int currentGeneration = generation;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!subscribed || generation != currentGeneration) return;
                if (connectionStatus != ConnectionStatus.Connected && new[] { leader }.Concat(followers)
                    .Any(c => Object.ReferenceEquals(c.Account.Connection, connection)))
                    core.Fault("A connection used by the replicator was lost.");
                ShowFault();
            }));
        }
        private void Audit()
        {
            if (!subscribed) return;
            if (leader.Account.ConnectionStatus != ConnectionStatus.Connected)
                core.Fault("The leader account disconnected.");
            core.Tick();
            ShowFault();
        }
        private void ShowFault()
        {
            if (core == null || !core.Faulted) return;
            status.Text = "ERROR | COPYING STOPPED | " + core.FaultReason;
            status.Foreground = Brushes.OrangeRed;
            timer.Stop();
        }
        private void EnableSelectors(bool enabled)
        {
            leaderBox.IsEnabled = enabled;
            followerBox.IsEnabled = enabled;
            simOnly.IsEnabled = enabled;
            refresh.IsEnabled = enabled;
            start.IsEnabled = enabled;
            stop.IsEnabled = !enabled;
        }
        private void StopByUser()
        {
            if (HasExposure() && MessageBox.Show(this,
                "There are open positions or active orders. Stopping ends replication of changes and exits. "
                + "You will need to manage each account manually. Stop copying?", "Stop replication",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Detach();
            Report("Copying stopped by the user. Manage existing orders and positions in NinjaTrader.");
        }
        private void Detach()
        {
            generation++;
            if (subscribed)
            {
                subscribed = false;
                foreach (AccountChoice choice in new[] { leader }.Concat(followers))
                    choice.Account.OrderUpdate -= OnOrder;
                Connection.ConnectionStatusUpdate -= OnConnection;
            }
            timer.Stop();
            core = null;
            gateway = null;
            EnableSelectors(true);
            status.Text = "STOPPED";
            status.Foreground = Brushes.Gray;
        }
        public void Shutdown()
        {
            shuttingDown = true;
            Detach();
            Close();
        }
        private void Report(string message)
        {
            string line = DateTime.UtcNow.ToString("O") + " " + message;
            if (log.Text.Length > 60000) log.Text = log.Text.Substring(log.Text.Length - 30000);
            log.AppendText(line + Environment.NewLine);
            log.ScrollToEnd();
            if (logPath == null) return;
            try { File.AppendAllText(logPath, line + Environment.NewLine); }
            catch (Exception ex) { log.AppendText("Could not save the log: " + ex.Message + Environment.NewLine); }
        }
    }
}
