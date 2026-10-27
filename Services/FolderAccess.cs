using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntigravitySidebar.Services
{
    /// <summary>Which folders a paired phone may act on. At the PC itself there is no restriction; these rules apply only to phone requests.</summary>
    public static class FolderAccess
    {
        private static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";

        /// <summary>True if <paramref name="path"/> is <paramref name="parent"/> or inside it (so "C:\Work\app-evil" is not inside "C:\Work\app").</summary>
        public static bool IsInside(string path, string parent)
        {
            try { return Norm(path).StartsWith(Norm(parent), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static bool Same(string a, string b)
        {
            try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>Opening a folder in the IDE: the projects folder (and below) or the current project.</summary>
        public static bool PhoneMayOpenInIde(string path, string projectsRoot, string currentFolder) =>
            IsInside(path, projectsRoot) || Same(path, currentFolder);

        /// <summary>Using a folder for a diagram: the projects folder (and below), the home folder, the current project, or a folder already picked on the PC.</summary>
        public static bool PhoneMayUseForDiagram(string path, string projectsRoot, string home, string currentFolder, IEnumerable<string> recent) =>
            IsInside(path, projectsRoot) || Same(path, home) || Same(path, currentFolder) || recent.Any(r => Same(path, r));
    }
}
