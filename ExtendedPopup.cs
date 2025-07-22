using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;      // for WMI queries
using System.ServiceProcess; // for ServiceController
using System.Windows.Forms;

namespace SystemCareLite
{
    public class ExtendedPopup : Form
    {
        private readonly MainPopup main;
        private Panel perfPanel;
        private CheckedListBox perfList;

        public ExtendedPopup(MainPopup parent)
        {
            main = parent;

            // Form styling
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = Color.FromArgb(30, 30, 30);
            Size            = new Size(420, 350);

            // 1) Top row: Performance / Service / Junk / Shortcut
            var topBar = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Dock          = DockStyle.Top,
                Height        = 35,
                Padding       = new Padding(5),
                BackColor     = Color.FromArgb(40, 40, 40)
            };
            topBar.Controls.AddRange(new Control[]
            {
                MakeTopButton("Performance",     TogglePerfOptions),
                MakeTopButton("Service Cleanup", ShowServiceCleanup),
                MakeTopButton("Junk Cleaner",    ShowJunkCleaner),
                MakeTopButton("Shortcut Fixer",  ShowShortcutFixer)
            });

            // 2) Driver Update – sits under the top row
            var driverBar = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Dock          = DockStyle.Top,
                Height        = 35,
                Padding       = new Padding(10, 5, 5, 5),
                BackColor     = Color.FromArgb(35, 35, 35)
            };
            var driverBtn = new Button
            {
                Text      = "Driver Update",
                AutoSize  = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Margin    = new Padding(0)
            };
            driverBtn.FlatAppearance.BorderSize = 1;
            driverBtn.Click += (_,__) => ShowDriverScan();
            driverBar.Controls.Add(driverBtn);

