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
using System.Management;
using System.IO;


namespace SystemCareLite
{
    public class MainPopup : Form
    {
        // UI
        private TableLayoutPanel mainLayout;
        private FlowLayoutPanel statsPanel, networkPanel, tempPanel, diskPanel;
        private Label ramLabel, cpuLabel, gpuLabel, pingLabel, fpsLabel, 
                     dlSpeedLabel, ulSpeedLabel, cpuTempLabel, gpuTempLabel, diskLabel;
        private Button hideButton, expandButton;
        private const int PADDING = 3;
        private readonly Font labelFont = new Font("Segoe UI", 8f, FontStyle.Regular);
        private readonly Color backgroundColor = Color.FromArgb(30, 30, 32);
        private readonly Color labelBackColor = Color.FromArgb(45, 45, 48);
        private readonly Color highlightColor = Color.FromArgb(0, 122, 204);

        // Performance counters
        private PerformanceCounter cpuCounter;
        private float cachedGpuUsage = 0;
        private long ping = 0;
        private Random fpsMock = new();
        
        // Network monitoring
        private PerformanceCounter netSentCounter;
        private PerformanceCounter netRecvCounter;
        private long lastNetSent = 0;
        private long lastNetRecv = 0;
        private DateTime lastNetUpdate = DateTime.Now;
        
        // Disk monitoring
        private PerformanceCounter diskReadCounter;
        private PerformanceCounter diskWriteCounter;
        private long lastDiskRead = 0;
        private long lastDiskWrite = 0;
        private DateTime lastDiskUpdate = DateTime.Now;
        
        // Temperature monitoring
        private ManagementObjectSearcher cpuTempSearcher;
        private ManagementObjectSearcher gpuTempSearcher;

        // Visibility persistence
        public Dictionary<string,bool> StatVisibility { get; private set; } = new()
        {
            ["RAM"]=true, ["CPU"]=true, ["GPU"]=true, ["Ping"]=true, ["FPS"]=true,
            ["Download"]=true, ["Upload"]=true, ["CPU Temp"]=true, ["GPU Temp"]=true, ["Disk"]=true
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
            BackColor = backgroundColor;
            ForeColor = Color.White;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Width = 380; // Reduced from 420
            Height = 55; // Reduced from 50
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point((wa.Width - Width) / 2, wa.Height - Height - 100);
            Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width, Height, 15, 15)); // Smaller border radius

