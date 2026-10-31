using System.Drawing;
using System.Windows;
using MultiWindowSwitcher.Core;
using MultiWindowSwitcher.Models;
using MultiWindowSwitcher.Views;
using Forms = System.Windows.Forms;

namespace MultiWindowSwitcher.Services;

/// <summary>Значок в трее с тёмным меню: показать окна, настройки, обновления, автозапуск, выход.</summary>
public sealed class TrayIcon : IDisposable
{
    private Forms.NotifyIcon? _icon;

    public TrayIcon() => Loc.I.LanguageChanged += () => { if (_icon is not null) _icon.ContextMenuStrip = BuildMenu(); };

    public void Apply(bool enabled)
    {
        if (enabled && _icon is null)
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;

            _icon = new Forms.NotifyIcon
            {
                // кадр ровно под трей (24 px на 150%), иначе Windows ужимает 32 и значок мылится
                Icon = stream is null ? SystemIcons.Application : new Icon(stream, Forms.SystemInformation.SmallIconSize),
                Text = "Multi Window Switcher",
                ContextMenuStrip = BuildMenu(),
                Visible = true,
            };

            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    App.Current.Switcher.ToggleFromTray();
                }
            };
        }
        else if (!enabled && _icon is not null)
        {
            Dispose();
        }
    }

    private static Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.5f),
        };

        menu.Items.Add(Item(Loc.T("TrayShow"), () => App.Current.Switcher.ShowSwitcher(App.Current.Fab.Side, ShowReason.Explicit)));
        menu.Items.Add(Item(Loc.T("TraySettings"), () => App.Current.Switcher.OpenSettings()));
        menu.Items.Add(Item(Loc.T("TrayUpdates"), () => App.Current.Switcher.OpenUpdates()));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var autostart = Item(Loc.T("SetAutostart"), () =>
        {
            Settings.Current.Autostart = !Settings.Current.Autostart;
            Autostart.Apply(Settings.Current.Autostart);
            Settings.Touch();
        });

        // иконку у края можно спрятать и вернуть отсюда же
        var fab = Item(Loc.T("TrayFab"), () => App.Current.SetFabVisible(!Settings.Current.ShowFab));

        menu.Opening += (_, _) =>
        {
            autostart.Checked = Settings.Current.Autostart;
            fab.Checked = Settings.Current.ShowFab;
        };
        menu.Items.Add(fab);
        menu.Items.Add(autostart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item(Loc.T("TrayExit"), () => App.Current.Quit("меню трея")));

        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = Color.FromArgb(235, 235, 235);
            item.Padding = new Forms.Padding(4, 3, 12, 3);
        }

        return menu;

        static Forms.ToolStripMenuItem Item(string text, Action action)
        {
            var item = new Forms.ToolStripMenuItem(text);
            item.Click += (_, _) => Application.Current.Dispatcher.BeginInvoke(action);
            return item;
        }
    }

    public void Dispose()
    {
        if (_icon is null)
        {
            return;
        }

        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }

    /// <summary>Меню в цветах программы, а не серое системное.</summary>
    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer()
            : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(235, 235, 235);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            using var brush = new SolidBrush(Color.FromArgb(0x4C, 0xA6, 0xFF));
            using var font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
            e.Graphics.DrawString("✓", font, brush, e.ImageRectangle.Left, e.ImageRectangle.Top - 2);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(0x2C, 0x2C, 0x2C);
        private static readonly Color Hover = Color.FromArgb(0x3D, 0x3D, 0x3D);
        private static readonly Color Line = Color.FromArgb(0x45, 0x45, 0x45);

        public override Color ToolStripDropDownBackground => Back;

        public override Color ImageMarginGradientBegin => Back;

        public override Color ImageMarginGradientMiddle => Back;

        public override Color ImageMarginGradientEnd => Back;

        public override Color MenuBorder => Line;

        public override Color MenuItemBorder => Hover;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color SeparatorDark => Line;

        public override Color SeparatorLight => Back;

        public override Color CheckBackground => Back;

        public override Color CheckSelectedBackground => Hover;

        public override Color CheckPressedBackground => Hover;
    }
}
