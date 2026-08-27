using System;
using System.Windows;
using System.Windows.Controls;

namespace MailPulse.UI
{
    public class AccountDialog : Window
    {
        private static readonly string[] MsDomains =
            { "@outlook.com", "@live.com", "@live.cn", "@hotmail.com", "@msn.com", "@passport.com" };

        private TextBox _tbName, _tbHost, _tbUser, _tbPass, _tbPort, _tbInterval, _tbSmtpHost, _tbSmtpPort, _tbOAuthClientId;
        private ComboBox _cbProtocol, _cbPreset, _cbOAuthMode;
        private CheckBox _chkSsl, _chkSmtpSsl, _chkEnabled;
        private TextBlock _oauthLabel, _oauthStatus, _codeText, _oauthClientIdLabel;
        private Button _oauthRegistrationButton;
        private StackPanel _oauthPanel, _codePanel;
        private string _imapRefreshToken, _graphRefreshToken;
        private string _authorizedOAuthClientId;
        private bool _useOAuth;

        private Models.AccountConfig _editing;

        public Models.AccountConfig ResultAccount() => Build();

        public AccountDialog(Models.AccountConfig editing)
        {
            _editing = editing;
            _useOAuth = editing != null && editing.UseOAuth;
            _authorizedOAuthClientId = editing?.OAuthClientId ?? "";
            Title = editing == null ? "添加邮箱账号" : "编辑账号: " + (editing.Name ?? "");
            Width = 520; Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Theme.BgB;

            var g = new Grid { Margin = new Thickness(20) };
            for (int i = 0; i < 13; i++)
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
            _oauthPanel.Children.Add(new TextBlock
            {
                Text = "授权方式",
                Foreground = Theme.TextDimB,
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 4)
            });
            _cbOAuthMode = new ComboBox { Margin = new Thickness(0, 0, 0, 7) };
            _cbOAuthMode.Items.Add("快速登录（用于读取，无需注册 Entra）");
            _cbOAuthMode.Items.Add("自有 Entra + Microsoft Graph（读取和发送）");
            _cbOAuthMode.SelectedIndex = string.IsNullOrWhiteSpace(_authorizedOAuthClientId) ? 0 : 1;
            Theme.StyleComboBox(_cbOAuthMode);
            _oauthPanel.Children.Add(_cbOAuthMode);
            _oauthPanel.Children.Add(new TextBlock
            {
                Text = "推荐使用自有 Entra + Microsoft Graph，可统一读取、标记、删除和发送邮件。",
                Foreground = Theme.TextDimB,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 7)
            });
            _oauthClientIdLabel = new TextBlock
            {
                Text = "Microsoft Entra 应用客户端 ID（不是密钥）",
                Foreground = Theme.TextDimB,
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 4)
            };
            _oauthPanel.Children.Add(_oauthClientIdLabel);
            _tbOAuthClientId = new TextBox { Margin = new Thickness(0, 0, 0, 7) };
            Theme.StyleTextBox(_tbOAuthClientId);
            _oauthPanel.Children.Add(_tbOAuthClientId);
            _cbOAuthMode.SelectionChanged += (s, e) => UpdateOAuthModeUi();
            var oauthRow = new StackPanel { Orientation = Orientation.Horizontal };
            Button oauthBtn = null;
            oauthBtn = Theme.CreateButton("微软 OAuth 登录", async () =>
            {
                try
                {
                    Theme.SetButtonLoading(oauthBtn, true, "等待授权…");
                    _oauthStatus.Text = "获取设备码...";
                    _codePanel.Visibility = Visibility.Collapsed;
                    string requestedClientId = _cbOAuthMode.SelectedIndex == 1 ? _tbOAuthClientId.Text.Trim() : "";
                    var start = await Services.MicrosoftOAuthService.StartDeviceLoginAsync(requestedClientId);
                    _oauthStatus.Text = "在浏览器打开 microsoft.com/link 输入代码：";
                    _codeText.Text = start.UserCode;
                    _codePanel.Visibility = Visibility.Visible;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(start.VerificationUri) { UseShellExecute = true }); } catch { }
                    var result = await Services.MicrosoftOAuthService.PollForTokenAsync(start);
                    if (result.Success)
                    {
                        bool smtpAuthorization = !string.IsNullOrWhiteSpace(requestedClientId);
                        if (smtpAuthorization)
                        {
                            _graphRefreshToken = result.RefreshToken;
                            _authorizedOAuthClientId = requestedClientId;
                        }
                        else
                            _imapRefreshToken = result.RefreshToken;
                        _useOAuth = true;
                        if (!string.IsNullOrWhiteSpace(result.UserEmail))
                            _tbUser.Text = result.UserEmail;
                        if (_editing != null)
                            Services.MicrosoftOAuthService.RememberAccessToken(
                                _editing.Id, result.AccessToken, result.RefreshToken, result.ExpiresAtUtc, false, smtpAuthorization);
                        _oauthStatus.Text = smtpAuthorization
                            ? "✓ Microsoft Graph 读取和发送授权成功" + (string.IsNullOrWhiteSpace(result.UserEmail) ? "" : "：" + result.UserEmail)
                            : "✓ 快速读取授权成功；发送授权保持不变" + (string.IsNullOrWhiteSpace(result.UserEmail) ? "" : "：" + result.UserEmail);
                        _codePanel.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        _oauthStatus.Text = "✗ " + result.Error;
                        _codePanel.Visibility = Visibility.Collapsed;
                    }
                }
                catch (Exception ex) { _oauthStatus.Text = "✗ " + ex.Message; _codePanel.Visibility = Visibility.Collapsed; }
                finally { Theme.SetButtonLoading(oauthBtn, false); }
            }, true);
            _oauthStatus = new TextBlock
            {
                Text = "",
                Foreground = Theme.TextDimB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            };
            oauthRow.Children.Add(oauthBtn);
            _oauthRegistrationButton = Theme.CreateButton("注册说明", () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app") { UseShellExecute = true }); } catch { }
            });
            oauthRow.Children.Add(_oauthRegistrationButton);
            _oauthPanel.Children.Add(oauthRow);
            _oauthPanel.Children.Add(_oauthStatus);

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

            // Incoming server: host, port and transport security share one row.
            AddLabel(g, r, "接收服务器");
            var incomingRow = BuildServerRow(out _tbHost, out _tbPort, out _chkSsl, "993");
            Grid.SetRow(incomingRow, r); Grid.SetColumn(incomingRow, 1); g.Children.Add(incomingRow); r++;

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

            // Outgoing server uses the same compact layout as the incoming server.
            AddLabel(g, r, "发送服务器");
            var outgoingRow = BuildServerRow(out _tbSmtpHost, out _tbSmtpPort, out _chkSmtpSsl, "465");
            Grid.SetRow(outgoingRow, r); Grid.SetColumn(outgoingRow, 1); g.Children.Add(outgoingRow); r++;

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

            Content = new ScrollViewer
            {
                Content = g,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            if (editing != null)
            {
                _tbName.Text = editing.Name ?? "";
                _cbProtocol.SelectedIndex = editing.Protocol == Models.MailProtocol.Imap ? 0 : 1;
                _tbHost.Text = editing.Host ?? "";
                _tbPort.Text = editing.Port.ToString();
                _chkSsl.IsChecked = editing.UseSsl;
                _tbUser.Text = editing.User ?? "";
                _tbOAuthClientId.Text = editing.OAuthClientId ?? "";
                _cbOAuthMode.SelectedIndex = string.IsNullOrWhiteSpace(editing.OAuthClientId) ? 0 : 1;
                _tbSmtpHost.Text = string.IsNullOrWhiteSpace(editing.SmtpHost)
                    ? Models.AccountConfig.GuessSmtpHost(editing.Host, editing.User)
                    : editing.SmtpHost;
                _tbSmtpPort.Text = (editing.SmtpPort <= 0 ? 465 : editing.SmtpPort).ToString();
                _chkSmtpSsl.IsChecked = editing.SmtpUseSsl;
                if (string.Equals(_tbSmtpHost.Text, "smtp.office365.com", StringComparison.OrdinalIgnoreCase) && _tbSmtpPort.Text == "465")
                    _tbSmtpPort.Text = "587";
                _tbInterval.Text = editing.PollIntervalSeconds.ToString();
                _chkEnabled.IsChecked = editing.Enabled;
                _tbPass.Text = "";
                if (editing.UseOAuth)
                {
                    _useOAuth = true;
                    bool readAuthorized = !string.IsNullOrWhiteSpace(editing.EncryptedImapRefreshToken) ||
                        (string.IsNullOrWhiteSpace(editing.OAuthClientId) && !string.IsNullOrWhiteSpace(editing.EncryptedRefreshToken));
                    bool sendAuthorized = !string.IsNullOrWhiteSpace(editing.EncryptedSmtpRefreshToken) ||
                        (!string.IsNullOrWhiteSpace(editing.OAuthClientId) && !string.IsNullOrWhiteSpace(editing.EncryptedRefreshToken));
                    bool graphAuthorized = !string.IsNullOrWhiteSpace(editing.EncryptedGraphRefreshToken);
                    _oauthStatus.Text = graphAuthorized ? "✓ Microsoft Graph 读取和发送已授权"
                        : (readAuthorized ? "✓ 快速读取已授权" : "○ 快速读取未授权") +
                          "  ·  " + (sendAuthorized ? "✓ 旧版发送授权已保存" : "○ Graph 未授权");
                }
            }

            UpdateOAuthVisibility();
            UpdateOAuthModeUi();
        }

        private TextBlock AddLabel(Grid grid, int row, string text)
        {
            var l = Theme.Label(text);
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            grid.Children.Add(l);
            return l;
        }

        private Grid BuildServerRow(out TextBox host, out TextBox port, out CheckBox secure, string defaultPort)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            host = new TextBox { Margin = new Thickness(0, 0, 10, 0) };
            Theme.StyleTextBox(host);
            row.Children.Add(host);

            var portLabel = Theme.Label("端口");
            portLabel.Margin = new Thickness(0, 0, 7, 0);
            Grid.SetColumn(portLabel, 1); row.Children.Add(portLabel);

            port = new TextBox { Text = defaultPort };
            Theme.StyleTextBox(port);
            Grid.SetColumn(port, 2); row.Children.Add(port);

            secure = new CheckBox
            {
                Content = "安全连接",
                IsChecked = true,
                Foreground = Theme.TextB,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(secure, 3); row.Children.Add(secure);
            return row;
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

        private void UpdateOAuthModeUi()
        {
            if (_tbOAuthClientId == null || _cbOAuthMode == null) return;
            bool custom = _cbOAuthMode.SelectedIndex == 1;
            var visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            _tbOAuthClientId.Visibility = visibility;
            if (_oauthClientIdLabel != null) _oauthClientIdLabel.Visibility = visibility;
            if (_oauthRegistrationButton != null) _oauthRegistrationButton.Visibility = visibility;
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
                _tbSmtpHost.Text = Models.AccountConfig.GuessSmtpHost(p.host, _tbUser.Text);
                _tbSmtpPort.Text = string.Equals(key, "Outlook", StringComparison.OrdinalIgnoreCase) ? "587" : "465";
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
            int.TryParse(_tbSmtpPort.Text, out int smtpPort);
            if (interval < 15) interval = 15;
            string existingImapToken = _editing?.EncryptedImapRefreshToken;
            if (string.IsNullOrWhiteSpace(existingImapToken) && string.IsNullOrWhiteSpace(_editing?.OAuthClientId))
                existingImapToken = _editing?.EncryptedRefreshToken;
            string existingSmtpToken = _editing?.EncryptedSmtpRefreshToken;
            if (string.IsNullOrWhiteSpace(existingSmtpToken) && !string.IsNullOrWhiteSpace(_editing?.OAuthClientId))
                existingSmtpToken = _editing?.EncryptedRefreshToken;
            string encryptedImapToken = string.IsNullOrWhiteSpace(_imapRefreshToken)
                ? existingImapToken : Services.SecureStore.Protect(_imapRefreshToken);
            string encryptedSmtpToken = existingSmtpToken;
            string encryptedGraphToken = string.IsNullOrWhiteSpace(_graphRefreshToken)
                ? (_editing?.EncryptedGraphRefreshToken ?? "") : Services.SecureStore.Protect(_graphRefreshToken);
            return new Models.AccountConfig
            {
                Name = _tbName.Text,
                Protocol = isImap ? Models.MailProtocol.Imap : Models.MailProtocol.Pop3,
                Host = _tbHost.Text.Trim(),
                Port = port == 0 ? (isImap ? 993 : 995) : port,
                UseSsl = _chkSsl.IsChecked ?? true,
                User = _tbUser.Text.Trim(),
                SmtpHost = _tbSmtpHost.Text.Trim(),
                SmtpPort = smtpPort <= 0 ? 465 : smtpPort,
                SmtpUseSsl = _chkSmtpSsl.IsChecked ?? true,
                EncryptedPassword = _useOAuth
                    ? ""
                    : (string.IsNullOrEmpty(_tbPass.Text) ? (_editing?.EncryptedPassword ?? "") : Services.SecureStore.Protect(_tbPass.Text)),
                UseOAuth = _useOAuth,
                EncryptedRefreshToken = encryptedImapToken ?? encryptedSmtpToken ?? "",
                EncryptedImapRefreshToken = encryptedImapToken ?? "",
                EncryptedSmtpRefreshToken = encryptedSmtpToken ?? "",
                EncryptedGraphRefreshToken = encryptedGraphToken ?? "",
                OAuthUserEmail = _useOAuth ? _tbUser.Text.Trim() : (_editing?.OAuthUserEmail ?? ""),
                OAuthClientId = _useOAuth ? (_authorizedOAuthClientId ?? "") : "",
                PollIntervalSeconds = interval,
                Enabled = _chkEnabled.IsChecked ?? true,
            };
        }
    }
}

