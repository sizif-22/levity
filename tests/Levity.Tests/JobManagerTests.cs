using System.Diagnostics;
using Levity.Services;
using Xunit;

namespace Levity.Tests;

/// <summary>Runs real PowerShell processes (Windows only), as the app does for /cmd{$...}.</summary>
public class JobManagerTests
{
    private static async Task<ShellJob> RunToEnd(JobManager m, string command, int seconds = 60)
    {
        var job = m.Start(command, TestEnvironment.NewTempDir("job"));
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(seconds)), $"'{command}' did not finish in {seconds}s");
        return job;
    }

    [Fact]
    public async Task Captures_output_and_a_zero_exit_code()
    {
        using var m = new JobManager();
        var job = await RunToEnd(m, "Write-Output 'hello levity'");
        Assert.Equal(JobState.Done, job.State);
        Assert.Equal(0, job.ExitCode);
        Assert.Contains("hello levity", job.Tail(1000));
        Assert.NotNull(job.EndedUtc);
    }

    [Fact]
    public async Task A_nonzero_exit_code_marks_the_job_failed()
    {
        using var m = new JobManager();
        var job = await RunToEnd(m, "exit 3");
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(3, job.ExitCode);
    }

    [Fact]
    public async Task Errors_are_captured_alongside_normal_output()
    {
        using var m = new JobManager();
        var job = await RunToEnd(m, "Write-Output 'out'; [Console]::Error.WriteLine('oops')");
        string text = job.Tail(1000);
        Assert.Contains("out", text);
        Assert.Contains("oops", text);
    }

    [Fact]
    public async Task Runs_in_the_requested_folder()
    {
        using var m = new JobManager();
        string dir = TestEnvironment.NewTempDir("cwd");
        var job = m.Start("(Get-Location).Path", dir);
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Contains(Path.GetFileName(dir), job.Tail(1000));
    }

    [Fact]
    public async Task Colour_codes_are_stripped_from_output()
    {
        using var m = new JobManager();
        var job = await RunToEnd(m, "[Console]::Out.WriteLine([char]27 + '[31mred text' + [char]27 + '[0m')");
        string text = job.Tail(1000);
        Assert.Contains("red text", text);
        Assert.False(text.Contains('\u001b'), "ESC must be removed (ordinal check: a culture compare ignores control characters)");
        Assert.DoesNotContain("[31m", text);
    }

    [Fact]
    public async Task Stdin_is_closed_so_prompting_commands_do_not_hang()
    {
        using var m = new JobManager();
        var job = await RunToEnd(m, "$x = [Console]::In.ReadLine(); Write-Output \"got:[$x]\"", seconds: 30);
        // the last line of output can arrive a moment after the process exits when the machine is busy
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!job.Tail(1000).Contains("got:[]") && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        Assert.Contains("got:[]", job.Tail(1000));
    }

    [Fact]
    public async Task A_long_command_is_still_running_after_a_short_wait_and_Stop_ends_it_at_once()
    {
        using var m = new JobManager();
        var job = m.Start("Start-Sleep -Seconds 300", TestEnvironment.NewTempDir("long"));
        job.Background = true;

        Assert.False(await job.WaitAsync(TimeSpan.FromMilliseconds(800)));
        Assert.Equal(JobState.Running, job.State);
        Assert.Single(m.Background);

        var sw = Stopwatch.StartNew();
        Assert.True(m.Stop(job.Id));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Stop took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(JobState.Stopped, job.State);
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Stop_on_an_unknown_job_does_nothing()
    {
        using var m = new JobManager();
        Assert.False(m.Stop(99999));
    }

    [Fact]
    public async Task Stop_kills_orphaned_grandchildren_too()
    {
        // cmd starts a detached child and exits at once, orphaning it (like npm -> node dev servers).
        string dir = TestEnvironment.NewTempDir("orphan");
        string marker = "levity_orphan_" + Guid.NewGuid().ToString("N")[..8];
        // A PowerShell sleeper whose command line carries a unique marker.
        string command = $"cmd /c start /b powershell -NoProfile -Command \"Start-Sleep 300 # {marker}\"; Start-Sleep 300";

        using var m = new JobManager();
        var job = m.Start(command, dir);
        job.Background = true;
        await Task.Delay(4000);
        Assert.True(CountWithMarker(marker) >= 1, "the detached child should be running before Stop");

        m.Stop(job.Id);
        await Task.Delay(2500);
        Assert.Equal(0, CountWithMarker(marker));
    }

    [Fact]
    public async Task Finished_background_jobs_raise_the_event_and_can_be_cleared()
    {
        using var m = new JobManager();
        var finished = new TaskCompletionSource<ShellJob>();
        m.BackgroundFinished += j => finished.TrySetResult(j);

        var job = m.Start("Write-Output done", TestEnvironment.NewTempDir("bg"));
        job.Background = true;
        var raised = await Task.WhenAny(finished.Task, Task.Delay(30000));
        Assert.Same(finished.Task, raised);

        m.ClearFinished();
        Assert.Empty(m.Background);
    }

    [Fact]
    public async Task Inline_jobs_can_be_discarded_so_they_never_reach_the_panel()
    {
        using var m = new JobManager();
        var job = await RunToEnd(m, "Write-Output quick");
        m.Discard(job);
        Assert.Empty(m.Background);
    }

    [Fact]
    public async Task Output_is_capped_to_the_tail()
    {
        using var m = new JobManager();
        var job = await RunToEnd(m, "1..3000 | ForEach-Object { 'line ' + $_ + ('x' * 100) }");
        string tail = job.Tail(5000);
        Assert.True(tail.Length <= 5002);
        Assert.Contains("line 3000", tail);
    }

    [Fact]
    public async Task Dispose_stops_running_jobs()
    {
        var m = new JobManager();
        var job = m.Start("Start-Sleep -Seconds 300", TestEnvironment.NewTempDir("dispose"));
        await Task.Delay(1500);
        m.Dispose();
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(JobState.Stopped, job.State);
    }

    private static int CountWithMarker(string marker)
    {
        int n = 0;
        foreach (var p in Process.GetProcessesByName("powershell"))
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={p.Id}");
                foreach (System.Management.ManagementBaseObject o in searcher.Get())
                    if ((o["CommandLine"] as string ?? "").Contains(marker)) n++;
            }
            catch { /* process exited meanwhile */ }
        }
        return n;
    }
}
