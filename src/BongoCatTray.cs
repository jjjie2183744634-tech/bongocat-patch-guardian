using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class BongoCatTray
{
    private const string TrueExitSignalName = "Local\\BongoCatTray_TrueExit";
    internal static readonly string ChatLogDirectory = GetChatLogDirectory();

    private static string GetChatLogDirectory()
    {
        string overridden = Environment.GetEnvironmentVariable("BONGOCAT_CHAT_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BongoCat聊天记录");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--exit", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using (var signal = System.Threading.EventWaitHandle.OpenExisting(TrueExitSignalName))
                    signal.Set();
                return 0;
            }
            catch { return 3; }
        }

        Process game = FindGame();
        if (game == null)
            return 2;

        bool createdNew;
        using (var mutex = new System.Threading.Mutex(true, "Local\\BongoCatTray_SingleInstance", out createdNew))
        {
            if (!createdNew)
                return 0;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(game));
        }
        return 0;
    }

    private static Process FindGame()
    {
        try
        {
            return Process.GetProcessesByName("BongoCat")
                .Where(p => !p.HasExited)
                .OrderByDescending(p => p.StartTime)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private sealed class TrayContext : ApplicationContext
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_APPWINDOW = 0x00040000L;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint WM_CLOSE = 0x0010;

        private readonly Process _game;
        private readonly NotifyIcon _notifyIcon;
        private readonly Timer _watchTimer;
        private readonly ToolStripMenuItem _showItem;
        private readonly ToolStripMenuItem _hideItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly System.Threading.EventWaitHandle _trueExitSignal;
        private IntPtr _window;
        private bool _trueExitRequested;
        private DateTime _forceExitAt;
        private ChatHistoryForm _historyForm;

        public TrayContext(Process game)
        {
            _game = game;
            bool createdExitSignal;
            _trueExitSignal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, TrueExitSignalName, out createdExitSignal);

            var menu = new ContextMenuStrip();
            var historyItem = new ToolStripMenuItem("聊天记录", null, delegate { ShowChatHistory(); });
            historyItem.Font = new Font(historyItem.Font, FontStyle.Bold);
            var folderItem = new ToolStripMenuItem("打开记录文件夹", null, delegate { OpenChatFolder(); });
            _showItem = new ToolStripMenuItem("显示小猫", null, delegate { ShowCat(); });
            _hideItem = new ToolStripMenuItem("隐藏小猫", null, delegate { HideCat(); });
            _exitItem = new ToolStripMenuItem("退出 Bongo Cat", null, delegate { ExitGame(); });
            menu.Items.Add(historyItem);
            menu.Items.Add(folderItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_showItem);
            menu.Items.Add(_hideItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_exitItem);

            Icon icon = SystemIcons.Application;
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(_game.MainModule.FileName);
                if (extracted != null)
                    icon = extracted;
            }
            catch { }

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "Bongo Cat（聊天记录已开启）",
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += delegate { ShowChatHistory(); };

            // Keep a hidden history window alive so it can notice messages that
            // arrive before the user opens it for the first time.
            _historyForm = new ChatHistoryForm();
            _historyForm.FormClosed += delegate { _historyForm = null; };

            _watchTimer = new Timer { Interval = 250 };
            _watchTimer.Tick += WatchTick;
            _watchTimer.Start();
            WatchTick(null, EventArgs.Empty);
        }

        private void WatchTick(object sender, EventArgs e)
        {
            if (!_trueExitRequested && _trueExitSignal.WaitOne(0))
                ExitGame();

            bool gameExited = IsGameExited();
            if (_trueExitRequested)
            {
                if (gameExited)
                {
                    ExitThread();
                    return;
                }

                if (DateTime.UtcNow >= _forceExitAt)
                {
                    try { _game.Kill(); }
                    catch { }
                }
                return;
            }

            if (gameExited)
            {
                ExitThread();
                return;
            }

            IntPtr found = FindMainWindow((uint)_game.Id);
            if (found != IntPtr.Zero)
            {
                _window = found;
                RemoveTaskbarButton(_window);
            }
            UpdateMenuState();
        }

        private bool IsGameExited()
        {
            try { return _game.HasExited; }
            catch { return true; }
        }

        private void ShowChatHistory()
        {
            if (_historyForm == null || _historyForm.IsDisposed)
            {
                _historyForm = new ChatHistoryForm();
                _historyForm.FormClosed += delegate { _historyForm = null; };
            }
            _historyForm.Show();
            _historyForm.WindowState = FormWindowState.Normal;
            _historyForm.BringToFront();
            _historyForm.Activate();
            _historyForm.RefreshNow();
            _historyForm.MarkRead();
        }

        private static void OpenChatFolder()
        {
            try
            {
                Directory.CreateDirectory(ChatLogDirectory);
                Process.Start("explorer.exe", "\"" + ChatLogDirectory + "\"");
            }
            catch { }
        }

        private void ShowCat()
        {
            if (_window == IntPtr.Zero)
                _window = FindMainWindow((uint)_game.Id);
            if (_window == IntPtr.Zero) return;
            RemoveTaskbarButton(_window);
            ShowWindow(_window, SW_SHOWNOACTIVATE);
            UpdateMenuState();
        }

        private void HideCat()
        {
            if (_window != IntPtr.Zero)
                ShowWindow(_window, SW_HIDE);
            UpdateMenuState();
        }

        private void ExitGame()
        {
            _trueExitRequested = true;
            _forceExitAt = DateTime.UtcNow.AddSeconds(5);
            _showItem.Enabled = false;
            _hideItem.Enabled = false;
            _exitItem.Enabled = false;
            if (IsGameExited())
            {
                ExitThread();
                return;
            }
            if (_window == IntPtr.Zero)
                _window = FindMainWindow((uint)_game.Id);
            if (_window != IntPtr.Zero)
                PostMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private void UpdateMenuState()
        {
            if (_trueExitRequested) return;
            bool visible = _window != IntPtr.Zero && IsWindowVisible(_window);
            _showItem.Enabled = !visible;
            _hideItem.Enabled = visible;
            _exitItem.Enabled = true;
        }

        protected override void ExitThreadCore()
        {
            _watchTimer.Stop();
            _watchTimer.Dispose();
            _trueExitSignal.Dispose();
            if (_historyForm != null && !_historyForm.IsDisposed)
                _historyForm.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            base.ExitThreadCore();
        }

        private static void RemoveTaskbarButton(IntPtr window)
        {
            long exStyle = GetWindowLong(window, GWL_EXSTYLE);
            long wanted = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            if (wanted != exStyle)
            {
                SetWindowLong(window, GWL_EXSTYLE, wanted);
                SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }

        private static IntPtr FindMainWindow(uint pid)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr ignored)
            {
                uint ownerPid;
                GetWindowThreadProcessId(window, out ownerPid);
                if (ownerPid != pid) return true;
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() == "UnityWndClass")
                {
                    result = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr window, int index, int value);

        private static long GetWindowLong(IntPtr window, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(window, index).ToInt64() : GetWindowLong32(window, index);
        }

        private static void SetWindowLong(IntPtr window, int index, long value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(window, index, new IntPtr(value));
            else SetWindowLong32(window, index, unchecked((int)value));
        }
    }

    private sealed class ChatHistoryForm : Form
    {
        private const int MaxLines = 1000;
        private const uint FlashStop = 0;
        private const uint FlashAll = 3;
        private const uint FlashTimerNoForeground = 12;
        private const int ShowMinimizedNoActivate = 7;
        private readonly TextBox _history;
        private readonly TextBox _messageInput;
        private readonly Label _status;
        private readonly Timer _refreshTimer;
        private string _lastText = "";
        private int _lastLineCount;
        private int _unreadCount;
        private bool _historyInitialized;

        public ChatHistoryForm()
        {
            Text = "Bongo Cat 聊天记录";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 520);
            MinimumSize = new Size(520, 320);
            ShowInTaskbar = true;

            _history = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = true,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };

            var sendPanel = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6, 5, 6, 5) };
            var sendLabel = new Label
            {
                Text = "发送到房间：",
                Dock = DockStyle.Left,
                Width = 92,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _messageInput = new TextBox
            {
                Dock = DockStyle.Fill,
                MaxLength = 200,
                Font = new Font("Microsoft YaHei UI", 10f)
            };
            var sendButton = new Button { Text = "发送", Dock = DockStyle.Right, Width = 72 };
            sendButton.Click += delegate { QueueOutgoingMessage(); };
            _messageInput.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    QueueOutgoingMessage();
                }
            };
            sendPanel.Controls.Add(_messageInput);
            sendPanel.Controls.Add(sendButton);
            sendPanel.Controls.Add(sendLabel);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38 };
            _status = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 4, 0)
            };
            var openFolder = new Button { Text = "打开记录文件夹", Dock = DockStyle.Right, Width = 120 };
            var refresh = new Button { Text = "刷新", Dock = DockStyle.Right, Width = 72 };
            openFolder.Click += delegate { OpenChatFolder(); };
            refresh.Click += delegate { RefreshNow(); };
            bottom.Controls.Add(_status);
            bottom.Controls.Add(refresh);
            bottom.Controls.Add(openFolder);

            Controls.Add(_history);
            Controls.Add(sendPanel);
            Controls.Add(bottom);

            _refreshTimer = new Timer { Interval = 1000 };
            _refreshTimer.Tick += delegate { RefreshNow(); };
            _refreshTimer.Start();

            Activated += delegate { MarkRead(); };
            RefreshNow(false);
        }

        private void QueueOutgoingMessage()
        {
            string message = (_messageInput.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (message.Length == 0)
            {
                _status.Text = "请输入要发送的文字。";
                return;
            }

            try
            {
                string outbox = Path.Combine(ChatLogDirectory, "待发送");
                Directory.CreateDirectory(outbox);
                string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N");
                string temporary = Path.Combine(outbox, id + ".tmp");
                string ready = Path.Combine(outbox, id + ".msg");
                File.WriteAllText(temporary, message, new UTF8Encoding(false));
                File.Move(temporary, ready);
                _messageInput.Clear();
                _messageInput.Focus();
                _status.Text = "已提交给游戏，进入房间后会自动发送。";
            }
            catch (Exception ex)
            {
                _status.Text = "提交发送失败：" + ex.Message;
            }
        }

        public void RefreshNow()
        {
            RefreshNow(true);
        }

        private void RefreshNow(bool notifyUnread)
        {
            try
            {
                Directory.CreateDirectory(ChatLogDirectory);
                var newestFirst = Directory.GetFiles(ChatLogDirectory, "聊天记录-*.txt")
                    .OrderByDescending(File.GetLastWriteTime)
                    .ToArray();
                var collectedNewestFirst = new List<string>();
                foreach (string file in newestFirst)
                {
                    string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                    for (int i = lines.Length - 1; i >= 0 && collectedNewestFirst.Count < MaxLines; i--)
                        collectedNewestFirst.Add(lines[i]);
                    if (collectedNewestFirst.Count >= MaxLines)
                        break;
                }
                collectedNewestFirst.Reverse();
                string text = collectedNewestFirst.Count == 0
                    ? "还没有聊天记录。收到新的猫咪文字后会自动显示在这里。"
                    : string.Join(Environment.NewLine, collectedNewestFirst.ToArray());
                if (text != _lastText)
                {
                    int added = Math.Max(1, collectedNewestFirst.Count - _lastLineCount);
                    bool isNewMessage = _historyInitialized && collectedNewestFirst.Count > 0;
                    _lastText = text;
                    _lastLineCount = collectedNewestFirst.Count;
                    _history.Text = text;
                    _history.SelectionStart = _history.TextLength;
                    _history.ScrollToCaret();
                    if (notifyUnread && isNewMessage && !(Visible && WindowState != FormWindowState.Minimized && ContainsFocus))
                        NotifyUnread(added);
                }
                _historyInitialized = true;
                _status.Text = "保存位置：" + ChatLogDirectory + "（显示最近 " + Math.Min(collectedNewestFirst.Count, MaxLines) + " 条）";
            }
            catch (Exception ex)
            {
                _status.Text = "读取记录失败：" + ex.Message;
            }
        }

        private void NotifyUnread(int count)
        {
            _unreadCount += Math.Max(1, count);
            Text = "(" + _unreadCount + " 条未读) Bongo Cat 聊天记录";

            // A hidden form has no taskbar button. Ask Windows directly to show
            // its native window minimized without activating it, then flash it.
            // This avoids stealing keyboard focus from TeamViewer or the game.
            if (!Visible)
            {
                // Show updates WinForms' managed Visible state; the native call
                // immediately enforces minimized/no-activate taskbar behavior.
                WindowState = FormWindowState.Minimized;
                Show();
                ShowWindow(Handle, ShowMinimizedNoActivate);
            }

            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(FlashWindowInfo)),
                Window = Handle,
                Flags = FlashAll | FlashTimerNoForeground,
                Count = uint.MaxValue,
                Timeout = 0
            };
            FlashWindowEx(ref info);
        }

        public void MarkRead()
        {
            if (_unreadCount == 0)
                return;
            _unreadCount = 0;
            Text = "Bongo Cat 聊天记录";
            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(FlashWindowInfo)),
                Window = Handle,
                Flags = FlashStop,
                Count = 0,
                Timeout = 0
            };
            FlashWindowEx(ref info);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private static void OpenChatFolder()
        {
            try
            {
                Directory.CreateDirectory(ChatLogDirectory);
                Process.Start("explorer.exe", "\"" + ChatLogDirectory + "\"");
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _refreshTimer.Dispose();
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashWindowInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashWindowInfo info);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
    }
}
