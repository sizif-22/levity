using Levity.Services;
using Xunit;

namespace Levity.Tests;

/// <summary>When Levity gives memory back, and the assistant that starts only when needed.</summary>
public class MemoryPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(10, 0, 600)]
    [InlineData(5, 0, 300)]
    [InlineData(10, 30, 30)]     // the seconds setting (a testing aid) wins
    [InlineData(0, 45, 45)]
    public void The_idle_limit_comes_from_the_settings(double minutes, double seconds, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MemoryPolicy.AgentIdleLimit(minutes, seconds));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    [InlineData(0, -1)]
    public void Zero_or_less_means_keep_the_assistant_ready_and_never_stop_it(double minutes, double seconds)
    {
        Assert.Null(MemoryPolicy.AgentIdleLimit(minutes, seconds));
    }

    [Fact]
    public void The_default_is_ten_minutes()
    {
        Assert.Equal(10, MemoryPolicy.DefaultAgentIdleMinutes);
    }

    [Fact]
    public void An_idle_assistant_is_stopped_only_after_the_quiet_spell()
    {
        Assert.False(MemoryPolicy.ShouldStopAgent(true, false, false, Now.AddMinutes(-9), Now, Ten));
        Assert.True(MemoryPolicy.ShouldStopAgent(true, false, false, Now.AddMinutes(-10), Now, Ten));
        Assert.True(MemoryPolicy.ShouldStopAgent(true, false, false, Now.AddHours(-3), Now, Ten));
    }

    [Theory]
    [InlineData(false, false, false)]   // not running: nothing to stop
    [InlineData(true, true, false)]     // answering right now
    [InlineData(true, false, true)]     // a command or a queued message is waiting
    public void A_working_or_absent_assistant_is_never_stopped(bool running, bool answering, bool otherWork)
    {
        Assert.False(MemoryPolicy.ShouldStopAgent(running, answering, otherWork, Now.AddDays(-1), Now, Ten));
    }

    [Fact]
    public void With_no_idle_limit_the_assistant_stays_ready()
    {
        Assert.False(MemoryPolicy.ShouldStopAgent(true, false, false, Now.AddDays(-1), Now, null));
    }

    [Fact]
    public void The_web_view_sleeps_only_when_hidden_still_and_unneeded_for_a_few_seconds()
    {
        var wait = MemoryPolicy.HiddenSuspendDelay;
        Assert.True(MemoryPolicy.ShouldSuspendWebView(false, false, false, wait));
        Assert.False(MemoryPolicy.ShouldSuspendWebView(false, false, false, wait - TimeSpan.FromMilliseconds(1)), "not straight away: a quick hide and show must not flicker");
        Assert.False(MemoryPolicy.ShouldSuspendWebView(true, false, false, wait), "visible");
        Assert.False(MemoryPolicy.ShouldSuspendWebView(false, true, false, wait), "mid-animation");
        Assert.False(MemoryPolicy.ShouldSuspendWebView(false, false, true, wait), "an answer or command needs the page");
    }

    [Fact]
    public void The_host_trims_itself_once_per_hide_after_the_delay()
    {
        var wait = MemoryPolicy.HiddenTrimDelay;
        Assert.True(MemoryPolicy.ShouldTrimHost(false, wait, false));
        Assert.False(MemoryPolicy.ShouldTrimHost(false, wait - TimeSpan.FromSeconds(1), false));
        Assert.False(MemoryPolicy.ShouldTrimHost(false, wait, true), "only once");
        Assert.False(MemoryPolicy.ShouldTrimHost(true, wait, false), "never while visible");
    }

    [Fact]
    public void The_page_sleeps_before_the_host_trims_so_the_trim_sees_the_smaller_process()
    {
        Assert.True(MemoryPolicy.HiddenSuspendDelay < MemoryPolicy.HiddenTrimDelay);
    }
}

