using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Levity.Services
{
    public class NotebookStore
    {
        public record NoteImage(
            [property: JsonPropertyName("id")] string Id,
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("mimeType")] string MimeType,
            [property: JsonPropertyName("url")] string Url,
            [property: JsonPropertyName("data")] string? Data = null,
            [property: JsonPropertyName("size")] long? Size = null
        );

        public record NoteItem(
            [property: JsonPropertyName("id")] string Id,
            [property: JsonPropertyName("title")] string Title,
            [property: JsonPropertyName("content")] string Content,
            [property: JsonPropertyName("images")] List<NoteImage>? Images,
            [property: JsonPropertyName("createdAt")] long CreatedAt,
            [property: JsonPropertyName("updatedAt")] long UpdatedAt,
            [property: JsonPropertyName("pinned")] bool? Pinned = false
        );

        private readonly string _baseDir;
        private readonly string _notesFilePath;
        private readonly string _imagesDir;
        private readonly object _lock = new();

        public NotebookStore(string? baseDir = null)
        {
            _baseDir = baseDir ?? Path.Combine(AppSettings.DataDir, "notebook");
            _notesFilePath = Path.Combine(_baseDir, "notes.json");
            _imagesDir = Path.Combine(_baseDir, "images");
        }

        public string BaseDir => _baseDir;
        public string ImagesDir => _imagesDir;

        public List<NoteItem> GetNotes()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_notesFilePath)) return new List<NoteItem>();
                    string json = File.ReadAllText(_notesFilePath);
                    var notes = JsonSerializer.Deserialize<List<NoteItem>>(json);
                    return (notes ?? new List<NoteItem>())
                        .OrderByDescending(n => n.Pinned == true)
                        .ThenByDescending(n => n.UpdatedAt)
                        .ToList();
                }
                catch
                {
                    return new List<NoteItem>();
                }
            }
        }

        public List<NoteItem> SaveNote(NoteItem note)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_baseDir);
                Directory.CreateDirectory(_imagesDir);

                var existing = GetNotes();
                var processedImages = new List<NoteImage>();

                if (note.Images != null)
                {
                    foreach (var img in note.Images)
                    {
                        string imageId = string.IsNullOrWhiteSpace(img.Id) ? Guid.NewGuid().ToString("N") : img.Id;
                        string filename = $"{imageId}_{CleanFileName(img.Name)}";
                        string diskPath = Path.Combine(_imagesDir, filename);

                        // If data is supplied (new or updated image), save it to disk
                        if (!string.IsNullOrWhiteSpace(img.Data))
                        {
                            try
                            {
                                byte[] bytes = Convert.FromBase64String(img.Data);
                                File.WriteAllBytes(diskPath, bytes);
                                processedImages.Add(new NoteImage(
                                    Id: imageId,
                                    Name: img.Name,
                                    MimeType: string.IsNullOrWhiteSpace(img.MimeType) ? "image/jpeg" : img.MimeType,
                                    Url: $"api/notebook/images/{filename}",
                                    Data: img.Data, // keep in reply for immediate client render if needed
                                    Size: bytes.Length
                                ));
                                continue;
                            }
                            catch { }
                        }

                        // Otherwise keep existing reference
                        processedImages.Add(img with { Id = imageId });
                    }
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var noteToSave = new NoteItem(
                    Id: string.IsNullOrWhiteSpace(note.Id) ? Guid.NewGuid().ToString("N") : note.Id,
                    Title: string.IsNullOrWhiteSpace(note.Title) ? "Untitled Note" : note.Title.Trim(),
                    Content: note.Content ?? "",
                    Images: processedImages.Select(i => i with { Data = null }).ToList(), // Strip heavy base64 from disk file
                    CreatedAt: note.CreatedAt > 0 ? note.CreatedAt : now,
                    UpdatedAt: now,
                    Pinned: note.Pinned == true
                );

                int idx = existing.FindIndex(n => n.Id == noteToSave.Id);
                if (idx >= 0)
                {
                    existing[idx] = noteToSave;
                }
                else
                {
                    existing.Insert(0, noteToSave);
                }

                string json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_notesFilePath, json);

                // Return sorted notes with images
                return existing
                    .OrderByDescending(n => n.Pinned == true)
                    .ThenByDescending(n => n.UpdatedAt)
                    .ToList();
            }
        }

        public List<NoteItem> DeleteNote(string noteId)
        {
            lock (_lock)
            {
                var existing = GetNotes();
                var toRemove = existing.FirstOrDefault(n => n.Id == noteId);
                if (toRemove != null)
                {
                    if (toRemove.Images != null)
                    {
                        foreach (var img in toRemove.Images)
                        {
                            try
                            {
                                string filename = $"{img.Id}_{CleanFileName(img.Name)}";
                                string path = Path.Combine(_imagesDir, filename);
                                if (File.Exists(path)) File.Delete(path);
                            }
                            catch { }
                        }
                    }

                    existing.RemoveAll(n => n.Id == noteId);
                    Directory.CreateDirectory(_baseDir);
                    string json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_notesFilePath, json);
                }

                return existing;
            }
        }

        public byte[]? GetImageBytes(string filename)
        {
            try
            {
                string safe = Path.GetFileName(filename);
                string path = Path.Combine(_imagesDir, safe);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string CleanFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "image.jpg";
            var invalids = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalids.Contains(c) ? '_' : c).ToArray());
            return clean.Length > 60 ? clean.Substring(clean.Length - 60) : clean;
        }
    }
}
