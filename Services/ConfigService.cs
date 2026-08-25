using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MailPulse.Services
{
    public static class SecureStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MailPulse.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return null;
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
    }

    public class ConfigService
    {
        public static string DirPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailPulse");
        public static string FilePath = Path.Combine(DirPath, "config.json");

        public Models.AppConfig Current { get; set; } = new Models.AppConfig();

        public void Load()
        {
            Directory.CreateDirectory(DirPath);
            if (File.Exists(FilePath))
            {
                try { Current = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.AppConfig>(File.ReadAllText(FilePath)) ?? new Models.AppConfig(); }
                catch { Current = new Models.AppConfig(); }
            }
            if (Current.Rules == null || Current.Rules.Count == 0)
                Current.Rules = new List<Models.RuleConfig>(Models.AppConfig.DefaultRules());
            // Dedupe rules by (Name + body patterns) to heal configs corrupted by older builds
            Current.Rules = Current.Rules
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                .GroupBy(r => (r.Name ?? "").Trim() + "|" + string.Join(";", r.BodyPatterns ?? new List<string>()))
                .Select(g => g.First())
                .ToList();
        }

        public void Save()
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePath, Newtonsoft.Json.JsonConvert.SerializeObject(Current, Newtonsoft.Json.Formatting.Indented));
        }
    }
}


