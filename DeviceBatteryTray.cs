using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DeviceBatteryTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--probe")
            {
                foreach (DeviceStatus status in DeviceReaders.ReadAll())
                    Console.WriteLine("{0}: {1}; {2}", status.Name, status.Percent.HasValue ? status.Percent.Value + "%" : "--", status.State);
                return;
            }

            bool created;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "Local\\DeviceBatteryTray", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Dashboard());
            }
        }
    }

    [DataContract]
    internal sealed class UserSettings
    {
        [DataMember] public int RefreshSeconds { get; set; }
        [DataMember] public bool LowBatteryAlerts { get; set; }
        [DataMember] public int LowBatteryThreshold { get; set; }
        [DataMember] public bool StartWithWindows { get; set; }
        [DataMember] public bool HasWidgetPosition { get; set; }
        [DataMember] public int WidgetLeft { get; set; }
        [DataMember] public int WidgetTop { get; set; }

        public static UserSettings Defaults()
        {
            return new UserSettings { RefreshSeconds = 60, LowBatteryAlerts = true, LowBatteryThreshold = 20, StartWithWindows = false };
        }

        public static UserSettings Load()
        {
            UserSettings value = Defaults();
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    using (FileStream stream = File.OpenRead(path))
                        value = (UserSettings)new DataContractJsonSerializer(typeof(UserSettings)).ReadObject(stream);
                }
            }
            catch { value = Defaults(); }
            value.RefreshSeconds = Math.Max(15, Math.Min(3600, value.RefreshSeconds));
            value.LowBatteryThreshold = Math.Max(1, Math.Min(100, value.LowBatteryThreshold));
            return value;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            using (FileStream stream = File.Create(FilePath))
                new DataContractJsonSerializer(typeof(UserSettings)).WriteObject(stream, this);
        }

        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryTray", "settings.json"); }
        }
    }

    internal sealed class DeviceStatus
    {
        public string Id;
        public string Name;
        public string State;
        public string Detail;
        public int? Percent;
        public bool Charging;
        public bool Available;
    }

    internal sealed class Dashboard : Form
    {
        private readonly UserSettings settings;
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer timer;
        private readonly System.Windows.Forms.Timer hoverTimer;
        private readonly System.Windows.Forms.Timer positionSaveTimer;
        private readonly Dictionary<string, DeviceStatus> latest = new Dictionary<string, DeviceStatus>();
        private readonly Dictionary<string, bool> alertArmed = new Dictionary<string, bool>();
        private bool polling;
        private bool exiting;
        private bool pinned;
        private bool dragging;
        private bool trayPointerKnown;
        private Point lastTrayPointer;
        private DateTime pointerLeftAt = DateTime.MinValue;
        private Icon currentTrayIcon;
        private static readonly Color Ink = Color.FromArgb(38, 48, 50);
        private static readonly Color Muted = Color.FromArgb(119, 130, 130);
        private static readonly Color Line = Color.FromArgb(231, 236, 234);
        private readonly string[] ids = new string[] { "mouse", "headset", "controller" };
        private readonly string[] names = new string[] { "迈从 A7 Ultra", "迈从 V9 Pro2 耳机", "北通鲲鹏20" };
        private readonly Color[] accents = new Color[] { Color.FromArgb(43, 145, 112), Color.FromArgb(67, 128, 176), Color.FromArgb(213, 143, 65) };

        public Dashboard()
        {
            settings = UserSettings.Load();
            Text = "设备电量";
            ClientSize = new Size(216, 164);
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = false;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            if (settings.HasWidgetPosition)
            {
                Point saved = new Point(settings.WidgetLeft, settings.WidgetTop);
                area = Screen.FromPoint(saved).WorkingArea;
                Location = new Point(Math.Max(area.Left, Math.Min(area.Right - Width, saved.X)), Math.Max(area.Top, Math.Min(area.Bottom - Height, saved.Y)));
            }
            else Location = new Point(area.Right - Width - 28, area.Bottom - Height - 34);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开并固定窗口", null, delegate { ShowDashboard(); });
            menu.Items.Add("立即刷新", null, delegate { RefreshDevices(); });
            menu.Items.Add("设置", null, delegate { ShowSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            tray = new NotifyIcon();
            tray.Text = "设备电量";
            currentTrayIcon = DrawTrayIcon(0);
            tray.Icon = currentTrayIcon;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseMove += HandleTrayMouseMove;
            tray.MouseClick += HandleTrayMouseClick;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = settings.RefreshSeconds * 1000;
            timer.Tick += delegate { RefreshDevices(); };
            timer.Start();
            hoverTimer = new System.Windows.Forms.Timer();
            hoverTimer.Interval = 100;
            hoverTimer.Tick += CheckHoverState;
            hoverTimer.Start();
            positionSaveTimer = new System.Windows.Forms.Timer();
            positionSaveTimer.Interval = 600;
            positionSaveTimer.Tick += delegate
            {
                positionSaveTimer.Stop();
                settings.HasWidgetPosition = true;
                settings.WidgetLeft = Left;
                settings.WidgetTop = Top;
                settings.Save();
            };
            LocationChanged += delegate
            {
                if (Visible && !exiting)
                {
                    positionSaveTimer.Stop();
                    positionSaveTimer.Start();
                }
            };
            Shown += delegate
            {
                pinned = true;
                RefreshDevices();
                ShowTransient();
            };
            MouseDown += DragWindow;
            MouseUp += HandleMouseUp;
            FormClosing += OnFormClosing;
            ApplyStartupSetting(settings.StartWithWindows);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using (Font percentFont = new Font("Segoe UI", 17F, FontStyle.Bold))
            {
                g.Clear(BackColor);
                DrawClose(g);
                for (int i = 0; i < ids.Length; i++) DrawDevice(g, i, percentFont);
                using (Pen separator = new Pen(Color.FromArgb(225, 230, 228))) g.DrawLine(separator, 8, 126, Width - 8, 126);
                DrawRefreshButton(g);
                DrawSettingsButton(g);
            }
        }

        private void DrawDevice(Graphics g, int index, Font percentFont)
        {
            int y = 7 + index * 39;
            if (index > 0) using (Pen separator = new Pen(Color.FromArgb(225, 230, 228))) g.DrawLine(separator, 8, y - 4, Width - 8, y - 4);
            Rectangle iconBox = new Rectangle(10, y + 1, 34, 34);
            DrawDeviceIcon(g, index, iconBox, accents[index]);

            DeviceStatus status;
            bool found = latest.TryGetValue(ids[index], out status);
            string value = found && status.Percent.HasValue ? status.Percent.Value + "%" : "--";
            Color valueColor = !found || !status.Percent.HasValue ? Muted : (status.Percent.Value <= settings.LowBatteryThreshold ? Color.FromArgb(190, 70, 63) : Ink);
            Rectangle percentBox = new Rectangle(129, y + 1, 76, 28);
            DrawOutlinedPercent(g, value, percentBox, valueColor, percentFont);
            Rectangle track = new Rectangle(160, y + 31, 45, 3);
            using (Brush trackBrush = new SolidBrush(Color.FromArgb(222, 228, 225))) g.FillRectangle(trackBrush, track);
            if (found && status.Percent.HasValue)
            {
                int width = Math.Max(2, track.Width * status.Percent.Value / 100);
                using (Brush fill = new SolidBrush(valueColor)) g.FillRectangle(fill, track.X, track.Y, width, track.Height);
            }
        }

        private static void DrawOutlinedPercent(Graphics g, string value, Rectangle bounds, Color color, Font font)
        {
            using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
            using (StringFormat format = new StringFormat())
            using (Pen outline = new Pen(Color.FromArgb(250, 255, 255, 255), 2.5F))
            using (Brush fill = new SolidBrush(color))
            {
                format.Alignment = StringAlignment.Far;
                format.LineAlignment = StringAlignment.Center;
                path.AddString(value, font.FontFamily, (int)FontStyle.Bold, 21F, bounds, format);
                outline.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                g.DrawPath(outline, path);
                g.FillPath(fill, path);
            }
        }

        private static void DrawDeviceIcon(Graphics g, int index, Rectangle box, Color color)
        {
            using (Pen pen = new Pen(color, 2F))
            using (Brush brush = new SolidBrush(color))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                int cx = box.X + box.Width / 2;
                int cy = box.Y + box.Height / 2;
                if (index == 0)
                {
                    Rectangle body = new Rectangle(cx - 9, cy - 14, 18, 29);
                    using (System.Drawing.Drawing2D.GraphicsPath path = RoundPath(body, 9)) g.DrawPath(pen, path);
                    g.DrawLine(pen, cx, cy - 13, cx, cy - 5);
                    g.DrawArc(pen, cx - 7, cy - 2, 14, 10, 20, 140);
                }
                else if (index == 1)
                {
                    g.DrawArc(pen, cx - 13, cy - 15, 26, 29, 180, 180);
                    using (System.Drawing.Drawing2D.GraphicsPath path = RoundPath(new Rectangle(cx - 14, cy - 1, 7, 14), 3)) g.FillPath(brush, path);
                    using (System.Drawing.Drawing2D.GraphicsPath path = RoundPath(new Rectangle(cx + 7, cy - 1, 7, 14), 3)) g.FillPath(brush, path);
                }
                else
                {
                    using (System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.StartFigure();
                        path.AddBezier(cx - 11, cy - 7, cx - 15, cy - 7, cx - 16, cy - 1, cx - 15, cy + 6);
                        path.AddBezier(cx - 15, cy + 6, cx - 14, cy + 12, cx - 9, cy + 13, cx - 6, cy + 8);
                        path.AddLine(cx - 6, cy + 8, cx + 6, cy + 8);
                        path.AddBezier(cx + 6, cy + 8, cx + 9, cy + 13, cx + 14, cy + 12, cx + 15, cy + 6);
                        path.AddBezier(cx + 15, cy + 6, cx + 16, cy - 1, cx + 15, cy - 7, cx + 11, cy - 7);
                        path.CloseFigure();
                        g.DrawPath(pen, path);
                    }
                    g.DrawLine(pen, cx - 9, cy - 1, cx - 3, cy - 1);
                    g.DrawLine(pen, cx - 6, cy - 4, cx - 6, cy + 2);
                    g.FillEllipse(brush, cx + 4, cy - 3, 3, 3);
                    g.FillEllipse(brush, cx + 9, cy + 1, 3, 3);
                }
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundPath(Rectangle rect, int radius)
        {
            int d = radius * 2;
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawClose(Graphics g)
        {
            using (Pen pen = new Pen(Muted, 1.5F))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(pen, 204, 8, 212, 16);
                g.DrawLine(pen, 212, 8, 204, 16);
            }
        }

        private static void DrawRefreshButton(Graphics g)
        {
            using (Brush background = new SolidBrush(Color.FromArgb(233, 240, 236)))
            using (Pen pen = new Pen(Color.FromArgb(67, 107, 91), 1.6F))
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundPath(new Rectangle(147, 132, 27, 25), 6))
            {
                g.FillPath(background, path);
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(pen, 154, 138, 13, 13, 35, 285);
                g.DrawLine(pen, 164, 137, 168, 138);
                g.DrawLine(pen, 164, 137, 164, 141);
            }
        }

        private static void DrawSettingsButton(Graphics g)
        {
            using (Brush background = new SolidBrush(Color.FromArgb(233, 240, 236)))
            using (Pen pen = new Pen(Color.FromArgb(67, 107, 91), 1.5F))
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundPath(new Rectangle(180, 132, 27, 25), 6))
            {
                g.FillPath(background, path);
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                int cx = 193, cy = 144;
                g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                g.DrawEllipse(pen, cx - 1.5F, cy - 1.5F, 3, 3);
                for (int i = 0; i < 8; i++)
                {
                    double angle = Math.PI * i / 4.0;
                    float x1 = cx + (float)Math.Cos(angle) * 6F;
                    float y1 = cy + (float)Math.Sin(angle) * 6F;
                    float x2 = cx + (float)Math.Cos(angle) * 8F;
                    float y2 = cy + (float)Math.Sin(angle) * 8F;
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
            }
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            bool closeButton = e.X >= 198 && e.Y < 24;
            bool actionButtons = e.Y >= 130 && e.X >= 146;
            if (e.Button == MouseButtons.Left && !closeButton && !actionButtons)
            {
                dragging = true;
                ReleaseCapture();
                SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
                dragging = false;
            }
        }

        private void HandleMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || dragging) return;
            if (e.X >= 198 && e.Y < 24) { pinned = false; Invalidate(); Hide(); return; }
            if (e.X >= 178 && e.Y >= 130) { ShowSettings(); return; }
            if (e.X >= 146 && e.X < 178 && e.Y >= 130) RefreshDevices();
        }

        private void HandleTrayMouseMove(object sender, MouseEventArgs e)
        {
            lastTrayPointer = Cursor.Position;
            trayPointerKnown = true;
            pointerLeftAt = DateTime.MinValue;
            if (!pinned) ShowTransient();
        }

        private void HandleTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            lastTrayPointer = Cursor.Position;
            trayPointerKnown = true;
            pinned = !pinned;
            pointerLeftAt = DateTime.MinValue;
            if (pinned) ShowTransient();
            Invalidate();
        }

        private void CheckHoverState(object sender, EventArgs e)
        {
            if (exiting || pinned || !Visible) return;
            Point cursor = Cursor.Position;
            bool overWindow = Bounds.Contains(cursor);
            bool overTray = trayPointerKnown && new Rectangle(lastTrayPointer.X - 12, lastTrayPointer.Y - 12, 25, 25).Contains(cursor);
            if (overWindow || overTray)
            {
                pointerLeftAt = DateTime.MinValue;
                return;
            }
            if (pointerLeftAt == DateTime.MinValue) pointerLeftAt = DateTime.UtcNow;
            else if ((DateTime.UtcNow - pointerLeftAt).TotalMilliseconds >= 500)
            {
                pointerLeftAt = DateTime.MinValue;
                Hide();
            }
        }

        private void ShowTransient()
        {
            if (exiting) return;
            pointerLeftAt = DateTime.MinValue;
            if (!Visible)
            {
                Show();
                WindowState = FormWindowState.Normal;
                TopMost = false;
            }
            PlaceOnDesktopLayer();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000;
                return parameters;
            }
        }

        private void PlaceOnDesktopLayer()
        {
            IntPtr foreground = GetForegroundWindow();
            StringBuilder className = new StringBuilder(128);
            if (foreground != IntPtr.Zero) GetClassName(foreground, className, className.Capacity);
            string name = className.ToString();
            bool desktop = name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd";
            IntPtr insertAfter = desktop || foreground == Handle ? IntPtr.Zero : foreground;
            SetWindowPos(Handle, insertAfter, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        private void RefreshDevices()
        {
            if (polling || exiting) return;
            polling = true;
            Invalidate();
            Task.Run(delegate { return DeviceReaders.ReadAll(); }).ContinueWith(delegate(Task<List<DeviceStatus>> task)
            {
                if (IsDisposed) return;
                BeginInvoke((Action)delegate
                {
                    polling = false;
                    if (task.IsFaulted)
                    {
                        Invalidate();
                        return;
                    }
                    DateTime now = DateTime.Now;
                    foreach (DeviceStatus status in task.Result) ApplyStatus(status, now);
                    Invalidate();
                    int readable = 0;
                    foreach (DeviceStatus status in latest.Values) if (status.Available) readable++;
                    Icon nextIcon = DrawTrayIcon(readable);
                    tray.Icon = nextIcon;
                    if (currentTrayIcon != null) currentTrayIcon.Dispose();
                    currentTrayIcon = nextIcon;
                    tray.Text = "设备电量：" + readable + "/3 可读取";
                });
            }, TaskScheduler.Default);
        }

        private void ApplyStatus(DeviceStatus status, DateTime now)
        {
            DeviceStatus old;
            latest.TryGetValue(status.Id, out old);
            if (status.Available)
            {
                status.Detail = "更新于 " + now.ToString("HH:mm:ss");
                latest[status.Id] = status;
                CheckLowBattery(status);
            }
            else
            {
                if (old != null && old.Percent.HasValue)
                {
                    status.Percent = old.Percent;
                    status.Detail = old.Detail;
                }
                latest[status.Id] = status;
            }
        }

        private void CheckLowBattery(DeviceStatus status)
        {
            bool armed;
            if (!alertArmed.TryGetValue(status.Id, out armed)) armed = true;
            if (status.Charging || (status.Percent.HasValue && status.Percent.Value >= settings.LowBatteryThreshold + 5))
                armed = true;
            if (settings.LowBatteryAlerts && armed && status.Percent.HasValue && status.Percent.Value <= settings.LowBatteryThreshold && !status.Charging)
            {
                tray.BalloonTipTitle = status.Name + " 电量偏低";
                tray.BalloonTipText = "当前电量 " + status.Percent.Value + "%";
                tray.BalloonTipIcon = ToolTipIcon.Warning;
                tray.ShowBalloonTip(5000);
                armed = false;
            }
            alertArmed[status.Id] = armed;
        }

        private void ShowSettings()
        {
            using (SettingsDialog dialog = new SettingsDialog(settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                settings.RefreshSeconds = dialog.RefreshSeconds;
                settings.LowBatteryAlerts = dialog.LowBatteryAlerts;
                settings.LowBatteryThreshold = dialog.LowBatteryThreshold;
                settings.StartWithWindows = dialog.StartWithWindows;
                settings.Save();
                timer.Interval = settings.RefreshSeconds * 1000;
                ApplyStartupSetting(settings.StartWithWindows);
                RefreshDevices();
            }
        }

        private static void ApplyStartupSetting(bool enabled)
        {
            const string keyName = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyName, true))
            {
                if (key == null) return;
                if (enabled) key.SetValue("DeviceBatteryTray", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("DeviceBatteryTray", false);
            }
        }

        private void ShowDashboard()
        {
            pinned = true;
            ShowTransient();
            Invalidate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                pinned = false;
                Hide();
            }
        }

        private void ExitApplication()
        {
            exiting = true;
            timer.Stop();
            hoverTimer.Stop();
            positionSaveTimer.Stop();
            settings.HasWidgetPosition = true;
            settings.WidgetLeft = Left;
            settings.WidgetTop = Top;
            settings.Save();
            tray.Visible = false;
            tray.Dispose();
            if (currentTrayIcon != null) currentTrayIcon.Dispose();
            Close();
        }

        private static Icon DrawTrayIcon(int readable)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (MemoryStream stream = new MemoryStream())
            {
                graphics.Clear(Color.Transparent);
                using (Brush brush = new SolidBrush(Color.FromArgb(39, 115, 86))) graphics.FillEllipse(brush, 2, 2, 28, 28);
                using (Font font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    string text = readable.ToString();
                    SizeF size = graphics.MeasureString(text, font);
                    graphics.DrawString(text, font, brush, (32 - size.Width) / 2, (32 - size.Height) / 2 - 1);
                }
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                using (Bitmap iconBitmap = (Bitmap)Bitmap.FromStream(stream))
                {
                    IntPtr handle = iconBitmap.GetHicon();
                    try
                    {
                        using (Icon icon = Icon.FromHandle(handle)) return (Icon)icon.Clone();
                    }
                    finally { DestroyIcon(handle); }
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr icon);
    }

    internal sealed class SettingsDialog : Form
    {
        public int RefreshSeconds { get { return (int)interval.Value; } }
        public bool LowBatteryAlerts { get { return alerts.Checked; } }
        public int LowBatteryThreshold { get { return (int)threshold.Value; } }
        public bool StartWithWindows { get { return startup.Checked; } }
        private readonly NumericUpDown interval;
        private readonly CheckBox alerts;
        private readonly NumericUpDown threshold;
        private readonly CheckBox startup;

        public SettingsDialog(UserSettings settings)
        {
            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(345, 250);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);

            Label intervalLabel = new Label(); intervalLabel.Text = "刷新间隔（秒）"; intervalLabel.Location = new Point(18, 22); intervalLabel.AutoSize = true;
            interval = new NumericUpDown(); interval.Minimum = 15; interval.Maximum = 3600; interval.Increment = 15; interval.Value = settings.RefreshSeconds; interval.Location = new Point(185, 18); interval.Width = 130;
            alerts = new CheckBox(); alerts.Text = "启用低电量提醒"; alerts.Checked = settings.LowBatteryAlerts; alerts.Location = new Point(18, 66); alerts.AutoSize = true;
            Label thresholdLabel = new Label(); thresholdLabel.Text = "提醒阈值（%）"; thresholdLabel.Location = new Point(18, 104); thresholdLabel.AutoSize = true;
            threshold = new NumericUpDown(); threshold.Minimum = 1; threshold.Maximum = 100; threshold.Value = settings.LowBatteryThreshold; threshold.Location = new Point(185, 100); threshold.Width = 130;
            startup = new CheckBox(); startup.Text = "登录 Windows 后自动启动"; startup.Checked = settings.StartWithWindows; startup.Location = new Point(18, 144); startup.AutoSize = true;
            Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(147, 197); cancel.Size = new Size(78, 30);
            Button save = new Button(); save.Text = "保存"; save.DialogResult = DialogResult.OK; save.Location = new Point(237, 197); save.Size = new Size(78, 30);
            Controls.Add(intervalLabel); Controls.Add(interval); Controls.Add(alerts); Controls.Add(thresholdLabel); Controls.Add(threshold); Controls.Add(startup); Controls.Add(cancel); Controls.Add(save);
            AcceptButton = save; CancelButton = cancel;
        }
    }

    internal static class DeviceReaders
    {
        private const int HidStatusSuccess = 0x110000;
        private const uint DigcfPresent = 0x02;
        private const uint DigcfDeviceInterface = 0x10;
        private const uint GenericReadWrite = 0xC0000000;
        private const uint FileShareReadWrite = 3;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint ErrorIoPending = 997;
        private static readonly Guid HidClassGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public ushort Vid, Pid, Version; }
        [StructLayout(LayoutKind.Sequential)] private struct Caps
        {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort Links, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Overlapped { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
        private sealed class HidDeviceInfo
        {
            public string Path;
            public ushort Vid, Pid, Page, Usage, InputLength, OutputLength, FeatureLength;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] report, int length);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint length, out uint read, ref Overlapped overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint length, out uint written, ref Overlapped overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle handle, ref Overlapped overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle handle, ref Overlapped overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

        public static List<DeviceStatus> ReadAll()
        {
            List<HidDeviceInfo> devices = Enumerate();
            List<DeviceStatus> results = new List<DeviceStatus>();
            results.Add(ReadMouse(devices));
            results.Add(ReadHeadset(devices));
            results.Add(ReadController(devices));
            return results;
        }

        private static DeviceStatus ReadMouse(List<HidDeviceInfo> devices)
        {
            HidDeviceInfo device = Find(devices, 0x5253, 0x1020, 0xFF01, 0x0001);
            DeviceStatus status = NewStatus("mouse", "迈从 A7 Ultra");
            if (device == null) { status.State = "未检测到鼠标接收器"; return status; }
            try
            {
                using (SafeFileHandle handle = CreateFile(device.Path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    byte[] request = new byte[21]; request[0] = 0x11; request[1] = 0xF9;
                    for (int i = 2; i < request.Length; i++) request[i] = 0xFF;
                    if (!HidD_SetFeature(handle, request, request.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    System.Threading.Thread.Sleep(100);
                    byte[] response = new byte[21]; response[0] = 0x11;
                    if (!HidD_GetFeature(handle, response, response.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    byte[] decoded = new byte[response.Length - 2];
                    for (int i = 2; i < response.Length; i++) decoded[i - 2] = (byte)(response[i] ^ 0xFF);
                    int vid = decoded[0] | decoded[1] << 8;
                    int pid = decoded[2] | decoded[3] << 8;
                    int percent = decoded[9];
                    int mode = decoded[8] & 0x07;
                    bool linked = (decoded[8] & 0x08) != 0;
                    if ((vid != 0x5253 && vid != 0x3837) || pid == 0 || pid == 0xFFFF || percent > 100)
                        throw new InvalidOperationException("身份或电量字段校验失败");
                    if (!linked)
                    {
                        status.State = "鼠标未连接或处于休眠状态";
                        return status;
                    }
                    status.Percent = percent;
                    status.Charging = decoded[10] == 1;
                    status.Available = true;
                    status.State = ChargeText(decoded[10]) + " · " + (mode == 1 ? "2.4G" : "连接模式 " + mode);
                    return status;
                }
            }
            catch (Exception ex) { status.State = "未收到有效电量回包：" + ex.Message; return status; }
        }

        private static DeviceStatus ReadHeadset(List<HidDeviceInfo> devices)
        {
            DeviceStatus status = NewStatus("headset", "迈从 V9 Pro2 耳机");
            HidDeviceInfo device = Find(devices, 0x3837, 0x6011, 0xFF07, 0x0222);
            if (device == null) { status.State = "未检测到耳机接收器"; return status; }
            try
            {
                using (SafeFileHandle handle = CreateFile(device.Path, GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    byte[] request = new byte[Math.Max(8, (int)device.OutputLength)];
                    request[0] = 0x55;
                    request[1] = 0xAA;
                    request[2] = 0x01;
                    request[4] = 0x01;
                    request[6] = 0x01;
                    request[7] = 0x0B;
                    uint written;
                    if (!WriteReport(handle, request, 1000, out written)) throw new Win32Exception(Marshal.GetLastWin32Error());

                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(1200);
                    while (DateTime.UtcNow < deadline)
                    {
                        byte[] response = TryReadReport(handle, device.InputLength, 100);
                        if (!IsMcBatteryResponse(response)) continue;
                        if (response[8] == 0)
                        {
                            status.State = "耳机休眠 · 已连接 2.4G 接收器";
                            return status;
                        }
                        status.Percent = response[9] & 0x7F;
                        status.Charging = (response[9] & 0x80) != 0;
                        status.Available = true;
                        status.State = (status.Charging ? "充电中" : "电池供电") + " · 2.4G";
                        return status;
                    }
                    status.State = "接收器已识别，未收到电量回包（请唤醒耳机）";
                    return status;
                }
            }
            catch (Exception ex) { status.State = "读取失败：" + ex.Message; return status; }
        }

        private static bool IsMcBatteryResponse(byte[] report)
        {
            if (report == null || report.Length < 11 || report[0] != 0x55 || report[1] != 0xAA || report[2] != 0x01)
                return false;
            int length = report[4];
            if (length < 3 || report[5] != 0 || report[6] != 1 || report[7] != 0x0B || report[8] > 1 || (report[9] & 0x7F) > 100)
                return false;
            int checksumIndex = 7 + length;
            if (checksumIndex >= report.Length) return false;
            if (report[3] == 0) return report[checksumIndex] == 0;
            if (report[3] != 1) return false;
            int checksum = report[3] ^ length ^ report[5] ^ report[6];
            for (int i = 0; i < length; i++) checksum ^= report[7 + i];
            return report[checksumIndex] == (byte)checksum;
        }

        private static DeviceStatus ReadController(List<HidDeviceInfo> devices)
        {
            DeviceStatus status = NewStatus("controller", "北通鲲鹏20");
            HidDeviceInfo device = FindController(devices);
            if (device == null) { status.State = "未检测到手柄接收器"; return status; }
            try
            {
                using (SafeFileHandle handle = CreateFile(device.Path, GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    for (int i = 0; i < 12; i++) TryReadReport(handle, device.InputLength, 3);
                    byte[] request = new byte[Math.Max(2, (int)device.OutputLength)];
                    request[0] = 0x02; request[1] = 0x15;
                    uint written;
                    if (!WriteReport(handle, request, 1000, out written)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(1500);
                    while (DateTime.UtcNow < deadline)
                    {
                        byte[] response = TryReadReport(handle, device.InputLength, 80);
                        if (response == null || response.Length < 5 || response[0] != 0x02 || response[1] != 0x15) continue;
                        int percent = response[2];
                        if (percent > 100) continue;
                        status.Percent = percent;
                        status.Charging = (response[4] & 0x0F) != 0;
                        status.Available = true;
                        status.State = status.Charging ? "充电中 · 2.4G" : "电池供电 · 2.4G";
                        return status;
                    }
                    status.State = "接收器已识别，未收到状态回包（请唤醒手柄）";
                    return status;
                }
            }
            catch (Exception ex) { status.State = "读取失败：" + ex.Message; return status; }
        }

        private static string ChargeText(int state)
        {
            if (state == 1) return "充电中";
            if (state == 2) return "充电完成";
            if (state == 0) return "电池供电";
            return "充电状态未知";
        }

        private static DeviceStatus NewStatus(string id, string name)
        {
            return new DeviceStatus { Id = id, Name = name, State = "尚未读取" };
        }

        private static HidDeviceInfo FindController(List<HidDeviceInfo> devices)
        {
            foreach (HidDeviceInfo device in devices)
                if ((device.Vid == 0x20BC || device.Vid == 0x20DD) && device.Pid == 0x5159 && device.Page == 0x00FF && device.Usage == 0x0003)
                    return device;
            return null;
        }

        private static HidDeviceInfo Find(List<HidDeviceInfo> devices, ushort vid, ushort pid, ushort page, ushort usage)
        {
            foreach (HidDeviceInfo device in devices)
                if (device.Vid == vid && device.Pid == pid && device.Page == page && device.Usage == usage)
                    return device;
            return null;
        }

        private static List<HidDeviceInfo> Enumerate()
        {
            List<HidDeviceInfo> devices = new List<HidDeviceInfo>();
            Guid guid = HidClassGuid;
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint i = 0; ; i++)
                {
                    InterfaceData data = new InterfaceData(); data.Size = Marshal.SizeOf(typeof(InterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    uint needed;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    if (needed < 8) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out needed, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        using (SafeFileHandle handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                        {
                            if (handle.IsInvalid) continue;
                            Attributes attributes = new Attributes(); attributes.Size = Marshal.SizeOf(typeof(Attributes));
                            if (!HidD_GetAttributes(handle, ref attributes)) continue;
                            IntPtr preparsed;
                            if (!HidD_GetPreparsedData(handle, out preparsed)) continue;
                            try
                            {
                                Caps caps;
                                if (HidP_GetCaps(preparsed, out caps) != HidStatusSuccess) continue;
                                devices.Add(new HidDeviceInfo { Path = path, Vid = attributes.Vid, Pid = attributes.Pid, Page = caps.UsagePage, Usage = caps.Usage, InputLength = caps.InputLength, OutputLength = caps.OutputLength, FeatureLength = caps.FeatureLength });
                            }
                            finally { HidD_FreePreparsedData(preparsed); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return devices;
        }

        private static bool WriteReport(SafeFileHandle handle, byte[] report, uint timeout, out uint written)
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
            Overlapped overlapped = new Overlapped(); overlapped.Event = ev;
            bool ok = WriteFile(handle, report, (uint)report.Length, out written, ref overlapped);
            int error = Marshal.GetLastWin32Error();
            if (!ok && error == ErrorIoPending && WaitForSingleObject(ev, timeout) == 0)
                ok = GetOverlappedResult(handle, ref overlapped, out written, false);
            else if (!ok && error == ErrorIoPending)
            {
                CancelIoEx(handle, ref overlapped);
                WaitForSingleObject(ev, 100);
            }
            CloseHandle(ev);
            return ok;
        }

        private static byte[] TryReadReport(SafeFileHandle handle, int length, uint timeout)
        {
            byte[] buffer = new byte[Math.Max(2, (int)length)];
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
            Overlapped overlapped = new Overlapped(); overlapped.Event = ev;
            uint read;
            bool ok = ReadFile(handle, buffer, (uint)buffer.Length, out read, ref overlapped);
            int error = Marshal.GetLastWin32Error();
            if (!ok && error == ErrorIoPending && WaitForSingleObject(ev, timeout) == 0)
                ok = GetOverlappedResult(handle, ref overlapped, out read, false);
            else if (!ok && error == ErrorIoPending)
            {
                CancelIoEx(handle, ref overlapped);
                WaitForSingleObject(ev, 100);
            }
            CloseHandle(ev);
            if (!ok || read == 0) return null;
            byte[] result = new byte[read];
            Array.Copy(buffer, result, read);
            return result;
        }
    }
}
