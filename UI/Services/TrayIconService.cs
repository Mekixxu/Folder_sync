using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace FolderSync.UI.Services
{
    /// <summary>
    /// 管理 Windows 托盘图标、菜单与主窗口显示/隐藏行为。
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly Action _showWindowAction;
        private readonly Action _exitApplicationAction;
        private readonly Forms.NotifyIcon _notifyIcon;
        private readonly Forms.ToolStripMenuItem _showMenuItem;
        private readonly Forms.ToolStripMenuItem _exitMenuItem;

        public TrayIconService(Action showWindowAction, Action exitApplicationAction)
        {
            _showWindowAction = showWindowAction ?? throw new ArgumentNullException(nameof(showWindowAction));
            _exitApplicationAction = exitApplicationAction ?? throw new ArgumentNullException(nameof(exitApplicationAction));

            _showMenuItem = new Forms.ToolStripMenuItem();
            _showMenuItem.Click += (_, _) => _showWindowAction();

            _exitMenuItem = new Forms.ToolStripMenuItem();
            _exitMenuItem.Click += (_, _) => _exitApplicationAction();

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(_showMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_exitMenuItem);

            _notifyIcon = new Forms.NotifyIcon
            {
                Visible = false,
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu
            };
            _notifyIcon.DoubleClick += (_, _) => _showWindowAction();

            RefreshText();
        }

        public void RefreshText()
        {
            var application = System.Windows.Application.Current;
            var tooltip = application?.TryFindResource("Tray.Tooltip")?.ToString() ?? "FolderSync Pro";

            _notifyIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            _showMenuItem.Text = application?.TryFindResource("Tray.ShowWindow")?.ToString() ?? "显示主窗口";
            _exitMenuItem.Text = application?.TryFindResource("Tray.Exit")?.ToString() ?? "退出程序";
        }

        public void Show()
        {
            _notifyIcon.Visible = true;
        }

        public void Hide()
        {
            _notifyIcon.Visible = false;
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _showMenuItem.Dispose();
            _exitMenuItem.Dispose();
        }
    }
}
