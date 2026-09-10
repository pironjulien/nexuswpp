using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;

namespace DesktopHtmlHost
{
    static class Program
    {
        private static Mutex appMutex;
        private const string AppDataFolderName = "NexusWpp";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterApplicationRestart(string commandLineArgs, int flags);

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                bool createdNew;
                appMutex = new Mutex(true, @"Local\NexusWppDesktopHost", out createdNew);
                if (!createdNew)
                {
                    return;
                }

                // Force Process DPI Awareness to prevent Windows from virtualizing coordinates
                SetProcessDPIAware();

                RegisterForUpdateRestart(args);
                CleanupStaleWebView2Processes();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string defaultHtml = GetDefaultHtmlPath();
                string htmlPath = args.Length > 0 ? args[0] : defaultHtml;

                Application.Run(new DesktopForm(htmlPath));
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);
            }
        }

        private static void RegisterForUpdateRestart(string[] args)
        {
            try
            {
                string restartArgs = BuildRestartCommandLine(args);
                int result = RegisterApplicationRestart(restartArgs, 0);
                if (result != 0)
                {
                    LogDebug("Application restart registration returned 0x" + result.ToString("X8", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                LogDebug("Application restart registration failed: " + ex.Message);
            }
        }

        private static string BuildRestartCommandLine(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return null;
            }

            return string.Join(" ", args.Select(QuoteCommandLineArgument).ToArray());
        }

        private static string QuoteCommandLineArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            StringBuilder escaped = new StringBuilder();
            escaped.Append('"');

            int backslashCount = 0;
            foreach (char current in value)
            {
                if (current == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (current == '"')
                {
                    escaped.Append('\\', (backslashCount * 2) + 1);
                    escaped.Append('"');
                    backslashCount = 0;
                    continue;
                }

                escaped.Append('\\', backslashCount);
                escaped.Append(current);
                backslashCount = 0;
            }

            escaped.Append('\\', backslashCount * 2);
            escaped.Append('"');
            return escaped.ToString();
        }

        internal static string GetAppDataFolder()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = AppDomain.CurrentDomain.BaseDirectory;
            }
            return Path.Combine(localAppData, AppDataFolderName);
        }

        internal static string GetWebViewUserDataFolder()
        {
            return Path.Combine(GetAppDataFolder(), "WebView2");
        }

        private static string GetDefaultHtmlPath()
        {
            string packagedHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
            if (File.Exists(packagedHtml))
            {
                return packagedHtml;
            }

            return @"C:\nexuswpp\index.html";
        }

        private static void WriteCrashLog(Exception ex)
        {
            try
            {
                string dir = GetAppDataFolder();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "crash.txt"), ex.ToString());
            }
            catch { }
        }

        private static readonly object logLock = new object();
        private static void AppendLog(string filePath, string content)
        {
            try
            {
                lock (logLock)
                {
                    string directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Basic log rotation (5MB limit)
                    if (File.Exists(filePath))
                    {
                        FileInfo fi = new FileInfo(filePath);
                        if (fi.Length > 5 * 1024 * 1024)
                        {
                            string backupPath = filePath + ".bak";
                            if (File.Exists(backupPath))
                            {
                                File.Delete(backupPath);
                            }
                            File.Move(filePath, backupPath);
                        }
                    }
                    File.AppendAllText(filePath, content);
                }
            }
            catch { }
        }

        internal static void LogDebug(string message)
        {
            string line = string.Format("[{0}] {1}\r\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), message);
            AppendLog(Path.Combine(GetAppDataFolder(), "webview_debug.log"), line);
        }

        private static void CleanupStaleWebView2Processes()
        {
            try
            {
                string profileFolder = Program.GetWebViewUserDataFolder();

                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"))
                {
                    foreach (ManagementObject process in searcher.Get())
                    {
                        string commandLine = Convert.ToString(process["CommandLine"] ?? "");
                        if (commandLine.IndexOf(profileFolder, StringComparison.OrdinalIgnoreCase) < 0 &&
                            commandLine.IndexOf("--webview-exe-name=nexuswpp.exe", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        try
                        {
                            process.InvokeMethod("Terminate", null);
                            Program.LogDebug("Cleaned stale Nexus WebView2 process pid=" + process["ProcessId"]);
                        }
                        catch (Exception ex)
                        {
                            Program.LogDebug("Stale WebView2 cleanup failed: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("Stale WebView2 cleanup error: " + ex.Message);
            }
        }
    }

    public class DesktopForm : Form
    {
        private WebView2 webView;
        private string htmlPath;
        private System.Windows.Forms.Timer searchTimer;
        private int retryCount = 0;
        private const int maxRetries = 300;
        private const int progmanFallbackRetries = 8;
        private bool webViewInitialized = false;
        private bool webViewRecoveryPending = false;
        private CoreWebView2Environment webViewEnvironment;
        private bool desktopAttached = false;
        private bool attachedToTemporaryParent = false;
        private IntPtr currentDesktopParent = IntPtr.Zero;
        private DateTime startupTime = DateTime.Now;

        // --- Telemetry state ---
        private System.Windows.Forms.Timer telemetryTimer;
        private TelemetryCollector telemetryCollector;
        private bool telemetryReady = false;
        private bool telemetryCollectPending = false;
        private System.Windows.Forms.Timer fullscreenTimer;
        private bool runtimeSuspended = false;
        private string fullscreenReason = "";
        private bool isClosing = false;

        // --- Static Hook and Bounds State ---
        private static DesktopForm activeInstance;
        private static IntPtr hookId = IntPtr.Zero;
        private static LowLevelMouseProc mouseProc;
        private static Thread mouseHookThread;
        private static int mouseHookThreadId;
        private static int mouseHookStopRequested;
        private static Exception mouseHookError;
        private static System.Drawing.Rectangle remotePanelBounds = System.Drawing.Rectangle.Empty;
        private static IntPtr renderWindow = IntPtr.Zero;
        private static MouseRoutingSnapshot mouseRouting;
        private static readonly uint currentProcessId = (uint)Process.GetCurrentProcess().Id;

        // Published by the UI thread as one immutable snapshot. The mouse thread must
        // never invoke a WinForms control or wait for WebView2/the display driver.
        private sealed class MouseRoutingSnapshot
        {
            internal readonly System.Drawing.Rectangle ScreenBounds;
            internal readonly IntPtr HostWindow;
            internal readonly IntPtr RenderWindow;

            internal MouseRoutingSnapshot(System.Drawing.Rectangle bounds, IntPtr host, IntPtr render)
            {
                ScreenBounds = bounds;
                HostWindow = host;
                RenderWindow = render;
            }
        }

        // --- Win32 P/Invoke API Definitions ---

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT point;
            public uint privateData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, 
            uint Msg, 
            IntPtr wParam, 
            IntPtr lParam, 
            uint fuFlags, 
            uint uTimeout, 
            out IntPtr lpdwResult
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        // Win32 Constants
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int SW_SHOW = 5;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        public DesktopForm(string htmlPath)
        {
            activeInstance = this;
            this.htmlPath = htmlPath;

            // Configure Form to act as a stealth, borderless wallpaper container
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;

            // Start off-screen to avoid visual flashes before parenting
            this.Location = new System.Drawing.Point(-32000, -32000);
            this.Size = new System.Drawing.Size(100, 100);

            // Force handle creation
            IntPtr forceHandle = this.Handle;

            // Listen for system resolution or display setting changes
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            SystemEvents.SessionEnding += SystemEvents_SessionEnding;

            // Create and configure the WebView2 UI component
            CreateWebViewControl();

            // Warm WebView2 immediately while Explorer is still preparing the desktop layer.
            InitializeWebViewAsync();

            // Initialize and start the non-blocking GUI timer to search for WorkerW.
            searchTimer = new System.Windows.Forms.Timer();
            searchTimer.Interval = 100;
            searchTimer.Tick += SearchTimer_Tick;
            searchTimer.Start();
        }

        private void CreateWebViewControl()
        {
            ResilientWebView2 newWebView = new ResilientWebView2();
            newWebView.Dock = DockStyle.Fill;
            newWebView.Disposed += WebView_Disposed;
            webView = newWebView;
            this.Controls.Add(webView);
        }

        private void WebView_Disposed(object sender, EventArgs e)
        {
            if (isClosing || IsDisposed || !ReferenceEquals(sender, webView))
            {
                return;
            }

            QueueWebViewRecovery("unexpected WebView2 disposal");
        }

        private void SearchTimer_Tick(object sender, EventArgs e)
        {
            if (desktopAttached && !attachedToTemporaryParent)
            {
                searchTimer.Stop();
                return;
            }

            IntPtr workerw = IntPtr.Zero;

            // 1. Signal Progman to split the desktop
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr result;
                SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 100, out result);
            }

            // 2. Discover the target WorkerW window designed to hold the background
            EnumWindows((hwnd, lParam) =>
            {
                IntPtr shellDll = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellDll != IntPtr.Zero)
                {
                    workerw = FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
                    if (workerw == IntPtr.Zero)
                    {
                        workerw = FindWindowEx(hwnd, IntPtr.Zero, "WorkerW", null);
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (workerw != IntPtr.Zero)
            {
                AttachToDesktopParent(workerw, false);
                searchTimer.Stop();
            }
            else
            {
                if (!desktopAttached && progman != IntPtr.Zero && retryCount >= progmanFallbackRetries)
                {
                    AttachToDesktopParent(progman, true);
                }

                retryCount++;
                if (retryCount >= maxRetries)
                {
                    searchTimer.Stop();
                    if (!desktopAttached)
                    {
                        Application.Exit();
                    }
                }
            }
        }

        private void AttachToDesktopParent(IntPtr parent, bool temporary)
        {
            if (parent == IntPtr.Zero || (desktopAttached && currentDesktopParent == parent))
            {
                return;
            }

            bool firstAttach = !desktopAttached;

            // Inject our WinForms application handle into the desktop wallpaper layer.
            SetParent(this.Handle, parent);

            int style = GetWindowLong(this.Handle, GWL_STYLE);
            style |= WS_CHILD;
            style &= ~WS_POPUP;
            SetWindowLong(this.Handle, GWL_STYLE, style);
            SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            UpdateBoundsToVirtualScreen();
            MoveWindow(this.Handle, this.Left, this.Top, this.Width, this.Height, true);
            ShowWindow(this.Handle, SW_SHOW);

            desktopAttached = true;
            attachedToTemporaryParent = temporary;
            currentDesktopParent = parent;

            double elapsed = (DateTime.Now - startupTime).TotalSeconds;
            if (temporary)
            {
                Program.LogDebug(string.Format(CultureInfo.InvariantCulture, "Wallpaper attached after {0:F2}s (Progman fallback while WorkerW is not ready).", elapsed));
            }
            else if (firstAttach)
            {
                Program.LogDebug(string.Format(CultureInfo.InvariantCulture, "Wallpaper attached after {0:F2}s.", elapsed));
            }
            else
            {
                Program.LogDebug(string.Format(CultureInfo.InvariantCulture, "Wallpaper reparented to WorkerW after {0:F2}s.", elapsed));
            }
        }

        private void UpdateBoundsToVirtualScreen()
        {
            this.Left = SystemInformation.VirtualScreen.Left;
            this.Top = SystemInformation.VirtualScreen.Top;
            this.Width = SystemInformation.VirtualScreen.Width;
            this.Height = SystemInformation.VirtualScreen.Height;
            PublishMouseRouting();
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            if (isClosing || IsDisposed) return;
            UpdateBoundsToVirtualScreen();
            if (this.Handle != IntPtr.Zero)
            {
                MoveWindow(this.Handle, this.Left, this.Top, this.Width, this.Height, true);
            }
            QueueDesktopReattach("display settings changed");
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                QueueDesktopReattach("system resumed from sleep");
            }
        }

        private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock ||
                e.Reason == SessionSwitchReason.SessionLogon ||
                e.Reason == SessionSwitchReason.RemoteConnect)
            {
                QueueDesktopReattach("session became active: " + e.Reason);
            }
        }

        private void QueueDesktopReattach(string reason)
        {
            if (isClosing || IsDisposed) return;

            TryBeginInvoke((MethodInvoker)delegate
            {
                if (isClosing || IsDisposed) return;
                RestartDesktopSearch(reason);
            });
        }

        private void RestartDesktopSearch(string reason)
        {
            Program.LogDebug("Restarting desktop attachment after " + reason + ".");

            desktopAttached = false;
            attachedToTemporaryParent = false;
            currentDesktopParent = IntPtr.Zero;
            retryCount = 0;
            startupTime = DateTime.Now;

            UpdateBoundsToVirtualScreen();
            if (this.Handle != IntPtr.Zero)
            {
                MoveWindow(this.Handle, this.Left, this.Top, this.Width, this.Height, true);
                ShowWindow(this.Handle, SW_SHOW);
            }

            if (searchTimer != null)
            {
                searchTimer.Stop();
                searchTimer.Start();
            }
        }

        private void SystemEvents_SessionEnding(object sender, SessionEndingEventArgs e)
        {
            Program.LogDebug("Windows session ending: closing NexusWpp cleanly.");
            BeginCleanShutdown();
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    Close();
                });
            }
            catch
            {
                Close();
            }
        }

        private void BeginCleanShutdown()
        {
            isClosing = true;
            telemetryCollectPending = false;
            runtimeSuspended = true;

            if (searchTimer != null) searchTimer.Stop();
            if (telemetryTimer != null) telemetryTimer.Stop();
            if (fullscreenTimer != null) fullscreenTimer.Stop();
            StopMouseHook();

            if (webView != null)
            {
                try
                {
                    ResilientWebView2 resilientWebView = webView as ResilientWebView2;
                    if (resilientWebView != null)
                    {
                        resilientWebView.BeginShutdown();
                    }

                    webView.Disposed -= WebView_Disposed;
                    webView.Dock = DockStyle.None;
                    webView.Visible = false;
                    Controls.Remove(webView);
                }
                catch (Exception ex)
                {
                    Program.LogDebug("WebView detach during shutdown failed: " + ex.Message);
                }
            }
        }

        private async void InitializeWebViewAsync()
        {
            if (webViewInitialized) return;
            webViewInitialized = true;

            try
            {
                string userDataFolder = Program.GetWebViewUserDataFolder();
                Directory.CreateDirectory(userDataFolder);

                if (webView == null || webView.IsDisposed)
                {
                    CreateWebViewControl();
                }

                if (webViewEnvironment == null)
                {
                    var options = new CoreWebView2EnvironmentOptions();
                    options.AdditionalBrowserArguments = "--disable-features=EdgeSidebar,EdgeTranslate";
                    webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                }

                if (isClosing || IsDisposed || webView == null || webView.IsDisposed) return;

                await webView.EnsureCoreWebView2Async(webViewEnvironment);
                if (isClosing || IsDisposed || webView == null || webView.IsDisposed) return;

                webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;

                // Configure virtual host mapping to serve files from local directory without HTTP server
                string directory = Path.GetDirectoryName(htmlPath);
                if (string.IsNullOrEmpty(directory)) directory = AppDomain.CurrentDomain.BaseDirectory;
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "nexuswpp.local",
                    directory,
                    CoreWebView2HostResourceAccessKind.Allow
                );

                webView.CoreWebView2.WebMessageReceived += (s, ev) =>
                {
                    try
                    {
                        if (isClosing || IsDisposed) return;

                        string msg = ev.TryGetWebMessageAsString();
                        if (msg != null && !msg.StartsWith("BOUNDS:"))
                        {
                            Program.LogDebug(msg);
                        }
                        
                        if (msg != null)
                        {
                            if (msg.StartsWith("BOUNDS:"))
                            {
                                string[] parts = msg.Substring(7).Split(',');
                                if (parts.Length >= 4)
                                {
                                    double scale = activeInstance.GetDpiScale();
                                    double webViewScale;
                                    if (parts.Length >= 5 &&
                                        double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out webViewScale) &&
                                        webViewScale > 0.0)
                                    {
                                        scale = webViewScale;
                                    }
                                    int left = (int)Math.Round(double.Parse(parts[0], CultureInfo.InvariantCulture) * scale);
                                    int top = (int)Math.Round(double.Parse(parts[1], CultureInfo.InvariantCulture) * scale);
                                    int right = (int)Math.Round(double.Parse(parts[2], CultureInfo.InvariantCulture) * scale);
                                    int bottom = (int)Math.Round(double.Parse(parts[3], CultureInfo.InvariantCulture) * scale);
                                    remotePanelBounds = new System.Drawing.Rectangle(left, top, right - left, bottom - top);
                                    
                                    FindRenderWindow();
                                }
                            }
                            else if (msg.StartsWith("SET_POWER:"))
                            {
                                string guidStr = msg.Substring(10);
                                SetPowerPlanFromUi(guidStr);
                            }
                            else if (msg == "REQUEST_TELEMETRY")
                            {
                                TelemetryTimer_Tick(null, null);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug(string.Format("WebMessageReceived Error: {0}", ex.ToString()));
                    }
                };

                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (function() {
                        window.onerror = function(message, source, lineno, colno, error) {
                            try { window.chrome.webview.postMessage('ERROR: ' + message + ' at ' + source + ':' + lineno); } catch(e) {}
                        };
                        const origErr = console.error;
                        console.error = function(...args) {
                            try {
                                origErr.apply(console, args);
                                window.chrome.webview.postMessage('CONSOLE_ERROR: ' + args.join(' '));
                            } catch(e) {}
                        };
                    })();
                ");

                // Load via virtual host
                webView.Source = new Uri("http://nexuswpp.local/index.html");

                StartRuntimeServices();
                webViewRecoveryPending = false;

                if (!telemetryReady || telemetryCollector == null)
                {
                    System.Threading.ThreadPool.QueueUserWorkItem((state) =>
                    {
                        try
                        {
                            var collector = new TelemetryCollector();
                            collector.Initialize();
                            TryBeginInvoke((MethodInvoker)delegate
                            {
                                telemetryCollector = collector;
                                telemetryReady = true;
                                if (!runtimeSuspended)
                                {
                                    TelemetryTimer_Tick(null, null);
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Program.LogDebug("Telemetry initialization error: " + ex.Message);
                        }
                    });
                }
                else if (!runtimeSuspended)
                {
                    TelemetryTimer_Tick(null, null);
                }
            }
            catch (Exception ex)
            {
                webViewRecoveryPending = false;
                if (isClosing || IsDisposed) return;

                Program.LogDebug("WebView2 Runtime failed to initialize: " + ex.ToString());
                BeginCleanShutdown();
            }
        }

        private void CoreWebView2_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            string kind = e != null ? e.ProcessFailedKind.ToString() : "unknown failure";
            Program.LogDebug("WebView2 process failed: " + kind + ".");
            QueueWebViewRecovery("process failure (" + kind + ")");
        }

        private void StartRuntimeServices()
        {
            StartMouseHook();

            if (telemetryTimer == null)
            {
                telemetryTimer = new System.Windows.Forms.Timer();
                telemetryTimer.Interval = 1000;
                telemetryTimer.Tick += TelemetryTimer_Tick;
            }

            if (!runtimeSuspended)
            {
                telemetryTimer.Start();
            }

            if (fullscreenTimer == null)
            {
                fullscreenTimer = new System.Windows.Forms.Timer();
                fullscreenTimer.Interval = 500;
                fullscreenTimer.Tick += FullscreenTimer_Tick;
            }

            fullscreenTimer.Start();
        }

        private void QueueWebViewRecovery(string reason)
        {
            if (isClosing || IsDisposed || webViewRecoveryPending)
            {
                return;
            }

            webViewRecoveryPending = true;
            Program.LogDebug("Scheduling WebView2 recovery after " + reason + ".");

            if (!TryBeginInvoke((MethodInvoker)delegate
            {
                RecoverWebView(reason);
            }))
            {
                webViewRecoveryPending = false;
            }
        }

        private void RecoverWebView(string reason)
        {
            if (isClosing || IsDisposed)
            {
                webViewRecoveryPending = false;
                return;
            }

            Program.LogDebug("Recovering WebView2 after " + reason + ".");
            telemetryCollectPending = false;
            renderWindow = IntPtr.Zero;
            Volatile.Write(ref mouseRouting, null);

            if (telemetryTimer != null) telemetryTimer.Stop();

            WebView2 oldWebView = webView;
            webView = null;
            webViewInitialized = false;

            if (oldWebView != null)
            {
                try
                {
                    oldWebView.Disposed -= WebView_Disposed;
                    Controls.Remove(oldWebView);
                }
                catch (Exception ex)
                {
                    if (!ResilientWebView2.IsDisposedLifecycleException(ex))
                    {
                        Program.LogDebug("WebView2 recovery detach skipped: " + ex.Message);
                    }
                }

                try
                {
                    ResilientWebView2 resilientWebView = oldWebView as ResilientWebView2;
                    if (resilientWebView != null)
                    {
                        resilientWebView.BeginShutdown();
                    }

                    if (!oldWebView.IsDisposed)
                    {
                        oldWebView.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    if (!ResilientWebView2.IsDisposedLifecycleException(ex))
                    {
                        Program.LogDebug("WebView2 recovery disposal skipped: " + ex.Message);
                    }
                }
            }

            CreateWebViewControl();
            InitializeWebViewAsync();
            RestartDesktopSearch("WebView2 recovery");
        }

        private void FullscreenTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                Exception hookError = Interlocked.Exchange(ref mouseHookError, null);
                if (hookError != null) Program.LogDebug("Mouse hook error: " + hookError);
                if (renderWindow == IntPtr.Zero || !IsWindow(renderWindow)) FindRenderWindow();
                else PublishMouseRouting();
                string reason;
                bool fullscreen = IsAnyFullscreenWindow(out reason);
                fullscreenReason = reason;
                SetRuntimeSuspended(fullscreen);
            }
            catch (Exception ex)
            {
                Program.LogDebug("Fullscreen detection error: " + ex.Message);
            }
        }

        private void SetRuntimeSuspended(bool suspended)
        {
            if (runtimeSuspended == suspended) return;
            runtimeSuspended = suspended;

            if (telemetryTimer != null)
            {
                if (suspended) telemetryTimer.Stop();
                else telemetryTimer.Start();
            }

            try
            {
                PostWebMessageAsJsonSafe(suspended ? "{\"control\":\"SUSPEND\"}" : "{\"control\":\"RESUME\"}", "Runtime suspend post");
            }
            catch (Exception ex)
            {
                Program.LogDebug("Runtime suspend post error: " + ex.Message);
            }

            if (!suspended)
            {
                TelemetryTimer_Tick(null, null);
            }

            Program.LogDebug(suspended ? "Runtime suspended: fullscreen foreground detected (" + fullscreenReason + ")." : "Runtime resumed: fullscreen foreground cleared.");
        }

        private void SetPowerPlanFromUi(string guidStr)
        {
            bool success = false;
            string activeGuid = "";
            string error = "";

            try
            {
                if (telemetryCollector == null)
                {
                    error = "telemetry collector not ready";
                }
                else
                {
                    success = telemetryCollector.SetActivePowerPlan(guidStr, out activeGuid, out error);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Program.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "Power plan request: requested={0}, active={1}, success={2}, error={3}",
                guidStr,
                activeGuid,
                success,
                error));

            try
            {
                string payload = string.Format(CultureInfo.InvariantCulture,
                    "{{\"control\":\"POWER_RESULT\",\"requestedGuid\":{0},\"activeGuid\":{1},\"success\":{2},\"error\":{3}}}",
                    JsonString(guidStr),
                    JsonString(activeGuid),
                    success ? "true" : "false",
                    JsonString(error));
                PostWebMessageAsJsonSafe(payload, "Power result post");
            }
            catch (Exception ex)
            {
                Program.LogDebug("Power result post error: " + ex.Message);
            }

            TelemetryTimer_Tick(null, null);
        }

        private static string JsonString(string value)
        {
            if (value == null) return "\"\"";
            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\t': sb.Append(@"\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private bool IsAnyFullscreenWindow(out string reason)
        {
            bool found = false;
            string foundReason = "";
            EnumWindows((hwnd, lParam) =>
            {
                if (found) return false;
                string candidateReason;
                if (IsFullscreenCandidate(hwnd, out candidateReason))
                {
                    found = true;
                    foundReason = candidateReason;
                }
                return !found;
            }, IntPtr.Zero);

            reason = foundReason;
            return found;
        }

        private bool IsFullscreenCandidate(IntPtr hwnd, out string reason)
        {
            reason = "";
            if (hwnd == IntPtr.Zero || hwnd == this.Handle || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return false;

            System.Text.StringBuilder className = new System.Text.StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            string cls = className.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Button") return false;

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == (uint)Process.GetCurrentProcess().Id) return false;
            string processName = "";
            try
            {
                Process p = Process.GetProcessById((int)pid);
                processName = p.ProcessName.ToLowerInvariant();
                if (processName == "msedgewebview2" ||
                    processName == "explorer" ||
                    processName == "textinputhost" ||
                    processName == "tabtip" ||
                    processName == "shellexperiencehost" ||
                    processName == "searchhost" ||
                    processName == "startmenuexperiencehost")
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return false;

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(monitor, ref info)) return false;

            int tolerance = 3;
            bool fullscreen = rect.Left <= info.rcMonitor.Left + tolerance &&
                              rect.Top <= info.rcMonitor.Top + tolerance &&
                              rect.Right >= info.rcMonitor.Right - tolerance &&
                              rect.Bottom >= info.rcMonitor.Bottom - tolerance;
            if (fullscreen)
            {
                reason = string.Format(CultureInfo.InvariantCulture, "{0}/{1} hwnd=0x{2:x}", processName, cls, hwnd.ToInt64());
            }
            return fullscreen;
        }

        private void TelemetryTimer_Tick(object sender, EventArgs e)
        {
            if (isClosing || IsDisposed) return;
            if (runtimeSuspended) return;
            if (!telemetryReady || telemetryCollector == null || telemetryCollectPending) return;
            telemetryCollectPending = true;

            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem((state) =>
                {
                    string statsJson = "";
                    try
                    {
                        if (isClosing || IsDisposed)
                        {
                            telemetryCollectPending = false;
                            return;
                        }
                        statsJson = telemetryCollector.CollectStats();
                        if (!TryBeginInvoke((MethodInvoker)delegate
                        {
                            try
                            {
                                PostWebMessageAsJsonSafe(statsJson, "Telemetry post");
                            }
                            catch (Exception ex)
                            {
                                Program.LogDebug("Telemetry post error: " + ex.Message + " | JSON: " + statsJson);
                            }
                            finally
                            {
                                telemetryCollectPending = false;
                            }
                        }))
                        {
                            telemetryCollectPending = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug("Telemetry collect error: " + ex.Message + " | JSON: " + statsJson);
                        try
                        {
                            if (!TryBeginInvoke((MethodInvoker)delegate
                            {
                                telemetryCollectPending = false;
                            }))
                            {
                                telemetryCollectPending = false;
                            }
                        }
                        catch
                        {
                            telemetryCollectPending = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                telemetryCollectPending = false;
                Program.LogDebug("TelemetryTimer_Tick error: " + ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            BeginCleanShutdown();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
                SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
                SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
                SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
                StopMouseHook();
                if (searchTimer != null)
                {
                    searchTimer.Dispose();
                }
                if (telemetryTimer != null)
                {
                    telemetryTimer.Dispose();
                }
                if (fullscreenTimer != null)
                {
                    fullscreenTimer.Dispose();
                }
                if (webView != null)
                {
                    ResilientWebView2 resilientWebView = webView as ResilientWebView2;
                    if (resilientWebView != null)
                    {
                        resilientWebView.BeginShutdown();
                    }

                    webView.Disposed -= WebView_Disposed;
                    try
                    {
                        webView.Dispose();
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Program.LogDebug("Suppressed WebView2 disposal after shutdown: " + ex.Message);
                    }
                    catch (InvalidOperationException ex)
                    {
                        if (!ResilientWebView2.IsDisposedLifecycleException(ex))
                        {
                            throw;
                        }

                        Program.LogDebug("Suppressed WebView2 disposal after shutdown: " + ex.Message);
                    }
                    finally
                    {
                        webView = null;
                    }
                }
            }
            base.Dispose(disposing);
        }

        private bool TryBeginInvoke(MethodInvoker action)
        {
            if (isClosing || IsDisposed || !IsHandleCreated)
            {
                return false;
            }

            try
            {
                BeginInvoke(action);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private bool TryGetCoreWebView2(out CoreWebView2 coreWebView)
        {
            coreWebView = null;
            if (isClosing || IsDisposed || webView == null || webView.IsDisposed)
            {
                if (!isClosing && !IsDisposed)
                {
                    QueueWebViewRecovery("CoreWebView2 unavailable");
                }

                return false;
            }

            try
            {
                coreWebView = webView.CoreWebView2;
                if (coreWebView == null)
                {
                    QueueWebViewRecovery("CoreWebView2 missing");
                    return false;
                }

                return coreWebView != null;
            }
            catch (ObjectDisposedException)
            {
                QueueWebViewRecovery("disposed CoreWebView2 access");
                return false;
            }
            catch (InvalidOperationException ex)
            {
                if (ResilientWebView2.IsDisposedLifecycleException(ex))
                {
                    QueueWebViewRecovery("disposed CoreWebView2 lifecycle");
                    return false;
                }

                if (!isClosing)
                {
                    Program.LogDebug("WebView unavailable: " + ex.Message);
                }
                return false;
            }
        }

        private void PostWebMessageAsJsonSafe(string payload, string context)
        {
            CoreWebView2 coreWebView;
            if (!TryGetCoreWebView2(out coreWebView))
            {
                return;
            }

            try
            {
                coreWebView.PostWebMessageAsJson(payload);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException ex)
            {
                if (!isClosing)
                {
                    Program.LogDebug(context + " skipped: " + ex.Message);
                }
            }
        }

        private sealed class ResilientWebView2 : WebView2
        {
            private bool shutdownStarted = false;

            internal void BeginShutdown()
            {
                shutdownStarted = true;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                try
                {
                    base.OnHandleCreated(e);
                }
                catch (ObjectDisposedException ex)
                {
                    if (!CanSuppressLifecycleException(ex))
                    {
                        throw;
                    }

                    LogSuppressedLifecycleException("handle creation", ex);
                }
                catch (InvalidOperationException ex)
                {
                    if (!CanSuppressLifecycleException(ex))
                    {
                        throw;
                    }

                    LogSuppressedLifecycleException("handle creation", ex);
                }
            }

            protected override void WndProc(ref Message m)
            {
                try
                {
                    base.WndProc(ref m);
                }
                catch (ObjectDisposedException ex)
                {
                    if (!CanSuppressLifecycleException(ex))
                    {
                        throw;
                    }

                    LogSuppressedLifecycleException("window message " + m.Msg.ToString(CultureInfo.InvariantCulture), ex);
                }
                catch (InvalidOperationException ex)
                {
                    if (!CanSuppressLifecycleException(ex))
                    {
                        throw;
                    }

                    LogSuppressedLifecycleException("window message " + m.Msg.ToString(CultureInfo.InvariantCulture), ex);
                }
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                try
                {
                    base.OnSizeChanged(e);
                }
                catch (ObjectDisposedException ex)
                {
                    if (!CanSuppressLifecycleException(ex))
                    {
                        throw;
                    }

                    LogSuppressedLifecycleException("resize", ex);
                }
                catch (InvalidOperationException ex)
                {
                    if (!CanSuppressLifecycleException(ex))
                    {
                        throw;
                    }

                    LogSuppressedLifecycleException("resize", ex);
                }
            }

            private bool CanSuppressLifecycleException(Exception ex)
            {
                if (ex is ObjectDisposedException)
                {
                    return shutdownStarted || IsDisposed || Disposing;
                }

                return IsDisposedLifecycleException(ex);
            }

            internal static bool IsDisposedLifecycleException(Exception ex)
            {
                InvalidOperationException invalid = ex as InvalidOperationException;
                if (invalid == null || string.IsNullOrEmpty(invalid.Message))
                {
                    return false;
                }

                return invalid.Message.IndexOf(
                    "CoreWebView2 members cannot be accessed after the WebView2 control is disposed",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static void LogSuppressedLifecycleException(string context, Exception ex)
            {
                Program.LogDebug("Suppressed WebView2 " + context + " after disposal: " + ex.Message);
            }
        }

        private static void StartMouseHook()
        {
            if (mouseHookThread != null && mouseHookThread.IsAlive) return;
            Volatile.Write(ref mouseHookStopRequested, 0);
            mouseProc = HookCallback;
            mouseHookThread = new Thread(RunMouseHook);
            mouseHookThread.IsBackground = true;
            mouseHookThread.Name = "NexusWpp mouse input";
            mouseHookThread.Start();
        }

        private static void RunMouseHook()
        {
            try
            {
                // Create the queue before publishing the ID, so shutdown can always
                // wake GetMessage, including a shutdown racing with startup.
                NativeMessage message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                Volatile.Write(ref mouseHookThreadId, unchecked((int)GetCurrentThreadId()));
                if (Volatile.Read(ref mouseHookStopRequested) != 0) return;

                hookId = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
                if (hookId == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                // Windows delivers WH_MOUSE_LL callbacks to the installing thread.
                // Keep this message pump separate from rendering and fullscreen scans.
                while (Volatile.Read(ref mouseHookStopRequested) == 0)
                {
                    int result = GetMessage(out message, IntPtr.Zero, 0, 0);
                    if (result == 0) break;
                    if (result < 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref mouseHookError, ex);
            }
            finally
            {
                IntPtr installedHook = Interlocked.Exchange(ref hookId, IntPtr.Zero);
                if (installedHook != IntPtr.Zero) UnhookWindowsHookEx(installedHook);
                Volatile.Write(ref mouseHookThreadId, 0);
            }
        }

        private static void StopMouseHook()
        {
            Volatile.Write(ref mouseRouting, null);
            Volatile.Write(ref mouseHookStopRequested, 1);
            int threadId = Volatile.Read(ref mouseHookThreadId);
            if (threadId != 0) PostThreadMessage(unchecked((uint)threadId), 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Motion, wheel and other buttons pass through without allocations or UI work.
            if (nCode < 0 || (wParam != (IntPtr)WM_LBUTTONDOWN && wParam != (IntPtr)WM_LBUTTONUP))
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            try
            {
                MouseRoutingSnapshot routing = Volatile.Read(ref mouseRouting);
                if (routing != null)
                {
                    MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (routing.ScreenBounds.Contains(hookStruct.pt.x, hookStruct.pt.y) &&
                        IsWindow(routing.RenderWindow) &&
                        ShouldForwardDesktopClick(hookStruct.pt, routing) &&
                        ForwardMouseClick(routing.RenderWindow, hookStruct.pt.x, hookStruct.pt.y, (uint)wParam.ToInt32()))
                    {
                        return (IntPtr)1; // Suppress desktop selection only after successful forwarding.
                    }
                }
            }
            catch (Exception ex)
            {
                // File I/O here would stall mouse input for the whole desktop.
                Interlocked.Exchange(ref mouseHookError, ex);
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private static bool ShouldForwardDesktopClick(POINT screenPt, MouseRoutingSnapshot routing)
        {
            IntPtr hit = WindowFromPoint(screenPt);
            if (hit == IntPtr.Zero) return false;
            if (hit == routing.HostWindow || hit == routing.RenderWindow) return true;

            IntPtr current = hit;
            for (int i = 0; i < 8 && current != IntPtr.Zero; i++)
            {
                if (current == routing.HostWindow || current == routing.RenderWindow) return true;

                string cls = GetWindowClassName(current);
                if (cls == "Progman" || cls == "WorkerW" || cls == "SHELLDLL_DefView" || cls == "SysListView32")
                {
                    return true;
                }

                uint pid;
                GetWindowThreadProcessId(current, out pid);
                if (pid == currentProcessId)
                {
                    return true;
                }

                current = GetParent(current);
            }

            return false;
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            System.Text.StringBuilder className = new System.Text.StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            return className.ToString();
        }

        private void FindRenderWindow()
        {
            try
            {
                renderWindow = IntPtr.Zero;
                EnumChildWindows(this.Handle, FindRenderWindowCallback, IntPtr.Zero);
            }
            catch { }
            PublishMouseRouting();
        }

        private void PublishMouseRouting()
        {
            if (isClosing || !IsHandleCreated || renderWindow == IntPtr.Zero || remotePanelBounds.IsEmpty)
            {
                Volatile.Write(ref mouseRouting, null);
                return;
            }
            System.Drawing.Point origin = PointToScreen(remotePanelBounds.Location);
            Volatile.Write(ref mouseRouting, new MouseRoutingSnapshot(
                new System.Drawing.Rectangle(origin, remotePanelBounds.Size), Handle, renderWindow));
        }

        private static bool FindRenderWindowCallback(IntPtr hwnd, IntPtr lParam)
        {
            System.Text.StringBuilder className = new System.Text.StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() == "Chrome_RenderWidgetHostHWND")
            {
                renderWindow = hwnd;
                return false;
            }
            return true;
        }

        private static bool ForwardMouseClick(IntPtr renderWin, int x, int y, uint msg)
        {
            POINT pt = new POINT { x = x, y = y };
            if (!ScreenToClient(renderWin, ref pt)) return false;
            IntPtr lParam = (IntPtr)((pt.y << 16) | (pt.x & 0xFFFF));
            IntPtr wParam = (IntPtr)(msg == WM_LBUTTONDOWN ? 1 : 0);
            return PostMessage(renderWin, msg, wParam, lParam);
        }

        private double GetDpiScale()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppliedDPI");
                        if (val != null)
                        {
                            int dpi = Convert.ToInt32(val);
                            return dpi / 96.0;
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (var g = this.CreateGraphics())
                {
                    return g.DpiX / 96.0;
                }
            }
            catch
            {
                return 1.0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    public static class Win32
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(
            out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime
        );

        [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme", SetLastError = true)]
        public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme", SetLastError = true)]
        public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid ActiveSchemeGuid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus([In, Out] SYSTEM_POWER_STATUS lpSystemPowerStatus);
    }

    [StructLayout(LayoutKind.Sequential)]
    public class SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public static class WlanApi
    {
        [DllImport("wlanapi.dll")]
        public static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        public static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        public static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        public static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(IntPtr pMemory);
    }

    public class TelemetryCollector
    {
        // Static / Boot cached specs
        public string MotherboardInfo = "";
        public string CpuInfo = "";
        public string CpuL2Cache = "";
        public string CpuL3Cache = "";
        public int CpuCores = 0;
        public int CpuLogical = 0;
        public string CpuBaseSpeed = "0.00";
        public int TotalRamGb = 0;
        public int RamSpeedMts = 0;
        public string RamType = "";
        public int RamModules = 0;
        public string NetworkName = "";
        public string IgpuInfo = "";
        public string IgpuDriver = "";
        public string IgpuDriverDate = "";
        public string DgpuDriver = "";
        public string DgpuDriverDate = "";
        public string GpuName = "";
        public string NpuName = "";
        public string NpuDriver = "";
        public string NpuDriverDate = "";
        public bool NpuDetected = false;
        public string DiskName = "";

        private string igpuLuid = "";
        private string dgpuLuid = "";
        private string npuLuid = "";
        private readonly List<string> directxAdapterLuids = new List<string>();
        private double cpuBaseSpeedVal = 3.80;

        // Active power plans cached list
        public class PowerPlanInfo
        {
            public string Guid;
            public string Name;
            public bool Active;
        }
        public List<PowerPlanInfo> PowerPlans = new List<PowerPlanInfo>();

        // Historical rates
        private ulong prevIdleTime = 0;
        private ulong prevSystemTime = 0;
        private long prevRxBytes = 0;
        private long prevTxBytes = 0;
        private DateTime prevNetTime = DateTime.MinValue;
        private int pingTime = -1;
        private DateTime lastPingTime = DateTime.MinValue;
        private bool isPingPending = false;

        // Process lists and cached top processes
        private string cachedTopProcessesJson = "[]";
        private string cachedTopRamProcessJson = "{}";
        private int topProcessCounter = 0;
        private bool topProcessUpdatePending = false;

        // Network rates cache
        private long cachedNetRxRate = 0;
        private long cachedNetTxRate = 0;
        private string cachedActiveIp = "127.0.0.1";
        private string cachedActiveIpv6 = "fe80::1";
        private string cachedActiveNetName = "Ethernet";
        private string cachedNetType = "Ethernet";
        private long cachedNetLinkSpeedMbps = 1000;

        // Slow perf counters cached separately so the UI can receive 2Hz payloads
        // without running every WMI query twice per second.
        private DateTime lastMemoryPerfTime = DateTime.MinValue;
        private long cachedRamCachedBytes = 0;
        private long cachedRamPoolPagedBytes = 0;
        private long cachedRamPoolNonPagedBytes = 0;
        private long cachedRamActivityVal = 0;
        private DateTime lastSystemPerfTime = DateTime.MinValue;
        private int cachedThreadsCount = 1200;
        private int cachedProcessesCount = 180;
        private DateTime lastGpuPerfTime = DateTime.MinValue;
        private int cachedIgpuUtil = 0;
        private long cachedIgpuMemBytes = 0;
        private int cachedNpuUtil = 0;
        private long cachedNpuMemBytes = 0;
        private int cachedDgpuUtil = 0;
        private long cachedDgpuMemBytes = 0;
        private int cachedIgpuDecodeUtil = 0;

        // NVIDIA telemetry is queried out of process so a faulty display driver cannot corrupt
        // the wallpaper host. Windows performance counters remain the fast utilization source.
        private DateTime lastNvidiaSmiTime = DateTime.MinValue;
        private DateTime lastNvidiaSmiErrorTime = DateTime.MinValue;
        private bool cachedNvidiaSmiSuccess = false;
        private int cachedNvidiaGpuUtil = 0;
        private int cachedNvidiaGpuTemp = -1;
        private int cachedNvidiaCoreClock = -1;
        private int cachedNvidiaMemoryClock = -1;
        private int cachedNvidiaPowerW = -1;
        private ulong cachedNvidiaVramTotal = 0;
        private ulong cachedNvidiaVramUsed = 0;

        // Wi-Fi signal cache
        private bool wlanUnavailable = false;
        private DateTime lastWifiSignalTime = DateTime.MinValue;
        private int cachedWifiSignal = -1;

        // Real sensors state
        private long dgpuVramTotalBytes = 0;
        private bool cpuThermalPerfUnavailable = false;
        private bool cpuThermalAcpiUnavailable = false;
        private DateTime lastCpuTempTime = DateTime.MinValue;
        private int cachedCpuTempC = -1;
        private double cachedCpuFreqGhz = 0.0;
        public void Initialize()
        {
            // Read GPUs AdapterLuid and names from Registry
            try
            {
                using (var rootKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX"))
                {
                    if (rootKey != null)
                    {
                        foreach (var subkeyName in rootKey.GetSubKeyNames())
                        {
                            using (var subkey = rootKey.OpenSubKey(subkeyName))
                            {
                                if (subkey != null)
                                {
                                    object descVal = subkey.GetValue("Description");
                                    string desc = descVal != null ? descVal.ToString() : "";
                                    object luidValObj = subkey.GetValue("AdapterLuid");
                                    if (luidValObj != null && !string.IsNullOrEmpty(desc))
                                    {
                                        long luidVal = Convert.ToInt64(luidValObj);
                                        uint low = (uint)(luidVal & 0xFFFFFFFF);
                                        uint high = (uint)((luidVal >> 32) & 0xFFFFFFFF);
                                        string formattedLuid = string.Format("0x{0:x8}_0x{1:x8}", high, low);
                                        if (!directxAdapterLuids.Contains(formattedLuid))
                                        {
                                            directxAdapterLuids.Add(formattedLuid);
                                        }
                                        
                                        string descLower = desc.ToLowerInvariant();
                                        if (descLower.Contains("nvidia"))
                                        {
                                            dgpuLuid = formattedLuid;
                                        }
                                        else if (descLower.Contains("intel") || descLower.Contains("uhd") || descLower.Contains("arc") ||
                                                 descLower.Contains("qualcomm") || descLower.Contains("adreno"))
                                        {
                                            igpuLuid = formattedLuid;
                                        }
                                        else if (descLower.Contains("radeon") || descLower.Contains("amd"))
                                        {
                                            if (IsIntegratedAmdGpuName(descLower))
                                            {
                                                if (string.IsNullOrEmpty(igpuLuid)) igpuLuid = formattedLuid;
                                            }
                                            else if (string.IsNullOrEmpty(dgpuLuid))
                                            {
                                                dgpuLuid = formattedLuid;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("DirectX Registry read error: " + ex.Message);
            }

            // Query CPU, Motherboard, RAM, GPU from WMI
            try
            {
                // Motherboard
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object mfgVal = obj["Manufacturer"];
                        object prodVal = obj["Product"];
                        string mfg = mfgVal != null ? mfgVal.ToString() : "";
                        string prod = prodVal != null ? prodVal.ToString() : "";
                        mfg = mfg.Replace(" Co., Ltd.", "").Replace("ASUSTeK COMPUTER INC.", "ASUS");
                        MotherboardInfo = string.Format("{0} ({1})", mfg, prod);
                        break;
                    }
                }

                // CPU
                using (var searcher = new ManagementObjectSearcher("SELECT Name, L2CacheSize, L3CacheSize, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object cpuNameVal = obj["Name"];
                        if (cpuNameVal != null) CpuInfo = cpuNameVal.ToString().Trim();
                        uint l2 = (uint)(obj["L2CacheSize"] ?? 0);
                        uint l3 = (uint)(obj["L3CacheSize"] ?? 0);
                        CpuL2Cache = string.Format("{0:F1} Mo", l2 / 1024.0);
                        CpuL3Cache = string.Format("{0:F1} Mo", l3 / 1024.0);
                        CpuCores = Convert.ToInt32(obj["NumberOfCores"] ?? CpuCores);
                        CpuLogical = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? CpuLogical);
                        uint maxClock = (uint)(obj["MaxClockSpeed"] ?? 0);
                        CpuBaseSpeed = (maxClock / 1000.0).ToString("F2", CultureInfo.InvariantCulture);
                        break;
                    }
                }
                double.TryParse(CpuBaseSpeed, NumberStyles.Any, CultureInfo.InvariantCulture, out cpuBaseSpeedVal);

                // RAM
                using (var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                {
                    ulong totalCapacity = 0;
                    uint speed = 0;
                    int modules = 0;
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        totalCapacity += (ulong)(obj["Capacity"] ?? 0);
                        speed = (uint)(obj["Speed"] ?? speed);
                        modules++;
                        if (string.IsNullOrEmpty(RamType))
                        {
                            int smbiosType = Convert.ToInt32(obj["SMBIOSMemoryType"] ?? 0);
                            RamType = MemoryTypeFromSmbios(smbiosType);
                        }
                    }
                    if (totalCapacity > 0)
                    {
                        TotalRamGb = (int)Math.Round(totalCapacity / (1024.0 * 1024.0 * 1024.0));
                    }
                    if (speed > 0)
                    {
                        RamSpeedMts = (int)speed;
                    }
                    RamModules = modules;
                }

                // GPUs
                using (var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object nameVal = obj["Name"];
                        object driverVerVal = obj["DriverVersion"];
                        object dateRawVal = obj["DriverDate"];
                        string name = nameVal != null ? nameVal.ToString() : "";
                        string driverVer = driverVerVal != null ? driverVerVal.ToString() : "";
                        string dateRaw = dateRawVal != null ? dateRawVal.ToString() : "";
                        string driverDate = "";
                        if (dateRaw.Length >= 8)
                        {
                            try
                            {
                                int y = int.Parse(dateRaw.Substring(0, 4));
                                int m = int.Parse(dateRaw.Substring(4, 2));
                                int d = int.Parse(dateRaw.Substring(6, 2));
                                driverDate = string.Format("{0:D2}/{1:D2}/{2:D4}", d, m, y);
                            }
                            catch {}
                        }

                        string nameLower = name.ToLowerInvariant();
                        if (nameLower.Contains("nvidia"))
                        {
                            GpuName = name;
                            DgpuDriver = driverVer;
                            DgpuDriverDate = driverDate;
                        }
                        else if (nameLower.Contains("intel") || nameLower.Contains("arc") || nameLower.Contains("uhd") ||
                                 nameLower.Contains("qualcomm") || nameLower.Contains("adreno"))
                        {
                            IgpuInfo = name;
                            IgpuDriver = driverVer;
                            IgpuDriverDate = driverDate;
                        }
                        else if (nameLower.Contains("radeon") || nameLower.Contains("amd"))
                        {
                            if (IsIntegratedAmdGpuName(nameLower))
                            {
                                if (string.IsNullOrEmpty(IgpuInfo))
                                {
                                    IgpuInfo = name;
                                    IgpuDriver = driverVer;
                                    IgpuDriverDate = driverDate;
                                }
                            }
                            else if (string.IsNullOrEmpty(GpuName))
                            {
                                GpuName = name;
                                DgpuDriver = driverVer;
                                DgpuDriverDate = driverDate;
                            }
                        }
                    }
                }

                using (var searcher = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceName LIKE '%AI Boost%' OR DeviceName LIKE '%Neural%' OR DeviceName LIKE '%VPU%' OR DeviceName LIKE '%Inference%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object nameVal = obj["DeviceName"];
                        object driverVerVal = obj["DriverVersion"];
                        object dateRawVal = obj["DriverDate"];
                        string name = nameVal != null ? nameVal.ToString() : "";
                        if (!IsLikelyNpuDeviceName(name))
                        {
                            continue;
                        }

                        NpuName = name;
                        NpuDetected = true;
                        NpuDriver = driverVerVal != null ? driverVerVal.ToString() : "";

                        string dateRaw = dateRawVal != null ? dateRawVal.ToString() : "";
                        if (dateRaw.Length >= 8)
                        {
                            try
                            {
                                int y = int.Parse(dateRaw.Substring(0, 4));
                                int m = int.Parse(dateRaw.Substring(4, 2));
                                int d = int.Parse(dateRaw.Substring(6, 2));
                                NpuDriverDate = string.Format("{0:D2}/{1:D2}/{2:D4}", d, m, y);
                            }
                            catch {}
                        }
                        break;
                    }
                }

                try
                {
                    string partitionDeviceId = "";
                    using (var searcher = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='C:'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            object deviceId = obj["DeviceID"];
                            if (deviceId != null)
                            {
                                partitionDeviceId = deviceId.ToString().Replace("\\", "\\\\").Replace("'", "\\'");
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(partitionDeviceId))
                    {
                        string query = string.Format(CultureInfo.InvariantCulture, "ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{0}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition", partitionDeviceId);
                        using (var searcher = new ManagementObjectSearcher(query))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                object model = obj["Model"];
                                if (model != null)
                                {
                                    DiskName = model.ToString().Trim();
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(DiskName))
                    {
                        using (var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_DiskDrive"))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                object model = obj["Model"];
                                if (model != null)
                                {
                                    DiskName = model.ToString().Trim();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Disk name detection error: " + ex.Message);
                }

                if (!NpuDetected)
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%AI Boost%' OR Name LIKE '%Neural%' OR Name LIKE '%VPU%' OR Name LIKE '%Inference%'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            object nameVal = obj["Name"];
                            string name = nameVal != null ? nameVal.ToString() : "";
                            if (IsLikelyNpuDeviceName(name))
                            {
                                NpuName = name;
                                NpuDetected = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("Telemetry init error: " + ex.Message);
            }

            ReadDgpuVramTotalFromRegistry();

            // Initialize power plans
            UpdatePowerPlansCache();
        }

        private bool TryGetNvidiaStats(out int utilization, out int temperature, out int coreClock,
            out int memoryClock, out int powerWatts, out ulong vramTotal, out ulong vramUsed)
        {
            if (lastNvidiaSmiTime == DateTime.MinValue || (DateTime.Now - lastNvidiaSmiTime).TotalSeconds >= 5.0)
            {
                lastNvidiaSmiTime = DateTime.Now;
                cachedNvidiaSmiSuccess = QueryNvidiaSmi();
            }

            utilization = cachedNvidiaGpuUtil;
            temperature = cachedNvidiaGpuTemp;
            coreClock = cachedNvidiaCoreClock;
            memoryClock = cachedNvidiaMemoryClock;
            powerWatts = cachedNvidiaPowerW;
            vramTotal = cachedNvidiaVramTotal;
            vramUsed = cachedNvidiaVramUsed;
            return cachedNvidiaSmiSuccess;
        }

        private bool QueryNvidiaSmi()
        {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string executable = Path.Combine(windowsDirectory, "System32", "nvidia-smi.exe");
            if (!File.Exists(executable)) return false;

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(executable,
                    "--query-gpu=utilization.gpu,temperature.gpu,clocks.gr,clocks.mem,power.draw,memory.total,memory.used --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    if (!process.WaitForExit(1500))
                    {
                        try { process.Kill(); } catch {}
                        throw new TimeoutException("nvidia-smi did not respond within 1500 ms.");
                    }
                    string output = process.StandardOutput.ReadLine();
                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return false;

                    string[] values = output.Split(',');
                    if (values.Length < 7) return false;

                    double parsedPower;
                    double parsedTotalMiB;
                    double parsedUsedMiB;
                    int parsedUtilization;
                    int parsedTemperature;
                    int parsedCoreClock;
                    int parsedMemoryClock;
                    if (!int.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUtilization) ||
                        !int.TryParse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedTemperature) ||
                        !int.TryParse(values[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedCoreClock) ||
                        !int.TryParse(values[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedMemoryClock) ||
                        !double.TryParse(values[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedPower) ||
                        !double.TryParse(values[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedTotalMiB) ||
                        !double.TryParse(values[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedUsedMiB))
                    {
                        return false;
                    }

                    cachedNvidiaGpuUtil = Math.Max(0, Math.Min(100, parsedUtilization));
                    cachedNvidiaGpuTemp = parsedTemperature;
                    cachedNvidiaCoreClock = parsedCoreClock;
                    cachedNvidiaMemoryClock = parsedMemoryClock;
                    cachedNvidiaPowerW = (int)Math.Round(parsedPower);
                    cachedNvidiaVramTotal = (ulong)Math.Max(0.0, parsedTotalMiB * 1024.0 * 1024.0);
                    cachedNvidiaVramUsed = (ulong)Math.Max(0.0, parsedUsedMiB * 1024.0 * 1024.0);
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (lastNvidiaSmiErrorTime == DateTime.MinValue || (DateTime.Now - lastNvidiaSmiErrorTime).TotalMinutes >= 1.0)
                {
                    lastNvidiaSmiErrorTime = DateTime.Now;
                    Program.LogDebug("NVIDIA telemetry process failed safely: " + ex.Message);
                }
                return false;
            }
        }

        private static string MemoryTypeFromSmbios(int smbiosType)
        {
            switch (smbiosType)
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                default: return "";
            }
        }

        // AMD APU iGPUs are reported as "AMD Radeon(TM) Graphics" or "Radeon Vega 8 Graphics";
        // discrete boards carry a model number such as "AMD Radeon RX 7800 XT".
        private static bool IsIntegratedAmdGpuName(string lowerName)
        {
            return lowerName.EndsWith("graphics") && !lowerName.Contains(" rx");
        }

        // Reads the dedicated VRAM size reported by the display driver (real hardware value).
        private void ReadDgpuVramTotalFromRegistry()
        {
            if (string.IsNullOrEmpty(GpuName)) return;

            try
            {
                using (var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (classKey == null) return;

                    foreach (var subkeyName in classKey.GetSubKeyNames())
                    {
                        using (var subkey = classKey.OpenSubKey(subkeyName))
                        {
                            if (subkey == null) continue;
                            string desc = Convert.ToString(subkey.GetValue("DriverDesc") ?? "");
                            if (string.IsNullOrEmpty(desc)) continue;
                            if (!desc.Equals(GpuName, StringComparison.OrdinalIgnoreCase)) continue;

                            object qw = subkey.GetValue("HardwareInformation.qwMemorySize");
                            if (qw != null)
                            {
                                long bytes = Convert.ToInt64(qw);
                                if (bytes > 0)
                                {
                                    dgpuVramTotalBytes = bytes;
                                    return;
                                }
                            }

                            object dw = subkey.GetValue("HardwareInformation.MemorySize");
                            byte[] raw = dw as byte[];
                            if (raw != null && raw.Length >= 4)
                            {
                                long bytes = BitConverter.ToUInt32(raw, 0);
                                if (bytes > 0)
                                {
                                    dgpuVramTotalBytes = bytes;
                                    return;
                                }
                            }
                            else if (dw != null)
                            {
                                long bytes = Convert.ToInt64(dw);
                                if (bytes > 0)
                                {
                                    dgpuVramTotalBytes = bytes;
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("VRAM registry read error: " + ex.Message);
            }
        }

        // Real CPU temperature. Tries the thermal zone performance counter first because it is
        // readable without administrator rights, then the ACPI WMI class. Returns -1 when no
        // sensor is exposed so the UI can substitute another real metric.
        private int GetCpuTemperature()
        {
            if (lastCpuTempTime != DateTime.MinValue && (DateTime.Now - lastCpuTempTime).TotalSeconds < 5.0)
            {
                return cachedCpuTempC;
            }
            lastCpuTempTime = DateTime.Now;

            double maxCelsius = double.MinValue;

            if (!cpuThermalPerfUnavailable)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            double celsius = Convert.ToDouble(obj["Temperature"] ?? 0.0) - 273.15;
                            if (celsius > maxCelsius) maxCelsius = celsius;
                        }
                    }
                }
                catch
                {
                    cpuThermalPerfUnavailable = true;
                }
            }

            if ((maxCelsius <= 5.0 || maxCelsius >= 120.0) && !cpuThermalAcpiUnavailable)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            double deciKelvin = Convert.ToDouble(obj["CurrentTemperature"] ?? 0.0);
                            double celsius = (deciKelvin / 10.0) - 273.15;
                            if (celsius > maxCelsius) maxCelsius = celsius;
                        }
                    }
                }
                catch
                {
                    // Usually access denied without elevation: stop querying this source.
                    cpuThermalAcpiUnavailable = true;
                }
            }

            cachedCpuTempC = (maxCelsius > 5.0 && maxCelsius < 120.0) ? (int)Math.Round(maxCelsius) : -1;
            return cachedCpuTempC;
        }

        private static bool IsLikelyNpuDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string lower = name.ToLowerInvariant();
            return lower.Contains("ai boost") ||
                   lower.Contains("neural") ||
                   lower.Contains("inference") ||
                   lower.Contains(" vpu") ||
                   lower.Contains("(vpu") ||
                   lower.Equals("npu") ||
                   lower.StartsWith("npu ") ||
                   lower.Contains(" npu ") ||
                   lower.Contains("(npu");
        }

        private string DiscoverNpuLuid()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_compute%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = Convert.ToString(obj["Name"] ?? "");
                        string luid = ExtractLuidFromCounterName(name);
                        if (string.IsNullOrEmpty(luid))
                        {
                            continue;
                        }
                        if (luid.Equals(igpuLuid, StringComparison.OrdinalIgnoreCase) ||
                            luid.Equals(dgpuLuid, StringComparison.OrdinalIgnoreCase) ||
                            directxAdapterLuids.Contains(luid))
                        {
                            continue;
                        }
                        return luid;
                    }
                }
            }
            catch {}

            return "";
        }

        private static string ExtractLuidFromCounterName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }

            int start = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return "";
            }
            start += 5;
            int end = name.IndexOf("_phys_", start, StringComparison.OrdinalIgnoreCase);
            if (end <= start)
            {
                return "";
            }
            return name.Substring(start, end - start).ToLowerInvariant();
        }

        public void UpdatePowerPlansCache()
        {
            try
            {
                var newPlans = new List<PowerPlanInfo>();
                ProcessStartInfo startInfo = new ProcessStartInfo("powercfg", "/list")
                {
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(startInfo))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"GUID.*:\s*([a-f0-9\-]+)\s*\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            string guid = match.Groups[1].Value.Trim();
                            string name = match.Groups[2].Value.Trim();
                            bool active = line.Contains("*");
                            newPlans.Add(new PowerPlanInfo { Guid = guid, Name = name, Active = active });
                        }
                    }
                }
                if (newPlans.Count > 0)
                {
                    PowerPlans = newPlans;
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug("UpdatePowerPlansCache error: " + ex.Message);
            }
        }

        public string GetActivePowerPlanGuid()
        {
            try
            {
                IntPtr activeGuidPtr;
                uint res = Win32.PowerGetActiveScheme(IntPtr.Zero, out activeGuidPtr);
                if (res == 0 && activeGuidPtr != IntPtr.Zero)
                {
                    Guid activeGuid = (Guid)Marshal.PtrToStructure(activeGuidPtr, typeof(Guid));
                    Win32.LocalFree(activeGuidPtr);
                    return activeGuid.ToString();
                }
            }
            catch {}

            return "";
        }

        public bool SetActivePowerPlan(string guidStr, out string activeGuid, out string error)
        {
            activeGuid = "";
            error = "";

            Guid requestedGuid;
            if (!Guid.TryParse(guidStr, out requestedGuid))
            {
                error = "invalid GUID";
                activeGuid = GetActivePowerPlanGuid();
                return false;
            }

            if (PowerPlans.Count > 0 && !PowerPlans.Any(p => p.Guid.Equals(guidStr, StringComparison.OrdinalIgnoreCase)))
            {
                UpdatePowerPlansCache();
                if (!PowerPlans.Any(p => p.Guid.Equals(guidStr, StringComparison.OrdinalIgnoreCase)))
                {
                    error = "GUID not found in powercfg /list";
                    activeGuid = GetActivePowerPlanGuid();
                    return false;
                }
            }

            uint result = Win32.PowerSetActiveScheme(IntPtr.Zero, ref requestedGuid);
            if (result != 0)
            {
                error = "PowerSetActiveScheme=" + result.ToString(CultureInfo.InvariantCulture);
            }

            Thread.Sleep(120);
            activeGuid = GetActivePowerPlanGuid();
            bool success = activeGuid.Equals(guidStr, StringComparison.OrdinalIgnoreCase);
            if (success)
            {
                foreach (var plan in PowerPlans)
                {
                    plan.Active = plan.Guid.Equals(activeGuid, StringComparison.OrdinalIgnoreCase);
                }
            }
            else if (string.IsNullOrEmpty(error))
            {
                error = "active scheme did not match request";
            }

            return success;
        }

        // Virtual switches, VM adapters and VPN tunnels must not pollute the displayed identity
        // or double-count traffic that also flows through the physical adapter.
        private static bool IsVirtualOrTunnelAdapter(NetworkInterface ni)
        {
            string text = (ni.Description + " " + ni.Name).ToLowerInvariant();
            return text.Contains("virtual") || text.Contains("vmware") || text.Contains("virtualbox") ||
                   text.Contains("vbox") || text.Contains("hyper-v") || text.Contains("vethernet") ||
                   text.Contains("tap-") || text.Contains("wintun") || text.Contains("wireguard") ||
                   text.Contains("openvpn") || text.Contains("tailscale") || text.Contains("zerotier") ||
                   text.Contains("loopback") || text.Contains("bluetooth");
        }

        private void UpdateNetworkStats()
        {
            try
            {
                long currentRx = 0;
                long currentTx = 0;
                string activeIp = "127.0.0.1";
                string activeIpv6 = "fe80::1";
                string activeNetName = "Ethernet";
                string netType = "Ethernet";
                long linkSpeedMbps = cachedNetLinkSpeedMbps;
                bool identityPinnedToGateway = false;

                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && 
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback && 
                        ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        !IsVirtualOrTunnelAdapter(ni))
                    {
                        var ipProps = ni.GetIPProperties();
                        var stats = ni.GetIPv4Statistics();
                        currentRx += stats.BytesReceived;
                        currentTx += stats.BytesSent;

                        // The interface holding the default gateway is the real internet-facing one.
                        bool hasGateway = ipProps.GatewayAddresses.Count > 0;
                        if (identityPinnedToGateway && !hasGateway)
                        {
                            continue;
                        }

                        bool gotIpv4 = false;
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                activeIp = addr.Address.ToString();
                                activeNetName = ni.Description;
                                netType = (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) ? "Wi-Fi" : "Ethernet";
                                if (ni.Speed > 0)
                                {
                                    linkSpeedMbps = Math.Max(1, ni.Speed / 1000000L);
                                }
                                gotIpv4 = true;
                            }
                            else if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                            {
                                activeIpv6 = addr.Address.ToString();
                            }
                        }

                        if (hasGateway && gotIpv4)
                        {
                            identityPinnedToGateway = true;
                        }
                    }
                }

                if (prevNetTime != DateTime.MinValue)
                {
                    double secElapsed = (DateTime.Now - prevNetTime).TotalSeconds;
                    if (secElapsed > 0.1)
                    {
                        cachedNetRxRate = (long)Math.Max(0, (currentRx - prevRxBytes) / 1024.0 / secElapsed);
                        cachedNetTxRate = (long)Math.Max(0, (currentTx - prevTxBytes) / 1024.0 / secElapsed);
                    }
                }
                prevRxBytes = currentRx;
                prevTxBytes = currentTx;
                prevNetTime = DateTime.Now;

                cachedActiveIp = activeIp;
                cachedActiveIpv6 = activeIpv6;
                cachedActiveNetName = activeNetName;
                cachedNetType = netType;
                cachedNetLinkSpeedMbps = linkSpeedMbps;
            }
            catch {}
        }

        private void UpdatePing()
        {
            if (isPingPending) return;
            if (lastPingTime != DateTime.MinValue && (DateTime.Now - lastPingTime).TotalSeconds < 5.0) return;

            lastPingTime = DateTime.Now;
            isPingPending = true;

            try
            {
                Ping pingSender = new Ping();
                pingSender.PingCompleted += (s, e) =>
                {
                    isPingPending = false;
                    if (e.Reply != null && e.Reply.Status == IPStatus.Success)
                    {
                        pingTime = (int)e.Reply.RoundtripTime;
                    }
                    else
                    {
                        pingTime = -1;
                    }
                    try { ((Ping)s).Dispose(); } catch {}
                };
                pingSender.SendAsync("1.1.1.1", 1000, null);
            }
            catch
            {
                isPingPending = false;
            }
        }

        // Real Wi-Fi signal quality (0-100) of the connected interface through the native WLAN
        // API, which is locale independent. Returns -1 when not connected or unsupported.
        private int GetWifiSignal()
        {
            if (wlanUnavailable) return -1;
            if (lastWifiSignalTime != DateTime.MinValue && (DateTime.Now - lastWifiSignalTime).TotalSeconds < 5.0)
            {
                return cachedWifiSignal;
            }
            lastWifiSignalTime = DateTime.Now;

            IntPtr handle = IntPtr.Zero;
            IntPtr listPtr = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                if (WlanApi.WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out handle) != 0)
                {
                    cachedWifiSignal = -1;
                    return -1;
                }
                if (WlanApi.WlanEnumInterfaces(handle, IntPtr.Zero, out listPtr) != 0)
                {
                    cachedWifiSignal = -1;
                    return -1;
                }

                // WLAN_INTERFACE_INFO_LIST layout: dwNumberOfItems(4) + dwIndex(4) + items.
                // WLAN_INTERFACE_INFO layout: GUID(16) + description WCHAR[256](512) + state(4) = 532 bytes.
                int count = Marshal.ReadInt32(listPtr, 0);
                int best = -1;
                for (int i = 0; i < count; i++)
                {
                    IntPtr infoPtr = new IntPtr(listPtr.ToInt64() + 8 + ((long)i * 532));
                    int state = Marshal.ReadInt32(infoPtr, 528);
                    if (state != 1) continue; // wlan_interface_state_connected

                    Guid ifaceGuid = (Guid)Marshal.PtrToStructure(infoPtr, typeof(Guid));
                    uint dataSize;
                    IntPtr dataPtr;
                    // 7 = wlan_intf_opcode_current_connection
                    if (WlanApi.WlanQueryInterface(handle, ref ifaceGuid, 7, IntPtr.Zero, out dataSize, out dataPtr, IntPtr.Zero) == 0)
                    {
                        try
                        {
                            // wlanSignalQuality offset in WLAN_CONNECTION_ATTRIBUTES:
                            // isState(4) + mode(4) + profile(512) + ssid(36) + bssType(4) + bssid(6+2) + phyType(4) + phyIndex(4) = 576
                            if (dataSize >= 580)
                            {
                                int signal = Marshal.ReadInt32(dataPtr, 576);
                                if (signal >= 0 && signal <= 100 && signal > best)
                                {
                                    best = signal;
                                }
                            }
                        }
                        finally
                        {
                            WlanApi.WlanFreeMemory(dataPtr);
                        }
                    }
                }

                cachedWifiSignal = best;
                return best;
            }
            catch
            {
                // wlanapi.dll missing (Server Core) or WLAN service stopped: stop querying.
                wlanUnavailable = true;
                cachedWifiSignal = -1;
                return -1;
            }
            finally
            {
                if (listPtr != IntPtr.Zero)
                {
                    try { WlanApi.WlanFreeMemory(listPtr); } catch {}
                }
                if (handle != IntPtr.Zero)
                {
                    try { WlanApi.WlanCloseHandle(handle, IntPtr.Zero); } catch {}
                }
            }
        }

        private void UpdateTopProcesses()
        {
            topProcessCounter++;
            if (topProcessCounter < 4) return; // run once every 4 seconds
            topProcessCounter = 0;
            if (topProcessUpdatePending) return;
            topProcessUpdatePending = true;

            System.Threading.ThreadPool.QueueUserWorkItem((state) =>
            {
                try
                {
                    var processes = new List<Tuple<string, int, long>>();
                    int cpuCount = Environment.ProcessorCount;
                    if (cpuCount <= 0) cpuCount = 1;

                    using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, PercentProcessorTime, WorkingSetPrivate FROM Win32_PerfFormattedData_PerfProc_Process WHERE Name <> '_Total' AND Name <> 'Idle'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            object nameVal = obj["Name"];
                            string name = nameVal != null ? nameVal.ToString() : "";
                            int hashIndex = name.IndexOf('#');
                            if (hashIndex != -1) name = name.Substring(0, hashIndex);

                            int pct = Convert.ToInt32(obj["PercentProcessorTime"] ?? 0);
                            int cpuPercent = (int)Math.Min(99, Math.Round((double)pct / cpuCount));
                            long ramBytes = Convert.ToInt64(obj["WorkingSetPrivate"] ?? 0L);
                            long ramMb = ramBytes / (1024 * 1024);

                            processes.Add(new Tuple<string, int, long>(name, cpuPercent, ramMb));
                        }
                    }

                    var topList = processes.OrderByDescending(p => p.Item2).Take(4).ToList();

                    StringBuilder sb = new StringBuilder();
                    sb.Append("[");
                    for (int i = 0; i < topList.Count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.AppendFormat(CultureInfo.InvariantCulture, "{{\"Name\":{0},\"PercentProcessorTime\":{1},\"WorkingSetPrivate\":{2}}}", 
                            JsonString(topList[i].Item1), topList[i].Item2 * cpuCount, topList[i].Item3 * (long)1024 * 1024);
                    }
                    sb.Append("]");

                    var topRam = processes.OrderByDescending(p => p.Item3).FirstOrDefault();
                    string topRamJson = topRam != null
                        ? string.Format(CultureInfo.InvariantCulture, "{{\"name\":{0},\"ramMb\":{1}}}", JsonString(topRam.Item1), topRam.Item3)
                        : "{}";
                    
                    lock (this)
                    {
                        cachedTopProcessesJson = sb.ToString();
                        cachedTopRamProcessJson = topRamJson;
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug("UpdateTopProcesses error: " + ex.Message);
                }
                finally
                {
                    topProcessUpdatePending = false;
                }
            });
        }

        private static string JsonString(string value)
        {
            if (value == null) return "\"\"";
            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append(@"\b"); break;
                    case '\f': sb.Append(@"\f"); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\t': sb.Append(@"\t"); break;
                    default:
                        if (c < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public string CollectStats()
        {
            // Gather CPU Load
            int cpuLoad = 0;
            System.Runtime.InteropServices.ComTypes.FILETIME idleTime, kernelTime, userTime;
            if (Win32.GetSystemTimes(out idleTime, out kernelTime, out userTime))
            {
                ulong idle = ((ulong)idleTime.dwHighDateTime << 32) | (uint)idleTime.dwLowDateTime;
                ulong kernel = ((ulong)kernelTime.dwHighDateTime << 32) | (uint)kernelTime.dwLowDateTime;
                ulong user = ((ulong)userTime.dwHighDateTime << 32) | (uint)userTime.dwLowDateTime;
                ulong system = kernel + user;

                if (prevSystemTime > 0)
                {
                    ulong systemDiff = system - prevSystemTime;
                    ulong idleDiff = idle - prevIdleTime;
                    if (systemDiff > 0)
                    {
                        double load = 1.0 - (double)idleDiff / systemDiff;
                        cpuLoad = (int)Math.Max(0, Math.Min(100, Math.Round(load * 100)));
                    }
                }
                prevIdleTime = idle;
                prevSystemTime = system;
            }

            // Real ACPI sensor; -1 when the machine does not expose a thermal zone.
            int cpuTemp = GetCpuTemperature();

            // RAM usage
            int ramLoad = 0;
            ulong ramTotalPhys = 0;
            ulong ramFreePhys = 0;
            ulong ramTotalPage = 0;
            ulong ramAvailPage = 0;
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            if (Win32.GlobalMemoryStatusEx(memStatus))
            {
                ramLoad = (int)memStatus.dwMemoryLoad;
                ramTotalPhys = memStatus.ullTotalPhys;
                ramFreePhys = memStatus.ullAvailPhys;
                ramTotalPage = memStatus.ullTotalPageFile;
                ramAvailPage = memStatus.ullAvailPageFile;
            }

            // Disk Space
            double diskFreeGb = 0.0;
            double diskTotalGb = 0.0;
            int diskStoragePercent = 0;
            try
            {
                var drive = new DriveInfo("C");
                diskFreeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                diskTotalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                diskStoragePercent = (int)Math.Round((1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100);
            }
            catch {}

            // Network stats
            UpdateNetworkStats();

            // Query fast WMI metrics
            long commitUsedBytes = (long)(ramTotalPage - ramAvailPage);
            long commitLimitBytes = (long)ramTotalPage;
            long ramCachedBytes = cachedRamCachedBytes;
            long ramPoolPagedBytes = cachedRamPoolPagedBytes;
            long ramPoolNonPagedBytes = cachedRamPoolNonPagedBytes;
            long ramActivityVal = cachedRamActivityVal;
            if (lastMemoryPerfTime == DateTime.MinValue || (DateTime.Now - lastMemoryPerfTime).TotalSeconds >= 2.0)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT CacheBytes, StandbyCacheNormalPriorityBytes, StandbyCacheReserveBytes, StandbyCacheCoreBytes, PoolPagedBytes, PoolNonpagedBytes, PageFaultsPersec FROM Win32_PerfFormattedData_PerfOS_Memory"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            ulong cache = (ulong)(obj["CacheBytes"] ?? 0);
                            ulong standbyNormal = (ulong)(obj["StandbyCacheNormalPriorityBytes"] ?? 0);
                            ulong standbyReserve = (ulong)(obj["StandbyCacheReserveBytes"] ?? 0);
                            ulong standbyCore = (ulong)(obj["StandbyCacheCoreBytes"] ?? 0);
                            ramCachedBytes = (long)(cache + standbyNormal + standbyReserve + standbyCore);
                            ramPoolPagedBytes = (long)((ulong)(obj["PoolPagedBytes"] ?? 0));
                            ramPoolNonPagedBytes = (long)((ulong)(obj["PoolNonpagedBytes"] ?? 0));
                            ramActivityVal = (long)((uint)(obj["PageFaultsPersec"] ?? 0));
                            cachedRamCachedBytes = ramCachedBytes;
                            cachedRamPoolPagedBytes = ramPoolPagedBytes;
                            cachedRamPoolNonPagedBytes = ramPoolNonPagedBytes;
                            cachedRamActivityVal = ramActivityVal;
                            lastMemoryPerfTime = DateTime.Now;
                            break;
                        }
                    }
                }
                catch {}
            }

            int threadsCount = cachedThreadsCount;
            int processesCount = cachedProcessesCount;
            if (lastSystemPerfTime == DateTime.MinValue || (DateTime.Now - lastSystemPerfTime).TotalSeconds >= 2.0)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Threads, Processes FROM Win32_PerfFormattedData_PerfOS_System"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            threadsCount = Convert.ToInt32(obj["Threads"] ?? 0);
                            processesCount = Convert.ToInt32(obj["Processes"] ?? 0);
                            cachedThreadsCount = threadsCount;
                            cachedProcessesCount = processesCount;
                            lastSystemPerfTime = DateTime.Now;
                            break;
                        }
                    }
                }
                catch {}

                // Real average core frequency. The performance percentage is relative to the
                // counter's own ProcessorFrequency base (true base clock on hybrid CPUs), not
                // to the WMI MaxClockSpeed value which can be a different reference.
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT PercentProcessorPerformance, ProcessorFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name LIKE '%_Total'"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            double perfPct = Convert.ToDouble(obj["PercentProcessorPerformance"] ?? 0.0);
                            double counterBaseMhz = Convert.ToDouble(obj["ProcessorFrequency"] ?? 0.0);
                            if (perfPct > 0 && counterBaseMhz > 0)
                            {
                                cachedCpuFreqGhz = counterBaseMhz * perfPct / 100.0 / 1000.0;
                            }
                            break;
                        }
                    }
                }
                catch {}
            }

            long diskReadSec = 0;
            long diskWriteSec = 0;
            long diskActiveTime = 0;
            double diskResponse = 0.0;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT DiskReadBytesPersec, DiskWriteBytesPersec, PercentDiskTime, AvgDiskSecPerTransfer FROM Win32_PerfFormattedData_PerfDisk_LogicalDisk WHERE Name = 'C:'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        diskReadSec = (long)((ulong)(obj["DiskReadBytesPersec"] ?? 0));
                        diskWriteSec = (long)((ulong)(obj["DiskWriteBytesPersec"] ?? 0));
                        diskActiveTime = (long)((uint)(obj["PercentDiskTime"] ?? 0));
                        diskResponse = Convert.ToDouble(obj["AvgDiskSecPerTransfer"] ?? 0.0);
                        break;
                    }
                }
            }
            catch {}

            double diskReadMbNum = diskReadSec / (1024.0 * 1024.0);
            double diskWriteMbNum = diskWriteSec / (1024.0 * 1024.0);
            double diskThroughputMb = diskReadMbNum + diskWriteMbNum;
            int diskThroughputActivity = diskThroughputMb < 0.05
                ? 0
                : (int)Math.Ceiling(Math.Min(100.0, Math.Sqrt(diskThroughputMb / 2500.0) * 100.0));
            diskActiveTime = Math.Max(0, Math.Min(100, Math.Max(diskActiveTime, diskThroughputActivity)));

            // GPU stats
            int igpuUtil = cachedIgpuUtil;
            long igpuMemBytes = cachedIgpuMemBytes;
            int igpuDecodeUtil = cachedIgpuDecodeUtil;
            int npuUtil = cachedNpuUtil;
            long npuMemBytes = cachedNpuMemBytes;
            int dgpuUtil = cachedDgpuUtil;
            if (lastGpuPerfTime == DateTime.MinValue || (DateTime.Now - lastGpuPerfTime).TotalSeconds >= 2.0)
            {
                int nextIgpuUtil = 0;
                long nextIgpuMemBytes = igpuMemBytes;
                int nextIgpuDecodeUtil = 0;
                int nextNpuUtil = 0;
                long nextNpuMemBytes = npuMemBytes;
                int nextDgpuUtil = 0;
                long nextDgpuMemBytes = cachedDgpuMemBytes;
                try
                {
                    if (NpuDetected && string.IsNullOrEmpty(npuLuid))
                    {
                        npuLuid = DiscoverNpuLuid();
                    }

                    if (!string.IsNullOrEmpty(igpuLuid))
                    {
                        string filter = string.Format("Name LIKE '%{0}%engtype_3d%'", igpuLuid);
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE " + filter))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                nextIgpuUtil += Convert.ToInt32(obj["UtilizationPercentage"] ?? 0);
                            }
                        }

                        string decodeFilter = string.Format("Name LIKE '%{0}%engtype_videodecode%'", igpuLuid);
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE " + decodeFilter))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                nextIgpuDecodeUtil += Convert.ToInt32(obj["UtilizationPercentage"] ?? 0);
                            }
                        }
                    
                        string memFilter = string.Format("Name LIKE '%{0}%'", igpuLuid);
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory WHERE " + memFilter))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                nextIgpuMemBytes = Convert.ToInt64(obj["SharedUsage"] ?? 0L);
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(dgpuLuid))
                    {
                        string filter = string.Format("Name LIKE '%{0}%engtype_3d%'", dgpuLuid);
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE " + filter))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                nextDgpuUtil += Convert.ToInt32(obj["UtilizationPercentage"] ?? 0);
                            }
                        }

                        string memFilter = string.Format("Name LIKE '%{0}%'", dgpuLuid);
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory WHERE " + memFilter))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                nextDgpuMemBytes = Convert.ToInt64(obj["DedicatedUsage"] ?? 0L);
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(npuLuid))
                    {
                        string filter = string.Format("Name LIKE '%{0}%engtype_compute%'", npuLuid);
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE " + filter))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                nextNpuUtil += Convert.ToInt32(obj["UtilizationPercentage"] ?? 0);
                            }
                        }

                        string memFilter = string.Format("Name LIKE '%{0}%'", npuLuid);
                        using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory WHERE " + memFilter))
                        {
                            foreach (ManagementObject obj in searcher.Get())
                            {
                                nextNpuMemBytes = Convert.ToInt64(obj["SharedUsage"] ?? 0L);
                                break;
                            }
                        }
                    }

                    cachedIgpuUtil = nextIgpuUtil;
                    cachedIgpuMemBytes = nextIgpuMemBytes;
                    cachedIgpuDecodeUtil = Math.Min(100, nextIgpuDecodeUtil);
                    cachedNpuUtil = nextNpuUtil;
                    cachedNpuMemBytes = nextNpuMemBytes;
                    cachedDgpuUtil = nextDgpuUtil;
                    cachedDgpuMemBytes = nextDgpuMemBytes;
                    igpuUtil = cachedIgpuUtil;
                    igpuMemBytes = cachedIgpuMemBytes;
                    igpuDecodeUtil = cachedIgpuDecodeUtil;
                    npuUtil = cachedNpuUtil;
                    npuMemBytes = cachedNpuMemBytes;
                    dgpuUtil = cachedDgpuUtil;
                    lastGpuPerfTime = DateTime.Now;
                }
                catch {}
            }

            // NVIDIA metrics are isolated in nvidia-smi. A driver crash can only terminate the
            // short-lived helper process, never this wallpaper process.
            int gpuUtil = dgpuUtil;
            int gpuTemp = -1;
            int gpuCoreClock = -1;
            int gpuMemClock = -1;
            int gpuPowerW = -1;
            ulong vramTotal = 0;
            ulong vramUsed = 0;
            bool nvidiaStatsSuccess = TryGetNvidiaStats(out gpuUtil, out gpuTemp, out gpuCoreClock,
                out gpuMemClock, out gpuPowerW, out vramTotal, out vramUsed);

            if (!nvidiaStatsSuccess)
            {
                // Real fallback: driver-reported VRAM size and the Windows dedicated-memory counter.
                vramTotal = (ulong)Math.Max(0, dgpuVramTotalBytes);
                vramUsed = (ulong)Math.Max(0, cachedDgpuMemBytes);
            }

            UpdatePing();
            UpdateTopProcesses();

            bool igpuDetected = !string.IsNullOrEmpty(igpuLuid) || !string.IsNullOrEmpty(IgpuInfo);
            bool dgpuDetected = nvidiaStatsSuccess || !string.IsNullOrEmpty(dgpuLuid) || !string.IsNullOrEmpty(GpuName);

            int wifiSignal = cachedNetType == "Wi-Fi" ? GetWifiSignal() : -1;

            // Real battery state (laptops); flag 128 means no system battery.
            bool batteryPresent = false;
            int batteryPercent = -1;
            bool acLine = true;
            try
            {
                SYSTEM_POWER_STATUS sps = new SYSTEM_POWER_STATUS();
                if (Win32.GetSystemPowerStatus(sps))
                {
                    batteryPresent = (sps.BatteryFlag & 128) == 0 && sps.BatteryFlag != 255;
                    acLine = sps.ACLineStatus == 1;
                    batteryPercent = sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : -1;
                    if (batteryPercent < 0)
                    {
                        batteryPresent = false;
                    }
                }
            }
            catch {}

            string activePlanGuidStr = GetActivePowerPlanGuid();

            foreach (var plan in PowerPlans)
            {
                plan.Active = plan.Guid.Equals(activePlanGuidStr, StringComparison.OrdinalIgnoreCase);
            }

            // Generate JSON payload
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            
            // CPU (real frequency from the performance counter; base clock until the first sample arrives)
            string cpuFreqGhzStr = (cachedCpuFreqGhz > 0 ? cachedCpuFreqGhz : cpuBaseSpeedVal).ToString("F2", CultureInfo.InvariantCulture);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"cpu\":{{\"utilization\":{0},\"temp\":{1},\"name\":{2},\"freqGhz\":\"{3}\",\"baseSpeedGhz\":\"{4}\",\"cores\":{5},\"logical\":{6},\"threads\":{7},\"handles\":0,\"l2Cache\":{8},\"l3Cache\":{9}}},",
                cpuLoad, cpuTemp, JsonString(CpuInfo), cpuFreqGhzStr, CpuBaseSpeed, CpuCores, CpuLogical, threadsCount, JsonString(CpuL2Cache), JsonString(CpuL3Cache));

            // igpu
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"igpu\":{{\"detected\":{0},\"utilization\":{1},\"decodeUtil\":{2},\"usedMb\":{3},\"totalMb\":{4},\"totalGb\":{5},\"name\":{6},\"driver\":{7},\"driverDate\":{8}}},",
                igpuDetected ? "true" : "false", igpuUtil, igpuDecodeUtil, igpuMemBytes / (1024 * 1024), ramTotalPhys / 1024 / 1024 / 2, ramTotalPhys / 1024 / 1024 / 1024 / 2, JsonString(IgpuInfo), JsonString(IgpuDriver), JsonString(IgpuDriverDate));

            // npu
            long npuTotalMb = (long)(ramTotalPhys / 1024 / 1024 / 2);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"npu\":{{\"detected\":{0},\"utilization\":{1},\"usedMb\":{2},\"totalMb\":{3},\"totalGb\":{4},\"name\":{5}}},",
                NpuDetected ? "true" : "false", npuUtil, npuMemBytes / (1024 * 1024), npuTotalMb, npuTotalMb / 1024, JsonString(NpuName));

            // vram
            int vramPct = vramTotal > 0 ? (int)Math.Round((double)vramUsed / vramTotal * 100) : 0;
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"vram\":{{\"utilization\":{0},\"usedMb\":{1},\"totalMb\":{2},\"totalGb\":{3}}},",
                vramPct, vramUsed / (1024 * 1024), vramTotal / (1024 * 1024), vramTotal / 1024 / 1024 / 1024);

            // gpu
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"gpu\":{{\"detected\":{0},\"utilization\":{1},\"temp\":{2},\"coreClock\":{3},\"memoryClock\":{4},\"powerW\":{5},\"name\":{6},\"driver\":{7},\"driverDate\":{8}}},",
                dgpuDetected ? "true" : "false", gpuUtil, gpuTemp, gpuCoreClock, gpuMemClock, gpuPowerW, JsonString(GpuName), JsonString(DgpuDriver), JsonString(DgpuDriverDate));

            // ram
            string commitUsedGbStr = (commitUsedBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
            string commitLimitGbStr = (commitLimitBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
            string cachedGbStr = (ramCachedBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"ram\":{{\"utilization\":{0},\"totalGb\":{1},\"commitUsedGb\":\"{2}\",\"commitLimitGb\":\"{3}\",\"cachedGb\":\"{4}\",\"poolPagedMb\":{5},\"poolNonPagedMb\":{6},\"speedMts\":{7},\"activity\":{8},\"type\":{9},\"modules\":{10}}},",
                ramLoad, TotalRamGb, commitUsedGbStr, commitLimitGbStr, cachedGbStr, ramPoolPagedBytes / (1024 * 1024), ramPoolNonPagedBytes / (1024 * 1024), RamSpeedMts, ramActivityVal, JsonString(RamType), RamModules);

            // disk
            string diskFreeGbStr = diskFreeGb.ToString("F1", CultureInfo.InvariantCulture);
            string diskReadMbStr = diskReadMbNum.ToString("F1", CultureInfo.InvariantCulture);
            string diskWriteMbStr = diskWriteMbNum.ToString("F1", CultureInfo.InvariantCulture);
            string diskResponseMsStr = (diskResponse * 1000.0).ToString("F1", CultureInfo.InvariantCulture);
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"disk\":{{\"freeGb\":{0},\"utilization\":{1},\"storagePercent\":{2},\"totalGb\":{3},\"readMb\":\"{4}\",\"writeMb\":\"{5}\",\"responseTimeMs\":{6},\"name\":{7}}},",
                diskFreeGbStr, diskActiveTime, diskStoragePercent, (int)diskTotalGb, diskReadMbStr, diskWriteMbStr, diskResponseMsStr, JsonString(DiskName));

            // network
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"network\":{{\"lan\":{0},\"wifi\":{1},\"ip\":{2},\"ipv6\":{3},\"name\":{4},\"type\":{5},\"linkSpeedMbps\":{6},\"signal\":{7}}},",
                cachedNetTxRate, cachedNetRxRate, JsonString(cachedActiveIp), JsonString(cachedActiveIpv6), JsonString(cachedActiveNetName), JsonString(cachedNetType), cachedNetLinkSpeedMbps, wifiSignal);

            // battery
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"battery\":{{\"present\":{0},\"percent\":{1},\"ac\":{2}}},",
                batteryPresent ? "true" : "false", batteryPercent, acLine ? "true" : "false");

            // global/uptime/ping (real Windows uptime, not the process lifetime)
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"uptime\":{0},\"ping\":{1},\"totalProcesses\":{2},\"motherboard\":{3},",
                Win32.GetTickCount64() / 1000, pingTime, processesCount, JsonString(MotherboardInfo));

            // topProcesses
            string topProcessesStr;
            string topRamProcessStr;
            lock (this)
            {
                topProcessesStr = cachedTopProcessesJson;
                topRamProcessStr = cachedTopRamProcessJson;
            }
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\"topProcesses\":{0},", topProcessesStr);
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\"topRamProcess\":{0},", topRamProcessStr);

            // powerPlans
            sb.Append("\"powerPlans\":[");
            for (int i = 0; i < PowerPlans.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.AppendFormat(CultureInfo.InvariantCulture, "{{\"guid\":{0},\"name\":{1},\"active\":{2}}}",
                    JsonString(PowerPlans[i].Guid), JsonString(PowerPlans[i].Name), PowerPlans[i].Active ? "true" : "false");
            }
            sb.Append("]");

            sb.Append("}");
            return sb.ToString();
        }
    }
}
