// TwinDesk — Copyright (C) 2026 inukaasith. GPL-3.0, see LICENSE.
// Windows notification-area icon and menu use the helper's existing STA message loop.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
namespace MacShare
{
    static class NativeTray
    {
        static NotifyIcon icon;
        static ContextMenuStrip menu;
        static Icon ownedIcon;

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
                if (e.Button == MouseButtons.Left) ShowMenu();
            };
            icon.Visible = true;
        }

        public static void Update(Dictionary<string, object> data)
        {
            Init();
            string tip = Json.Str(data, "tooltip", "TwinDesk");
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            icon.Text = tip;
            menu.SuspendLayout();
            try
            {
                ToolStripItem[] oldItems = new ToolStripItem[menu.Items.Count];
                menu.Items.CopyTo(oldItems, 0);
                menu.Items.Clear();
                foreach (ToolStripItem old in oldItems) old.Dispose();
                object raw;
                if (data != null && data.TryGetValue("items", out raw)) AddItems(menu.Items, raw as IList);
            }
            finally { menu.ResumeLayout(true); }
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
                        Dictionary<string, object> message = new Dictionary<string, object>();
                        message["event"] = "trayAction";
                        message["id"] = id;
                        message["checked"] = item.Checked;
                        Program.Emit(message);
                    };
                }
                target.Add(item);
            }
        }

        public static void ShowMenu()
        {
            if (menu != null && icon != null && icon.Visible && menu.Items.Count > 0)
                menu.Show(Cursor.Position);
        }

        public static void Dispose()
        {
            if (icon != null) { icon.Visible = false; icon.Dispose(); icon = null; }
            if (menu != null) { menu.Dispose(); menu = null; }
            if (ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
        }
    }
}
