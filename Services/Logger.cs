using System;
using System.IO;

namespace MailPulse.Services
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        public static string LogDir =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailPulse", "logs");

        public static void Info(string msg) => Write("INFO ", msg);
        public static void Warn(string msg) => Write("WARN ", msg);
        public static void Error(string msg, Exception ex = null)
            => Write("ERROR", msg + (ex != null ? " | " + ex.GetType().Name + ": " + ex.Message : ""));

        private static void Write(string level, string msg)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogDir);
                    var path = Path.Combine(LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    File.AppendAllText(path,
                        $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
                    // simple retention: keep last 7 days only
                    foreach (var f in Directory.GetFiles(LogDir, "*.log"))
                    {
                        if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd",
                            null, System.Globalization.DateTimeStyles.None, out var d) &&
                            (DateTime.Now - d).TotalDays > 7)
                            File.Delete(f);
                    }
                }
            }
            catch { /* never crash app for logging */ }
        }
    }
}
