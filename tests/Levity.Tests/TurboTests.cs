using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Levity.Services;
using Xunit;

namespace Levity.Tests
{
    public class TurboTests
    {
        private const string TwoTasks = "Here you go:\n```json\n{\"contracts\":\"event is named x_done\",\"tasks\":[" +
            "{\"id\":\"t1\",\"title\":\"C# service\",\"files\":[\"Services/A.cs\"],\"instructions\":\"write A\"}," +
            "{\"id\":\"t2\",\"title\":\"React screen\",\"files\":[\"web/src/B.tsx\"],\"instructions\":\"write B\"}]}\n```";

        [Fact]
        public void A_plan_wrapped_in_text_and_a_code_fence_is_read()
        {
            var plan = Turbo.ParsePlan(TwoTasks);
            Assert.NotNull(plan);
            Assert.Equal(2, plan!.Tasks.Count);
            Assert.Equal("event is named x_done", plan.Contracts);
            Assert.Equal("web/src/B.tsx", plan.Tasks[1].Files[0]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("no json here")]
        [InlineData("{\"tasks\": 5}")]
        [InlineData("{\"tasks\":[{\"id\":\"t1\",\"files\":[\"a\"]}]}")]   // no instructions: nothing to do
        [InlineData("{ broken")]
        public void An_unusable_answer_is_not_a_plan(string reply) => Assert.Null(Turbo.ParsePlan(reply));

        [Theory]
        [InlineData("../secret.txt")]
        [InlineData("C:\\Windows\\x")]
        [InlineData("")]
        public void A_task_cannot_claim_a_path_outside_the_project(string path) => Assert.Null(Turbo.NormalizePath(path));

        [Fact]
        public void Paths_are_made_relative_with_forward_slashes()
        {
            Assert.Equal("a/b.cs", Turbo.NormalizePath(".\\a\\b.cs"));
            Assert.Equal("a/b.cs", Turbo.NormalizePath("/a/b.cs"));
        }

        private static TurboTask T(string id, params string[] files) => new(id, id, files, "do " + id);

        [Fact]
        public void Tasks_that_share_a_file_are_merged_so_two_workers_never_edit_the_same_file()
        {
            var plan = Turbo.Normalize(new TurboPlan("", new[] { T("a", "x.cs", "y.cs"), T("b", "y.cs"), T("c", "z.cs") }), 4);
            Assert.Equal(2, plan.Tasks.Count);
            var merged = plan.Tasks.Single(t => t.Files.Contains("y.cs"));
            Assert.Contains("x.cs", merged.Files);
            Assert.DoesNotContain("z.cs", merged.Files);
        }

        [Fact]
        public void A_folder_and_a_file_inside_it_overlap()
        {
            var plan = Turbo.Normalize(new TurboPlan("", new[] { T("a", "web/src"), T("b", "web/src/App.tsx") }), 4);
            Assert.Single(plan.Tasks);
        }

        [Fact]
        public void Tasks_that_name_no_files_cannot_be_proven_separate_so_they_become_one()
        {
            var plan = Turbo.Normalize(new TurboPlan("", new[] { T("a"), T("b"), T("c", "z.cs") }), 4);
            Assert.Equal(2, plan.Tasks.Count);
        }

        [Fact]
        public void The_number_of_workers_is_capped_by_merging_the_smallest()
        {
            var many = Enumerable.Range(1, 7).Select(i => T("t" + i, "f" + i + ".cs")).ToArray();
            var plan = Turbo.Normalize(new TurboPlan("", many), 3);
            Assert.Equal(3, plan.Tasks.Count);
            Assert.Equal(7, plan.Tasks.SelectMany(t => t.Files).Distinct().Count());   // no file was lost in the merging
        }

        [Fact]
        public void A_worker_is_told_its_own_files_the_contracts_and_what_the_others_do()
        {
            var plan = Turbo.Normalize(Turbo.ParsePlan(TwoTasks)!, 4);
            string prompt = Turbo.WorkerPrompt("build it", plan, plan.Tasks[1]);
            Assert.Contains("YOUR TASK: t2", prompt);
            Assert.Contains("web/src/B.tsx", prompt);
            Assert.Contains("event is named x_done", prompt);
            Assert.Contains("t1: C# service", prompt);
            Assert.Contains("Do not run git add", prompt);
        }

        [Fact]
        public async Task The_workers_really_run_at_the_same_time()
        {
            int running = 0, peak = 0;
            async Task<AgentRun> Fake(string folder, string prompt, string effort, CancellationToken ct)
            {
                if (prompt.StartsWith("[TURBO PLANNER")) return new AgentRun(true, TwoTasks, 0);
                int now = Interlocked.Increment(ref running);
                int p;
                while (now > (p = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, now, p);
                await Task.Delay(150, ct);
                Interlocked.Decrement(ref running);
                return new AgentRun(true, "changed a file", 0.15);
            }
            var lines = new List<string>();
            var result = await Turbo.RunAsync("build it", "C:\\p", Fake, lines.Add, CancellationToken.None);
            Assert.True(result.Split);
            Assert.Equal(2, result.Workers);
            Assert.Equal(0, result.Failed);
            Assert.Equal(2, peak);
            Assert.Contains("Turbo finished", result.Markdown);
            Assert.Contains(lines, l => l.Contains("Split into 2 tasks"));
        }

        [Fact]
        public async Task A_job_that_cannot_be_split_goes_back_to_the_normal_assistant_without_running_workers()
        {
            int workers = 0;
            Task<AgentRun> Fake(string folder, string prompt, string effort, CancellationToken ct)
            {
                if (prompt.StartsWith("[TURBO PLANNER"))
                    return Task.FromResult(new AgentRun(true, "{\"tasks\":[{\"id\":\"t1\",\"files\":[\"a.cs\"],\"instructions\":\"x\"}]}", 0));
                Interlocked.Increment(ref workers);
                return Task.FromResult(new AgentRun(true, "", 0));
            }
            var result = await Turbo.RunAsync("small fix", "C:\\p", Fake, _ => { }, CancellationToken.None);
            Assert.False(result.Split);
            Assert.Equal(0, workers);
        }

        [Fact]
        public async Task A_failed_planner_falls_back_instead_of_failing_the_message()
        {
            var result = await Turbo.RunAsync("x", "C:\\p", (f, p, e, c) => Task.FromResult(new AgentRun(false, "boom", 0)), _ => { }, CancellationToken.None);
            Assert.False(result.Split);
        }

        [Fact]
        public async Task One_failed_worker_is_reported_and_the_others_still_finish()
        {
            Task<AgentRun> Fake(string folder, string prompt, string effort, CancellationToken ct)
            {
                if (prompt.StartsWith("[TURBO PLANNER")) return Task.FromResult(new AgentRun(true, TwoTasks, 0));
                if (prompt.Contains("YOUR TASK: t1")) throw new InvalidOperationException("agy crashed");
                return Task.FromResult(new AgentRun(true, "ok", 1));
            }
            var result = await Turbo.RunAsync("x", "C:\\p", Fake, _ => { }, CancellationToken.None);
            Assert.True(result.Split);
            Assert.Equal(1, result.Failed);
            Assert.Contains("1 failed task", result.Markdown);
            Assert.Contains("agy crashed", result.Markdown);
        }

        [Fact]
        public async Task Stopping_cancels_the_run()
        {
            using var cts = new CancellationTokenSource();
            async Task<AgentRun> Fake(string folder, string prompt, string effort, CancellationToken ct)
            {
                if (prompt.StartsWith("[TURBO PLANNER")) return new AgentRun(true, TwoTasks, 0);
                cts.Cancel();
                await Task.Delay(5000, ct);
                return new AgentRun(true, "", 0);
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Turbo.RunAsync("x", "C:\\p", Fake, _ => { }, cts.Token));
        }
    }
}
