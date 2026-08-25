using System;
using System.Windows;

namespace MailPulse
{
    public class App : Application
    {
        private Services.ConfigService _config = new Services.ConfigService();
        private Services.MailMonitorService _monitor;
        private System.Windows.Forms.NotifyIcon _tray;
        private UI.SettingsWindow _settings;

        [STAThread]
        public static void Main()
        {
            var app = new App();
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;   // keep running in tray

            _config.Load();
            UI.Theme.Apply(UI.Theme.ParseMode(_config.Current.ThemeMode));
            Services.Logger.Info("App started");

            // Tray icon
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "MailPulse - 邮件验证码监控",
                Visible = true
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("设置", null, (s, ev) => ShowSettings());
            menu.Items.Add("暂停监控", null, (s, ev) =>
            {
                if (_monitor != null) { _monitor.Stop(); _monitor = null; _tray.Text = "MailPulse - 已暂停"; }
                else { StartMonitoring(); }
            });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, ev) => Shutdown());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, ev) => ShowSettings();

            StartMonitoring();
            ShowSettings();   // open main window on launch (tray keeps running after close)
        }

        private void StartMonitoring()
        {
            _monitor?.Stop();
            _monitor = new Services.MailMonitorService(_config);
            _monitor.OnNewMatchedMail += r => Dispatcher.BeginInvoke(new Action(() =>
            {
                var toast = new UI.ToastWindow(r, () => { });
                toast.Show();
            }));
            _monitor.Start();
            _tray.Text = "MailPulse - 监控中";
        }

        public void RestartMonitoring() { _config.Load();
            UI.Theme.Apply(UI.Theme.ParseMode(_config.Current.ThemeMode));
            Services.Logger.Info("App started"); StartMonitoring(); }

        private void ShowSettings()
        {
            if (_settings == null || !_settings.IsVisible)
            {
                _settings = new UI.SettingsWindow(_config);
                _settings.Show();
            }
            else _settings.Activate();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _monitor?.Stop(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            base.OnExit(e);
        }
    }
}




