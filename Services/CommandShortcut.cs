using System;

namespace AntigravitySidebar.Services
{
    /// <summary>
    /// Turns "/cmd{git status}" (also "/cmd git status", aliases /cli /run /sh) into an instruction
    /// telling the agent to run that exact command. Text after the closing brace is kept as a follow-up.
    /// </summary>
    public static class CommandShortcut
    {
        private static readonly string[] Names = { "/cmd", "/cli", "/run", "/sh" };

        /// <summary>Splits "/cmd{...} follow-up" into the command and the trailing text. False when the prompt is not a command shortcut.</summary>
        public static bool TryParse(string prompt, out string command, out string after)
        {
            command = after = "";
            string text = prompt.Trim();
            foreach (string name in Names)
            {
                if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = text.Substring(name.Length);

                if (rest.StartsWith("{"))
                {
                    int depth = 0, end = -1;
                    for (int i = 0; i < rest.Length; i++)
                    {
                        if (rest[i] == '{') depth++;
                        else if (rest[i] == '}' && --depth == 0) { end = i; break; }
                    }
                    if (end < 0) command = rest.Substring(1);
                    else { command = rest.Substring(1, end - 1); after = rest.Substring(end + 1).Trim(); }
                }
                else if (rest.Length > 0 && char.IsWhiteSpace(rest[0])) command = rest;
                else continue; // e.g. "/cmdx": not our command

                command = command.Trim();
                return command.Length > 0;
            }
            return false;
        }

        /// <summary>Instruction for the agent to run the command; used only when a follow-up needs the AI.</summary>
        public static string Expand(string prompt, string cwd)
        {
            if (!TryParse(prompt, out string command, out string after)) return prompt;
            string expanded =
                $"Run this exact command in the shell with the working directory {cwd}. " +
                "Do not change, extend or substitute it. Show its output and exit code. " +
                "If it fails, show the error and stop; do not try workarounds.\n\n" +
                $"Command:\n```\n{command}\n```";
            if (after.Length > 0) expanded += $"\n\nThen: {after}";
            return expanded;
        }
    }
}
