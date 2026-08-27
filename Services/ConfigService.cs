using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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
        private static readonly object IoGate = new object();
        private static readonly Mutex FileMutex = new Mutex(false, @"Local\MailPulse.ConfigFile.v1");
        public static string DirPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailPulse");
        public static string FilePath = Path.Combine(DirPath, "config.json");

        public Models.AppConfig Current { get; set; } = new Models.AppConfig();

        public void Load()
        {
            Directory.CreateDirectory(DirPath);
            if (File.Exists(FilePath))
            {
                try
                {
                    string json = WithFileLock(() => RetryIo(() => File.ReadAllText(FilePath)));
                    var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.AppConfig>(json);
                    if (loaded != null) Current = loaded;
                }
                catch (IOException ex)
                {
                    Logger.Warn("config load deferred because file is busy: " + ex.Message);
                    return; // Preserve the current in-memory config instead of replacing it with an empty one.
                }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.Warn("config load denied: " + ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn("config parse failed, using defaults: " + ex.Message);
                    Current = new Models.AppConfig();
                }
            }
            if (Current.Rules == null || Current.Rules.Count == 0)
                Current.Rules = new List<Models.RuleConfig>(Models.AppConfig.DefaultRules());
            foreach (var account in Current.Accounts ?? new List<Models.AccountConfig>())
            {
                if (!account.UseOAuth || string.IsNullOrWhiteSpace(account.EncryptedRefreshToken)) continue;
                if (string.IsNullOrWhiteSpace(account.OAuthClientId) && string.IsNullOrWhiteSpace(account.EncryptedImapRefreshToken))
                    account.EncryptedImapRefreshToken = account.EncryptedRefreshToken;
                else if (!string.IsNullOrWhiteSpace(account.OAuthClientId) && string.IsNullOrWhiteSpace(account.EncryptedSmtpRefreshToken))
                    account.EncryptedSmtpRefreshToken = account.EncryptedRefreshToken;
            }
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
            WithFileLock(() =>
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(Current, Newtonsoft.Json.Formatting.Indented);
                RetryIo(() => WriteAtomic(json));
            });
        }

        public bool TrySave(string context)
        {
            try { Save(); return true; }
            catch (Exception ex)
            {
                Logger.Warn("config save deferred" + (string.IsNullOrWhiteSpace(context) ? "" : " (" + context + ")") +
                    ": " + ex.Message);
                return false;
            }
        }

        private static void WriteAtomic(string json)
        {
            string tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null, true);
                else File.Move(tempPath, FilePath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static T WithFileLock<T>(Func<T> action)
        {
            bool acquired = false;
            try
            {
                try { acquired = FileMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("等待配置文件写入锁超时。");
                lock (IoGate) return action();
            }
            finally { if (acquired) FileMutex.ReleaseMutex(); }
        }

        private static void WithFileLock(Action action)
        {
            WithFileLock(() => { action(); return true; });
        }

        private static T RetryIo<T>(Func<T> action)
        {
            IOException last = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try { return action(); }
                catch (IOException ex)
                {
                    last = ex;
                    if (attempt < 5) Thread.Sleep(40 * (attempt + 1));
                }
            }
            throw last;
        }

        private static void RetryIo(Action action)
        {
            RetryIo(() => { action(); return true; });
        }
    }
}