            // Main layout
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                AutoSize = true,
                Padding = new Padding(PADDING),
                BackColor = Color.Black,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            
            // Main stats panel (CPU, RAM, GPU, Ping, FPS)
            statsPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 5)
            };
            
            // Network panel
            networkPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 5)
            };
            
            // Temperature panel
            tempPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 5)
            };
            
            // Disk panel
            diskPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent
            };
            
            // Create and style labels with consistent padding
            ramLabel = MakeLabel("RAM: --%", 10);
            cpuLabel = MakeLabel("CPU: --%", 10);
            gpuLabel = MakeLabel("GPU: --%", 10);
            pingLabel = MakeLabel("Ping: --ms", 10);
            fpsLabel = MakeLabel("FPS: --", 10);
            dlSpeedLabel = MakeLabel("▼ -- MB/s", 10);
            ulSpeedLabel = MakeLabel("▲ -- MB/s", 10);
            cpuTempLabel = MakeLabel("CPU: --°C", 10);
            gpuTempLabel = MakeLabel("GPU: --°C", 10);
            diskLabel = MakeLabel("💾 --%", 10);
            
            // Add labels to their respective panels
            statsPanel.Controls.AddRange(new Control[] {
                cpuLabel, ramLabel, gpuLabel, pingLabel, fpsLabel
            });
            
            networkPanel.Controls.AddRange(new Control[] {
                dlSpeedLabel, ulSpeedLabel
            });
            
            tempPanel.Controls.AddRange(new Control[] {
                cpuTempLabel, gpuTempLabel
            });
            
            diskPanel.Controls.Add(diskLabel);
            
            // Add panels to main layout
            mainLayout.Controls.Add(statsPanel, 0, 0);
            mainLayout.Controls.Add(networkPanel, 0, 1);
            mainLayout.Controls.Add(tempPanel, 0, 2);
            mainLayout.Controls.Add(diskPanel, 0, 3);
            
            // Initialize performance counters
            InitializePerformanceCounters();
            
            // Add main layout to form
            Controls.Add(mainLayout);
            
            // Close & expand buttons
            hideButton = MakeButton("✕", new Point(Width - 22, 4), (_,__) => Application.Exit());
            expandButton = MakeButton("⋮", new Point(Width - 22, Height - 22), (_,__) => ToggleExtended());
            
            // Style buttons
            hideButton.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            expandButton.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            
            Controls.Add(hideButton);
            Controls.Add(expandButton);
            
            // Ensure buttons stay on top
            hideButton.BringToFront();
            expandButton.BringToFront();
            
            // Set form size based on content
            Size = new Size(420, mainLayout.Height + 20);

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
                // Position the popup directly below the main form
                extendedPopup.Location = new Point(Left, Location.Y + Height + 2);
                extendedPopup.Show();
                expandButton.Text = "︽";
            }
            else
            {
                extendedPopup.Close();
                extendedPopup = null;
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
                case "Download": dlSpeedLabel.Visible = visible; break;
                case "Upload": ulSpeedLabel.Visible = visible; break;
                case "CPU Temp": cpuTempLabel.Visible = visible; break;
                case "GPU Temp": gpuTempLabel.Visible = visible; break;
                case "Disk": diskLabel.Visible = visible; break;
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

        private void InitializePerformanceCounters()
        {
            try
            {
                // Network counters - handle case where the interface might not exist
                try
                {
                    var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(ni => ni.OperationalStatus == OperationalStatus.Up && 
                                   ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                   !ni.Description.Contains("Virtual") &&
                                   !ni.Description.Contains("VPN"))
                        .ToList();

                    foreach (var ni in networkInterfaces)
                    {
                        try
                        {
                            string instanceName = ni.Name.Replace('(', '[').Replace(']', ')');
                            netSentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instanceName);
                            netRecvCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", instanceName);
                            netSentCounter.NextValue();
                            netRecvCounter.NextValue();
                            break; // Use the first working interface
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to initialize network counter for {ni.Name}: {ex.Message}");
                            netSentCounter = null;
                            netRecvCounter = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Network interface initialization failed: {ex.Message}");
                    netSentCounter = null;
                    netRecvCounter = null;
                }

                // Disk counters
                try
                {
                    diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    diskReadCounter.NextValue();
                    diskWriteCounter.NextValue();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Disk counter initialization failed: {ex.Message}");
                    diskReadCounter = null;
                    diskWriteCounter = null;
                }

                // Temperature monitoring - wrap in try-catch to prevent crashes
                try
                {
                    cpuTempSearcher = new ManagementObjectSearcher(
                        "root\\WMI", 
                        "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                    
                    gpuTempSearcher = new ManagementObjectSearcher(
                        "root\\CIMV2", 
                        "SELECT * FROM Win32_VideoController");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Temperature monitoring initialization failed: {ex.Message}");
                    cpuTempSearcher = null;
                    gpuTempSearcher = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize performance counters: {ex.Message}");
            }
        }

        private float GetCpuTemperature()
        {
            try
            {
                if (cpuTempSearcher == null)
                    return 0;

                foreach (ManagementObject obj in cpuTempSearcher.Get())
                {
                    if (obj["CurrentTemperature"] != null)
                    {
                        double temp = Convert.ToDouble(obj["CurrentTemperature"].ToString());
                        return (float)(temp / 10.0 - 273.15); // Convert to Celsius
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting CPU temperature: {ex.Message}");
            }
            return 0;
        }

        private float GetGpuTemperature()
        {
            try
            {
                if (gpuTempSearcher == null)
                    return 0;

                foreach (ManagementObject obj in gpuTempSearcher.Get())
                {
                    // Try to get temperature if available
                    if (obj["CurrentTemperature"] != null)
                    {
                        return Convert.ToSingle(obj["CurrentTemperature"]) - 273.15f;
                    }
                    // Alternative temperature property used by some GPUs
                    else if (obj["Temperature"] != null)
                    {
                        return Convert.ToSingle(obj["Temperature"]);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting GPU temperature: {ex.Message}");
            }
            return 0;
        }

        private (float sent, float received) GetNetworkSpeed()
        {
            try
            {
                if (netSentCounter == null || netRecvCounter == null)
                    return (0, 0);

                float sent = netSentCounter.NextValue();
                float received = netRecvCounter.NextValue();
                return (sent, received);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting network speed: {ex.Message}");
                return (0, 0);
            }
        }

        private (float read, float write) GetDiskSpeed()
        {
            if (diskReadCounter == null || diskWriteCounter == null)
                return (0, 0);

            float read = diskReadCounter.NextValue();
            float write = diskWriteCounter.NextValue();
            return (read, write);
        }

        private float GetDiskUsage()
        {
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                if (drive.IsReady)
                {
                    float totalSpace = drive.TotalSize;
                    float freeSpace = drive.AvailableFreeSpace;
                    return ((totalSpace - freeSpace) / totalSpace) * 100;
                }
            }
            catch { }
            return 0;
        }

        private void UpdateUI()
        {
            try
            {
                // Update basic metrics - these are always visible
                cpuLabel.Text = $"CPU {Math.Min(100, cpuCounter.NextValue()):F0}%";
                ramLabel.Text = $"RAM {Math.Min(100, GetRamUsage()):F0}%";
                gpuLabel.Text = $"GPU {Math.Min(100, cachedGpuUsage):F0}%";
                pingLabel.Text = $"PING {ping}ms";
                fpsLabel.Text = IsFullscreen() ? $"FPS {fpsMock.Next(200,245)}" : "FPS --";
                
                // Update network speeds if counters are available
                if (netSentCounter != null && netRecvCounter != null)
                {
                    var (sent, received) = GetNetworkSpeed();
                    dlSpeedLabel.Text = $"▼ {received / (1024 * 1024):0.1}";
                    ulSpeedLabel.Text = $"▲ {sent / (1024 * 1024):0.1}";
                }
                else
                {
                    dlSpeedLabel.Text = "▼ --";
                    ulSpeedLabel.Text = "▲ --";
                }
                
                // Update temperatures if available
                try
                {
                    float cpuTemp = GetCpuTemperature();
                    cpuTempLabel.Text = cpuTemp > 0 ? $"CPU {cpuTemp:0}°" : "CPU --°";
                }
                catch
                {
                    cpuTempLabel.Text = "CPU --°C";
                }

                try
                {
                    float gpuTemp = GetGpuTemperature();
                    gpuTempLabel.Text = gpuTemp > 0 ? $"GPU {gpuTemp:0}°" : "GPU --°";
                }
                catch
                {
                    gpuTempLabel.Text = "GPU --°C";
                }
                
                // Update disk usage if available
                try
                {
                    float diskUsage = GetDiskUsage();
                    diskLabel.Text = $"💾 {Math.Min(100, diskUsage):F0}%";
                }
                catch
                {
                    diskLabel.Text = "💾 --%";
                }
                
                // Ensure the form is properly sized for the content
                Size = new Size(420, mainLayout.Height + 20);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in UpdateUI: {ex.Message}");
            }
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
        private Label MakeLabel(string text, int rightPadding = 0) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(2, 1, rightPadding, 1), // Reduced margins
            Padding = new Padding(3, 1, 3, 1), // Reduced padding
            Font = labelFont,
            ForeColor = Color.White,
            BackColor = labelBackColor,
            MinimumSize = new Size(60, 18), // More compact
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.None
        };
        private Button MakeButton(string txt, Point loc, EventHandler onClick)
        {
            var b = new Button
            {
                Text = txt,
                Width = 18, Height = 18, // Slightly smaller buttons
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Location = loc,
                FlatAppearance = {
                    BorderSize = 0,
                    MouseOverBackColor = highlightColor,
                    MouseDownBackColor = Color.FromArgb(0, 92, 156)
                },
                Cursor = Cursors.Hand
            };
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
