using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services
{
    public class AgyProcessManager
    {
        private Process? _process;
        private StreamWriter? _stdin;
        private readonly object _lock = new();

        public event Action<string>? OnEventReceived;
        public event Action<string>? OnErrorReceived;
        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnConversationInitialized;

        public string CurrentWorkingDirectory { get; private set; }
        public string? CurrentConversationId { get; private set; }

        /// <summary>Thinking effort for new turns (low, medium or high). Takes effect the next time the assistant is started.</summary>
        public string Effort { get; set; } = AgentLaunch.Fast;

        private bool _hinted;   // the efficiency hint goes out once per assistant process

        /// <summary>When the assistant last received a message or printed anything. Used to stop it after a quiet spell.</summary>
        public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
        public bool IsRunning => _process != null && !_process.HasExited;
        public bool IsBusy { get; private set; } = false;

        public AgyProcessManager(string workingDirectory)
        {
            CurrentWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : workingDirectory;
        }

        public static string? LocateAgyExecutable()
        {
            // 0. Explicit override from .env / environment
            string? overridePath = DotEnv.Get("AGY_PATH");
            if (overridePath != null && File.Exists(overridePath)) return overridePath;

            // 1. Direct local AppData path
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string knownPath = Path.Combine(localAppData, "agy", "bin", "agy.exe");
            if (File.Exists(knownPath)) return knownPath;

            // 2. Search PATH environment variable
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), "agy.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }

            // 3. Fallback to just "agy"
            return "agy";
        }

        public bool Start(string? resumeConversationId = null)
        {
            lock (_lock)
            {
                Stop();

                string? agyPath = LocateAgyExecutable();
                string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
                try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Start called. agyPath={agyPath}, cwd={CurrentWorkingDirectory}\n"); } catch { }

                if (string.IsNullOrEmpty(agyPath))
                {
                    OnErrorReceived?.Invoke("Could not locate agy.exe on system.");
                    OnStatusChanged?.Invoke("Error: agy.exe not found");
                    return false;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = agyPath,
                        Arguments = AgentLaunch.Arguments(resumeConversationId, Effort),
                        WorkingDirectory = CurrentWorkingDirectory,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    psi.Environment[AgentLaunch.EnvMarker] = "1";   // lets a shell profile skip its slow extras for the assistant's shells
                    _hinted = false;

                    _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                    _process.Exited += (s, e) =>
                    {
                        if (!ReferenceEquals(s, _process)) return; // a deliberate stop/restart, not a crash
                        IsBusy = false;
                        try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] agy.exe process exited. ExitCode={_process?.ExitCode}\n"); } catch { }
                        OnStatusChanged?.Invoke("Process Stopped");
                    };

                    if (!_process.Start())
                    {
                        try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] _process.Start() returned false\n"); } catch { }
                        OnStatusChanged?.Invoke("Failed to start agy.exe");
                        return false;
                    }

                    try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] agy.exe started successfully with PID {_process.Id}\n"); } catch { }

                    _process.StandardInput.AutoFlush = true;
                    _stdin = _process.StandardInput;

                    // Start background output reader
                    Task.Run(() => ReadStdoutAsync(_process.StandardOutput));
                    // Start background error reader
                    Task.Run(() => ReadStderrAsync(_process.StandardError));

                    OnStatusChanged?.Invoke("Ready");
                    return true;
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] Exception starting agy: {ex}\n"); } catch { }
                    OnErrorReceived?.Invoke($"Failed to launch agy: {ex.Message}");
                    OnStatusChanged?.Invoke("Launch Error");
                    return false;
                }
            }
        }

        /// <summary>
        /// Points the assistant at a folder and (optionally) a saved conversation WITHOUT starting it: the process is only started by the
        /// next message, and resumes that conversation. A null id means a fresh conversation. Keeps an idle PC from carrying an assistant
        /// process (about 100 MB) nobody is talking to.
        /// </summary>
        public void Prepare(string? newWorkingDirectory = null, string? resumeConversationId = null)
        {
            lock (_lock)
            {
                Stop();
                if (!string.IsNullOrWhiteSpace(newWorkingDirectory) && Directory.Exists(newWorkingDirectory))
                    CurrentWorkingDirectory = newWorkingDirectory;
                CurrentConversationId = string.IsNullOrWhiteSpace(resumeConversationId) ? null : resumeConversationId;
            }
        }

        public void Restart(string? newWorkingDirectory = null, string? resumeConversationId = null)
        {
            if (!string.IsNullOrWhiteSpace(newWorkingDirectory) && Directory.Exists(newWorkingDirectory))
            {
                CurrentWorkingDirectory = newWorkingDirectory;
            }
            Start(resumeConversationId);
        }

        /// <summary>Cancels the running turn by stopping agy; the next message starts it again on the same conversation, so context is kept.</summary>
        public void Interrupt() => Stop();

        public void Stop()
        {
            lock (_lock)
            {
                // Forget the process BEFORE ending it: the Exited handler treats "still the current process" as a crash, so ending it
                // first made a deliberate stop (idle stop, new chat, Stop button) flash "Process Stopped" as an error.
                var proc = _process;
                _process = null;
                _stdin = null;
                IsBusy = false;
                try
                {
                    if (proc != null && !proc.HasExited)
                    {
                        try { proc.Kill(true); } catch { }
                    }
                    proc?.Dispose();
                }
                catch { }
            }
        }

        public bool SendPrompt(string promptText)
        {
            lock (_lock)
            {
                if (!IsRunning || _stdin == null)
                {
                    OnStatusChanged?.Invoke("Starting the assistant...");
                    if (!Start(CurrentConversationId)) return false;   // resumes the conversation if it was stopped while idle
                }

                try
                {
                    LastActivityUtc = DateTime.UtcNow;
                    IsBusy = true;
                    OnStatusChanged?.Invoke("Thinking...");
                    promptText = AgentLaunch.WithHint(promptText, !_hinted);
                    _hinted = true;

                    // NDJSON message format: {"event": "user", "message": {"content": "..."}}
                    var payload = new
                    {
                        @event = "user",
                        message = new
                        {
                            content = promptText
                        }
                    };

                    string json = JsonSerializer.Serialize(payload);
                    try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"), $"[{DateTime.Now:HH:mm:ss}] [STDIN WRITE] {json}\n"); } catch { }
                    _stdin!.WriteLine(json);
                    _stdin!.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    IsBusy = false;
                    OnErrorReceived?.Invoke($"Failed to send prompt: {ex.Message}");
                    OnStatusChanged?.Invoke("Error sending");
                    return false;
                }
            }
        }

        private async Task ReadStdoutAsync(StreamReader reader)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    LastActivityUtc = DateTime.UtcNow;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("event", out var eventProp))
                        {
                            string? evt = eventProp.GetString();
                            if (evt == "init")
                            {
                                if (root.TryGetProperty("conversation_id", out var convIdProp))
                                {
                                    CurrentConversationId = convIdProp.GetString();
                                    OnConversationInitialized?.Invoke(CurrentConversationId ?? "");
                                }
                                OnStatusChanged?.Invoke("Ready");
                            }
                            else if (evt == "result")
                            {
                                IsBusy = false;
                                OnStatusChanged?.Invoke("Ready");
                            }
                            else if (evt == "step_update")
                            {
                                if (root.TryGetProperty("step_update", out var stepProp) &&
                                    stepProp.TryGetProperty("step_type", out var typeProp) &&
                                    typeProp.GetString() == "agent_response")
                                {
                                    OnStatusChanged?.Invoke("Responding...");
                                }
                            }
                        }
                    }
                    catch { }

                    OnEventReceived?.Invoke(line);
                    try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"), $"[{DateTime.Now:HH:mm:ss}] [STDOUT] {line}\n"); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"), $"[{DateTime.Now:HH:mm:ss}] [STDOUT ERR] {ex}\n"); } catch { }
                OnErrorReceived?.Invoke($"Stdout read error: {ex.Message}");
            }
        }

        private async Task ReadStderrAsync(StreamReader reader)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log"), $"[{DateTime.Now:HH:mm:ss}] [STDERR] {line}\n"); } catch { }
                        OnErrorReceived?.Invoke(line);
                    }
                }
            }
            catch { }
        }
    }
}
