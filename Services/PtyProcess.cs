using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Levity.Services
{
    /// <summary>
    /// A program running in a Windows pseudo-console (ConPTY), so it believes it has a real terminal. That is what lets tools that ask
    /// questions (menus, "Project name?" prompts, logins) work: with plain pipes they refuse to prompt or fail outright.
    /// Output is a stream of terminal text (feed it to VtScreen); input is typed text and key sequences.
    /// </summary>
    internal sealed class PtyProcess : IDisposable
    {
        private IntPtr _console;
        private IntPtr _process;
        private FileStream? _input;
        private FileStream? _output;
        private int _disposed;
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; private set; }
        internal IntPtr ProcessHandle => _process;
        public Task<int> Exited => _exit.Task;
        /// <summary>Completes when everything the program printed has been delivered to the output handler.</summary>
        public Task Drained => _drained.Task;

        /// <summary>Starts <paramref name="commandLine"/> in a pseudo-console of the given size. <paramref name="onOutput"/> runs on a background thread.</summary>
        public static PtyProcess Start(string commandLine, string cwd, IDictionary<string, string> extraEnv, int cols, int rows, Action<string> onOutput)
        {
            var pty = new PtyProcess();
            try { pty.Launch(commandLine, cwd, extraEnv, cols, rows, onOutput); }
            catch { pty.Dispose(); throw; }
            return pty;
        }

        private void Launch(string commandLine, string cwd, IDictionary<string, string> extraEnv, int cols, int rows, Action<string> onOutput)
        {
            if (!CreatePipe(out SafeFileHandle inRead, out SafeFileHandle inWrite, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreatePipe(out SafeFileHandle outRead, out SafeFileHandle outWrite, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());

            int hr = CreatePseudoConsole(new Coord((short)cols, (short)rows), inRead, outWrite, 0, out _console);
            inRead.Dispose();      // the pseudo-console keeps its own copies of these two ends
            outWrite.Dispose();
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed (needs Windows 10 1809 or newer)");

            _input = new FileStream(inWrite, FileAccess.Write, 1, false);
            _output = new FileStream(outRead, FileAccess.Read, 4096, false);

            IntPtr attrList = IntPtr.Zero, envBlock = IntPtr.Zero;
            try
            {
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attrList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)ProcThreadAttributePseudoConsole, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var si = new StartupInfoEx();
                si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                si.lpAttributeList = attrList;
                // Without this, a parent whose own standard handles are redirected (a test host, a service) would pass them on instead of the pseudo-console.
                si.StartupInfo.dwFlags = StartfUseStdHandles;

                envBlock = Marshal.StringToHGlobalUni(BuildEnvironment(extraEnv));
                var cmd = new StringBuilder(commandLine);
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, envBlock, cwd, ref si, out ProcessInformation pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(pi.hThread);
                _process = pi.hProcess;
                ProcessId = pi.dwProcessId;
            }
            finally
            {
                if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
                if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            }

            // Output: read until the pseudo-console is closed (which happens after the program exits).
            var reader = new Thread(() =>
            {
                var decoder = Encoding.UTF8.GetDecoder();
                var bytes = new byte[4096];
                var chars = new char[4096];
                try
                {
                    int n;
                    while ((n = _output!.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        int c = decoder.GetChars(bytes, 0, n, chars, 0);
                        if (c > 0) onOutput(new string(chars, 0, c));
                    }
                }
                catch { /* the pipe closed under us: that is the normal end */ }
                finally { _drained.TrySetResult(); }
            }) { IsBackground = true, Name = "pty-output" };
            reader.Start();

            // Exit: wait for the program, then close the pseudo-console so the reader sees the end of the output.
            var waiter = new Thread(() =>
            {
                IntPtr h = _process;
                WaitForSingleObject(h, 0xFFFFFFFF);
                GetExitCodeProcess(h, out uint code);
                _exit.TrySetResult(unchecked((int)code));
                IntPtr p = Interlocked.Exchange(ref _process, IntPtr.Zero);   // after this, Kill is a no-op
                if (p != IntPtr.Zero) CloseHandle(p);
                CloseConsole();
            }) { IsBackground = true, Name = "pty-exit" };
            waiter.Start();
        }

        private static string BuildEnvironment(IDictionary<string, string> extra)
        {
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = (string?)e.Value ?? "";
            foreach (var kv in extra) env[kv.Key] = kv.Value;
            var sb = new StringBuilder();
            foreach (var kv in env) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            sb.Append('\0');
            return sb.ToString();
        }

        /// <summary>Types text into the program (use "\r" for Enter, or the key sequences in <see cref="KeyCode"/>).</summary>
        public bool Write(string text)
        {
            try
            {
                var input = _input;
                if (input == null) return false;
                byte[] data = Encoding.UTF8.GetBytes(text);
                input.Write(data, 0, data.Length);
                input.Flush();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Ends the program and everything it started.</summary>
        public void Kill()
        {
            IntPtr p = _process;
            if (p != IntPtr.Zero) TerminateProcess(p, 1);
        }

        private void CloseConsole()
        {
            IntPtr c = Interlocked.Exchange(ref _console, IntPtr.Zero);
            if (c != IntPtr.Zero) ClosePseudoConsole(c);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Kill();
            CloseConsole();
            try { _input?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
        }

        // ---- keys ----

        /// <summary>What to type for a named key (the Jobs panel offers these as buttons).</summary>
        public static string? KeyCode(string key) => (key ?? "").ToLowerInvariant() switch
        {
            "enter" => "\r",
            "up" => "\x1b[A",
            "down" => "\x1b[B",
            "right" => "\x1b[C",
            "left" => "\x1b[D",
            "space" => " ",
            "tab" => "\t",
            "esc" => "\x1b",
            "backspace" => "\x7f",
            "ctrl-c" => "\x03",
            "ctrl-d" => "\x04",
            _ => null,
        };

        // ---- Win32 ----

        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int StartfUseStdHandles = 0x00000100;

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord { public short X, Y; public Coord(short x, short y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, uint size);

        [DllImport("kernel32.dll")]
        private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr console);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttrs, IntPtr threadAttrs, bool inherit,
            uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);

        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
