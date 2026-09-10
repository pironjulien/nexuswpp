using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("NexusWpp Installer")]
[assembly: AssemblyProduct("NexusWpp")]
[assembly: AssemblyCompany("JULIENPIRON.FR")]
[assembly: AssemblyFileVersion("1.0.12.0")]
[assembly: AssemblyVersion("1.0.12.0")]

namespace NexusWppInstaller
{
    internal static class Program
    {
        private const string InstallDir = @"C:\nexuswpp";
        private const string PayloadResourceName = "NexusWpp.Payload.zip";
        private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        private const string WebView2ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private const string ProductName = "NexusWpp";
        internal const string ProductVersion = "1.0.12";
        private const string Publisher = "JULIENPIRON.FR";
        private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NexusWpp";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = HasArg(args, "/silent") || HasArg(args, "-silent") || HasArg(args, "/quiet") || HasArg(args, "-quiet");
            bool uninstall = HasArg(args, "/uninstall") || HasArg(args, "-uninstall");

            try { SetProcessDPIAware(); } catch { }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (!IsAdministrator())
            {
                Show(silent, "NexusWpp installer must be run as administrator.");
                return 1;
            }

            if (!silent)
            {
                return RunWithProgressWindow(uninstall);
            }

            try
            {
                if (uninstall)
                {
                    Uninstall(new NullProgressReporter());
                    return 0;
                }

                Install(new NullProgressReporter());
                Show(silent, ProductName + " " + ProductVersion + " is installed and running.");
                return 0;
            }
            catch (Exception ex)
            {
                Log("Installation failed: " + ex);
                Show(false, "NexusWpp installation failed:\r\n\r\n" + ex.Message + "\r\n\r\nLog: " + Path.Combine(InstallDir, "install.log"));
                return 1;
            }
        }

