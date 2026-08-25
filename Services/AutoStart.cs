using System;
using Microsoft.Win32;

namespace MailPulse.Services
{
    public static class AutoStart
    {
        private const string KeyName = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppName = "MailPulse";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyName))
                return key?.GetValue(AppName) != null;
        }

        public static void Set(bool enable, string exePath = null)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyName))
            {
                if (enable)
                    key.SetValue(AppName, $"\"{exePath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}\"");
                else
                    key.DeleteValue(AppName, false);
            }
        }
    }
}
