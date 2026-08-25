using System;
using System.Windows;
using System.Windows.Controls;

namespace MailPulse.UI
{
    public class AccountDialog : Window
    {
        private static readonly string[] MsDomains =
            { "@outlook.com", "@live.com", "@live.cn", "@hotmail.com", "@msn.com", "@passport.com" };

        private TextBox _tbName, _tbHost, _tbUser, _tbPass, _tbPort, _tbInterval;
        private ComboBox _cbProtocol, _cbPreset;
        private CheckBox _chkSsl, _chkEnabled;
        private TextBlock _oauthLabel, _oauthStatus, _codeText;
        private StackPanel _oauthPanel, _codePanel;
        private string _oauthRefreshToken;
        private bool _useOAuth;

        private Models.AccountConfig _editing;

        public Models.AccountConfig ResultAccount() => Build();

        public AccountDialog(Models.AccountConfig editing)
        {
            _editing = editing;
            Title = editing == null ? "添加邮箱账号" : "编辑账号: " + (editing.Name ?? "");
            Width = 500; Height = 592;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Theme.BgB;

            var g = new Grid { Margin = new Thickness(20) };
            for (int i = 0; i < 11; i++)
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int r = 0;

            // 预设
            AddLabel(g, r, "预设");
            _cbPreset = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
            _cbPreset.Items.Add("(手动)");
            foreach (var k in Models.AccountConfig.Presets.Keys) _cbPreset.Items.Add(k);
            _cbPreset.SelectedIndex = 0;
            Theme.StyleComboBox(_cbPreset, 200);
            _cbPreset.SelectionChanged += OnPresetChanged;
            Grid.SetRow(_cbPreset, r); Grid.SetColumn(_cbPreset, 1); g.Children.Add(_cbPreset); r++;

            // 名称
            AddLabel(g, r, "名称");
            _tbName = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbName);
            Grid.SetRow(_tbName, r); Grid.SetColumn(_tbName, 1); g.Children.Add(_tbName); r++;

            // 协议
            AddLabel(g, r, "协议");
            _cbProtocol = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
            _cbProtocol.Items.Add("IMAP"); _cbProtocol.Items.Add("POP3");
            _cbProtocol.SelectedIndex = 0;
            Theme.StyleComboBox(_cbProtocol, 200);
            _cbProtocol.SelectionChanged += (s, e) => { if (_cbPreset.SelectedIndex > 0) ApplyPreset(); UpdatePort(); };
            Grid.SetRow(_cbProtocol, r); Grid.SetColumn(_cbProtocol, 1); g.Children.Add(_cbProtocol); r++;

