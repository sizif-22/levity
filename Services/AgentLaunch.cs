namespace Levity.Services
{
    /// <summary>How Levity starts the assistant: the command line, the speed setting, and the environment its shells inherit.</summary>
    public static class AgentLaunch
    {
        /// <summary>Set to "1" in everything the assistant starts, so a shell profile can skip its slow extras (see docs/PERFORMANCE.md).</summary>
        public const string EnvMarker = "LEVITY_AGENT";

        public const string Fast = "low";
        public const string Deep = "high";

        /// <summary>Only the three levels the assistant understands; anything else falls back to fast.</summary>
        public static string NormalizeEffort(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
        {
            "medium" => "medium",
            "high" => "high",
            _ => Fast,
        };

        public const string Auto = "auto";
        public const string Plan = "plan";

        /// <summary>"plan" makes the assistant think and propose without changing anything; anything else is the normal working mode.</summary>
        public static string NormalizeMode(string? value) => string.Equals((value ?? "").Trim(), Plan, StringComparison.OrdinalIgnoreCase) ? Plan : Auto;

        public static string Arguments(string? resumeConversationId, string? effort, string? mode = null)
        {
            string args = "--dangerously-skip-permissions --input-format=stream-json --output-format=stream-json --print=\"\" --effort " + NormalizeEffort(effort);
            if (NormalizeMode(mode) == Plan) args += " --mode plan";
            // The id comes from the assistant itself, but it ends up on a command line, so only plain id characters are let through.
            if (!string.IsNullOrWhiteSpace(resumeConversationId) && IsPlainId(resumeConversationId))
                args += " --conversation " + resumeConversationId;
            return args;
        }

        private static bool IsPlainId(string id)
        {
            foreach (char c in id)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
            return true;
        }

        /// <summary>
        /// Added once to the first message of a conversation. Every tool call costs a full round trip to the model (several seconds), and
        /// every command costs a shell start, so fewer, bigger steps are much faster.
        /// </summary>
        public const string EfficiencyHint =
            "[Work efficiently: every tool call costs a model round trip (several seconds), so use as few as possible. " +
            "If AGENTS.md has a \"Where things live\" section, start there and go straight to the files it names; do not search for what it already tells you. " +
            "Read a file WHOLE in one call (Get-Content -Raw, up to about 800 lines) and never read it again in slices with -Skip/-First; " +
            "to look at several files use one command that prints them all (Get-Content a,b,c) instead of one read_file each, and do not re-read a file you already read in this conversation. " +
            "Search once for the whole repo with a case-insensitive pattern (rg -n -i \"a|b|c\"), then read the files it found; do not run a series of narrower greps. " +
            "Prefer one command that does the whole job (not recursive Get-ChildItem) and do not re-verify work that already succeeded. " +
            "Run only the tests that cover what you changed (dotnet test --filter, vitest run <file>, pnpm test <file>), and type-check or build once at the end, not after every edit. " +
            "If a program fails to start or show a window after two tries, stop and tell me what you saw instead of trying more ways.]\n\n";

        public static string WithHint(string prompt, bool firstOfConversation, string? pcFacts = null)
        {
            if (!firstOfConversation) return prompt;
            string facts = string.IsNullOrWhiteSpace(pcFacts) ? "" : "[" + pcFacts.Trim() + "]\n\n";
            return EfficiencyHint + facts + prompt;
        }
    }
}
