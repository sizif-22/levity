using System;
using System.Collections.Generic;
using System.IO;

namespace Levity.Services
{
    /// <summary>
    /// Handles saving image attachments sent from the mobile app to disk in the data folder,
    /// so the AI agent can read and inspect them by local file path.
    /// </summary>
    public static class MobileImages
    {
        public static string ImagesDir => Path.Combine(AppSettings.DataDir, "uploads");

        public record IncomingImage(string? Name, string? Type, string? Data);

        /// <summary>
        /// Saves base64 images to %APPDATA%\Levity\uploads and returns a list of absolute paths.
        /// Deletes old files older than 7 days to keep disk clean.
        /// </summary>
        public static List<string> SaveIncomingImages(IEnumerable<IncomingImage> images)
        {
            var savedPaths = new List<string>();
            try
            {
                Directory.CreateDirectory(ImagesDir);
                CleanOldImages(ImagesDir);

                foreach (var img in images)
                {
                    if (string.IsNullOrWhiteSpace(img.Data)) continue;
                    byte[] bytes;
                    try
                    {
                        bytes = Convert.FromBase64String(img.Data);
                    }
                    catch
                    {
                        continue;
                    }

                    if (bytes.Length == 0) continue;

                    string ext = ".jpg";
                    if (img.Type?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ||
                        img.Name?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ext = ".png";
                    }
                    else if (img.Type?.Contains("webp", StringComparison.OrdinalIgnoreCase) == true ||
                             img.Name?.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ext = ".webp";
                    }
                    else if (img.Type?.Contains("gif", StringComparison.OrdinalIgnoreCase) == true ||
                             img.Name?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ext = ".gif";
                    }

                    string filename = $"img_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}";
                    string fullPath = Path.Combine(ImagesDir, filename);
                    File.WriteAllBytes(fullPath, bytes);
                    savedPaths.Add(fullPath);
                }
            }
            catch
            {
                // Never crash the turn on image save failure
            }

            return savedPaths;
        }

        private static void CleanOldImages(string dir)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (File.GetCreationTimeUtc(file) < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Augments a user prompt with pointers to the attached images so the model knows where they are and inspects them.
        /// </summary>
        public static string AttachImagesToPrompt(string prompt, IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return prompt;

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                sb.AppendLine(prompt);
                sb.AppendLine();
            }

            sb.AppendLine(paths.Count == 1
                ? "[Attached 1 image:]"
                : $"[Attached {paths.Count} images:]");

            for (int i = 0; i < paths.Count; i++)
            {
                sb.AppendLine($"- Image {i + 1}: {paths[i]}");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
