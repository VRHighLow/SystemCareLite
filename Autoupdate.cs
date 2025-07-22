using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;

namespace SystemCareLite
{
    public static class AutoUpdater
    {
        // ─── CONFIGURATION ────────────────────────────────────────────────────

        // Your GitHub user/org
        private const string GitHubOwner = "VRHighLow";
        // Your repo name
        private const string GitHubRepo  = "SystemCareLite";
        // The asset name exactly as it appears in your release (single.exe)
        private const string AssetName   = "SystemCareLite.exe";

        // ─── PUBLIC ENTRY POINTS ──────────────────────────────────────────────

        /// <summary>
        /// Call at the very top of Main().
        /// If launched as "--self-update oldpath newpath", we overwrite and relaunch.
        /// </summary>
        public static bool HandleSelfUpdate(string[] args)
        {
            if (args.Length == 3 && args[0] == "--self-update")
            {
                var targetExe = args[1];
                var tempFile  = args[2];

                // Give the original process a moment to exit:
                Thread.Sleep(500);

                // Overwrite and delete the temp
                File.Copy(tempFile, targetExe, overwrite: true);
                File.Delete(tempFile);

                // Relaunch updated exe
                Process.Start(new ProcessStartInfo
                {
                    FileName        = targetExe,
                    UseShellExecute = true
                });

                return true;
            }

            return false;
        }

        /// <summary>
        /// Fire‐and‐forget: checks GitHub latest release and, if newer, downloads & invokes self‐update.
        /// </summary>
        [RequiresAssemblyFiles()]
        public static void InitializeAsync()
        {
            _ = CheckForUpdatesAsync();
        }

        // ─── IMPLEMENTATION ────────────────────────────────────────────────────

        [RequiresAssemblyFiles("Calls System.Reflection.Assembly.Location")]
        private static async Task CheckForUpdatesAsync()
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SystemCareLite-Updater");

                // 1) Grab latest release metadata
                var apiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
                var json   = await http.GetStringAsync(apiUrl).ConfigureAwait(false);
                var root   = JObject.Parse(json);

                // 2) Extract tag_name, strip leading 'v'
                var tag = (root.Value<string>("tag_name") ?? "").TrimStart('v');
                if (!Version.TryParse(tag, out var latestVersion))
                    return;

                // 3) Compare to our own Version
                var current = Assembly.GetExecutingAssembly()
                                      .GetName().Version!;
                if (!(latestVersion > current))
                    return;

                // 4) Find the EXE asset
                var assets = (root["assets"] as JArray) ?? new JArray();
                var asset  = assets.FirstOrDefault(a =>
                    string.Equals(a.Value<string>("name"),
                                  AssetName,
                                  StringComparison.OrdinalIgnoreCase));
                if (asset == null) return;

                var downloadUrl = asset.Value<string>("browser_download_url");
                if (string.IsNullOrEmpty(downloadUrl)) return;

                // 5) Download to temp
                var tempFile = Path.Combine(
                    Path.GetTempPath(),
                    $"{AssetName}_{latestVersion}.tmp");
                using (var resp = await http.GetAsync(downloadUrl).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tempFile);
                    await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                }

                // 6) Relaunch ourselves in self‐update mode
                var exePath = Assembly.GetExecutingAssembly().Location;
                Process.Start(new ProcessStartInfo(exePath)
                {
                    Arguments       = $"--self-update \"{exePath}\" \"{tempFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });

                // 7) Exit so updater can overwrite
                Environment.Exit(0);
            }
            catch
            {
                // ignore all errors silently
            }
        }
    }
}
