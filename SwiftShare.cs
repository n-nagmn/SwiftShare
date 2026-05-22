using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime;
using System.IO.MemoryMappedFiles;

namespace FileTransferApp
{
    public class SwiftShare : Form
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        private const int UdpPort = 8888;
        private const string MulticastIp = "239.255.255.250";
        private string instanceId = Guid.NewGuid().ToString();
        private int actualTcpPort = 0;
        private string myIpAddress;
        private string linkSpeed = "Unknown";
        private HashSet<string> localIps = new HashSet<string>();
        private Dictionary<string, DateTime> peerLastSeen = new Dictionary<string, DateTime>();
        
        private string currentRemotePeer = ""; 
        private string currentRemotePath = "";

        private List<string> remoteClipboardPaths = new List<string>();
        private string remoteClipboardPeer = "";
        
        private TableLayoutPanel rootLayout;
        private Panel sidebar;
        private Panel dashboard;
        private ListBox peerList;
        
        private SplitContainer splitMain;
        private SplitContainer splitExplorer;

        private TextBox txtLocal;
        private ListView lvLocal;
        private Button btnLocalUp;
        private Button btnLocalRefresh;
        private Button btnUpload;

        private TextBox txtRemote;
        private ListView lvRemote;
        private Button btnRemoteUp;
        private Button btnRemoteRefresh;
        private Button btnDownload;
        
        private FlowLayoutPanel historyFlow;
        private MenuStrip mainMenu;
        private Label infoLbl;
        private ImageList imageList;
        private NotifyIcon trayIcon;
        private ContextMenu trayMenu;

