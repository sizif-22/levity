using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravitySidebar.Services
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

        public bool Start()
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
                        Arguments = "--dangerously-skip-permissions --input-format=stream-json --output-format=stream-json --print=\"\"",
                        WorkingDirectory = CurrentWorkingDirectory,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                    _process.Exited += (s, e) =>
                    {
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

        public void Restart(string? newWorkingDirectory = null)
        {
            if (!string.IsNullOrWhiteSpace(newWorkingDirectory) && Directory.Exists(newWorkingDirectory))
            {
                CurrentWorkingDirectory = newWorkingDirectory;
            }
            Start();
        }

        public void Stop()
        {
            lock (_lock)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        try { _process.Kill(true); } catch { }
                        _process.Dispose();
                    }
                }
                catch { }
                finally
                {
                    _process = null;
                    _stdin = null;
                    IsBusy = false;
                }
            }
        }

        public bool SendPrompt(string promptText)
        {
            lock (_lock)
            {
                if (!IsRunning || _stdin == null)
                {
                    if (!Start()) return false;
                }

                try
                {
                    IsBusy = true;
                    OnStatusChanged?.Invoke("Thinking...");

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
