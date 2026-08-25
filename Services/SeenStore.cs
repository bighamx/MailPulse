using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MailPulse.Services
{
    /// <summary>Persistent store of already-alerted mails (key = accountId + "|" + messageId).</summary>
    public class SeenStore
    {
        private static readonly string FilePath =
            Path.Combine(ConfigService.DirPath, "seen.json");

        private readonly HashSet<string> _keys = new HashSet<string>();
        private readonly object _lock = new object();

        public void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(FilePath));
                    if (list != null) lock (_lock) { _keys.Clear(); foreach (var k in list) _keys.Add(k); }
                }
            }
            catch { /* ignore corrupt file */ }
        }

        public bool Contains(string key)
        {
            lock (_lock) return _keys.Contains(key);
        }

        public void Add(string key)
        {
            bool added;
            lock (_lock) { added = _keys.Add(key); }
            if (added) Save();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigService.DirPath);
                List<string> snapshot;
                lock (_lock) { snapshot = _keys.ToList(); }
                File.WriteAllText(FilePath, Newtonsoft.Json.JsonConvert.SerializeObject(snapshot, Newtonsoft.Json.Formatting.Indented));
            }
            catch { /* never crash on persistence */ }
        }
    }
}