            // 3) Performance checklist panel (initially hidden)
            perfPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 130,
                BackColor = Color.FromArgb(30, 30, 30),
                Visible   = false
            };

            // Add in correct z‑order: topBar at very top, then driverBar, then perfPanel
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
                Margin    = new Padding(5, 3, 5, 3)
            };
            b.FlatAppearance.BorderSize = 1;
            b.Click += (_,__) => onClick();
            return b;
        }

        // Rebuilds (and shows) the 5‑item checklist every time
        private void TogglePerfOptions()
        {
            // clear out any old controls
            perfPanel.Controls.Clear();

            // build a fresh CheckedListBox
            perfList = new CheckedListBox
            {
                CheckOnClick = true,
                Dock         = DockStyle.Fill,
                BackColor    = Color.FromArgb(30, 30, 30),
                ForeColor    = Color.White,
                BorderStyle  = BorderStyle.None
            };

            // add all five stats, using MainPopup's current visibility
            foreach (var key in new[] { "RAM", "CPU", "GPU", "Ping", "FPS" })
            {
                bool isChecked = main.GetStatVisibility(key);
                perfList.Items.Add(key, isChecked);
            }

            perfList.ItemCheck += (s, e) =>
            {
                var stat = perfList.Items[e.Index].ToString()!;
                bool now  = e.NewValue == CheckState.Checked;
                main.SetStatVisibility(stat, now);
            };

            perfPanel.Controls.Add(perfList);
            perfPanel.Visible = true;
        }

        // ----------------------------------------------------------------
        // Service Cleanup
        private void ShowServiceCleanup()
        {
            try
            {
                MessageBox.Show(
                    "Scanning auto‑started services for memory usage…",
                    "Service Cleanup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                // query all auto start services
                var mos = new ManagementObjectSearcher(
                    "SELECT Name,DisplayName,ProcessId,StartMode FROM Win32_Service"
                );
                var list = mos.Get()
                    .Cast<ManagementObject>()
                    .Select(mo => new {
                        Name      = (string)mo["Name"],
                        Display   = (string)mo["DisplayName"],
                        Pid       = (uint)mo["ProcessId"],
                        StartMode = (string)mo["StartMode"]
                    })
                    .Where(s => s.Pid > 0
                             && s.StartMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // collect memory usage
                var withMem = new List<(string Name, string Display, long Mem)>();
                foreach (var svc in list)
                {
                    try
                    {
                        var p = Process.GetProcessById((int)svc.Pid);
                        withMem.Add((svc.Name, svc.Display, p.WorkingSet64));
                    }
                    catch { }
                }

                // sort, recommend top 5
                var sorted = withMem.OrderByDescending(x => x.Mem).ToList();
                int rec    = Math.Min(5, sorted.Count);
                var top5   = sorted.Take(rec).ToList();
                var rest   = sorted.Skip(rec).ToList();

                var items  = new List<string>();
                var checks = new List<bool>();
                foreach (var s in top5)
                {
                    items .Add($"[Recommended] {s.Display} — {s.Mem/1024/1024} MB");
                    checks.Add(true);
                }
                foreach (var s in rest)
                {
                    items .Add($"{s.Display} — {s.Mem/1024/1024} MB");
                    checks.Add(false);
                }

                ShowConfirmDialog(
                    "Stop & Disable Services",
                    items.ToArray(),
                    checks.ToArray(),
                    selected =>
                    {
                        foreach (int i in selected)
                        {
                            var svc = i < rec
                                ? top5[i]
                                : rest[i - rec];
                            StopAndDisableService(svc.Name);
                        }
                    }
                );
            }
            catch (PlatformNotSupportedException)
            {
                MessageBox.Show(
                  "Service Cleanup isn’t supported in this trimmed build.\n" +
                  "Rebuild with PublishTrimmed=false.",
                  "Not Supported",
                  MessageBoxButtons.OK,
                  MessageBoxIcon.Warning
                );
            }
        }

        private void StopAndDisableService(string svcName)
        {
            try
            {
                using var sc = new ServiceController(svcName);
                if (sc.Status != ServiceControllerStatus.Stopped &&
                    sc.Status != ServiceControllerStatus.StopPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped,
                                     TimeSpan.FromSeconds(10));
                }
            }
            catch { }

            try
            {
                using var mo = new ManagementObject($"Win32_Service.Name='{svcName}'");
                mo.InvokeMethod("ChangeStartMode", new object[]{ "Manual" });
            }
            catch { }
        }

        // ----------------------------------------------------------------
        // Junk Cleaner
        private void ShowJunkCleaner()
        {
            MessageBox.Show(
                "Scanning TEMP folders (this may take a few seconds)…",
                "Junk Cleaner",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            var roots = new[] {
                Environment.GetEnvironmentVariable("TEMP")!,
                Path.Combine(
                    Environment.GetFolderPath(
                      Environment.SpecialFolder.Windows),
                    "Temp"
                )
            }
            .Where(Directory.Exists)
            .ToArray();

            var cutoff = DateTime.Now.AddDays(-7);
            var junk = roots
                .SelectMany(r => SafeEnumerateFiles(r))
                .Where(f =>
                {
                    try { return File.GetLastWriteTime(f) < cutoff; }
                    catch { return false; }
                })
                .Take(500)
                .ToArray();

            ShowConfirmDialog(
                "Delete Old Temp Files",
                junk,
                selected =>
                {
                    foreach (int i in selected)
                    {
                        try { File.Delete(junk[i]); }
                        catch { }
                    }
                }
            );
        }

        private IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files;
                try { files = Directory.GetFiles(dir); } catch { continue; }
                foreach (var f in files) yield return f;

                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { continue; }
                foreach (var d in subs) stack.Push(d);
            }
        }

        // ----------------------------------------------------------------
        // Shortcut Fixer
        private void ShowShortcutFixer()
        {
            var dirs = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic wsh    = Activator.CreateInstance(shellType);

            var bad = dirs
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", 
                                  SearchOption.AllDirectories))
                .Where(lnk =>
                {
                    try
                    {
                        dynamic sc = wsh.CreateShortcut(lnk);
                        return !File.Exists((string)sc.TargetPath);
                    }
                    catch { return false; }
                })
                .ToArray();

            ShowConfirmDialog(
                "Remove Invalid Shortcuts",
                bad,
                selected =>
                {
                    foreach (int i in selected)
                        try { File.Delete(bad[i]); } catch { }
                }
            );
        }

        // ----------------------------------------------------------------
        // Driver Scan (read‑only)
        private void ShowDriverScan()
        {
            var mos = new ManagementObjectSearcher(
                "SELECT DeviceName,DriverVersion FROM Win32_PnPSignedDriver"
            );
            var drivers = mos.Get()
                .Cast<ManagementObject>()
                .Select(m => $"{m["DeviceName"]} — v{m["DriverVersion"]}")
                .Take(50)
                .ToArray();

            ShowConfirmDialog(
                "Installed Drivers (read‑only)",
                drivers,
                _ => { /* no action */ }
            );
        }

        // ----------------------------------------------------------------
        // Generic Confirm Dialog
        private void ShowConfirmDialog(
            string title,
            string[] items,
            Action<int[]> onConfirm
        ) => ShowConfirmDialog(title, items,
               Enumerable.Repeat(true, items.Length).ToArray(),
               onConfirm);

        private void ShowConfirmDialog(
            string title,
            string[] items,
            bool[] initialChecked,
            Action<int[]> onConfirm
        )
        {
            var dlg = new Form
            {
                Text          = title,
                Size          = new Size(400, 500),
                StartPosition = FormStartPosition.CenterParent,
                BackColor     = Color.FromArgb(30, 30, 30),
                ForeColor     = Color.White
            };

            var chk = new CheckedListBox
            {
                Dock         = DockStyle.Fill,
                CheckOnClick = true,
                BackColor    = Color.FromArgb(30, 30, 30),
                ForeColor    = Color.White,
                BorderStyle  = BorderStyle.None
            };
            for (int i = 0; i < items.Length; i++)
                chk.Items.Add(items[i], initialChecked[i]);

            var btnPanel = new FlowLayoutPanel
            {
                Dock      = DockStyle.Bottom,
                Height    = 35,
                BackColor = Color.FromArgb(40, 40, 40)
            };
            var ok = new Button
            {
                Text      = "Confirm",
                AutoSize  = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Black,
                ForeColor = Color.White
            };
            ok.Click += (_,__) =>
            {
                var sel = Enumerable.Range(0, chk.Items.Count)
                                    .Where(i => chk.GetItemChecked(i))
                                    .ToArray();
                onConfirm(sel);
                dlg.Close();
            };
            var cancel = new Button
            {
                Text      = "Cancel",
                AutoSize  = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Black,
                ForeColor = Color.White
            };
            cancel.Click += (_,__) => dlg.Close();

            btnPanel.Controls.Add(ok);
            btnPanel.Controls.Add(cancel);

            dlg.Controls.Add(chk);
            dlg.Controls.Add(btnPanel);
            dlg.ShowDialog();
        }
    }
}
