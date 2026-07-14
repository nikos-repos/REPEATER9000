#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

//This namespace holds Add ons in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.AddOns
{
    #region Data Models

    [Serializable]
    public class FollowerAccountData : INotifyPropertyChanged
    {
        private string _accountName;
        private string _groupName;
        private string _status;

        public string AccountName
        {
            get { return _accountName; }
            set { _accountName = value; OnPropertyChanged("AccountName"); }
        }

        public string GroupName
        {
            get { return string.IsNullOrEmpty(_groupName) || _groupName == "—" ? "Ungrouped" : _groupName; }
            set { _groupName = value; OnPropertyChanged("GroupName"); OnPropertyChanged("HasGroup"); }
        }

        public bool HasGroup
        {
            get { return GroupName != "Ungrouped"; }
        }

        public string Status
        {
            get { return string.IsNullOrEmpty(_status) ? "Disabled" : _status; }
            set { _status = value; OnPropertyChanged("Status"); }
        }

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }

        public FollowerAccountData()
        {
            _status = "Copying Disabled";
            _groupName = "Ungrouped";
        }

        public FollowerAccountData(string accountName) : this()
        {
            _accountName = accountName;
        }
    }

    [Serializable]
    public class FollowerGroupData : INotifyPropertyChanged
    {
        private string _groupName;
        private string _leaderAccountName;

        public string GroupName
        {
            get { return _groupName; }
            set { _groupName = value; OnPropertyChanged("GroupName"); }
        }

        public string LeaderAccountName
        {
            get { return _leaderAccountName; }
            set { _leaderAccountName = value; OnPropertyChanged("LeaderAccountName"); }
        }

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }

        public FollowerGroupData() { }

        public FollowerGroupData(string groupName) : this()
        {
            _groupName = groupName;
        }
    }

    [Serializable]
    public class Repeater9000Settings
    {
        public bool LatencyProbeEnabled { get; set; }
        public List<FollowerAccountData> Followers { get; set; }
        public List<FollowerGroupData> Groups { get; set; }

        public Repeater9000Settings()
        {
            LatencyProbeEnabled = false;
            Followers = new List<FollowerAccountData>();
            Groups = new List<FollowerGroupData>();
        }
    }

    #endregion

    public class Repeater9000Addon : AddOnBase
    {
        private NTMenuItem _menuItem;
        private NTMenuItem _parentMenuItem;
        private Repeater9000Window _window;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "REPEATER9000 - Group-based trade copier";
                Name = "REPEATER9000";
            }
            else if (State == State.Terminated)
            {
                Repeater9000Window window = _window;
                if (window != null)
                    window.Dispatcher.InvokeAsync(() => window.Shutdown());
                RemoveMenuItem();
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter controlCenter = window as ControlCenter;
            if (controlCenter == null)
                return;

            AddMenuItem(controlCenter);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            ControlCenter controlCenter = window as ControlCenter;
            if (controlCenter == null)
                return;

            RemoveMenuItem();
        }

        private void AddMenuItem(ControlCenter controlCenter)
        {
            _parentMenuItem = controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            
            if (_parentMenuItem == null)
            {
                foreach (object item in controlCenter.MainMenu)
                {
                    NTMenuItem menuItem = item as NTMenuItem;
                    if (menuItem != null && menuItem.Header != null && menuItem.Header.ToString() == "New")
                    {
                        _parentMenuItem = menuItem;
                        break;
                    }
                }
            }

            if (_parentMenuItem != null && _menuItem == null)
            {
                _menuItem = new NTMenuItem
                {
                    Header = "REPEATER9000",
                    Style = Application.Current.TryFindResource("MainMenuItem") as Style
                };
                _menuItem.Click += OnMenuItemClick;
                _parentMenuItem.Items.Add(_menuItem);
            }
        }

        private void RemoveMenuItem()
        {
            if (_menuItem != null && _parentMenuItem != null)
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_parentMenuItem.Items.Contains(_menuItem))
                    {
                        _parentMenuItem.Items.Remove(_menuItem);
                    }
                });
            }
            _menuItem = null;
            _parentMenuItem = null;
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                if (_window == null)
                {
                    _window = new Repeater9000Window();
                    _window.Closed += (s, args) => _window = null;
                }

                if (!_window.IsVisible)
                    _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            });
        }
    }

    public class Repeater9000Window : Window
    {
        #region Fields

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "Repeater9000Settings.xml");
        private static readonly string LatencyProbeFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "Repeater9000LatencyProbe.csv");
        private static readonly object LatencyProbeFileLock = new object();
        private const string LatencyProbeHeader = "RecordedUtc,OrderId,Follower,OrderState,MasterExecutionReceivedUtc,OrderCreatedUtc,BeforeSubmitUtc,AfterSubmitUtc,FollowerOrderUpdateUtc,MasterExecutionReceivedTicks,OrderCreatedTicks,BeforeSubmitTicks,AfterSubmitTicks,FollowerOrderUpdateTicks,OrderCreateDelayMs,SubmitDelayMs,SubmitCallMs,OrderUpdateDelayMs,FillDelayMs";

        private ObservableCollection<FollowerAccountData> _followers;
        private ObservableCollection<FollowerGroupData> _groups;
        private Dictionary<string, Account> _accountCache;
        private ConcurrentDictionary<long, PendingOrderInfo> _pendingOrders;
        private ConcurrentDictionary<string, SubmitQueue> _submitQueues;

        // _copiedLeaderOrders only deduplicates the initial copy of a master order.
        // It never suppresses later change/cancel/fill mirroring - that flows
        // through the link maps below.
        private ConcurrentDictionary<long, byte> _copiedLeaderOrders;

        // Master-to-follower order mirroring state. All maps are touched from
        // account event threads and submit workers, so they are concurrent.
        private ConcurrentDictionary<string, OrderLink> _linksByMasterFollower;    // LinkKey(masterOrderId, followerName) -> link
        private ConcurrentDictionary<long, OrderLink> _linksByFollowerOrderId;     // follower order id -> link
        private ConcurrentDictionary<long, List<OrderLink>> _linksByMasterOrderId; // master order id -> all follower links
        private ConcurrentDictionary<string, string> _followerOcoByMasterOco;      // masterOco|followerName -> follower OCO id
        private ConcurrentDictionary<string, List<OrderLink>> _followerOcoGroups;  // follower OCO id -> member links
        private ConcurrentDictionary<string, byte> _masterOcoFillSeen;             // master OCO ids that had a member fill
        private ConcurrentDictionary<string, int> _activeEntryLinks;               // followerName|instrument -> live copied entry count
        private ConcurrentDictionary<string, long> _recentEntryFills;              // followerName|instrument -> last entry fill ticks

        private volatile Dictionary<string, FollowerCopyRoute[]> _leaderRoutesByName;
        private volatile Dictionary<string, FollowerAccountData> _followersByName;

        // UI Elements
        private Button _enableCopyingButton;
        private CheckBox _latencyProbeCheckBox;
        private DataGrid _followerDataGrid;
        private DataGrid _groupDataGrid;
        private TextBlock _selectionTextBlock;
        private TextBlock _statusTextBlock;

        private bool _isCopyingEnabled;
        private bool _isLatencyProbeEnabled;
        private bool _isLoadingSettings;
        private volatile bool _isDisposed;
        private bool _isShuttingDown;
        private readonly object _submissionGate = new object();

        private struct PendingOrderInfo
        {
            public readonly string FollowerName;
            public readonly long MasterReceivedTimestamp;
            public readonly long OrderCreatedTimestamp;
            public readonly long BeforeSubmitTimestamp;
            public readonly long AfterSubmitTimestamp;
            public readonly DateTime MasterReceivedUtc;

            public PendingOrderInfo(string followerName, long masterReceivedTimestamp,
                long orderCreatedTimestamp, long beforeSubmitTimestamp, long afterSubmitTimestamp,
                DateTime masterReceivedUtc)
            {
                FollowerName = followerName;
                MasterReceivedTimestamp = masterReceivedTimestamp;
                OrderCreatedTimestamp = orderCreatedTimestamp;
                BeforeSubmitTimestamp = beforeSubmitTimestamp;
                AfterSubmitTimestamp = afterSubmitTimestamp;
                MasterReceivedUtc = masterReceivedUtc;
            }
        }

        private sealed class FollowerCopyRoute
        {
            public readonly FollowerAccountData Follower;
            public readonly Account Account;

            public FollowerCopyRoute(FollowerAccountData follower, Account account)
            {
                Follower = follower;
                Account = account;
            }
        }

        // One mapped follower order per master order per follower account. The
        // master ATM is the source of truth; the link carries the latest master
        // state (Target*) that the follower order must converge to.
        private sealed class OrderLink
        {
            public readonly object Gate = new object();
            public readonly Account MasterAccount;
            public readonly long MasterOrderId;
            public readonly string MasterOco;
            public readonly FollowerAccountData Follower;
            public readonly Account FollowerAccount;
            public readonly Instrument Instrument;
            public readonly OrderAction OrderAction;
            public readonly OrderType OrderType;
            public readonly TimeInForce TimeInForce;
            public readonly string FollowerOco;
            public readonly bool IsExit;

            public Order FollowerOrder;          // assigned once by the submit worker (under Gate)
            public volatile bool IsSubmitted;
            public volatile bool CancelRequested;
            public volatile bool IsTerminal;     // follower order reached a terminal state
            public volatile bool IsFilled;       // follower order fully filled
            public volatile bool MasterTerminal; // master order filled/cancelled/rejected
            public int SyncQueued;

            public double TargetLimitPrice;      // master-side values, guarded by Gate
            public double TargetStopPrice;
            public int TargetQuantity;           // follower-scaled quantity, guarded by Gate

            public OrderLink(Account masterAccount, long masterOrderId, string masterOco,
                FollowerCopyRoute route, Instrument instrument, OrderAction orderAction,
                OrderType orderType, TimeInForce timeInForce, string followerOco, bool isExit,
                int quantity, double limitPrice, double stopPrice)
            {
                MasterAccount = masterAccount;
                MasterOrderId = masterOrderId;
                MasterOco = masterOco;
                Follower = route.Follower;
                FollowerAccount = route.Account;
                Instrument = instrument;
                OrderAction = orderAction;
                OrderType = orderType;
                TimeInForce = timeInForce;
                FollowerOco = followerOco;
                IsExit = isExit;
                TargetQuantity = quantity;
                TargetLimitPrice = limitPrice;
                TargetStopPrice = stopPrice;
            }
        }

        private sealed class CopyRequest
        {
            public readonly OrderLink Link;
            public readonly bool IsCreate;
            public readonly long MasterReceivedTicks;
            public readonly DateTime MasterReceivedUtc;

            public CopyRequest(OrderLink link, long masterReceivedTicks,
                DateTime masterReceivedUtc)
            {
                Link = link;
                IsCreate = true;
                MasterReceivedTicks = masterReceivedTicks;
                MasterReceivedUtc = masterReceivedUtc;
            }

            public CopyRequest(OrderLink link)
            {
                Link = link;
                IsCreate = false;
            }
        }

        private sealed class SubmitQueue
        {
            public readonly ConcurrentQueue<CopyRequest> Requests = new ConcurrentQueue<CopyRequest>();
            public readonly ManualResetEventSlim Signal = new ManualResetEventSlim(false);
            public volatile bool IsCompleted;
        }

        #endregion

        #region Constructor

        public Repeater9000Window()
        {
            Title = "REPEATER9000 - Advanced Trade Copier";
            Width = 1500;
            Height = 750;
            MinWidth = 1200;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Black;

            _followers = new ObservableCollection<FollowerAccountData>();
            _groups = new ObservableCollection<FollowerGroupData>();
            _accountCache = new Dictionary<string, Account>(StringComparer.Ordinal);
            _pendingOrders = new ConcurrentDictionary<long, PendingOrderInfo>();
            _submitQueues = new ConcurrentDictionary<string, SubmitQueue>(StringComparer.Ordinal);
            _copiedLeaderOrders = new ConcurrentDictionary<long, byte>();
            _linksByMasterFollower = new ConcurrentDictionary<string, OrderLink>(StringComparer.Ordinal);
            _linksByFollowerOrderId = new ConcurrentDictionary<long, OrderLink>();
            _linksByMasterOrderId = new ConcurrentDictionary<long, List<OrderLink>>();
            _followerOcoByMasterOco = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            _followerOcoGroups = new ConcurrentDictionary<string, List<OrderLink>>(StringComparer.Ordinal);
            _masterOcoFillSeen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            _activeEntryLinks = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            _recentEntryFills = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            _leaderRoutesByName = new Dictionary<string, FollowerCopyRoute[]>(StringComparer.Ordinal);
            _followersByName = new Dictionary<string, FollowerAccountData>(StringComparer.Ordinal);

            InitializeUI();
            LoadSettings();
            CacheAccounts();
            RebuildFollowerMap();
            if (_isLatencyProbeEnabled)
            {
                EnsureLatencyProbeFile();
                UpdateStatus("Latency probe on");
            }
            else
            {
                UpdateStatus("Latency probe off");
            }
        }

        #endregion

        #region UI Initialization

        private void InitializeUI()
        {
            Grid page = new Grid { Margin = new Thickness(28, 20, 28, 16) };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            page.Children.Add(new TextBlock
            {
                Text = "REPEATER9000",
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _enableCopyingButton = ActionButton("COPYING DISABLED", new SolidColorBrush(Color.FromRgb(145, 48, 48)));
            _enableCopyingButton.FontSize = 18;
            _enableCopyingButton.FontWeight = FontWeights.Bold;
            _enableCopyingButton.Height = 54;
            _enableCopyingButton.Width = 330;
            _enableCopyingButton.HorizontalAlignment = HorizontalAlignment.Center;
            _enableCopyingButton.Click += OnCopyingButtonClick;
            Grid.SetRow(_enableCopyingButton, 1);
            page.Children.Add(_enableCopyingButton);

            Grid columns = new Grid { MaxWidth = 1300, Margin = new Thickness(0, 22, 0, 18), HorizontalAlignment = HorizontalAlignment.Center };
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(columns, 2);
            page.Children.Add(columns);

            Border followers = CreateFollowerSection();
            Grid.SetColumn(followers, 0);
            columns.Children.Add(followers);
            Border groups = CreateGroupManagerSection();
            Grid.SetColumn(groups, 2);
            columns.Children.Add(groups);

            StackPanel advanced = new StackPanel { Orientation = Orientation.Horizontal, MaxWidth = 1300, HorizontalAlignment = HorizontalAlignment.Center };
            _latencyProbeCheckBox = new CheckBox
            {
                Content = "Latency Probe",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 40, 0)
            };
            _latencyProbeCheckBox.Checked += (s, e) => { _isLatencyProbeEnabled = true; EnsureLatencyProbeFile(); if (!_isLoadingSettings) SaveSettings(); };
            _latencyProbeCheckBox.Unchecked += (s, e) => { _isLatencyProbeEnabled = false; _pendingOrders.Clear(); if (!_isLoadingSettings) SaveSettings(); };
            advanced.Children.Add(_latencyProbeCheckBox);
            Button flatten = ActionButton("Flatten ALL", new SolidColorBrush(Color.FromRgb(175, 70, 35)));
            flatten.Click += OnFlattenAllClick;
            advanced.Children.Add(flatten);
            Grid.SetRow(advanced, 3);
            page.Children.Add(advanced);

            _statusTextBlock = new TextBlock { Foreground = Brushes.LightGray, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(_statusTextBlock, 4);
            page.Children.Add(_statusTextBlock);

            Content = page;
        }

        private Border CreateFollowerSection()
        {
            _followerDataGrid = CreateGrid(_followers, DataGridSelectionMode.Extended);
            _followerDataGrid.Columns.Add(new DataGridTextColumn { Header = "Account Name", Binding = new Binding("AccountName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
            _followerDataGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(100), IsReadOnly = true });
            _followerDataGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(125), IsReadOnly = true });
            _followerDataGrid.SelectionChanged += (s, e) => UpdateSelectionText();

            _selectionTextBlock = new TextBlock { Foreground = Brushes.LightSkyBlue, Text = "Selected: none", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            StackPanel body = new StackPanel();
            body.Children.Add(_followerDataGrid);
            body.Children.Add(_selectionTextBlock);
            body.Children.Add(ButtonRow(
                new Button[] {
                    Wire(ActionButton("Add Follower", new SolidColorBrush(Color.FromRgb(44, 112, 77))), OnAddFollowerClick),
                    Wire(ActionButton("Remove Follower", new SolidColorBrush(Color.FromRgb(145, 48, 48))), OnRemoveFollowerClick),
                    Wire(ActionButton("Add to Group", new SolidColorBrush(Color.FromRgb(48, 94, 145))), OnAddToGroupClick)
                }));
            return Section("Follower Accounts", body);
        }

        private Border CreateGroupManagerSection()
        {
            _groupDataGrid = CreateGrid(_groups, DataGridSelectionMode.Single);
            _groupDataGrid.Columns.Add(new DataGridTextColumn { Header = "Group Name", Binding = new Binding("GroupName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
            _groupDataGrid.Columns.Add(new DataGridTextColumn { Header = "Leader Account", Binding = new Binding("LeaderAccountName"), Width = new DataGridLength(150), IsReadOnly = true });

            StackPanel body = new StackPanel();
            body.Children.Add(_groupDataGrid);
            body.Children.Add(new TextBlock { Text = "Select a group to edit or remove it.", Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 8) });
            body.Children.Add(ButtonRow(
                new Button[] {
                    Wire(ActionButton("Create Group", new SolidColorBrush(Color.FromRgb(44, 112, 77))), OnCreateGroupClick),
                    Wire(ActionButton("Edit Group", new SolidColorBrush(Color.FromRgb(48, 94, 145))), OnEditGroupClick),
                    Wire(ActionButton("Remove Group", new SolidColorBrush(Color.FromRgb(145, 48, 48))), OnDeleteGroupButtonClick)
                }));
            return Section("Group Management", body);
        }

        private DataGrid CreateGrid(System.Collections.IEnumerable source, DataGridSelectionMode selectionMode)
        {
            Style headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new SolidColorBrush(Color.FromRgb(31, 41, 55))));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, Brushes.White));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(76, 84, 96))));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(10, 8, 10, 8)));
            Style rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(DataGridRow.ForegroundProperty, Brushes.White));
            rowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(42, 46, 54))));
            Trigger selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(38, 110, 165))));
            selected.Setters.Add(new Setter(DataGridRow.ForegroundProperty, Brushes.White));
            rowStyle.Triggers.Add(selected);
            return new DataGrid
            {
                ItemsSource = source,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = true,
                SelectionMode = selectionMode,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                RowStyle = rowStyle,
                ColumnHeaderStyle = headerStyle,
                Background = new SolidColorBrush(Color.FromRgb(30, 33, 39)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(76, 84, 96)),
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(66, 72, 82)),
                MinHeight = 270
            };
        }

        private static Button ActionButton(string text, Brush background)
        {
            return new Button { Content = text, Background = background, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        }

        private static Button Wire(Button button, RoutedEventHandler handler)
        {
            button.Click += handler;
            return button;
        }

        private static WrapPanel ButtonRow(IEnumerable<Button> buttons)
        {
            WrapPanel row = new WrapPanel();
            foreach (Button button in buttons) row.Children.Add(button);
            return row;
        }

        private static Border Section(string title, UIElement content)
        {
            StackPanel panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(content);
            return new Border { Child = panel, Padding = new Thickness(16), BorderBrush = new SolidColorBrush(Color.FromRgb(76, 84, 96)), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(35, 38, 45)) };
        }

        private void OnCopyingButtonClick(object sender, RoutedEventArgs e)
        {
            _isCopyingEnabled = !_isCopyingEnabled;
            UpdateAllStatuses();
            SaveSettings();
        }

        private void UpdateSelectionText()
        {
            List<string> names = new List<string>();
            foreach (object item in _followerDataGrid.SelectedItems)
            {
                FollowerAccountData follower = item as FollowerAccountData;
                if (follower != null) names.Add(follower.AccountName);
            }
            _selectionTextBlock.Text = names.Count == 0 ? "Selected: none" : "Selected (" + names.Count + "): " + string.Join(", ", names);
        }

        #endregion


        #region Account Management

        private void CacheAccounts()
        {
            UnsubscribeFromAccountEvents();
            _accountCache.Clear();
            lock (Account.All)
            {
                foreach (Account account in Account.All)
                {
                    if (account.Connection != null && account.Connection.Status == ConnectionStatus.Connected)
                    {
                        _accountCache[account.Name] = account;
                    }
                }
            }
        }

        private void RefreshAccountCache()
        {
            CacheAccounts();
            RebuildFollowerMap();
        }

        private List<string> GetNonFollowerAccounts()
        {
            HashSet<string> followerNames = new HashSet<string>();
            foreach (FollowerAccountData f in _followers)
            {
                followerNames.Add(f.AccountName);
            }

            List<string> result = new List<string>();
            foreach (string name in _accountCache.Keys)
            {
                if (!followerNames.Contains(name))
                    result.Add(name);
            }
            return result;
        }

        private List<string> GetAvailableFollowerAccounts()
        {
            HashSet<string> existingFollowers = new HashSet<string>();
            foreach (FollowerAccountData f in _followers)
            {
                existingFollowers.Add(f.AccountName);
            }

            HashSet<string> leaderAccounts = new HashSet<string>();
            foreach (FollowerGroupData group in _groups)
            {
                if (!string.IsNullOrEmpty(group.LeaderAccountName))
                    leaderAccounts.Add(group.LeaderAccountName);
            }

            List<string> result = new List<string>();
            foreach (string name in _accountCache.Keys)
            {
                if (!existingFollowers.Contains(name) && !leaderAccounts.Contains(name))
                    result.Add(name);
            }
            return result;
        }

        private bool IsAccountFollower(string accountName)
        {
            foreach (FollowerAccountData f in _followers)
            {
                if (f.AccountName == accountName)
                    return true;
            }
            return false;
        }

        private void RefreshAccountEventSubscriptions()
        {
            UnsubscribeFromAccountEvents();

            foreach (KeyValuePair<string, Account> pair in _accountCache)
            {
                if (_leaderRoutesByName.ContainsKey(pair.Key) || _followersByName.ContainsKey(pair.Key))
                    pair.Value.OrderUpdate += OnOrderUpdate;
            }
        }

        private void UnsubscribeFromAccountEvents()
        {
            foreach (Account account in _accountCache.Values)
            {
                try
                {
                    account.OrderUpdate -= OnOrderUpdate;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("REPEATER9000 unsubscribe failed: " + ex);
                }
            }
        }

        #endregion

        #region Event Handlers - Order Updates

        private void CopyLeaderOrderToFollowers(Account account, Order order,
            long masterReceivedTicks, DateTime masterReceivedUtc)
        {
            if (!_isCopyingEnabled || _isDisposed) return;
            if (!_copiedLeaderOrders.TryAdd(order.Id, 0)) return;

            FollowerCopyRoute[] routes;
            if (!_leaderRoutesByName.TryGetValue(account.Name, out routes) || routes.Length == 0)
                return;

            Instrument instrument = order.Instrument;
            bool isExit = IsExitOrder(account, order);
            string masterOco = order.Oco ?? string.Empty;
            double limitPrice = order.LimitPrice;
            double stopPrice = order.StopPrice;
            int leaderQuantity = order.Quantity;

            // Pass 1: build and register every link before any submit can run,
            // so master change/cancel events always find the mapping.
            List<OrderLink> links = new List<OrderLink>();
            for (int i = 0; i < routes.Length; i++)
            {
                try
                {
                    FollowerCopyRoute route = routes[i];

                    // Exits are copied whenever the follower has copied exposure to protect.
                    bool copyable = !isExit || CanCopyExitToFollower(route, instrument);
                    if (!copyable)
                        continue;

                    int quantity = leaderQuantity;
                    if (quantity <= 0)
                        continue;

                    string followerName = route.Follower.AccountName;
                    string linkKey = LinkKey(order.Id, followerName);
                    if (_linksByMasterFollower.ContainsKey(linkKey))
                        continue;

                    // One follower OCO id per master OCO group per follower.
                    // Sibling stop/target copies share it, so the venue's own
                    // OCO handling protects the bracket even if the copier lags.
                    string followerOco = string.Empty;
                    if (masterOco.Length > 0)
                    {
                        followerOco = _followerOcoByMasterOco.GetOrAdd(
                            masterOco + "|" + followerName,
                            k => "RPT9K" + Guid.NewGuid().ToString("N"));
                    }

                    OrderLink link = new OrderLink(account, order.Id, masterOco, route,
                        instrument, order.OrderAction, order.OrderType, order.TimeInForce,
                        followerOco, isExit, quantity, limitPrice, stopPrice);

                    _linksByMasterFollower[linkKey] = link;
                    links.Add(link);

                    if (followerOco.Length > 0)
                    {
                        List<OrderLink> group = _followerOcoGroups.GetOrAdd(followerOco,
                            k => new List<OrderLink>());
                        lock (group)
                            group.Add(link);
                    }

                    if (!isExit)
                        AddActiveEntryLinks(followerName, instrument, 1);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("REPEATER9000 route copy failed: " + ex);
                }
            }

            if (links.Count == 0)
                return;

            _linksByMasterOrderId[order.Id] = links;

            // Pass 2: hand off to the per-follower submit workers.
            for (int i = 0; i < links.Count; i++)
            {
                OrderLink link = links[i];
                SubmitQueue queue = EnsureSubmitWorker(link.Follower.AccountName);
                queue.Requests.Enqueue(new CopyRequest(link, masterReceivedTicks, masterReceivedUtc));
                queue.Signal.Set();
            }
        }

        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            long nowTicks = Stopwatch.GetTimestamp();

            if (_isDisposed) return;

            Order order = e.Order;
            if (order == null) return;

            Account account = sender as Account;
            if (account == null) return;

            // Safety-critical mirroring runs first. Latency probe/UI work runs
            // strictly afterwards so it can never delay an order action.
            if (_leaderRoutesByName.ContainsKey(account.Name))
                HandleMasterOrderUpdate(account, order, e, nowTicks);
            else
                HandleFollowerOrderUpdate(order, e);

            UpdateLatencyProbe(order, e.OrderState, nowTicks);
        }

        private void HandleMasterOrderUpdate(Account account, Order order, OrderEventArgs e,
            long masterReceivedTicks)
        {
            OrderState orderState = e.OrderState;
            long masterOrderId = order.Id;

            List<OrderLink> links;
            if (!_linksByMasterOrderId.TryGetValue(masterOrderId, out links))
            {
                if (IsCopyableLeaderSubmission(orderState))
                {
                    if (_isCopyingEnabled && IsCopyableMasterOrder(order))
                        CopyLeaderOrderToFollowers(account, order, masterReceivedTicks, DateTime.UtcNow);
                    return;
                }

                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected ||
                    orderState == OrderState.Filled)
                {
                    byte claimed;
                    _copiedLeaderOrders.TryRemove(masterOrderId, out claimed);

                    if (orderState == OrderState.Filled && !string.IsNullOrEmpty(order.Oco))
                        _masterOcoFillSeen[order.Oco] = 1;

                    // Fail closed: a master exit ended but no mapping exists for it
                    // (e.g. the copier was restarted mid-trade). Cancel matching
                    // unmanaged copied exits instead of guessing a remap.
                    if (orderState != OrderState.Rejected && IsExitOrder(account, order))
                        ReconcileUnmappedMasterExit(account, order);
                }
                return;
            }

            switch (orderState)
            {
                case OrderState.Accepted:
                case OrderState.Working:
                case OrderState.TriggerPending:
                    MirrorMasterOrderChange(order, links);
                    break;

                case OrderState.Filled:
                    HandleMasterOrderFilled(order, links);
                    break;

                case OrderState.Cancelled:
                    HandleMasterOrderCancelled(order, links);
                    break;

                case OrderState.Rejected:
                    CancelFollowerLinks(links);
                    CancelFollowerOcoGroups(links);
                    CleanupMasterOrder(masterOrderId);
                    break;
            }
        }

        // The master ATM moved a stop/target or changed quantity: converge the
        // mapped follower orders via Account.Change. Never recreate the order.
        private void MirrorMasterOrderChange(Order masterOrder, List<OrderLink> links)
        {
            double limitPrice = masterOrder.LimitPrice;
            double stopPrice = masterOrder.StopPrice;
            int masterQuantity = masterOrder.Quantity;

            OrderLink[] snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++)
            {
                OrderLink link = snapshot[i];
                int quantity = masterQuantity;
                bool dirty = false;
                lock (link.Gate)
                {
                    if (link.IsTerminal || link.CancelRequested)
                        continue;
                    if (link.TargetLimitPrice != limitPrice)
                    {
                        link.TargetLimitPrice = limitPrice;
                        dirty = true;
                    }
                    if (link.TargetStopPrice != stopPrice)
                    {
                        link.TargetStopPrice = stopPrice;
                        dirty = true;
                    }
                    if (quantity > 0 && link.TargetQuantity != quantity)
                    {
                        link.TargetQuantity = quantity;
                        dirty = true;
                    }
                }
                if (dirty)
                    EnqueueLinkSync(link);
            }
        }

        private void HandleMasterOrderFilled(Order masterOrder, List<OrderLink> links)
        {
            string masterOco = masterOrder.Oco;
            if (!string.IsNullOrEmpty(masterOco))
                _masterOcoFillSeen[masterOco] = 1;

            // Follower copies of a filled master order stay working on purpose:
            // the follower bracket resolves through its own fills and OCO. A
            // follower stop is never cancelled just because the master filled.
            OrderLink[] snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i].MasterTerminal = true;

            CleanupMasterOrder(masterOrder.Id);
        }

        private void HandleMasterOrderCancelled(Order masterOrder, List<OrderLink> links)
        {
            string masterOco = masterOrder.Oco;
            bool ocoSiblingFilled = !string.IsNullOrEmpty(masterOco) &&
                _masterOcoFillSeen.ContainsKey(masterOco);

            if (!ocoSiblingFilled)
            {
                // Genuine cancel (trader or ATM removed the order): mirror it.
                CancelFollowerLinks(links);
            }
            else
            {
                // The master ATM cancelled this order because its OCO sibling
                // filled. The follower bracket keeps protecting the follower
                // position and resolves via its own OCO; only enforce the cancel
                // where the follower's own sibling already filled.
                OrderLink[] snapshot = SnapshotLinks(links);
                for (int i = 0; i < snapshot.Length; i++)
                {
                    OrderLink link = snapshot[i];
                    link.MasterTerminal = true;
                    if (HasFilledFollowerSibling(link))
                        RequestLinkCancel(link);
                }
            }
            CleanupMasterOrder(masterOrder.Id);
        }

        private void CancelFollowerLinks(List<OrderLink> links)
        {
            OrderLink[] snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].MasterTerminal = true;
                RequestLinkCancel(snapshot[i]);
            }
        }

        // Risk cancels are immediate: they run inline on the event thread, not
        // through the submit queue. If the follower order is still being created
        // the flag makes the worker fail it closed instead.
        private void RequestLinkCancel(OrderLink link)
        {
            Order followerOrder = null;
            lock (link.Gate)
            {
                if (link.IsTerminal)
                    return;
                link.CancelRequested = true;
                if (link.FollowerOrder != null && link.IsSubmitted)
                    followerOrder = link.FollowerOrder;
            }

            if (followerOrder != null)
                TryCancelOrder(link.FollowerAccount, followerOrder);
            else
                EnqueueLinkSync(link);
        }

        private void TryCancelOrder(Account account, Order order)
        {
            try
            {
                lock (_submissionGate)
                {
                    if (!_isDisposed && IsActiveOrderState(order.OrderState))
                        account.Cancel(new Order[] { order });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("REPEATER9000 cancel failed: " + ex);
            }
        }

        private void CleanupMasterOrder(long masterOrderId)
        {
            List<OrderLink> links;
            _linksByMasterOrderId.TryRemove(masterOrderId, out links);
            byte claimed;
            _copiedLeaderOrders.TryRemove(masterOrderId, out claimed);
        }

        private void HandleFollowerOrderUpdate(Order order, OrderEventArgs e)
        {
            OrderLink link;
            if (!_linksByFollowerOrderId.TryGetValue(order.Id, out link))
                return;

            OrderState orderState = e.OrderState;

            if (orderState == OrderState.Filled)
            {
                lock (link.Gate)
                {
                    link.IsFilled = true;
                    link.IsTerminal = true;
                }

                if (link.IsExit)
                {
                    // Follower bracket cleanup must not wait for the master: if a
                    // follower target fills before the master's, the sibling stop
                    // is cancelled right here (the shared follower OCO id is the
                    // venue-side backstop for the same case).
                    CancelFollowerOcoSiblings(link);
                    SweepFollowerExitsIfMasterFlat(link);
                }
                else
                {
                    // Bridge the gap between the entry fill and the position
                    // becoming visible, so bracket copies are never skipped.
                    _recentEntryFills[FollowerScopeKey(link.Follower.AccountName, link.Instrument)] =
                        Stopwatch.GetTimestamp();
                    AddActiveEntryLinks(link.Follower.AccountName, link.Instrument, -1);
                }

                RemoveLink(link);
            }
            else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
            {
                lock (link.Gate)
                    link.IsTerminal = true;

                if (orderState == OrderState.Rejected && link.IsExit)
                    CancelFollowerOcoSiblings(link);

                if (!link.IsExit)
                    AddActiveEntryLinks(link.Follower.AccountName, link.Instrument, -1);

                RemoveLink(link);
            }
        }

        private void CancelFollowerOcoSiblings(OrderLink filledLink)
        {
            if (filledLink.FollowerOco.Length == 0)
                return;

            List<OrderLink> group;
            if (!_followerOcoGroups.TryGetValue(filledLink.FollowerOco, out group))
                return;

            OrderLink[] members = SnapshotLinks(group);
            for (int i = 0; i < members.Length; i++)
            {
                if (!ReferenceEquals(members[i], filledLink))
                    RequestLinkCancel(members[i]);
            }
        }

        private void CancelFollowerOcoGroups(List<OrderLink> links)
        {
            OrderLink[] snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++)
                CancelFollowerOcoSiblings(snapshot[i]);
        }

        private bool HasFilledFollowerSibling(OrderLink link)
        {
            if (link.FollowerOco.Length == 0)
                return false;

            List<OrderLink> group;
            if (!_followerOcoGroups.TryGetValue(link.FollowerOco, out group))
                return false;

            OrderLink[] members = SnapshotLinks(group);
            for (int i = 0; i < members.Length; i++)
            {
                if (!ReferenceEquals(members[i], link) && members[i].IsFilled)
                    return true;
            }
            return false;
        }

        // Fail closed: after a follower take profit fills and the master is flat,
        // no copied exit whose bracket already resolved (its follower OCO group
        // saw a fill, or it is a single-order mapping whose master order is gone)
        // may stay working. Groups still protecting an open follower position are
        // left alone. Slow-path reconciliation; runs only on a follower exit fill.
        private void SweepFollowerExitsIfMasterFlat(OrderLink filledExit)
        {
            if (HasOpenPosition(filledExit.MasterAccount, filledExit.Instrument))
                return;

            string followerName = filledExit.Follower.AccountName;
            string instrumentName = filledExit.Instrument.FullName;

            foreach (KeyValuePair<long, OrderLink> pair in _linksByFollowerOrderId)
            {
                OrderLink link = pair.Value;
                if (!link.IsExit || link.IsTerminal)
                    continue;
                if (!string.Equals(link.Follower.AccountName, followerName, StringComparison.Ordinal))
                    continue;
                if (link.Instrument.FullName != instrumentName)
                    continue;

                bool orphaned = link.FollowerOco.Length == 0
                    ? link.MasterTerminal
                    : HasFilledFollowerSibling(link);

                if (orphaned)
                    RequestLinkCancel(link);
            }
        }

        // Fail closed: a master exit went terminal but we hold no mapping for it
        // (e.g. copier restart). Cancel working copied exits we no longer manage.
        private void ReconcileUnmappedMasterExit(Account masterAccount, Order masterOrder)
        {
            FollowerCopyRoute[] routes;
            if (!_leaderRoutesByName.TryGetValue(masterAccount.Name, out routes))
                return;

            string instrumentName = masterOrder.Instrument != null
                ? masterOrder.Instrument.FullName
                : null;
            if (instrumentName == null)
                return;

            for (int i = 0; i < routes.Length; i++)
            {
                Account followerAccount = routes[i].Account;
                try
                {
                    List<Order> toCancel = null;
                    lock (followerAccount.Orders)
                    {
                        foreach (Order followerOrder in followerAccount.Orders)
                        {
                            if (!IsActiveOrderState(followerOrder.OrderState)) continue;
                            if (!IsExitOrder(followerAccount, followerOrder)) continue;
                            if (!string.Equals(followerOrder.Name, "REPEATER9000", StringComparison.Ordinal)) continue;
                            if (followerOrder.Instrument == null ||
                                followerOrder.Instrument.FullName != instrumentName) continue;
                            if (_linksByFollowerOrderId.ContainsKey(followerOrder.Id)) continue;

                            if (toCancel == null)
                                toCancel = new List<Order>();
                            toCancel.Add(followerOrder);
                        }
                    }
                    if (toCancel != null)
                    {
                        lock (_submissionGate)
                            if (!_isDisposed)
                                followerAccount.Cancel(toCancel);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("REPEATER9000 exit reconciliation failed: " + ex);
                }
            }
        }

        private void RemoveLink(OrderLink link)
        {
            OrderLink removed;
            _linksByMasterFollower.TryRemove(LinkKey(link.MasterOrderId, link.Follower.AccountName), out removed);

            Order followerOrder;
            lock (link.Gate)
                followerOrder = link.FollowerOrder;
            if (followerOrder != null)
            {
                OrderLink byId;
                _linksByFollowerOrderId.TryRemove(followerOrder.Id, out byId);
            }

            List<OrderLink> masterLinks;
            if (_linksByMasterOrderId.TryGetValue(link.MasterOrderId, out masterLinks))
            {
                lock (masterLinks)
                    masterLinks.Remove(link);
            }

            // Filled links stay in their OCO group list as fill memory until the
            // whole group is terminal; then the group record is dropped.
            if (link.FollowerOco.Length > 0)
            {
                List<OrderLink> group;
                if (_followerOcoGroups.TryGetValue(link.FollowerOco, out group))
                {
                    bool allTerminal = true;
                    lock (group)
                    {
                        for (int i = 0; i < group.Count; i++)
                        {
                            if (!group[i].IsTerminal)
                            {
                                allTerminal = false;
                                break;
                            }
                        }
                    }
                    if (allTerminal)
                    {
                        List<OrderLink> gone;
                        _followerOcoGroups.TryRemove(link.FollowerOco, out gone);
                        string followerOco;
                        _followerOcoByMasterOco.TryRemove(
                            link.MasterOco + "|" + link.Follower.AccountName, out followerOco);
                        if (!HasFollowerOcoMappings(link.MasterOco))
                        {
                            byte fillSeen;
                            _masterOcoFillSeen.TryRemove(link.MasterOco, out fillSeen);
                        }
                    }
                }
            }
        }

        private static OrderLink[] SnapshotLinks(List<OrderLink> links)
        {
            lock (links)
                return links.ToArray();
        }

        private void AddActiveEntryLinks(string followerName, Instrument instrument, int delta)
        {
            string key = FollowerScopeKey(followerName, instrument);
            int count = _activeEntryLinks.AddOrUpdate(key, delta > 0 ? delta : 0,
                (k, current) => Math.Max(0, current + delta));
            if (count == 0)
                ((ICollection<KeyValuePair<string, int>>)_activeEntryLinks).Remove(
                    new KeyValuePair<string, int>(key, 0));
        }

        // A master exit is only copied to followers that have copied exposure to
        // protect: a live copied entry order or an open position. This keeps
        // rejected/skipped followers from receiving naked exit orders.
        private bool CanCopyExitToFollower(FollowerCopyRoute route, Instrument instrument)
        {
            string key = FollowerScopeKey(route.Follower.AccountName, instrument);

            int activeEntries;
            if (_activeEntryLinks.TryGetValue(key, out activeEntries) && activeEntries > 0)
                return true;

            long fillTicks;
            if (_recentEntryFills.TryGetValue(key, out fillTicks))
            {
                if (TicksToMilliseconds(Stopwatch.GetTimestamp() - fillTicks) < 10000)
                    return true;
                ((ICollection<KeyValuePair<string, long>>)_recentEntryFills).Remove(
                    new KeyValuePair<string, long>(key, fillTicks));
            }

            return HasOpenPosition(route.Account, instrument);
        }

        private static bool HasOpenPosition(Account account, Instrument instrument)
        {
            return GetMarketPosition(account, instrument) != MarketPosition.Flat;
        }

        private static MarketPosition GetMarketPosition(Account account, Instrument instrument)
        {
            try
            {
                string instrumentName = instrument.FullName;
                foreach (Position position in account.Positions)
                {
                    if (position.MarketPosition != MarketPosition.Flat &&
                        position.Instrument != null &&
                        position.Instrument.FullName == instrumentName)
                        return position.MarketPosition;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("REPEATER9000 position lookup failed: " + ex);
            }
            return MarketPosition.Flat;
        }

        private void EnqueueLinkSync(OrderLink link)
        {
            if (_isDisposed || Interlocked.Exchange(ref link.SyncQueued, 1) != 0)
                return;
            SubmitQueue queue = EnsureSubmitWorker(link.Follower.AccountName);
            queue.Requests.Enqueue(new CopyRequest(link));
            queue.Signal.Set();
        }

        private void UpdateLatencyProbe(Order order, OrderState orderState, long nowTicks)
        {
            if (orderState != OrderState.Cancelled && orderState != OrderState.Rejected &&
                orderState != OrderState.PartFilled && orderState != OrderState.Filled)
                return;

            if (!_isLatencyProbeEnabled)
                return;

            long orderId = order.Id;
            PendingOrderInfo pendingOrder;

            if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected ||
                orderState == OrderState.Filled)
            {
                if (!_pendingOrders.TryRemove(orderId, out pendingOrder))
                    return;
            }
            else if (!_pendingOrders.TryGetValue(orderId, out pendingOrder))
                return;

            WriteLatencyProbeAsync(pendingOrder, order, orderState, nowTicks);
        }

        private bool IsCopyableLeaderSubmission(OrderState orderState)
        {
            return orderState == OrderState.Submitted ||
                orderState == OrderState.Accepted ||
                orderState == OrderState.Working ||
                orderState == OrderState.TriggerPending;
        }

        // Entries and ATM stop/target children are all copied; the child orders
        // carry the master OCO that drives follower-side OCO assignment.
        private static bool IsCopyableMasterOrder(Order order)
        {
            OrderAction action = order.OrderAction;
            if (action != OrderAction.Buy && action != OrderAction.SellShort &&
                action != OrderAction.Sell && action != OrderAction.BuyToCover)
                return false;

            OrderType type = order.OrderType;
            return type == OrderType.Market || type == OrderType.Limit ||
                type == OrderType.StopMarket || type == OrderType.StopLimit ||
                type == OrderType.MIT;
        }

        // A master order is an exit only if it closes against the master's live
        // position in that instrument. A Sell placed while flat is a short ENTRY
        // (NT8 does not reliably use SellShort), so it must copy as an entry.
        private static bool IsExitOrder(Account account, Order order)
        {
            OrderAction action = order.OrderAction;
            if (action == OrderAction.BuyToCover)
                return true;
            if (action == OrderAction.SellShort)
                return false;

            MarketPosition position = GetMarketPosition(account, order.Instrument);
            return action == OrderAction.Sell
                ? position == MarketPosition.Long
                : position == MarketPosition.Short;
        }

        private static bool IsActiveOrderState(OrderState state)
        {
            return state == OrderState.Submitted || state == OrderState.Accepted ||
                state == OrderState.Working || state == OrderState.TriggerPending ||
                state == OrderState.ChangePending || state == OrderState.ChangeSubmitted ||
                state == OrderState.PartFilled;
        }

        private static string LinkKey(long masterOrderId, string followerName)
        {
            return masterOrderId.ToString(CultureInfo.InvariantCulture) + "|" + followerName;
        }

        private static string FollowerScopeKey(string followerName, Instrument instrument)
        {
            return followerName + "|" + instrument.FullName;
        }

        private bool HasFollowerOcoMappings(string masterOco)
        {
            string prefix = masterOco + "|";
            foreach (string key in _followerOcoByMasterOco.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private void AbandonUnsubmittedLink(OrderLink link)
        {
            Order followerOrder;
            lock (link.Gate)
            {
                link.IsTerminal = true;
                followerOrder = link.FollowerOrder;
            }
            if (!link.IsExit)
                AddActiveEntryLinks(link.Follower.AccountName, link.Instrument, -1);
            if (followerOrder != null)
            {
                PendingOrderInfo pending;
                _pendingOrders.TryRemove(followerOrder.Id, out pending);
            }
            RemoveLink(link);
        }

        private void CopyExecutionToFollower(CopyRequest request)
        {
            OrderLink link = request.Link;
            FollowerAccountData follower = link.Follower;
            Order followerOrder = null;

            try
            {
                if (_isDisposed)
                {
                    AbandonUnsubmittedLink(link);
                    return;
                }
                double limitPrice = 0;
                double stopPrice = 0;
                int quantity = 0;
                bool abandoned = false;
                lock (link.Gate)
                {
                    if (link.CancelRequested || link.IsTerminal)
                    {
                        link.IsTerminal = true;
                        abandoned = true;
                    }
                    else
                    {
                        limitPrice = link.TargetLimitPrice;
                        stopPrice = link.TargetStopPrice;
                        quantity = link.TargetQuantity;
                    }
                }
                if (abandoned)
                {
                    AbandonUnsubmittedLink(link);
                    return;
                }

                long orderCreatedTicks = Stopwatch.GetTimestamp();
                followerOrder = link.FollowerAccount.CreateOrder(
                    link.Instrument,
                    link.OrderAction,
                    link.OrderType,
                    OrderEntry.Automated,
                    link.TimeInForce,
                    quantity,
                    limitPrice,
                    stopPrice,
                    link.FollowerOco,
                    "REPEATER9000",
                    Core.Globals.MaxDate,
                    null);

                lock (link.Gate)
                    link.FollowerOrder = followerOrder;
                _linksByFollowerOrderId[followerOrder.Id] = link;

                long beforeSubmitTicks = Stopwatch.GetTimestamp();
                if (_isLatencyProbeEnabled)
                    _pendingOrders[followerOrder.Id] = new PendingOrderInfo(follower.AccountName,
                        request.MasterReceivedTicks, orderCreatedTicks, beforeSubmitTicks,
                        beforeSubmitTicks, request.MasterReceivedUtc);

                lock (_submissionGate)
                {
                    lock (link.Gate)
                    {
                        if (_isDisposed || link.CancelRequested || link.IsTerminal)
                            abandoned = true;
                    }
                    if (!abandoned)
                        link.FollowerAccount.Submit(new Order[] { followerOrder });
                }
                if (abandoned)
                {
                    AbandonUnsubmittedLink(link);
                    return;
                }

                long afterSubmitTicks = Stopwatch.GetTimestamp();
                if (_isLatencyProbeEnabled)
                    _pendingOrders[followerOrder.Id] = new PendingOrderInfo(follower.AccountName,
                        request.MasterReceivedTicks, orderCreatedTicks, beforeSubmitTicks,
                        afterSubmitTicks, request.MasterReceivedUtc);

                bool cancelNow = false;
                bool resync = false;
                lock (link.Gate)
                {
                    link.IsSubmitted = true;
                    if (link.CancelRequested)
                        cancelNow = true;
                    else if (link.TargetLimitPrice != limitPrice ||
                        link.TargetStopPrice != stopPrice ||
                        link.TargetQuantity != quantity)
                        resync = true;
                }

                // The master may have moved or cancelled the order while this
                // submit was in flight; converge before going back to the queue.
                if (cancelNow)
                    TryCancelOrder(link.FollowerAccount, followerOrder);
                else if (resync)
                    SyncFollowerOrder(link);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("REPEATER9000 follower copy failed: " + ex);
                lock (link.Gate)
                    link.IsTerminal = true;
                if (!link.IsExit)
                    AddActiveEntryLinks(follower.AccountName, link.Instrument, -1);
                if (followerOrder != null)
                {
                    PendingOrderInfo pending;
                    _pendingOrders.TryRemove(followerOrder.Id, out pending);
                }
                RemoveLink(link);

                string message = ex.Message;
                Dispatcher.InvokeAsync(() => UpdateStatus(string.Format("Error copying to {0}: {1}", follower.AccountName, message)));
            }
        }

        // Converge a mapped follower order to the master's latest state: cancel
        // if the master went away, otherwise Change price/quantity in place.
        private void SyncFollowerOrder(OrderLink link)
        {
            if (_isDisposed)
                return;
            Order followerOrder;
            bool cancelRequested;
            double limitPrice;
            double stopPrice;
            int quantity;

            lock (link.Gate)
            {
                if (link.IsTerminal || link.FollowerOrder == null || !link.IsSubmitted)
                    return;
                followerOrder = link.FollowerOrder;
                cancelRequested = link.CancelRequested;
                limitPrice = link.TargetLimitPrice;
                stopPrice = link.TargetStopPrice;
                quantity = link.TargetQuantity;
            }

            if (cancelRequested)
            {
                TryCancelOrder(link.FollowerAccount, followerOrder);
                return;
            }

            if (!IsActiveOrderState(followerOrder.OrderState))
                return;

            try
            {
                bool changed = false;
                OrderType orderType = followerOrder.OrderType;

                if ((orderType == OrderType.Limit || orderType == OrderType.StopLimit) &&
                    limitPrice > 0 && followerOrder.LimitPrice != limitPrice)
                {
                    followerOrder.LimitPriceChanged = limitPrice;
                    changed = true;
                }

                if ((orderType == OrderType.StopMarket || orderType == OrderType.StopLimit ||
                    orderType == OrderType.MIT) &&
                    stopPrice > 0 && followerOrder.StopPrice != stopPrice)
                {
                    followerOrder.StopPriceChanged = stopPrice;
                    changed = true;
                }

                if (quantity > 0 && followerOrder.Quantity != quantity)
                {
                    followerOrder.QuantityChanged = quantity;
                    changed = true;
                }

                if (changed)
                {
                    lock (_submissionGate)
                        if (!_isDisposed)
                            link.FollowerAccount.Change(new Order[] { followerOrder });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("REPEATER9000 follower sync failed: " + ex);
            }
        }

        private SubmitQueue EnsureSubmitWorker(string followerName)
        {
            SubmitQueue queue;
            if (_submitQueues.TryGetValue(followerName, out queue))
                return queue;

            queue = new SubmitQueue();
            if (_submitQueues.TryAdd(followerName, queue))
            {
                Thread worker = new Thread(() => ProcessSubmitQueue(queue));
                worker.IsBackground = true;
                worker.Name = "REPEATER9000 " + followerName;
                worker.Start();
                return queue;
            }

            queue.Signal.Dispose();
            _submitQueues.TryGetValue(followerName, out queue);
            return queue;
        }

        private void ProcessSubmitQueue(SubmitQueue queue)
        {
            try
            {
                while (!queue.IsCompleted)
                {
                    CopyRequest request;
                    if (!queue.Requests.TryDequeue(out request))
                    {
                        queue.Signal.Reset();
                        if (!queue.Requests.IsEmpty)
                            queue.Signal.Set();
                        else
                            queue.Signal.Wait();
                        continue;
                    }

                    try
                    {
                        if (!_isDisposed)
                        {
                            if (request.IsCreate)
                                CopyExecutionToFollower(request);
                            else
                            {
                                Interlocked.Exchange(ref request.Link.SyncQueued, 0);
                                SyncFollowerOrder(request.Link);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("REPEATER9000 submit worker failed: " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("REPEATER9000 submit worker terminated: " + ex);
            }
        }

        private void StopSubmitWorkers()
        {
            foreach (SubmitQueue queue in _submitQueues.Values)
            {
                try
                {
                    queue.IsCompleted = true;
                    queue.Signal.Set();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("REPEATER9000 submit worker stop failed: " + ex);
                }
            }
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static DateTime ProbeUtc(PendingOrderInfo pendingOrder, long timestamp)
        {
            return pendingOrder.MasterReceivedUtc.AddSeconds(
                (timestamp - pendingOrder.MasterReceivedTimestamp) / (double)Stopwatch.Frequency);
        }

        private void WriteLatencyProbeAsync(PendingOrderInfo pendingOrder, Order order,
            OrderState orderState, long orderUpdateTimestamp)
        {
            string row = BuildLatencyProbeRow(pendingOrder, order, orderState, orderUpdateTimestamp);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    lock (LatencyProbeFileLock)
                    {
                        string directory = Path.GetDirectoryName(LatencyProbeFilePath);
                        if (!string.IsNullOrEmpty(directory))
                            Directory.CreateDirectory(directory);

                        bool writeHeader = !File.Exists(LatencyProbeFilePath);
                        using (StreamWriter writer = new StreamWriter(LatencyProbeFilePath, true))
                        {
                            if (writeHeader)
                                writer.WriteLine(LatencyProbeHeader);
                            writer.WriteLine(row);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("REPEATER9000 latency write failed: " + ex);
                }
            });
        }

        private void EnsureLatencyProbeFile()
        {
            lock (LatencyProbeFileLock)
            {
                string directory = Path.GetDirectoryName(LatencyProbeFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                if (File.Exists(LatencyProbeFilePath))
                    return;

                using (StreamWriter writer = new StreamWriter(LatencyProbeFilePath, false))
                    writer.WriteLine(LatencyProbeHeader);
            }
        }

        private static string BuildLatencyProbeRow(PendingOrderInfo pendingOrder, Order order,
            OrderState orderState, long orderUpdateTimestamp)
        {
            double orderCreateDelayMs = TicksToMilliseconds(
                pendingOrder.OrderCreatedTimestamp - pendingOrder.MasterReceivedTimestamp);
            double submitDelayMs = TicksToMilliseconds(
                pendingOrder.BeforeSubmitTimestamp - pendingOrder.MasterReceivedTimestamp);
            double submitCallMs = TicksToMilliseconds(
                pendingOrder.AfterSubmitTimestamp - pendingOrder.BeforeSubmitTimestamp);
            double orderUpdateDelayMs = TicksToMilliseconds(
                orderUpdateTimestamp - pendingOrder.AfterSubmitTimestamp);
            string fillDelayMs = orderState == OrderState.Filled
                ? orderUpdateDelayMs.ToString("F3", CultureInfo.InvariantCulture)
                : string.Empty;

            return string.Join(",",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                order.Id.ToString(CultureInfo.InvariantCulture),
                Csv(pendingOrder.FollowerName),
                Csv(orderState.ToString()),
                pendingOrder.MasterReceivedUtc.ToString("O", CultureInfo.InvariantCulture),
                ProbeUtc(pendingOrder, pendingOrder.OrderCreatedTimestamp).ToString("O", CultureInfo.InvariantCulture),
                ProbeUtc(pendingOrder, pendingOrder.BeforeSubmitTimestamp).ToString("O", CultureInfo.InvariantCulture),
                ProbeUtc(pendingOrder, pendingOrder.AfterSubmitTimestamp).ToString("O", CultureInfo.InvariantCulture),
                ProbeUtc(pendingOrder, orderUpdateTimestamp).ToString("O", CultureInfo.InvariantCulture),
                pendingOrder.MasterReceivedTimestamp.ToString(CultureInfo.InvariantCulture),
                pendingOrder.OrderCreatedTimestamp.ToString(CultureInfo.InvariantCulture),
                pendingOrder.BeforeSubmitTimestamp.ToString(CultureInfo.InvariantCulture),
                pendingOrder.AfterSubmitTimestamp.ToString(CultureInfo.InvariantCulture),
                orderUpdateTimestamp.ToString(CultureInfo.InvariantCulture),
                orderCreateDelayMs.ToString("F3", CultureInfo.InvariantCulture),
                submitDelayMs.ToString("F3", CultureInfo.InvariantCulture),
                submitCallMs.ToString("F3", CultureInfo.InvariantCulture),
                orderUpdateDelayMs.ToString("F3", CultureInfo.InvariantCulture),
                fillDelayMs);
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) < 0)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        #endregion

        #region Follower/Group Logic Helpers

        private string GetFollowerLeader(FollowerAccountData follower)
        {
            FollowerGroupData group = GetFollowerGroup(follower);
            if (group != null && !string.IsNullOrEmpty(group.LeaderAccountName))
                return group.LeaderAccountName;

            return null;
        }

        private FollowerGroupData GetFollowerGroup(FollowerAccountData follower)
        {
            if (!follower.HasGroup) return null;

            foreach (FollowerGroupData g in _groups)
            {
                if (g.GroupName == follower.GroupName)
                    return g;
            }
            return null;
        }

        private void RebuildFollowerMap()
        {
            Dictionary<string, List<FollowerCopyRoute>> routeLists =
                new Dictionary<string, List<FollowerCopyRoute>>(StringComparer.Ordinal);
            Dictionary<string, FollowerAccountData> followersByName =
                new Dictionary<string, FollowerAccountData>(StringComparer.Ordinal);

            foreach (FollowerAccountData follower in _followers)
            {
                if (!string.IsNullOrEmpty(follower.AccountName))
                    followersByName[follower.AccountName] = follower;

                string leader = GetFollowerLeader(follower);
                if (string.IsNullOrEmpty(leader)) continue;

                Account followerAccount;
                if (!_accountCache.TryGetValue(follower.AccountName, out followerAccount))
                    continue;

                FollowerCopyRoute route = new FollowerCopyRoute(follower, followerAccount);

                List<FollowerCopyRoute> routesForLeader;
                if (!routeLists.TryGetValue(leader, out routesForLeader))
                {
                    routesForLeader = new List<FollowerCopyRoute>();
                    routeLists[leader] = routesForLeader;
                }

                routesForLeader.Add(route);
            }

            Dictionary<string, FollowerCopyRoute[]> routesByLeader =
                new Dictionary<string, FollowerCopyRoute[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<FollowerCopyRoute>> pair in routeLists)
                routesByLeader[pair.Key] = pair.Value.ToArray();

            _followersByName = followersByName;
            _leaderRoutesByName = routesByLeader;
            RefreshAccountEventSubscriptions();
        }

        #endregion

        #region UI Commands and Status

        private List<FollowerAccountData> SelectedFollowers()
        {
            List<FollowerAccountData> selected = new List<FollowerAccountData>();
            foreach (object item in _followerDataGrid.SelectedItems)
            {
                FollowerAccountData follower = item as FollowerAccountData;
                if (follower != null) selected.Add(follower);
            }
            return selected;
        }

        private bool CanChangeRoutes()
        {
            if (_linksByMasterFollower.IsEmpty)
                return true;

            MessageBox.Show("Wait for copied orders to reach a terminal state before changing follower routes.",
                "Active ATM Orders", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        private void OnAddFollowerClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeRoutes()) return;
            RefreshAccountCache();
            List<string> available = GetAvailableFollowerAccounts();
            if (available.Count == 0)
            {
                MessageBox.Show("No accounts are available to add.", "Add Follower", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MultiAccountSelectionDialog dialog = new MultiAccountSelectionDialog(available, "Add Followers");
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            foreach (string name in dialog.SelectedAccounts)
                _followers.Add(new FollowerAccountData(name));
            UpdateAllStatuses();
            SaveSettings();
            UpdateStatus("Added " + dialog.SelectedAccounts.Count + " follower account(s)");
        }

        private void OnRemoveFollowerClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeRoutes()) return;
            List<FollowerAccountData> selected = SelectedFollowers();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select one or more followers first.", "Remove Follower", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("Remove " + selected.Count + " follower account(s)?", "Remove Follower", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (FollowerAccountData follower in selected) _followers.Remove(follower);
            SaveSettings();
            UpdateSelectionText();
            UpdateStatus("Removed " + selected.Count + " follower account(s)");
        }

        private void OnAddToGroupClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeRoutes()) return;
            List<FollowerAccountData> selected = SelectedFollowers();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select one or more followers first.", "Add to Group", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            List<string> names = new List<string>();
            foreach (FollowerGroupData group in _groups) names.Add(group.GroupName);
            if (names.Count == 0)
            {
                MessageBox.Show("Create a group first.", "Add to Group", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AccountSelectionDialog dialog = new AccountSelectionDialog(names, "Choose Group", "Select group:");
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            foreach (FollowerAccountData follower in selected) follower.GroupName = dialog.SelectedAccount;
            SaveSettings();
            UpdateStatus("Added " + selected.Count + " follower account(s) to " + dialog.SelectedAccount);
        }

        private void OnCreateGroupClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeRoutes()) return;
            RefreshAccountCache();
            GroupEditorDialog dialog = new GroupEditorDialog(GetNonFollowerAccounts(), "Create Group", null, null);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            if (FindGroup(dialog.GroupName) != null)
            {
                MessageBox.Show("That group name already exists.", "Create Group", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _groups.Add(new FollowerGroupData(dialog.GroupName) { LeaderAccountName = dialog.LeaderAccountName });
            SaveSettings();
            UpdateStatus("Created group " + dialog.GroupName);
        }

        private void OnEditGroupClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeRoutes()) return;
            RefreshAccountCache();
            FollowerGroupData group = _groupDataGrid.SelectedItem as FollowerGroupData;
            if (group == null)
            {
                MessageBox.Show("Select a group first.", "Edit Group", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            GroupEditorDialog dialog = new GroupEditorDialog(GetNonFollowerAccounts(), "Edit Group", group.GroupName, group.LeaderAccountName);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            FollowerGroupData duplicate = FindGroup(dialog.GroupName);
            if (duplicate != null && !ReferenceEquals(duplicate, group))
            {
                MessageBox.Show("That group name already exists.", "Edit Group", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string oldName = group.GroupName;
            group.GroupName = dialog.GroupName;
            group.LeaderAccountName = dialog.LeaderAccountName;
            foreach (FollowerAccountData follower in _followers)
                if (follower.GroupName == oldName) follower.GroupName = group.GroupName;
            SaveSettings();
            UpdateStatus("Updated group " + group.GroupName);
        }

        private void OnDeleteGroupButtonClick(object sender, RoutedEventArgs e)
        {
            if (!CanChangeRoutes()) return;
            FollowerGroupData group = _groupDataGrid.SelectedItem as FollowerGroupData;
            if (group == null)
            {
                MessageBox.Show("Select a group first.", "Remove Group", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("Do you really want to delete \"" + group.GroupName + "\"?", "Remove Group", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (FollowerAccountData follower in _followers)
                if (follower.GroupName == group.GroupName) follower.GroupName = "Ungrouped";
            _groups.Remove(group);
            SaveSettings();
            UpdateStatus("Deleted group " + group.GroupName);
        }

        private FollowerGroupData FindGroup(string name)
        {
            foreach (FollowerGroupData group in _groups)
                if (string.Equals(group.GroupName, name, StringComparison.OrdinalIgnoreCase)) return group;
            return null;
        }

        private void OnFlattenAllClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Flatten open positions on every configured follower account and disable copying?", "Flatten ALL", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _isCopyingEnabled = false;
            UpdateAllStatuses();
            SaveSettings();
            int flattened = 0;
            foreach (FollowerAccountData follower in _followers)
            {
                Account account;
                if (!_accountCache.TryGetValue(follower.AccountName, out account)) continue;
                try
                {
                    List<Instrument> instruments = new List<Instrument>();
                    lock (account.Positions)
                        foreach (Position position in account.Positions)
                            if (position.MarketPosition != MarketPosition.Flat) instruments.Add(position.Instrument);
                    if (instruments.Count > 0)
                    {
                        account.Flatten(instruments);
                        flattened++;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("REPEATER9000 flatten failed: " + ex);
                }
            }
            UpdateStatus("Copying disabled; flatten requested for " + flattened + " follower account(s)");
        }

        private void UpdateAllStatuses()
        {
            foreach (FollowerAccountData follower in _followers)
                follower.Status = _isCopyingEnabled ? "Copying Enabled" : "Copying Disabled";
            if (_enableCopyingButton != null)
            {
                _enableCopyingButton.Content = _isCopyingEnabled ? "COPYING ENABLED" : "COPYING DISABLED";
                _enableCopyingButton.Background = _isCopyingEnabled
                    ? new SolidColorBrush(Color.FromRgb(34, 211, 238))
                    : new SolidColorBrush(Color.FromRgb(145, 48, 48));
            }
        }

        private void UpdateStatus(string message)
        {
            if (_statusTextBlock != null)
                _statusTextBlock.Text = message;
        }

        #endregion


        #region Settings Persistence

        private void SaveSettings()
        {
            try
            {
                Repeater9000Settings settings = new Repeater9000Settings
                {
                    LatencyProbeEnabled = _isLatencyProbeEnabled,
                    Followers = new List<FollowerAccountData>(_followers),
                    Groups = new List<FollowerGroupData>(_groups)
                };

                string directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                string temporaryPath = SettingsFilePath + ".tmp";
                XmlSerializer serializer = new XmlSerializer(typeof(Repeater9000Settings));
                using (StreamWriter writer = new StreamWriter(temporaryPath))
                {
                    serializer.Serialize(writer, settings);
                }
                if (File.Exists(SettingsFilePath))
                    File.Replace(temporaryPath, SettingsFilePath, null);
                else
                    File.Move(temporaryPath, SettingsFilePath);

                if (!_isDisposed) RebuildFollowerMap();
            }
            catch (Exception ex)
            {
                UpdateStatus(string.Format("Error saving settings: {0}", ex.Message));
            }
        }

        private void LoadSettings()
        {
            _isLoadingSettings = true;
            try
            {
                if (!File.Exists(SettingsFilePath)) return;

                XmlSerializer serializer = new XmlSerializer(typeof(Repeater9000Settings));
                using (StreamReader reader = new StreamReader(SettingsFilePath))
                {
                    Repeater9000Settings settings = (Repeater9000Settings)serializer.Deserialize(reader);

                    // A saved layout must never resume live copying on startup.
                    _isCopyingEnabled = false;
                    _isLatencyProbeEnabled = settings.LatencyProbeEnabled;
                    _latencyProbeCheckBox.IsChecked = _isLatencyProbeEnabled;

                    _followers.Clear();
                    if (settings.Followers != null)
                    {
                        foreach (FollowerAccountData follower in settings.Followers)
                        {
                            _followers.Add(follower);
                        }
                    }

                    _groups.Clear();
                    if (settings.Groups != null)
                    {
                        foreach (FollowerGroupData group in settings.Groups)
                        {
                            _groups.Add(group);
                        }
                    }
                }

                RebuildFollowerMap();
                UpdateAllStatuses();
                UpdateStatus("Settings loaded");
            }
            catch (Exception ex)
            {
                UpdateStatus(string.Format("Error loading settings: {0}", ex.Message));
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        #endregion

        #region Window Lifecycle

        internal void Shutdown()
        {
            if (_isDisposed) return;
            _isShuttingDown = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isShuttingDown)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            lock (_submissionGate)
                _isDisposed = true;

            UnsubscribeFromAccountEvents();
            StopSubmitWorkers();
            SaveSettings();

            base.OnClosed(e);
        }

        #endregion
    }

    #region Dialog Windows

    public class AccountSelectionDialog : Window
    {
        public string SelectedAccount { get; private set; }
        private ComboBox _accountComboBox;

        public AccountSelectionDialog(List<string> accounts, string title, string prompt = "Select Account:")
        {
            Title = title;
            Width = 320;
            Height = 160;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));
            ResizeMode = ResizeMode.NoResize;

            StackPanel stack = new StackPanel { Margin = new Thickness(20) };

            TextBlock label = new TextBlock
            {
                Text = prompt,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 10)
            };
            stack.Children.Add(label);

            _accountComboBox = new ComboBox
            {
                ItemsSource = accounts,
                SelectedIndex = accounts.Count > 0 ? 0 : -1,
                Margin = new Thickness(0, 0, 0, 20),
                Height = 26
            };
            stack.Children.Add(_accountComboBox);

            StackPanel buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button okButton = new Button
            {
                Content = "OK",
                Width = 75,
                Height = 26,
                Margin = new Thickness(0, 0, 10, 0)
            };
            okButton.Click += (s, e) =>
            {
                SelectedAccount = _accountComboBox.SelectedItem as string;
                DialogResult = true;
            };
            buttonPanel.Children.Add(okButton);

            Button cancelButton = new Button
            {
                Content = "Cancel",
                Width = 75,
                Height = 26
            };
            cancelButton.Click += (s, e) => DialogResult = false;
            buttonPanel.Children.Add(cancelButton);

            stack.Children.Add(buttonPanel);
            Content = stack;
        }
    }

    public class MultiAccountSelectionDialog : Window
    {
        public List<string> SelectedAccounts { get; private set; }
        private ListBox _accounts;

        public MultiAccountSelectionDialog(List<string> accounts, string title)
        {
            Title = title;
            Width = 360;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));

            Grid page = new Grid { Margin = new Thickness(20) };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.Children.Add(new TextBlock { Text = "Select one or more accounts:", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
            _accounts = new ListBox { ItemsSource = accounts, SelectionMode = SelectionMode.Extended };
            Grid.SetRow(_accounts, 1);
            page.Children.Add(_accounts);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button add = new Button { Content = "Add Selected", Width = 105, Margin = new Thickness(0, 0, 8, 0) };
            add.Click += (s, e) =>
            {
                SelectedAccounts = new List<string>();
                foreach (object item in _accounts.SelectedItems) SelectedAccounts.Add(item.ToString());
                if (SelectedAccounts.Count == 0) return;
                DialogResult = true;
            };
            buttons.Children.Add(add);
            Button cancel = new Button { Content = "Cancel", Width = 75 };
            cancel.Click += (s, e) => DialogResult = false;
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            page.Children.Add(buttons);
            Content = page;
        }
    }

    public class GroupEditorDialog : Window
    {
        public string GroupName { get; private set; }
        public string LeaderAccountName { get; private set; }
        private TextBox _name;
        private ComboBox _leader;

        public GroupEditorDialog(List<string> leaders, string title, string groupName, string leaderName)
        {
            Title = title;
            Width = 360;
            Height = 220;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));
            ResizeMode = ResizeMode.NoResize;
            StackPanel page = new StackPanel { Margin = new Thickness(20) };
            page.Children.Add(new TextBlock { Text = "Group name", Foreground = Brushes.White });
            _name = new TextBox { Text = groupName ?? string.Empty, Height = 25, Margin = new Thickness(0, 4, 0, 12) };
            page.Children.Add(_name);
            page.Children.Add(new TextBlock { Text = "Leader account", Foreground = Brushes.White });
            _leader = new ComboBox { ItemsSource = leaders, SelectedItem = leaderName, Height = 26, Margin = new Thickness(0, 4, 0, 18) };
            if (_leader.SelectedIndex < 0 && leaders.Count > 0) _leader.SelectedIndex = 0;
            page.Children.Add(_leader);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button save = new Button { Content = "Save", Width = 75, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (s, e) =>
            {
                GroupName = _name.Text.Trim();
                LeaderAccountName = _leader.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(GroupName) || string.IsNullOrWhiteSpace(LeaderAccountName))
                {
                    MessageBox.Show("A group name and leader account are required.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                DialogResult = true;
            };
            buttons.Children.Add(save);
            Button cancel = new Button { Content = "Cancel", Width = 75 };
            cancel.Click += (s, e) => DialogResult = false;
            buttons.Children.Add(cancel);
            page.Children.Add(buttons);
            Content = page;
        }
    }

    #endregion
}
