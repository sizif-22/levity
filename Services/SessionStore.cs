using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AntigravitySidebar.Services
{
    /// <summary>One saved chat: what the UI replays plus the agy conversation id it can resume.</summary>
    public sealed class ChatSession
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Cwd { get; set; } = "";
        public string? ConversationId { get; set; }
        public DateTime Created { get; set; }
        public DateTime Updated { get; set; }
        public int Messages { get; set; }
        /// <summary>Raw host events (user_message / agy_event JSON lines), exactly as replayed to the UI.</summary>
        public List<string> History { get; set; } = new();
    }

    /// <summary>Chat sessions as one JSON file each under the settings folder (%APPDATA%\AntigravitySidebar\sessions).</summary>
    public sealed class SessionStore
    {
        private readonly string _dir = Path.Combine(AppSettings.DataDir, "sessions");
        private readonly Dictionary<string, ChatSession> _meta = new();

        public SessionStore()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                foreach (string file in Directory.EnumerateFiles(_dir, "*.json"))
                {
                    try
                    {
                        var s = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(file));
                        if (s == null || s.Id.Length == 0) continue;
                        s.History = new();
                        _meta[s.Id] = s;
                    }
                    catch { }
                }
            }
            catch { }
        }

        public IReadOnlyList<ChatSession> List() => _meta.Values.OrderByDescending(s => s.Updated).ToList();

        private string PathFor(string id) => Path.Combine(_dir, id + ".json");

        public void Save(ChatSession s)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string tmp = PathFor(s.Id) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s));
                File.Move(tmp, PathFor(s.Id), overwrite: true);
                _meta[s.Id] = new ChatSession
                {
                    Id = s.Id, Title = s.Title, Cwd = s.Cwd, ConversationId = s.ConversationId,
                    Created = s.Created, Updated = s.Updated, Messages = s.Messages
                };
            }
            catch { }
        }

        public ChatSession? Load(string id)
        {
            try
            {
                if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                return JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(PathFor(id)));
            }
            catch { return null; }
        }

        public bool Delete(string id)
        {
            try
            {
                if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
                File.Delete(PathFor(id));
                return _meta.Remove(id);
            }
            catch { return false; }
        }
    }
}
