using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services.WindowsTools
{
    /// <summary>
    /// Admin-only changes (services, hosts file, firewall, Defender exclusions) without a permanent privileged service. Levity writes one
    /// request, starts an elevated copy of itself that runs exactly that one tool, and Windows shows its own prompt on the PC: the person
    /// at the PC approving it IS the approval. Nothing stays running with extra rights.
    /// </summary>
    public static class ElevationPolicy
    {
        public const string OverrideVariable = "LEVITY_ALLOW_ELEVATION_ANYWHERE";

        /// <summary>
        /// An elevated program that lives in a folder ordinary programs can write to can be swapped for something else, so elevation is only
        /// offered when Levity runs from Program Files or Windows, unless the developer turns the check off.
        /// </summary>
        public static string? WhyNotAvailable(string exePath, string? overrideValue = null)
        {
            if (string.Equals(overrideValue ?? Environment.GetEnvironmentVariable(OverrideVariable), "1", StringComparison.Ordinal)) return null;
            string full = Path.GetFullPath(exePath);
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
            {
                string root = Environment.GetFolderPath(folder);
                if (root.Length > 0 && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            }
            return "Administrator changes are only offered when Levity is installed in Program Files, because an elevated program in a folder that other programs can write to could be replaced. (Developers can set " + OverrideVariable + "=1.)";
        }
    }

    /// <summary>The request the normal process hands to the elevated one.</summary>
    public sealed record ElevatedRequest(string Tool, JsonObject Args, string DataDir, string ResultFile, DateTime CreatedUtc);

    /// <summary>Runs inside the elevated copy: reads one request, runs that one tool, writes the result. Deliberately small and strict.</summary>
    public sealed class ElevatedRunner
    {
        public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(5);
        private readonly ToolRegistry _registry;

        public ElevatedRunner(ToolRegistry registry) { _registry = registry; }

        public static string RequestFolder => Path.Combine(AppSettings.DataDir, "elevation");

        public static ElevatedRequest? ReadRequest(string file)
        {
            try
            {
                var n = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (n == null) return null;
                return new ElevatedRequest((string?)n["tool"] ?? "", n["args"] as JsonObject ?? new JsonObject(), (string?)n["dataDir"] ?? "", (string?)n["resultFile"] ?? "",
                    DateTime.TryParse((string?)n["createdUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue);
            }
            catch { return null; }
        }

        public static void WriteRequest(string file, ElevatedRequest r) =>
            File.WriteAllText(file, new JsonObject { ["tool"] = r.Tool, ["args"] = JsonNode.Parse(r.Args.ToJsonString()), ["dataDir"] = r.DataDir, ["resultFile"] = r.ResultFile, ["createdUtc"] = r.CreatedUtc.ToString("o") }.ToJsonString());

        /// <summary>Validates and runs a request file. Returns 0 on a completed run (even if the tool said no), non-zero if the request was refused.</summary>
        public async Task<int> ExecuteAsync(string requestFile, ChangeJournal journal, DateTime? nowUtc = null)
        {
            var req = ReadRequest(requestFile);
            string? resultFile = req?.ResultFile;
            ToolResult result;
            int code = 0;
            if (req == null) { result = ToolResult.Failure("The request could not be read."); code = 2; }
            else if (!IsInside(requestFile, RequestFolderFor(req.DataDir)) || !IsInside(req.ResultFile, RequestFolderFor(req.DataDir))) { result = ToolResult.Failure("The request is not in Levity's elevation folder."); code = 3; }
            else if ((nowUtc ?? DateTime.UtcNow) - req.CreatedUtc > MaxRequestAge || req.CreatedUtc > (nowUtc ?? DateTime.UtcNow).AddMinutes(1)) { result = ToolResult.Failure("The request is too old; ask again."); code = 4; }
            else
            {
                var tool = _registry.Find(req.Tool);
                if (tool == null || !tool.NeedsAdminToChange || tool.Access != ToolAccess.Change)
                { result = ToolResult.Failure("That tool does not run elevated."); code = 5; }
                else
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, tool.TimeoutSeconds)));
                    result = await _registry.CallAsync(req.Tool, req.Args, new ToolContext { Journal = journal, Cancel = limit.Token });
                }
            }
            try
            {
                if (resultFile != null && IsInside(resultFile, RequestFolderFor(req!.DataDir)))
                    File.WriteAllText(resultFile, new JsonObject { ["ok"] = result.Ok, ["text"] = result.Text, ["data"] = result.Data == null ? null : JsonNode.Parse(result.Data.ToJsonString()), ["elevated"] = Elevation.IsElevated }.ToJsonString());
            }
            catch { }
            return code;
        }

        private static string RequestFolderFor(string dataDir) => Path.Combine(dataDir, "elevation");

        private static bool IsInside(string file, string folder)
        {
            try
            {
                string f = Path.GetFullPath(file), d = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                return f.StartsWith(d, StringComparison.OrdinalIgnoreCase) && !f.Substring(d.Length).Contains('\\');
            }
            catch { return false; }
        }
    }

    /// <summary>What "Levity.exe --run-tool file" does. It only does anything if the process really is elevated.</summary>
    public static class ElevatedEntry
    {
        public static int Run(string requestFile)
        {
            if (!Elevation.IsElevated) return 6;   // never run an admin tool without admin rights: a plain copy would only half-do it
            var req = ElevatedRunner.ReadRequest(requestFile);
            string dataDir = req?.DataDir is { Length: > 0 } d ? d : AppSettings.DataDir;
            var box = Toolbox.Create(dataDir);
            return new ElevatedRunner(box.Registry).ExecuteAsync(requestFile, box.Journal).GetAwaiter().GetResult();
        }
    }

    /// <summary>Runs in the normal process: asks Windows to start the elevated copy and waits for its answer.</summary>
    public sealed class Elevator
    {
        private readonly string _dataDir;
        private readonly string _exePath;
        private readonly Func<string, Task<string?>> _launch;

        /// <param name="launch">Starts the elevated process for a request file. Returns null on success or what went wrong. Replaceable in tests.</param>
        public Elevator(string dataDir, string exePath, Func<string, Task<string?>>? launch = null)
        {
            _dataDir = dataDir;
            _exePath = exePath;
            _launch = launch ?? DefaultLaunch;
        }

        public string? WhyNotAvailable => ElevationPolicy.WhyNotAvailable(_exePath);

        /// <summary>How long to wait for the elevated step to report back once the Windows prompt has been dealt with.</summary>
        public int WaitSeconds { get; set; } = 15;

        private async Task<string?> DefaultLaunch(string requestFile)
        {
            try
            {
                var psi = new ProcessStartInfo(_exePath, $"--run-tool \"{requestFile}\"") { Verb = "runas", UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_exePath)! };
                using var p = Process.Start(psi);
                if (p == null) return "Windows did not start the elevated copy.";
                await Task.Run(() => p.WaitForExit(150_000));
                return null;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "The Windows prompt was declined, so nothing was changed."; }
            catch (Exception ex) { return "Could not ask Windows for permission: " + ex.Message; }
        }

        public async Task<ToolResult> RunAsync(ToolRegistry registry, string toolName, JsonObject args, CancellationToken ct)
        {
            var tool = registry.Find(toolName);
            if (tool == null || !tool.NeedsAdminToChange || tool.Access != ToolAccess.Change) return ToolResult.Failure($"'{toolName}' is not a tool that needs administrator rights.");
            string? blocked = WhyNotAvailable;
            if (blocked != null) return ToolResult.Failure(blocked);
            if (Elevation.IsElevated) return await registry.CallAsync(toolName, args, new ToolContext { Journal = new ChangeJournal(Path.Combine(_dataDir, "journal")), Cancel = ct });

            string folder = Path.Combine(_dataDir, "elevation");
            Directory.CreateDirectory(folder);
            PruneOld(folder);
            string id = Guid.NewGuid().ToString("N");
            string request = Path.Combine(folder, id + ".request.json");
            string result = Path.Combine(folder, id + ".result.json");
            ElevatedRunner.WriteRequest(request, new ElevatedRequest(toolName, args, _dataDir, result, DateTime.UtcNow));
            try
            {
                string? err = await _launch(request);
                if (err != null) return ToolResult.Failure(err);
                for (int i = 0; i < WaitSeconds * 10 && !File.Exists(result); i++) await Task.Delay(100, ct);
                if (!File.Exists(result)) return ToolResult.Failure("The elevated step did not report back. Nothing is known to have changed; check the Windows changes list.");
                var n = JsonNode.Parse(File.ReadAllText(result)) as JsonObject;
                bool ok = (bool?)n?["ok"] ?? false;
                string text = (string?)n?["text"] ?? "";
                return ok ? ToolResult.Success(text, n?["data"]) : ToolResult.Failure(text);
            }
            finally
            {
                try { File.Delete(request); } catch { }
                try { File.Delete(result); } catch { }
            }
        }

        private static void PruneOld(string folder)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(folder))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) File.Delete(f);
            }
            catch { }
        }
    }

    /// <summary>The tool the assistant calls to run one admin-only tool elevated. The Windows prompt on the PC is the approval.</summary>
    public sealed class RunElevatedTool : WindowsTool
    {
        private readonly Elevator _elevator;
        private readonly ToolRegistry _registry;

        public RunElevatedTool(Elevator elevator, ToolRegistry registry) { _elevator = elevator; _registry = registry; }

        public override string Name => "run_elevated";
        public override ToolAccess Access => ToolAccess.Change;
        public override int TimeoutSeconds => 240;
        public override string Description =>
            "Run one tool that needs administrator rights (" + string.Join(", ", AdminToolNames(_registry)) + "). Windows shows an approval prompt on the PC and nothing happens unless the person there approves it, so tell the user what is about to be changed first (use the tool's dry_run). Never ask for more than one tool at a time.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["tool"] = Prop("string", "The tool to run elevated.", AdminToolNames(_registry)),
            ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "The arguments for that tool." },
        }, "tool", "arguments");

        private static string[] AdminToolNames(ToolRegistry r) => r.All.Where(t => t.NeedsAdminToChange && t.Access == ToolAccess.Change).Select(t => t.Name).ToArray();

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string tool = RequiredStr(args, "tool");
            var toolArgs = args["arguments"] as JsonObject ?? new JsonObject();
            if (ctx.DryRun)
                return await _registry.CallAsync(tool, new JsonObject(toolArgs.Select(kv => KeyValuePair.Create(kv.Key, kv.Value == null ? null : JsonNode.Parse(kv.Value.ToJsonString())))) { ["dry_run"] = true }, ctx);
            return await _elevator.RunAsync(_registry, tool, toolArgs, ctx.Cancel);
        }
    }
}