public class LazyAssistantTests
{
    [Fact]
    public void Preparing_points_the_assistant_at_a_folder_and_conversation_without_starting_it()
    {
        string dir = TestEnvironment.NewTempDir("agy");
        var agy = new AgyProcessManager(dir);

        agy.Prepare(dir, "conv-123");

        Assert.False(agy.IsRunning, "nothing is started until the first message");
        Assert.Equal(dir, agy.CurrentWorkingDirectory);
        Assert.Equal("conv-123", agy.CurrentConversationId);
    }

    [Fact]
    public void Preparing_without_an_id_means_a_fresh_conversation()
    {
        string dir = TestEnvironment.NewTempDir("agy");
        var agy = new AgyProcessManager(dir);
        agy.Prepare(dir, "old");
        agy.Prepare(null, null);
        Assert.Null(agy.CurrentConversationId);
        Assert.Equal(dir, agy.CurrentWorkingDirectory);
        agy.Prepare(null, "  ");
        Assert.Null(agy.CurrentConversationId);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_ignored()
    {
        string dir = TestEnvironment.NewTempDir("agy");
        var agy = new AgyProcessManager(dir);
        agy.Prepare(Path.Combine(dir, "nope-" + Guid.NewGuid().ToString("N")), null);
        Assert.Equal(dir, agy.CurrentWorkingDirectory);
    }

    [Fact]
    public void Stopping_a_stopped_assistant_is_harmless_and_keeps_the_conversation()
    {
        string dir = TestEnvironment.NewTempDir("agy");
        var agy = new AgyProcessManager(dir);
        agy.Prepare(dir, "keep-me");
        agy.Stop();
        agy.Interrupt();
        Assert.False(agy.IsRunning);
        Assert.False(agy.IsBusy);
        Assert.Equal("keep-me", agy.CurrentConversationId);
    }

    [Fact]
    public void The_activity_clock_starts_at_creation()
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        var agy = new AgyProcessManager(TestEnvironment.NewTempDir("agy"));
        Assert.True(agy.LastActivityUtc >= before);
        Assert.True(agy.LastActivityUtc <= DateTime.UtcNow.AddSeconds(2));
    }
}

/// <summary>The real start, stop and resume of the assistant, against a stand-in program (no tokens, no account).</summary>
[Collection("assistant-environment")]
public class AssistantLifecycleTests
{
    private const string FakeScript = @"
param([Parameter(ValueFromRemainingArguments=$true)]$Rest)
Add-Content -Path (Join-Path $PSScriptRoot 'launches.log') -Value ($Rest -join ' ')
$conv = 'conv-new'
for ($i = 0; $i -lt $Rest.Count; $i++) { if ($Rest[$i] -eq '--conversation') { $conv = $Rest[$i + 1] } }
if ($conv -eq 'conv-new') { $conv = 'conv-' + [guid]::NewGuid().ToString('N').Substring(0, 8) }
[Console]::Out.WriteLine('{""event"":""init"",""conversation_id"":""' + $conv + '""}')
while (($line = [Console]::In.ReadLine()) -ne $null) { [Console]::Out.WriteLine('{""event"":""result"",""result"":{""status"":""SUCCESS""}}') }
";

