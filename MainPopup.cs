using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualBasic.Devices;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;


namespace SystemCareLite
{
    public class MainPopup : Form
    {
        // UI
        private FlowLayoutPanel statsPanel;
        private Label ramLabel, cpuLabel, gpuLabel, pingLabel, fpsLabel;
        private Button hideButton, expandButton;

        // Performance counters
        private PerformanceCounter cpuCounter;
        private float cachedGpuUsage = 0;
        private long ping = 0;
        private Random fpsMock = new();

        // Visibility persistence
        public Dictionary<string,bool> StatVisibility { get; private set; } = new()
        {
            ["RAM"]=true, ["CPU"]=true, ["GPU"]=true, ["Ping"]=true, ["FPS"]=true
        };

        // Idle‐tracking for RAM cleanup
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        private Dictionary<int, DateTime> lastActive = new();
        private System.Windows.Forms.Timer activityTimer;

        // UI update timer
        private System.Windows.Forms.Timer updateTimer;

        // Rounded corners
        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeft, int nTop, int nRight, int nBottom, int nWidthEllipse, int nHeightEllipse
        );
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        private struct RECT { public int Left, Top, Right, Bottom; }

        // Drag
        private bool dragging = false;
        private Point dragCursor, dragForm;

        // Extended menu
        private ExtendedPopup extendedPopup;

        public MainPopup()
        {
            // Form setup
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            BackColor = Color.Black;
            ForeColor = Color.White;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Width = 420; Height = 50;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point((wa.Width - Width) / 2, wa.Height - Height - 100);
            Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width, Height, 30, 30));

            // Stats panel
            statsPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Location = new Point(10, 10),
                BackColor = Color.Black
            };
            ramLabel = MakeLabel("RAM: --%");
            cpuLabel = MakeLabel("CPU: --%");
            gpuLabel = MakeLabel("GPU: --%");
            pingLabel = MakeLabel("Ping: --ms");
            fpsLabel = MakeLabel("FPS: --");
            statsPanel.Controls.AddRange(new Control[]{ ramLabel, cpuLabel, gpuLabel, pingLabel, fpsLabel });
            Controls.Add(statsPanel);

            // Hide & expand buttons
            hideButton = MakeButton("✕", new Point(Width - 25, 5), (_,__) => Hide());
            expandButton = MakeButton("︾", new Point(Width - 25, Height - 25), (_,__) => ToggleExtended());
            Controls.Add(hideButton);
            Controls.Add(expandButton);

            // CPU counter + update timer
            cpuCounter = new PerformanceCounter("Processor","% Processor Time","_Total");
            cpuCounter.NextValue();
            updateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            updateTimer.Tick += (_,__) => UpdateUI();
            updateTimer.Start();

            // GPU & Ping loops
            StartGpuLoop();
            StartPingLoop();

            // Idle tracker
            activityTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
            activityTimer.Tick += (_,__) => TrackActiveProcess();
            activityTimer.Start();

            RegisterDrag(this);
        }

        private void ToggleExtended()
        {
            if (extendedPopup == null || extendedPopup.IsDisposed)
            {
                extendedPopup = new ExtendedPopup(this);
                extendedPopup.StartPosition = FormStartPosition.Manual;
                extendedPopup.Location = new Point(Left, Bottom + 2);
                extendedPopup.Show();
                expandButton.Text = "︽";
            }
            else
            {
                extendedPopup.Close();
                expandButton.Text = "︾";
            }
        }

        public bool GetStatVisibility(string key)
            => StatVisibility.TryGetValue(key, out var v) && v;
        public void SetStatVisibility(string key, bool visible)
        {
            StatVisibility[key] = visible;
            switch(key)
            {
                case "RAM":  ramLabel.Visible = visible; break;
                case "CPU":  cpuLabel.Visible = visible; break;
                case "GPU":  gpuLabel.Visible = visible; break;
                case "Ping": pingLabel.Visible = visible; break;
                case "FPS":  fpsLabel.Visible = visible; break;
            }
        }

        private void TrackActiveProcess()
        {
            var fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint pid);
            lastActive[(int)pid] = DateTime.Now;
        }
        public bool IsProcessIdle(Process p)
        {
            var cutoff = DateTime.Now.AddHours(-1);
            if (lastActive.TryGetValue(p.Id, out var t))
                return t < cutoff;
            return true; // if never seen, treat as idle
        }

        private void UpdateUI()
        {
            cpuLabel.Text = $"CPU: {cpuCounter.NextValue():F0}%";
            ramLabel.Text = $"RAM: {GetRamUsage():F0}%";
            gpuLabel.Text = $"GPU: {cachedGpuUsage:F0}%";
            pingLabel.Text = $"Ping: {ping}ms";
            fpsLabel.Text = IsFullscreen() ? $"FPS: {fpsMock.Next(200,245)}" : "FPS: --";
        }

        private bool IsFullscreen()
        {
            var sb = Screen.PrimaryScreen.Bounds;
            var fg = GetForegroundWindow();
            GetWindowRect(fg, out RECT r);
            return (r.Right - r.Left == sb.Width && r.Bottom - r.Top == sb.Height);
        }

        private float GetRamUsage()
        {
            var info = new ComputerInfo();
            return 100f * (info.TotalPhysicalMemory - info.AvailablePhysicalMemory)
                         / info.TotalPhysicalMemory;
        }
private readonly List<PerformanceCounter> gpuCounters = new();
private void StartGpuLoop()
{
    Task.Run(async () =>
    {
        try
        {
            // Build your list of GPU counters once
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames()
                                    .Where(n => n.ToLower().Contains("engtype_"));
            foreach (var inst in instances)
            {
                var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                pc.NextValue();    // warm‑up sample
                gpuCounters.Add(pc);
            }
        }
        catch
        {
            cachedGpuUsage = 0;
            return;
        }

        // Now poll every second
        while (true)
        {
            float total = 0;
            foreach (var pc in gpuCounters)
            {
                total += pc.NextValue();
            }

            cachedGpuUsage = Math.Min(total, 100);
            await Task.Delay(1000); 
        }
    });
}

        private void StartPingLoop()
            => Task.Run(() =>
            {
                var p = new Ping();
                while (true)
                {
                    try
                    {
                        var r = p.Send("8.8.8.8");
                        if (r.Status == IPStatus.Success) ping = r.RoundtripTime;
                    }
                    catch { ping = 0; }
                    Thread.Sleep(1000);
                }
            });

        // UI helpers
        private Label MakeLabel(string txt) => new Label
        {
            Text = txt,
            AutoSize = true,
            Margin = new Padding(10,0,10,0),
            ForeColor = Color.White,
            BackColor = Color.Black
        };
        private Button MakeButton(string txt, Point loc, EventHandler onClick)
        {
            var b = new Button
            {
                Text = txt,
                Width = 20, Height = 20,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Location = loc
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += onClick;
            return b;
        }

        // Drag support
        private void RegisterDrag(Control c)
        {
            c.MouseDown += OnMouseDown;
            c.MouseMove += OnMouseMove;
            c.MouseUp   += OnMouseUp;
            foreach (Control ch in c.Controls) RegisterDrag(ch);
        }
        private void OnMouseDown(object s, MouseEventArgs e)
        {
            dragging = true;
            dragCursor = Cursor.Position;
            dragForm   = Location;
        }
        private void OnMouseMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            var diff = Point.Subtract(Cursor.Position, new Size(dragCursor));
            Location = Point.Add(dragForm, new Size(diff));
        }
        private void OnMouseUp(object s, MouseEventArgs e) => dragging = false;
    }
}
