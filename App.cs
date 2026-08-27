using System;
using System.Windows;

namespace MailPulse
{
    public class App : Application
    {
        private Services.ConfigService _config = new Services.ConfigService();
        private Services.MailMonitorService _monitor;
        private System.Windows.Forms.NotifyIcon _tray;
        private System.Windows.Forms.ToolStripMenuItem _monitorToggleMenuItem;
        private UI.SettingsWindow _settings;
        private UI.MailCenterWindow _mailCenter;
        private bool _monitoringPaused;

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

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) =>
                {
                    var window = sender as Window;
                    if (window == null || window.Icon != null) return;
                    try
                    {
                        window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                            new Uri("pack://application:,,,/Assets/MailPulse.ico", UriKind.Absolute));
                    }
                    catch { }
                }));

            _config.Load();
            UI.Theme.Apply(UI.Theme.ParseMode(_config.Current.ThemeMode));
            Services.Logger.Info("App started");

            // Tray icon
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadApplicationIcon(),
                Text = "MailPulse - 邮件验证码监控",
                Visible = true
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("设置", null, (s, ev) => ShowSettings());
            menu.Items.Add("邮件中心", null, (s, ev) => ShowMailCenter());
            _monitorToggleMenuItem = new System.Windows.Forms.ToolStripMenuItem("暂停监控");
            _monitorToggleMenuItem.Click += (s, ev) =>
            {
                if (_monitoringPaused) StartMonitoring();
                else PauseMonitoring();
            };
            menu.Items.Add(_monitorToggleMenuItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, ev) => Shutdown());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, ev) => ShowSettings();

            StartMonitoring();
            ShowSettings();   // open main window on launch (tray keeps running after close)
        }

        private static System.Drawing.Icon LoadApplicationIcon()
        {
            try
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)
                    ?? System.Drawing.SystemIcons.Application;
            }
            catch { return System.Drawing.SystemIcons.Application; }
        }

        private void StartMonitoring()
        {
            _monitor?.Stop();
            var monitor = new Services.MailMonitorService(_config);
            _monitor = monitor;
            monitor.OnNewMatchedMail += r => Dispatcher.BeginInvoke(new Action(() =>
            {
                // A cancelled monitor can finish an in-flight request. Ignore its late result.
                if (!ReferenceEquals(_monitor, monitor) || _monitoringPaused) return;
                var toast = new UI.ToastWindow(r, () => { });
                toast.Show();
            }));
            monitor.Start();
            _monitoringPaused = false;
            _tray.Text = "MailPulse - 监控中";
            if (_monitorToggleMenuItem != null) _monitorToggleMenuItem.Text = "暂停监控";
        }

        private void PauseMonitoring()
        {
            var monitor = _monitor;
            _monitor = null;
            _monitoringPaused = true;
            try { monitor?.Stop(); } catch (Exception ex) { Services.Logger.Error("pause monitoring failed", ex); }
            _tray.Text = "MailPulse - 已暂停";
            if (_monitorToggleMenuItem != null) _monitorToggleMenuItem.Text = "恢复监控";
            Services.Logger.Info("Monitoring paused by user");
            _tray.ShowBalloonTip(1500, "MailPulse", "邮件监控已暂停", System.Windows.Forms.ToolTipIcon.Info);
        }

        public void RestartMonitoring() { _config.Load();
            UI.Theme.Apply(UI.Theme.ParseMode(_config.Current.ThemeMode));
            if (!_monitoringPaused) StartMonitoring(); }

        public bool TemporarilyStopMonitoring()
        {
            if (_monitoringPaused) return false;
            var monitor = _monitor;
            _monitor = null;
            try { monitor?.Stop(); } catch { }
            return true;
        }

        public void ResumeMonitoring(bool shouldResume)
        {
            if (shouldResume && !_monitoringPaused) StartMonitoring();
        }

        private void ShowSettings()
        {
            if (_settings == null || !_settings.IsVisible)
            {
                _settings = new UI.SettingsWindow(_config);
                _settings.Show();
            }
            else _settings.Activate();
        }

        public void ShowMailCenter()
        {
            if (_mailCenter == null || !_mailCenter.IsVisible)
            {
                _mailCenter = new UI.MailCenterWindow(_config);
                _mailCenter.Show();
            }
            else _mailCenter.Activate();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _monitor?.Stop(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            base.OnExit(e);
        }
    }
}




