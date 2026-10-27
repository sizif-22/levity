using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using AntigravitySidebar.Services;
using Microsoft.Web.WebView2.Core;

namespace AntigravitySidebar
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings = AppSettings.Load();
        private AgyProcessManager _agyManager = null!;
        private TrayIconManager _trayManager = null!;
        private HotkeyManager? _hotkeyManager;
        private bool _isSidebarVisible = false;
        private bool _isAnimating = false;

        private readonly MobileHub _hub;
        private readonly ProjectSyncService _sync = new();
        private readonly List<string> _history = new();
        private const int MaxHistory = 4000;
        private string? _lastStatusJson;
        private string? _lastSyncJson;

        private double _targetRight;
        private double _dockedLeft;
        private double _targetTop;
        private double _targetHeight;
        private double _targetWidth;

        public MainWindow()
        {
            InitializeComponent();
            _hub = new MobileHub(_settings);
            this.Deactivated += MainWindow_Deactivated;
        }

        public void InitializeSidebar()
        {
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            try { File.AppendAllText(log, "[TRACE 1] In InitializeSidebar\n"); } catch { }

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            _trayManager = new TrayIconManager(iconPath, _settings.IsPinned, _settings.LaunchOnStartup);
            try { File.AppendAllText(log, "[TRACE 2] TrayManager created\n"); } catch { }

            _trayManager.OnToggleRequested += ToggleVisibility;
            _trayManager.OnNewConversationRequested += () => Dispatcher.Invoke(HandleNewConversation);
            _trayManager.OnChooseDirectoryRequested += () => Dispatcher.Invoke(HandlePickFolder);
            _trayManager.OnPinToggled += (pinned) => Dispatcher.Invoke(() => SetPinned(pinned));
            _trayManager.OnStartupToggled += (startup) =>
            {
                _settings.LaunchOnStartup = startup;
                _settings.Save();
                AppSettings.SetStartupRegistration(startup);
            };
            _trayManager.OnRestartRequested += () => Dispatcher.Invoke(() =>
            {
                _agyManager.Restart();
                _trayManager.ShowNotification("Levity", "Engine restarted.");
            });
            _trayManager.OnMobileRequested += () => Dispatcher.Invoke(() => OpenPanel("mobile"));
            _trayManager.OnReviewProjectsRequested += () => Dispatcher.Invoke(() => OpenPanel("projects"));
            _trayManager.OnExitRequested += () => Dispatcher.Invoke(ShutdownApplication);

            _hub.OnMessage += (clientId, msg) => Dispatcher.BeginInvoke(new Action(() => HandleClientMessage(msg, clientId)));
            _hub.OnChanged += () => Dispatcher.BeginInvoke(new Action(() => PostJsonToWeb(BuildRemoteInfo())));
            _hub.OnPairRequest += req => Dispatcher.BeginInvoke(new Action(() =>
            {
                SlideIn();
                _trayManager.ShowNotification("Levity", $"\"{req.Name}\" wants to connect. Approve it in the sidebar.");
            }));
            _sync.OnProgress += () => Dispatcher.Invoke(PublishSyncState);
            _jobs.Changed += ScheduleJobsPublish;
            _jobs.BackgroundFinished += j => Dispatcher.BeginInvoke(new Action(() =>
                RawPublish(JsonSerializer.Serialize(new { type = "toast", text = $"Job #{j.Id} {(j.State == JobState.Stopped ? "stopped" : j.ExitCode == 0 ? "finished" : $"failed (exit {j.ExitCode})")}" }))));

            // Agy Process Manager
            _agyManager = new AgyProcessManager(_settings.WorkingDirectory!);
            _agyManager.OnEventReceived += HandleAgyEvent;
            _agyManager.OnStatusChanged += HandleAgyStatus;
            _agyManager.OnErrorReceived += HandleAgyError;
            _agyManager.Start();
            try { File.AppendAllText(log, "[TRACE 3] AgyManager started\n"); } catch { }

            if (_settings.RemoteEnabled) _hub.Start();

            // Setup WebView2
            InitializeWebViewAsync();
            try { File.AppendAllText(log, "[TRACE 4] InitializeWebViewAsync triggered\n"); } catch { }

            // Auto slide in on startup so the sidebar is immediately visible to the user!
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SlideIn();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            try { File.AppendAllText(log, "[TRACE 5] SlideIn dispatched\n"); } catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.EnableImmersiveDarkMode(hwnd);
            NativeMethods.SetWindowCornerPreference(hwnd, NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND);

            // Hotkey Manager
            if (_hotkeyManager == null)
            {
                _hotkeyManager = new HotkeyManager();
                _hotkeyManager.Register(hwnd);
                _hotkeyManager.OnHotkeyPressed += () => Dispatcher.Invoke(ToggleVisibility);
            }

            // Position initially
            PositionWindow();
        }

        private async void InitializeWebViewAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntigravitySidebar", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                string wwwrootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
                if (Directory.Exists(wwwrootDir))
                {
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "levity.local",
                        wwwrootDir,
                        CoreWebView2HostResourceAccessKind.Allow
                    );
                    webView.CoreWebView2.Navigate("https://levity.local/index.html");
                }

                webView.CoreWebView2.WebMessageReceived += HandleWebMessageReceived;

                // Disable default context menu
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // Links in agent replies open in the default browser instead of navigating the sidebar.
                webView.CoreWebView2.NewWindowRequested += (s, args) =>
                {
                    args.Handled = true;
                    if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                    }
                };
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to initialize WebView2: {ex.Message}", "Levity Error");
            }
        }

        private void PositionWindow(bool offscreen = false)
        {
            var screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var workArea = screen.WorkingArea;

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            _settings ??= AppSettings.Load();
            _targetWidth = (_settings?.SidebarWidth > 0) ? _settings.SidebarWidth : 460;
            _targetTop = workArea.Top / dpiY;
            _targetHeight = workArea.Height / dpiY;
            _targetRight = workArea.Right / dpiX;
            _dockedLeft = _targetRight - _targetWidth;

            this.Width = _targetWidth;
            this.Top = _targetTop;
            this.Height = _targetHeight;
            this.Left = offscreen ? _targetRight : _dockedLeft;
        }

        public void ToggleVisibility()
        {
            if (_isSidebarVisible)
            {
                SlideOut();
            }
            else
            {
                SlideIn();
            }
        }

        public void SlideIn()
        {
            if (_isAnimating) return;
            if (_isSidebarVisible)
            {
                try { this.Activate(); } catch { }
                this.Topmost = true;
                NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
                PostJsonToWeb(new { type = "focus_input" });
                return;
            }

            _isAnimating = true;

            PositionWindow(offscreen: true);
            this.Topmost = true;
            this.Show();
            try { this.Activate(); } catch { }
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);

            var anim = new DoubleAnimation
            {
                From = _targetRight,
                To = _dockedLeft,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            anim.Completed += (s, e) =>
            {
                this.BeginAnimation(Window.LeftProperty, null);
                this.Left = _dockedLeft;
                _isAnimating = false;
                _isSidebarVisible = true;
                try
                {
                    webView.Focus();
                    PostJsonToWeb(new { type = "focus_input" });
                }
                catch { }
            };

            this.BeginAnimation(Window.LeftProperty, anim);
        }

        public void SlideOut()
        {
            if (_isAnimating || !_isSidebarVisible) return;
            _isAnimating = true;

            PositionWindow(offscreen: false);

            var anim = new DoubleAnimation
            {
                From = this.Left,
                To = _targetRight,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            anim.Completed += (s, e) =>
            {
                this.BeginAnimation(Window.LeftProperty, null);
                this.Left = _targetRight;
                _isAnimating = false;
                _isSidebarVisible = false;
                this.Hide();
            };

            this.BeginAnimation(Window.LeftProperty, anim);
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log"), $"[{DateTime.Now:HH:mm:ss}] Deactivated: IsPinned={_settings.IsPinned}, IsVisible={_isSidebarVisible}, IsAnimating={_isAnimating}\n"); } catch { }
            if (!_settings.IsPinned && _isSidebarVisible && !_isAnimating)
            {
                SlideOut();
            }
        }

        private void HandleWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                HandleClientMessage(doc.RootElement, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling web message: {ex.Message}");
            }
        }

        /// <summary>
        /// Single entry point for UI messages. clientId is null for the desktop WebView and set for phones;
        /// phones may not drive native-only actions (folder dialog, pinning, hiding the window, remote settings).
        /// </summary>
        private void HandleClientMessage(JsonElement root, string? clientId)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp)) return;
            bool remote = clientId != null;

            try
            {
                switch (typeProp.GetString())
                {
                    case "ready":
                        SendSnapshot(clientId);
                        break;

                    case "send_prompt":
                        string prompt = GetString(root, "text").Trim();
                        if (prompt.Length > 0) HandlePrompt(prompt, remote, clientId);
                        break;

                    case "stop_response":
                        StopResponse();
                        break;

                    case "job_stop":
                        if (root.TryGetProperty("id", out var jid) && jid.TryGetInt32(out int jobId)) _jobs.Stop(jobId);
                        break;

                    case "job_clear":
                        _jobs.ClearFinished();
                        PublishJobs();
                        break;

                    case "queue_remove":
                        if (root.TryGetProperty("id", out var qid) && qid.TryGetInt32(out int queueId))
                        {
                            _queue.RemoveAll(q => q.Id == queueId);
                            PublishQueue();
                        }
                        break;

                    case "queue_clear":
                        _queue.Clear();
                        PublishQueue();
                        break;

                    case "new_chat":
                        HandleNewConversation(showWindow: !remote);
                        break;

                    case "pick_folder" when !remote:
                        HandlePickFolder();
                        break;

                    case "toggle_pin" when !remote:
                        SetPinned(!_settings.IsPinned);
                        break;

                    case "close" when !remote:
                        SlideOut();
                        break;

                    case "list_projects":
                        _ = ListProjectsAsync(clientId);
                        break;

                    case "set_root":
                        string newRoot = GetString(root, "path");
                        if (Directory.Exists(newRoot))
                        {
                            _settings.ProjectsRoot = newRoot;
                            _settings.Save();
                            _ = ListProjectsAsync(clientId);
                        }
                        else
                        {
                            Reply(clientId, new { type = "toast", text = $"Folder not found: {newRoot}" });
                        }
                        break;

                    case "pin_project":
                        string pinPath = GetString(root, "path");
                        if (pinPath.Length > 0)
                        {
                            _settings.SetPinned(pinPath, root.TryGetProperty("pinned", out var pn) && pn.ValueKind == JsonValueKind.True);
                            _ = ListProjectsAsync(clientId);
                        }
                        break;

                    case "open_ide":
                        OpenInIde(GetString(root, "path"), clientId);
                        break;

                    case "set_cwd":
                        SwitchProject(GetString(root, "path"), clientId);
                        break;

                    case "no_project":
                        SwitchProject(AppSettings.HomeFolder, clientId);
                        break;

                    case "sync_start":
                        _ = StartSyncAsync(root, clientId);
                        break;

                    case "sync_cancel":
                        _sync.Cancel();
                        break;

                    // Everything below manages phone access and is desktop-only.
                    case "remote_info" when !remote:
                        PostJsonToWeb(BuildRemoteInfo());
                        break;

                    case "remote_toggle" when !remote:
                        SetRemoteEnabled(root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True);
                        break;

                    case "relay_test" when !remote:
                        _ = TestRelayAsync();
                        break;

                    case "remote_config" when !remote:
                        SaveRemoteConfig(GetString(root, "relayUrl"), GetString(root, "appUrl"));
                        break;

                    case "pair_begin" when !remote:
                        try
                        {
                            if (!_hub.IsRunning) SetRemoteEnabled(true);
                            if (_hub.IsRunning) _hub.BeginPairing();
                        }
                        catch (Exception ex) { PostJsonToWeb(new { type = "toast", text = ex.Message }); }
                        PostJsonToWeb(BuildRemoteInfo());
                        break;

                    case "pair_cancel" when !remote:
                        _hub.EndPairing();
                        break;

                    case "pair_approve" when !remote:
                        _ = _hub.ApproveAsync(GetString(root, "id"));
                        break;

                    case "pair_deny" when !remote:
                        _ = _hub.DenyAsync(GetString(root, "id"));
                        break;

                    case "device_revoke" when !remote:
                        _hub.Revoke(GetString(root, "id"));
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling client message: {ex.Message}");
            }
        }

        private static string GetString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

        // ---- Projects & review ------------------------------------------------------------

        private async Task ListProjectsAsync(string? clientId)
        {
            string rootDir = _settings.ProjectsRoot;
            var projects = await Task.Run(() =>
            {
                var list = ProjectScanner.Scan(rootDir);
                // Hand-picked folders outside the scanned root stay reachable.
                foreach (string r in _settings.RecentFolders.ToList())
                {
                    if (Directory.Exists(r) && !list.Any(p => string.Equals(p.Path, r, StringComparison.OrdinalIgnoreCase)))
                        list.Add(ProjectScanner.Describe(r));
                }
                return list;
            });
            Reply(clientId, new
            {
                type = "projects",
                root = rootDir,
                current = _agyManager.CurrentWorkingDirectory,
                projects = projects.Select(p => new
                {
                    name = p.Name,
                    path = p.Path,
                    kind = p.Kind,
                    hasReadme = p.HasReadme,
                    hasAgentsDoc = p.HasAgentsDoc,
                    hasSkills = p.HasSkills,
                    modified = p.LastModifiedUtc,
                    pinned = _settings.PinnedProjects.Contains(AppSettings.ProjectKey(p.Path)),
                    uses = _settings.ProjectUses.GetValueOrDefault(AppSettings.ProjectKey(p.Path)),
                    lastUsed = _settings.ProjectLastUsed.TryGetValue(AppSettings.ProjectKey(p.Path), out var lu) ? lu : (DateTime?)null
                })
            });
        }

        // ---- Open in IDE -------------------------------------------------------------------

        private static string? LocateIdeLauncher()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string name in new[] { "Antigravity IDE", "Antigravity" })
            {
                string candidate = Path.Combine(local, "Programs", name, "bin", "antigravity.cmd");
                if (File.Exists(candidate)) return candidate;
            }

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "antigravity.cmd");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Opens a project folder in the Antigravity IDE directly (no AI round-trips).</summary>
        private void OpenInIde(string path, string? clientId)
        {
            if (string.IsNullOrWhiteSpace(path)) path = _agyManager.CurrentWorkingDirectory;

            bool allowed = Directory.Exists(path);
            if (allowed && clientId != null)
            {
                // Phones may only open folders under the projects root, or the current project.
                string full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
                string rootFull = Path.GetFullPath(_settings.ProjectsRoot).TrimEnd('\\') + "\\";
                string cwdFull = Path.GetFullPath(_agyManager.CurrentWorkingDirectory).TrimEnd('\\') + "\\";
                allowed = full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(full, cwdFull, StringComparison.OrdinalIgnoreCase);
            }
            // The launcher is a .cmd file: refuse anything cmd.exe would treat as a command separator.
            if (!allowed || path.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '"', '!' }) >= 0)
            {
                Reply(clientId, new { type = "toast", text = "That folder cannot be opened." });
                return;
            }

            string? launcher = LocateIdeLauncher();
            if (launcher == null)
            {
                Reply(clientId, new { type = "toast", text = "Antigravity IDE was not found on this PC." });
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = launcher,
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Reply(clientId, new { type = "toast", text = $"Opening {Path.GetFileName(path.TrimEnd('\\'))} in Antigravity IDE…" });
            }
            catch (Exception ex)
            {
                Reply(clientId, new { type = "toast", text = $"Could not start the IDE: {ex.Message}" });
            }
        }

        private async Task StartSyncAsync(JsonElement root, string? clientId)
        {
            if (_sync.IsRunning)
            {
                Reply(clientId, new { type = "toast", text = "A project review is already running." });
                return;
            }

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in paths.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String) wanted.Add(p.GetString() ?? "");
            }

            // Only folders the scanner itself finds under the configured root can be reviewed.
            string rootDir = _settings.ProjectsRoot;
            var known = await Task.Run(() => ProjectScanner.Scan(rootDir));
            var selected = known.Where(k => wanted.Contains(k.Path)).ToList();
            if (selected.Count == 0)
            {
                Reply(clientId, new { type = "toast", text = "No valid projects selected." });
                return;
            }

            string mode = GetString(root, "mode");
            _trayManager.ShowNotification("Levity", $"Reviewing {selected.Count} project(s)...");
            await _sync.RunAsync(selected, mode, _settings.SyncPrompt);

            int ok = _sync.Items.Count(i => i.Status == "done");
            _trayManager.ShowNotification("Levity", $"Project review finished: {ok}/{_sync.Items.Count} done.");
        }

        private void PublishSyncState()
        {
            var state = new
            {
                type = "sync_state",
                running = _sync.IsRunning,
                mode = _sync.Mode,
                items = _sync.Items
            };
            _lastSyncJson = JsonSerializer.Serialize(state);
            RawPublish(_lastSyncJson);
        }

        private bool SwitchProject(string path, string? clientId)
        {
            bool allowed = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            if (allowed && clientId != null)
            {
                // Phones may switch to folders under the projects root, folders already picked on the PC, or the home folder (no project).
                static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\') + "\\";
                string full = Norm(path);
                allowed = full.StartsWith(Norm(_settings.ProjectsRoot), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, Norm(AppSettings.HomeFolder), StringComparison.OrdinalIgnoreCase)
                    || _settings.RecentFolders.Any(r => string.Equals(full, Norm(r), StringComparison.OrdinalIgnoreCase));
            }
            if (!allowed)
            {
                Reply(clientId, new { type = "toast", text = "That folder is not available." });
                return false;
            }

            _settings.WorkingDirectory = path;
            _settings.AddRecentFolder(path);
            _settings.RecordProjectUse(path);
            _agyManager.Restart(path);
            ClearHistory();
            Publish(new { type = "config", cwd = path, isPinned = _settings.IsPinned });
            Publish(new { type = "clear_chat" });
            return true;
        }

        // ---- Prompts: queue, direct commands, background jobs, stop -----------------------

        private record QueuedPrompt(int Id, string Text, bool Remote, string? ClientId);

        private readonly List<QueuedPrompt> _queue = new();
        private readonly JobManager _jobs = new();
        private int _nextQueueId = 1;
        private int _jobsPending;
        private string? _lastQueueJson;
        private string? _lastJobsJson;
        private bool _inlineShell;

        private bool TurnBusy => _agyManager.IsBusy || _inlineShell;

        /// <summary>Entry point for every typed message: runs it now, or queues it while a turn is in progress.</summary>
        private void HandlePrompt(string prompt, bool remote, string? clientId)
        {
            if (TurnBusy)
            {
                _queue.Add(new QueuedPrompt(_nextQueueId++, prompt, remote, clientId));
                PublishQueue();
                return;
            }
            RunPrompt(prompt, remote, clientId);
        }

        private void RunPrompt(string prompt, bool remote, string? clientId)
        {
            if (CommandShortcut.TryParse(prompt, out string shellCmd, out string shellAfter, out bool shellDirect) && shellDirect && shellAfter.Length == 0)
            {
                // /cmd{$...}: run it directly, no AI, no tokens.
                _ = RunDirectCommandAsync(prompt, shellCmd, remote, clientId);
                return;
            }
            _settings.RecordProjectUse(_agyManager.CurrentWorkingDirectory);
            Publish(new { type = "user_message", text = prompt, source = remote ? "mobile" : "desktop" });
            _agyManager.SendPrompt(CommandShortcut.Expand(prompt, _agyManager.CurrentWorkingDirectory));
        }

        /// <summary>Starts the next queued message once the current turn is over.</summary>
        private void DrainQueue()
        {
            if (TurnBusy || _queue.Count == 0) return;
            var next = _queue[0];
            _queue.RemoveAt(0);
            PublishQueue();
            RunPrompt(next.Text, next.Remote, next.ClientId);
        }

        private void PublishQueue()
        {
            _lastQueueJson = JsonSerializer.Serialize(new { type = "queue", items = _queue.Select(q => new { id = q.Id, text = q.Text }) });
            RawPublish(_lastQueueJson);
        }

        /// <summary>Cancels the AI's current response. The conversation is kept; queued messages carry on.</summary>
        private void StopResponse()
        {
            if (!_agyManager.IsBusy) return;
            _agyManager.Interrupt();
            FinishDirectTurn("_Stopped._", 0, cancelled: true);
            Dispatcher.BeginInvoke(new Action(DrainQueue), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void PublishJobs()
        {
            _lastJobsJson = JsonSerializer.Serialize(new
            {
                type = "jobs",
                jobs = _jobs.Background.Select(j => new
                {
                    id = j.Id,
                    command = j.Command,
                    cwd = j.Cwd,
                    state = j.State.ToString().ToLowerInvariant(),
                    exitCode = j.ExitCode,
                    started = j.StartedUtc,
                    ended = j.EndedUtc,
                    tail = j.Tail(8000)
                })
            });
            RawPublish(_lastJobsJson);
        }

        /// <summary>Output arrives line by line; coalesce into at most one update per 300 ms.</summary>
        private void ScheduleJobsPublish()
        {
            if (Interlocked.Exchange(ref _jobsPending, 1) == 1) return;
            _ = Task.Delay(300).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _jobsPending, 0);
                PublishJobs();
            })));
        }

        /// <summary>
        /// Runs "/cmd{$...}" in PowerShell without the AI and shows it as a normal chat turn. "cd" changes the working folder.
        /// A command still running after 3 s moves to the Jobs panel so the chat stays free.
        /// </summary>
        private async Task RunDirectCommandAsync(string typed, string command, bool remote, string? clientId)
        {
            _inlineShell = true;
            string source = remote ? "mobile" : "desktop";
            try
            {
                string cwd = _agyManager.CurrentWorkingDirectory;
                bool forceBackground = DirectShell.StripBackgroundFlag(command, out command);
                string? cdTarget = DirectShell.ResolveCd(command, cwd);
                if (cdTarget != null)
                {
                    if (!Directory.Exists(cdTarget))
                    {
                        Publish(new { type = "user_message", text = typed, source });
                        FinishDirectTurn($"Folder not found: `{cdTarget}`", 0, error: true);
                        return;
                    }
                    if (!SwitchProject(cdTarget, clientId)) return;
                    Publish(new { type = "user_message", text = typed, source });
                    FinishDirectTurn($"Now working in `{cdTarget}`", 0);
                    return;
                }

                Publish(new { type = "user_message", text = typed, source });
                RawPublish(JsonSerializer.Serialize(new { type = "status", text = "Running command...", state = "busy" }));
                var job = _jobs.Start(command, cwd);

                if (forceBackground || !await job.WaitAsync(TimeSpan.FromSeconds(3)))
                {
                    job.Background = true;
                    PublishJobs();
                    FinishDirectTurn($"{(forceBackground ? "Started in the background as" : "Still running, so it moved to the background as")} **job #{job.Id}**. Watch it or stop it from the **Jobs** button in the header. Chat is free.", 0);
                    return;
                }

                _jobs.Discard(job);
                string output = job.Tail(20000);
                string fence = output.Contains("```") ? "````" : "```";
                string body = output.Length == 0 ? "_(no output)_" : $"{fence}\n{output}\n{fence}";
                FinishDirectTurn($"{body}\n\n**Exit code:** `{job.ExitCode}`", (job.EndedUtc!.Value - job.StartedUtc).TotalSeconds);
            }
            finally
            {
                _inlineShell = false;
                Dispatcher.BeginInvoke(new Action(DrainQueue), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void FinishDirectTurn(string markdown, double seconds, bool error = false, bool cancelled = false)
        {
            Publish(new { type = "agy_event", payload = new { @event = "step_update", step_update = new { step_type = "agent_response", state = "DONE", text_delta = markdown } } });
            Publish(new { type = "agy_event", payload = new { @event = "result", result = new { status = error ? "ERROR" : cancelled ? "CANCELLED" : "DONE", error = error ? "Command not run." : "", duration_seconds = seconds } } });
            RawPublish(JsonSerializer.Serialize(new { type = "status", text = "Ready", state = "online" }));
        }

        // ---- Mobile access (relay + device pairing) ---------------------------------------

        private string? _qrCacheKey;
        private string? _qrCacheSvg;

        private void SetRemoteEnabled(bool enabled)
        {
            if (enabled && (string.IsNullOrWhiteSpace(_settings.RelayUrl) || string.IsNullOrWhiteSpace(_settings.MobileAppUrl)))
            {
                PostJsonToWeb(new { type = "toast", text = "Set the relay URL and app URL first." });
                enabled = false;
            }

            _settings.RemoteEnabled = enabled;
            _settings.Save();
            if (enabled) _hub.Start(); else _hub.Stop();
            PostJsonToWeb(BuildRemoteInfo());
        }

        private async Task TestRelayAsync()
        {
            string relay = _settings.RelayUrl ?? "";
            if (relay.Length == 0)
            {
                PostJsonToWeb(new { type = "relay_test_result", ok = false, ms = 0L, error = "Set the relay address first." });
                return;
            }

            var (ok, ms, error) = await MobileHub.TestAsync(relay);
            PostJsonToWeb(new { type = "relay_test_result", ok, ms, error });
        }

        private void SaveRemoteConfig(string relayUrl, string appUrl)
        {
            relayUrl = relayUrl.Trim().TrimEnd('/');
            appUrl = appUrl.Trim().TrimEnd('/');

            // Cleartext is only allowed for a relay/app running on this machine (local development).
            static bool Ok(string url, string secure, string insecure) =>
                Uri.TryCreate(url, UriKind.Absolute, out var u)
                && (u.Scheme == secure || (u.Scheme == insecure && (u.IsLoopback)));

            if (!Ok(relayUrl, "wss", "ws") || !Ok(appUrl, "https", "http"))
            {
                PostJsonToWeb(new { type = "toast", text = "Relay must be wss://… and the app https://… (http/ws only for localhost)." });
                return;
            }

            _settings.RelayUrl = relayUrl;
            _settings.MobileAppUrl = appUrl;
            _settings.Save();
            if (_settings.RemoteEnabled) { _hub.Stop(); _hub.Start(); }
            else { SetRemoteEnabled(true); return; }
            PostJsonToWeb(BuildRemoteInfo());
        }

        private object BuildRemoteInfo()
        {
            object? pairing = null;
            if (_hub.PairingKey != null && _hub.PairingExpiresUtc != null && !string.IsNullOrEmpty(_settings.MobileAppUrl))
            {
                // ":" and "/" are legal in a fragment, so the relay address stays unescaped: a shorter link means a simpler QR.
                string url = $"{_settings.MobileAppUrl}/#p={_hub.PairingKey}&r={_settings.RelayUrl}";
                if (_qrCacheKey != url) { _qrCacheSvg = MakeQrSvg(url); _qrCacheKey = url; }
                pairing = new { qr = _qrCacheSvg, expiresAt = _hub.PairingExpiresUtc };
            }

            var (relayState, relayError) = _hub.Health();

            return new
            {
                type = "remote_info",
                relay = new { state = relayState, error = relayError },
                enabled = _hub.IsRunning,
                configured = !string.IsNullOrWhiteSpace(_settings.RelayUrl) && !string.IsNullOrWhiteSpace(_settings.MobileAppUrl),
                relayUrl = _settings.RelayUrl ?? "",
                appUrl = _settings.MobileAppUrl ?? "",
                pairing,
                pending = _hub.PendingRequests.Select(r => new { id = r.Id, name = r.Name, code = r.Code }),
                devices = _hub.DeviceStates().Select(x => new
                {
                    id = x.Device.Id,
                    name = x.Device.Name,
                    connected = x.Connected,
                    phones = x.Phones,
                    created = x.Device.CreatedUtc,
                    lastSeen = x.Device.LastSeenUtc
                })
            };
        }

        /// <summary>
        /// Draws the QR ourselves so the viewBox is exactly square with an even quiet zone on all four sides
        /// (the library's SVG sizing left uneven white space).
        /// </summary>
        private static string MakeQrSvg(string text)
        {
            const int quiet = 4; // modules of white border required by the QR spec
            using var generator = new QRCoder.QRCodeGenerator();
            // Level L: shown on a screen, so redundancy for print damage is not needed and fewer modules scan faster.
            using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.L);

            var matrix = data.ModuleMatrix;
            int n = matrix.Count;
            int size = n + quiet * 2;

            var sb = new System.Text.StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\">");
            sb.Append($"<rect width=\"{size}\" height=\"{size}\" fill=\"#ffffff\"/><path fill=\"#09090b\" d=\"");
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (matrix[y][x]) sb.Append($"M{x + quiet},{y + quiet}h1v1h-1z");
            sb.Append("\"/></svg>");
            return sb.ToString();
        }

        // ---- Chat plumbing ----------------------------------------------------------------

        private void SendSnapshot(string? clientId)
        {
            Reply(clientId, new
            {
                type = "config",
                cwd = _agyManager.CurrentWorkingDirectory,
                isPinned = _settings.IsPinned
            });
            if (_lastStatusJson != null) ReplyRaw(clientId, _lastStatusJson);
            if (_lastSyncJson != null) ReplyRaw(clientId, _lastSyncJson);
            if (_lastQueueJson != null) ReplyRaw(clientId, _lastQueueJson);
            if (_lastJobsJson != null) ReplyRaw(clientId, _lastJobsJson);
            var lines = _history.ToArray();
            if (clientId == null)
            {
                foreach (string line in lines) ReplyRaw(null, line);
            }
            else
            {
                for (int i = 0; i < lines.Length; i += 150)
                    ReplyRaw(clientId, $"{{\"type\":\"batch\",\"items\":[{string.Join(",", lines.Skip(i).Take(150))}]}}");
            }
        }

        private void OpenPanel(string panel)
        {
            SlideIn();
            PostJsonToWeb(new { type = "open_panel", panel });
        }

        private void HandleNewConversation(bool showWindow = true)
        {
            _agyManager.Restart();
            ClearHistory();
            Publish(new { type = "clear_chat" });
            if (showWindow) SlideIn();
        }

        private void ClearHistory() => _history.Clear();

        private void HandlePickFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose a working folder for Levity (any folder)",
                UseDescriptionForTitle = true,
                SelectedPath = _agyManager.CurrentWorkingDirectory
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                SwitchProject(dialog.SelectedPath, null);
                _trayManager.ShowNotification("Levity", $"Switched to project:\n{dialog.SelectedPath}");
            }
        }

        private void SetPinned(bool pinned)
        {
            _settings.IsPinned = pinned;
            _settings.Save();
            _trayManager.UpdatePinState(pinned);

            Publish(new
            {
                type = "config",
                cwd = _agyManager.CurrentWorkingDirectory,
                isPinned = pinned
            });
        }

        private void HandleAgyEvent(string rawJson)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    Publish(new { type = "agy_event", payload = doc.RootElement });
                }
                catch { }
            });
        }

        private void HandleAgyStatus(string statusText)
        {
            Dispatcher.Invoke(() =>
            {
                string state = "online";
                if (statusText.Contains("Thinking") || statusText.Contains("Respond"))
                {
                    state = "busy";
                }
                else if (statusText.Contains("Error") || statusText.Contains("Stopped"))
                {
                    state = "error";
                }

                string json = JsonSerializer.Serialize(new { type = "status", text = statusText, state });
                _lastStatusJson = json;
                RawPublish(json);
                // After the turn's result event has been shown, start the next queued message.
                if (statusText == "Ready") Dispatcher.BeginInvoke(new Action(DrainQueue), System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        private void HandleAgyError(string errorText)
        {
            System.Diagnostics.Debug.WriteLine($"[AGY ERR] {errorText}");
        }

        /// <summary>Send to the desktop WebView and every connected phone; chat events are kept for late joiners.</summary>
        private void Publish(object obj)
        {
            string json = JsonSerializer.Serialize(obj);
            if (json.StartsWith("{\"type\":\"agy_event\"") || json.StartsWith("{\"type\":\"user_message\""))
            {
                _history.Add(json);
                if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
            }
            RawPublish(json);
        }

        private void RawPublish(string json)
        {
            PostRawToWeb(json);
            if (_hub.IsRunning) _ = _hub.BroadcastAsync(json);
        }

        private void Reply(string? clientId, object obj) => ReplyRaw(clientId, JsonSerializer.Serialize(obj));

        private void ReplyRaw(string? clientId, string json)
        {
            if (clientId == null) PostRawToWeb(json);
            else _ = _hub.SendAsync(clientId, json);
        }

        private void PostJsonToWeb(object obj) => PostRawToWeb(JsonSerializer.Serialize(obj));

        private void PostRawToWeb(string json)
        {
            try
            {
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.PostWebMessageAsJson(json);
                }
            }
            catch { }
        }

        private void ShutdownApplication()
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log"), $"[{DateTime.Now:HH:mm:ss}] ShutdownApplication called! Stack:\n{Environment.StackTrace}\n"); } catch { }
            _sync.Cancel();
            _agyManager?.Stop();
            try { _hub.Dispose(); } catch { }
            try { _jobs.Dispose(); } catch { }
            _hotkeyManager?.Dispose();
            _trayManager?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log"), $"[{DateTime.Now:HH:mm:ss}] OnClosed called!\n"); } catch { }
            ShutdownApplication();
            base.OnClosed(e);
        }
    }
}
