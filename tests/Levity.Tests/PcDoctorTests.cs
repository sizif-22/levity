using System.Linq;
using Levity.Services.WindowsTools;
using Xunit;

public class PcDoctorTests
{
    private static DoctorFacts Healthy() => new()
    {
        Drives = { new DriveFact("C:", 500, 200) },
        UptimeDays = 3, RamTotalGb = 16, RamFreeGb = 8, StartupEnabled = 5, AdaptersUp = 1, AdaptersWithGateway = 1, DnsWorks = true,
    };

    [Fact]
    public void A_healthy_pc_gets_one_ok_finding()
    {
        var r = PcDoctor.Evaluate(Healthy());
        Assert.Single(r);
        Assert.Equal(Severity.Ok, r[0].Severity);
    }

    [Theory]
    [InlineData(500, 20, Severity.Critical)]   // 4%
    [InlineData(500, 4, Severity.Critical)]    // under 5 GB
    [InlineData(500, 40, Severity.Warning)]    // 8%
    [InlineData(100, 14, Severity.Warning)]    // under 15 GB
    public void Low_disk_space_is_ranked_by_how_low_it_is(double total, double free, Severity expected)
    {
        var f = Healthy();
        f.Drives = new() { new DriveFact("C:", total, free) };
        var r = PcDoctor.Evaluate(f);
        Assert.Equal(expected, r.Single(x => x.Id == "disk-C:").Severity);
    }

    [Fact]
    public void Plenty_of_space_on_a_big_drive_is_not_flagged()
    {
        var f = Healthy();
        f.Drives = new() { new DriveFact("D:", 2000, 300) };
        Assert.DoesNotContain(PcDoctor.Evaluate(f), x => x.Id.StartsWith("disk-"));
    }

    [Fact]
    public void Critical_findings_sort_before_warnings_and_info()
    {
        var f = Healthy();
        f.UptimeDays = 60;
        f.PendingReboot = true;
        f.Drives = new() { new DriveFact("C:", 500, 5) };
        var order = PcDoctor.Evaluate(f).Select(x => x.Severity).ToList();
        Assert.Equal(order.OrderBy(x => x).ToList(), order);
        Assert.Equal(Severity.Critical, order[0]);
    }

    [Fact]
    public void Every_disk_finding_offers_a_read_only_look_and_an_assistant_task_and_changes_nothing_itself()
    {
        var f = Healthy();
        f.Drives = new() { new DriveFact("C:", 500, 5) };
        var fixes = PcDoctor.Evaluate(f).Single(x => x.Id == "disk-C:").Fixes;
        Assert.Contains(fixes, x => x.Kind == "tool" && x.Tool == "disk_usage" && !x.Changes);
        Assert.Contains(fixes, x => x.Kind == "prompt" && x.Prompt!.Contains("ask me before deleting", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unexpected_shutdowns_hand_the_assistant_a_task_that_says_not_to_change_anything()
    {
        var f = Healthy();
        f.Events.Add(new EventFact("Microsoft-Windows-Kernel-Power", 41, 2, "The system rebooted without cleanly shutting down."));
        var x = PcDoctor.Evaluate(f).Single(y => y.Id == "power-41");
        Assert.Equal(Severity.Critical, x.Severity);
        Assert.Contains("Do not change anything", x.Fixes.Single().Prompt);
    }

    [Fact]
    public void Harmless_DCOM_noise_is_ignored_even_when_frequent()
    {
        var f = Healthy();
        f.Events.Add(new EventFact("DCOM", 10016, 400, "permission settings"));
        Assert.Equal(Severity.Ok, PcDoctor.Evaluate(f).Single().Severity);
    }

    [Fact]
    public void A_service_failing_once_is_not_flagged_but_repeatedly_is()
    {
        var f = Healthy();
        f.Events.Add(new EventFact("Service Control Manager", 7031, 1, "x"));
        Assert.Equal(Severity.Ok, PcDoctor.Evaluate(f).Single().Severity);
        f.Events[0] = new EventFact("Service Control Manager", 7031, 3, "The Print Spooler service terminated unexpectedly.");
        Assert.Contains(PcDoctor.Evaluate(f), x => x.Id == "service-7031" && x.Severity == Severity.Warning);
    }

    [Fact]
    public void Disk_errors_in_the_log_are_critical()
    {
        var f = Healthy();
        f.Events.Add(new EventFact("disk", 153, 4, "The IO operation was retried."));
        Assert.Contains(PcDoctor.Evaluate(f), x => x.Severity == Severity.Critical && x.Title.Contains("disk"));
    }

    [Fact]
    public void Dns_failure_with_a_connected_adapter_is_critical_and_no_adapter_is_not_a_dns_finding()
    {
        var f = Healthy();
        f.DnsWorks = false;
        Assert.Equal(Severity.Critical, PcDoctor.Evaluate(f).Single(x => x.Id == "dns").Severity);
        f.AdaptersUp = 0;
        Assert.DoesNotContain(PcDoctor.Evaluate(f), x => x.Id == "dns");
    }

    [Fact]
    public void Path_problems_offer_a_clean_up_that_is_marked_as_a_change()
    {
        var f = Healthy();
        f.PathMissing = 2; f.PathDuplicates = 1;
        var x = PcDoctor.Evaluate(f).Single(y => y.Id == "path");
        Assert.Contains("2 entries point", x.Detail);
        Assert.Contains("1 duplicate", x.Detail);
        Assert.False(x.Fixes.Single(y => y.Tool == "path_check").Changes);
        Assert.True(x.Fixes.Single(y => y.Tool == "path_edit").Changes);
    }

    [Fact]
    public void Thresholds_for_memory_start_up_temp_and_uptime()
    {
        var f = Healthy();
        f.RamFreeGb = 1; f.StartupEnabled = 20; f.TempGb = 5; f.TempPath = "C:\\T"; f.UptimeDays = 45; f.PendingReboot = true;
        var ids = PcDoctor.Evaluate(f).Select(x => x.Id).ToHashSet();
        Assert.Superset(new[] { "ram", "startup", "temp", "uptime", "reboot" }.ToHashSet(), ids);
        f.RamFreeGb = 8; f.StartupEnabled = 12; f.TempGb = 1.9; f.UptimeDays = 29; f.PendingReboot = false;
        Assert.Equal(Severity.Ok, PcDoctor.Evaluate(f).Single().Severity);
    }

    [Fact]
    public void Json_never_carries_the_prompt_or_arguments_to_the_screen_only_labels_and_indexes()
    {
        var f = Healthy();
        f.Drives = new() { new DriveFact("C:", 500, 5) };
        var json = PcDoctor.ToJson(PcDoctor.Evaluate(f), f).ToJsonString();
        Assert.DoesNotContain("ask me before deleting", json);
        Assert.Contains("\"index\"", json);
        Assert.Contains("\"severity\":\"critical\"", json);
    }

    [Fact]
    public void Skipped_checks_are_reported_not_hidden()
    {
        var f = Healthy();
        f.Skipped.Add("event log");
        Assert.Contains("event log", PcDoctor.ToJson(PcDoctor.Evaluate(f), f).ToJsonString());
    }

    [Fact]
    public async System.Threading.Tasks.Task A_real_scan_of_this_pc_finishes_reads_the_drives_and_changes_nothing()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "doctor-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var tb = Toolbox.Create(dir);
            var facts = await PcDoctor.CollectAsync(tb.Registry, tb.Journal);
            Assert.NotEmpty(facts.Drives);
            Assert.NotEmpty(PcDoctor.Evaluate(facts));
            Assert.Empty(tb.Journal.List(10));
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
}