        private static int RunWithProgressWindow(bool uninstall)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (InstallerProgressForm form = new InstallerProgressForm(uninstall))
            {
                form.Shown += delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            if (uninstall)
                            {
                                Uninstall(form);
                                form.Complete(true, "NexusWpp " + ProductVersion + " a ete desinstalle.", 0);
                            }
                            else
                            {
                                Install(form);
                                form.Complete(true, "NexusWpp " + ProductVersion + " est installe et lance.", 0);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log((uninstall ? "Uninstall" : "Installation") + " failed: " + ex);
                            form.Complete(false, ex.Message + "\r\n\r\nLog: " + Path.Combine(InstallDir, "install.log"), 1);
                        }
                    });
                };

                Application.Run(form);
                return form.ExitCode;
            }
        }

        private static void Install(IProgressReporter progress)
        {
            Log("Starting NexusWpp " + ProductVersion + " installation.");
            progress.Report(5, "Preparation de l'installation " + ProductVersion + "...");

            progress.Report(12, "Arret de l'ancienne version...");
            StopRunningWallpaper();
            Directory.CreateDirectory(InstallDir);

            progress.Report(24, "Verification du runtime WebView2...");
            if (!IsWebView2RuntimeInstalled())
            {
                progress.Report(30, "Installation du runtime WebView2 Microsoft...");
                InstallWebView2Runtime();
            }

            progress.Report(48, "Copie des fichiers NexusWpp...");
            ExtractPayload();

            progress.Report(64, "Configuration du demarrage Windows...");
            RegisterStartup();

            progress.Report(78, "Ajout au menu Demarrer...");
            RegisterStartMenuShortcut();

            progress.Report(86, "Enregistrement de la desinstallation...");
            RegisterUninstallEntry();
            PersistInstallerForUninstall();

            progress.Report(94, "Lancement du fond d'ecran...");
            StartWallpaper();

            progress.Report(100, "Installation terminee.");
            Log("NexusWpp " + ProductVersion + " installation completed.");
        }

        private static void Uninstall(IProgressReporter progress)
        {
            Log("Starting NexusWpp " + ProductVersion + " uninstall.");
            progress.Report(10, "Arret de NexusWpp...");
            StopRunningWallpaper();
            progress.Report(35, "Nettoyage du demarrage Windows...");
            CleanupStartupEntries();
            progress.Report(55, "Suppression du menu Demarrer...");
            RemoveStartMenuShortcut();
            progress.Report(70, "Suppression de l'entree Applications installees...");
            RemoveUninstallEntry();

            string self = Assembly.GetExecutingAssembly().Location;
            string installFullPath = Path.GetFullPath(InstallDir);
            string selfFullPath = Path.GetFullPath(self);
            progress.Report(84, "Suppression des fichiers...");
            if (selfFullPath.StartsWith(installFullPath, StringComparison.OrdinalIgnoreCase))
            {
                ScheduleInstallDirectoryRemoval();
            }
            else if (Directory.Exists(InstallDir))
            {
                Directory.Delete(InstallDir, true);
            }

            progress.Report(100, "Desinstallation terminee.");
        }

        private static bool HasArg(string[] args, string value)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void StopRunningWallpaper()
        {
            KillProcessesByName("nexuswpp");
            KillProcessesByName("DesktopHtmlHost");
            KillOwnedWebView2Processes();
            Thread.Sleep(500);
        }

        private static void KillProcessesByName(string name)
        {
            Process[] processes = Process.GetProcessesByName(name);
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    processes[i].Kill();
                    processes[i].WaitForExit(3000);
                }
                catch { }
                finally
                {
                    processes[i].Dispose();
                }
            }
        }

        private static void KillOwnedWebView2Processes()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        string commandLine = Convert.ToString(item["CommandLine"]);
                        if (commandLine == null) continue;
                        if (commandLine.IndexOf("nexuswpp", StringComparison.OrdinalIgnoreCase) < 0 &&
                            commandLine.IndexOf("EBWebView", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        int pid = Convert.ToInt32(item["ProcessId"]);
                        try
                        {
                            using (Process process = Process.GetProcessById(pid))
                            {
                                process.Kill();
                                process.WaitForExit(3000);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static bool IsWebView2RuntimeInstalled()
        {
            string[] paths = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WebView2ClientGuid,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientGuid
            };

            for (int i = 0; i < paths.Length; i++)
            {
                if (HasValidRuntimeVersion(Registry.LocalMachine, paths[i])) return true;
                if (HasValidRuntimeVersion(Registry.CurrentUser, paths[i])) return true;
            }

            return false;
        }

        private static bool HasValidRuntimeVersion(RegistryKey root, string path)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(path, false))
                {
                    if (key == null) return false;
                    string version = Convert.ToString(key.GetValue("pv"));
                    return !string.IsNullOrWhiteSpace(version) && version != "0.0.0.0";
                }
            }
            catch
            {
                return false;
            }
        }

        private static void InstallWebView2Runtime()
        {
            Log("WebView2 Runtime missing. Downloading Evergreen Bootstrapper.");
            string bootstrapper = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (WebClient client = new WebClient())
            {
                client.DownloadFile(WebView2BootstrapperUrl, bootstrapper);
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = bootstrapper;
            psi.Arguments = "/silent /install";
            psi.UseShellExecute = false;
            using (Process process = Process.Start(psi))
            {
                process.WaitForExit();
                if (process.ExitCode != 0 && process.ExitCode != 1638)
                {
                    throw new InvalidOperationException("WebView2 Runtime installer failed with exit code " + process.ExitCode + ".");
                }
            }

            if (!IsWebView2RuntimeInstalled())
            {
                throw new InvalidOperationException("WebView2 Runtime was not detected after installation.");
            }
        }

        private static void ExtractPayload()
        {
            Log("Extracting payload to " + InstallDir + ".");

            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream payload = assembly.GetManifestResourceStream(PayloadResourceName))
            {
                if (payload == null)
                {
                    throw new InvalidOperationException("Embedded payload not found: " + PayloadResourceName);
                }

                using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    for (int i = 0; i < archive.Entries.Count; i++)
                    {
                        ZipArchiveEntry entry = archive.Entries[i];
                        string targetPath = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
                        if (!targetPath.StartsWith(Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Unsafe archive entry: " + entry.FullName);
                        }

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(targetPath);
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                        using (Stream input = entry.Open())
                        using (FileStream output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                        }
                    }
                }
            }
        }

        private static void RegisterStartup()
        {
            string exe = Path.Combine(InstallDir, "nexuswpp.exe");
            string command = "\"" + exe + "\"";

            CleanupStartupEntries();

            using (RegistryKey run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (run == null) throw new InvalidOperationException("Unable to open HKLM Run key.");
                run.SetValue("NexusWpp", command, RegistryValueKind.String);
            }

            try
            {
                using (RegistryKey serialize = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize"))
                {
                    serialize.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord);
                }
            }
            catch { }

            Log("Startup registered through HKLM Run only.");
        }

        private static void CleanupStartupEntries()
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (run != null) run.DeleteValue("NexusWpp", false);
                }
            }
            catch (Exception ex)
            {
                Log("HKCU Run cleanup failed: " + ex.Message);
            }

            DeleteStartupShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            DeleteStartupShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

            try
            {
                using (RegistryKey run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (run != null) run.DeleteValue("NexusWpp", false);
                }
            }
            catch (Exception ex)
            {
                Log("HKLM Run cleanup failed: " + ex.Message);
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "schtasks.exe";
                psi.Arguments = "/Delete /TN NexusWpp /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process process = Process.Start(psi))
                {
                    process.WaitForExit(10000);
                }
            }
            catch (Exception ex)
            {
                Log("Scheduled task cleanup failed: " + ex.Message);
            }
        }

        private static void DeleteStartupShortcut(string startupFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(startupFolder)) return;
                string shortcut = Path.Combine(startupFolder, "NexusWpp.lnk");
                if (File.Exists(shortcut)) File.Delete(shortcut);
            }
            catch (Exception ex)
            {
                Log("Startup shortcut cleanup failed: " + ex.Message);
            }
        }

        private static void RegisterStartMenuShortcut()
        {
            string shortcutPath = GetCommonStartMenuShortcutPath();
            string shortcutDir = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(shortcutDir)) Directory.CreateDirectory(shortcutDir);

            CreateShellShortcut(
                shortcutPath,
                Path.Combine(InstallDir, "nexuswpp.exe"),
                InstallDir,
                Path.Combine(InstallDir, "icon.ico"),
                "NexusWpp dynamic desktop wallpaper");

            Log("Start Menu shortcut registered: " + shortcutPath);
        }

        private static void RemoveStartMenuShortcut()
        {
            DeleteStartMenuShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
            DeleteStartMenuShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        }

        private static string GetCommonStartMenuShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "NexusWpp.lnk");
        }

        private static void DeleteStartMenuShortcut(string programsFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(programsFolder)) return;
                string shortcut = Path.Combine(programsFolder, "NexusWpp.lnk");
                if (File.Exists(shortcut)) File.Delete(shortcut);
            }
            catch (Exception ex)
            {
                Log("Start Menu shortcut cleanup failed: " + ex.Message);
            }
        }

        private static void CreateShellShortcut(string shortcutPath, string targetPath, string workingDirectory, string iconPath, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell is unavailable.");

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconPath });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
                }

                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
        }

        private static void RegisterUninstallEntry()
        {
            string installerPath = Path.Combine(InstallDir, "NexusWppSetup.exe");
            string uninstallString = "\"" + installerPath + "\" /uninstall";

            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKeyPath))
            {
                key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
                key.SetValue("Publisher", Publisher, RegistryValueKind.String);
                key.SetValue("InstallLocation", InstallDir, RegistryValueKind.String);
                key.SetValue("DisplayIcon", Path.Combine(InstallDir, "icon.ico"), RegistryValueKind.String);
                key.SetValue("UninstallString", uninstallString, RegistryValueKind.String);
                key.SetValue("QuietUninstallString", uninstallString + " /quiet", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", GetInstallSizeKb(), RegistryValueKind.DWord);
            }
        }

        private static void RemoveUninstallEntry()
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, false);
            }
            catch (Exception ex)
            {
                Log("Uninstall registry cleanup failed: " + ex.Message);
            }
        }

        private static int GetInstallSizeKb()
        {
            try
            {
                long bytes = 0;
                if (Directory.Exists(InstallDir))
                {
                    string[] files = Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories);
                    for (int i = 0; i < files.Length; i++)
                    {
                        bytes += new FileInfo(files[i]).Length;
                    }
                }
                return (int)Math.Max(1, bytes / 1024);
            }
            catch
            {
                return 1;
            }
        }

        private static void PersistInstallerForUninstall()
        {
            string source = Assembly.GetExecutingAssembly().Location;
            string destination = Path.Combine(InstallDir, "NexusWppSetup.exe");
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination, true);
            }
        }

        private static void ScheduleInstallDirectoryRemoval()
        {
            string cmd = "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + InstallDir + "\"";
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = cmd;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            try { Process.Start(psi); } catch { }
        }

        private static void StartWallpaper()
        {
            string exe = Path.Combine(InstallDir, "nexuswpp.exe");
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.WorkingDirectory = InstallDir;
            psi.UseShellExecute = true;
            Process.Start(psi);
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                File.AppendAllText(Path.Combine(InstallDir, "install.log"), "[" + DateTime.Now.ToString("s") + "] " + message + Environment.NewLine);
            }
            catch
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "NexusWppInstall.log"), "[" + DateTime.Now.ToString("s") + "] " + message + Environment.NewLine);
                }
                catch { }
            }
        }

        private static void Show(bool silent, string message)
        {
            if (!silent)
            {
                MessageBox.Show(message, "NexusWpp Installer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    internal interface IProgressReporter
    {
        void Report(int percent, string message);
    }

    internal sealed class NullProgressReporter : IProgressReporter
    {
        public void Report(int percent, string message)
        {
        }
    }

    internal sealed class InstallerProgressForm : Form, IProgressReporter
    {
        private readonly Label titleLabel;
        private readonly Label statusLabel;
        private readonly Label percentLabel;
        private readonly Panel progressTrack;
        private readonly Panel progressFill;
        private readonly Button closeButton;
        private bool completed;

        public int ExitCode { get; private set; }

        public InstallerProgressForm(bool uninstall)
        {
            ExitCode = 1;
            Text = uninstall ? "Desinstallation NexusWpp " + Program.ProductVersion : "Installation NexusWpp " + Program.ProductVersion;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ClientSize = new Size(520, 230);
            BackColor = Color.FromArgb(22, 26, 31);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            titleLabel = new Label();
            titleLabel.AutoSize = false;
            titleLabel.Left = 28;
            titleLabel.Top = 24;
            titleLabel.Width = 464;
            titleLabel.Height = 34;
            titleLabel.Text = uninstall ? "Desinstallation de NexusWpp " + Program.ProductVersion : "Installation de NexusWpp " + Program.ProductVersion;
            titleLabel.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point);
            titleLabel.ForeColor = Color.White;

            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Left = 30;
            statusLabel.Top = 72;
            statusLabel.Width = 460;
            statusLabel.Height = 44;
            statusLabel.Text = "Preparation...";
            statusLabel.ForeColor = Color.FromArgb(210, 220, 230);

            progressTrack = new Panel();
            progressTrack.Left = 30;
            progressTrack.Top = 130;
            progressTrack.Width = 460;
            progressTrack.Height = 12;
            progressTrack.BackColor = Color.FromArgb(48, 55, 64);

            progressFill = new Panel();
            progressFill.Left = 0;
            progressFill.Top = 0;
            progressFill.Width = 0;
            progressFill.Height = progressTrack.Height;
            progressFill.BackColor = Color.FromArgb(0, 199, 190);
            progressTrack.Controls.Add(progressFill);

            percentLabel = new Label();
            percentLabel.AutoSize = false;
            percentLabel.Left = 30;
            percentLabel.Top = 150;
            percentLabel.Width = 460;
            percentLabel.Height = 28;
            percentLabel.Text = "0 %";
            percentLabel.TextAlign = ContentAlignment.MiddleRight;
            percentLabel.ForeColor = Color.FromArgb(150, 166, 178);

            closeButton = new Button();
            closeButton.Left = 370;
            closeButton.Top = 178;
            closeButton.Width = 120;
            closeButton.Height = 34;
            closeButton.Text = "Fermer";
            closeButton.Enabled = false;
            closeButton.FlatStyle = FlatStyle.Flat;
            closeButton.FlatAppearance.BorderColor = Color.FromArgb(80, 94, 108);
            closeButton.BackColor = Color.FromArgb(36, 43, 51);
            closeButton.ForeColor = Color.White;
            closeButton.Click += delegate { Close(); };

            Controls.Add(titleLabel);
            Controls.Add(statusLabel);
            Controls.Add(progressTrack);
            Controls.Add(percentLabel);
            Controls.Add(closeButton);
        }

        public void Report(int percent, string message)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, string>(Report), percent, message);
                return;
            }

            int safePercent = Math.Max(0, Math.Min(100, percent));
            statusLabel.Text = message;
            percentLabel.Text = safePercent.ToString() + " %";
            progressFill.Width = (int)Math.Round(progressTrack.Width * (safePercent / 100.0));
            progressFill.Refresh();
        }

        public void Complete(bool success, string message, int exitCode)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool, string, int>(Complete), success, message, exitCode);
                return;
            }

            completed = true;
            ExitCode = exitCode;
            Report(100, message);
            progressFill.BackColor = success ? Color.FromArgb(48, 209, 88) : Color.FromArgb(255, 69, 58);
            titleLabel.Text = success ? "Termine" : "Erreur d'installation";
            closeButton.Enabled = true;
            closeButton.Focus();
            ControlBox = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!completed)
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }
    }
}
