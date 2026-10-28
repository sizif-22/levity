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

            SettingsSetTool.RegisterUndo(journal);
            EnvironmentSetTool.RegisterUndo(journal);
            ProcessEndTool.RegisterUndo(journal);
            return new Toolbox(registry, journal, profile);
        }
    }
}
