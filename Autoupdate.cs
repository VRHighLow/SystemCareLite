using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;

namespace SystemCareLite
{
    internal static class AutoUpdater
    {
        private const string GitHubOwner = "VRHighLow";
        private const string GitHubRepo = "SystemCareLite";
        private const string AppName = "SystemCareLite";
        private const string AssetName = "SystemCareLite.exe";
        
        private static readonly string ProgramFilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        private static readonly string TempPath = Path.Combine(Path.GetTempPath(), $"{AppName}_Update");
        private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), $"{AppName}_Update.log");
        private static readonly object _logLock = new object();
        private static bool _updateInProgress = false;

        public static async void InitializeAsync()
        {
            try
            {
                if (_updateInProgress) return;
                _updateInProgress = true;
                
                LogToFile($"=== {AppName} Update Check Started ===");
                LogToFile($"Version: {GetCurrentVersion()}");
                LogToFile($"OS: {Environment.OSVersion}");
                LogToFile($"64-bit OS: {Environment.Is64BitOperatingSystem}");
                LogToFile($"64-bit Process: {Environment.Is64BitProcess}");

                // Ensure temp directory exists
                if (!Directory.Exists(TempPath))
                {
                    Directory.CreateDirectory(TempPath);
                    LogToFile($"Created temp directory: {TempPath}");
                }

                // Check for updates in background
                await CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                LogToFile($"Error in InitializeAsync: {ex}");
                _updateInProgress = false;
            }
        }

        private static string GetCurrentVersion()
        {
            try
            {
                // First try to get version from entry assembly
                var entryAssembly = Assembly.GetEntryAssembly();
                var version = entryAssembly?.GetName().Version;

                // If that fails, try executing assembly
                if (version == null || version.Major == 0)
                {
                    version = Assembly.GetExecutingAssembly().GetName().Version;
                }

                // If still no version, use file version info
                if (version == null || version.Major == 0)
                {
                    var fileVersion = FileVersionInfo.GetVersionInfo(entryAssembly?.Location ?? 
                        Assembly.GetExecutingAssembly().Location);
                    if (Version.TryParse(fileVersion.FileVersion, out var fileVer))
                    {
                        version = fileVer;
                    }
                }

                return version?.ToString() ?? "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        private static async Task CheckForUpdatesAsync()
        {
            try
            {
                LogToFile("Checking for updates...");
                
                // Get current version
                var currentVersion = new Version(GetCurrentVersion());
                LogToFile($"Current version: {currentVersion}");

                // Get latest version from GitHub
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppName} Update Checker/1.0");
                http.Timeout = TimeSpan.FromSeconds(30);

                LogToFile("Fetching latest release info from GitHub...");
                var response = await http.GetStringAsync($"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest");
                var release = JObject.Parse(response);
                
                var versionString = release["tag_name"]?.ToString().TrimStart('v');
                if (string.IsNullOrEmpty(versionString))
                {
                    LogToFile("Could not determine latest version from GitHub release");
                    return;
                }

                // Handle version strings with more than 3 parts (e.g., 1.2.3.4)
                var versionParts = versionString.Split('.');
                if (versionParts.Length > 3)
                {
                    versionString = string.Join(".", versionParts.Take(3));
                }

                if (!Version.TryParse(versionString, out var latestVersion))
                {
                    LogToFile($"Invalid version format from GitHub: {versionString}");
                    return;
                }

                LogToFile($"Current version: {currentVersion}, Latest version on GitHub: {latestVersion}");

                // Compare versions
                var versionComparison = currentVersion.CompareTo(latestVersion);
                if (versionComparison >= 0)
                {
                    LogToFile($"Application is up to date (Current: {currentVersion}, Latest: {latestVersion})");
                    return;
                }

                LogToFile($"Update available! Current: {currentVersion}, Latest: {latestVersion}");

                // Show update prompt on UI thread
                var releaseNotes = release["body"]?.ToString() ?? "No release notes available.";
                var result = await ShowUpdatePrompt(currentVersion.ToString(), latestVersion.ToString(), releaseNotes);

                if (result == DialogResult.Yes)
                {
                    LogToFile("User chose to update");
                    await DownloadAndInstallUpdateAsync(release);
                }
                else
                {
                    LogToFile("User chose not to update");
                }
            }
            catch (Exception ex)
            {
                LogToFile($"Error in CheckForUpdatesAsync: {ex}");
            }
            finally
            {
                _updateInProgress = false;
            }
        }

        private static async Task<DialogResult> ShowUpdatePrompt(string currentVersion, string latestVersion, string releaseNotes)
        {
            var tcs = new TaskCompletionSource<DialogResult>();
            
            void ShowDialog()
            {
                using var form = new Form
                {
                    Width = 500,
                    Height = 400,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = "Update Available",
                    StartPosition = FormStartPosition.CenterScreen,
                    MinimizeBox = false,
                    MaximizeBox = false
                };

                var label = new Label
                {
                    Text = $"A new version {latestVersion} is available!\n\nCurrent version: {currentVersion}\n\nRelease Notes:",
                    AutoSize = true,
                    Location = new System.Drawing.Point(10, 10)
                };

                var textBox = new RichTextBox
                {
                    Text = releaseNotes,
                    ReadOnly = true,
                    ScrollBars = RichTextBoxScrollBars.Vertical,
                    Location = new System.Drawing.Point(10, 60),
                    Size = new System.Drawing.Size(460, 250),
                    BackColor = System.Drawing.SystemColors.Window
                };

                var yesButton = new Button
                {
                    Text = "Update Now",
                    DialogResult = DialogResult.Yes,
                    Location = new System.Drawing.Point(315, 320)
                };

                var noButton = new Button
                {
                    Text = "Later",
                    DialogResult = DialogResult.No,
                    Location = new System.Drawing.Point(395, 320)
                };

                yesButton.Click += (s, e) => { form.DialogResult = DialogResult.Yes; form.Close(); };
                noButton.Click += (s, e) => { form.DialogResult = DialogResult.No; form.Close(); };

                form.AcceptButton = yesButton;
                form.CancelButton = noButton;

                form.Controls.Add(label);
                form.Controls.Add(textBox);
                form.Controls.Add(yesButton);
                form.Controls.Add(noButton);

                var result = form.ShowDialog();
                tcs.SetResult(result == DialogResult.Yes ? DialogResult.Yes : DialogResult.No);
            }

            if (Application.MessageLoop && Application.OpenForms.Count > 0)
            {
                var mainForm = Application.OpenForms[0];
                if (mainForm != null && !mainForm.IsDisposed)
                {
                    mainForm.Invoke(new Action(ShowDialog));
                    return await tcs.Task;
                }
            }

            // Fallback to simple message box if no UI thread available
            return MessageBox.Show(
                $"A new version {latestVersion} is available!\n\nCurrent version: {currentVersion}\n\nWould you like to update now?",
                "Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
        }

        private static async Task DownloadAndInstallUpdateAsync(JObject release)
        {
            try
            {
                LogToFile("Starting update process...");
                
                // Get the download URL directly from the latest release
                var downloadUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest/download/{AssetName}";
                LogToFile($"Using direct download URL: {downloadUrl}");
                
                // Get the current executable path
                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(currentExe))
                {
                    LogToFile("Error: Could not determine current executable path");
                    return;
                }
                
                var currentDir = Path.GetDirectoryName(currentExe);
                if (string.IsNullOrEmpty(currentDir))
                {
                    LogToFile("Error: Could not determine current directory");
                    return;
                }
                
                // Ensure temp directory exists
                if (!Directory.Exists(TempPath))
                {
                    Directory.CreateDirectory(TempPath);
                    LogToFile($"Created temp directory: {TempPath}");
                }

                // Download the update to a temporary file
                var tempFile = Path.Combine(TempPath, $"update_{DateTime.Now:yyyyMMddHHmmss}.exe");
                LogToFile($"Downloading update from {downloadUrl} to {tempFile}");
                
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(5);
                    var response = await client.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                    
                    using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                        await fs.FlushAsync();
                    }
                }

                LogToFile("Download completed successfully");

                // Get current executable path if not already determined
                if (string.IsNullOrEmpty(currentExe))
                {
                    currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? 
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{AppName}.exe");
                }

                // Create update script paths
                var batchFile = Path.Combine(TempPath, "update.bat");
                var targetExe = Path.Combine(ProgramFilesPath, $"{AppName}.exe");
                var logFile = Path.Combine(TempPath, "update_log.txt");
                var vbsScript = Path.Combine(TempPath, "create_shortcut.vbs");

                // Create VBS script to create shortcut
                var vbsContent = $@"
Set oWS = WScript.CreateObject(""WScript.Shell"")
sLinkFile = oWS.ExpandEnvironmentStrings(""%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\{AppName}.lnk"")
Set oLink = oWS.CreateShortcut(sLinkFile)
oLink.TargetPath = ""{targetExe}""
oLink.WorkingDirectory = ""{Path.GetDirectoryName(targetExe)}""
oLink.Description = ""{AppName}""
oLink.Save
";
                await File.WriteAllTextAsync(vbsScript, vbsContent);

                // Create batch file with properly escaped content
              // In the DownloadAndInstallUpdateAsync method, replace the batch script generation with this:

var batchContent = new StringBuilder();
batchContent.AppendLine("@echo off");
batchContent.AppendLine("setlocal enabledelayedexpansion");
batchContent.AppendLine("echo =================================================================");
batchContent.AppendLine("echo ================ SystemCareLite Update Process =================");
batchContent.AppendLine("echo =================================================================");
batchContent.AppendLine("echo.");
batchContent.AppendLine("echo [%TIME%] Stopping SystemCareLite...");
batchContent.AppendLine($"taskkill /F /IM \"{Path.GetFileName(currentExe)}\" >nul 2>&1");
batchContent.AppendLine("timeout /t 1 /nobreak >nul");
batchContent.AppendLine();
batchContent.AppendLine("echo [%TIME%] Updating to new version...");
batchContent.AppendLine($"if exist \"{targetExe}\" (");
batchContent.AppendLine("    del /F /Q \"" + targetExe + "\"");
batchContent.AppendLine("    if errorlevel 1 (");
batchContent.AppendLine("        echo [%TIME%] ERROR: Could not remove old version");
batchContent.AppendLine("        pause");
batchContent.AppendLine("        exit /b 1");
batchContent.AppendLine("    )");
batchContent.AppendLine(")");
batchContent.AppendLine();
batchContent.AppendLine($"copy /Y \"{tempFile}\" \"{targetExe}\"");
batchContent.AppendLine("if errorlevel 1 (");
batchContent.AppendLine("    echo [%TIME%] ERROR: Failed to copy new version");
batchContent.AppendLine("    pause");
batchContent.AppendLine("    exit /b 1");
batchContent.AppendLine(")");
batchContent.AppendLine();
batchContent.AppendLine("echo [%TIME%] Starting updated version...");
batchContent.AppendLine($"start \"\" \"{targetExe}\"");
batchContent.AppendLine();
batchContent.AppendLine("echo [%TIME%] Cleaning up temporary files...");
batchContent.AppendLine($"del /F /Q \"{tempFile}\" >nul 2>&1");
batchContent.AppendLine($"del /F /Q \"{vbsScript}\" >nul 2>&1");
batchContent.AppendLine("echo [%TIME%] Update completed successfully!");
batchContent.AppendLine("echo.");
batchContent.AppendLine("echo This window will close in 3 seconds...");
batchContent.AppendLine("timeout /t 3 >nul");
batchContent.AppendLine("exit");

                
                await File.WriteAllTextAsync(batchFile, batchContent.ToString());
                LogToFile($"Created update batch file: {batchFile}");

                // Start the update process with admin privileges
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"{batchFile}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetTempPath()
                };

                LogToFile("Starting update process with admin privileges...");
                try 
                {
                    Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    LogToFile($"Failed to start update process: {ex}");
                    throw;
                }
                
                // Exit the current instance
                LogToFile("Exiting current instance...");
                Application.Exit();
            }
            catch (Exception ex)
            {
                LogToFile($"Error in DownloadAndInstallUpdateAsync: {ex}");
                MessageBox.Show(
                    $"An error occurred while trying to update: {ex.Message}",
                    "Update Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                _updateInProgress = false;
            }
        }

        private static void LogToFile(string message)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logMessage = $"[{timestamp}] {message}";
                
                lock (_logLock)
                {
                    File.AppendAllText(LogFilePath, logMessage + Environment.NewLine);
                }
                
                Debug.WriteLine(logMessage);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write to log: {ex}");
            }
        }
    }
}
