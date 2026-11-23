using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("WingetUI")]
[assembly: AssemblyProduct("WingetUI")]
[assembly: AssemblyDescription("Package & Preset Manager for winget")]
[assembly: AssemblyCompany("projectGloria")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0")]

namespace WingetUI
{
    public class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private WebView2 webView;
        private string appDir;
        private string dataDir;        // all mutable state lives here, so the app folder stays portable
        private string presetsPath;
        private string wingetTxtPath;
        private string metaCachePath;
        private bool devTools;
        private int scanBusy = 0;      // 1 while a scan_installed run is in flight
        private int scanPending = 0;   // 1 if another scan_installed was requested while scanBusy == 1
        // Mutating winget task state, all guarded by procLock. activeTaskId is set the moment the
        // run_winget_task message arrives (before the worker thread starts), so a cancel that races
        // the process start is never lost.
        private string activeTaskId = null;
        private string cancelledTaskId = null;
        private int activePid = 0;
        private Action activeTerminate = null;
        private bool forceClose = false;
        private readonly object procLock = new object();
        private readonly string appVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        private readonly object cacheLock = new object();
        private readonly SemaphoreSlim wingetShowGate = new SemaphoreSlim(4);

        // winget exit codes that mean "nothing to do", not "failed"
        private const int WINGET_UPDATE_NOT_APPLICABLE = -1978335189;  // 0x8A15002B
        private const int WINGET_ALREADY_INSTALLED = -1978335135;      // 0x8A150061
        private JavaScriptSerializer json;
        private ConcurrentDictionary<string, Dictionary<string, string>> metaCache =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, string> fileIconCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, Dictionary<string, string>> detailsCache =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public MainForm()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 16;
            appDir = AppDomain.CurrentDomain.BaseDirectory;
            json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            devTools = Array.IndexOf(Environment.GetCommandLineArgs(), "--devtools") >= 0;
            dataDir = ResolveDataDir();
            presetsPath = Path.Combine(dataDir, "presets.json");
            wingetTxtPath = Path.Combine(dataDir, "winget.txt");
            metaCachePath = Path.Combine(dataDir, "meta-cache.json");
            SeedPresets();
            LoadMetaCache();

            this.Text = "WingetUI — Package & Preset Manager";
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(1260, 840);
            this.MinimumSize = new Size(920, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(13, 15, 20);
            this.ForeColor = Color.White;
            this.Padding = new Padding(0);

            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            EnableDarkAndShadow();

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(13, 15, 20)
            };
            this.Controls.Add(webView);

            this.Resize += (s, e) =>
            {
                SendToUi(new
                {
                    type = "window_state_changed",
                    maximized = (this.WindowState == FormWindowState.Maximized)
                });
            };

            this.Load += async (s, e) => await InitializeWebViewAsync();
            this.FormClosing += (s, e) =>
            {
                bool busy;
                lock (procLock) { busy = activeTaskId != null; }
                // Alt+F4 / taskbar close while an installer runs: aborting can leave a half-installed app.
                // (The in-app close button asks in the UI first and sets forceClose.)
                if (busy && !forceClose && e.CloseReason == CloseReason.UserClosing)
                {
                    var answer = MessageBox.Show(this,
                        "A winget task is still running. Closing WingetUI now will abort it, which can leave the app partially installed or removed.\n\nClose anyway?",
                        "WingetUI", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
                KillActiveProcess();
                SaveMetaCache();
            };
        }

        // Portable first: Data\ next to the exe. Falls back to %LOCALAPPDATA% when the folder is read-only
        // (e.g. extracted into Program Files).
        private string ResolveDataDir()
        {
            string preferred = Path.Combine(appDir, "Data");
            try
            {
                Directory.CreateDirectory(preferred);
                string probe = Path.Combine(preferred, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return preferred;
            }
            catch { }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fallback = Path.Combine(localAppData, "WingetUI");
            // Keep using the pre-rename folder if it exists so existing presets/cache aren't lost
            string legacy = Path.Combine(localAppData, "WingetBaby");
            if (!Directory.Exists(fallback) && Directory.Exists(legacy)) return legacy;
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        // The presets.json shipped beside the exe is only a seed; edits are written to Data\.
        // On first run, presets saved by an earlier install in another folder are carried over first:
        // v1.0/v1.1 shipped an Inno Setup installer (%LOCALAPPDATA%\Programs\WingetUI), and GloriaSetup
        // installs elsewhere, so without this an upgrade would silently reset the user's presets.
        private void SeedPresets()
        {
            try
            {
                if (File.Exists(presetsPath)) return;

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] legacyDataDirs =
                {
                    Path.Combine(localAppData, @"Programs\WingetUI\Data"),
                    Path.Combine(localAppData, "WingetUI"),
                    Path.Combine(localAppData, "WingetBaby")
                };
                string ownDir = Path.GetFullPath(dataDir).TrimEnd('\\');
                foreach (string dir in legacyDataDirs)
                {
                    if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), ownDir, StringComparison.OrdinalIgnoreCase)) continue;
                    string legacyPresets = Path.Combine(dir, "presets.json");
                    if (!File.Exists(legacyPresets)) continue;
                    File.Copy(legacyPresets, presetsPath);
                    string legacyCache = Path.Combine(dir, "meta-cache.json");
                    if (File.Exists(legacyCache) && !File.Exists(metaCachePath)) File.Copy(legacyCache, metaCachePath);
                    return;
                }

                string seed = Path.Combine(appDir, "presets.json");
                if (File.Exists(seed)) File.Copy(seed, presetsPath);
            }
            catch { }
        }

        private void LoadMetaCache()
        {
            try
            {
                if (!File.Exists(metaCachePath)) return;
                var data = json.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                    File.ReadAllText(metaCachePath, Encoding.UTF8));
                if (data == null) return;
                DateTime cutoff = DateTime.Now.AddDays(-7);
                foreach (var kv in data)
                {
                    string ts;
                    DateTime t;
                    if (kv.Value != null && kv.Value.TryGetValue("ts", out ts) &&
                        DateTime.TryParse(ts, out t) && t >= cutoff)
                    {
                        metaCache[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private void SaveMetaCache()
        {
            try
            {
                lock (cacheLock)
                {
                    var snapshot = new Dictionary<string, Dictionary<string, string>>(metaCache);
                    string tmp = metaCachePath + ".tmp";
                    File.WriteAllText(tmp, json.Serialize(snapshot), Encoding.UTF8);
                    if (File.Exists(metaCachePath)) File.Delete(metaCachePath);
                    File.Move(tmp, metaCachePath);
                }
            }
            catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x20000;  // WS_MINIMIZEBOX (allows taskbar minimize)
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x0083;
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }

            const int WM_GETMINMAXINFO = 0x0024;
            if (m.Msg == WM_GETMINMAXINFO)
            {
                Screen screen = Screen.FromHandle(this.Handle);
                Rectangle workArea = screen.WorkingArea;
                Rectangle monitorArea = screen.Bounds;

                MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                mmi.ptMaxPosition.x = Math.Abs(workArea.Left - monitorArea.Left);
                mmi.ptMaxPosition.y = Math.Abs(workArea.Top - monitorArea.Top);
                mmi.ptMaxSize.x = workArea.Width;
                mmi.ptMaxSize.y = workArea.Height;
                Marshal.StructureToPtr(mmi, m.LParam, true);
            }
            base.WndProc(ref m);
        }

        private void EnableDarkAndShadow()
        {
            try
            {
                int useDarkMode = 1;
                // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 11 / 10 20H1+)
                if (DwmSetWindowAttribute(this.Handle, 20, ref useDarkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(this.Handle, 19, ref useDarkMode, sizeof(int));
                }

                // DWMWA_WINDOW_CORNER_PREFERENCE = 33, 2 = DWMWCP_ROUND (Windows 11 rounded corners)
                int cornerPreference = 2;
                DwmSetWindowAttribute(this.Handle, 33, ref cornerPreference, sizeof(int));

                // DWMWA_BORDER_COLOR = 34, 0xFFFFFFFE = DWMWA_COLOR_NONE (Disable window border)
                int noBorder = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(this.Handle, 34, ref noBorder, sizeof(int));
            }
            catch { }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(dataDir, "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                // F12 / Ctrl+R / etc. only with --devtools; a refresh would nuke in-memory queue state
                webView.CoreWebView2.Settings.AreDevToolsEnabled = devTools;
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = devTools;

                string uiFolder = Path.Combine(appDir, "ui");
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "winget.app",
                    uiFolder,
                    CoreWebView2HostResourceAccessKind.Allow
                );

                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    SendToUi(new
                    {
                        type = "window_state_changed",
                        maximized = (this.WindowState == FormWindowState.Maximized)
                    });
                };
                webView.CoreWebView2.Navigate("https://winget.app/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to initialize WebView2 runtime:\n" + ex.Message,
                    "WingetUI Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private void SendToUi(object payload)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                try
                {
                    this.BeginInvoke(new Action(() => SendToUi(payload)));
                }
                catch { }
                return;
            }
            try
            {
                if (webView == null || webView.CoreWebView2 == null) return;
                string serialized = json.Serialize(payload);
                webView.CoreWebView2.PostWebMessageAsJson(serialized);
            }
            catch { }
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.WebMessageAsJson;
                var msg = json.Deserialize<Dictionary<string, object>>(raw);
                if (msg == null || !msg.ContainsKey("action")) return;

                string action = Convert.ToString(msg["action"]);

                switch (action)
                {
                    case "get_presets":
                        HandleGetPresets();
                        break;

                    case "save_presets":
                        if (msg.ContainsKey("data"))
                        {
                            // Write-then-swap so a crash mid-write can't leave a truncated presets file
                            string formatted = json.Serialize(msg["data"]);
                            string tmpPresets = presetsPath + ".tmp";
                            File.WriteAllText(tmpPresets, formatted, Encoding.UTF8);
                            if (File.Exists(presetsPath)) File.Replace(tmpPresets, presetsPath, null);
                            else File.Move(tmpPresets, presetsPath);
                            SendToUi(new { type = "presets_saved", success = true });
                        }
                        break;

                    case "export_winget_txt":
                        if (msg.ContainsKey("markdown"))
                        {
                            string md = Convert.ToString(msg["markdown"]);
                            File.WriteAllText(wingetTxtPath, md, Encoding.UTF8);
                            SendToUi(new { type = "exported_winget_txt", path = wingetTxtPath, success = true });
                        }
                        break;

                    case "scan_installed":
                        bool startup = msg.ContainsKey("startup") && Convert.ToBoolean(msg["startup"]);
                        await Task.Run(() => HandleScanInstalled(startup));
                        break;

                    case "check_updates":
                        await Task.Run(() => HandleCheckUpdates());
                        break;

                    case "search_winget":
                        string query = msg.ContainsKey("query") ? Convert.ToString(msg["query"]) : "";
                        int searchSeq = msg.ContainsKey("seq") ? Convert.ToInt32(msg["seq"]) : 0;
                        await Task.Run(() => HandleSearchWinget(query, searchSeq));
                        break;

                    case "run_winget_task":
                        string taskId = msg.ContainsKey("taskId") ? Convert.ToString(msg["taskId"]) : "";
                        string operation = msg.ContainsKey("operation") ? Convert.ToString(msg["operation"]) : "install";
                        string packageId = msg.ContainsKey("packageId") ? Convert.ToString(msg["packageId"]) : "";
                        string packageName = msg.ContainsKey("packageName") ? Convert.ToString(msg["packageName"]) : packageId;
                        string fromVersion = msg.ContainsKey("fromVersion") ? Convert.ToString(msg["fromVersion"]) : "";
                        bool silent = !msg.ContainsKey("silent") || Convert.ToBoolean(msg["silent"]);
                        lock (procLock)
                        {
                            if (activeTaskId != null)
                            {
                                // The UI queue runs one task at a time; refuse rather than run two installers at once
                                SendToUi(new { type = "task_done", taskId = taskId, operation = operation, packageId = packageId,
                                    packageName = packageName, exitCode = -1, success = false, skipped = false,
                                    verify = "none", message = "Another winget task is still running." });
                                break;
                            }
                            activeTaskId = taskId;
                            cancelledTaskId = null;
                        }
                        await Task.Run(() => HandleRunWingetTask(taskId, operation, packageId, packageName, silent, fromVersion));
                        break;

                    case "cancel_task":
                        string cancelId = msg.ContainsKey("taskId") ? Convert.ToString(msg["taskId"]) : "";
                        // task_cancelled is sent by the worker once the process is really gone
                        await Task.Run(() => CancelTask(cancelId));
                        break;

                    case "open_file":
                        string pathToOpen = msg.ContainsKey("path") ? Convert.ToString(msg["path"]) : appDir;
                        if (File.Exists(pathToOpen) || Directory.Exists(pathToOpen))
                        {
                            Process.Start("explorer.exe", "/select,\"" + pathToOpen + "\"");
                        }
                        break;

                    case "open_url":
                        string url = msg.ContainsKey("url") ? Convert.ToString(msg["url"]) : "";
                        Uri parsedUrl;
                        if (Uri.TryCreate(url, UriKind.Absolute, out parsedUrl) &&
                            (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps))
                        {
                            Process.Start(parsedUrl.AbsoluteUri);
                        }
                        break;

                    case "get_package_details":
                        string detailId = msg.ContainsKey("id") ? Convert.ToString(msg["id"]) : "";
                        string detailName = msg.ContainsKey("name") ? Convert.ToString(msg["name"]) : "";
                        string detailSource = msg.ContainsKey("source") ? Convert.ToString(msg["source"]) : "";
                        await Task.Run(() => HandleGetPackageDetails(detailId, detailName, detailSource));
                        break;

                    case "window_drag":
                        if (this.WindowState == FormWindowState.Normal)
                        {
                            ReleaseCapture();
                            SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                        }
                        break;

                    case "window_minimize":
                        this.WindowState = FormWindowState.Minimized;
                        break;

                    case "window_maximize":
                        this.WindowState = (this.WindowState == FormWindowState.Maximized)
                            ? FormWindowState.Normal
                            : FormWindowState.Maximized;
                        break;

                    case "window_close":
                        // The UI already asked the user if a task is running
                        forceClose = msg.ContainsKey("force") && Convert.ToBoolean(msg["force"]);
                        this.Close();
                        forceClose = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                SendToUi(new { type = "error", message = ex.Message });
            }
        }

        private void HandleGetPresets()
        {
            if (File.Exists(presetsPath))
            {
                string content = File.ReadAllText(presetsPath, Encoding.UTF8);
                var data = json.DeserializeObject(content);
                SendToUi(new
                {
                    type = "presets_loaded",
                    data = data,
                    wingetTxtPath = wingetTxtPath,
                    appVersion = appVersion
                });
            }
            else
            {
                SendToUi(new
                {
                    type = "presets_loaded",
                    data = new { categories = new object[0] },
                    wingetTxtPath = wingetTxtPath,
                    appVersion = appVersion
                });
            }
        }

        private void SendScanProgress(string phase, int done, int total, string id, string name)
        {
            SendToUi(new
            {
                type = "scan_progress",
                phase = phase,
                done = done,
                total = total,
                id = id ?? "",
                name = name ?? ""
            });
        }

        private void HandleScanInstalled(bool startup)
        {
            // Coalesce concurrent scan requests so a post-task refresh is never silently dropped
            if (Interlocked.CompareExchange(ref scanBusy, 1, 0) != 0)
            {
                Interlocked.Exchange(ref scanPending, 1);
                return;
            }
            string error = "";
            try
            {
                do
                {
                    Interlocked.Exchange(ref scanPending, 0);
                    detailsCache.Clear();
                    error = "";
                    SendToUi(new { type = "scan_status", status = "scanning" });
                    SendScanProgress("Querying Windows Package Manager", 0, 0, "", "");
                    string output = RunWingetCapture("list --accept-source-agreements --disable-interactivity");
                    var packages = ParseWingetTable(output);

                    SendScanProgress("Reading installed applications", 0, packages.Count, "", "");
                    AttachLocalRegistryIcons(packages);

                    SendToUi(new
                    {
                        type = "installed_list",
                        packages = packages,
                        timestamp = DateTime.Now.ToString("HH:mm:ss")
                    });

                    if (packages.Count == 0)
                    {
                        error = (output ?? "").StartsWith("Error:") ? output : "winget returned no installed packages.";
                    }

                    // Updatable packages first: they need release dates, so they are the slow ones
                    var ordered = new List<Dictionary<string, string>>();
                    foreach (var p in packages)
                    {
                        if (p.ContainsKey("available") && !string.IsNullOrWhiteSpace(p["available"])) ordered.Add(p);
                    }
                    foreach (var p in packages)
                    {
                        if (!(p.ContainsKey("available") && !string.IsNullOrWhiteSpace(p["available"]))) ordered.Add(p);
                    }

                    int total = ordered.Count;
                    int done = 0;
                    Action<Dictionary<string, string>> onItem = p =>
                    {
                        int n = Interlocked.Increment(ref done);
                        SendScanProgress(
                            "Resolving package details",
                            n,
                            total,
                            p.ContainsKey("id") ? p["id"] : "",
                            p.ContainsKey("name") ? p["name"] : ""
                        );
                    };

                    var work = Task.Run(() => ResolvePackageMetadata(ordered, onItem));
                    work.ContinueWith(t => SaveMetaCache());

                    // Splash only waits a bounded time; stragglers keep filling in behind the live UI
                    if (startup && ordered.Count > 0) work.Wait(20000);
                    startup = false;
                }
                while (Interlocked.CompareExchange(ref scanPending, 0, 1) == 1);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref scanBusy, 0);
                SendToUi(new { type = "scan_complete", error = error });
            }
        }

        private void HandleCheckUpdates()
        {
            SendToUi(new { type = "updates_status", status = "checking" });
            string output = RunWingetCapture("upgrade --include-unknown --accept-source-agreements --disable-interactivity");
            var updates = ParseWingetTable(output);
            AttachLocalRegistryIcons(updates);

            SendToUi(new
            {
                type = "updates_list",
                updates = updates,
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            if (updates.Count > 0)
            {
                ResolvePackageMetadata(updates, null);
                SaveMetaCache();
            }
        }

        private void AttachLocalRegistryIcons(List<Dictionary<string, string>> packages)
        {
            if (packages == null || packages.Count == 0) return;
            try
            {
                var regByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var regByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ScanRegistryUninstallHive(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", regByName, regByKey);
                ScanRegistryUninstallHive(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", regByName, regByKey);
                ScanRegistryUninstallHive(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", regByName, regByKey);

                foreach (var pkg in packages)
                {
                    string name = pkg.ContainsKey("name") ? pkg["name"] : "";
                    string id = pkg.ContainsKey("id") ? pkg["id"] : "";
                    string iconPath = null;

                    if (!string.IsNullOrEmpty(name) && regByName.TryGetValue(NormalizeAppName(name), out iconPath))
                    {
                        // Direct normalized DisplayName match
                    }
                    else if (!string.IsNullOrEmpty(id))
                    {
                        string lastSegment = id.Contains("\\") ? id.Substring(id.LastIndexOf('\\') + 1) : id;
                        if (!regByKey.TryGetValue(lastSegment, out iconPath))
                        {
                            string pkgPart = id.Contains(".") ? id.Substring(id.IndexOf('.') + 1) : id;
                            regByName.TryGetValue(NormalizeAppName(pkgPart), out iconPath);
                        }
                    }

                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        string dataUri = ExtractIconAsDataUri(iconPath);
                        if (!string.IsNullOrEmpty(dataUri))
                        {
                            pkg["localIcon"] = dataUri;
                        }
                    }
                }
            }
            catch { }
        }

        private void ScanRegistryUninstallHive(
            RegistryKey root,
            string subKeyPath,
            Dictionary<string, string> byName,
            Dictionary<string, string> byKey)
        {
            try
            {
                using (var baseKey = root.OpenSubKey(subKeyPath))
                {
                    if (baseKey == null) return;
                    foreach (string subName in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (var sub = baseKey.OpenSubKey(subName))
                            {
                                if (sub == null) continue;
                                string displayName = Convert.ToString(sub.GetValue("DisplayName") ?? "").Trim();
                                string displayIcon = Convert.ToString(sub.GetValue("DisplayIcon") ?? "").Trim();
                                if (string.IsNullOrEmpty(displayIcon)) continue;

                                string cleanPath = CleanIconFilePath(displayIcon);
                                if (string.IsNullOrEmpty(cleanPath) || !File.Exists(cleanPath)) continue;

                                byKey[subName] = cleanPath;
                                if (!string.IsNullOrEmpty(displayName))
                                {
                                    string norm = NormalizeAppName(displayName);
                                    if (!string.IsNullOrEmpty(norm) && !byName.ContainsKey(norm))
                                    {
                                        byName[norm] = cleanPath;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private string CleanIconFilePath(string rawIcon)
        {
            if (string.IsNullOrWhiteSpace(rawIcon)) return "";
            string s = rawIcon.Trim().Trim('"');
            int commaIdx = s.LastIndexOf(',');
            if (commaIdx > 0)
            {
                string beforeComma = s.Substring(0, commaIdx).Trim().Trim('"');
                if (File.Exists(beforeComma)) return beforeComma;
            }
            Match m = Regex.Match(s, @"^([a-zA-Z]:\\[^""]+?\.(?:exe|ico))", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            return s;
        }

        private string NormalizeAppName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            // Strip version suffixes or architecture tags like (x64), 64-bit, v1.2.3
            string cleaned = Regex.Replace(name, @"\([^)]*\)", " ");
            cleaned = Regex.Replace(cleaned, @"\b(x64|x86|64-bit|32-bit|en-US)\b", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\b\d+(\.\d+)+[a-z0-9-]*\b", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"[^a-zA-Z0-9]+", "").ToLowerInvariant();
            return cleaned;
        }

        private string ExtractIconAsDataUri(string filePath)
        {
            string cached;
            if (fileIconCache.TryGetValue(filePath, out cached)) return cached;

            try
            {
                using (Icon ico = Icon.ExtractAssociatedIcon(filePath))
                {
                    if (ico == null) return "";
                    using (Bitmap bmp = ico.ToBitmap())
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        string b64 = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                        fileIconCache[filePath] = b64;
                        return b64;
                    }
                }
            }
            catch
            {
                fileIconCache[filePath] = "";
                return "";
            }
        }

        // A package with an "available" version is an update candidate and gets a release-date lookup;
        // everything else only needs icon/homepage data. onItem fires once per package (even skipped
        // ones) so the splash progress bar always reaches 100%.
        private void ResolvePackageMetadata(
            List<Dictionary<string, string>> packages,
            Action<Dictionary<string, string>> onItem)
        {
            Parallel.ForEach(
                packages,
                new ParallelOptions { MaxDegreeOfParallelism = 12 },
                pkg =>
                {
                    try { ResolveOnePackage(pkg); }
                    catch { }
                    finally
                    {
                        if (onItem != null) onItem(pkg);
                    }
                }
            );
        }

        private void ResolveOnePackage(Dictionary<string, string> pkg)
        {
            if (!pkg.ContainsKey("id")) return;
            string id = pkg["id"];
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith("MSIX\\") || id.StartsWith("ARP\\")) return;

            bool needReleaseDate = pkg.ContainsKey("available") && !string.IsNullOrWhiteSpace(pkg["available"]);
            string version = "";
            if (needReleaseDate)
            {
                version = pkg["available"];
            }
            else if (pkg.ContainsKey("version"))
            {
                version = pkg["version"];
            }

            Dictionary<string, string> meta;
            string cachedVer;
            // Re-fetch when a newer version appeared since the cached release date was recorded
            string source = pkg.ContainsKey("source") ? pkg["source"] : "";
            bool shouldFetch = !metaCache.TryGetValue(id, out meta) ||
                               !meta.ContainsKey("trust") ||   // entries cached before trust scoring existed
                               (needReleaseDate &&
                                (!meta.ContainsKey("releaseDateChecked") || meta["releaseDateChecked"] != "1" ||
                                 !meta.TryGetValue("ver", out cachedVer) || cachedVer != version));

            if (shouldFetch)
            {
                meta = FetchSinglePackageMeta(id, version, needReleaseDate, source);
                metaCache[id] = meta;
            }

            SendToUi(new
            {
                type = "package_metadata",
                id = id,
                trusted = meta.ContainsKey("trust") && meta["trust"] == "1",
                releaseDate = meta.ContainsKey("releaseDate") ? meta["releaseDate"] : "",
                releaseNotesUrl = meta.ContainsKey("releaseNotesUrl") ? meta["releaseNotesUrl"] : "",
                homepage = meta.ContainsKey("homepage") ? meta["homepage"] : "",
                iconUrl = meta.ContainsKey("iconUrl") ? meta["iconUrl"] : "",
                fallbackIconUrl = meta.ContainsKey("fallbackIconUrl") ? meta["fallbackIconUrl"] : ""
            });
        }

        private static bool IsStoreId(string id, string source)
        {
            if (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrEmpty(id) && Regex.IsMatch(id, @"^[0-9A-Za-z]{12}$") && !id.Contains(".");
        }

        // Microsoft Store catalog: the only place a Store-only app (no winget manifest, no homepage) has a real icon.
        private Dictionary<string, string> FetchStoreMeta(string id, string version)
        {
            var meta = new Dictionary<string, string>();
            string icon = "";
            string homepage = "";
            string body = FetchUrlString(
                "https://storeedgefd.dsx.mp.microsoft.com/v9.0/products/" + Uri.EscapeDataString(id) +
                "?market=US&locale=en-US&deviceFamily=Windows.Desktop", 6000);
            try
            {
                if (!string.IsNullOrEmpty(body))
                {
                    var root = json.DeserializeObject(body) as Dictionary<string, object>;
                    var payload = root != null && root.ContainsKey("Payload") ? root["Payload"] as Dictionary<string, object> : null;
                    if (payload != null)
                    {
                        if (payload.ContainsKey("AppWebsiteUrl")) homepage = Convert.ToString(payload["AppWebsiteUrl"]);
                        var images = payload.ContainsKey("Images") ? payload["Images"] as System.Collections.IEnumerable : null;
                        int bestWidth = -1;
                        if (images != null)
                        {
                            foreach (object o in images)
                            {
                                var img = o as Dictionary<string, object>;
                                if (img == null || !img.ContainsKey("Url")) continue;
                                string type = Convert.ToString(img["ImageType"]);
                                if (!string.Equals(type, "logo", StringComparison.OrdinalIgnoreCase)) continue;
                                int w = img.ContainsKey("Width") ? Convert.ToInt32(img["Width"]) : 0;
                                if (w > bestWidth)
                                {
                                    bestWidth = w;
                                    icon = Convert.ToString(img["Url"]);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            meta["releaseDate"] = "";
            meta["releaseDateChecked"] = "0";
            meta["releaseNotesUrl"] = "";
            meta["homepage"] = homepage ?? "";
            meta["iconUrl"] = icon;
            meta["fallbackIconUrl"] = "";
            meta["ver"] = version ?? "";
            meta["trust"] = "1";  // Store apps are certified and signed by Microsoft
            meta["ts"] = (string.IsNullOrEmpty(icon) ? DateTime.Now.AddDays(-6) : DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss");
            return meta;
        }

        // Publisher-prefix -> extra registrable domain labels the same publisher is known to use
        private static readonly Dictionary<string, string[]> PublisherDomainAliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "microsoft", new[] { "visualstudio", "windows", "azure", "office", "xbox", "msft", "aka" } },
                { "google", new[] { "chrome", "android", "googleapis", "withgoogle" } },
                { "openjs", new[] { "nodejs" } },
                { "git", new[] { "gitscm" } },
                { "notepad", new[] { "notepadplusplus" } },
                { "valve", new[] { "steampowered" } },
                { "mozilla", new[] { "firefox" } },
                { "videolan", new[] { "vlc" } }
            };

        private static string NormToken(string s)
        {
            return Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
        }

        // Second-level label of the registrable domain: download.brave.com -> "brave", foo.co.uk -> "foo".
        private static string RegistrableLabel(string host)
        {
            string[] parts = host.Split('.');
            if (parts.Length < 2) return "";
            int idx = parts.Length - 2;
            string[] sld = { "co", "com", "org", "net", "gov", "ac", "edu" };
            if (parts.Length >= 3 && parts[parts.Length - 1].Length == 2 && Array.IndexOf(sld, parts[parts.Length - 2]) >= 0)
            {
                idx = parts.Length - 3;
            }
            return NormToken(parts[idx]);
        }

        // Heuristic "trustworthy publisher" check. winget-pkgs is community-submitted, so being in it
        // proves nothing; instead require the package's publisher (ID prefix) to match the identity behind
        // its homepage / publisher / installer URLs (own domain, or GitHub org of the same name).
        // Store apps are always trusted (Microsoft-certified). Not cryptographic verification.
        private bool ComputeTrust(string id, string source, params string[] urls)
        {
            if (IsStoreId(id, source)) return true;
            if (string.IsNullOrEmpty(id) || !id.Contains(".")) return false;

            string pub = NormToken(id.Substring(0, id.IndexOf('.')));
            if (pub.Length < 2) return false;

            // GitHub's own packages point at github.com, which the shared-host filter below would skip
            if (pub == "github")
            {
                foreach (string u in urls)
                {
                    Uri gu;
                    if (Uri.TryCreate((u ?? "").Trim(), UriKind.Absolute, out gu) &&
                        (gu.Host == "github.com" || gu.Host == "www.github.com") &&
                        gu.AbsolutePath.StartsWith("/features")) return true;
                    if (Uri.TryCreate((u ?? "").Trim(), UriKind.Absolute, out gu) &&
                        (gu.Host == "desktop.github.com" || gu.Host == "cli.github.com" || gu.AbsolutePath.StartsWith("/apps/desktop"))) return true;
                }
            }

            var accepted = new List<string> { pub };
            string[] aliases;
            if (PublisherDomainAliases.TryGetValue(pub, out aliases)) accepted.AddRange(aliases);

            foreach (string url in urls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;

                string owner = TryExtractGitHubOwner(url);
                if (!string.IsNullOrEmpty(owner) && accepted.Contains(NormToken(owner))) return true;

                // TryExtractCustomDomain returns "" for github/gitlab/*.github.io etc., so shared hosts never match here
                string domain = TryExtractCustomDomain(url);
                if (!string.IsNullOrEmpty(domain) && accepted.Contains(RegistrableLabel(domain))) return true;
            }
            return false;
        }

        private Dictionary<string, string> FetchSinglePackageMeta(string id, string version, bool needReleaseDate, string source)
        {
            if (IsStoreId(id, source)) return FetchStoreMeta(id, version);

            var meta = new Dictionary<string, string>();
            string releaseDate = "";
            string installerUrl = "";
            string releaseNotesUrl = "";
            string packageUrl = "";
            string publisherUrl = "";
            string rawIconUrl = "";

            // 1. Fast path: Query winget-pkgs manifest on GitHub directly via HTTPS (no child process overhead)
            string cleanVer = (version ?? "").Trim();
            if (cleanVer.StartsWith("<")) cleanVer = cleanVer.Substring(1).Trim();
            if (id.Contains(".") && !string.IsNullOrEmpty(cleanVer) && !cleanVer.Contains(" ") &&
                !cleanVer.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                string firstChar = id.Substring(0, 1).ToLowerInvariant();
                string idPath = id.Replace('.', '/');
                string baseUrl = string.Format(
                    "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/{0}/{1}/{2}/{3}",
                    firstChar, idPath, cleanVer, id
                );

                string localeYaml = FetchUrlString(baseUrl + ".locale.en-US.yaml", 3000);
                if (string.IsNullOrEmpty(localeYaml))
                {
                    localeYaml = FetchUrlString(baseUrl + ".yaml", 3000);
                }

                if (!string.IsNullOrEmpty(localeYaml))
                {
                    ParseManifestYamlFields(
                        localeYaml,
                        ref releaseDate,
                        ref releaseNotesUrl,
                        ref packageUrl,
                        ref publisherUrl,
                        ref rawIconUrl,
                        ref installerUrl
                    );
                }

                if (needReleaseDate || (string.IsNullOrEmpty(packageUrl) && string.IsNullOrEmpty(publisherUrl) && string.IsNullOrEmpty(rawIconUrl)))
                {
                    string instYaml = FetchUrlString(baseUrl + ".installer.yaml", 3000);
                    if (!string.IsNullOrEmpty(instYaml))
                    {
                        ParseManifestYamlFields(
                            instYaml,
                            ref releaseDate,
                            ref releaseNotesUrl,
                            ref packageUrl,
                            ref publisherUrl,
                            ref rawIconUrl,
                            ref installerUrl
                        );
                    }
                }
            }

            // 2. Fallback to `winget show` if we need release date for an update and GitHub manifest didn't have it
            // Also needed when the version-specific manifest 404'd (installed version not in winget-pkgs):
            // without any URL we can neither score trust nor derive an icon.
            bool noUrls = string.IsNullOrEmpty(packageUrl) && string.IsNullOrEmpty(publisherUrl) &&
                          string.IsNullOrEmpty(installerUrl) && string.IsNullOrEmpty(rawIconUrl);
            if ((needReleaseDate && string.IsNullOrEmpty(releaseDate)) || noUrls)
            {
                string safeId = SanitizeWingetArg(id);
                string output;
                wingetShowGate.Wait();  // winget show is heavy; cap concurrent runs
                try
                {
                    output = RunWingetCapture("show -e --id \"" + safeId + "\" --accept-source-agreements --disable-interactivity", 30000);
                }
                finally
                {
                    wingetShowGate.Release();
                }
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("Release Date:", StringComparison.OrdinalIgnoreCase))
                    {
                        releaseDate = line.Substring("Release Date:".Length).Trim();
                    }
                    else if (line.StartsWith("Release Notes Url:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(releaseNotesUrl))
                    {
                        releaseNotesUrl = line.Substring("Release Notes Url:".Length).Trim();
                    }
                    else if (line.StartsWith("Homepage:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(packageUrl))
                    {
                        packageUrl = line.Substring("Homepage:".Length).Trim();
                    }
                    else if (line.StartsWith("Publisher Url:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(publisherUrl))
                    {
                        publisherUrl = line.Substring("Publisher Url:".Length).Trim();
                    }
                    else if (line.StartsWith("Installer Url:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(installerUrl))
                    {
                        string val = line.Substring("Installer Url:".Length).Trim();
                        if (string.IsNullOrEmpty(val) && i + 1 < lines.Length)
                        {
                            val = lines[i + 1].Trim();
                        }
                        installerUrl = val;
                    }
                }

                // Fallback: HTTP HEAD Last-Modified on Installer Url
                if (needReleaseDate && string.IsNullOrEmpty(releaseDate) && !string.IsNullOrEmpty(installerUrl) && installerUrl.StartsWith("http"))
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(installerUrl);
                        req.Method = "HEAD";
                        req.Timeout = 4000;
                        req.AllowAutoRedirect = true;
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            string lastMod = resp.Headers["Last-Modified"];
                            DateTime dt;
                            if (!string.IsNullOrEmpty(lastMod) && DateTime.TryParse(lastMod, out dt))
                            {
                                releaseDate = dt.ToString("yyyy-MM-dd");
                            }
                        }
                    }
                    catch { }
                }
            }

            // 3. Derive high-res iconUrl & fallbackIconUrl from discovered URLs
            string githubOwner = TryExtractGitHubOwner(packageUrl);
            if (string.IsNullOrEmpty(githubOwner)) githubOwner = TryExtractGitHubOwner(publisherUrl);
            if (string.IsNullOrEmpty(githubOwner)) githubOwner = TryExtractGitHubOwner(installerUrl);
            string githubAvatar = !string.IsNullOrEmpty(githubOwner)
                ? "https://github.com/" + githubOwner + ".png?size=128"
                : "";

            string customDomain = TryExtractCustomDomain(packageUrl);
            if (string.IsNullOrEmpty(customDomain)) customDomain = TryExtractCustomDomain(publisherUrl);
            string domainFavicon = !string.IsNullOrEmpty(customDomain)
                ? "https://www.google.com/s2/favicons?sz=128&domain_url=" + Uri.EscapeDataString(customDomain)
                : "";

            string primaryIcon = "";
            string fallbackIcon = "";

            if (!string.IsNullOrEmpty(rawIconUrl) && rawIconUrl.StartsWith("http"))
            {
                primaryIcon = rawIconUrl;
                fallbackIcon = !string.IsNullOrEmpty(domainFavicon) ? domainFavicon : githubAvatar;
            }
            else if (!string.IsNullOrEmpty(domainFavicon))
            {
                primaryIcon = domainFavicon;
                fallbackIcon = githubAvatar;
            }
            else if (!string.IsNullOrEmpty(githubAvatar))
            {
                primaryIcon = githubAvatar;
            }

            meta["releaseDate"] = releaseDate;
            meta["releaseDateChecked"] = needReleaseDate ? "1" : "0";
            meta["releaseNotesUrl"] = releaseNotesUrl;
            meta["homepage"] = !string.IsNullOrEmpty(packageUrl) ? packageUrl : publisherUrl;
            meta["iconUrl"] = primaryIcon;
            meta["fallbackIconUrl"] = fallbackIcon;
            meta["ver"] = version ?? "";
            meta["trust"] = ComputeTrust(id, source, packageUrl, publisherUrl, installerUrl) ? "1" : "0";

            // Empty results are probably a 404 or being offline: back-date the timestamp so the
            // 7-day cache TTL expires them after ~1 day instead of poisoning the cache for a week.
            bool hasData = !string.IsNullOrEmpty(primaryIcon) || !string.IsNullOrEmpty(releaseDate) ||
                           !string.IsNullOrEmpty(meta["homepage"]);
            meta["ts"] = (hasData ? DateTime.Now : DateTime.Now.AddDays(-6)).ToString("yyyy-MM-dd HH:mm:ss");
            return meta;
        }

        private string FetchUrlString(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "WingetUI/" + appVersion;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return "";
            }
        }

        private void ParseManifestYamlFields(
            string yaml,
            ref string releaseDate,
            ref string releaseNotesUrl,
            ref string packageUrl,
            ref string publisherUrl,
            ref string rawIconUrl,
            ref string installerUrl)
        {
            string[] lines = yaml.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("#")) continue;

                if (line.StartsWith("PackageUrl:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(packageUrl))
                {
                    packageUrl = UnquoteYaml(line.Substring("PackageUrl:".Length));
                }
                else if (line.StartsWith("PublisherUrl:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(publisherUrl))
                {
                    publisherUrl = UnquoteYaml(line.Substring("PublisherUrl:".Length));
                }
                else if (line.StartsWith("IconUrl:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(rawIconUrl))
                {
                    rawIconUrl = UnquoteYaml(line.Substring("IconUrl:".Length));
                }
                else if (line.StartsWith("ReleaseDate:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(releaseDate))
                {
                    releaseDate = UnquoteYaml(line.Substring("ReleaseDate:".Length));
                }
                else if (line.StartsWith("ReleaseNotesUrl:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(releaseNotesUrl))
                {
                    releaseNotesUrl = UnquoteYaml(line.Substring("ReleaseNotesUrl:".Length));
                }
                else if (line.StartsWith("InstallerUrl:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(installerUrl))
                {
                    installerUrl = UnquoteYaml(line.Substring("InstallerUrl:".Length));
                }
            }
        }

        private static string SanitizeWingetArg(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            string s = val.Replace("\"", "").Replace("'", "").Replace("\r", "").Replace("\n", "").Trim();
            // Trailing backslashes escape the closing quote in CommandLineToArgvW
            return s.TrimEnd('\\');
        }

        private string UnquoteYaml(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return "";
            string s = val.Trim();
            if (!s.StartsWith("'") && !s.StartsWith("\""))
            {
                int commentIdx = s.IndexOf(" #", StringComparison.Ordinal);
                if (commentIdx >= 0) s = s.Substring(0, commentIdx).Trim();
            }
            return s.Trim('\'', '"');
        }

        private string TryExtractGitHubOwner(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                Uri u;
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return "";
                string host = u.Host.ToLowerInvariant();

                if (host == "github.com" || host == "www.github.com" || host == "raw.githubusercontent.com")
                {
                    string[] segs = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (segs.Length >= 1)
                    {
                        string owner = segs[0];
                        if (!owner.Equals("orgs", StringComparison.OrdinalIgnoreCase) &&
                            !owner.Equals("users", StringComparison.OrdinalIgnoreCase) &&
                            !owner.Equals("topics", StringComparison.OrdinalIgnoreCase) &&
                            !owner.Equals("features", StringComparison.OrdinalIgnoreCase) &&
                            !owner.Equals("marketplace", StringComparison.OrdinalIgnoreCase) &&
                            !owner.Equals("apps", StringComparison.OrdinalIgnoreCase))
                        {
                            return owner;
                        }
                    }
                }
                else if (host.EndsWith(".github.io") && host.Length > ".github.io".Length)
                {
                    string owner = host.Substring(0, host.Length - ".github.io".Length);
                    if (!owner.Contains(".")) return owner;
                }
            }
            catch { }
            return "";
        }

        private string TryExtractCustomDomain(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                Uri u;
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return "";
                string host = u.Host.ToLowerInvariant();
                if (host.StartsWith("www.")) host = host.Substring(4);

                string[] genericHosts = new[]
                {
                    "github.com", "raw.githubusercontent.com", "gist.github.com", "objects.githubusercontent.com",
                    "gitlab.com", "bitbucket.org", "sourceforge.net", "codeberg.org", "sr.ht", "git.sr.ht",
                    "pypi.org", "npmjs.com", "crates.io", "aka.ms", "ko-fi.com", "patreon.com", "buymeacoffee.com"
                };
                foreach (string g in genericHosts)
                {
                    if (host == g || host.EndsWith("." + g)) return "";
                }

                string[] genericSuffixes = new[]
                {
                    ".github.io", ".gitlab.io", ".sourceforge.io", ".vercel.app",
                    ".netlify.app", ".pages.dev", ".herokuapp.com", ".readthedocs.io"
                };
                foreach (string suf in genericSuffixes)
                {
                    if (host.EndsWith(suf)) return "";
                }

                return host;
            }
            catch
            {
                return "";
            }
        }

        private void HandleSearchWinget(string query, int seq)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                SendToUi(new { type = "search_results", results = new object[0], query = "", seq = seq });
                return;
            }
            string safeQuery = SanitizeWingetArg(query);
            string output = RunWingetCapture("search \"" + safeQuery + "\" -n 40 --accept-source-agreements --disable-interactivity");
            var results = ParseWingetTable(output);
            SendToUi(new
            {
                type = "search_results",
                results = results,
                query = query,
                seq = seq
            });

            if (results.Count > 0)
            {
                ResolvePackageMetadata(results, null);
            }
            // Trust scores are in; the UI can now sort/filter without rows jumping around mid-load
            SendToUi(new { type = "search_complete", query = query, seq = seq });
        }

        private void HandleGetPackageDetails(string id, string fallbackName, string fallbackSource)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            string cleanId = id.Trim();

            Dictionary<string, string> details;
            if (!detailsCache.TryGetValue(cleanId, out details))
            {
                string safeId = SanitizeWingetArg(cleanId);
                string output = RunWingetCapture("show -e --id \"" + safeId + "\" --accept-source-agreements --disable-interactivity");
                details = ParseWingetShow(output);
                if (!details.ContainsKey("Id") || string.IsNullOrWhiteSpace(details["Id"]))
                {
                    details["Id"] = cleanId;
                }
                if (!details.ContainsKey("Name") || string.IsNullOrWhiteSpace(details["Name"]))
                {
                    details["Name"] = !string.IsNullOrEmpty(fallbackName) ? fallbackName : cleanId;
                }
                if (!details.ContainsKey("Source") || string.IsNullOrWhiteSpace(details["Source"]))
                {
                    if (!string.IsNullOrEmpty(fallbackSource))
                    {
                        details["Source"] = fallbackSource;
                    }
                    else if ((details.ContainsKey("Installer Type") && details["Installer Type"].IndexOf("msstore", StringComparison.OrdinalIgnoreCase) >= 0) ||
                             (cleanId.Length >= 12 && !cleanId.Contains(".")))
                    {
                        details["Source"] = "msstore";
                    }
                    else
                    {
                        details["Source"] = "winget";
                    }
                }
                if (!details.ContainsKey("Homepage") || string.IsNullOrWhiteSpace(details["Homepage"]))
                {
                    if (details.ContainsKey("Publisher Url") && !string.IsNullOrWhiteSpace(details["Publisher Url"]))
                    {
                        details["Homepage"] = details["Publisher Url"];
                    }
                }
                if (!details.ContainsKey("Author") || string.IsNullOrWhiteSpace(details["Author"]))
                {
                    if (details.ContainsKey("Publisher") && !string.IsNullOrWhiteSpace(details["Publisher"]))
                    {
                        details["Author"] = details["Publisher"];
                    }
                }
                detailsCache[cleanId] = details;
            }

            string iconUrl = "";
            string fallbackIconUrl = "";
            Dictionary<string, string> meta;
            if (metaCache.TryGetValue(cleanId, out meta))
            {
                if (meta.ContainsKey("iconUrl") && !string.IsNullOrEmpty(meta["iconUrl"])) iconUrl = meta["iconUrl"];
                if (meta.ContainsKey("fallbackIconUrl") && !string.IsNullOrEmpty(meta["fallbackIconUrl"])) fallbackIconUrl = meta["fallbackIconUrl"];
            }

            string src = details.ContainsKey("Source") ? details["Source"].ToLowerInvariant() : "winget";
            bool isOfficial = (src == "winget" || src == "msstore");

            if (string.IsNullOrEmpty(iconUrl))
            {
                string testUrl = details.ContainsKey("Homepage") ? details["Homepage"] : "";
                if (string.IsNullOrEmpty(testUrl) && details.ContainsKey("Publisher Url")) testUrl = details["Publisher Url"];
                string dom = TryExtractCustomDomain(testUrl);
                if (!string.IsNullOrEmpty(dom))
                {
                    iconUrl = "https://www.google.com/s2/favicons?sz=128&domain_url=" + Uri.EscapeDataString(dom);
                }
            }

            // Derive download host if available
            string downloadHost = "";
            string instUrl = details.ContainsKey("Installer Url") ? details["Installer Url"] : "";
            if (!string.IsNullOrEmpty(instUrl))
            {
                try
                {
                    Uri u = new Uri(instUrl);
                    downloadHost = u.Host;
                }
                catch { }
            }

            if (src == "msstore")
            {
                if (string.IsNullOrEmpty(downloadHost)) downloadHost = "Microsoft Store CDN (Protected)";
                if (!details.ContainsKey("Installer SHA256") || string.IsNullOrEmpty(details["Installer SHA256"]))
                {
                    details["Installer SHA256"] = "Microsoft Store Signed & Certified";
                }
            }

            bool trusted = ComputeTrust(
                cleanId,
                src,
                details.ContainsKey("Homepage") ? details["Homepage"] : "",
                details.ContainsKey("Publisher Url") ? details["Publisher Url"] : "",
                instUrl);

            SendToUi(new
            {
                type = "package_details",
                trusted = trusted,
                id = cleanId,
                name = details.ContainsKey("Name") ? details["Name"] : cleanId,
                version = details.ContainsKey("Version") ? details["Version"] : "",
                publisher = details.ContainsKey("Publisher") ? details["Publisher"] : "",
                publisherUrl = details.ContainsKey("Publisher Url") ? details["Publisher Url"] : "",
                author = details.ContainsKey("Author") ? details["Author"] : "",
                moniker = details.ContainsKey("Moniker") ? details["Moniker"] : "",
                description = details.ContainsKey("Description") ? details["Description"] : "",
                homepage = details.ContainsKey("Homepage") ? details["Homepage"] : "",
                license = details.ContainsKey("License") ? details["License"] : "",
                licenseUrl = details.ContainsKey("License Url") ? details["License Url"] : "",
                privacyUrl = details.ContainsKey("Privacy Url") ? details["Privacy Url"] : "",
                copyright = details.ContainsKey("Copyright") ? details["Copyright"] : "",
                installerType = details.ContainsKey("Installer Type") ? details["Installer Type"] : (src == "msstore" ? "msstore" : ""),
                installerUrl = instUrl,
                installerSha256 = details.ContainsKey("Installer SHA256") ? details["Installer SHA256"] : "",
                releaseDate = details.ContainsKey("Release Date") ? details["Release Date"] : "",
                source = src,
                isOfficial = isOfficial,
                downloadHost = downloadHost,
                iconUrl = iconUrl,
                fallbackIconUrl = fallbackIconUrl
            });
        }

        private Dictionary<string, string> ParseWingetShow(string rawOutput)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(rawOutput)) return d;

            string[] lines = rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string currentKey = null;
            var currentLines = new List<string>();

            Action flush = () =>
            {
                if (currentKey != null && currentLines.Count > 0)
                {
                    string combined = string.Join("\n", currentLines.ToArray()).Trim();
                    if (!d.ContainsKey(currentKey))
                    {
                        d[currentKey] = combined;
                    }
                }
                currentLines.Clear();
            };

            foreach (string rawLine in lines)
            {
                string line = rawLine;
                if (line.StartsWith("Found ") && line.Contains("[") && line.Contains("]"))
                {
                    flush();
                    int openBracket = line.IndexOf('[');
                    int closeBracket = line.LastIndexOf(']');
                    if (openBracket > 6 && closeBracket > openBracket)
                    {
                        d["Name"] = line.Substring(6, openBracket - 6).Trim();
                        d["Id"] = line.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();
                    }
                    continue;
                }

                if (!line.StartsWith("  ") && !line.StartsWith("\t") && line.Contains(":"))
                {
                    int colonIdx = line.IndexOf(':');
                    string key = line.Substring(0, colonIdx).Trim();
                    string val = line.Substring(colonIdx + 1).Trim();
                    flush();
                    currentKey = key;
                    if (!string.IsNullOrEmpty(val))
                    {
                        currentLines.Add(val);
                    }
                }
                else if (currentKey == "Description")
                {
                    if (currentLines.Count < 15 && !string.IsNullOrWhiteSpace(line))
                    {
                        currentLines.Add(line.Trim());
                    }
                }
                else if ((line.StartsWith("  ") || line.StartsWith("\t")) && line.Contains(":"))
                {
                    string trimmed = line.Trim();
                    int colonIdx = trimmed.IndexOf(':');
                    string subKey = trimmed.Substring(0, colonIdx).Trim();
                    string subVal = trimmed.Substring(colonIdx + 1).Trim();
                    if (!d.ContainsKey(subKey))
                    {
                        d[subKey] = subVal;
                    }
                }
                else if (currentKey == "License" && currentLines.Count < 3)
                {
                    currentLines.Add(line.Trim());
                }
            }
            flush();
            return d;
        }

        private const int WINGET_NO_PACKAGES_FOUND = -1978335212;      // 0x8A150014
        private const int WINGET_REBOOT_TO_FINISH = -1978334967;       // 0x8A150109
        private const int WINGET_REBOOT_INITIATED = -1978334965;       // 0x8A15010B

        private void SendTaskProgress(string taskId, string phase, int percent, string detail)
        {
            SendToUi(new { type = "task_progress", taskId = taskId, phase = phase, percent = percent, detail = detail ?? "" });
        }

        private static string BuildTaskArgs(string operation, string safeId, bool silent)
        {
            // --disable-interactivity: a prompt would otherwise hang forever (there is no one to answer it)
            string common = " --accept-source-agreements --disable-interactivity";
            string args;
            if (operation == "install")
                args = string.Format("install -e --id \"{0}\" --accept-package-agreements", safeId) + common;
            else if (operation == "upgrade")
                // --include-unknown: packages whose installed version winget can't read can still be upgraded
                args = string.Format("upgrade -e --id \"{0}\" --accept-package-agreements --include-unknown", safeId) + common;
            else if (operation == "uninstall")
                args = string.Format("uninstall -e --id \"{0}\"", safeId) + common;
            else
                return null;
            if (silent) args += " --silent";
            return args;
        }

        private void HandleRunWingetTask(string taskId, string operation, string packageId, string packageName, bool silent, string fromVersion)
        {
            try
            {
                RunWingetTaskCore(taskId, operation, packageId, packageName, silent, fromVersion);
            }
            finally
            {
                lock (procLock)
                {
                    if (activeTaskId == taskId) activeTaskId = null;
                    activePid = 0;
                    activeTerminate = null;
                }
                SetTaskbarProgress(0, 0);
            }
        }

        private bool IsCancelled(string taskId)
        {
            lock (procLock) { return cancelledTaskId != null && cancelledTaskId == taskId; }
        }

        private void RunWingetTaskCore(string taskId, string operation, string packageId, string packageName, bool silent, string fromVersion)
        {
            string safeId = SanitizeWingetArg(packageId);
            string args = BuildTaskArgs(operation, safeId, silent);
            if (args == null || string.IsNullOrEmpty(safeId))
            {
                SendToUi(new { type = "task_done", taskId = taskId, operation = operation, packageId = packageId, packageName = packageName,
                    exitCode = -1, success = false, skipped = false, verify = "none", message = "Invalid task." });
                return;
            }

            SendToUi(new
            {
                type = "task_started",
                taskId = taskId,
                operation = operation,
                packageId = packageId,
                packageName = packageName,
                command = "winget " + args
            });

            // Progress state shared by the output parser callbacks (all on the reader thread)
            string phase = "prepare";
            int percent = -1;
            string bytes = "";
            long lastSentTicks = 0;
            Action<bool> push = force =>
            {
                long now = DateTime.UtcNow.Ticks;
                if (!force && now - lastSentTicks < TimeSpan.TicksPerMillisecond * 120) return;
                lastSentTicks = now;
                SendTaskProgress(taskId, phase, percent, bytes);
                SetTaskbarProgress(percent >= 0 ? 2 : 1, Math.Max(0, percent));
            };
            SendTaskProgress(taskId, phase, -1, "");
            SetTaskbarProgress(1, 0);

            var parser = new WingetOutputParser();
            parser.OnLine = text =>
            {
                SendToUi(new { type = "task_log", taskId = taskId, packageId = packageId, line = text });
                string next = phase;
                if (text.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase)) next = "download";
                else if (text.IndexOf("verified installer hash", StringComparison.OrdinalIgnoreCase) >= 0) next = "verify";
                else if (text.StartsWith("Starting package install", StringComparison.OrdinalIgnoreCase)) next = "install";
                else if (text.StartsWith("Starting package uninstall", StringComparison.OrdinalIgnoreCase)) next = "uninstall";
                else if (text.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase) ||
                         text.StartsWith("Successfully uninstalled", StringComparison.OrdinalIgnoreCase)) next = "finish";
                if (next != phase)
                {
                    phase = next;
                    percent = -1;
                    bytes = "";
                    push(true);
                }
            };
            parser.OnProgress = (state, pct) =>
            {
                if (state == 1)
                {
                    if (phase == "prepare") phase = "download";
                    if (pct != percent) { percent = pct; push(pct == 100); }
                }
                else if (state == 3 || state == 0)
                {
                    if (percent != -1) { percent = -1; push(true); }
                }
            };
            parser.OnBytes = b =>
            {
                if (phase == "prepare") phase = "download";
                bytes = b;
                push(false);
            };

            int exitCode = -1;
            string startError = null;
            try
            {
                exitCode = RunWithConsole(taskId, "winget.exe " + args, args, parser);
            }
            catch (Exception ex)
            {
                startError = DescribeStartFailure(ex);
                SendToUi(new { type = "task_log", taskId = taskId, packageId = packageId, line = "Error: " + startError });
            }

            Dictionary<string, string> removed;
            detailsCache.TryRemove(packageId, out removed);

            if (IsCancelled(taskId))
            {
                SendToUi(new { type = "task_cancelled", taskId = taskId, packageId = packageId, packageName = packageName });
                return;
            }

            // "No applicable update" / "already installed" are no-ops, not failures
            bool skipped = (exitCode == WINGET_UPDATE_NOT_APPLICABLE || exitCode == WINGET_ALREADY_INSTALLED);
            bool rebootRequired = (exitCode == WINGET_REBOOT_TO_FINISH || exitCode == WINGET_REBOOT_INITIATED);
            bool success = (exitCode == 0) || skipped || rebootRequired;

            // winget's exit code says the command finished, not that the system state changed: Store
            // installs complete asynchronously and many uninstallers relaunch themselves from %TEMP% and
            // exit immediately. Ask winget what is installed now before telling the UI it's done.
            string verify = "none";
            Dictionary<string, string> row = null;
            if (success && startError == null)
            {
                phase = "confirm";
                percent = -1;
                bytes = "";
                push(true);
                verify = VerifyTaskOutcome(taskId, operation, packageId, fromVersion, out row);
                if (row != null) AttachLocalRegistryIcons(new List<Dictionary<string, string>> { row });
                if (IsCancelled(taskId))
                {
                    SendToUi(new { type = "task_cancelled", taskId = taskId, packageId = packageId, packageName = packageName });
                    return;
                }
            }

            SendToUi(new
            {
                type = "task_done",
                taskId = taskId,
                operation = operation,
                packageId = packageId,
                packageName = packageName,
                exitCode = exitCode,
                success = success,
                skipped = skipped,
                rebootRequired = rebootRequired,
                verify = verify,               // confirmed | pending | unknown | none
                installed = row,               // fresh `winget list` row for the package, or null if absent
                message = startError ?? parser.LastProblemLine
            });
        }

        // Runs winget under a pseudo console (real progress) or, if ConPTY is unavailable, with plain
        // redirected pipes. Returns the exit code.
        private int RunWithConsole(string taskId, string commandLine, string args, WingetOutputParser parser)
        {
            PtyProcess pty = PtyProcess.Start(commandLine);
            if (pty != null)
            {
                using (pty)
                {
                    if (!RegisterActive(taskId, pty.Pid, () => pty.Terminate())) return -1;

                    var reader = new Thread(() =>
                    {
                        try
                        {
                            using (var sr = new StreamReader(pty.Output, new UTF8Encoding(false)))
                            {
                                var buf = new char[2048];
                                int n;
                                while ((n = sr.Read(buf, 0, buf.Length)) > 0) parser.Feed(buf, n);
                            }
                        }
                        catch { }
                        parser.Flush();
                    });
                    reader.IsBackground = true;
                    reader.Start();

                    pty.WaitForExit();
                    int code = pty.ExitCode;
                    // Let the reader drain, then close the console so the output stream ends
                    reader.Join(400);
                    pty.ClosePseudoConsole();
                    reader.Join(3000);
                    return code;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = "winget.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var proc = Process.Start(psi))
            {
                if (!RegisterActive(taskId, proc.Id, () => { try { if (!proc.HasExited) proc.Kill(); } catch { } })) return -1;
                var errParser = new WingetOutputParser { OnLine = parser.OnLine };
                Task outReader = Task.Run(() => PumpReader(proc.StandardOutput, parser));
                Task errReader = Task.Run(() => PumpReader(proc.StandardError, errParser));
                proc.WaitForExit();
                Task.WaitAll(new[] { outReader, errReader }, 2000);
                if (string.IsNullOrEmpty(parser.LastProblemLine)) parser.LastProblemLine = errParser.LastProblemLine;
                return proc.ExitCode;
            }
        }

        private static void PumpReader(StreamReader reader, WingetOutputParser parser)
        {
            var buf = new char[2048];
            int n;
            try
            {
                while ((n = reader.Read(buf, 0, buf.Length)) > 0) parser.Feed(buf, n);
            }
            catch { }
            parser.Flush();
        }

        // Publishes the running process so cancel/close can kill it. Returns false (after killing it)
        // if the task was cancelled before the process even started.
        private bool RegisterActive(string taskId, int pid, Action terminate)
        {
            bool cancelled;
            lock (procLock)
            {
                activePid = pid;
                activeTerminate = terminate;
                cancelled = cancelledTaskId == taskId;
            }
            if (cancelled)
            {
                KillActiveProcess();
                return false;
            }
            return true;
        }

        private void CancelTask(string taskId)
        {
            lock (procLock)
            {
                if (activeTaskId == null || (!string.IsNullOrEmpty(taskId) && activeTaskId != taskId)) return;
                cancelledTaskId = activeTaskId;
            }
            KillActiveProcess();
        }

        private void KillActiveProcess()
        {
            int pid;
            Action terminate;
            lock (procLock)
            {
                pid = activePid;
                terminate = activeTerminate;
                activePid = 0;
                activeTerminate = null;
            }
            if (pid == 0) return;
            try
            {
                // winget spawns the real installer as a child; killing winget alone would orphan it
                using (var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (killer != null) killer.WaitForExit(3000);
                }
            }
            catch { }
            try { if (terminate != null) terminate(); } catch { }
        }

        // Ask winget whether `id` is installed right now. ok=false means winget itself failed, so a
        // missing row proves nothing.
        private Dictionary<string, string> QueryInstalledPackage(string id, out bool ok)
        {
            int code;
            string output = RunWingetCapture(
                "list -e --id \"" + SanitizeWingetArg(id) + "\" --accept-source-agreements --disable-interactivity", 45000, out code);
            if (code == WINGET_NO_PACKAGES_FOUND)
            {
                ok = true;
                return null;
            }
            ok = (code == 0);
            foreach (var row in ParseWingetTable(output))
            {
                if (string.Equals(row["id"], id, StringComparison.OrdinalIgnoreCase)) return row;
            }
            return null;
        }

        private string VerifyTaskOutcome(string taskId, string operation, string id, string fromVersion,
            out Dictionary<string, string> row)
        {
            // Upgrades are synchronous in practice; installs/uninstalls may finish in the background
            int budgetMs = operation == "upgrade" ? 8000 : 45000;
            var sw = Stopwatch.StartNew();
            bool announced = false;
            row = null;
            while (true)
            {
                if (IsCancelled(taskId)) return "unknown";
                bool ok;
                row = QueryInstalledPackage(id, out ok);
                if (ok)
                {
                    if (operation == "uninstall" && row == null) return "confirmed";
                    if (operation == "install" && row != null) return "confirmed";
                    if (operation == "upgrade" && row != null &&
                        (string.IsNullOrWhiteSpace(row["available"]) ||
                         !string.Equals(row["version"], fromVersion ?? "", StringComparison.OrdinalIgnoreCase)))
                    {
                        return "confirmed";
                    }
                }
                if (sw.ElapsedMilliseconds > budgetMs) return ok ? "pending" : "unknown";
                if (!announced)
                {
                    announced = true;
                    SendToUi(new
                    {
                        type = "task_log",
                        taskId = taskId,
                        packageId = id,
                        line = operation == "uninstall"
                            ? "winget finished; waiting for the uninstaller to remove the package..."
                            : "winget finished; waiting for the package to show up as installed..."
                    });
                }
                Thread.Sleep(2500);
            }
        }

        // Windows taskbar button progress (ITaskbarList3). state: 0 none, 1 indeterminate, 2 normal.
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int flags);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        private class TaskbarListClass { }

        private ITaskbarList3 taskbar;

        private void SetTaskbarProgress(int state, int percent)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action(() => SetTaskbarProgress(state, percent))); } catch { }
                return;
            }
            try
            {
                if (taskbar == null)
                {
                    taskbar = (ITaskbarList3)new TaskbarListClass();
                    taskbar.HrInit();
                }
                int flags = state == 2 ? 0x2 : state == 1 ? 0x1 : 0x0;
                taskbar.SetProgressState(this.Handle, flags);
                if (state == 2) taskbar.SetProgressValue(this.Handle, (ulong)percent, 100UL);
            }
            catch { }
        }

        private string RunWingetCapture(string arguments, int timeoutMs = 60000)
        {
            int ignored;
            return RunWingetCapture(arguments, timeoutMs, out ignored);
        }

        // exitCode is -1 when winget could not be started or timed out
        private string RunWingetCapture(string arguments, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var proc = Process.Start(psi))
                {
                    // Drain both pipes concurrently: ReadToEnd() on stdout alone blocks forever on a hung
                    // winget (so a trailing WaitForExit timeout never ran), and an unread stderr can
                    // deadlock the child once its pipe buffer fills.
                    Task<string> outTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                    Task<string> errTask = Task.Run(() => proc.StandardError.ReadToEnd());

                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                    }
                    else
                    {
                        exitCode = proc.ExitCode;
                    }
                    Task.WaitAll(new[] { outTask, errTask }, 3000);
                    string outStr = outTask.IsCompleted ? outTask.Result : "";
                    string errStr = errTask.IsCompleted ? errTask.Result : "";
                    if (string.IsNullOrWhiteSpace(outStr) && !string.IsNullOrWhiteSpace(errStr))
                    {
                        return "Error: " + errStr.Trim();
                    }
                    return outStr;
                }
            }
            catch (Exception ex)
            {
                return "Error: " + DescribeStartFailure(ex);
            }
        }

        private List<Dictionary<string, string>> ParseWingetTable(string rawOutput)
        {
            var results = new List<Dictionary<string, string>>();
            if (string.IsNullOrEmpty(rawOutput)) return results;

            string[] lines = rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int headerIdx = -1;
            int idStart = -1, verStart = -1, availStart = -1, srcStart = -1, matchStart = -1;

            for (int i = 0; i < lines.Length - 1; i++)
            {
                // The header is whatever sits directly above the all-dashes rule, in any locale
                if (!Regex.IsMatch(lines[i + 1], @"^\s*-{5,}\s*$")) continue;

                string line = lines[i];
                headerIdx = i;

                // English headers: locate columns by name. Strip leading spinner artifacts first,
                // then add the stripped offset back so indices align with unstripped data rows.
                int namePos = line.IndexOf("Name", StringComparison.Ordinal);
                int offset = namePos >= 0 ? namePos : (line.Length - line.TrimStart().Length);
                string cleanHeader = line.Substring(offset);
                idStart = cleanHeader.IndexOf("Id", StringComparison.Ordinal);
                verStart = cleanHeader.IndexOf("Version", StringComparison.Ordinal);
                availStart = cleanHeader.IndexOf("Available", StringComparison.Ordinal);
                srcStart = cleanHeader.IndexOf("Source", StringComparison.Ordinal);
                matchStart = cleanHeader.IndexOf("Match", StringComparison.Ordinal);

                if (idStart >= 0) idStart += offset;
                if (verStart >= 0) verStart += offset;
                if (availStart >= 0) availStart += offset;
                if (srcStart >= 0) srcStart += offset;
                if (matchStart >= 0) matchStart += offset;

                if (idStart < 0 || verStart < 0)
                {
                    // Localized headers: columns are runs of text separated by 2+ spaces. winget's order
                    // is fixed (Name, Id, Version, [Available|Match], [Source]).
                    idStart = verStart = availStart = srcStart = matchStart = -1;
                    var cols = Regex.Matches(cleanHeader, @"\S+(?: \S+)*");
                    if (cols.Count >= 3)
                    {
                        idStart = cols[1].Index + offset;
                        verStart = cols[2].Index + offset;
                        if (cols.Count >= 5)
                        {
                            availStart = cols[3].Index + offset;
                            srcStart = cols[4].Index + offset;
                        }
                        else if (cols.Count == 4)
                        {
                            // 4th column is Source if the first data row ends in a known source name
                            string sample = i + 2 < lines.Length ? lines[i + 2] : "";
                            int c = cols[3].Index + offset;
                            string tail = c < sample.Length ? sample.Substring(c).Trim() : "";
                            if (tail == "winget" || tail == "msstore") srcStart = c;
                            else availStart = c;
                        }
                    }
                }
                break;
            }

            if (headerIdx == -1 || idStart == -1 || verStart == -1) return results;

            for (int i = headerIdx + 2; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("---")) continue;
                if (line.EndsWith("upgrades available.")) continue;
                if (line.Contains("package(s) have version numbers that cannot be determined")) continue;
                if (line.Length < idStart + 2) continue;

                try
                {
                    string name = SafeSlice(line, 0, idStart);
                    int afterId = verStart;
                    if (matchStart > idStart && matchStart < verStart)
                    {
                        afterId = matchStart;
                    }

                    string id = SafeSlice(line, idStart, afterId);
                    string version = "";
                    string available = "";
                    string source = "";

                    if (availStart > verStart)
                    {
                        version = SafeSlice(line, verStart, availStart);
                        if (srcStart > availStart)
                        {
                            available = SafeSlice(line, availStart, srcStart);
                            source = SafeSlice(line, srcStart, line.Length);
                        }
                        else
                        {
                            available = SafeSlice(line, availStart, line.Length);
                        }
                    }
                    else if (srcStart > verStart)
                    {
                        int verEnd = (matchStart > verStart && matchStart < srcStart) ? matchStart : srcStart;
                        version = SafeSlice(line, verStart, verEnd);
                        source = SafeSlice(line, srcStart, line.Length);
                    }
                    else
                    {
                        int verEnd = (matchStart > verStart) ? matchStart : line.Length;
                        version = SafeSlice(line, verStart, verEnd);
                    }

                    if (!string.IsNullOrEmpty(id) && !id.Contains(" "))
                    {
                        var item = new Dictionary<string, string>
                        {
                            { "name", name },
                            { "id", id },
                            { "version", version },
                            { "available", available },
                            { "source", source }
                        };
                        results.Add(item);
                    }
                }
                catch { }
            }

            return results;
        }

        private string SafeSlice(string str, int start, int end)
        {
            if (start >= str.Length) return "";
            if (end > str.Length) end = str.Length;
            if (end <= start) return "";
            return str.Substring(start, end - start).Trim();
        }

        // Friendly text for the "winget.exe not found" case (App Installer missing or broken)
        private static string DescribeStartFailure(Exception ex)
        {
            var w32 = ex as System.ComponentModel.Win32Exception;
            if (w32 != null && (w32.NativeErrorCode == 2 || w32.NativeErrorCode == 3))
            {
                return "winget.exe was not found. Install or update \"App Installer\" from the Microsoft Store, then restart WingetUI.";
            }
            return ex.Message;
        }

        [STAThread]
        public static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // One instance per folder: two copies would fight over Data\ (presets, cache, WebView2 profile)
            bool created;
            string key = "WingetUI_" + Regex.Replace(
                AppDomain.CurrentDomain.BaseDirectory.ToLowerInvariant(), "[^a-z0-9]", "_");
            using (var mutex = new Mutex(true, key, out created))
            {
                if (!created)
                {
                    MessageBox.Show("WingetUI is already running.", "WingetUI",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new MainForm());
            }
        }
    }

    // Runs a command attached to a Windows pseudo console (ConPTY). winget only draws its download
    // progress (and the OSC 9;4 percent sequences) when it believes it is talking to a terminal; with
    // plain redirected pipes it prints nothing between "Downloading ..." and "Successfully verified".
    // Start() returns null when ConPTY is unavailable (pre-1809 Windows), and callers fall back to pipes.
    internal sealed class PtyProcess : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out Microsoft.Win32.SafeHandles.SafeFileHandle r, out Microsoft.Win32.SafeHandles.SafeFileHandle w, IntPtr sa, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, Microsoft.Win32.SafeHandles.SafeFileHandle hIn, Microsoft.Win32.SafeHandles.SafeFileHandle hOut, uint flags, out IntPtr hpc);
        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr hpc);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr val, IntPtr cb, IntPtr prev, IntPtr ret);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
            IntPtr env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeProcess(IntPtr h, out int code);
        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        private IntPtr hpc = IntPtr.Zero;
        private IntPtr hProcess = IntPtr.Zero;
        private IntPtr attrList = IntPtr.Zero;
        private Microsoft.Win32.SafeHandles.SafeFileHandle inWrite;
        private readonly object closeLock = new object();

        public int Pid { get; private set; }
        public Stream Output { get; private set; }

        private PtyProcess() { }

        // Throws Win32Exception when the executable cannot be started (e.g. winget missing).
        public static PtyProcess Start(string commandLine)
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle inRead = null, inWrite = null, outRead = null, outWrite = null;
            var p = new PtyProcess();
            try
            {
                if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) return null;
                if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) return null;

                IntPtr pc;
                int hr;
                try
                {
                    // Wide enough that winget never wraps a progress or table line
                    hr = CreatePseudoConsole(new COORD { X = 240, Y = 60 }, inRead, outWrite, 0, out pc);
                }
                catch (EntryPointNotFoundException)
                {
                    return null;
                }
                if (hr != 0) return null;
                p.hpc = pc;

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                p.attrList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(p.attrList, 1, 0, ref size)) { p.Dispose(); return null; }
                if (!UpdateProcThreadAttribute(p.attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pc,
                        (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    p.Dispose();
                    return null;
                }

                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                // Without this the child can pick up the parent's std handles instead of the pseudo console
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.lpAttributeList = p.attrList;

                PROCESS_INFORMATION pi;
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref si, out pi))
                {
                    int err = Marshal.GetLastWin32Error();
                    p.Dispose();
                    throw new System.ComponentModel.Win32Exception(err);
                }
                CloseHandle(pi.hThread);
                p.hProcess = pi.hProcess;
                p.Pid = pi.dwProcessId;
                p.inWrite = inWrite;
                inWrite = null;
                p.Output = new FileStream(outRead, FileAccess.Read, 4096, false);
                outRead = null;
                return p;
            }
            finally
            {
                // The pseudo console holds its own duplicates of these ends
                if (inRead != null) inRead.Dispose();
                if (outWrite != null) outWrite.Dispose();
                if (inWrite != null) inWrite.Dispose();
                if (outRead != null) outRead.Dispose();
            }
        }

        public bool HasExited
        {
            get { return hProcess == IntPtr.Zero || WaitForSingleObject(hProcess, 0) == 0; }
        }

        public void WaitForExit()
        {
            if (hProcess != IntPtr.Zero) WaitForSingleObject(hProcess, 0xFFFFFFFF);
        }

        public int ExitCode
        {
            get
            {
                int code = -1;
                if (hProcess != IntPtr.Zero) GetExitCodeProcess(hProcess, out code);
                return code;
            }
        }

        public void Terminate()
        {
            if (hProcess != IntPtr.Zero && !HasExited) TerminateProcess(hProcess, 1);
        }

        // Closing the pseudo console flushes its remaining output and ends the Output stream (EOF).
        public void ClosePseudoConsole()
        {
            lock (closeLock)
            {
                if (hpc != IntPtr.Zero)
                {
                    ClosePseudoConsole(hpc);
                    hpc = IntPtr.Zero;
                }
            }
        }

        public void Dispose()
        {
            ClosePseudoConsole();
            if (inWrite != null) { inWrite.Dispose(); inWrite = null; }
            if (Output != null) { try { Output.Dispose(); } catch { } Output = null; }
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
                attrList = IntPtr.Zero;
            }
            if (hProcess != IntPtr.Zero)
            {
                CloseHandle(hProcess);
                hProcess = IntPtr.Zero;
            }
        }
    }

    // Turns raw winget console output (VT sequences, \r-redrawn progress bars, spinner frames) into
    // clean log lines plus structured progress. Locale-independent where it matters: the percentage
    // comes from winget's OSC 9;4 taskbar-progress sequence, not from parsing translated text.
    internal sealed class WingetOutputParser
    {
        public Action<string> OnLine;                 // a real, human-readable output line
        public Action<int, int> OnProgress;           // (state, percent): state 1 = percent, 3 = indeterminate, 0 = cleared
        public Action<string> OnBytes;                // "14.0 MB / 60.4 MB"
        public string LastProblemLine = "";

        private enum Mode { Text, Esc, Csi, Osc, OscEsc }
        private Mode mode = Mode.Text;
        private readonly StringBuilder line = new StringBuilder();
        private readonly StringBuilder osc = new StringBuilder();
        private string lastLine = "";

        private static readonly Regex SpinnerRx = new Regex(@"^[-\\|/]$");
        private static readonly Regex LeadingSpinnerRx = new Regex(@"^(?:[-\\|/]\s+)+");
        private static readonly Regex BytesRx = new Regex(
            @"([\d.,]+\s*[KMGT]?i?B)\s*/\s*([\d.,]+\s*[KMGT]?i?B)\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex PercentRx = new Regex(@"^[▀-▟\s]*(\d{1,3})\s*%\s*$");
        private static readonly Regex BarCharsRx = new Regex(@"[▀-▟]");
        private static readonly Regex ProblemRx = new Regex(
            @"fail|error|cannot|unable|not found|denied|blocked|restart|reboot", RegexOptions.IgnoreCase);

        public void Feed(char[] buf, int count)
        {
            for (int i = 0; i < count; i++) FeedChar(buf[i]);
        }

        public void Flush()
        {
            EmitLine();
        }

        private void FeedChar(char c)
        {
            switch (mode)
            {
                case Mode.Text:
                    if (c == '\x1b') { mode = Mode.Esc; return; }
                    if (c == '\r' || c == '\n') { EmitLine(); return; }
                    if (c == '\x07' || c == '\b') return;
                    line.Append(c);
                    return;

                case Mode.Esc:
                    if (c == '[') { mode = Mode.Csi; return; }
                    if (c == ']') { osc.Clear(); mode = Mode.Osc; return; }
                    mode = Mode.Text;   // two-character escape (ESC 7, ESC =, ...): drop it
                    return;

                case Mode.Csi:
                    // Parameters/intermediates are 0x20-0x3F; the final byte (0x40-0x7E) ends the sequence
                    if (c >= '@' && c <= '~')
                    {
                        mode = Mode.Text;
                        // Cursor-forward is how ConPTY paints the empty part of a progress bar; keep a gap
                        if (c == 'C' && line.Length > 0 && line[line.Length - 1] != ' ') line.Append(' ');
                    }
                    return;

                case Mode.Osc:
                    if (c == '\x07') { EndOsc(); return; }
                    if (c == '\x1b') { mode = Mode.OscEsc; return; }
                    if (osc.Length < 512) osc.Append(c);
                    return;

                case Mode.OscEsc:
                    EndOsc();   // ESC \ (string terminator); anything else also ends the OSC
                    return;
            }
        }

        private void EndOsc()
        {
            mode = Mode.Text;
            string s = osc.ToString();
            osc.Clear();
            if (!s.StartsWith("9;4;")) return;   // window titles etc.
            string[] parts = s.Split(';');
            int state, pct = 0;
            if (parts.Length >= 3 && int.TryParse(parts[2], out state))
            {
                if (parts.Length >= 4) int.TryParse(parts[3], out pct);
                if (OnProgress != null) OnProgress(state, Math.Max(0, Math.Min(100, pct)));
            }
        }

        private void EmitLine()
        {
            if (line.Length == 0) return;
            string text = line.ToString().Trim();
            line.Clear();
            // ConPTY repaints spinner frames in place, so several can be left in front of the real text
            text = LeadingSpinnerRx.Replace(text, "");
            if (text.Length == 0 || SpinnerRx.IsMatch(text)) return;

            Match m = BytesRx.Match(text);
            if (m.Success && (BarCharsRx.IsMatch(text) || text.Length == m.Length))
            {
                if (OnBytes != null) OnBytes(m.Groups[1].Value.Trim() + " / " + m.Groups[2].Value.Trim());
                return;
            }
            Match pm = PercentRx.Match(text);
            if (pm.Success)
            {
                int pct;
                if (int.TryParse(pm.Groups[1].Value, out pct) && OnProgress != null) OnProgress(1, Math.Min(100, pct));
                return;
            }
            // A bare bar with no numbers yet
            if (BarCharsRx.Replace(text, "").Trim().Length == 0) return;

            if (text == lastLine) return;
            lastLine = text;
            if (ProblemRx.IsMatch(text)) LastProblemLine = text;
            if (OnLine != null) OnLine(text);
        }
    }
}
