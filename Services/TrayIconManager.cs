using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AntigravitySidebar.Services
{
    public class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _pinMenuItem;
        private readonly ToolStripMenuItem _startupMenuItem;

        public event Action? OnToggleRequested;
        public event Action? OnNewConversationRequested;
        public event Action? OnChooseDirectoryRequested;
        public event Action<bool>? OnPinToggled;
        public event Action<bool>? OnStartupToggled;
        public event Action? OnRestartRequested;
        public event Action? OnMobileRequested;
        public event Action? OnReviewProjectsRequested;
        public event Action? OnExitRequested;

        public TrayIconManager(string iconPath, bool isPinned, bool isStartup)
        {
            _notifyIcon = new NotifyIcon();

            if (File.Exists(iconPath))
            {
                try
                {
                    _notifyIcon.Icon = new Icon(iconPath);
                }
                catch
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            _notifyIcon.Text = "Levity (Win+Alt+A)";
            _notifyIcon.Visible = true;

            var contextMenu = new ContextMenuStrip();

            var openItem = new ToolStripMenuItem("Open Levity", null, (s, e) => OnToggleRequested?.Invoke());
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.ShortcutKeyDisplayString = "Win+Alt+A";
            contextMenu.Items.Add(openItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            contextMenu.Items.Add(new ToolStripMenuItem("New Conversation", null, (s, e) => OnNewConversationRequested?.Invoke()));
            contextMenu.Items.Add(new ToolStripMenuItem("Change Working Directory...", null, (s, e) => OnChooseDirectoryRequested?.Invoke()));
            contextMenu.Items.Add(new ToolStripMenuItem("Review All Projects...", null, (s, e) => OnReviewProjectsRequested?.Invoke()));
            contextMenu.Items.Add(new ToolStripMenuItem("Connect Phone...", null, (s, e) => OnMobileRequested?.Invoke()));

            _pinMenuItem = new ToolStripMenuItem("Always on Top (Pin)");
            _pinMenuItem.Checked = isPinned;
            _pinMenuItem.Click += (s, e) =>
            {
                _pinMenuItem.Checked = !_pinMenuItem.Checked;
                OnPinToggled?.Invoke(_pinMenuItem.Checked);
            };
            contextMenu.Items.Add(_pinMenuItem);

            _startupMenuItem = new ToolStripMenuItem("Start with Windows");
            _startupMenuItem.Checked = isStartup;
            _startupMenuItem.Click += (s, e) =>
            {
                _startupMenuItem.Checked = !_startupMenuItem.Checked;
                OnStartupToggled?.Invoke(_startupMenuItem.Checked);
            };
            contextMenu.Items.Add(_startupMenuItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            contextMenu.Items.Add(new ToolStripMenuItem("Restart agy Engine", null, (s, e) => OnRestartRequested?.Invoke()));

            contextMenu.Items.Add(new ToolStripSeparator());

            contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => OnExitRequested?.Invoke()));

            _notifyIcon.ContextMenuStrip = contextMenu;

            // Single click toggles sidebar
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    OnToggleRequested?.Invoke();
                }
            };
        }

        public void UpdatePinState(bool isPinned)
        {
            _pinMenuItem.Checked = isPinned;
        }

        public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, icon);
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
