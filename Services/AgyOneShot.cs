using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services
{
    /// <summary>Runs one message on a separate, short-lived assistant process and returns what it said. Used by Turbo, one per task.</summary>
    public static class AgyOneShot
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

        public static async Task<AgentRun> RunAsync(string folder, string prompt, string effort, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new StringBuilder();
            var manager = new AgyProcessManager(folder) { Effort = AgentLaunch.NormalizeEffort(effort) };
            manager.OnEventReceived += line =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var evt)) return;
                    switch (evt.GetString())
                    {
                        case "step_update":
                            if (root.TryGetProperty("step_update", out var su) && su.TryGetProperty("step_type", out var st) && st.GetString() == "agent_response"
                                && su.TryGetProperty("text_delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                                reply.Append(delta.GetString());
                            break;
                        case "result":
                            bool failed = root.TryGetProperty("result", out var res) && res.TryGetProperty("status", out var s) && s.GetString() == "ERROR";
                            done.TrySetResult(!failed);
                            break;
                    }
                }
                catch { }
            };
            manager.OnStatusChanged += text =>
            {
                if (text.Contains("Stopped") || text.Contains("Error") || text.Contains("Failed")) done.TrySetResult(false);
            };
            try
            {
                if (!manager.Start() || !manager.SendPrompt(prompt)) return new AgentRun(false, "Could not start the assistant.", 0);
                using var reg = ct.Register(() => { try { manager.Stop(); } catch { } done.TrySetResult(false); });
                var finished = await Task.WhenAny(done.Task, Task.Delay(Timeout, ct));
                ct.ThrowIfCancellationRequested();
                if (finished != done.Task) return new AgentRun(false, "Timed out.", (DateTime.UtcNow - started).TotalSeconds);
                return new AgentRun(done.Task.Result, reply.ToString(), (DateTime.UtcNow - started).TotalSeconds);
            }
            finally { try { manager.Stop(); } catch { } }
        }
    }
}
