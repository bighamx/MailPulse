using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;

namespace MailPulse.UI
{
    public class SettingsWindow : Window
    {
        private readonly Services.ConfigService _config;
        private ObservableCollection<AccountRow> _rows = new ObservableCollection<AccountRow>();
        private ListBox _list;
        private Button _btnTest;
        private CheckBox _chkAutoStart, _chkAutoCopy;
        private ComboBox _cbTheme;
        private bool _building;

        public class AccountRow
        {
            public string Name { get; set; }
            public string Protocol { get; set; }
            public string User { get; set; }
            public bool Enabled { get; set; }
            public string Badge => string.Equals(Protocol, "IMAP", StringComparison.OrdinalIgnoreCase) ? "IMAP" : "POP3";
            public string StateText => Enabled ? "已启用" : "已停用";
            public Brush StateBrush => Enabled ? Theme.AccentB : Theme.TextDimB;
        }

        public SettingsWindow(Services.ConfigService config)
        {
            _config = config;
            Title = "MailPulse 设置";
            Width = 900; Height = 580;
            MinWidth = 780; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.BgB;
            BuildUi();
        }

        private void BuildUi()
        {
            _building = true;
            try
            {
                Background = Theme.BgB;
                var root = new Grid { Margin = new Thickness(20) };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // header
                var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                titleRow.Children.Add(new TextBlock { Text = "MailPulse", Foreground = Theme.AccentB, FontSize = 24, FontWeight = FontWeights.Bold });
                titleRow.Children.Add(new TextBlock { Text = "  邮件验证码监控", Foreground = Theme.TextDimB, FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 3) });
                header.Children.Add(titleRow);
                header.Children.Add(new TextBlock { Text = "管理邮箱账号与规则，验证码邮件即时弹出通知。", Foreground = Theme.TextDimB, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetRow(header, 0);
                root.Children.Add(header);

                // account list card
                var listCard = Theme.Card(BuildAccountList());
                Grid.SetRow(listCard, 1);
                root.Children.Add(listCard);

                // account buttons
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
                btnPanel.Children.Add(Theme.CreateButton("＋ 添加账号", () => AddAccount(), true));
                btnPanel.Children.Add(Theme.CreateButton("编辑", () => { if (SelectedAccount() == null) { MessageBox.Show("请先选中一个账号。"); return; } EditAccount(); }));
                btnPanel.Children.Add(Theme.CreateButton("删除", DeleteSelected));
                _btnTest = Theme.CreateButton("测试连接", TestSelected);
                btnPanel.Children.Add(_btnTest);
                Grid.SetRow(btnPanel, 2);
                root.Children.Add(btnPanel);

                // options + footer
                _chkAutoStart = new CheckBox { Content = "开机自启", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextB, Margin = new Thickness(0, 0, 24, 0) };
                _chkAutoStart.IsChecked = Services.AutoStart.IsEnabled();
                _chkAutoStart.Checked += (s, e) => TrySetAutoStart(true);
                _chkAutoStart.Unchecked += (s, e) => TrySetAutoStart(false);

                _chkAutoCopy = new CheckBox { Content = "验证码自动复制到剪贴板", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextB };
                _chkAutoCopy.IsChecked = _config.Current.AutoCopyCode;
                _chkAutoCopy.Click += (s, e) => { _config.Current.AutoCopyCode = _chkAutoCopy.IsChecked ?? true; _config.Save(); };

                // theme selector
                var themeLabel = new TextBlock { Text = "外观", Foreground = Theme.TextDimB, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
                _cbTheme = new ComboBox { MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };
                Theme.StyleComboBox(_cbTheme, 100);
                _cbTheme.Items.Add("浅色");
                _cbTheme.Items.Add("深色");
                _cbTheme.SelectedIndex = Theme.ParseMode(_config.Current.ThemeMode) == ThemeMode.Dark ? 1 : 0;
                _cbTheme.SelectionChanged += (s, e) =>
                {
                    if (_building) return;
                    _config.Current.ThemeMode = _cbTheme.SelectedIndex == 1 ? "Dark" : "Light";
                    _config.Save();
                    Theme.Apply(Theme.ParseMode(_config.Current.ThemeMode));
                    BuildUi();
                };

                var bottomRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                bottomRight.Children.Add(Theme.CreateButton("规则编辑器", ShowRulesEditor));
                bottomRight.Children.Add(Theme.CreateButton("LLM 设置", ShowLlmSettings));
                bottomRight.Children.Add(Theme.CreateButton("关闭", Close));

                var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var leftBox = new StackPanel { Orientation = Orientation.Horizontal };
                leftBox.Children.Add(_chkAutoStart);
                leftBox.Children.Add(_chkAutoCopy);
                leftBox.Children.Add(themeLabel);
                leftBox.Children.Add(_cbTheme);
                Grid.SetColumn(leftBox, 0);
                footer.Children.Add(leftBox);
                Grid.SetColumn(bottomRight, 1);
                footer.Children.Add(bottomRight);
                Grid.SetRow(footer, 3);
                root.Children.Add(footer);

                Content = root;
                ReloadRows();
            }
            finally { _building = false; }
        }

        private FrameworkElement BuildAccountList()
        {
            _list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = _rows,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var template = new DataTemplate(typeof(AccountRow));
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.MarginProperty, new Thickness(6));
            gridFactory.SetValue(Grid.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            gridFactory.SetValue(Grid.MinHeightProperty, 44.0);

            var colName = new FrameworkElementFactory(typeof(ColumnDefinition));
            colName.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var colBadge = new FrameworkElementFactory(typeof(ColumnDefinition));
            colBadge.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var colUser = new FrameworkElementFactory(typeof(ColumnDefinition));
            colUser.SetValue(ColumnDefinition.WidthProperty, new GridLength(1.4, GridUnitType.Star));
            var colState = new FrameworkElementFactory(typeof(ColumnDefinition));
            colState.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            gridFactory.AppendChild(colName);
            gridFactory.AppendChild(colBadge);
            gridFactory.AppendChild(colUser);
            gridFactory.AppendChild(colState);

            var nameTxt = new FrameworkElementFactory(typeof(TextBlock));
            nameTxt.SetValue(TextBlock.TextProperty, new Binding("Name"));
            nameTxt.SetValue(TextBlock.ForegroundProperty, Theme.TextB);
            nameTxt.SetValue(TextBlock.FontSizeProperty, 14.0);
            nameTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            nameTxt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            nameTxt.SetValue(Grid.ColumnProperty, 0);
            gridFactory.AppendChild(nameTxt);

            var badge = new FrameworkElementFactory(typeof(Border));
            badge.SetValue(Border.BackgroundProperty, Theme.BadgeB);
            badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            badge.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
            badge.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            badge.SetValue(Border.MarginProperty, new Thickness(8, 0, 0, 0));
            badge.SetValue(Grid.ColumnProperty, 1);
            var badgeTxt = new FrameworkElementFactory(typeof(TextBlock));
            badgeTxt.SetValue(TextBlock.TextProperty, new Binding("Badge"));
            badgeTxt.SetValue(TextBlock.ForegroundProperty, Theme.AccentHiB);
            badgeTxt.SetValue(TextBlock.FontSizeProperty, 11.0);
            badgeTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            badge.AppendChild(badgeTxt);
            gridFactory.AppendChild(badge);

            var userTxt = new FrameworkElementFactory(typeof(TextBlock));
            userTxt.SetValue(TextBlock.TextProperty, new Binding("User"));
            userTxt.SetValue(TextBlock.ForegroundProperty, Theme.TextDimB);
            userTxt.SetValue(TextBlock.FontSizeProperty, 12.5);
            userTxt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            userTxt.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 0, 0));
            userTxt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            userTxt.SetValue(Grid.ColumnProperty, 2);
            gridFactory.AppendChild(userTxt);

            var stateTxt = new FrameworkElementFactory(typeof(TextBlock));
            stateTxt.SetValue(TextBlock.TextProperty, new Binding("StateText"));
            stateTxt.SetValue(TextBlock.ForegroundProperty, new Binding("StateBrush"));
            stateTxt.SetValue(TextBlock.FontSizeProperty, 11.5);
            stateTxt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            stateTxt.SetValue(Grid.ColumnProperty, 3);
            gridFactory.AppendChild(stateTxt);

            template.VisualTree = gridFactory;
            _list.ItemTemplate = template;

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.TextB));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
            var trigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            trigger.Setters.Add(new Setter(Control.BackgroundProperty, Theme.SelectionB));
            style.Triggers.Add(trigger);
            _list.ItemContainerStyle = style;
            return _list;
        }

        private void TrySetAutoStart(bool on)
        {
            try { Services.AutoStart.Set(on); }
            catch (Exception ex)
            {
                MessageBox.Show("设置失败: " + ex.Message);
                _chkAutoStart.IsChecked = !on;
            }
        }

        private void ReloadRows()
        {
            _rows.Clear();
            if (_list != null) _list.SelectedIndex = -1;
            foreach (var a in _config.Current.Accounts)
                _rows.Add(new AccountRow { Name = a.Name, Protocol = a.Protocol.ToString(), User = a.User, Enabled = a.Enabled });
        }

        private Models.AccountConfig SelectedAccount()
        {
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= _config.Current.Accounts.Count) return null;
            return _config.Current.Accounts[idx];
        }

        private void AddAccount()
        {
            var dlg = new AccountDialog(null) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var acc = dlg.ResultAccount();
            _config.Current.Accounts.Add(acc);
            _config.Save();
            ReloadRows();
            ((App)Application.Current).RestartMonitoring();
        }

        private void EditAccount()
        {
            var existing = SelectedAccount();
            var dlg = new AccountDialog(existing) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var acc = dlg.ResultAccount();
            acc.Id = existing.Id;
            int i = _config.Current.Accounts.IndexOf(existing);
            _config.Current.Accounts[i] = acc;
            _config.Save();
            ReloadRows();
            ((App)Application.Current).RestartMonitoring();
        }

        private void DeleteSelected()
        {
            var acc = SelectedAccount();
            if (acc == null) { MessageBox.Show("请先选中一个账号。"); return; }
            if (MessageBox.Show("确定删除账号 \"" + acc.Name + "\" ?", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _config.Current.Accounts.Remove(acc);
            _config.Save();
            ReloadRows();
            ((App)Application.Current).RestartMonitoring();
        }

        private async void TestSelected()
        {
            var acc = SelectedAccount();
            if (acc == null) { MessageBox.Show("请先选中一个账号。"); return; }
            _btnTest.IsEnabled = false;
            try
            {
                string result = await Task.Run(() => TestConnect(acc));
                MessageBox.Show(result, "连接测试");
            }
            catch (Exception ex) { MessageBox.Show("测试失败: " + ex.Message, "错误"); }
            finally { _btnTest.IsEnabled = true; }
        }

        private static string TestConnect(Models.AccountConfig acc)
        {
            if (acc.Protocol == Models.MailProtocol.Imap)
            {
                using (var c = new ImapClient())
                {
                    c.Connect(acc.Host, acc.Port, true);
                    c.Authenticate(acc.User, Services.SecureStore.Unprotect(acc.EncryptedPassword));
                    c.Disconnect(true);
                    return "✓ IMAP 连接成功";
                }
            }
            else
            {
                using (var c = new Pop3Client())
                {
                    c.Connect(acc.Host, acc.Port, true);
                    c.Authenticate(acc.User, Services.SecureStore.Unprotect(acc.EncryptedPassword));
                    c.Disconnect(true);
                    return "✓ POP3 连接成功";
                }
            }
        }

        private void ShowLlmSettings()
        {
            var dlg = new LlmSettingsWindow(_config) { Owner = this };
            dlg.ShowDialog();
            ((App)Application.Current).RestartMonitoring();
        }

        private void ShowRulesEditor()
        {
            var dlg = new RulesEditorWindow(_config) { Owner = this };
            dlg.ShowDialog();
            ((App)Application.Current).RestartMonitoring();
        }
    }

    // Rules Editor

    public class RulesEditorWindow : Window
    {
        private readonly Services.ConfigService _config;
        private ObservableCollection<RuleRow> _rows = new ObservableCollection<RuleRow>();
        private DataGrid _grid;

        public class RuleRow
        {
            public string Name { get; set; }
            public string SubjectKeywords { get; set; }
            public string BodyPatterns { get; set; }
            public string SenderWhitelist { get; set; }
            public bool NotifyWithCode { get; set; }
            public bool NotifyWithLink { get; set; }
        }

        public RulesEditorWindow(Services.ConfigService config)
        {
            _config = config;
            Title = "规则编辑器";
            Width = 840; Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.BgB;

            var root = new DockPanel();

            var tip = new TextBlock
            {
                Text = "【主题关键词】用逗号分隔，【正文正则】用分号分隔；两者任一命中即触发。发件人白名单逗号分隔，可留空表示不限。",
                Foreground = Theme.TextDimB,
                Padding = new Thickness(14, 12, 14, 8),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            };
            DockPanel.SetDock(tip, Dock.Top);
            root.Children.Add(tip);

            _grid = new DataGrid
            {
                ItemsSource = _rows,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Margin = new Thickness(14),
                Background = Theme.SurfaceB,
                Foreground = Theme.TextB,
                BorderBrush = Theme.BorderB,
                RowBackground = Theme.SurfaceB,
                AlternatingRowBackground = Theme.AltRowB,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Theme.BorderB,
                SelectionMode = DataGridSelectionMode.Single
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "规则名", Binding = new Binding("Name"), Width = new DataGridLength(120) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "主题关键词", Binding = new Binding("SubjectKeywords"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "正文正则", Binding = new Binding("BodyPatterns"), Width = new DataGridLength(1.6, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "发件人白名单", Binding = new Binding("SenderWhitelist"), Width = new DataGridLength(130) });
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "提取码", Binding = new Binding("NotifyWithCode"), Width = new DataGridLength(56) });
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "提取链接", Binding = new Binding("NotifyWithLink"), Width = new DataGridLength(60) });

            root.Children.Add(_grid);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 16) };
            btnPanel.Children.Add(Theme.CreateButton("＋ 添加规则", () => { _rows.Add(new RuleRow()); _grid.CommitEdit(DataGridEditingUnit.Row, true); }, true));
            btnPanel.Children.Add(Theme.CreateButton("－ 删除选中", () => { if (_grid.SelectedItem is RuleRow rr) _rows.Remove(rr); }));
            btnPanel.Children.Add(Theme.CreateButton("保存并关闭", () => { SaveBack(); DialogResult = true; }));

            DockPanel.SetDock(btnPanel, Dock.Bottom);
            root.Children.Add(btnPanel);

            Content = root;
            LoadFromConfig();
        }

        private void LoadFromConfig()
        {
            _rows.Clear();
            foreach (var r in _config.Current.Rules ?? Enumerable.Empty<Models.RuleConfig>())
                _rows.Add(new RuleRow
                {
                    Name = r.Name,
                    SubjectKeywords = string.Join(", ", r.SubjectKeywords ?? new List<string>()),
                    BodyPatterns = string.Join("; ", r.BodyPatterns ?? new List<string>()),
                    SenderWhitelist = string.Join(", ", r.SenderWhitelist ?? new List<string>()),
                    NotifyWithCode = r.NotifyWithCode,
                    NotifyWithLink = r.NotifyWithLink,
                });
        }

        private void SaveBack()
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var rules = new List<Models.RuleConfig>();
            foreach (var r in _rows)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                rules.Add(new Models.RuleConfig
                {
                    Name = r.Name,
                    SubjectKeywords = SplitCsv(r.SubjectKeywords).ToList(),
                    BodyPatterns = r.BodyPatterns?.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList() ?? new List<string>(),
                    SenderWhitelist = SplitCsv(r.SenderWhitelist).ToList(),
                    NotifyWithCode = r.NotifyWithCode,
                    NotifyWithLink = r.NotifyWithLink,
                });
            }
            _config.Current.Rules = rules;
            _config.Save();
        }

        private IEnumerable<string> SplitCsv(string s)
            => (s ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);
    }
}


