using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services
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
        /// <summary>Runs in a pseudo-console, so the command can ask questions that are answered from the Jobs panel.</summary>
        public bool Interactive { get; init; }
        internal VtScreen? Screen { get; set; }
        internal PtyProcess? Pty { get; set; }
        internal Process? Proc { get; set; }
        internal WinJob? Win { get; set; }
        internal bool StopRequested { get; set; }
        private int _finished;

        /// <summary>True the first time only, so Stop and the exit handler never both finish a job.</summary>
        internal bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;

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
            if (Screen != null)
            {
                string shown = Screen.Text();
                return shown.Length <= chars ? shown : "…" + shown.Substring(shown.Length - chars);
            }
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

        /// <summary>Types into a running interactive command. Text goes in as typed; pass enter to press Enter after it.</summary>
        public bool SendText(int id, string text, bool enter)
        {
            ShellJob? job;
            lock (_gate) job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job?.Pty == null || job.State != JobState.Running) return false;
            if (text.Length > 4000) text = text.Substring(0, 4000);
            return job.Pty.Write(text.Replace("\r\n", "\r").Replace('\n', '\r') + (enter ? "\r" : ""));
        }

        /// <summary>Presses a named key (see PtyProcess.KeyCode) in a running interactive command.</summary>
        public bool SendKey(int id, string key)
        {
            string? code = PtyProcess.KeyCode(key);
            if (code == null) return false;
            ShellJob? job;
            lock (_gate) job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job?.Pty == null || job.State != JobState.Running) return false;
            return job.Pty.Write(code);
        }

        /// <summary>Like Start, but in a pseudo-console: programs see a real terminal, so prompts and menus work.</summary>
        public ShellJob StartInteractive(string command, string cwd)
        {
            var job = new ShellJob { Id = Interlocked.Increment(ref _nextId) - 1, Command = command, Cwd = cwd, Interactive = true, Screen = new VtScreen() };
            lock (_gate) _jobs.Add(job);
            try
            {
                string line = "powershell.exe -NoLogo -NoProfile -Command " + QuoteArg("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);
                var env = new Dictionary<string, string> { ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "0" };
                var pty = PtyProcess.Start(line, cwd, env, job.Screen.Columns, job.Screen.Rows, text => { job.Screen.Feed(text); Changed?.Invoke(); });
                job.Pty = pty;
                job.Win = WinJob.TryCreate(pty.ProcessHandle);
                _ = pty.Exited.ContinueWith(async t =>
                {
                    // Let the last output arrive before the job is reported finished.
                    await Task.WhenAny(pty.Drained, Task.Delay(1500));
                    Finish(job, t.Result);
                });
            }
            catch (Exception ex)
            {
                job.Screen.Feed($"Could not start the command: {ex.Message}");
                job.ExitCode = -1;
                job.EndedUtc = DateTime.UtcNow;
                job.State = JobState.Failed;
                job.Complete();
            }
            return job;
        }

        /// <summary>One argument for a Windows command line, quoted the way CommandLineToArgvW reads it.</summary>
        internal static string QuoteArg(string arg)
        {
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
                else sb.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
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
                // Bounded wait: a surviving grandchild can hold the output pipes open indefinitely.
                try { proc.WaitForExit(1500); } catch { }
                Finish(job, SafeExitCode(proc));
            };

            try
            {
                proc.Start();
                job.Win = WinJob.TryCreate(proc); // groups every descendant so Stop can kill them all
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

        private void Finish(ShellJob job, int exitCode)
        {
            if (!job.TryFinish()) return;
            job.ExitCode = exitCode;
            job.EndedUtc = DateTime.UtcNow;
            job.State = job.StopRequested ? JobState.Stopped : exitCode == 0 ? JobState.Done : JobState.Failed;
            job.Complete();
            job.Win?.Dispose();
            TrimFinished();
            Changed?.Invoke();
            if (job.Background) BackgroundFinished?.Invoke(job);
        }

        /// <summary>Finished background jobs keep their output for looking at; only the latest ones, so a long day does not pile them up.</summary>
        public const int MaxFinishedJobs = 20;

        private void TrimFinished()
        {
            lock (_gate)
            {
                var finished = _jobs.Where(j => j.Background && j.State != JobState.Running).OrderBy(j => j.EndedUtc ?? DateTime.MaxValue).ToList();
                for (int i = 0; i < finished.Count - MaxFinishedJobs; i++) _jobs.Remove(finished[i]);
            }
        }

        private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

        public bool Stop(int id)
        {
            ShellJob? job;
            lock (_gate) job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null || (job.Proc == null && job.Pty == null) || job.State != JobState.Running) return false;
            job.StopRequested = true;
            job.Win?.Terminate();                                 // everything started by the command, even orphans
            try { job.Proc?.Kill(entireProcessTree: true); } catch { }
            try { job.Pty?.Kill(); } catch { }
            Finish(job, -1);                                      // show "stopped" now, not when the pipes finally close
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
                j.Win?.Terminate();
                try { j.Proc?.Kill(entireProcessTree: true); } catch { }
                try { j.Pty?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Windows job object: terminating it kills the whole process group, including orphaned grandchildren.</summary>
    internal sealed class WinJob : IDisposable
    {
        private IntPtr _handle;

        public static WinJob? TryCreate(Process p) => TryCreate(p.Handle);

        public static WinJob? TryCreate(IntPtr process)
        {
            try
            {
                IntPtr h = CreateJobObject(IntPtr.Zero, null);
                if (h == IntPtr.Zero) return null;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // kill everything if Levity itself closes the handle
                int len = Marshal.SizeOf(info);
                IntPtr ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(h, 9, ptr, (uint)len)) { CloseHandle(h); return null; }
                }
                finally { Marshal.FreeHGlobal(ptr); }
                if (!AssignProcessToJobObject(h, process)) { CloseHandle(h); return null; }
                return new WinJob { _handle = h };
            }
            catch { return null; }
        }

        public void Terminate() { if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1); }

        public void Dispose()
        {
            IntPtr h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero) CloseHandle(h);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong A, B, C, D, E, F; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
