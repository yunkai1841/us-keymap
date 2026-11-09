using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace UsKeyBridge
{
    /// <summary>通知領域 (タスクトレイ) のアイコンとメニュー。</summary>
    public sealed class TrayApp : ApplicationContext
    {
        readonly Settings settings;
        readonly DeviceDirectory devices;
        readonly Bridge bridge;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Icon iconOn, iconOff;

        public TrayApp()
        {
            settings = new Settings();
            settings.Load();
            devices = new DeviceDirectory(settings);
            bridge = new Bridge(settings, devices);
            bridge.DetectionProblem += delegate { OnDetectionProblem(); };

            iconOn = MakeIcon(Color.FromArgb(0, 103, 192), Color.White);
            iconOff = MakeIcon(Color.FromArgb(140, 140, 140), Color.FromArgb(235, 235, 235));

            menu = new ContextMenuStrip();
            menu.Items.Add("..."); // Opening で作り直す
            menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                BuildMenu();
                e.Cancel = false;
            };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleEnabled();
            };

            devices.Enumerate(); // フックを付ける前にキーボード情報を読み込んでおく
            bridge.Start();
            UpdateTray();
            tray.Visible = true;

            if (!Bridge.LayoutIsJis(Bridge.ForegroundLayout()))
            {
                tray.ShowBalloonTip(8000, "UsKeyBridge",
                    "現在のキーボードレイアウトが日本語配列ではありません。日本語配列のときだけ変換します。",
                    ToolTipIcon.Info);
            }
        }

        void BuildMenu()
        {
            menu.Items.Clear();

            ToolStripMenuItem enabled = new ToolStripMenuItem("有効", null, delegate { ToggleEnabled(); });
            enabled.Checked = settings.Enabled;
            menu.Items.Add(enabled);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mode = new ToolStripMenuItem("判別モード");
            ToolStripMenuItem perDevice = new ToolStripMenuItem("キーボードごとに判別する (推奨)", null,
                delegate { SetMode(BridgeMode.PerDevice); });
            perDevice.Checked = settings.Mode == BridgeMode.PerDevice;
            ToolStripMenuItem allUs = new ToolStripMenuItem("すべてのキーボードを US 配列として扱う", null,
                delegate { SetMode(BridgeMode.AllUs); });
            allUs.Checked = settings.Mode == BridgeMode.AllUs;
            mode.DropDownItems.Add(perDevice);
            mode.DropDownItems.Add(allUs);
            menu.Items.Add(mode);

            menu.Items.Add(BuildDeviceMenu());

            ToolStripMenuItem caps = new ToolStripMenuItem("US 配列キーボードの CapsLock を CapsLock として使う", null,
                delegate
                {
                    settings.CapsLockFix = !settings.CapsLockFix;
                    settings.Save();
                    bridge.ApplySettings();
                });
            caps.Checked = settings.CapsLockFix;
            menu.Items.Add(caps);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("状態を表示...", null, delegate { ShowStatus(); }));

            ToolStripMenuItem log = new ToolStripMenuItem("診断ログを記録する", null, delegate
            {
                settings.Log = !settings.Log;
                settings.Save();
            });
            log.Checked = settings.Log;
            menu.Items.Add(log);
            menu.Items.Add(new ToolStripMenuItem("ログフォルダを開く", null, delegate
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Bridge.LogDirectory);
                    Process.Start("explorer.exe", "\"" + Bridge.LogDirectory + "\"");
                }
                catch (Exception) { }
            }));

            ToolStripMenuItem startup = new ToolStripMenuItem("Windows の起動時に自動で起動する", null, delegate
            {
                Settings.StartupEnabled = !Settings.StartupEnabled;
            });
            startup.Checked = Settings.StartupEnabled;
            menu.Items.Add(startup);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("終了", null, delegate { Quit(); }));
        }

        ToolStripMenuItem BuildDeviceMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("US 配列として扱うキーボード");
            if (settings.Mode == BridgeMode.AllUs)
            {
                ToolStripMenuItem note = new ToolStripMenuItem("(現在はすべて US 配列として扱っています)");
                note.Enabled = false;
                root.DropDownItems.Add(note);
                root.DropDownItems.Add(new ToolStripSeparator());
            }

            devices.Invalidate();
            KeyboardInfo last = devices.LoadName(devices.Get(bridge.LastDevice));
            List<KeyboardInfo> list = devices.Enumerate();
            if (list.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("(キーボードが見つかりません)");
                none.Enabled = false;
                root.DropDownItems.Add(none);
            }
            foreach (KeyboardInfo info in list)
            {
                string text = info.Display;
                if (last != null && last.Key == info.Key) text += "   ← 最後に入力したキーボード";
                if (!info.Overridden) text += "   (自動)";
                KeyboardInfo target = info;
                ToolStripMenuItem item = new ToolStripMenuItem(text, null, delegate
                {
                    settings.DeviceOverrides[target.Key] = !target.IsUs;
                    settings.Save();
                    devices.Invalidate();
                });
                item.Checked = info.IsUs;
                item.ToolTipText = info.Path;
                root.DropDownItems.Add(item);
            }

            root.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem hint = new ToolStripMenuItem("チェック = US 配列。クリックで切り替え");
            hint.Enabled = false;
            root.DropDownItems.Add(hint);
            ToolStripMenuItem reset = new ToolStripMenuItem("自動判定に戻す", null, delegate
            {
                settings.DeviceOverrides.Clear();
                settings.Save();
                devices.Invalidate();
            });
            reset.Enabled = settings.DeviceOverrides.Count > 0;
            root.DropDownItems.Add(reset);
            return root;
        }

        void ToggleEnabled()
        {
            settings.Enabled = !settings.Enabled;
            settings.Save();
            bridge.ApplySettings();
            UpdateTray();
        }

        void SetMode(BridgeMode mode)
        {
            settings.Mode = mode;
            settings.Save();
            bridge.ApplySettings();
            bridge.ResetStats();
            UpdateTray();
        }

        void UpdateTray()
        {
            tray.Icon = settings.Enabled ? iconOn : iconOff;
            string text = "UsKeyBridge: ";
            if (!settings.Enabled) text += "無効";
            else text += settings.Mode == BridgeMode.PerDevice ? "有効 (キーボードごと)" : "有効 (すべて US)";
            tray.Text = text;
        }

        void ShowStatus()
        {
            EngineStats s = bridge.Engine.Stats;
            KeyboardInfo last = devices.LoadName(devices.Get(bridge.LastDevice));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("UsKeyBridge " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            sb.AppendLine();
            sb.AppendLine("状態: " + (settings.Enabled ? "有効" : "無効"));
            sb.AppendLine("判別モード: " + (settings.Mode == BridgeMode.PerDevice
                ? "キーボードごとに判別" : "すべて US 配列"));
            sb.AppendLine("前面アプリのレイアウト: " + Bridge.DescribeLayout(Bridge.ForegroundLayout()));
            sb.AppendLine("最後に入力したキーボード: " + (last == null ? "(なし)"
                : last.Display + (last.IsUs ? "  → US 配列" : "  → 日本語配列")));
            sb.AppendLine();
            sb.AppendLine("キーボード判別の統計:");
            sb.AppendLine("  受け取った Raw Input: " + s.RawSeen);
            sb.AppendLine("  記号キー: そのキーの情報で判別 " + s.PairedAtHook
                + " / 直前のキーボードで判断 " + s.Guessed);
            sb.AppendLine("  変換したキー: " + s.Translated);
            sb.AppendLine();
            sb.AppendLine("「受け取った Raw Input」が 0 のままの場合、キーボードを判別できません。");
            sb.AppendLine("判別モードを「すべてのキーボードを US 配列として扱う」に切り替えてください。");
            MessageBox.Show(sb.ToString(), "UsKeyBridge の状態", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OnDetectionProblem()
        {
            tray.ShowBalloonTip(15000, "UsKeyBridge: キーボードを判別できません",
                "キーボードの入力元の情報 (Raw Input) が届いていません。" +
                "メニューの「判別モード」→「すべてのキーボードを US 配列として扱う」を試してください。",
                ToolTipIcon.Warning);
        }

        void Quit()
        {
            tray.Visible = false;
            bridge.Dispose();
            tray.Dispose();
            ExitThread();
        }

        static Icon MakeIcon(Color bg, Color fg)
        {
            const int size = 32;
            using (Bitmap bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath path = RoundedRect(new Rectangle(0, 2, size - 1, size - 5), 7))
                    using (SolidBrush b = new SolidBrush(bg))
                    {
                        g.FillPath(b, path);
                    }
                    using (Font f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush b = new SolidBrush(fg))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("US", f, b, new RectangleF(0, 1, size, size), sf);
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
