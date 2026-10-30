using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Levity.Services;
using Levity.Services.WindowsTools;
using Microsoft.Web.WebView2.Core;

namespace Levity
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
            _push = new PushService(AppSettings.DataDir);
            this.Deactivated += MainWindow_Deactivated;
        }

        public void InitializeSidebar()
        {
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
            try { RollingLog.AppendTo(log, "[TRACE 1] In InitializeSidebar\n"); } catch { }

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            _trayManager = new TrayIconManager(iconPath, _settings.IsPinned, _settings.LaunchOnStartup);
            try { RollingLog.AppendTo(log, "[TRACE 2] TrayManager created\n"); } catch { }

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
            _diagrams.Progress += p => Dispatcher.BeginInvoke(new Action(() => PublishDiagramState(p.Root, p.Phase, p.Message)));
            _jobs.BackgroundFinished += j => Dispatcher.BeginInvoke(new Action(() =>
                { RawPublish(JsonSerializer.Serialize(new { type = "toast", text = $"Job #{j.Id} {(j.State == JobState.Stopped ? "stopped" : j.ExitCode == 0 ? "finished" : $"failed (exit {j.ExitCode})")}" })); PushNews(); }));

            // Agy Process Manager
            _agyManager = new AgyProcessManager(_settings.WorkingDirectory!) { Effort = AgentLaunch.NormalizeEffort(_settings.Effort), Mode = AgentLaunch.NormalizeMode(_settings.Mode) };
            _agyManager.OnEventReceived += HandleAgyEvent;
            _agyManager.OnStatusChanged += HandleAgyStatus;
            _agyManager.OnConversationInitialized += id => Dispatcher.BeginInvoke(new Action(() => _sessionConvId = id));
            _sessionCwd = _agyManager.CurrentWorkingDirectory;
            _agyManager.OnErrorReceived += HandleAgyError;
            // Windows tools for the assistant: off until the user turns the switch on; if it is on, the server comes back on the same port.
            _toolHost = new ToolHost(_settings, AppSettings.DataDir, Environment.ProcessPath ?? "");
            _agyManager.PcFactsProvider = () => _settings.WindowsToolsEnabled && _toolHost.Toolbox.Profile.Peek() is { } p ? MachineProfile.Summary(p) : null;
            _ = ResumeToolsAsync();
            // The assistant is started by the first message, not at launch, and stopped again after a quiet spell (see Housekeeping).
            _lastStatusJson = JsonSerializer.Serialize(new { type = "status", text = "Ready", state = "online" });
            StartHousekeeping();
            try { RollingLog.AppendTo(log, "[TRACE 3] AgyManager prepared (starts on the first message)\n"); } catch { }

            if (_settings.RemoteEnabled) _hub.Start();

            // Setup WebView2
            InitializeWebViewAsync();
            try { RollingLog.AppendTo(log, "[TRACE 4] InitializeWebViewAsync triggered\n"); } catch { }

            // Auto slide in on startup so the sidebar is immediately visible to the user!
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SlideIn();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            try { RollingLog.AppendTo(log, "[TRACE 5] SlideIn dispatched\n"); } catch { }
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
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Levity", "WebView2");
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

        /// <summary>Fast or deep thinking. The assistant is restarted on the same conversation (context is kept); never in the middle of an answer.</summary>
        private void SetEffort(string value, string? clientId)
        {
            string effort = AgentLaunch.NormalizeEffort(value);
            if (effort == AgentLaunch.NormalizeEffort(_settings.Effort)) { Reply(clientId, new { type = "config", effort }); return; }
            if (TurnBusy)
            {
                Reply(clientId, new { type = "toast", text = "Switch speed after the current answer finishes." });
                Reply(clientId, new { type = "config", effort = AgentLaunch.NormalizeEffort(_settings.Effort) });
                return;
            }
            _settings.Effort = effort;
            _settings.Save();
            _agyManager.Effort = effort;
            _agyManager.Prepare(null, _agyManager.CurrentConversationId);   // same conversation, starts with the new level on the next message
            Publish(new { type = "config", effort });
        }

        /// <summary>Plan (the assistant only proposes, changing nothing) or normal. Same rules as the speed switch.</summary>
        private void SetMode(string value, string? clientId)
        {
            string mode = AgentLaunch.NormalizeMode(value);
            if (mode == AgentLaunch.NormalizeMode(_settings.Mode)) { Reply(clientId, new { type = "config", mode }); return; }
            if (TurnBusy)
            {
                Reply(clientId, new { type = "toast", text = "Switch after the current answer finishes." });
                Reply(clientId, new { type = "config", mode = AgentLaunch.NormalizeMode(_settings.Mode) });
                return;
            }
            _settings.Mode = mode;
            _settings.Save();
            _agyManager.Mode = mode;
            _agyManager.Prepare(null, _agyManager.CurrentConversationId);
            Publish(new { type = "config", mode });
        }

        private bool _diagramExpanded;
        private bool _diagramAutoPinned;

        /// <summary>Pins the sidebar while a diagram is open, and puts the pin back the way it was when the diagram closes.</summary>
        private void SetDiagramPin(bool on)
        {
            if (on)
            {
                if (_settings.IsPinned) return;   // already pinned by the user: leave it alone, and do not unpin it later
                _diagramAutoPinned = true;
                SetPinned(true);
            }
            else if (_diagramAutoPinned)
            {
                _diagramAutoPinned = false;
                SetPinned(false);
            }
        }

        /// <summary>Stretches the sidebar over the whole work area so a big diagram can be read, and puts it back afterwards.</summary>
        private void SetDiagramExpanded(bool on)
        {
            if (_isAnimating || !_isSidebarVisible) return;
            if (on == _diagramExpanded) return;
            _diagramExpanded = on;
            if (on)
            {
                var screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                this.Left = screen.WorkingArea.Left / dpiX;
                this.Width = screen.WorkingArea.Width / dpiX;
            }
            else
            {
                this.Width = _targetWidth;
                this.Left = _dockedLeft;
            }
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

            EnsureWebView();   // a page thrown away after a long hide is created again (the host replays the conversation into it)
            WakeWebView();     // before the window is shown, so the page is live when it appears
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

            if (_diagramExpanded)
            {
                _diagramExpanded = false;
                PostJsonToWeb(new { type = "diagram_collapsed" });   // the page was stretched; tell it the window is back to normal
            }
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
                _hiddenSinceUtc = DateTime.UtcNow;
                _trimmedThisHide = false;
                try { if (webView.CoreWebView2 != null) webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
            };

            this.BeginAnimation(Window.LeftProperty, anim);
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            try { RollingLog.Append("startup.log", $"[{DateTime.Now:HH:mm:ss}] Deactivated: IsPinned={_settings.IsPinned}, IsVisible={_isSidebarVisible}, IsAnimating={_isAnimating}\n"); } catch { }
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
                        if (clientId == null && _isSidebarVisible) PostJsonToWeb(new { type = "focus_input" });   // a rebuilt page has no focus yet
                        break;

                    case "send_prompt":
                        string prompt = GetString(root, "text").Trim();
                        List<string>? imagePaths = null;
                        if (root.TryGetProperty("images", out var imagesProp) && imagesProp.ValueKind == JsonValueKind.Array)
                        {
                            var incoming = new List<MobileImages.IncomingImage>();
                            foreach (var item in imagesProp.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object) continue;
                                string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                                string? type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                                string? data = item.TryGetProperty("data", out var d) ? d.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(data))
                                {
                                    incoming.Add(new MobileImages.IncomingImage(name, type, data));
                                }
                            }
                            if (incoming.Count > 0)
                            {
                                imagePaths = MobileImages.SaveIncomingImages(incoming);
                            }
                        }

                        if (prompt.Length > 0 || (imagePaths != null && imagePaths.Count > 0))
                        {
                            HandlePrompt(prompt, remote, clientId, imagePaths: imagePaths);
                        }
                        break;

                    case "list_sessions":
                        Reply(clientId, JsonDocument.Parse(SessionsJson()).RootElement);
                        break;

                    case "session_open":
                        OpenSession(GetString(root, "id"), clientId);
                        break;

                    case "session_delete":
                        DeleteSession(GetString(root, "id"), clientId);
                        break;

                    case "diagram_get":
                        _ = DiagramGetAsync(GetString(root, "path"), clientId);
                        break;

                    case "diagram_prepare":
                        _ = DiagramPrepareAsync(GetString(root, "path"), clientId);
                        break;

                    case "diagram_pin":
                        if (clientId == null)
                            SetDiagramPin(root.TryGetProperty("on", out var pinOn) && pinOn.ValueKind == JsonValueKind.True);
                        break;

                    case "diagram_expand":
                        if (clientId == null)   // only the sidebar itself: a phone cannot resize the PC window
                            SetDiagramExpanded(root.TryGetProperty("on", out var ex) && ex.ValueKind == JsonValueKind.True);
                        break;

                    case "diagram_generate":
                        _ = DiagramGenerateAsync(GetString(root, "path"), clientId);
                        break;

                    case "diagram_cancel":
                        _diagrams.Cancel();
                        break;

                    case "diagram_open":
                        if (root.TryGetProperty("node", out var dnode) && dnode.TryGetInt32(out int diagramNode))
                            _ = DiagramOpenAsync(GetString(root, "path"), diagramNode, clientId);
                        break;

                    case "diagram_save_docs":
                        _ = DiagramSaveDocsAsync(GetString(root, "path"), clientId);
                        break;

                    case "diagram_delete":
                        if (DiagramGuard(GetString(root, "path"), clientId))
                        {
                            _diagrams.Delete(GetString(root, "path"));
                            Reply(clientId, new { type = "diagram_none", path = DiagramStore.NormalizeRoot(GetString(root, "path")) });
                        }
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

                    case "checkpoint_undo":
                        _ = UndoCheckpointAsync(GetString(root, "id"), clientId);
                        break;

                    case "speed_get":
                        _ = PublishSpeedAsync(clientId);
                        break;

                    case "tools_get":
                        PublishToolsState(clientId);
                        break;

                    case "tools_set":
                        if (clientId == null) _ = SetToolsAsync(root.TryGetProperty("on", out var toolsOn) && toolsOn.ValueKind == JsonValueKind.True);
                        else Reply(clientId, new { type = "toast", text = "Turn the Windows tools on or off from the PC." });
                        break;

                    case "journal_list":
                        PublishJournal(clientId);
                        break;

                    case "journal_revert":
                    {
                        string? err = _toolHost.Toolbox.Journal.Revert(GetString(root, "id"));
                        Reply(clientId, new { type = "toast", text = err ?? "Change reverted." });
                        PublishJournal(null);
                        break;
                    }

                    case "doctor_scan":
                        _ = RunDoctorAsync(clientId);
                        break;

                    case "doctor_fix":
                        _ = RunDoctorFixAsync(clientId, GetString(root, "id"), root.TryGetProperty("index", out var dfi) && dfi.TryGetInt32(out int fixIndex) ? fixIndex : -1);
                        break;

                    case "machine_get":
                        _ = PublishMachineAsync(clientId, root.TryGetProperty("refresh", out var mr) && mr.ValueKind == JsonValueKind.True);
                        break;

                    case "job_input":
                        if (root.TryGetProperty("id", out var iid) && iid.TryGetInt32(out int inputJob))
                        {
                            bool enter = !root.TryGetProperty("enter", out var enterProp) || enterProp.ValueKind != JsonValueKind.False;
                            if (!_jobs.SendText(inputJob, GetString(root, "text"), enter)) Reply(clientId, new { type = "toast", text = "That job is not waiting for input." });
                        }
                        break;

                    case "job_key":
                        if (root.TryGetProperty("id", out var kid) && kid.TryGetInt32(out int keyJob))
                            _jobs.SendKey(keyJob, GetString(root, "key"));
                        break;

                    case "actions_get":
                        PublishActions(clientId);
                        break;

                    case "git_state":
                        _ = PublishGitStateAsync(clientId);
                        break;

                    case "run_script":
                        RunScriptButton(GetString(root, "name"), root.TryGetProperty("interactive", out var ri) && ri.ValueKind == JsonValueKind.True, remote, clientId);
                        break;

                    case "git_run":
                        GitButton(GetString(root, "action"), GetString(root, "message"), GetString(root, "name"),
                            root.TryGetProperty("stage", out var gs) && gs.ValueKind == JsonValueKind.True, remote, clientId);
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

                    case "set_effort":
                        SetEffort(GetString(root, "value"), clientId);
                        break;

                    case "set_mode":
                        SetMode(GetString(root, "value"), clientId);
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
                        if (_syncStarting) _syncCancelPending = true; // still scanning: nothing to kill yet, stop it before it starts
                        _sync.Cancel();
                        // Nothing is running on this PC (e.g. a screen left over from before a restart): tell every UI so it can leave "Starting".
                        if (!_sync.IsRunning && !_syncStarting) RawPublish(IdleSyncJson);
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
                        _push.Unsubscribe(GetString(root, "id"));
                        _hub.Revoke(GetString(root, "id"));
                        break;

                    case "presence" when clientId != null:
                        lock (_visibleClients) { if (root.TryGetProperty("visible", out var vis) && vis.ValueKind == JsonValueKind.True) _visibleClients.Add(clientId); else _visibleClients.Remove(clientId); }
                        break;

                    case "push_get" when clientId != null:
                        PublishPushState(clientId);
                        break;

                    case "push_subscribe" when clientId != null:
                    {
                        string? err = _push.Subscribe(DeviceOf(clientId), GetString(root, "endpoint"));
                        if (err != null) Reply(clientId, new { type = "toast", text = err });
                        PublishPushState(clientId);
                        break;
                    }

                    case "push_unsubscribe" when clientId != null:
                        _push.Unsubscribe(DeviceOf(clientId));
                        PublishPushState(clientId);
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
            string? overridePath = DotEnv.Get("ANTIGRAVITY_IDE_PATH");
            if (overridePath != null && File.Exists(overridePath)) return overridePath;

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
            // Phones may only open folders under the projects root, or the current project.
            if (allowed && clientId != null) allowed = FolderAccess.PhoneMayOpenInIde(path, _settings.ProjectsRoot, _agyManager.CurrentWorkingDirectory);
            if (!allowed)
            {
                Reply(clientId, new { type = "toast", text = "That folder cannot be opened." });
                return;
            }
            LaunchIde(path, clientId);
        }

        /// <summary>Starts the IDE on a folder or file. The launcher is a .cmd file, so anything cmd.exe would treat as a command separator is refused.</summary>
        private void LaunchIde(string path, string? clientId)
        {
            if (path.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '"', '!' }) >= 0)
            {
                Reply(clientId, new { type = "toast", text = "That path cannot be opened." });
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
                Reply(clientId, new { type = "toast", text = $"Opening {Path.GetFileName(path.TrimEnd('\\'))} in Antigravity IDE..." });
            }
            catch (Exception ex)
            {
                Reply(clientId, new { type = "toast", text = $"Could not start the IDE: {ex.Message}" });
            }
        }

        private const string IdleSyncJson = "{\"type\":\"sync_state\",\"running\":false,\"mode\":\"update\",\"items\":[]}";
        private bool _syncStarting;
        private bool _syncCancelPending;

        private async Task StartSyncAsync(JsonElement root, string? clientId)
        {
            if (_sync.IsRunning || _syncStarting)
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
            _syncStarting = true;
            _syncCancelPending = false;
            List<ProjectInfo> selected;
            try
            {
                string rootDir = _settings.ProjectsRoot;
                var known = await Task.Run(() => ProjectScanner.Scan(rootDir));
                selected = known.Where(k => wanted.Contains(k.Path)).ToList();
                if (_syncCancelPending)
                {
                    // Stopped before the first project began: report every project as cancelled so the UI leaves "Starting".
                    _lastSyncJson = JsonSerializer.Serialize(new
                    {
                        type = "sync_state",
                        running = false,
                        mode = GetString(root, "mode"),
                        items = selected.Select(p => new SyncItem { Name = p.Name, Path = p.Path, Status = "cancelled" })
                    });
                    RawPublish(_lastSyncJson);
                    return;
                }
            }
            finally { _syncStarting = false; }

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
            _agyManager.Prepare(path, null);
            ClearHistory();
            Publish(new { type = "config", cwd = path, isPinned = _settings.IsPinned });
            PublishActions(null);
            Publish(new { type = "clear_chat" });
            return true;
        }

        // ---- Chat sessions: saved on this PC, reopenable from the PC or a phone ------------

        private readonly SessionStore _sessions = new();
        private string _sessionId = Guid.NewGuid().ToString("N");
        private string _sessionCwd = "";
        private string? _sessionConvId;
        private DateTime _sessionCreated = DateTime.UtcNow;
        private System.Windows.Threading.DispatcherTimer? _saveTimer;

        private static string SessionTitle(IEnumerable<string> history)
        {
            foreach (string line in history)
            {
                if (!line.StartsWith("{\"type\":\"user_message\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    string text = doc.RootElement.GetProperty("text").GetString() ?? "";
                    text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
                }
                catch { }
            }
            return "";
        }

        /// <summary>Writes the open chat to disk shortly after it changes (a chat with no user message is never saved).</summary>
        private void ScheduleSessionSave()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _saveTimer.Tick -= OnSaveTick;
                _saveTimer.Tick += OnSaveTick;
                _saveTimer.Stop();
                _saveTimer.Start();
            }));
        }

        private void OnSaveTick(object? sender, EventArgs e)
        {
            _saveTimer?.Stop();
            SaveSession();
        }

        private void SaveSession()
        {
            _saveTimer?.Stop();
            string title = SessionTitle(_history);
            if (title.Length == 0) return;
            _sessions.Save(new ChatSession
            {
                Id = _sessionId,
                Title = title,
                Cwd = _sessionCwd,
                ConversationId = _sessionConvId,
                Created = _sessionCreated,
                Updated = DateTime.UtcNow,
                Messages = _history.Count(l => l.StartsWith("{\"type\":\"user_message\"")),
                History = _history.ToList()
            });
            PublishSessions();
        }

        /// <summary>Saves the open chat, then starts a fresh, empty one in the current working folder.</summary>
        private void BeginNewSession()
        {
            SaveSession();
            _history.Clear();
            _sessionId = Guid.NewGuid().ToString("N");
            _sessionConvId = null;
            _sessionCreated = DateTime.UtcNow;
            _sessionCwd = _agyManager.CurrentWorkingDirectory;
        }

        private string SessionsJson() => JsonSerializer.Serialize(new
        {
            type = "sessions",
            current = _sessionId,
            sessions = _sessions.List().Select(s => new
            {
                id = s.Id,
                title = s.Title,
                cwd = s.Cwd,
                updated = s.Updated,
                messages = s.Messages
            })
        });

        private void PublishSessions() => RawPublish(SessionsJson());

        private void OpenSession(string id, string? clientId)
        {
            if (TurnBusy)
            {
                Reply(clientId, new { type = "toast", text = "Wait for the current turn to finish, or stop it, first." });
                return;
            }
            if (id == _sessionId)
            {
                Reply(clientId, new { type = "toast", text = "That chat is already open." });
                return;
            }
            var s = _sessions.Load(id);
            if (s == null)
            {
                Reply(clientId, new { type = "toast", text = "That chat could not be loaded." });
                return;
            }

            SaveSession();
            string cwd = Directory.Exists(s.Cwd) ? s.Cwd : _agyManager.CurrentWorkingDirectory;
            _settings.WorkingDirectory = cwd;
            _settings.Save();
            _queue.Clear();
            PublishQueue();
            _agyManager.Prepare(cwd, s.ConversationId); // same agy conversation (started by the next message), so the AI keeps its context

            _history.Clear();
            _history.AddRange(s.History);
            _sessionId = s.Id;
            _sessionCwd = cwd;
            _sessionConvId = s.ConversationId;
            _sessionCreated = s.Created;

            Publish(new { type = "config", cwd, isPinned = _settings.IsPinned });
            PublishActions(null);
            RawPublish("{\"type\":\"clear_chat\"}");
            var lines = _history.ToArray();
            foreach (string line in lines) PostRawToWeb(line);
            if (_hub.IsRunning)
            {
                for (int i = 0; i < lines.Length; i += 150)
                    _ = _hub.BroadcastAsync($"{{\"type\":\"batch\",\"items\":[{string.Join(",", lines.Skip(i).Take(150))}]}}");
            }
            PublishSessions();
        }

        private void DeleteSession(string id, string? clientId)
        {
            if (id == _sessionId)
            {
                Reply(clientId, new { type = "toast", text = "This chat is open. Start a new chat, then delete it." });
                return;
            }
            _sessions.Delete(id);
            PublishSessions();
        }

        // ---- Architecture diagrams -----------------------------------------------------------

        private readonly DiagramService _diagrams = new(new AgyDiagramRunner(), new DiagramStore());

        /// <summary>At the PC any existing folder is fine; a phone may only use the folders it may already switch to.</summary>
        private bool DiagramFolderAllowed(string path, string? clientId)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Directory.Exists(path)) return false;
            if (clientId == null) return true;
            return FolderAccess.PhoneMayUseForDiagram(path, _settings.ProjectsRoot, AppSettings.HomeFolder, _agyManager.CurrentWorkingDirectory, _settings.RecentFolders);
        }

        private bool DiagramGuard(string path, string? clientId)
        {
            if (DiagramFolderAllowed(path, clientId)) return true;
            Reply(clientId, new { type = "toast", text = "That folder is not available." });
            return false;
        }

        private void PublishDiagramState(string path, string phase, string message) =>
            RawPublish(JsonSerializer.Serialize(new { type = "diagram_state", path = DiagramStore.NormalizeRoot(path), phase, message }));

        private async Task DiagramGetAsync(string path, string? clientId)
        {
            if (!DiagramGuard(path, clientId)) return;
            try
            {
                var result = await _diagrams.GetAsync(path);
                if (result != null) Reply(clientId, DiagramPayload.Build(result));
                else Reply(clientId, new { type = "diagram_none", path = DiagramStore.NormalizeRoot(path) });
            }
            catch { Reply(clientId, new { type = "toast", text = "Could not read the saved diagram." }); }
            if (_diagrams.IsRunning && _diagrams.RunningRoot != null)
                Reply(clientId, new { type = "diagram_state", path = _diagrams.RunningRoot, phase = "generating", message = "A diagram is being generated..." });
        }

        /// <summary>Tells the user what would be sent before anything is.</summary>
        private async Task DiagramPrepareAsync(string path, string? clientId)
        {
            if (!DiagramFolderAllowed(path, clientId))
            {
                Reply(clientId, new { type = "diagram_state", path = DiagramStore.NormalizeRoot(path), phase = "error", message = "That folder is not available." });
                return;
            }
            try
            {
                var (stats, chars) = await _diagrams.PrepareAsync(path);
                Reply(clientId, new
                {
                    type = "diagram_prepared", path = DiagramStore.NormalizeRoot(path),
                    files = stats.Files, excerpts = stats.ExcerptFiles, indexed = stats.IndexedFiles,
                    redactions = stats.Redactions, truncated = stats.Truncated, kb = (chars + 1023) / 1024,
                });
            }
            catch { Reply(clientId, new { type = "diagram_state", path = DiagramStore.NormalizeRoot(path), phase = "error", message = "Could not read that folder." }); }
        }

        private async Task DiagramGenerateAsync(string path, string? clientId)
        {
            if (!DiagramGuard(path, clientId)) return;
            if (_diagrams.IsRunning)
            {
                Reply(clientId, new { type = "toast", text = DiagramService.BusyMessage });
                return;
            }
            PublishDiagramState(path, "ingest", "Reading the project...");
            try
            {
                var (result, error) = await _diagrams.GenerateAsync(path);
                if (result != null) RawPublish(JsonSerializer.Serialize(DiagramPayload.Build(result)));
                else if (error == DiagramService.BusyMessage) Reply(clientId, new { type = "toast", text = error });
                else PublishDiagramState(path, "error", error ?? "The diagram could not be made.");
            }
            catch (OperationCanceledException) { PublishDiagramState(path, "cancelled", "Stopped."); }
            catch { PublishDiagramState(path, "error", "The diagram could not be made."); }
        }

        /// <summary>Opens the file behind a clicked node. The client sends a node number; the path comes from the validated diagram on the PC.</summary>
        private async Task DiagramOpenAsync(string path, int node, string? clientId)
        {
            if (!DiagramGuard(path, clientId)) return;
            var result = await _diagrams.GetAsync(path);
            var n = result?.Compiled.Nodes.FirstOrDefault(x => x.Index == node);
            if (result == null || n?.Path == null)
            {
                Reply(clientId, new { type = "toast", text = "That component has no file to open." });
                return;
            }
            string? full = DiagramIngest.SafeFullPath(result.Root, n.Path);
            if (full == null || (!File.Exists(full) && !Directory.Exists(full)))
            {
                Reply(clientId, new { type = "toast", text = "That file no longer exists." });
                return;
            }
            LaunchIde(full, clientId);
        }

        /// <summary>Writes the diagram into docs/ARCHITECTURE.md, replacing only the generated section of an existing file.</summary>
        private async Task DiagramSaveDocsAsync(string path, string? clientId)
        {
            if (!DiagramGuard(path, clientId)) return;
            var result = await _diagrams.GetAsync(path);
            if (result == null) { Reply(clientId, new { type = "toast", text = "Make a diagram first." }); return; }

            string? full = DiagramIngest.SafeFullPath(result.Root, DiagramDocs.RelativePath);
            if (full == null) { Reply(clientId, new { type = "toast", text = "docs/ARCHITECTURE.md cannot be written in this project." }); return; }
            try
            {
                string? existing = null;
                if (File.Exists(full))
                {
                    if (new FileInfo(full).Length > 2_000_000) { Reply(clientId, new { type = "toast", text = "docs/ARCHITECTURE.md is too large to update safely." }); return; }
                    existing = File.ReadAllText(full);
                }
                string merged = DiagramDocs.Merge(existing, DiagramDocs.Render(result));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                string tmp = full + ".levity-tmp";
                File.WriteAllText(tmp, merged, new System.Text.UTF8Encoding(false));
                File.Move(tmp, full, overwrite: true);
                Reply(clientId, new { type = "toast", text = "Saved to docs/ARCHITECTURE.md" });
            }
            catch { Reply(clientId, new { type = "toast", text = "Could not write docs/ARCHITECTURE.md." }); }
        }

        // ---- Prompts: queue, direct commands, background jobs, stop -----------------------

        private record QueuedPrompt(int Id, string Text, bool Remote, string? ClientId, string? DirectCommand = null, List<string>? ImagePaths = null);

        private readonly List<QueuedPrompt> _queue = new();
        private readonly JobManager _jobs = new();
        private int _nextQueueId = 1;
        private int _jobsPending;
        private string? _lastQueueJson;
        private string? _lastJobsJson;
        private bool _inlineShell;

        private bool TurnBusy => _agyManager.IsBusy || _inlineShell || _checkpointing;

        /// <summary>Entry point for every typed message: runs it now, or queues it while a turn is in progress.</summary>
        private void HandlePrompt(string prompt, bool remote, string? clientId, string? directCommand = null, List<string>? imagePaths = null)
        {
            if (TurnBusy)
            {
                _queue.Add(new QueuedPrompt(_nextQueueId++, prompt, remote, clientId, directCommand, imagePaths));
                PublishQueue();
                return;
            }
            RunPrompt(prompt, remote, clientId, directCommand, imagePaths);
        }

        private void RunPrompt(string prompt, bool remote, string? clientId, string? directCommand = null, List<string>? imagePaths = null)
        {
            if (directCommand != null)
            {
                // A button (/run, /git): the text shown in the chat is `prompt`, what runs is the command built by the PC.
                _ = RunDirectCommandAsync(prompt, directCommand, remote, clientId);
                return;
            }
            if (CommandShortcut.TryParse(prompt, out string shellCmd, out string shellAfter, out bool shellDirect) && shellDirect && shellAfter.Length == 0)
            {
                // /cmd{$...}: run it directly, no AI, no tokens.
                _ = RunDirectCommandAsync(prompt, shellCmd, remote, clientId);
                return;
            }
            _settings.RecordProjectUse(_agyManager.CurrentWorkingDirectory);
            Publish(new { type = "user_message", text = prompt, source = remote ? "mobile" : "desktop", images = imagePaths });
            RawPublish(JsonSerializer.Serialize(new { type = "checkpoint", none = true }));   // the previous answer's undo bar goes away
            string expandedPrompt = CommandShortcut.Expand(prompt, _agyManager.CurrentWorkingDirectory);
            string promptForAgent = MobileImages.AttachImagesToPrompt(expandedPrompt, imagePaths ?? (IReadOnlyList<string>)Array.Empty<string>());
            StartAiTurn(promptForAgent);
        }

        // ---- Undo for an AI turn ---------------------------------------------------------------------

        private bool _checkpointing;
        private Checkpoints? _checkpoints;
        private string? _checkpointId;
        private bool _summaryDue;
        private bool _turnSeen;

        /// <summary>Takes a snapshot first (quick; gives up after 15 seconds) and then sends the message. Folders that are not git repositories skip straight to sending.</summary>
        private void StartAiTurn(string text)
        {
            if (!MemoryPolicy.CheckpointsEnabled) { _agyManager.SendPrompt(text); return; }
            string cwd = _agyManager.CurrentWorkingDirectory;
            if (ProjectActions.FindGitDir(cwd) == null) { _agyManager.SendPrompt(text); return; }
            _checkpointing = true;
            RawPublish(JsonSerializer.Serialize(new { type = "status", text = "Saving a checkpoint...", state = "busy" }));
            _ = Task.Run(async () =>
            {
                Checkpoints? c = null;
                string? id = null;
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    c = await Checkpoints.OpenAsync(cwd, limit.Token);
                    if (c != null) id = await c.CreateAsync(limit.Token);
                }
                catch { /* no checkpoint this time: the turn still runs */ }
                Dispatcher.Invoke(() =>
                {
                    _checkpointing = false;
                    _checkpoints = c;
                    _checkpointId = id;
                    _summaryDue = id != null;
                    _turnSeen = false;
                    _agyManager.SendPrompt(text);
                });
            });
        }

        /// <summary>After a turn: what changed in the folder since the snapshot, for the "Undo" bar.</summary>
        private async Task PublishCheckpointSummaryAsync()
        {
            var c = _checkpoints;
            string? id = _checkpointId;
            if (c == null || id == null || !_summaryDue) return;
            _summaryDue = false;
            try
            {
                var diff = await c.DiffAsync(id);
                if (diff == null || diff.Changes.Count == 0) { Dispatcher.Invoke(() => RawPublish(JsonSerializer.Serialize(new { type = "checkpoint", none = true }))); return; }
                var msg = new
                {
                    type = "checkpoint", none = false, id, root = c.Root, added = diff.Added, modified = diff.Modified, deleted = diff.Deleted,
                    insertions = diff.Insertions, deletions = diff.Deletions,
                    changes = diff.Changes.Take(200).Select(x => new { status = x.Status.ToString(), path = x.Path }),
                    more = Math.Max(0, diff.Changes.Count - 200)
                };
                Dispatcher.Invoke(() => RawPublish(JsonSerializer.Serialize(msg)));
            }
            catch { }
        }

        private async Task UndoCheckpointAsync(string id, string? clientId)
        {
            var c = _checkpoints;
            if (c == null || id != _checkpointId) { Reply(clientId, new { type = "toast", text = "That checkpoint is no longer available." }); return; }
            if (TurnBusy) { Reply(clientId, new { type = "toast", text = "Wait for the current answer to finish, then undo." }); return; }
            var (restored, removed, error) = await c.RestoreAsync(id);
            Dispatcher.Invoke(() =>
            {
                RawPublish(JsonSerializer.Serialize(new { type = "toast", text = error ?? $"Undone: {restored} file(s) restored, {removed} removed." }));
                RawPublish(JsonSerializer.Serialize(new { type = "checkpoint", none = true }));
            });
            if (error == null) { _summaryDue = false; }
        }

        /// <summary>Starts the next queued message once the current turn is over.</summary>
        private void DrainQueue()
        {
            if (TurnBusy || _queue.Count == 0) return;
            var next = _queue[0];
            _queue.RemoveAt(0);
            PublishQueue();
            RunPrompt(next.Text, next.Remote, next.ClientId, next.DirectCommand, next.ImagePaths);
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
                    interactive = j.Interactive,
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
                DirectShell.StripFlags(command, out command, out bool wantInteractive);
                bool interactive = wantInteractive || InteractiveCommands.LooksInteractive(command);
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
                var job = interactive ? _jobs.StartInteractive(command, cwd) : _jobs.Start(command, cwd);

                if (!await job.WaitAsync(TimeSpan.FromSeconds(interactive ? 2 : 3)))
                {
                    job.Background = true;
                    PublishJobs();
                    if (interactive)
                    {
                        Reply(clientId, new { type = "jobs_open", id = job.Id });   // the Jobs panel is where it is answered, on the device that started it
                        FinishDirectTurn($"**Job #{job.Id}** is running and may ask you something. Answer it in the **Jobs** panel (it just opened): type a reply, or use the arrow, Enter and Space buttons for menus. Chat is free.", 0);
                    }
                    else
                        FinishDirectTurn($"Still running, so it moved to the background as **job #{job.Id}**. Watch it or stop it from the **Jobs** button in the header. Chat is free.", 0);
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

        // ---- Speed and cost --------------------------------------------------------------------------

        private async Task PublishSpeedAsync(string? clientId)
        {
            try
            {
                var live = _history.ToArray();
                var msg = await Task.Run(() =>
                {
                    object Shape(SessionStatsResult s) => new
                    {
                        turns = s.Turns, seconds = s.TotalSeconds, inputTokens = s.InputTokens, outputTokens = s.OutputTokens, thinkingTokens = s.ThinkingTokens,
                        perTurn = s.PerTurn.Select(t => new { seconds = t.Seconds, inputTokens = t.InputTokens, outputTokens = t.OutputTokens }),
                        tools = s.Tools.Take(8).Select(t => new { name = t.Name, count = t.Count, seconds = t.TotalSeconds, max = t.MaxSeconds }),
                        slow = s.SlowSteps.Select(t => new { tool = t.Tool, seconds = t.Seconds, what = t.What }),
                        findings = s.Findings,
                    };
                    var current = SessionStats.Compute(live);
                    var recent = _sessions.List().Take(6).Select(m =>
                    {
                        var full = _sessions.Load(m.Id);
                        var st = full == null ? null : SessionStats.Compute(full.History);
                        return new { title = m.Title, updated = m.Updated, turns = st?.Turns ?? 0, seconds = st?.TotalSeconds ?? 0, inputTokens = st?.InputTokens ?? 0 };
                    }).ToList();
                    var foot = GeminiFootprint.Scan();
                    return new
                    {
                        type = "speed",
                        current = Shape(current),
                        recent,
                        footprint = new { totalMb = foot.TotalMegabytes, files = foot.TotalFiles, truncated = foot.Truncated, folders = foot.Folders.Take(8).Select(f => new { name = f.Name, mb = f.Megabytes, files = f.Files }), notes = foot.Notes }
                    };
                });
                Dispatcher.Invoke(() => Reply(clientId, msg));
            }
            catch { Dispatcher.Invoke(() => Reply(clientId, new { type = "toast", text = "Could not work out the speed report." })); }
        }

        // ---- Windows tools -------------------------------------------------------------------------

        private ToolHost _toolHost = null!;
        private PushService _push = null!;
        private readonly HashSet<string> _visibleClients = new();

        private async Task ResumeToolsAsync()
        {
            try { await _toolHost.ResumeAsync(); } catch { }
            if (_settings.WindowsToolsEnabled)
            {
                // learn about this PC a little after start-up, so the first message already has the facts and start-up stays quick
                await Task.Delay(TimeSpan.FromSeconds(20));
                try { await _toolHost.Toolbox.Profile.GetAsync(); } catch { }
            }
            Dispatcher.BeginInvoke(new Action(() => PublishToolsState(null)));
        }

        private async Task SetToolsAsync(bool on)
        {
            var (ok, message) = on ? await _toolHost.EnableAsync() : await _toolHost.DisableAsync();
            if (!_agyManager.IsBusy) _agyManager.Prepare(null, _agyManager.CurrentConversationId);   // the assistant reads its tool list when it starts
            Dispatcher.Invoke(() =>
            {
                RawPublish(JsonSerializer.Serialize(new { type = "toast", text = message }));
                PublishToolsState(null);
            });
            if (ok && on) _ = _toolHost.Toolbox.Profile.GetAsync();
        }

        private void PublishToolsState(string? clientId)
        {
            var msg = new
            {
                type = "tools_state",
                enabled = _settings.WindowsToolsEnabled,
                running = _toolHost.Running,
                count = _toolHost.Toolbox.Registry.All.Count,
                canElevate = _toolHost.Elevator.WhyNotAvailable == null,
                elevateNote = _toolHost.Elevator.WhyNotAvailable
            };
            if (clientId == null) RawPublish(JsonSerializer.Serialize(msg)); else Reply(clientId, msg);
        }

        private void PublishJournal(string? clientId)
        {
            var msg = new
            {
                type = "journal",
                entries = _toolHost.Toolbox.Journal.List(50).Select(e => new { id = e.Id, time = e.TimeUtc, tool = e.Tool, summary = e.Summary, reverted = e.Reverted, canUndo = _toolHost.Toolbox.Journal.CanUndo(e.UndoKind) && e.UndoKind != "none" })
            };
            if (clientId == null) RawPublish(JsonSerializer.Serialize(msg)); else Reply(clientId, msg);
        }

        // ---- PC Doctor ---------------------------------------------------------------------

        private IReadOnlyList<Finding> _doctorFindings = Array.Empty<Finding>();
        private int _doctorBusy;

        /// <summary>Scans the PC (no assistant, no tokens) and sends ranked findings to whoever asked.</summary>
        private static string DeviceOf(string clientId) { int i = clientId.IndexOf('/'); return i < 0 ? clientId : clientId[..i]; }

        private void PublishPushState(string clientId) =>
            Reply(clientId, new { type = "push_state", publicKey = _push.PublicKey, subscribed = _push.IsSubscribed(DeviceOf(clientId)) });

        /// <summary>A generic "Levity has news" push, only when no phone is connected to see it already.</summary>
        private void PushNews()
        {
            if (!_push.HasSubscribers) return;
            // A phone that is connected but not on screen (app in the background) does not count as watching.
            int visible; lock (_visibleClients) visible = _visibleClients.Count;
            int phones = Math.Min(_hub.ConnectedPhones, visible);
            _ = Task.Run(async () => { try { await _push.NotifyIfQuietAsync(phones); } catch { } });
        }

        private async Task RunDoctorAsync(string? clientId)
        {
            if (Interlocked.Exchange(ref _doctorBusy, 1) == 1) { Reply(clientId, new { type = "toast", text = "A scan is already running." }); return; }
            try
            {
                var tb = _toolHost.Toolbox;
                var facts = await Task.Run(() => PcDoctor.CollectAsync(tb.Registry, tb.Journal));
                _doctorFindings = PcDoctor.Evaluate(facts);
                var json = PcDoctor.ToJson(_doctorFindings, facts);
                json["type"] = "doctor";
                json["time"] = DateTime.UtcNow.ToString("o");
                string raw = json.ToJsonString();
                Dispatcher.Invoke(() => { if (clientId == null) RawPublish(raw); else Reply(clientId, JsonSerializer.Deserialize<JsonElement>(raw)); });
            }
            catch { Dispatcher.Invoke(() => Reply(clientId, new { type = "toast", text = "The scan failed." })); }
            finally { Interlocked.Exchange(ref _doctorBusy, 0); }
        }

        /// <summary>Runs one fix button: a read-only tool shows its answer, a change is only allowed from the PC, a prompt goes to the assistant.</summary>
        private async Task RunDoctorFixAsync(string? clientId, string id, int index)
        {
            var finding = _doctorFindings.FirstOrDefault(f => f.Id == id);
            if (finding == null || index < 0 || index >= finding.Fixes.Count) { Dispatcher.Invoke(() => Reply(clientId, new { type = "toast", text = "Scan again first." })); return; }
            var fix = finding.Fixes[index];
            try
            {
                switch (fix.Kind)
                {
                    case "prompt":
                        Dispatcher.Invoke(() => { if (TurnBusy) Reply(clientId, new { type = "toast", text = "Wait for the current answer to finish." }); else StartAiTurn(fix.Prompt ?? ""); });
                        break;
                    case "settings":
                    {
                        var r = await _toolHost.Toolbox.Registry.CallAsync("open_settings", new JsonObject { ["page"] = fix.Page }, new ToolContext { Journal = _toolHost.Toolbox.Journal });
                        Dispatcher.Invoke(() => Reply(clientId, new { type = "toast", text = r.Ok ? "Opened." : r.Text }));
                        break;
                    }
                    case "tool":
                    {
                        if (fix.Changes && clientId != null) { Dispatcher.Invoke(() => Reply(clientId, new { type = "toast", text = "Do this one from the PC." })); break; }
                        var r = await _toolHost.Toolbox.Registry.CallAsync(fix.Tool!, fix.Args ?? new JsonObject(), new ToolContext { Journal = _toolHost.Toolbox.Journal });
                        string text = r.Text + (r.Data != null ? "\n" + r.Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : "");
                        if (text.Length > 6000) text = text[..6000] + "\n...";
                        Dispatcher.Invoke(() => Reply(clientId, new { type = "doctor_result", id, index, ok = r.Ok, text }));
                        if (fix.Changes) Dispatcher.Invoke(() => PublishJournal(null));
                        break;
                    }
                }
            }
            catch (Exception ex) { Dispatcher.Invoke(() => Reply(clientId, new { type = "toast", text = "That did not work: " + ex.Message })); }
        }

        private async Task PublishMachineAsync(string? clientId, bool refresh)
        {
            try
            {
                var profile = await _toolHost.Toolbox.Profile.GetAsync(refresh);
                var msg = new { type = "machine", summary = MachineProfile.Summary(profile), built = (string?)profile["built_utc"] };
                Dispatcher.Invoke(() => { if (clientId == null) RawPublish(JsonSerializer.Serialize(msg)); else Reply(clientId, msg); });
            }
            catch { Dispatcher.Invoke(() => Reply(clientId, new { type = "toast", text = "Could not read this PC's details." })); }
        }

        // ---- /run and /git buttons -----------------------------------------------------------

        /// <summary>Tells the UI which buttons make sense for the current folder (scripts in package.json, a git repository). Sent to one client, or to all.</summary>
        private void PublishActions(string? clientId)
        {
            ProjectActionsInfo info;
            try { info = ProjectActions.Detect(_agyManager.CurrentWorkingDirectory); }
            catch { info = new ProjectActionsInfo(_agyManager.CurrentWorkingDirectory, null, Array.Empty<ProjectScript>(), false, null); }
            var msg = new
            {
                type = "actions",
                path = info.Path,
                packageManager = info.PackageManager,
                scripts = info.Scripts.Select(s => new { name = s.Name, command = s.Command }),
                git = new { repo = info.IsGitRepo, branch = info.Branch },
                hasFiles = info.HasFiles,
                isProject = info.IsProject,
                hasReadme = info.HasReadme
            };
            if (clientId == null && _hub != null) RawPublish(JsonSerializer.Serialize(msg)); else Reply(clientId, msg);
        }

        /// <summary>Branch, changes and ahead/behind for the git dialog. Runs git with a time limit so a huge repository cannot freeze anything.</summary>
        private async Task PublishGitStateAsync(string? clientId)
        {
            string cwd = _agyManager.CurrentWorkingDirectory;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "status --porcelain=v1 -b")
                {
                    WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) throw new InvalidOperationException();
                var read = proc.StandardOutput.ReadToEndAsync();
                if (!proc.WaitForExit(6000)) { try { proc.Kill(true); } catch { } throw new TimeoutException(); }
                var st = GitStatusParser.Parse(await read);
                Reply(clientId, new { type = "git_state", path = cwd, ok = true, branch = st.Branch, upstream = st.Upstream, ahead = st.Ahead, behind = st.Behind, changes = st.Changes });
            }
            catch { Reply(clientId, new { type = "git_state", path = cwd, ok = false }); }
        }

        private void RunScriptButton(string name, bool interactive, bool remote, string? clientId)
        {
            string cwd = _agyManager.CurrentWorkingDirectory;
            string? command = ProjectActions.RunCommand(cwd, name);   // only a script that really exists in this folder's package.json
            if (command == null) { Reply(clientId, new { type = "toast", text = "That script is not in this folder's package.json." }); return; }
            HandlePrompt($"/run {name}", remote, clientId, (interactive ? "-i " : "") + command);
        }

        private void GitButton(string action, string message, string name, bool stage, bool remote, string? clientId)
        {
            string cwd = _agyManager.CurrentWorkingDirectory;
            if (ProjectActions.FindGitDir(cwd) == null) { Reply(clientId, new { type = "toast", text = "This folder is not a git repository." }); return; }
            var built = GitActions.Build(action, message, name, stage, Path.GetTempPath());
            if (built == null) { Reply(clientId, new { type = "toast", text = action == "commit" ? "Write a commit message first." : "That git action is not available." }); return; }
            try { GitActions.WriteMessage(built, message); }
            catch { Reply(clientId, new { type = "toast", text = "Could not prepare the commit message." }); return; }
            HandlePrompt(built.Typed, remote, clientId, built.Command);
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
                isPinned = _settings.IsPinned,
                effort = AgentLaunch.NormalizeEffort(_settings.Effort),
                mode = AgentLaunch.NormalizeMode(_settings.Mode)
            });
            PublishActions(clientId);
            PublishToolsState(clientId);
            if (_lastStatusJson != null) ReplyRaw(clientId, _lastStatusJson);
            ReplyRaw(clientId, _lastSyncJson ?? IdleSyncJson);
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
            _agyManager.Prepare(null, null);
            ClearHistory();
            Publish(new { type = "clear_chat" });
            if (showWindow) SlideIn();
        }

        private void ClearHistory() => BeginNewSession();

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
                PublishFastLearner(rawJson);
            });
        }

        private string? _lastFastLearnerKey;
        private DateTime _lastFastLearnerAt;

        /// <summary>Tells the sidebar, the phone and (if the sidebar is hidden) Windows when Fast Learner starts working.</summary>
        private void PublishFastLearner(string rawJson)
        {
            var notice = FastLearnerDetector.Inspect(rawJson);
            if (notice == null) return;

            // "announce skill X" followed by the write into skill X's folder is one event, not two.
            string key = $"{notice.Phase}|{notice.Kind}|{notice.Detail}";
            if (key == _lastFastLearnerKey && (DateTime.UtcNow - _lastFastLearnerAt).TotalSeconds < 60) return;
            _lastFastLearnerKey = key;
            _lastFastLearnerAt = DateTime.UtcNow;

            Publish(new { type = "fast_learner", phase = notice.Phase, kind = notice.Kind, detail = notice.Detail, path = notice.Path, source = notice.Source, text = notice.Text });
            if (!_isSidebarVisible) _trayManager.ShowNotification("Levity · Fast Learner", notice.Text);
        }

        private void HandleAgyStatus(string statusText)
        {
            Dispatcher.Invoke(() =>
            {
                string state = "online";
                if (statusText.Contains("Thinking") || statusText.Contains("Respond") || statusText.Contains("Starting"))
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
                if (statusText.Contains("Thinking") || statusText.Contains("Respond")) _turnSeen = true;
                if (statusText == "Ready")
                {
                    Dispatcher.BeginInvoke(new Action(DrainQueue), System.Windows.Threading.DispatcherPriority.Background);
                    if (_turnSeen) { _turnSeen = false; _ = PublishCheckpointSummaryAsync(); PushNews(); }   // not for the "Ready" the assistant reports when it merely starts
                }
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
            if (json.StartsWith("{\"type\":\"agy_event\"") || json.StartsWith("{\"type\":\"user_message\"") || json.StartsWith("{\"type\":\"fast_learner\""))
            {
                HistoryCompactor.Add(_history, json);   // neighbouring streaming fragments become one line
                if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
                if (_history.Count % 64 == 0) HistoryCompactor.TrimToBudget(_history);   // and the oldest turns go when the size budget is passed
                ScheduleSessionSave();
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

        // ---- Giving memory back --------------------------------------------------------------------

        private System.Windows.Threading.DispatcherTimer? _housekeeping;
        private DateTime _hiddenSinceUtc = DateTime.UtcNow;
        private bool _webSuspended;
        private bool _missedWhileSuspended;
        private bool _trimmedThisHide;
        private bool _suspending;
        private bool _webReleased;

        private void StartHousekeeping()
        {
            _housekeeping = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _housekeeping.Tick += (_, _) => Housekeeping();
            _housekeeping.Start();
        }

        private bool AnythingNeedsThePage => TurnBusy || _queue.Count > 0 || _jobs.Background.Any(j => j.State == JobState.Running);

        /// <summary>Every few seconds: stop an assistant nobody is using, sleep the hidden page, trim the host. All of it is undone by the next use.</summary>
        private void Housekeeping()
        {
            try
            {
                var now = DateTime.UtcNow;

                // 1. the assistant process
                if (MemoryPolicy.ShouldStopAgent(_agyManager.IsRunning, _agyManager.IsBusy, TurnBusy || _queue.Count > 0, _agyManager.LastActivityUtc, now, MemoryPolicy.AgentIdleLimitFromEnvironment()))
                    _agyManager.Stop();   // keeps the conversation id: the next message resumes it

                // 2. the page
                var hiddenFor = _isSidebarVisible ? TimeSpan.Zero : now - _hiddenSinceUtc;
                if (!_webSuspended && !_suspending && MemoryPolicy.ShouldSuspendWebView(_isSidebarVisible, _isAnimating, AnythingNeedsThePage, hiddenFor))
                    _ = SuspendWebViewAsync();
                else if (_webSuspended && AnythingNeedsThePage) WakeWebView();   // an answer or command started while hidden (from the phone, say)

                // 2b. a long hide: throw the page away completely
                if (!_webReleased && MemoryPolicy.ShouldReleaseWebView(_isSidebarVisible, _isAnimating, AnythingNeedsThePage, hiddenFor, MemoryPolicy.WebViewReleaseLimitFromEnvironment()))
                    ReleaseWebView();

                // 3. the host itself
                if (MemoryPolicy.ShouldTrimHost(_isSidebarVisible, hiddenFor, _trimmedThisHide))
                {
                    _trimmedThisHide = true;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    NativeMethods.TrimWorkingSet();
                }
            }
            catch { /* housekeeping must never take the app down */ }
        }

        /// <summary>Disposes the web view: its browser, renderer and GPU processes exit and the memory goes back to Windows.</summary>
        private void ReleaseWebView()
        {
            try
            {
                _webReleased = true;
                _webSuspended = false;
                _missedWhileSuspended = false;
                var old = webView;
                RootBorder.Child = null;
                try { old.Dispose(); } catch { }
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                NativeMethods.TrimWorkingSet();
            }
            catch { }
        }

        /// <summary>Creates the page again if it was released. The new page asks for the conversation when it loads, like at start-up.</summary>
        private void EnsureWebView()
        {
            if (!_webReleased) return;
            _webReleased = false;
            _missedWhileSuspended = false;
            webView = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x09, 0x09, 0x0b) };
            RootBorder.Child = webView;
            InitializeWebViewAsync();
        }

        private async Task SuspendWebViewAsync()
        {
            _suspending = true;
            try
            {
                if (webView.CoreWebView2 == null) return;
                webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                bool ok = await webView.CoreWebView2.TrySuspendAsync();
                if (ok && !_isSidebarVisible && !_isAnimating) _webSuspended = true;
                else if (ok) { webView.CoreWebView2.Resume(); }   // shown again while it was going to sleep
            }
            catch { }
            finally { _suspending = false; }
        }

        /// <summary>Wakes the page (if it was asleep) and rebuilds it if it missed anything meanwhile.</summary>
        private void WakeWebView()
        {
            if (!_webSuspended)
            {
                // not asleep, but it may be running on the low-memory setting from the last hide
                try { if (webView.CoreWebView2 != null && (_isSidebarVisible || _isAnimating)) webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
                return;
            }
            _webSuspended = false;
            try
            {
                if (webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.Resume();
                    if (_isSidebarVisible || _isAnimating) webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                }
            }
            catch { }
            if (_missedWhileSuspended)
            {
                _missedWhileSuspended = false;
                PostJsonToWeb(new { type = "clear_chat" });
                SendSnapshot(null);   // the same replay a freshly opened page gets
            }
        }

        private void PostJsonToWeb(object obj) => PostRawToWeb(JsonSerializer.Serialize(obj));

        private void PostRawToWeb(string json)
        {
            if (_webSuspended || _webReleased) { _missedWhileSuspended = true; return; }   // nobody is looking; the page is rebuilt from the host when it wakes
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
            try { RollingLog.Append("startup.log", $"[{DateTime.Now:HH:mm:ss}] ShutdownApplication called! Stack:\n{Environment.StackTrace}\n"); } catch { }
            _sync.Cancel();
            _agyManager?.Stop();
            try { _hub.Dispose(); } catch { }
            try { _jobs.Dispose(); } catch { }
            try { SaveSession(); } catch { }
            try { _toolHost?.Dispose(); } catch { }
            try { _push?.Dispose(); } catch { }
            _hotkeyManager?.Dispose();
            _trayManager?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            try { RollingLog.Append("startup.log", $"[{DateTime.Now:HH:mm:ss}] OnClosed called!\n"); } catch { }
            ShutdownApplication();
            base.OnClosed(e);
        }
    }
}
