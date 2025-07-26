using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SystemCareLite
{
    static class Program
    {
        [STAThread]
        [RequiresAssemblyFiles("Calls SystemCareLite.AutoUpdater.InitializeAsync()")]
        static void Main(string[] args)
        {
            // ── 1) Usual startup config ─────────────────────────────────────
            ApplicationConfiguration.Initialize();
            EnsureRunsAtStartup();

            // ── 2) Kick off GitHub‐based update check in background ────────
            AutoUpdater.InitializeAsync();

            // ── 3) Show tray icon & popup context ────────────────────────
            Application.Run(new TrayAppContext());
        }

        private static void EnsureRunsAtStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key.SetValue("SystemCareLite",
                             $"\"{Application.ExecutablePath}\"");
            }
            catch { /* silently ignore */ }
        }
    }
}
