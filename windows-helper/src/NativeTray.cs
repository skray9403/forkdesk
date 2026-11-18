// TwinDesk — Copyright (C) 2026 inukaasith. GPL-3.0, see LICENSE.
// Windows notification-area icon and compact flyout use the helper's existing STA message loop.
// The flyout is ordinary WinForms/Win32 UI: no WebView, Chromium renderer, or extra process.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacShare
{
    static class NativeTray
    {
        static NotifyIcon icon;
        static ContextMenuStrip menu;
        static Icon ownedIcon;
        static Form flyout;
        static FlowLayoutPanel flyoutRows;
        static Label flyoutStatus;
        static IList lastItems;
        static string lastStatus = "Not connected";
        static bool connected;
        static bool dark;

        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        static Color Background
        {
            get { return dark ? Color.FromArgb(32, 33, 36) : Color.FromArgb(250, 250, 252); }
        }
        static Color Foreground
        {
            get { return dark ? Color.FromArgb(242, 243, 245) : Color.FromArgb(32, 33, 36); }
        }
        static Color Muted
        {
            get { return dark ? Color.FromArgb(170, 174, 181) : Color.FromArgb(100, 105, 113); }
        }
        static Color Hover
        {
            get { return dark ? Color.FromArgb(55, 57, 62) : Color.FromArgb(232, 236, 243); }
        }
        static Color Border
        {
            get { return dark ? Color.FromArgb(68, 71, 77) : Color.FromArgb(218, 222, 229); }
        }

        public static void Init()
        {
            if (icon != null) return;
            menu = new ContextMenuStrip();
            menu.RenderMode = ToolStripRenderMode.System;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            icon = new NotifyIcon();
            try { ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { ownedIcon = null; }
            icon.Icon = ownedIcon ?? SystemIcons.Application;
            icon.Text = "TwinDesk";
            icon.ContextMenuStrip = menu;
            icon.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowFlyout();
            };
            icon.Visible = true;
        }

        public static void Update(Dictionary<string, object> data)
        {
            Init();
            string tip = Json.Str(data, "tooltip", "TwinDesk");
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            icon.Text = tip;
            dark = Json.Bool(data, "dark", false);
            connected = Json.Bool(data, "connected", false);

            object raw;
            lastItems = data != null && data.TryGetValue("items", out raw) ? raw as IList : null;
            lastStatus = "Not connected";
            if (lastItems != null && lastItems.Count > 0)
            {
                Dictionary<string, object> first = lastItems[0] as Dictionary<string, object>;
                if (first != null) lastStatus = Json.Str(first, "label", lastStatus);
            }

            menu.SuspendLayout();
            try
            {
                ToolStripItem[] oldItems = new ToolStripItem[menu.Items.Count];
                menu.Items.CopyTo(oldItems, 0);
                menu.Items.Clear();
                foreach (ToolStripItem old in oldItems) old.Dispose();
                AddItems(menu.Items, lastItems);
            }
            finally { menu.ResumeLayout(true); }

            if (flyout != null && !flyout.IsDisposed && flyout.Visible) BuildFlyoutContents();
        }

        static void AddItems(ToolStripItemCollection target, IList items)
        {
            if (items == null) return;
            foreach (object value in items)
            {
                Dictionary<string, object> spec = value as Dictionary<string, object>;
                if (spec == null) continue;
                string type = Json.Str(spec, "type", "item");
                if (type == "separator") { target.Add(new ToolStripSeparator()); continue; }
                ToolStripMenuItem item = new ToolStripMenuItem(Json.Str(spec, "label", "").Replace("&", "&&"));
                item.Enabled = Json.Bool(spec, "enabled", true);
                object childValue;
                if (type == "submenu" && spec.TryGetValue("items", out childValue))
                    AddItems(item.DropDownItems, childValue as IList);
                else
                {
                    string id = Json.Str(spec, "id", "");
                    if (type == "checkbox")
                    {
                        item.CheckOnClick = true;
                        item.Checked = Json.Bool(spec, "checked", false);
                    }
                    if (id.Length > 0) item.Click += delegate
                    {
                        EmitAction(id, item.Checked);
                    };
                }
                target.Add(item);
            }
        }

        static void EmitAction(string id, bool isChecked)
        {
            if (String.IsNullOrEmpty(id)) return;
            Dictionary<string, object> message = new Dictionary<string, object>();
            message["event"] = "trayAction";
            message["id"] = id;
            message["checked"] = isChecked;
            Program.Emit(message);
        }

        static void EnsureFlyout()
        {
            if (flyout != null && !flyout.IsDisposed) return;

            flyout = new Form();
            flyout.FormBorderStyle = FormBorderStyle.None;
            flyout.ShowInTaskbar = false;
            flyout.StartPosition = FormStartPosition.Manual;
            flyout.TopMost = true;
            flyout.KeyPreview = true;
            flyout.AutoScaleMode = AutoScaleMode.Dpi;
            flyout.BackColor = Border;
            flyout.Padding = new Padding(1);
            flyout.Size = new Size(372, 620);
            flyout.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    flyout.Close();
                }
            };
            flyout.Deactivate += delegate(object sender, EventArgs e)
            {
                if (flyout != null && !flyout.IsDisposed) flyout.Close();
            };
            flyout.Shown += delegate(object sender, EventArgs e)
            {
                try
                {
                    int preference = 2; // DWMWCP_ROUND on Windows 11; ignored by older Windows.
                    DwmSetWindowAttribute(flyout.Handle, 33, ref preference, 4);
                }
                catch (Exception) { }
            };
            flyout.FormClosed += delegate(object sender, FormClosedEventArgs e)
            {
                flyout = null;
                flyoutRows = null;
                flyoutStatus = null;
            };

            Panel surface = new Panel();
            surface.Dock = DockStyle.Fill;
            surface.BackColor = Background;
            surface.Padding = new Padding(16, 14, 16, 12);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.Margin = new Padding(0);
            layout.Padding = new Padding(0);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0);

            Label title = new Label();
            title.Text = "TwinDesk";
            title.AutoSize = true;
            title.Location = new Point(0, 0);
            title.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            title.ForeColor = Foreground;
            title.BackColor = Background;

            flyoutStatus = new Label();
            flyoutStatus.AutoSize = false;
            flyoutStatus.Location = new Point(1, 31);
            flyoutStatus.Size = new Size(320, 26);
            flyoutStatus.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
            flyoutStatus.BackColor = Background;

            header.Controls.Add(title);
            header.Controls.Add(flyoutStatus);

            flyoutRows = new FlowLayoutPanel();
            flyoutRows.Dock = DockStyle.Fill;
            flyoutRows.FlowDirection = FlowDirection.TopDown;
            flyoutRows.WrapContents = false;
            flyoutRows.AutoScroll = true;
            flyoutRows.Margin = new Padding(0);
            flyoutRows.Padding = new Padding(0, 2, 4, 4);
            flyoutRows.BackColor = Background;

            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(flyoutRows, 0, 1);
            surface.Controls.Add(layout);
            flyout.Controls.Add(surface);
        }

        static void ApplyFlyoutTheme()
        {
            if (flyout == null || flyout.IsDisposed) return;
            flyout.BackColor = Border;
            foreach (Control c in flyout.Controls)
            {
                Panel surface = c as Panel;
                if (surface != null) surface.BackColor = Background;
            }
            if (flyoutStatus != null)
            {
                flyoutStatus.Text = lastStatus;
                flyoutStatus.ForeColor = connected ? Color.FromArgb(39, 151, 96) : Muted;
            }
            if (flyoutRows != null) flyoutRows.BackColor = Background;
        }

        static int RowWidth()
        {
            if (flyoutRows == null) return 320;
            return Math.Max(260, flyoutRows.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        }

        static void BuildFlyoutContents()
        {
            EnsureFlyout();
            ApplyFlyoutTheme();
            if (flyoutRows == null) return;

            Control[] oldRows = new Control[flyoutRows.Controls.Count];
            flyoutRows.Controls.CopyTo(oldRows, 0);
            flyoutRows.Controls.Clear();
            foreach (Control old in oldRows) old.Dispose();

            AddFlyoutItems(flyoutRows, lastItems, 0, true);
            flyoutRows.ResumeLayout(true);
        }

        static void AddFlyoutItems(FlowLayoutPanel target, IList items, int depth, bool root)
        {
            if (items == null) return;
            int width = RowWidth();
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> spec = items[i] as Dictionary<string, object>;
                if (spec == null) continue;
                string type = Json.Str(spec, "type", "item");
                if (root && i == 0 && type == "item" && !Json.Bool(spec, "enabled", true)) continue;

                if (type == "separator")
                {
                    Panel line = new Panel();
                    line.Width = width;
                    line.Height = 1;
                    line.BackColor = Border;
                    line.Margin = new Padding(0, 7, 0, 7);
                    target.Controls.Add(line);
                    continue;
                }

                string label = Json.Str(spec, "label", "");
                if (type == "submenu")
                {
                    Label section = new Label();
                    section.Text = label;
                    section.Width = width;
                    section.Height = 24;
                    section.Margin = new Padding(depth == 0 ? 0 : 8, depth == 0 ? 8 : 4, 0, 1);
                    section.Padding = new Padding(0, 3, 0, 0);
                    section.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
                    section.ForeColor = Muted;
                    section.BackColor = Background;
                    target.Controls.Add(section);
                    object childValue;
                    if (spec.TryGetValue("items", out childValue))
                        AddFlyoutItems(target, childValue as IList, depth + 1, false);
                    continue;
                }

                string id = Json.Str(spec, "id", "");
                bool enabled = Json.Bool(spec, "enabled", true);
                if (type == "checkbox")
                {
                    CheckBox check = new CheckBox();
                    check.Text = label;
                    check.Width = width;
                    check.Height = 34;
                    check.Margin = new Padding(depth == 0 ? 0 : 8, 1, 0, 1);
                    check.Padding = new Padding(8, 0, 8, 0);
                    check.AutoEllipsis = true;
                    check.CheckAlign = ContentAlignment.MiddleRight;
                    check.TextAlign = ContentAlignment.MiddleLeft;
                    check.FlatStyle = FlatStyle.Flat;
                    check.FlatAppearance.BorderSize = 0;
                    check.FlatAppearance.MouseOverBackColor = Hover;
                    check.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
                    check.ForeColor = Foreground;
                    check.BackColor = Background;
                    check.Enabled = enabled && id.Length > 0;
                    check.Checked = Json.Bool(spec, "checked", false);
                    check.CheckedChanged += delegate { EmitAction(id, check.Checked); };
                    target.Controls.Add(check);
                }
                else
                {
                    Button button = new Button();
                    button.Text = label;
                    button.Width = width;
                    button.Height = 34;
                    button.Margin = new Padding(depth == 0 ? 0 : 8, 1, 0, 1);
                    button.Padding = new Padding(10, 0, 8, 0);
                    button.TextAlign = ContentAlignment.MiddleLeft;
                    button.AutoEllipsis = true;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderSize = 0;
                    button.FlatAppearance.MouseOverBackColor = Hover;
                    button.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
                    button.ForeColor = enabled ? Foreground : Muted;
                    button.BackColor = Background;
                    button.Enabled = enabled && id.Length > 0;
                    button.Click += delegate
                    {
                        EmitAction(id, false);
                        if (flyout != null && !flyout.IsDisposed) flyout.Close();
                    };
                    target.Controls.Add(button);
                }
            }
        }

        public static void ShowMenu()
        {
            ShowFlyout();
        }

        static void ShowFlyout()
        {
            if (icon == null || !icon.Visible) return;
            if (flyout != null && !flyout.IsDisposed)
            {
                if (flyout.Visible) { flyout.Close(); return; }
            }
            EnsureFlyout();
            BuildFlyoutContents();

            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int height = Math.Min(620, Math.Max(300, area.Height - 16));
            flyout.Size = new Size(372, height);
            int x = Cursor.Position.X - (flyout.Width / 2);
            int y = Cursor.Position.Y - flyout.Height - 8;
            if (y < area.Top + 6) y = Cursor.Position.Y + 8;
            x = Math.Max(area.Left + 6, Math.Min(x, area.Right - flyout.Width - 6));
            y = Math.Max(area.Top + 6, Math.Min(y, area.Bottom - flyout.Height - 6));
            flyout.Location = new Point(x, y);
            flyout.Show();
            flyout.Activate();
        }

        public static void Dispose()
        {
            if (flyout != null && !flyout.IsDisposed) flyout.Close();
            if (icon != null) { icon.Visible = false; icon.Dispose(); icon = null; }
            if (menu != null) { menu.Dispose(); menu = null; }
            if (ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
        }
    }
}
