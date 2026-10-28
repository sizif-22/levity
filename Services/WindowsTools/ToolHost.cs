using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Levity.Services.WindowsTools
{
    /// <summary>
    /// Turns the Windows toolbox on and off for the assistant. On: the MCP server starts on this PC (loopback only) and the assistant is told
    /// about it with its own command, "agy mcp add". Off: the server stops and the entry is removed. Nothing is registered until the user
    /// turns the switch on, and Levity never edits the assistant's configuration files by hand.
    /// </summary>
    public sealed class ToolHost : IDisposable
    {
        public const string ServerName = "levity-windows";

        private readonly AppSettings _settings;
        private readonly Elevator _elevator;
        private readonly string _dataDir;
        private McpServer? _server;

        public Toolbox Toolbox { get; }
        public bool Running => _server?.IsRunning == true;
        public int Port => _server?.Port ?? 0;
        public string Url => $"http://127.0.0.1:{Port}/mcp";
        public Elevator Elevator => _elevator;

        /// <summary>Runs the assistant's own command line (agy). Replaceable in tests.</summary>
        public Func<IEnumerable<string>, Task<Proc.Output>> RunAgy { get; set; }

        public ToolHost(AppSettings settings, string dataDir, string exePath)
        {
            _settings = settings;
            _dataDir = dataDir;
            _elevator = new Elevator(dataDir, exePath);
            Toolbox = Toolbox.Create(dataDir, _elevator);
            RunAgy = args =>
            {
                string? agy = AgyProcessManager.LocateAgyExecutable();
                return agy == null ? Task.FromResult(new Proc.Output(-1, "The assistant (agy) was not found on this PC.", false)) : Proc.RunAsync(agy, args, 30);
            };
        }

        public static IReadOnlyList<string> AddArguments(string url, string token) =>
            new[] { "mcp", "add", "--header", "Authorization: Bearer " + token, ServerName, url };

        public static IReadOnlyList<string> RemoveArguments() => new[] { "mcp", "remove", ServerName };

        /// <summary>Starts the server (without telling the assistant). Used at start-up when the switch is already on.</summary>
        public bool StartServer()
        {
            if (Running) return true;
            if (string.IsNullOrEmpty(_settings.WindowsToolsToken)) _settings.WindowsToolsToken = McpServer.NewToken();
            _server = new McpServer(Toolbox.Registry, Toolbox.Journal, _settings.WindowsToolsToken!);
            if (!_server.Start(_settings.WindowsToolsPort)) { _server = null; return false; }
            _settings.WindowsToolsPort = _server.Port;
            return true;
        }

        /// <summary>The user turned the switch on: start the server and register it with the assistant. Returns a message for the user either way.</summary>
        public async Task<(bool Ok, string Message)> EnableAsync()
        {
            if (!StartServer()) return (false, "Could not start the Windows tools on this PC (no free local port).");
            _settings.WindowsToolsEnabled = true;
            _settings.Save();
            var r = await RunAgy(AddArguments(Url, _settings.WindowsToolsToken!));
            if (r.ExitCode != 0)
            {
                // keep the server off if the assistant cannot use it: a half-on state is confusing
                StopServer();
                _settings.WindowsToolsEnabled = false;
                _settings.Save();
                return (false, "The assistant did not accept the Windows tools: " + Short(r.Text));
            }
            return (true, $"Windows tools are on ({Toolbox.Registry.All.Count} tools). The assistant picks them up with its next message.");
        }

        public async Task<(bool Ok, string Message)> DisableAsync()
        {
            _settings.WindowsToolsEnabled = false;
            _settings.Save();
            var r = await RunAgy(RemoveArguments());
            StopServer();
            return r.ExitCode == 0
                ? (true, "Windows tools are off.")
                : (true, "Windows tools are off here. The assistant may still list them until you run: agy mcp remove " + ServerName);
        }

        /// <summary>Called at start-up: if the switch is on, bring the server back on the same port so the assistant's saved address keeps working.</summary>
        public async Task ResumeAsync()
        {
            if (!_settings.WindowsToolsEnabled) return;
            int before = _settings.WindowsToolsPort;
            if (!StartServer()) return;
            if (Port != before) await RunAgy(AddArguments(Url, _settings.WindowsToolsToken!));   // the address changed: update the saved entry
            _settings.Save();
        }

        public void StopServer()
        {
            _server?.Dispose();
            _server = null;
        }

        public void Dispose() => StopServer();

        private static string Short(string s) => s.Length > 300 ? s[..300] + "..." : s;
    }
}
