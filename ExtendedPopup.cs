using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SystemCareLite
{
    public class ExtendedPopup : Form
    {
        private readonly MainPopup main;
        private CancellationTokenSource _updateCts;
        private CancellationTokenSource _shortcutCts;

        private Panel perfPanel;
        private CheckedListBox perfList;

        // Performance monitoring
        private PerformanceCounter netSentCounter;
        private PerformanceCounter netRecvCounter;
        private PerformanceCounter diskReadCounter;
        private PerformanceCounter diskWriteCounter;
        private long lastNetSent = 0;
        private long lastNetRecv = 0;
        private long lastDiskRead = 0;
        private long lastDiskWrite = 0;
        private DateTime lastUpdateTime = DateTime.Now;
        private System.Windows.Forms.Timer perfUpdateTimer;

        // ─────── P/Invoke & constants ─────────────────────────────────
        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(
            IntPtr hwndParent,
            string HardwareId,
            string FullInfPath,
            uint InstallFlags,
            out bool pRebootRequired
        );
        private const uint INSTALLFLAG_FORCE = 0x00000001;

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(
            IntPtr hwnd,
            string pszRootPath,
            uint dwFlags
        );

        public ExtendedPopup(MainPopup parent)
        {
            main = parent;
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = Color.FromArgb(30,30,30);
            Size            = new Size(420,350);

            var topBar = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Dock          = DockStyle.Top,
                Height        = 35,
                Padding       = new Padding(5),
                BackColor     = Color.FromArgb(40,40,40)
            };
            topBar.Controls.AddRange(new Control[]
            {
                MakeTopButton("Performance",     TogglePerfOptions),
                MakeTopButton("Service Cleanup", ShowServiceCleanup),
                MakeTopButton("Junk Cleaner",    ShowJunkCleaner),
                MakeTopButton("Shortcut Fixer",  ShowShortcutFixer),
            });

            var driverBar = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Dock          = DockStyle.Top,
                Height        = 35,
                Padding       = new Padding(10,5,5,5),
                BackColor     = Color.FromArgb(35,35,35)
            };
            var driverBtn = new Button
            {
                Text      = "Update Drivers/Apps",
                AutoSize  = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Margin    = new Padding(0)
            };
            driverBtn.FlatAppearance.BorderSize = 1;
            driverBtn.Click += (_,__) => ShowDriverUpdate();
            driverBar.Controls.Add(driverBtn);

            perfPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 140,
                BackColor = Color.FromArgb(30,30,30),
                Visible   = false
            };

            Controls.Add(topBar);
            Controls.Add(driverBar);
            Controls.Add(perfPanel);
        }

         private Button MakeTopButton(string text, Action onClick)
        {
            var b = new Button
            {
                Text      = text,
                AutoSize  = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Margin    = new Padding(5,3,5,3)
            };
            b.FlatAppearance.BorderSize = 1;
            b.Click += (_,__) => onClick();
            return b;
        }
         private void TogglePerfOptions()
        {
            perfPanel.Controls.Clear();
            perfList = new CheckedListBox
            {
                Dock         = DockStyle.Fill,
                CheckOnClick = true,
                BackColor    = Color.FromArgb(30,30,30),
                ForeColor    = Color.White,
                BorderStyle  = BorderStyle.None
            };
            foreach (var key in new[] {
                "RAM","CPU","GPU","Ping","FPS",
                "Download","Upload","CPU Temp","GPU Temp","Disk"
            })
                perfList.Items.Add(key, main.GetStatVisibility(key));

            perfList.ItemCheck += (s,e) =>
            {
                var stat = perfList.Items[e.Index].ToString()!;
                var now  = e.NewValue == CheckState.Checked;
                main.SetStatVisibility(stat, now);
            };

            perfPanel.Controls.Add(perfList);
            perfPanel.Visible = true;
        }

        // ─────── Driver & App Update via winget ─────────────────────
        private void ShowDriverUpdate()
        {
            _updateCts?.Cancel();
            _updateCts = new CancellationTokenSource();
            var token = _updateCts.Token;

            var scanning = new Form
            {
                Text = "Update Drivers/Apps",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(350, 100),
                ControlBox = false
            };
            var status = new Label
            {
                Text = "Scanning for winget‑managed upgrades…",
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleCenter
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                Dock = DockStyle.Bottom,
                Height = 30,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.Click += (_, __) => _updateCts.Cancel();
            scanning.Controls.Add(status);
            scanning.Controls.Add(btnCancel);
            scanning.Show(this);

            Task.Run(async () =>
            {
                var pkgs = new List<(string Name, string Id)>();
                try
                {
                    var psi = new ProcessStartInfo("winget", "upgrade --all --disable-interactivity")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) throw new InvalidOperationException("Failed to start winget");

                    using var reader = proc.StandardOutput;
                    string line;
                    var allLines = new List<string>();
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        allLines.Add(line);
                        if (token.IsCancellationRequested)
                            throw new OperationCanceledException(token);

                        Invoke((Action)(() => status.Text = "Scanning: " + line.Trim()));
                    }
                    proc.WaitForExit();
                    token.ThrowIfCancellationRequested();

                    var lines = allLines.ToArray();
                    int idxH = Array.FindIndex(lines, l => l.StartsWith("Name", StringComparison.OrdinalIgnoreCase));
                    if (idxH >= 0 && idxH + 2 < lines.Length)
                    {
                        foreach (var dataLine in lines.Skip(idxH + 2))
                        {
                            var cols = Regex.Split(dataLine.Trim(), @"\s{2,}");
                            if (cols.Length >= 2)
                                pkgs.Add((cols[0], cols[1]));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // user hit Cancel
                }
                catch (Exception ex)
                {
                    Invoke((Action)(() =>
                    {
                        scanning.Close();
                        MessageBox.Show(
                            $"Error running winget:\n{ex.Message}",
                            "Update Drivers/Apps",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                    }));
                    return;
                }

                Invoke((Action)(() =>
                {
                    scanning.Close();
                    if (token.IsCancellationRequested) return;

                    if (pkgs.Count == 0)
                    {
                        MessageBox.Show(
                            "No winget‑managed upgrades found.",
                            "Update Drivers/Apps",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                        return;
                    }

                    var items = pkgs.Select(p => $"{p.Name} → {p.Id}").ToArray();
                    var checks = Enumerable.Repeat(true, items.Length).ToArray();

                    ShowConfirmDialog(
                        "Pick packages to upgrade",
                        items, checks,
                        async selected =>
                        {
                            try
                            {
                                foreach (var idx in selected)
                                {
                                    token.ThrowIfCancellationRequested();
                                    var (name, id) = pkgs[idx];
                                    status.Text = "Installing: " + name;
                                    scanning = new Form
                                    {
                                        Text = "Update Drivers/Apps",
                                        StartPosition = FormStartPosition.CenterParent,
                                        Size = new Size(350, 80),
                                        ControlBox = false
                                    };
                                    scanning.Controls.Add(new Label
                                    {
                                        Text = status.Text,
                                        Dock = DockStyle.Fill,
                                        TextAlign = ContentAlignment.MiddleCenter
                                    });
                                    scanning.Show(this);

                                    var installPsi = new ProcessStartInfo("winget",
                                        $"upgrade --id \"{id}\" --accept-source-agreements --accept-package-agreements")
                                    {
                                        UseShellExecute = true,
                                        CreateNoWindow = true
                                    };
                                    using var p2 = Process.Start(installPsi);
                                    p2?.WaitForExit();
                                    scanning.Close();
                                }

                                MessageBox.Show(
                                    "Selected packages have been successfully updated.",
                                    "Update Drivers/Apps",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Information
                                );
                            }
                            catch (OperationCanceledException)
                            {
                                MessageBox.Show(
                                    "Update canceled.",
                                    "Update Drivers/Apps",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning
                                );
                            }
                        }
                    );
                }));
            }, token);
        }

        // ─────── Service Cleanup ─────────────────────────────────────────
        private void ShowServiceCleanup()
        {
            MessageBox.Show(
                "Scanning auto‑started services for memory usage…",
                "Service Cleanup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            var mos = new ManagementObjectSearcher(
                "SELECT Name,DisplayName,ProcessId,StartMode FROM Win32_Service"
            );
            var list = mos.Get().Cast<ManagementObject>()
                          .Select(mo => new
                          {
                              Name = (string)mo["Name"],
                              Display = (string)mo["DisplayName"],
                              Pid = (uint)mo["ProcessId"],
                              StartMode = (string)mo["StartMode"]
                          })
                          .Where(s => s.Pid > 0
                                   && s.StartMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                          .ToList();

            var withMem = new List<(string Name, string Disp, long M)>();
            foreach (var svc in list)
            {
                try { withMem.Add((svc.Name, svc.Display, Process.GetProcessById((int)svc.Pid).WorkingSet64)); }
                catch { }
            }

            var sorted = withMem.OrderByDescending(x => x.M).ToList();
            int rec = Math.Min(5, sorted.Count);
            var top5 = sorted.Take(rec).ToList();
            var rest = sorted.Skip(rec).ToList();

            var items = new List<string>();
            var checks = new List<bool>();
            foreach (var s in top5) { items.Add($"[Recommended] {s.Disp} — {s.M / 1024 / 1024} MB"); checks.Add(true); }
            foreach (var s in rest) { items.Add($"{s.Disp} — {s.M / 1024 / 1024} MB"); checks.Add(false); }

            ShowConfirmDialog(
                "Stop & Disable Services",
                items.ToArray(),
                checks.ToArray(),
                selected =>
                {
                    foreach (int i in selected)
                    {
                        var svc = i < rec ? top5[i] : rest[i - rec];
                        using var sc = new ServiceController(svc.Name);
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        }
                        using var mo = new ManagementObject($"Win32_Service.Name='{svc.Name}'");
                        mo.InvokeMethod("ChangeStartMode", new object[] { "Manual" });
                    }
                }
            );
        }

        // ─────── Junk Cleaner (Storage Sense style) ───────────────────
        private void ShowJunkCleaner()
        {
            // 1) Define each category and its scanner
            var cutoff = DateTime.Now.AddDays(-7);
            var cats = new List<(string Title, Func<List<string>> Scan, long SizeBytes)>()
    {
        (
            "Old Temp files (> 7 days)",
            () => ScanByAge(cutoff,
                Environment.GetEnvironmentVariable("TEMP")!,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
            ),
            0
        ),
        (
            "Downloads folder",
            () => ScanFolder(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            ),
            0
        ),
        (
            "Windows Update cache",
            () => ScanFolder(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                             "SoftwareDistribution", "Download")
            ),
            0
        ),
        (
            "Thumbnail cache",
            () => ScanFolderFilter(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Microsoft", "Windows", "Explorer"),
                "*.db"
            ),
            0
        ),
        (
            "Empty Recycle Bin",
            () => new List<string>(),    // we'll call SHEmptyRecycleBin instead of scanning
            0
        )
    };

            // 2) Compute sizes and build display strings
            var displayTitles = cats
                .Select(cat =>
                {
                    long bytes = 0;
                    if (cat.Title != "Empty Recycle Bin")
                    {
                        foreach (var f in cat.Scan())
                        {
                            try { bytes += new FileInfo(f).Length; } catch { }
                        }
                    }
                    var human = $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
                    return (Title: $"{cat.Title} — {human}", Scan: cat.Scan, SizeBytes: bytes);
                })
                .ToList();

            // 3) Let user pick categories
            var choices = ShowCheckedDialog(
                "Select Temporary Categories to Delete",
                displayTitles.Select(d => d.Title).ToArray(),
                Enumerable.Repeat(true, displayTitles.Count).ToArray()
            );
            if (choices == null || choices.Length == 0) return;

            // 4) Empty recycle bin right away if chosen
            var recycleIdx = displayTitles.FindIndex(d => d.Title.StartsWith("Empty Recycle Bin"));
            if (Array.IndexOf(choices, recycleIdx) >= 0)
                SHEmptyRecycleBin(IntPtr.Zero, null, 0);

            // 5) Gather all files from chosen categories
            var toDelete = new List<string>();
            foreach (var idx in choices)
            {
                if (idx == recycleIdx) continue;
                toDelete.AddRange(displayTitles[idx].Scan());
            }
            toDelete = toDelete.Distinct().ToList();
            if (toDelete.Count == 0)
            {
                MessageBox.Show("Nothing to clean up.", "Junk Cleaner",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 6) Show list of *files* (capped at 500) for final confirmation
            var preview = toDelete.Take(500).ToArray();
            var finalPick = ShowCheckedDialog(
                $"Delete {preview.Length} files?",
                preview,
                Enumerable.Repeat(true, preview.Length).ToArray()
            );
            if (finalPick == null || finalPick.Length == 0) return;

            // 7) Delete and tally freed space
            long freed = 0;
            foreach (var i in finalPick)
            {
                try
                {
                    var fi = new FileInfo(preview[i]);
                    freed += fi.Length;
                    fi.Delete();
                }
                catch { }
            }
            var freedGb = freed / 1024.0 / 1024.0 / 1024.0;
            MessageBox.Show(
                $"Deleted {finalPick.Length} files and freed {freedGb:0.##} GB.",
                "Junk Cleaner",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }

        // ─────── Shortcut Fixer ─────────────────────────────────────
        private void ShowShortcutFixer()
        {
            _shortcutCts?.Cancel();
            _shortcutCts = new CancellationTokenSource();
            var token = _shortcutCts.Token;

            var roots = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .Concat(new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                })
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var dlg = new Form
            {
                Text = "Shortcut Fixer",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(500, 120),
                ControlBox = false
            };
            var lbl = new Label
            {
                Text = "Starting scan…",
                Dock = DockStyle.Top,
                Height = 50,
                TextAlign = ContentAlignment.MiddleCenter
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                Dock = DockStyle.Bottom,
                Height = 30,
                DialogResult = DialogResult.Cancel
            };
            btnCancel.Click += (_, __) => _shortcutCts.Cancel();
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(btnCancel);
            dlg.Show(this);

            var wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));

            Task.Run(() =>
            {
                var broken = new List<string>();

                foreach (var root in roots)
                {
                    if (token.IsCancellationRequested) break;

                    foreach (var lnk in EnumerateFilesSafe(root, "*.lnk", token))
                    {
                        if (token.IsCancellationRequested) break;

                        Invoke((Action)(() => lbl.Text = "Scanning:\n" + lnk));
                        if (lnk.IndexOf(@"\Windows\WinSxS\", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        try
                        {
                            dynamic sc = wsh.GetType()
                                .InvokeMember("CreateShortcut",
                                               BindingFlags.InvokeMethod,
                                               null, wsh,
                                               new object[] { lnk });
                            string target = (string)sc.TargetPath;
                            if (string.IsNullOrWhiteSpace(target)) continue;
                            if (!File.Exists(target) && !Directory.Exists(target))
                                broken.Add(lnk);
                        }
                        catch { }
                    }
                }

                Invoke((Action)(() =>
                {
                    dlg.Close();
                    if (token.IsCancellationRequested) return;

                    var unique = broken.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (unique.Length == 0)
                    {
                        MessageBox.Show(
                            "No invalid shortcuts found.",
                            "Shortcut Fixer",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                        return;
                    }

                    ShowConfirmDialog(
                        "Remove Invalid Shortcuts",
                        unique,
                        Enumerable.Repeat(true, unique.Length).ToArray(),
                        selected =>
                        {
                            foreach (var idx in selected)
                                try { File.Delete(unique[idx]); } catch { }
                        }
                    );
                }));
            }, token);
        }

        // ─────── Helpers ────────────────────────────────────────────

        /// <summary>
/// Safely enumerate all files under <paramref name="root"/>,
/// skipping any directories or files that throw UnauthorizedAccessException, etc.
/// </summary>
private IEnumerable<string> EnumerateFilesSafe(string root)
{
    var dirs = new Stack<string>();
    dirs.Push(root);

    while (dirs.Count > 0)
    {
        var dir = dirs.Pop();
        string[] files = null, subdirs = null;

        try { files = Directory.GetFiles(dir); }
        catch { /* skip unreadable */ }
        if (files != null)
            foreach (var f in files)
                yield return f;

        try { subdirs = Directory.GetDirectories(dir); }
        catch { /* skip */ }
        if (subdirs != null)
            foreach (var d in subdirs)
                dirs.Push(d);
    }
}

/// <summary>
/// Same as above, but filters by <paramref name="pattern"/>
/// and respects <paramref name="token"/> cancellation.
/// </summary>
private IEnumerable<string> EnumerateFilesSafe(string root, string pattern, CancellationToken token)
{
    var dirs = new Stack<string>();
    dirs.Push(root);

    while (dirs.Count > 0)
    {
        if (token.IsCancellationRequested) yield break;
        var dir = dirs.Pop();
        string[] files = null, subdirs = null;

        try { files = Directory.GetFiles(dir, pattern); }
        catch { /* skip */ }
        if (files != null)
        {
            foreach (var f in files)
            {
                if (token.IsCancellationRequested) yield break;
                yield return f;
            }
        }

        try { subdirs = Directory.GetDirectories(dir); }
        catch { /* skip */ }
        if (subdirs != null)
            foreach (var d in subdirs)
                dirs.Push(d);
    }
}

/// <summary>
/// Enumerate all files older than <paramref name="cutoff"/> under the given <paramref name="roots"/>,
/// using the safe enumerator to avoid UnauthorizedAccessException.
/// </summary>
private List<string> ScanByAge(DateTime cutoff, params string[] roots)
{
    var list = new List<string>();
    foreach (var root in roots.Where(Directory.Exists))
    {
        foreach (var f in EnumerateFilesSafe(root))
        {
            try
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    list.Add(f);
            }
            catch { /* could not read timestamp, skip */ }
        }
    }
    return list;
}

/// <summary>
/// Enumerate every file under <paramref name="root"/> (all subfolders),
/// using the safe enumerator to avoid UnauthorizedAccessException.
/// </summary>
private List<string> ScanFolder(string root)
{
    var list = new List<string>();
    if (!Directory.Exists(root))
        return list;

    foreach (var f in EnumerateFilesSafe(root))
        list.Add(f);

    return list;
}

/// <summary>
/// Enumerate every file matching <paramref name="pattern"/> under <paramref name="root"/>,
/// using the safe enumerator to avoid UnauthorizedAccessException.
/// </summary>
private List<string> ScanFolderFilter(string root, string pattern)
{
    var list = new List<string>();
    if (!Directory.Exists(root))
        return list;

    foreach (var f in EnumerateFilesSafe(root, pattern, CancellationToken.None))
        list.Add(f);

    return list;
}

        /// <summary>
        /// Show a simple checked‐list dialog, returning the selected indices (or null if cancelled).
        /// </summary>
        private int[] ShowCheckedDialog(string title, string[] items, bool[] defaults)
        {
            var dlg = new Form
            {
                Text = title,
                Size = new Size(500, 600),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F)
            };

            var list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White
            };
            for (int i = 0; i < items.Length; i++)
                list.Items.Add(items[i], defaults[i]);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Color.FromArgb(40, 40, 40)
            };
            var ok = new Button { Text = "OK", AutoSize = true, FlatStyle = FlatStyle.Flat };
            ok.Click += (_, __) => dlg.DialogResult = DialogResult.OK;
            var cancel = new Button { Text = "Cancel", AutoSize = true, FlatStyle = FlatStyle.Flat };
            cancel.Click += (_, __) => dlg.DialogResult = DialogResult.Cancel;

            btnPanel.Controls.Add(ok);
            btnPanel.Controls.Add(cancel);

            dlg.Controls.Add(list);
            dlg.Controls.Add(btnPanel);

            return dlg.ShowDialog() == DialogResult.OK
                ? Enumerable.Range(0, list.Items.Count)
                            .Where(i => list.GetItemChecked(i))
                            .ToArray()
                : null;
        }

        /// <summary>
        /// Show a confirm dialog with a checked list, invoking <paramref name="onConfirm"/>
        /// with the selected indices when “Confirm” is clicked.
        /// </summary>
        private void ShowConfirmDialog(string title, string[] items, bool[] initialChecked, Action<int[]> onConfirm)
        {
            var dlg = new Form
            {
                Text = title,
                Size = new Size(500, 600),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F)
            };

            var list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White
            };
            for (int i = 0; i < items.Length; i++)
                list.Items.Add(items[i], initialChecked[i]);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Color.FromArgb(40, 40, 40)
            };
            var ok = new Button { Text = "Confirm", AutoSize = true, FlatStyle = FlatStyle.Flat };
            ok.Click += (_, __) =>
            {
                var sel = Enumerable.Range(0, list.Items.Count)
                                    .Where(j => list.GetItemChecked(j))
                                    .ToArray();
                onConfirm(sel);
                dlg.Close();
            };
            var cancel = new Button { Text = "Cancel", AutoSize = true, FlatStyle = FlatStyle.Flat };
            cancel.Click += (_, __) => dlg.Close();

            btnPanel.Controls.Add(ok);
            btnPanel.Controls.Add(cancel);

            dlg.Controls.Add(list);
            dlg.Controls.Add(btnPanel);
            dlg.ShowDialog();
        }
    }
}