            // 微软 OAuth（仅微软邮箱时显示）
            _oauthLabel = AddLabel(g, r, "微软授权");
            _oauthPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 3, 0, 3) };
            var oauthRow = new StackPanel { Orientation = Orientation.Horizontal };
            Button oauthBtn = null;
            oauthBtn = Theme.CreateButton("微软 OAuth 登录", async () =>
            {
                try
                {
                    oauthBtn.IsEnabled = false;
                    _oauthStatus.Text = "获取设备码...";
                    _codePanel.Visibility = Visibility.Collapsed;
                    var start = await Services.MicrosoftOAuthService.StartDeviceLoginAsync();
                    _oauthStatus.Text = "在浏览器打开 microsoft.com/link 输入代码：";
                    _codeText.Text = start.UserCode;
                    _codePanel.Visibility = Visibility.Visible;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(start.VerificationUri) { UseShellExecute = true }); } catch { }
                    var result = await Services.MicrosoftOAuthService.PollForTokenAsync(start);
                    if (result.Success)
                    {
                        _oauthRefreshToken = result.RefreshToken;
                        _useOAuth = true;
                        _oauthStatus.Text = "✓ 授权成功";
                        _codePanel.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        _useOAuth = false;
                        _oauthStatus.Text = "✗ " + result.Error;
                        _codePanel.Visibility = Visibility.Collapsed;
                    }
                }
                catch (Exception ex) { _oauthStatus.Text = "✗ " + ex.Message; _codePanel.Visibility = Visibility.Collapsed; }
                finally { oauthBtn.IsEnabled = true; }
            }, true);
            _oauthStatus = new TextBlock
            {
                Text = "",
                Foreground = Theme.TextDimB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
                FontSize = 12
            };
            oauthRow.Children.Add(oauthBtn);
            oauthRow.Children.Add(_oauthStatus);
            _oauthPanel.Children.Add(oauthRow);

            _codePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _codeText = new TextBlock
            {
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.CodeB,
                VerticalAlignment = VerticalAlignment.Center
            };
            var copyBtn = Theme.CreateButton("复制代码", () =>
            {
                try { Clipboard.SetText(_codeText.Text); } catch { }
                _oauthStatus.Text = "代码已复制，去浏览器粘贴吧";
            });
            _codePanel.Children.Add(_codeText);
            _codePanel.Children.Add(copyBtn);
            _oauthPanel.Children.Add(_codePanel);

            Grid.SetRow(_oauthPanel, r); Grid.SetColumn(_oauthPanel, 1); g.Children.Add(_oauthPanel); r++;

            // 服务器
            AddLabel(g, r, "服务器");
            _tbHost = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbHost);
            Grid.SetRow(_tbHost, r); Grid.SetColumn(_tbHost, 1); g.Children.Add(_tbHost); r++;

            // 端口 + SSL
            AddLabel(g, r, "端口");
            var portPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            _tbPort = new TextBox { Width = 80 };
            Theme.StyleTextBox(_tbPort, 80);
            portPanel.Children.Add(_tbPort);
            _chkSsl = new CheckBox { Content = "SSL", IsChecked = true, Foreground = Theme.TextB, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            portPanel.Children.Add(_chkSsl);
            Grid.SetRow(portPanel, r); Grid.SetColumn(portPanel, 1); g.Children.Add(portPanel); r++;

            // 用户名
            AddLabel(g, r, "用户名");
            _tbUser = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbUser);
            _tbUser.TextChanged += (s, e) => UpdateOAuthVisibility();
            Grid.SetRow(_tbUser, r); Grid.SetColumn(_tbUser, 1); g.Children.Add(_tbUser); r++;

            // 密码
            AddLabel(g, r, "密码/授权码");
            _tbPass = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbPass);
            Grid.SetRow(_tbPass, r); Grid.SetColumn(_tbPass, 1); g.Children.Add(_tbPass); r++;

            // 轮询间隔
            AddLabel(g, r, "轮询间隔(秒)");
            _tbInterval = new TextBox { Text = "45", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbInterval, 80);
            Grid.SetRow(_tbInterval, r); Grid.SetColumn(_tbInterval, 1); g.Children.Add(_tbInterval); r++;

            // 启用复选框
            _chkEnabled = new CheckBox
            {
                Content = "启用此账号",
                IsChecked = true,
                Foreground = Theme.TextB,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(_chkEnabled, r); Grid.SetColumn(_chkEnabled, 0);
            Grid.SetColumnSpan(_chkEnabled, 2);
            g.Children.Add(_chkEnabled); r++;

            // 按钮行
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var ok = Theme.CreateButton("确定", () =>
            {
                if (string.IsNullOrWhiteSpace(_tbUser.Text) || string.IsNullOrWhiteSpace(_tbHost.Text))
                { MessageBox.Show("服务器和用户名不能为空。"); return; }
                DialogResult = true;
            }, true);
            var cancel = Theme.CreateButton("取消", () => DialogResult = false);
            btnPanel.Children.Add(ok);
            btnPanel.Children.Add(cancel);
            Grid.SetRow(btnPanel, r); Grid.SetColumnSpan(btnPanel, 2);
            g.Children.Add(btnPanel);

            Content = g;

            if (editing != null)
            {
                _tbName.Text = editing.Name ?? "";
                _cbProtocol.SelectedIndex = editing.Protocol == Models.MailProtocol.Imap ? 0 : 1;
                _tbHost.Text = editing.Host ?? "";
                _tbPort.Text = editing.Port.ToString();
                _chkSsl.IsChecked = editing.UseSsl;
                _tbUser.Text = editing.User ?? "";
                _tbInterval.Text = editing.PollIntervalSeconds.ToString();
                _chkEnabled.IsChecked = editing.Enabled;
                _tbPass.Text = "";
                if (editing.UseOAuth)
                {
                    _useOAuth = true;
                    _oauthStatus.Text = "✓ 已授权 (OAuth)";
                }
            }

            UpdateOAuthVisibility();
        }

        private TextBlock AddLabel(Grid grid, int row, string text)
        {
            var l = Theme.Label(text);
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            grid.Children.Add(l);
            return l;
        }

        private void UpdateOAuthVisibility()
        {
            bool isMs = false;
            if (_cbPreset != null && string.Equals(_cbPreset.SelectedItem as string, "Outlook", StringComparison.OrdinalIgnoreCase))
                isMs = true;
            string user = _tbUser?.Text ?? "";
            foreach (var d in MsDomains)
                if (user.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0) { isMs = true; break; }

            var vis = isMs ? Visibility.Visible : Visibility.Collapsed;
            if (_oauthLabel != null) _oauthLabel.Visibility = vis;
            if (_oauthPanel != null) _oauthPanel.Visibility = vis;
        }

        private void OnPresetChanged(object s, SelectionChangedEventArgs e)
        {
            if (_cbPreset.SelectedIndex > 0) { ApplyPreset(); UpdatePort(); }
            UpdateOAuthVisibility();
        }

        private void ApplyPreset()
        {
            string key = _cbPreset.SelectedItem as string;
            if (!string.IsNullOrEmpty(key) && Models.AccountConfig.Presets.TryGetValue(key, out var p))
            {
                _tbHost.Text = p.host;
                if (string.IsNullOrEmpty(_tbName.Text)) _tbName.Text = key + " 邮箱";
            }
        }

        private void UpdatePort()
        {
            bool imap = _cbProtocol.SelectedIndex == 0;
            _tbPort.Text = imap ? "993" : "995";
        }

        private Models.AccountConfig Build()
        {
            bool isImap = _cbProtocol.SelectedIndex == 0;
            int.TryParse(_tbPort.Text, out int port);
            int.TryParse(_tbInterval.Text, out int interval);
            if (interval < 15) interval = 15;
            return new Models.AccountConfig
            {
                Name = _tbName.Text,
                Protocol = isImap ? Models.MailProtocol.Imap : Models.MailProtocol.Pop3,
                Host = _tbHost.Text.Trim(),
                Port = port == 0 ? (isImap ? 993 : 995) : port,
                UseSsl = _chkSsl.IsChecked ?? true,
                User = _tbUser.Text.Trim(),
                EncryptedPassword = _useOAuth
                    ? ""
                    : (string.IsNullOrEmpty(_tbPass.Text) ? (_editing?.EncryptedPassword ?? "") : Services.SecureStore.Protect(_tbPass.Text)),
                UseOAuth = _useOAuth,
                EncryptedRefreshToken = _useOAuth
                    ? Services.SecureStore.Protect(_oauthRefreshToken)
                    : (_editing?.EncryptedRefreshToken ?? ""),
                OAuthUserEmail = _useOAuth ? _tbUser.Text.Trim() : (_editing?.OAuthUserEmail ?? ""),
                PollIntervalSeconds = interval,
                Enabled = _chkEnabled.IsChecked ?? true,
            };
        }
    }
}