    private static string Install(out string logFile)
    {
        string dir = TestEnvironment.NewTempDir("fakeagy");
        File.WriteAllText(Path.Combine(dir, "fake-agy.ps1"), FakeScript);
        File.WriteAllText(Path.Combine(dir, "agy.cmd"), "@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-agy.ps1\" %*\r\n");
        logFile = Path.Combine(dir, "launches.log");
        return Path.Combine(dir, "agy.cmd");
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    [Fact]
    public async Task The_assistant_starts_on_the_first_message_resumes_after_an_idle_stop_and_starts_fresh_after_a_new_chat()
    {
        string agyPath = Install(out string log);
        string? previous = Environment.GetEnvironmentVariable("AGY_PATH");
        Environment.SetEnvironmentVariable("AGY_PATH", agyPath);
        string work = TestEnvironment.NewTempDir("work");
        var agy = new AgyProcessManager(work);
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();   // written from the assistant's reader threads
        agy.OnStatusChanged += statuses.Enqueue;
        try
        {
            // nothing runs before the first message
            agy.Prepare(work, null);
            Assert.False(agy.IsRunning);
            Assert.False(File.Exists(log), "no assistant was launched yet");

            // the first message starts it, on a fresh conversation, at the fast level
            Assert.True(agy.SendPrompt("hello"));
            Assert.Contains("Starting the assistant...", statuses);
            Assert.True(await Until(() => agy.CurrentConversationId != null && !agy.IsBusy), "init and result should arrive");
            string first = agy.CurrentConversationId!;
            var launches = File.ReadAllLines(log);
            Assert.Single(launches);
            Assert.DoesNotContain("--conversation", launches[0]);
            Assert.Contains("--effort low", launches[0]);

            // an idle stop (what the housekeeping does) keeps the conversation...
            agy.Stop();
            Assert.False(agy.IsRunning);
            Assert.Equal(first, agy.CurrentConversationId);
            await Task.Delay(600);
            Assert.DoesNotContain("Process Stopped", statuses);   // a deliberate stop is not an error the user should see

            // ...and the next message resumes it
            Assert.True(agy.SendPrompt("again"));
            Assert.True(await Until(() => !agy.IsBusy && agy.IsRunning));
            launches = File.ReadAllLines(log);
            Assert.Equal(2, launches.Length);
            Assert.Contains("--conversation " + first, launches[1]);
            Assert.Equal(first, agy.CurrentConversationId);

            // a new chat forgets the conversation and starts nothing; the next message is a fresh one
            agy.Prepare(null, null);
            Assert.False(agy.IsRunning);
            Assert.Null(agy.CurrentConversationId);
            Assert.Equal(2, File.ReadAllLines(log).Length);
            Assert.True(agy.SendPrompt("new topic"));
            Assert.True(await Until(() => agy.CurrentConversationId != null && !agy.IsBusy));
            launches = File.ReadAllLines(log);
            Assert.Equal(3, launches.Length);
            Assert.DoesNotContain("--conversation", launches[2]);
            Assert.NotEqual(first, agy.CurrentConversationId);

            // stopping a turn in progress and sending again also resumes the same conversation
            string second = agy.CurrentConversationId!;
            agy.Interrupt();
            Assert.False(agy.IsRunning);
            Assert.Equal(second, agy.CurrentConversationId);
        }
        finally
        {
            agy.Stop();
            Environment.SetEnvironmentVariable("AGY_PATH", previous);
        }
    }

    [Fact]
    public async Task The_activity_clock_moves_when_a_message_is_sent_and_when_the_assistant_answers()
    {
        string agyPath = Install(out _);
        string? previous = Environment.GetEnvironmentVariable("AGY_PATH");
        Environment.SetEnvironmentVariable("AGY_PATH", agyPath);
        var agy = new AgyProcessManager(TestEnvironment.NewTempDir("work"));
        try
        {
            var before = agy.LastActivityUtc;
            await Task.Delay(30);
            Assert.True(agy.SendPrompt("hi"));
            Assert.True(agy.LastActivityUtc > before);
            Assert.True(await Until(() => !agy.IsBusy));
            var afterAnswer = agy.LastActivityUtc;
            Assert.True(afterAnswer >= before);
            Assert.False(MemoryPolicy.ShouldStopAgent(agy.IsRunning, agy.IsBusy, false, agy.LastActivityUtc, DateTime.UtcNow, TimeSpan.FromMinutes(10)), "just used: not idle");
            Assert.True(MemoryPolicy.ShouldStopAgent(agy.IsRunning, agy.IsBusy, false, agy.LastActivityUtc, DateTime.UtcNow.AddMinutes(11), TimeSpan.FromMinutes(10)), "eleven quiet minutes later it is");
        }
        finally
        {
            agy.Stop();
            Environment.SetEnvironmentVariable("AGY_PATH", previous);
        }
    }
}