        [StructLayout(LayoutKind.Sequential)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        public const uint SHGFI_ICON = 0x100;
        public const uint SHGFI_SMALLICON = 0x1;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        public const uint SHGFI_TYPENAME = 0x400;

        [DllImport("shell32.dll")]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        extern static bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        public const int WM_MOUSEWHEEL = 0x020A;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private void SetDarkTitleBar()
        {
            try {
                int useDarkMode = 1;
                if (DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int)) != 0)
                    DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));
            } catch { }
        }
        
        private Color primaryColor = Color.FromArgb(63, 81, 181); 
        private Color sidebarColor = Color.FromArgb(33, 33, 33);
        private Color bgColor = Color.FromArgb(245, 245, 245);
        private Color cardColor = Color.White;
        private Dictionary<string, string> typeCache = new Dictionary<string, string>();
        private Font typeFont = new Font("Segoe UI", 9f);
        private Dictionary<string, string> peerNames = new Dictionary<string, string>();

        [STAThread]
        static void Main()
        {
            try {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            } catch {}
            
            Application.EnableVisualStyles();

            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => {
                MessageBox.Show("Error:\n" + e.Exception.Message, "SwiftShare Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                MessageBox.Show("Fatal error.", "SwiftShare Fatal Error");
            };
            Application.Run(new SwiftShare());
        }

        public SwiftShare()
        {
            UpdateNetworkInfo();
            InitializeComponent();
            SetupTrayIcon();
            
            this.Load += (s, e) => {
                SetDarkTitleBar();
                StartTcpServer();
                StartUdpListener();
                BroadcastPresence();
                PeerCleanupLoop();
                LoadLayoutSettings();
                string dlDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                RefreshLocalList(dlDir);

                if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "/background") {
                    this.BeginInvoke(new MethodInvoker(delegate {
                        this.Hide();
                        this.WindowState = FormWindowState.Minimized;
                    }));
                }
            };

            this.FormClosing += (s, e) => {
                if (e.CloseReason == CloseReason.UserClosing) {
                    e.Cancel = true;
                    this.Hide();
                    this.WindowState = FormWindowState.Minimized;
                } else {
                    SaveLayoutSettings();
                }
            };
        }

        private void SetupTrayIcon()
        {
            this.Icon = CreateStylishIcon();
            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Open", (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; this.BringToFront(); });
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Exit", (s, e) => { SaveLayoutSettings(); trayIcon.Visible = false; Application.Exit(); });

            trayIcon = new NotifyIcon();
            trayIcon.Text = "SwiftShare";
            trayIcon.Icon = this.Icon;
            trayIcon.ContextMenu = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; this.BringToFront(); };
        }

        private Icon CreateStylishIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // Background Circle with Gradient
                using (System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Point(0, 0), new Point(32, 32), Color.FromArgb(63, 81, 181), Color.FromArgb(48, 63, 159))) {
                    g.FillEllipse(brush, 2, 2, 28, 28);
                }
                // Stylish S/Arrow Motif
                Point[] pts = { new Point(8, 16), new Point(16, 8), new Point(24, 16), new Point(16, 16), new Point(16, 24) };
                using (Pen p = new Pen(Color.White, 3)) {
                    p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(p, pts);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void LoadLayoutSettings()
        {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\SwiftShare", false)) {
                    if (key != null) {
                        if (key.GetValue("SplitMainDist") != null) splitMain.SplitterDistance = (int)key.GetValue("SplitMainDist");
                    } else {
                        splitMain.SplitterDistance = 670;
                    }
                }
            } catch { }
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\SwiftShare\PeerNames", false)) {
                    if (key != null) {
                        foreach (string valName in key.GetValueNames()) {
                            string val = key.GetValue(valName).ToString();
                            if (!val.Contains("|")) val += "|0";
                            peerNames[valName] = val;
                        }
                    }
                }
            } catch { }
        }

        private void SaveLayoutSettings()
        {
            try {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SwiftShare")) {
                    key.SetValue("SplitMainDist", splitMain.SplitterDistance);
                }
            } catch { }
        }

        private void UpdateNetworkInfo()
        {
            myIpAddress = "127.0.0.1";
            localIps.Clear();
            localIps.Add("127.0.0.1");
            try {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) {
                    if (ni.OperationalStatus == OperationalStatus.Up) {
                        foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses) {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork) {
                                localIps.Add(ip.Address.ToString());
                                if (ni.NetworkInterfaceType != NetworkInterfaceType.Loopback && !IsVirtualAdapter(ni) && ni.GetIPProperties().GatewayAddresses.Count > 0) {
                                    myIpAddress = ip.Address.ToString();
                                    linkSpeed = (ni.Speed / 1000000).ToString() + " Mbps";
                                }
                            }
                        }
                    }
                }
            } catch { }
        }

        private bool IsVirtualAdapter(NetworkInterface ni)
        {
            string desc = ni.Description.ToLower();
            string name = ni.Name.ToLower();
            return desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("vbox") || desc.Contains("hyper-v") || name.Contains("vnet") || name.Contains("pseudo");
        }

        private void InitializeComponent()
        {
            this.Text = "SwiftShare - Advanced Explorer";
            this.Size = new Size(1560, 1020);
            this.MinimumSize = new Size(1300, 840);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = bgColor;
            this.Font = new Font("Segoe UI", 10);
            this.AllowDrop = true;

            mainMenu = new MenuStrip();
            mainMenu.BackColor = Color.FromArgb(45, 45, 45);
            mainMenu.ForeColor = Color.White;
            mainMenu.Renderer = new DarkRenderer();

            ToolStripMenuItem fileMenu = new ToolStripMenuItem("File(&F)");
            fileMenu.ForeColor = Color.White;
            ToolStripMenuItem autoLaunchItem = new ToolStripMenuItem("Launch on Startup");
            autoLaunchItem.CheckOnClick = true;
            autoLaunchItem.Checked = IsAutoLaunchEnabled();
            autoLaunchItem.Click += (s, e) => SetAutoLaunch(autoLaunchItem.Checked);
            autoLaunchItem.ForeColor = Color.White;
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit(&X)", null, (s, e) => Application.Exit());
            exitItem.ForeColor = Color.White;
            fileMenu.DropDownItems.Add(autoLaunchItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(exitItem);

            ToolStripMenuItem helpMenu = new ToolStripMenuItem("Help(&H)");
            helpMenu.ForeColor = Color.White;
            ToolStripMenuItem aboutItem = new ToolStripMenuItem("About SwiftShare", null, (s, e) => MessageBox.Show("SwiftShare v1.1\nHigh-speed file transfer tool.", "About"));
            aboutItem.ForeColor = Color.White;
            helpMenu.DropDownItems.Add(aboutItem);

            mainMenu.Items.Add(fileMenu);
            mainMenu.Items.Add(helpMenu);
            this.MainMenuStrip = mainMenu;
            this.Controls.Add(mainMenu);

            rootLayout = new TableLayoutPanel();
            rootLayout.Dock = DockStyle.Fill;
            rootLayout.ColumnCount = 2;
            rootLayout.RowCount = 1;
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            this.Controls.Add(rootLayout);
            rootLayout.BringToFront();

            sidebar = new Panel();
            sidebar.Dock = DockStyle.Fill;
            sidebar.BackColor = sidebarColor;
            sidebar.Margin = new Padding(0);
            rootLayout.Controls.Add(sidebar, 0, 0);

            Label logo = new Label();
            logo.Text = "SwiftShare";
            logo.ForeColor = Color.White;
            logo.Font = new Font("Segoe UI Semibold", 18);
            logo.Location = new Point(20, 30);
            logo.AutoSize = true;
            sidebar.Controls.Add(logo);

            infoLbl = new Label();
            infoLbl.Text = "IP: " + myIpAddress + "\nPort: " + actualTcpPort + "\nLink: " + linkSpeed;
            infoLbl.ForeColor = Color.DarkGray;
            infoLbl.Font = new Font("Segoe UI", 8);
            infoLbl.Location = new Point(22, 70);
            infoLbl.AutoSize = true;
            sidebar.Controls.Add(infoLbl);

            Label onlineTitle = new Label();
            onlineTitle.Text = "ONLINE PEERS";
            onlineTitle.ForeColor = Color.Gray;
            onlineTitle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            onlineTitle.Location = new Point(20, 160);
            onlineTitle.AutoSize = true;
            sidebar.Controls.Add(onlineTitle);

            peerList = new ListBox();
            peerList.BackColor = sidebarColor;
            peerList.ForeColor = Color.White;
            peerList.BorderStyle = BorderStyle.None;
            peerList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            peerList.Location = new Point(10, 190);
            peerList.Size = new Size(240, 600);
            peerList.ItemHeight = 55;
            peerList.DrawMode = DrawMode.OwnerDrawFixed;
            peerList.DrawItem += PeerList_DrawItem;
            peerList.SelectedIndexChanged += (s, e) => {
                if (peerList.SelectedItem == null) return;
                string itemText = peerList.SelectedItem.ToString();
                string newPeer = itemText.Split(' ')[0];
                if (newPeer != currentRemotePeer) {
                    currentRemotePeer = newPeer;
                    currentRemotePath = "";
                    RefreshRemoteList();
                }
            };
            ContextMenuStrip peerMenu = new ContextMenuStrip();
            ToolStripMenuItem renameItem = new ToolStripMenuItem("Set Alias / Name");
            renameItem.Click += (s, e) => {
                if (peerList.SelectedItem == null) return;
                string ep = peerList.SelectedItem.ToString().Split(' ')[0]; string ip = ep.Split(':')[0];
                string curName = peerNames.ContainsKey(ip) ? peerNames[ip].Split('|')[0] : "";
                Form pForm = new Form() { Width = 300, Height = 130, FormBorderStyle = FormBorderStyle.FixedDialog, Text = "Set Peer Name", StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
                TextBox tb = new TextBox() { Left = 20, Top = 20, Width = 240, Text = curName };
                Button okBtn = new Button() { Text = "OK", Left = 160, Top = 50, Width = 100, DialogResult = DialogResult.OK };
                pForm.Controls.Add(tb); pForm.Controls.Add(okBtn); pForm.AcceptButton = okBtn;
                if (pForm.ShowDialog() == DialogResult.OK) {
                    string newName = tb.Text.Trim();
                    long ts = DateTime.UtcNow.Ticks;
                    if (string.IsNullOrEmpty(newName)) { peerNames.Remove(ip); try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SwiftShare\PeerNames")) { key.DeleteValue(ip, false); } } catch {} }
                    else { peerNames[ip] = newName + "|" + ts; try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SwiftShare\PeerNames")) { key.SetValue(ip, peerNames[ip]); } } catch {} }
                    peerList.Invalidate();
                    BroadcastAliasUpdate(ip, newName, ts);
                }
            };
            peerMenu.Items.Add(renameItem);
            peerList.ContextMenuStrip = peerMenu;
            peerList.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) { int idx = peerList.IndexFromPoint(e.Location); if (idx != ListBox.NoMatches) peerList.SelectedIndex = idx; } };
            sidebar.Controls.Add(peerList);

            dashboard = new Panel();
            dashboard.Dock = DockStyle.Fill;
            dashboard.Padding = new Padding(15);
            dashboard.AllowDrop = true;
            rootLayout.Controls.Add(dashboard, 1, 0);

            splitMain = new SplitContainer();
            splitMain.Orientation = Orientation.Horizontal;
            splitMain.Dock = DockStyle.Fill;
            splitMain.SplitterDistance = 670;
            dashboard.Controls.Add(splitMain);

            Panel explorerCard = CreateCard("FILE EXPLORER", 0, 0, splitMain.Panel1.Width, splitMain.Panel1.Height);
            explorerCard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            splitMain.Panel1.Controls.Add(explorerCard);

            splitExplorer = new SplitContainer();
            splitExplorer.Location = new Point(10, 35);
            splitExplorer.Size = new Size(explorerCard.Width - 20, explorerCard.Height - 45);
            splitExplorer.SplitterDistance = splitExplorer.Width / 2;
            splitExplorer.IsSplitterFixed = true;
            splitExplorer.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            splitExplorer.Resize += (s, e) => { if (splitExplorer.Width > 0) splitExplorer.SplitterDistance = splitExplorer.Width / 2; };
            explorerCard.Controls.Add(splitExplorer);

            // Left Pane (Local)
            Label lblLocal = new Label() { Text = "Local:", Location = new Point(5, 7), AutoSize = true, Font = new Font("Segoe UI Semibold", 9) };
            txtLocal = new TextBox() { Location = new Point(55, 5), Width = splitExplorer.Panel1.Width - 140, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            txtLocal.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { RefreshLocalList(txtLocal.Text); e.Handled = true; e.SuppressKeyPress = true; } };
            btnLocalUp = new Button() { Text = "↑", Location = new Point(splitExplorer.Panel1.Width - 80, 4), Size = new Size(35, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnLocalUp.Click += (s, e) => {
                try {
                    if (string.IsNullOrEmpty(txtLocal.Text)) return;
                    DirectoryInfo parentDir = Directory.GetParent(txtLocal.Text);
                    if (parentDir != null) RefreshLocalList(parentDir.FullName);
                    else RefreshLocalList(""); 
                } catch {}
            };
            btnLocalRefresh = new Button() { Text = "↻", Location = new Point(splitExplorer.Panel1.Width - 40, 4), Size = new Size(35, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnLocalRefresh.Click += (s, e) => RefreshLocalList(txtLocal.Text);
            
            lvLocal = new ListView() { Location = new Point(5, 35), Size = new Size(splitExplorer.Panel1.Width - 10, splitExplorer.Panel1.Height - 80), View = View.Details, FullRowSelect = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Segoe UI", 9.5f) };
            lvLocal.Columns.Add("Name", 200);
            lvLocal.Columns.Add("Date Modified", 130);
            lvLocal.Columns.Add("Type", 150);
            lvLocal.Columns.Add("Size", 80);
            
            ContextMenuStrip localMenu = new ContextMenuStrip();
            ToolStripMenuItem copyLocal = new ToolStripMenuItem("Copy", null, (s, e) => CopyLocalFiles()) { ShortcutKeyDisplayString = "Ctrl+C" };
            ToolStripMenuItem pasteLocal = new ToolStripMenuItem("Paste", null, (s, e) => PasteLocalFiles()) { ShortcutKeyDisplayString = "Ctrl+V" };
            ToolStripMenuItem deleteLocal = new ToolStripMenuItem("Delete", null, (s, e) => DeleteLocalFiles()) { ShortcutKeyDisplayString = "Del" };
            localMenu.Items.AddRange(new ToolStripItem[] { copyLocal, pasteLocal, new ToolStripSeparator(), deleteLocal });
            lvLocal.ContextMenuStrip = localMenu;

            lvLocal.KeyDown += (s, e) => {
                if (e.Control && e.KeyCode == Keys.C) { CopyLocalFiles(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.V) { PasteLocalFiles(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Delete || (e.Control && e.KeyCode == Keys.D)) { DeleteLocalFiles(); e.Handled = true; e.SuppressKeyPress = true; }
            };

            lvLocal.DoubleClick += (s, e) => {
                if (lvLocal.SelectedItems.Count == 0) return;
                var item = lvLocal.SelectedItems[0]; string tag = item.Tag.ToString();
                if (tag == "UP") btnLocalUp.PerformClick();
                else if (tag.StartsWith("DRIVE|")) RefreshLocalList(tag.Split('|')[1]);
                else if (tag == "D") {
                    string nextPath = string.IsNullOrEmpty(txtLocal.Text) ? item.Text : Path.Combine(txtLocal.Text, item.Text);
                    RefreshLocalList(nextPath);
                }
            };
            
            btnUpload = new Button() { Text = "Upload ➔", Location = new Point(5, splitExplorer.Panel1.Height - 40), Size = new Size(splitExplorer.Panel1.Width - 10, 35), BackColor = primaryColor, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            btnUpload.FlatAppearance.BorderSize = 0;
            btnUpload.Click += async (s, e) => {
                if (lvLocal.SelectedItems.Count == 0 || string.IsNullOrEmpty(currentRemotePeer)) {
                    MessageBox.Show("Select a peer and at least one local item."); return;
                }
                if (string.IsNullOrEmpty(currentRemotePath)) {
                    MessageBox.Show("Please select a valid remote directory to upload to.", "Remote Path Required", MessageBoxButtons.OK, MessageBoxIcon.Information); return;
                }
                string[] epParts = currentRemotePeer.Split(':');
                string ip = epParts[0]; int port = int.Parse(epParts[1]);
                List<string> paths = new List<string>();
                foreach(ListViewItem item in lvLocal.SelectedItems) {
                    string tag = item.Tag.ToString();
                    if (tag == "UP") continue;
                    paths.Add(tag.StartsWith("DRIVE|") ? tag.Split('|')[1] : Path.Combine(txtLocal.Text, item.Text));
                }
                await ProcessOutgoingItems(ip, port, paths.ToArray(), txtLocal.Text, currentRemotePath);
            };

            splitExplorer.Panel1.Controls.AddRange(new Control[] { lblLocal, txtLocal, btnLocalUp, btnLocalRefresh, lvLocal, btnUpload });

            // Right Pane (Remote)
            Label lblRemote = new Label() { Text = "Remote:", Location = new Point(5, 7), AutoSize = true, Font = new Font("Segoe UI Semibold", 9) };
            txtRemote = new TextBox() { Location = new Point(65, 5), Width = splitExplorer.Panel2.Width - 150, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            txtRemote.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { currentRemotePath = txtRemote.Text; RefreshRemoteList(); e.Handled = true; e.SuppressKeyPress = true; } };
            btnRemoteUp = new Button() { Text = "↑", Location = new Point(splitExplorer.Panel2.Width - 80, 4), Size = new Size(35, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnRemoteUp.Click += (s, e) => {
                if (string.IsNullOrEmpty(currentRemotePath)) return;
                try {
                    string parent = Path.GetDirectoryName(currentRemotePath);
                    currentRemotePath = string.IsNullOrEmpty(parent) ? "" : parent;
                    RefreshRemoteList();
                } catch {}
            };
            btnRemoteRefresh = new Button() { Text = "↻", Location = new Point(splitExplorer.Panel2.Width - 40, 4), Size = new Size(35, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnRemoteRefresh.Click += (s, e) => RefreshRemoteList();

            lvRemote = new ListView() { Location = new Point(5, 35), Size = new Size(splitExplorer.Panel2.Width - 10, splitExplorer.Panel2.Height - 80), View = View.Details, FullRowSelect = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Segoe UI", 9.5f) };
            lvRemote.Columns.Add("Name", 200);
            lvRemote.Columns.Add("Date Modified", 130);
            lvRemote.Columns.Add("Type", 150);
            lvRemote.Columns.Add("Size", 80);

            ContextMenuStrip remoteMenu = new ContextMenuStrip();
            ToolStripMenuItem copyRemote = new ToolStripMenuItem("Copy", null, (s, e) => CopyRemoteFiles()) { ShortcutKeyDisplayString = "Ctrl+C" };
            ToolStripMenuItem pasteRemote = new ToolStripMenuItem("Paste", null, (s, e) => PasteRemoteFiles()) { ShortcutKeyDisplayString = "Ctrl+V" };
            ToolStripMenuItem downloadRemote = new ToolStripMenuItem("Download", null, (s, e) => btnDownload.PerformClick());
            ToolStripMenuItem deleteRemote = new ToolStripMenuItem("Delete", null, (s, e) => DeleteRemoteFiles()) { ShortcutKeyDisplayString = "Del" };
            remoteMenu.Items.AddRange(new ToolStripItem[] { copyRemote, pasteRemote, new ToolStripSeparator(), downloadRemote, deleteRemote });
            lvRemote.ContextMenuStrip = remoteMenu;

            lvRemote.KeyDown += (s, e) => {
                if (e.Control && e.KeyCode == Keys.C) { CopyRemoteFiles(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.V) { PasteRemoteFiles(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Delete || (e.Control && e.KeyCode == Keys.D)) { DeleteRemoteFiles(); e.Handled = true; e.SuppressKeyPress = true; }
            };

            lvRemote.DoubleClick += (s, e) => {
                if (lvRemote.SelectedItems.Count == 0) return;
                var item = lvRemote.SelectedItems[0]; string tag = item.Tag.ToString();
                if (tag == "UP") btnRemoteUp.PerformClick();
                else if (tag.StartsWith("DRIVE|")) {
                    currentRemotePath = tag.Split('|')[1];
                    RefreshRemoteList();
                }
                else if (tag == "D") {
                    currentRemotePath = string.IsNullOrEmpty(currentRemotePath) ? item.Text : Path.Combine(currentRemotePath, item.Text);
                    RefreshRemoteList();
                }
            };

            btnDownload = new Button() { Text = "⇦ Download", Location = new Point(5, splitExplorer.Panel2.Height - 40), Size = new Size(splitExplorer.Panel2.Width - 10, 35), BackColor = Color.FromArgb(46, 125, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            btnDownload.FlatAppearance.BorderSize = 0;
            btnDownload.Click += async (s, e) => {
                if (lvRemote.SelectedItems.Count == 0 || string.IsNullOrEmpty(currentRemotePeer)) {
                    MessageBox.Show("Select a peer and at least one remote item."); return;
                }
                if (string.IsNullOrEmpty(txtLocal.Text)) {
                    MessageBox.Show("Please select a valid local directory to download to.", "Local Path Required", MessageBoxButtons.OK, MessageBoxIcon.Information); return;
                }
                string[] epParts = currentRemotePeer.Split(':');
                string ip = epParts[0]; int port = int.Parse(epParts[1]);
                List<string> remotePaths = new List<string>();
                foreach(ListViewItem item in lvRemote.SelectedItems) {
                    string tag = item.Tag.ToString();
                    if (tag == "UP") continue;
                    remotePaths.Add(tag.StartsWith("DRIVE|") ? tag.Split('|')[1] : (string.IsNullOrEmpty(currentRemotePath) ? item.Text : Path.Combine(currentRemotePath, item.Text)));
                }
                await RequestPullItems(ip, port, remotePaths.ToArray(), txtLocal.Text);
            };

            splitExplorer.Panel2.Controls.AddRange(new Control[] { lblRemote, txtRemote, btnRemoteUp, btnRemoteRefresh, lvRemote, btnDownload });

            // History Card
            Panel historyCard = CreateCard("RECENT ACTIVITY", 0, 10, splitMain.Panel2.Width, splitMain.Panel2.Height - 10);
            historyCard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            splitMain.Panel2.Controls.Add(historyCard);

            historyFlow = new FlowLayoutPanel();
            historyFlow.Location = new Point(10, 40);
            historyFlow.Size = new Size(historyCard.Width - 20, historyCard.Height - 45);
            historyFlow.AutoScroll = true;
            historyFlow.FlowDirection = FlowDirection.TopDown;
            historyFlow.WrapContents = false;
            historyFlow.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            historyFlow.Resize += (s, e) => {
                foreach (Control c in historyFlow.Controls) {
                    c.Width = historyFlow.ClientSize.Width - 25; // Leave room for scrollbar
                }
            };
            historyFlow.MouseEnter += (s, e) => historyFlow.Focus();
            historyCard.Controls.Add(historyFlow);

            SetupDragDrop(lvLocal, lvRemote);

            imageList = new ImageList();
            imageList.ColorDepth = ColorDepth.Depth32Bit;
            imageList.ImageSize = new Size(16, 16);
            lvLocal.SmallImageList = imageList;
            lvRemote.SmallImageList = imageList;

            lvLocal.ColumnClick += (s, e) => SortListView(lvLocal, e.Column);
            lvRemote.ColumnClick += (s, e) => SortListView(lvRemote, e.Column);
        }

        private void SortListView(ListView lv, int column)
        {
            ListViewItemComparer sorter = lv.ListViewItemSorter as ListViewItemComparer;
            if (sorter != null && sorter.Column == column) {
                sorter.Order = (sorter.Order == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;
            } else {
                lv.ListViewItemSorter = new ListViewItemComparer(column, SortOrder.Ascending);
            }
            lv.Sort();
        }

        private string GetTypeName(string path, bool isFolder, bool useAttributes = true)
        {
            string key = isFolder ? "folder" : Path.GetExtension(path).ToLower();
            if (string.IsNullOrEmpty(key)) key = ".unknown";
            if (!typeCache.ContainsKey(key)) {
                try {
                    SHFILEINFO shfi = new SHFILEINFO();
                    uint flags = SHGFI_TYPENAME;
                    if (useAttributes) flags |= SHGFI_USEFILEATTRIBUTES;
                    uint attributes = isFolder ? (uint)0x10 : (uint)0x80;
                    if (SHGetFileInfo(path, attributes, ref shfi, (uint)Marshal.SizeOf(shfi), flags) != IntPtr.Zero) {
                        typeCache[key] = shfi.szTypeName;
                    }
                } catch { }
            }
            return typeCache.ContainsKey(key) ? typeCache[key] : (isFolder ? "File folder" : "File");
        }

        private int GetIconIndex(string path, bool isFolder, bool useAttributes = true)
        {
            string key = isFolder ? (useAttributes ? "folder" : "drive_" + path) : Path.GetExtension(path).ToLower();
            if (string.IsNullOrEmpty(key)) key = ".unknown";
            if (!imageList.Images.ContainsKey(key))
            {
                try {
                    SHFILEINFO shfi = new SHFILEINFO();
                    uint flags = SHGFI_ICON | SHGFI_SMALLICON;
                    if (useAttributes) flags |= SHGFI_USEFILEATTRIBUTES;
                    uint attributes = isFolder ? (uint)0x10 : (uint)0x80;
                    if (SHGetFileInfo(path, attributes, ref shfi, (uint)Marshal.SizeOf(shfi), flags) != IntPtr.Zero) {
                        if (shfi.hIcon != IntPtr.Zero) {
                            Icon icon = (Icon)Icon.FromHandle(shfi.hIcon).Clone();
                            DestroyIcon(shfi.hIcon);
                            imageList.Images.Add(key, icon);
                        }
                    }
                } catch { }
            }
            return imageList.Images.IndexOfKey(key);
        }

        private void CopyLocalFiles()
        {
            if (lvLocal.SelectedItems.Count == 0 || string.IsNullOrEmpty(txtLocal.Text)) return;
            StringCollection paths = new StringCollection();
            foreach (ListViewItem item in lvLocal.SelectedItems) {
                string tag = item.Tag.ToString();
                if (tag == "UP") continue;
                paths.Add(tag.StartsWith("DRIVE|") ? tag.Split('|')[1] : Path.Combine(txtLocal.Text, item.Text));
            }
            if (paths.Count > 0) Clipboard.SetFileDropList(paths);
            remoteClipboardPaths.Clear();
        }

        private void PasteLocalFiles()
        {
            if (remoteClipboardPaths.Count > 0 && !string.IsNullOrEmpty(remoteClipboardPeer)) {
                if (string.IsNullOrEmpty(txtLocal.Text)) {
                    MessageBox.Show("Please select a valid local directory to download to."); return;
                }
                string[] epParts = remoteClipboardPeer.Split(':');
                RequestPullItems(epParts[0], int.Parse(epParts[1]), remoteClipboardPaths.ToArray(), txtLocal.Text);
                return;
            }
            if (!Clipboard.ContainsFileDropList() || string.IsNullOrEmpty(txtLocal.Text)) return;
            StringCollection paths = Clipboard.GetFileDropList();
            foreach (string path in paths) {
                try {
                    string dest = Path.Combine(txtLocal.Text, Path.GetFileName(path));
                    if (File.Exists(path)) File.Copy(path, dest, true);
                    else if (Directory.Exists(path)) CopyDirectory(path, dest);
                } catch (Exception ex) { MessageBox.Show("Paste error: " + ex.Message); }
            }
            RefreshLocalList(txtLocal.Text);
        }

        private void CopyRemoteFiles()
        {
            if (lvRemote.SelectedItems.Count == 0 || string.IsNullOrEmpty(currentRemotePeer)) return;
            remoteClipboardPaths.Clear();
            foreach (ListViewItem item in lvRemote.SelectedItems) {
                string tag = item.Tag.ToString();
                if (tag == "UP") continue;
                remoteClipboardPaths.Add(tag.StartsWith("DRIVE|") ? tag.Split('|')[1] : (string.IsNullOrEmpty(currentRemotePath) ? item.Text : Path.Combine(currentRemotePath, item.Text)));
            }
            remoteClipboardPeer = currentRemotePeer;
            Clipboard.Clear();
        }

        private async void PasteRemoteFiles()
        {
            if (Clipboard.ContainsFileDropList()) {
                if (string.IsNullOrEmpty(currentRemotePeer) || string.IsNullOrEmpty(currentRemotePath)) {
                    MessageBox.Show("Please select a valid remote directory to upload to."); return;
                }
                StringCollection paths = Clipboard.GetFileDropList();
                string[] pathArr = new string[paths.Count];
                paths.CopyTo(pathArr, 0);
                string[] epParts = currentRemotePeer.Split(':');
                await ProcessOutgoingItems(epParts[0], int.Parse(epParts[1]), pathArr, "", currentRemotePath);
            }
        }

        private void CopyDirectory(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(src)) File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(src)) CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }

        private void DeleteLocalFiles()
        {
            if (lvLocal.SelectedItems.Count == 0 || string.IsNullOrEmpty(txtLocal.Text)) return;
            if (MessageBox.Show("Delete selected local files?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
                foreach (ListViewItem item in lvLocal.SelectedItems) {
                    string tag = item.Tag.ToString();
                    if (tag == "UP") continue;
                    string p = tag.StartsWith("DRIVE|") ? tag.Split('|')[1] : Path.Combine(txtLocal.Text, item.Text);
                    try {
                        if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); }
                        else if (Directory.Exists(p)) Directory.Delete(p, true);
                    } catch {}
                }
                RefreshLocalList(txtLocal.Text);
            }
        }

        private async void DeleteRemoteFiles()
        {
            if (lvRemote.SelectedItems.Count == 0 || string.IsNullOrEmpty(currentRemotePeer)) return;
            if (MessageBox.Show("Delete selected remote files?", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
                string[] epParts = currentRemotePeer.Split(':');
                string ip = epParts[0]; int port = int.Parse(epParts[1]);
                try {
                    using (TcpClient client = new TcpClient()) {
                        await client.ConnectAsync(ip, port);
                        using (NetworkStream ns = client.GetStream()) {
                            StringBuilder sb = new StringBuilder();
                            foreach (ListViewItem item in lvRemote.SelectedItems) {
                                string tag = item.Tag.ToString();
                                if (tag == "UP") continue;
                                string p = tag.StartsWith("DRIVE|") ? tag.Split('|')[1] : (string.IsNullOrEmpty(currentRemotePath) ? item.Text : Path.Combine(currentRemotePath, item.Text));
                                sb.Append(p).Append(";");
                            }
                            await SendCommandAsync(ns, "TASK_REMOTE_DELETE|" + sb.ToString());
                        }
                    }
                    await Task.Delay(500); RefreshRemoteList();
                } catch (Exception ex) { MessageBox.Show("Remote delete error: " + ex.Message); }
            }
        }

        private bool IsAutoLaunchEnabled() {
            try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false)) { return key != null && key.GetValue("SwiftShare") != null; } } catch { return false; }
        }

        private void SetAutoLaunch(bool enable) {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)) {
                    if (enable) key.SetValue("SwiftShare", "\"" + Application.ExecutablePath + "\" /background");
                    else if (key != null && key.GetValue("SwiftShare") != null) key.DeleteValue("SwiftShare");
                }
            } catch (Exception ex) { MessageBox.Show("Failed to set auto-launch: " + ex.Message); }
        }

        private void SetupDragDrop(Control local, Control remote)
        {
            lvLocal.AllowDrop = true; lvRemote.AllowDrop = true;
            
            lvLocal.ItemDrag += (s, e) => { if (lvLocal.SelectedItems.Count > 0) lvLocal.DoDragDrop("LOCAL_ITEMS", DragDropEffects.Copy); };
            lvRemote.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.StringFormat) && e.Data.GetData(DataFormats.StringFormat).ToString() == "LOCAL_ITEMS") e.Effect = DragDropEffects.Copy; };
            lvRemote.DragDrop += (s, e) => { if (e.Data.GetDataPresent(DataFormats.StringFormat) && e.Data.GetData(DataFormats.StringFormat).ToString() == "LOCAL_ITEMS") btnUpload.PerformClick(); };

            lvRemote.ItemDrag += (s, e) => { if (lvRemote.SelectedItems.Count > 0) lvRemote.DoDragDrop("REMOTE_ITEMS", DragDropEffects.Copy); };
            lvLocal.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.StringFormat) && e.Data.GetData(DataFormats.StringFormat).ToString() == "REMOTE_ITEMS") e.Effect = DragDropEffects.Copy; };
            lvLocal.DragDrop += (s, e) => { if (e.Data.GetDataPresent(DataFormats.StringFormat) && e.Data.GetData(DataFormats.StringFormat).ToString() == "REMOTE_ITEMS") btnDownload.PerformClick(); };
            
            this.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            this.DragDrop += async (s, e) => {
                string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (string.IsNullOrEmpty(currentRemotePeer) || string.IsNullOrEmpty(currentRemotePath)) return;
                string[] epParts = currentRemotePeer.Split(':');
                await ProcessOutgoingItems(epParts[0], int.Parse(epParts[1]), paths, "", currentRemotePath);
            };
        }

        private Panel CreateCard(string title, int x, int y, int w, int h)
        {
            Panel card = new Panel() { Location = new Point(x, y), Size = new Size(w, h), BackColor = cardColor };
            Label lbl = new Label() { Text = title.ToUpper(), Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.Silver, Location = new Point(20, 10), AutoSize = true };
            card.Controls.Add(lbl); return card;
        }

        private void PeerList_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush brush = new SolidBrush(isSelected ? primaryColor : sidebarColor)) e.Graphics.FillRectangle(brush, e.Bounds);
            string fullText = peerList.Items[e.Index].ToString(); string[] parts = fullText.Split(new string[] { " (" }, StringSplitOptions.None);
            string ip = parts[0].Split(':')[0];
            string displayName = peerNames.ContainsKey(ip) ? peerNames[ip].Split('|')[0] : parts[0];
            e.Graphics.DrawString(displayName, new Font("Segoe UI Semibold", 10), Brushes.White, e.Bounds.X + 20, e.Bounds.Y + 8);
            
            string subText = (peerNames.ContainsKey(ip) ? parts[0] + " - " : "") + (parts.Length > 1 ? parts[1].Replace(")", "") : "");
            if (!string.IsNullOrEmpty(subText)) { e.Graphics.DrawString(subText, new Font("Segoe UI", 8), Brushes.LightGray, e.Bounds.X + 20, e.Bounds.Y + 30); }
        }

        private void RefreshLocalList(string path)
        {
            SafeInvoke(() => {
                try {
                    txtLocal.Text = path; lvLocal.Items.Clear();
                    if (string.IsNullOrEmpty(path)) { 
                        foreach (var drive in DriveInfo.GetDrives()) { 
                            string vol = ""; try { if (drive.IsReady) vol = drive.VolumeLabel; } catch {}
                            string name = string.IsNullOrEmpty(vol) ? drive.Name : vol + " (" + drive.Name.TrimEnd('\\') + ")";
                            ListViewItem item = new ListViewItem(name); item.SubItems.Add(""); var tSi = item.SubItems.Add(GetTypeName(drive.Name, true, false)); tSi.Font = typeFont; item.SubItems.Add(""); item.Tag = "DRIVE|" + drive.Name; item.ImageIndex = GetIconIndex(drive.Name, true, false); item.UseItemStyleForSubItems = false; lvLocal.Items.Add(item); 
                        } 
                    }
                    else {
                        if (!Directory.Exists(path)) return;
                        DirectoryInfo di = new DirectoryInfo(path); ListViewItem up = new ListViewItem(".."); up.SubItems.Add(""); var tSiUp = up.SubItems.Add("File folder"); tSiUp.Font = typeFont; up.SubItems.Add(""); up.Tag = "UP"; up.ImageIndex = GetIconIndex(path, true); up.UseItemStyleForSubItems = false; lvLocal.Items.Add(up);
                        foreach(var d in di.GetDirectories()) { try { if ((d.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue; ListViewItem item = new ListViewItem(d.Name); item.SubItems.Add(d.LastWriteTime.ToString("yyyy/MM/dd HH:mm")); var tSi = item.SubItems.Add(GetTypeName(d.FullName, true)); tSi.Font = typeFont; item.SubItems.Add(""); item.Tag = "D"; item.ImageIndex = GetIconIndex(d.FullName, true); item.UseItemStyleForSubItems = false; lvLocal.Items.Add(item); } catch {} }
                        foreach(var f in di.GetFiles()) { try { if ((f.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue; ListViewItem item = new ListViewItem(f.Name); item.SubItems.Add(f.LastWriteTime.ToString("yyyy/MM/dd HH:mm")); var tSi = item.SubItems.Add(GetTypeName(f.FullName, false)); tSi.Font = typeFont; item.SubItems.Add(FormatSize(f.Length)); item.Tag = "F"; item.ImageIndex = GetIconIndex(f.FullName, false); item.UseItemStyleForSubItems = false; lvLocal.Items.Add(item); } catch {} }
                    }
                } catch (Exception ex) { MessageBox.Show("Cannot access local path: " + ex.Message); }
            });
        }

        private async void RefreshRemoteList()
        {
            if (string.IsNullOrEmpty(currentRemotePeer)) return;
            try {
                string[] epParts = currentRemotePeer.Split(':'); string ip = epParts[0]; int port = int.Parse(epParts[1]);
                using (TcpClient client = new TcpClient()) {
                    await client.ConnectAsync(ip, port);
                    using (NetworkStream ns = client.GetStream()) {
                        await SendCommandAsync(ns, "LIST|" + currentRemotePath);
                        byte[] lenBuf = new byte[4]; await ReadFullAsync(ns, lenBuf, 4); int resLen = BitConverter.ToInt32(lenBuf, 0);
                        if (resLen == 0) { SafeInvoke(() => { lvRemote.Items.Clear(); txtRemote.Text = currentRemotePath; }); return; }
                        byte[] resBuf = new byte[resLen]; await ReadFullAsync(ns, resBuf, resLen); string resStr = Encoding.UTF8.GetString(resBuf);
                        SafeInvoke(() => {
                            lvRemote.Items.Clear(); txtRemote.Text = currentRemotePath;
                            if (!string.IsNullOrEmpty(currentRemotePath)) { ListViewItem up = new ListViewItem(".."); up.SubItems.Add(""); var tSiUp = up.SubItems.Add("File folder"); tSiUp.Font = typeFont; up.SubItems.Add(""); up.Tag = "UP"; up.ImageIndex = GetIconIndex(currentRemotePath, true); up.UseItemStyleForSubItems = false; lvRemote.Items.Add(up); }
                            string[] lines = resStr.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach(string line in lines) {
                                string[] parts = line.Split('|'); string type = parts[0]; 
                                if (type == "DRIVE") {
                                    string name = parts[1]; string realPath = parts[2]; string typeName = parts.Length > 5 ? parts[5] : "Drive";
                                    ListViewItem item = new ListViewItem(name); item.SubItems.Add(""); var tSi = item.SubItems.Add(typeName); tSi.Font = typeFont; item.SubItems.Add(""); item.Tag = "DRIVE|" + realPath; item.ImageIndex = GetIconIndex(realPath, true, false); item.UseItemStyleForSubItems = false; lvRemote.Items.Add(item);
                                } else {
                                    string name = parts[1]; string size = parts.Length > 2 ? parts[2] : ""; string ticks = parts.Length > 3 ? parts[3] : ""; string typeName = parts.Length > 4 ? parts[4] : (type == "D" ? (string.IsNullOrEmpty(currentRemotePath) ? "Drive" : "File folder") : "File");
                                    string dateStr = ""; if (!string.IsNullOrEmpty(ticks)) { try { dateStr = new DateTime(long.Parse(ticks)).ToString("yyyy/MM/dd HH:mm"); } catch {} }
                                    ListViewItem item = new ListViewItem(name); item.SubItems.Add(dateStr); var tSi = item.SubItems.Add(typeName); tSi.Font = typeFont; item.SubItems.Add(type == "D" ? "" : FormatSize(long.Parse(size))); item.Tag = type; item.ImageIndex = GetIconIndex(name, type == "D"); item.UseItemStyleForSubItems = false; lvRemote.Items.Add(item);
                                }
                            }
                        });
                    }
                }
            } catch { SafeInvoke(() => { lvRemote.Items.Clear(); txtRemote.Text = "Connection Failed"; }); }
        }

        private string FormatSize(long bytes) { if (bytes < 1024) return bytes + " B"; if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB"; if (bytes < 1024 * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB"; return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB"; }
        private async Task SendCommandAsync(NetworkStream ns, string cmdStr) { byte[] cmdBytes = Encoding.UTF8.GetBytes(cmdStr); byte[] lenBytes = BitConverter.GetBytes(cmdBytes.Length); await ns.WriteAsync(lenBytes, 0, 4); await ns.WriteAsync(cmdBytes, 0, cmdBytes.Length); }
        private async Task<string> ReadCommandAsync(NetworkStream ns) { byte[] lenBuf = new byte[4]; await ReadFullAsync(ns, lenBuf, 4); int len = BitConverter.ToInt32(lenBuf, 0); if (len <= 0 || len > 67108864) throw new Exception("Invalid command length: " + len); byte[] cmdBuf = new byte[len]; await ReadFullAsync(ns, cmdBuf, len); return Encoding.UTF8.GetString(cmdBuf); }

        private async Task RequestPullItems(string ip, int port, string[] remotePaths, string localDestDir)
        {
            try {
                using (TcpClient client = new TcpClient()) {
                    await client.ConnectAsync(ip, port);
                    using (NetworkStream ns = client.GetStream()) {
                        StringBuilder sb = new StringBuilder();
                        foreach(var p in remotePaths) sb.Append(p).Append(";");
                        await SendCommandAsync(ns, "TASK_PULL|" + actualTcpPort + "|" + localDestDir + "|" + sb.ToString());
                    }
                }
            } catch (Exception ex) { MessageBox.Show("Download request failed: " + ex.Message); }
        }

        private async Task ProcessOutgoingItems(string ip, int port, string[] paths, string baseDir, string remoteDestDir)
        {
            TransferTask task = new TransferTask { 
                TaskName = paths.Length == 1 ? Path.GetFileName(paths[0]) : paths.Length + " items", 
                Direction = "OUT", RemoteIP = ip, RemotePort = port 
            };
            task.LocalBaseDir = baseDir; 
            SafeInvoke(() => { CreateTaskCard(task); task.StatusLbl.Text = "Status: Initializing Batch Stream..."; });

            try { 
                using (TcpClient client = new TcpClient()) {
                    client.NoDelay = true;
                    client.SendBufferSize = 33554432;
                    await client.ConnectAsync(ip, port);
                    using (NetworkStream ns = client.GetStream()) {
                        await SendCommandAsync(ns, "TASK_START|" + task.TaskName + "|0|0|" + task.TaskId + "|" + actualTcpPort); 

                        Stopwatch sw = new Stopwatch(); sw.Start();
                        long processedFiles = 0;

                        foreach (string path in paths) {
                            if (task.IsCancelled) break;
                            if (File.Exists(path)) {
                                await StreamSingleFilePersistent(ns, path, Path.Combine(remoteDestDir, Path.GetFileName(path)), task, sw);
                                processedFiles++;
                            } else if (Directory.Exists(path)) {
                                string rootDir = Path.GetDirectoryName(path);
                                if (!rootDir.EndsWith(Path.DirectorySeparatorChar.ToString())) rootDir += Path.DirectorySeparatorChar;
                                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) {
                                    if (task.IsCancelled) break;
                                    string relPath = file.Substring(rootDir.Length).Replace("\\", "/");
                                    await StreamSingleFilePersistent(ns, file, Path.Combine(remoteDestDir, relPath), task, sw);
                                    processedFiles++;
                                    if (processedFiles % 500 == 0) {
                                        SafeInvoke(() => { task.StatusLbl.Text = "Status: Sending " + processedFiles + " files..."; });
                                    }
                                }
                            }
                        }

                        if (!task.IsCancelled) { 
                            await SendCommandAsync(ns, "TASK_END|" + task.TaskId);
                            await Task.Delay(300);
                        }
                    }
                }
            } catch (Exception ex) { task.CompleteTask("Failed: " + ex.Message); return; }

            task.CompleteTask(task.IsCancelled ? "Cancelled" : "Completed");
            SafeInvoke(() => { RefreshRemoteList(); });
        }

        private async Task StreamSingleFilePersistent(NetworkStream ns, string localPath, string remotePath, TransferTask task, Stopwatch sw)
        {
            try {
                long fsLen = new FileInfo(localPath).Length;
                await SendCommandAsync(ns, "PUSH|" + remotePath + "|" + fsLen + "|" + task.TaskId);
                if (fsLen > 0) {
                    using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4194304, FileOptions.SequentialScan | FileOptions.Asynchronous)) { 
                        byte[] buf1 = new byte[2097152]; byte[] buf2 = new byte[2097152]; 
                        byte[] rB = buf1; byte[] wB = buf2; Task wT = Task.Delay(0);
                        int r = await fs.ReadAsync(rB, 0, rB.Length);
                        while (r > 0) {
                            if (task.IsCancelled) break;
                            while (task.IsPaused && !task.IsCancelled) await Task.Delay(200);
                            await wT;
                            byte[] tmp = rB; rB = wB; wB = tmp;
                            wT = ns.WriteAsync(wB, 0, r);
                            System.Threading.Interlocked.Add(ref task.transferredBytesBacking, r);
                            task.UpdateProgress(task.TransferredBytes, (task.TransferredBytes / 1024.0 / 1024.0) / (sw.Elapsed.TotalSeconds + 0.001));
                            r = await fs.ReadAsync(rB, 0, rB.Length);
                        }
                        await wT;
                    }
                }
            } catch { throw; } // Let outer loop handle connection loss
        }

        private void CreateTaskCard(TransferTask task)
        {
            Panel card = new Panel { Height = 70, BackColor = Color.White, Margin = new Padding(0, 0, 0, 10), BorderStyle = BorderStyle.FixedSingle };
            card.MouseEnter += (s, e) => historyFlow.Focus();
            task.Card = card;
            card.Tag = task;
            Label nameLbl = new Label { Text = (task.Direction == "OUT" ? "↗ " : "↘ ") + task.TaskName, Font = new Font("Segoe UI Semibold", 10), Location = new Point(10, 10), Size = new Size(300, 20) };
            card.Controls.Add(nameLbl);
            ProgressBar pb = new ProgressBar { Location = new Point(10, 35), Height = 10 };
            task.Progress = pb; card.Controls.Add(pb);
            Label statusLbl = new Label { Text = "Status: Waiting", Font = new Font("Segoe UI", 8), ForeColor = Color.Gray, Location = new Point(10, 50), Size = new Size(200, 15) };
            task.StatusLbl = statusLbl; card.Controls.Add(statusLbl);
            Label speedLbl = new Label { Text = "0.0 MB/s", Font = new Font("Segoe UI Semibold", 9), ForeColor = primaryColor, Location = new Point(0, 30), Size = new Size(90, 20), TextAlign = ContentAlignment.MiddleRight };
            task.SpeedLbl = speedLbl; card.Controls.Add(speedLbl);
            Button expandBtn = new Button { Text = "Files ▼", Location = new Point(0, 10), Size = new Size(100, 25), FlatStyle = FlatStyle.Flat };
            Button removeBtn = new Button { Text = "Remove", Location = new Point(0, 10), Size = new Size(100, 25), FlatStyle = FlatStyle.Flat, Visible = false };
            Button deleteBtn = new Button { Text = "Delete", Location = new Point(0, 10), Size = new Size(100, 25), FlatStyle = FlatStyle.Flat, Visible = false };
            Button cancelBtn = new Button { Text = "Cancel", Location = new Point(0, 10), Size = new Size(100, 25), FlatStyle = FlatStyle.Flat };
            Button openBtn = new Button { Text = "Open", Location = new Point(0, 10), Size = new Size(100, 25), FlatStyle = FlatStyle.Flat, Visible = false };
            Button pauseBtn = new Button { Text = "Pause", Location = new Point(0, 10), Size = new Size(100, 25), FlatStyle = FlatStyle.Flat };
            card.Resize += (s, e) => { int w = card.ClientSize.Width; expandBtn.Left = w - 110; removeBtn.Left = w - 220; deleteBtn.Left = w - 330; cancelBtn.Left = w - 330; openBtn.Left = w - 440; pauseBtn.Left = w - 440; speedLbl.Left = w - 540; pb.Width = Math.Max(10, speedLbl.Left - 20); if (task.TreePanel != null) task.TreePanel.Width = w - 20; };
            pauseBtn.Click += (s, e) => { task.IsPaused = !task.IsPaused; pauseBtn.Text = task.IsPaused ? "Resume" : "Pause"; statusLbl.Text = task.IsPaused ? "Status: Paused" : "Status: Transferring"; };
            task.PauseBtn = pauseBtn; card.Controls.Add(pauseBtn);
            cancelBtn.Click += (s, e) => { task.IsCancelled = true; };
            task.CancelBtn = cancelBtn; card.Controls.Add(cancelBtn);
            openBtn.Click += (s, e) => { try { if (Directory.Exists(task.LocalBaseDir)) Process.Start("explorer.exe", task.LocalBaseDir); } catch {} };
            task.OpenBtn = openBtn; card.Controls.Add(openBtn);
            deleteBtn.Click += async (s, e) => {
                if (MessageBox.Show("Delete transferred files from disk and remove history on both sides?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                    try {
                        if (task.Direction == "IN") {
                            foreach (var f in task.Files) {
                                if (!string.IsNullOrEmpty(f.DestinationPath) && File.Exists(f.DestinationPath)) {
                                    try { File.SetAttributes(f.DestinationPath, FileAttributes.Normal); File.Delete(f.DestinationPath); } catch {}
                                }
                            }
                            await Task.Delay(500); 
                            SafeInvoke(() => RefreshLocalList(txtLocal.Text)); 
                        } 
                        try {
                            if (task.RemotePort > 0) {
                                using (TcpClient client = new TcpClient()) { 
                                    await client.ConnectAsync(task.RemoteIP, task.RemotePort); 
                                    using (NetworkStream ns = client.GetStream()) { 
                                        await SendCommandAsync(ns, "SYNC_REMOVE_TASK|" + task.TaskId + "|1"); 
                                    } 
                                } 
                            }
                            if (task.Direction == "OUT") { await Task.Delay(800); SafeInvoke(() => RefreshRemoteList()); }
                        } catch {}
                        historyFlow.Controls.Remove(card); card.Dispose(); 
                    } catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
                }
            };
            task.DeleteBtn = deleteBtn; card.Controls.Add(deleteBtn);
            removeBtn.Click += async (s, e) => { 
                try {
                    if (task.RemotePort > 0) {
                        using (TcpClient client = new TcpClient()) { 
                            await client.ConnectAsync(task.RemoteIP, task.RemotePort); 
                            using (NetworkStream ns = client.GetStream()) { 
                                await SendCommandAsync(ns, "SYNC_REMOVE_TASK|" + task.TaskId + "|0"); 
                            } 
                        } 
                    }
                } catch {}
                historyFlow.Controls.Remove(card); card.Dispose(); 
            };
            task.RemoveBtn = removeBtn; card.Controls.Add(removeBtn);
            expandBtn.Click += (s, e) => { if (card.Height == 70) { card.Height = 250; expandBtn.Text = "Files ▲"; if (task.TreePanel == null) PopulateTaskTree(task); } else { card.Height = 70; expandBtn.Text = "Files ▼"; } };
            card.Controls.Add(expandBtn);
            card.Width = historyFlow.ClientSize.Width > 50 ? historyFlow.ClientSize.Width - 25 : 850;
            historyFlow.Controls.Add(card); historyFlow.Controls.SetChildIndex(card, 0);
        }

        private void PopulateTaskTree(TransferTask task)
        {
            Panel pnl = new Panel { Location = new Point(10, 75), Size = new Size(task.Card.ClientSize.Width - 20, 160), BorderStyle = BorderStyle.None, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            TransparentTreeView tv = new TransparentTreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9), ImageList = imageList };
            tv.MouseEnter += (s, e) => tv.Focus();
            task.TreePanel = pnl; task.FileTree = tv; pnl.Controls.Add(tv); task.Card.Controls.Add(pnl);
            foreach (var file in task.Files) {
                string[] parts = file.RelativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                TreeNodeCollection currentNodes = tv.Nodes; TreeNode lastNode = null;
                for (int i = 0; i < parts.Length; i++) {
                    string part = parts[i];
                    bool isFolder = i < parts.Length - 1;
                    TreeNode nextNode = null; foreach (TreeNode node in currentNodes) { if (node.Text.StartsWith(part)) { nextNode = node; break; } }
                    if (nextNode == null) { nextNode = new TreeNode(part); nextNode.ImageIndex = nextNode.SelectedImageIndex = GetIconIndex(part, isFolder); currentNodes.Add(nextNode); }
                    currentNodes = nextNode.Nodes; lastNode = nextNode;
                }
                file.NodeRef = lastNode;
            }
            tv.ExpandAll();
        }

        private async void StartTcpServer()
        {
            try {
                TcpListener listener = new TcpListener(IPAddress.Any, 0); listener.Start(); actualTcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                SafeInvoke(() => { this.Text = "SwiftShare - Port: " + actualTcpPort; if (infoLbl != null) infoLbl.Text = "IP: " + myIpAddress + "\nPort: " + actualTcpPort + "\nLink: " + linkSpeed; });
                while (true) { TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); Task.Run(() => HandleIncomingConnection(client)); }
            } catch { }
        }

        private Dictionary<string, TransferTask> activeInTasks = new Dictionary<string, TransferTask>();

        private async Task HandleIncomingConnection(TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveBufferSize = 33554432; 
            client.SendBufferSize = 33554432;
            Stopwatch sw = new Stopwatch();
            TransferTask taskContext = null;
            try {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;
                using (NetworkStream ns = client.GetStream()) {
                    while (true) {
                        string cmdStr;
                        try { cmdStr = await ReadCommandAsync(ns); } catch { break; } // Connection closed
                        string[] parts = cmdStr.Split('|'); string cmd = parts[0];
                        if (cmd == "LIST") {
                            string reqPath = parts.Length > 1 ? parts[1] : ""; StringBuilder sb = new StringBuilder();
                            if (string.IsNullOrEmpty(reqPath)) { 
                                foreach (var d in DriveInfo.GetDrives()) {
                                    string vol = ""; try { if (d.IsReady) vol = d.VolumeLabel; } catch {}
                                    string name = string.IsNullOrEmpty(vol) ? d.Name : vol + " (" + d.Name.TrimEnd('\\') + ")";
                                    sb.AppendLine("DRIVE|" + name + "|" + d.Name + "|||" + GetTypeName(d.Name, true, false)); 
                                }
                            }
                            else if (Directory.Exists(reqPath)) { foreach (FileSystemInfo fsi in new DirectoryInfo(reqPath).GetFileSystemInfos()) { try { if ((fsi.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue; string typeName = GetTypeName(fsi.FullName, fsi is DirectoryInfo); if (fsi is DirectoryInfo) sb.AppendLine("D|" + fsi.Name + "||" + fsi.LastWriteTime.Ticks + "|" + typeName); else sb.AppendLine("F|" + fsi.Name + "|" + ((FileInfo)fsi).Length + "|" + fsi.LastWriteTime.Ticks + "|" + typeName); } catch {} } }
                            byte[] resBytes = Encoding.UTF8.GetBytes(sb.ToString()); byte[] resLen = BitConverter.GetBytes(resBytes.Length); await ns.WriteAsync(resLen, 0, 4); await ns.WriteAsync(resBytes, 0, resBytes.Length);
                        }
                        else if (cmd == "TASK_START") { 
                            TransferTask t = new TransferTask { TaskName = parts[1], TotalBytes = long.Parse(parts[2]), Direction = "IN", TaskId = parts.Length > 4 ? parts[4] : Guid.NewGuid().ToString(), RemoteIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(), RemotePort = parts.Length > 5 ? int.Parse(parts[5]) : 0 }; 
                            lock(activeInTasks) { activeInTasks[t.TaskId] = t; }
                            SafeInvoke(() => { CreateTaskCard(t); }); 
                        }
                        else if (cmd == "TASK_END") { 
                            string tId = parts.Length > 1 ? parts[1] : "";
                            TransferTask t = null;
                            lock(activeInTasks) { if (activeInTasks.ContainsKey(tId)) { t = activeInTasks[tId]; activeInTasks.Remove(tId); } }
                            if (t != null) t.CompleteTask("Completed"); 
                            SafeInvoke(() => { RefreshLocalList(txtLocal.Text); });
                        }
                        else if (cmd == "SYNC_REMOVE_TASK") {
                            string targetId = parts[1]; bool deleteFiles = parts.Length > 2 && parts[2] == "1";
                            SafeInvoke(async () => {
                                Control targetCard = null; TransferTask targetTask = null;
                                foreach (Control c in historyFlow.Controls) { TransferTask t = c.Tag as TransferTask; if (t != null && t.TaskId == targetId) { targetCard = c; targetTask = t; break; } }
                                if (targetCard != null && targetTask != null) {
                                    if (deleteFiles && targetTask.Direction == "IN") {
                                        foreach (var f in targetTask.Files) {
                                            if (!string.IsNullOrEmpty(f.DestinationPath) && File.Exists(f.DestinationPath)) { try { File.SetAttributes(f.DestinationPath, FileAttributes.Normal); File.Delete(f.DestinationPath); } catch {} }
                                        }
                                        await Task.Delay(500); RefreshLocalList(txtLocal.Text);
                                    }
                                    else if (deleteFiles && targetTask.Direction == "OUT") { await Task.Delay(800); RefreshRemoteList(); }
                                    historyFlow.Controls.Remove(targetCard); targetCard.Dispose();
                                }
                            });
                        }
                        else if (cmd == "TASK_REMOTE_DELETE") { string[] paths = parts[1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries); foreach(var p in paths) { try { if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); } else if (Directory.Exists(p)) Directory.Delete(p, true); } catch {} } SafeInvoke(() => RefreshLocalList(txtLocal.Text)); }
                        else if (cmd == "TASK_PULL") { int cp = int.Parse(parts[1]); string ld = parts[2]; string[] rp = parts[3].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries); string ci = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(); string bd = Path.GetDirectoryName(rp[0]); ProcessOutgoingItems(ci, cp, rp, bd, ld); }
                        else if (cmd == "PUSH") {
                            string relPath = parts[1]; long fs = long.Parse(parts[2]); string tId = parts.Length > 3 ? parts[3] : "";
                            lock(activeInTasks) { if (activeInTasks.ContainsKey(tId)) taskContext = activeInTasks[tId]; }
                            TransferItem item = new TransferItem { RelativePath = relPath, TotalSize = fs, LocalPath = "", DestinationPath = relPath }; 
                            if (taskContext != null) { lock(taskContext.Files) { taskContext.Files.Add(item); } }
                            string saveDir = Path.GetDirectoryName(relPath); if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
                            if (!sw.IsRunning) sw.Start();
                            try {
                                if (fs == 0) { using (File.Create(relPath)) {} }
                                else {
                                    using (FileStream fstream = new FileStream(relPath, FileMode.Create, FileAccess.Write, FileShare.None, 4194304, FileOptions.SequentialScan | FileOptions.Asynchronous)) { 
                                        if (fs > 0) fstream.SetLength(fs);
                                        byte[] buf1 = new byte[2097152]; byte[] buf2 = new byte[2097152]; 
                                        byte[] rB = buf1; byte[] wB = buf2; long total = 0; Task wT = Task.Delay(0);
                                        int r = await ns.ReadAsync(rB, 0, (int)Math.Min((long)rB.Length, fs - total));
                                        while (r > 0) { 
                                            if (taskContext != null) { if (taskContext.IsCancelled) break; while (taskContext.IsPaused && !taskContext.IsCancelled) await Task.Delay(200); } 
                                            await wT;
                                            byte[] tmp = rB; rB = wB; wB = tmp;
                                            wT = fstream.WriteAsync(wB, 0, r);
                                            total += r; 
                                            if (taskContext != null) { 
                                                System.Threading.Interlocked.Add(ref taskContext.transferredBytesBacking, r);
                                                System.Threading.Interlocked.Add(ref item.transferredBytesBacking, r);
                                                double spd = (taskContext.TransferredBytes / 1024.0 / 1024.0) / (sw.Elapsed.TotalSeconds + 0.001); 
                                                taskContext.UpdateProgress(taskContext.TransferredBytes, spd); 
                                            } 
                                            if (total >= fs) break;
                                            r = await ns.ReadAsync(rB, 0, (int)Math.Min((long)rB.Length, fs - total));
                                        } 
                                        await wT;
                                    }
                                }
                            } catch { if (taskContext != null) taskContext.CompleteTask("Write Error"); }
                            SafeInvoke(() => { RefreshLocalList(txtLocal.Text); });
                        }
                        else if (cmd == "EXCHANGE_ALIASES") {
                            string peerData = parts.Length > 1 ? parts[1] : "";
                            MergeAliasSyncString(peerData);
                            string myData = GetAliasSyncString();
                            await SendCommandAsync(ns, "EXCHANGE_ALIASES_REPLY|" + myData);
                        }
                    }
                }
            } catch { SafeInvoke(() => { if (taskContext != null) taskContext.CompleteTask("Error"); }); }
            finally { client.Close(); }
        }

        private async Task ReadFullAsync(Stream s, byte[] buf, int len) { int total = 0; while (total < len) { int r = await s.ReadAsync(buf, total, len - total); if (r == 0) throw new Exception("Closed"); total += r; } }

        private async void BroadcastPresence() { while (true) { try { using (UdpClient udp = new UdpClient()) { udp.EnableBroadcast = true; udp.MulticastLoopback = true; udp.JoinMulticastGroup(IPAddress.Parse(MulticastIp)); byte[] data = Encoding.UTF8.GetBytes("SWIFTSHARE_V1|" + linkSpeed + "|" + actualTcpPort + "|" + instanceId); udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(MulticastIp), UdpPort)); udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort)); } } catch { } await Task.Delay(3000); } }

        private void BroadcastAliasUpdate(string targetIp, string name, long ts)
        {
            try {
                using (UdpClient udp = new UdpClient()) {
                    udp.EnableBroadcast = true;
                    byte[] data = Encoding.UTF8.GetBytes("SYNC_ALIAS|" + targetIp + "|" + name + "|" + ts);
                    udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse(MulticastIp), UdpPort));
                    udp.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort));
                }
            } catch { }
        }

        private string GetAliasSyncString() {
            StringBuilder sb = new StringBuilder();
            lock (peerNames) {
                foreach(var kvp in peerNames) {
                    string[] p = kvp.Value.Split('|');
                    if (p.Length > 1) { sb.Append(kvp.Key).Append(",").Append(p[0]).Append(",").Append(p[1]).Append(";"); }
                }
            }
            return sb.ToString();
        }

        private void MergeAliasSyncString(string data) {
            if (string.IsNullOrEmpty(data)) return;
            SafeInvoke(() => {
                bool updated = false;
                string[] entries = data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach(string entry in entries) {
                    string[] p = entry.Split(',');
                    if (p.Length >= 3) {
                        string ip = p[0]; string name = p[1]; long ts = 0;
                        if (long.TryParse(p[2], out ts)) {
                            bool shouldUpdate = true;
                            lock (peerNames) {
                                if (peerNames.ContainsKey(ip)) {
                                    string[] cur = peerNames[ip].Split('|');
                                    long curTs = cur.Length > 1 ? long.Parse(cur[1]) : 0;
                                    if (ts <= curTs) shouldUpdate = false;
                                }
                                if (shouldUpdate) {
                                    if (string.IsNullOrEmpty(name)) {
                                        peerNames.Remove(ip);
                                        try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SwiftShare\PeerNames")) { key.DeleteValue(ip, false); } } catch {}
                                    } else {
                                        peerNames[ip] = name + "|" + ts;
                                        try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SwiftShare\PeerNames")) { key.SetValue(ip, peerNames[ip]); } } catch {}
                                    }
                                    updated = true;
                                }
                            }
                        }
                    }
                }
                if (updated) peerList.Invalidate();
            });
        }

        private async void InitiateAliasSync(string ip, int port) {
            try {
                using (TcpClient client = new TcpClient()) {
                    await client.ConnectAsync(ip, port);
                    using (NetworkStream ns = client.GetStream()) {
                        string myData = GetAliasSyncString();
                        await SendCommandAsync(ns, "EXCHANGE_ALIASES|" + myData);
                        string reply = await ReadCommandAsync(ns);
                        if (reply.StartsWith("EXCHANGE_ALIASES_REPLY|")) {
                            string replyData = reply.Length > 23 ? reply.Substring(23) : "";
                            MergeAliasSyncString(replyData);
                        }
                    }
                }
            } catch { }
        }

        private async void StartUdpListener() {
            try {
                UdpClient udp = new UdpClient(); udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); udp.Client.ExclusiveAddressUse = false; udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort)); udp.JoinMulticastGroup(IPAddress.Parse(MulticastIp));
                while (true) {
                    UdpReceiveResult res = await udp.ReceiveAsync().ConfigureAwait(false); string msg = Encoding.UTF8.GetString(res.Buffer); string ip = res.RemoteEndPoint.Address.ToString();
                    if (msg.StartsWith("SWIFTSHARE_V1")) {
                        string[] parts = msg.Split('|'); string remoteSpeed = parts[1]; int remotePort = int.Parse(parts[2]); string remoteInstanceId = parts[3];
                        if (remoteInstanceId == this.instanceId) continue;
                        if (localIps.Contains(ip)) ip = "127.0.0.1";
                        string remoteEndPoint = ip + ":" + remotePort; 
                        bool isNew = !peerLastSeen.ContainsKey(remoteEndPoint);
                        peerLastSeen[remoteEndPoint] = DateTime.Now; 
                        string displayText = remoteEndPoint + " (" + remoteSpeed + ")";
                        SafeInvoke(() => { 
                            int existingIndex = -1; for(int i=0; i<peerList.Items.Count; i++) { if (peerList.Items[i].ToString().StartsWith(remoteEndPoint)) { existingIndex = i; break; } } 
                            if (existingIndex >= 0) peerList.Items[existingIndex] = displayText; 
                            else { peerList.Items.Add(displayText); if (isNew) InitiateAliasSync(ip, remotePort); }
                        });
                    }
                    else if (msg.StartsWith("SYNC_ALIAS")) {
                        string[] parts = msg.Split('|');
                        if (parts.Length >= 4) {
                            string targetIp = parts[1]; string newName = parts[2]; long ts = long.Parse(parts[3]);
                            bool shouldUpdate = true;
                            if (peerNames.ContainsKey(targetIp)) {
                                string[] curParts = peerNames[targetIp].Split('|');
                                long curTs = curParts.Length > 1 ? long.Parse(curParts[1]) : 0;
                                if (ts <= curTs) shouldUpdate = false;
                            }
                            if (shouldUpdate) {
                                SafeInvoke(() => {
                                    if (string.IsNullOrEmpty(newName)) {
                                        peerNames.Remove(targetIp);
                                        try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SwiftShare\PeerNames")) { key.DeleteValue(targetIp, false); } } catch {}
                                    } else {
                                        peerNames[targetIp] = newName + "|" + ts;
                                        try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SwiftShare\PeerNames")) { key.SetValue(targetIp, peerNames[targetIp]); } } catch {}
                                    }
                                    peerList.Invalidate();
                                });
                            }
                        }
                    }
                }
            } catch { }
        }


        private async void PeerCleanupLoop() { while (true) { await Task.Delay(3000); SafeInvoke(() => { List<string> toRemove = new List<string>(); foreach (var kvp in peerLastSeen) { if ((DateTime.Now - kvp.Value).TotalSeconds > 10) toRemove.Add(kvp.Key); } foreach (string key in toRemove) { peerLastSeen.Remove(key); for (int i = peerList.Items.Count - 1; i >= 0; i--) { if (peerList.Items[i].ToString().StartsWith(key)) peerList.Items.RemoveAt(i); } if (currentRemotePeer == key) { currentRemotePeer = ""; lvRemote.Items.Clear(); txtRemote.Text = "Peer Disconnected"; } } }); } }

        private void SafeInvoke(Action action) { if (this.IsHandleCreated && !this.IsDisposed) { if (this.InvokeRequired) this.Invoke(action); else action(); } }
    }

    public class TransparentTreeView : TreeView
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A) // WM_MOUSEWHEEL
            {
                Control parent = this.Parent;
                while (parent != null && !(parent is FlowLayoutPanel)) {
                    parent = parent.Parent;
                }
                if (parent != null) {
                    SendMessage(parent.Handle, m.Msg, m.WParam, m.LParam);
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
    }

    public class DarkRenderer : ToolStripProfessionalRenderer { public DarkRenderer() : base(new CustomColorTable()) { } protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Color.White; base.OnRenderItemText(e); } }

    public class ListViewItemComparer : System.Collections.IComparer
    {
        public int Column { get; set; }
        public SortOrder Order { get; set; }
        public ListViewItemComparer(int column, SortOrder order) { Column = column; Order = order; }
        public int Compare(object x, object y)
        {
            ListViewItem itemX = (ListViewItem)x; ListViewItem itemY = (ListViewItem)y;
            if (itemX.Tag.ToString() == "UP") return -1; if (itemY.Tag.ToString() == "UP") return 1;
            int result;
            if (Column == 3) { // Size (Now column 3)
                long sizeX = ParseSize(itemX.SubItems[3].Text); long sizeY = ParseSize(itemY.SubItems[3].Text);
                result = sizeX.CompareTo(sizeY);
            } else if (Column == 1) { // Date Modified
                DateTime dtX, dtY;
                bool hasX = DateTime.TryParse(itemX.SubItems[1].Text, out dtX);
                bool hasY = DateTime.TryParse(itemY.SubItems[1].Text, out dtY);
                if (hasX && hasY) result = dtX.CompareTo(dtY);
                else if (hasX) result = 1; else if (hasY) result = -1;
                else result = 0;
            } else {
                result = string.Compare(itemX.SubItems[Column].Text, itemY.SubItems[Column].Text);
            }
            return (Order == SortOrder.Descending) ? -result : result;
        }
        private long ParseSize(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            string[] parts = text.Split(' '); if (parts.Length < 2) return 0;
            double val; if (!double.TryParse(parts[0], out val)) return 0;
            switch (parts[1]) {
                case "KB": return (long)(val * 1024);
                case "MB": return (long)(val * 1024 * 1024);
                case "GB": return (long)(val * 1024 * 1024 * 1024);
                default: return (long)val;
            }
        }
    }

    public class CustomColorTable : ProfessionalColorTable
 { public override Color MenuStripGradientBegin { get { return Color.FromArgb(45, 45, 45); } } public override Color MenuStripGradientEnd { get { return Color.FromArgb(45, 45, 45); } } public override Color MenuItemSelected { get { return Color.FromArgb(60, 60, 60); } } public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(60, 60, 60); } } public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(60, 60, 60); } } public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(70, 70, 70); } } public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(70, 70, 70); } } public override Color MenuItemBorder { get { return Color.Transparent; } } public override Color MenuBorder { get { return Color.FromArgb(30, 30, 30); } } public override Color ToolStripDropDownBackground { get { return Color.FromArgb(30, 30, 30); } } public override Color SeparatorDark { get { return Color.FromArgb(80, 80, 80); } } public override Color ImageMarginGradientBegin { get { return Color.FromArgb(30, 30, 30); } } public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(30, 30, 30); } } public override Color ImageMarginGradientEnd { get { return Color.FromArgb(30, 30, 30); } } }
    public class TransferTask {
        public string TaskId { get; set; }
        public string TaskName { get; set; } public string Direction { get; set; } public long TotalBytes { get; set; } 
        public long TransferredBytes { get { return System.Threading.Interlocked.Read(ref transferredBytesBacking); } }
        public long transferredBytesBacking; 
        public Stopwatch TransferSw { get; set; }
        public bool IsPaused { get; set; } public bool IsCancelled { get; set; } public bool IsCompleted { get; set; }
        public List<TransferItem> Files { get; set; } public string LocalBaseDir { get; set; } public string RemoteIP { get; set; } public int RemotePort { get; set; }
        public Panel Card { get; set; } public ProgressBar Progress { get; set; } public Label StatusLbl { get; set; } public Label SpeedLbl { get; set; } public Button PauseBtn { get; set; } public Button CancelBtn { get; set; } public Button OpenBtn { get; set; } public Button DeleteBtn { get; set; } public Button RemoveBtn { get; set; } public TreeView FileTree { get; set; } public Panel TreePanel { get; set; }
        private long lastUiUpdateTicks = 0;
        public TransferTask() { Files = new List<TransferItem>(); TaskId = Guid.NewGuid().ToString(); TransferSw = new Stopwatch(); }
        public void UpdateProgress(long totalCurrent, double speedMBs) { 
            if (IsCompleted) return;
            long currentTicks = DateTime.UtcNow.Ticks;
            if (currentTicks - lastUiUpdateTicks < 1000000 && totalCurrent < TotalBytes) return; // Throttle to 10Hz
            lastUiUpdateTicks = currentTicks;
            if (Card != null && !Card.IsDisposed) { 
                Card.BeginInvoke(new MethodInvoker(delegate { 
                    if (StatusLbl != null && StatusLbl.Text.Contains("Waiting")) StatusLbl.Text = "Status: Transferring...";
                    if (Progress != null) { 
                        int p = 0;
                        if (TotalBytes > 0) {
                            long pct = (totalCurrent * 100) / TotalBytes;
                            p = (int)Math.Max(0, Math.Min(100, pct));
                        }
                        Progress.Value = p;
                    } 
                    if (SpeedLbl != null) SpeedLbl.Text = speedMBs.ToString("F1") + " MB/s"; 
                    UpdateTreeNodes(); 
                })); 
            } 
        }
        private void UpdateTreeNodes() { if (FileTree == null) return; lock(Files) { foreach (var item in Files) { if (item.NodeRef != null) { int p = (int)((item.TransferredBytes * 100) / (item.TotalSize > 0 ? item.TotalSize : 1)); string newText = Path.GetFileName(item.RelativePath) + " [" + p + "%]"; if (item.NodeRef.Text != newText) item.NodeRef.Text = newText; UpdateParentNode(item.NodeRef.Parent); } } } }
        private void UpdateParentNode(TreeNode parent) { if (parent == null) return; double totalP = 0; foreach (TreeNode child in parent.Nodes) { string txt = child.Text; int start = txt.LastIndexOf('['); int end = txt.LastIndexOf('%'); if (start >= 0 && end > start) { double p; if (double.TryParse(txt.Substring(start + 1, end - start - 1), out p)) totalP += p; } } int avgP = (int)(totalP / (parent.Nodes.Count > 0 ? parent.Nodes.Count : 1)); string cleanName = parent.Text.Split('[')[0].Trim(); parent.Text = cleanName + " [" + avgP + "%]"; UpdateParentNode(parent.Parent); }
        public void CompleteTask(string status) { 
            IsCompleted = true;
            if (Card != null && !Card.IsDisposed) { Card.BeginInvoke(new MethodInvoker(delegate { if (StatusLbl != null) StatusLbl.Text = "Status: " + status; if (PauseBtn != null) PauseBtn.Visible = false; if (CancelBtn != null) CancelBtn.Visible = false; if (OpenBtn != null) OpenBtn.Visible = (status == "Completed"); if (DeleteBtn != null) DeleteBtn.Visible = (status == "Completed"); if (RemoveBtn != null) RemoveBtn.Visible = true; if (Progress != null) { Progress.Value = 100; Progress.Update(); } if (SpeedLbl != null) SpeedLbl.Text = "---"; UpdateTreeNodes(); })); } 
        }
    }
    public class TransferItem { public string LocalPath { get; set; } public string RelativePath { get; set; } public string DestinationPath { get; set; } public long TotalSize { get; set; } public long TransferredBytes { get { return System.Threading.Interlocked.Read(ref transferredBytesBacking); } } public long transferredBytesBacking; public TreeNode NodeRef { get; set; } }
}