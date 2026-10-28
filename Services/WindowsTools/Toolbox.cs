using System.IO;

namespace Levity.Services.WindowsTools
{
    /// <summary>The Windows toolbox: every tool, the change journal and the machine profile, wired together.</summary>
    public sealed class Toolbox
    {
        public ToolRegistry Registry { get; }
        public ChangeJournal Journal { get; }
        public MachineProfile Profile { get; }

        private Toolbox(ToolRegistry registry, ChangeJournal journal, MachineProfile profile)
        {
            Registry = registry;
            Journal = journal;
            Profile = profile;
        }

        /// <summary>Builds the toolbox with its files under <paramref name="dataDir"/> (journal folder and machine.json).</summary>
        public static Toolbox Create(string dataDir)
        {
            var journal = new ChangeJournal(Path.Combine(dataDir, "journal"));
            var profile = new MachineProfile(Path.Combine(dataDir, "machine.json"));
            var registry = new ToolRegistry();

            registry.Add(new MachineProfileTool(profile));
            registry.Add(new SettingsGetTool());
            registry.Add(new SettingsSetTool());
            registry.Add(new StartupAppsListTool());
            registry.Add(new StartupAppsSetTool());
            registry.Add(new EnvironmentGetTool());
            registry.Add(new EnvironmentSetTool());
            registry.Add(new PathCheckTool());
            registry.Add(new PathEditTool());
            registry.Add(new DiskUsageTool());
            registry.Add(new NetworkInfoTool());
            registry.Add(new ProcessesTool());
            registry.Add(new ProcessEndTool());
            registry.Add(new ServicesListTool());
            registry.Add(new ServiceControlTool());
            registry.Add(new ScheduledTasksListTool());
            registry.Add(new ScheduledTaskControlTool());
            registry.Add(new WingetQueryTool());
            registry.Add(new WingetChangeTool());
            registry.Add(new PrintersListTool());
            registry.Add(new PrinterControlTool());
            registry.Add(new EventLogTool());
            registry.Add(new HostsFileTool());
            registry.Add(new FirewallRulesTool());
            registry.Add(new FirewallChangeTool());
            registry.Add(new DefenderStatusTool());
            registry.Add(new DefenderChangeTool());
            registry.Add(new RegistryReadTool());
            registry.Add(new CertificatesTool());
            registry.Add(new DefaultAppsTool());
            registry.Add(new OpenSettingsTool());
            registry.Add(new ChangesListTool());
            registry.Add(new ChangesRevertTool());

            SettingsSetTool.RegisterUndo(journal);
            EnvironmentSetTool.RegisterUndo(journal);
            ProcessEndTool.RegisterUndo(journal);
            ServiceControlTool.RegisterUndo(journal);
            ScheduledTaskControlTool.RegisterUndo(journal);
            WingetChangeTool.RegisterUndo(journal);
            HostsFileTool.RegisterUndo(journal);
            PrinterControlTool.RegisterUndo(journal);
            FirewallChangeTool.RegisterUndo(journal);
            DefenderChangeTool.RegisterUndo(journal);
            return new Toolbox(registry, journal, profile);
        }
    }
}
