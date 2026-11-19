using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
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

namespace WingetBabyUI
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
        private volatile bool activeTaskCancelled = false;
        private Process activeProcess = null;
        private readonly object procLock = new object();
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

            this.Text = "Winget Baby — Package & Preset Manager";
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

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WingetBaby");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        // The presets.json shipped beside the exe is only a seed; edits are written to Data\.
        private void SeedPresets()
        {
            try
            {
                string seed = Path.Combine(appDir, "presets.json");
                if (!File.Exists(presetsPath) && File.Exists(seed))
                {
                    File.Copy(seed, presetsPath);
                }
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
                    "Winget Baby Error",
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
                            string formatted = json.Serialize(msg["data"]);
                            File.WriteAllText(presetsPath, formatted, Encoding.UTF8);
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
                        bool silent = !msg.ContainsKey("silent") || Convert.ToBoolean(msg["silent"]);
                        await Task.Run(() => HandleRunWingetTask(taskId, operation, packageId, packageName, silent));
                        break;

                    case "cancel_task":
                        activeTaskCancelled = true;
                        await Task.Run(() => KillActiveProcess());
                        SendToUi(new { type = "task_cancelled" });
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
                        this.Close();
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
                    wingetTxtPath = wingetTxtPath
                });
            }
            else
            {
                SendToUi(new
                {
                    type = "presets_loaded",
                    data = new { categories = new object[0] },
                    wingetTxtPath = wingetTxtPath
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
                req.UserAgent = "WingetBabyUI/1.0";
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

        private void HandleRunWingetTask(string taskId, string operation, string packageId, string packageName, bool silent)
        {
            string safeId = SanitizeWingetArg(packageId);
            string args;

            if (operation == "install")
            {
                args = string.Format("install -e --id \"{0}\" --accept-package-agreements --accept-source-agreements", safeId);
                if (silent) args += " --silent";
            }
            else if (operation == "upgrade")
            {
                args = string.Format("upgrade -e --id \"{0}\" --accept-package-agreements --accept-source-agreements", safeId);
                if (silent) args += " --silent";
            }
            else if (operation == "uninstall")
            {
                args = string.Format("uninstall -e --id \"{0}\" --accept-source-agreements", safeId);
                if (silent) args += " --silent";
            }
            else
            {
                SendToUi(new { type = "task_done", taskId = taskId, packageId = packageId, exitCode = -1, success = false });
                return;
            }

            activeTaskCancelled = false;
            SendToUi(new
            {
                type = "task_started",
                taskId = taskId,
                operation = operation,
                packageId = packageId,
                packageName = packageName,
                command = "winget " + args
            });

            int exitCode = -1;
            Process proc = null;
            try
            {
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

                proc = new Process { StartInfo = psi };
                lock (procLock)
                {
                    activeProcess = proc;
                }

                proc.Start();

                // Stream stdout and stderr cleanly
                Task outReader = Task.Run(() => StreamReaderLoop(proc.StandardOutput, taskId, packageId));
                Task errReader = Task.Run(() => StreamReaderLoop(proc.StandardError, taskId, packageId));

                proc.WaitForExit();
                Task.WaitAll(new[] { outReader, errReader }, 2000);
                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                if (!activeTaskCancelled)
                {
                    SendToUi(new
                    {
                        type = "task_log",
                        taskId = taskId,
                        packageId = packageId,
                        line = "Error: " + ex.Message
                    });
                }
            }
            finally
            {
                lock (procLock)
                {
                    activeProcess = null;
                }
                if (proc != null)
                {
                    try { proc.Dispose(); } catch { }
                }
            }

            Dictionary<string, string> removed;
            detailsCache.TryRemove(packageId, out removed);

            // cancel_task already emitted task_cancelled; don't emit a conflicting task_done
            if (activeTaskCancelled) return;

            // "No applicable update" / "already installed" are no-ops, not failures
            bool skipped = (exitCode == WINGET_UPDATE_NOT_APPLICABLE || exitCode == WINGET_ALREADY_INSTALLED);
            bool success = (exitCode == 0) || skipped;
            SendToUi(new
            {
                type = "task_done",
                taskId = taskId,
                operation = operation,
                packageId = packageId,
                packageName = packageName,
                exitCode = exitCode,
                success = success,
                skipped = skipped
            });
        }

        private void StreamReaderLoop(StreamReader reader, string taskId, string packageId)
        {
            var buffer = new StringBuilder();
            int ch;
            string lastSent = "";

            while ((ch = reader.Read()) != -1)
            {
                if (ch == '\r' || ch == '\n')
                {
                    if (buffer.Length > 0)
                    {
                        string line = buffer.ToString().Trim();
                        buffer.Clear();
                        // Skip single-character spinner frames (- \ | /)
                        if (line.Length > 1 && line != lastSent)
                        {
                            lastSent = line;
                            SendToUi(new
                            {
                                type = "task_log",
                                taskId = taskId,
                                packageId = packageId,
                                line = line
                            });
                        }
                    }
                }
                else
                {
                    buffer.Append((char)ch);
                }
            }

            if (buffer.Length > 1)
            {
                SendToUi(new
                {
                    type = "task_log",
                    taskId = taskId,
                    packageId = packageId,
                    line = buffer.ToString().Trim()
                });
            }
        }

        private void KillActiveProcess()
        {
            lock (procLock)
            {
                if (activeProcess != null)
                {
                    try
                    {
                        if (!activeProcess.HasExited)
                        {
                            // winget spawns the real installer as a child; Kill() alone would orphan it
                            using (var killer = Process.Start(new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = "/PID " + activeProcess.Id + " /T /F",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }))
                            {
                                if (killer != null) killer.WaitForExit(3000);
                            }
                            if (!activeProcess.HasExited) activeProcess.Kill();
                        }
                    }
                    catch { }
                    activeProcess = null;
                }
            }
        }

        private string RunWingetCapture(string arguments, int timeoutMs = 60000)
        {
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
                return "Error: " + ex.Message;
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

        [STAThread]
        public static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // One instance per folder: two copies would fight over Data\ (presets, cache, WebView2 profile)
            bool created;
            string key = "WingetBabyUI_" + Regex.Replace(
                AppDomain.CurrentDomain.BaseDirectory.ToLowerInvariant(), "[^a-z0-9]", "_");
            using (var mutex = new Mutex(true, key, out created))
            {
                if (!created)
                {
                    MessageBox.Show("Winget Baby is already running.", "Winget Baby",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new MainForm());
            }
        }
    }
}
