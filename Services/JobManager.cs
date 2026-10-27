using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravitySidebar.Services
{
    public enum JobState { Running, Done, Failed, Stopped }

    /// <summary>One PowerShell command started from "/cmd{$...}". Long-running ones are promoted to background jobs.</summary>
    public sealed class ShellJob
    {
        private const int MaxChars = 200_000;
        private readonly StringBuilder _out = new();
        private readonly object _gate = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; init; }
        public string Command { get; init; } = "";
        public string Cwd { get; init; } = "";
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime? EndedUtc { get; internal set; }
        public JobState State { get; internal set; } = JobState.Running;
        public int? ExitCode { get; internal set; }
        /// <summary>Shown in the Jobs panel (set once the command outlives the inline wait).</summary>
        public bool Background { get; set; }
        internal Process? Proc { get; set; }
        internal bool StopRequested { get; set; }

        internal void Append(string? line)
        {
            if (line == null) return;
            lock (_gate)
            {
                _out.AppendLine(line);
                if (_out.Length > MaxChars) _out.Remove(0, _out.Length - MaxChars);
            }
        }

        public string Tail(int chars)
        {
            lock (_gate)
            {
                string all = _out.ToString().TrimEnd();
                return all.Length <= chars ? all : "…" + all.Substring(all.Length - chars);
            }
        }

        internal void Complete() => _exited.TrySetResult();

        /// <summary>True if the command finished within the timeout.</summary>
        public async Task<bool> WaitAsync(TimeSpan timeout) =>
            await Task.WhenAny(_exited.Task, Task.Delay(timeout)) == _exited.Task;
    }

    public sealed class JobManager : IDisposable
    {
        // CSI and OSC escape sequences (colours, cursor moves, window titles) that PowerShell tools emit.
        private static readonly Regex Ansi = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);

        private readonly object _gate = new();
        private readonly List<ShellJob> _jobs = new();
        private int _nextId = 1;

        /// <summary>Raised from any thread whenever a job's output or state changes.</summary>
        public event Action? Changed;
        /// <summary>Raised when a background job exits.</summary>
        public event Action<ShellJob>? BackgroundFinished;

        public IReadOnlyList<ShellJob> Background
        {
            get { lock (_gate) return _jobs.Where(j => j.Background).ToList(); }
        }

        public ShellJob Start(string command, string cwd)
        {
            var job = new ShellJob { Id = Interlocked.Increment(ref _nextId) - 1, Command = command, Cwd = cwd };
            lock (_gate) _jobs.Add(job);

            var psi = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["NO_COLOR"] = "1";
            psi.Environment["FORCE_COLOR"] = "0";
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command })
                psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            job.Proc = proc;
            void OnLine(object _, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                job.Append(Ansi.Replace(e.Data, "").TrimEnd('\r'));
                Changed?.Invoke();
            }
            proc.OutputDataReceived += OnLine;
            proc.ErrorDataReceived += OnLine;
            proc.Exited += (_, _) =>
            {
                try { proc.WaitForExit(); } catch { } // flush the async readers
                job.ExitCode = SafeExitCode(proc);
                job.EndedUtc = DateTime.UtcNow;
                job.State = job.StopRequested ? JobState.Stopped : job.ExitCode == 0 ? JobState.Done : JobState.Failed;
                job.Complete();
                Changed?.Invoke();
                if (job.Background) BackgroundFinished?.Invoke(job);
            };

            try
            {
                proc.Start();
                proc.StandardInput.Close(); // commands that prompt must not hang
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                job.Append($"Could not start PowerShell: {ex.Message}");
                job.ExitCode = -1;
                job.EndedUtc = DateTime.UtcNow;
                job.State = JobState.Failed;
                job.Complete();
            }
            return job;
        }

        private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

        public bool Stop(int id)
        {
            ShellJob? job;
            lock (_gate) job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job?.Proc == null || job.State != JobState.Running) return false;
            job.StopRequested = true;
            try { job.Proc.Kill(entireProcessTree: true); } catch { }
            return true;
        }

        /// <summary>Drops a job that finished inline so it never appears in the Jobs panel.</summary>
        public void Discard(ShellJob job) { lock (_gate) _jobs.Remove(job); }

        public void ClearFinished()
        {
            lock (_gate) _jobs.RemoveAll(j => j.Background && j.State != JobState.Running);
            Changed?.Invoke();
        }

        public void Dispose()
        {
            List<ShellJob> all;
            lock (_gate) all = _jobs.ToList();
            foreach (var j in all)
            {
                if (j.State != JobState.Running) continue;
                j.StopRequested = true;
                try { j.Proc?.Kill(entireProcessTree: true); } catch { }
            }
        }
    }
}
