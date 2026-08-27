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
                btnPanel.Children.Add(Theme.CreateButton("邮件中心", () => ((App)Application.Current).ShowMailCenter(), true));
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
                var themeLabel = new TextBlock { Text = "外观", Foreground = Theme.TextDimB, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(20, 0, 6, 0) };
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
            Theme.SetButtonLoading(_btnTest, true, "测试中…");
            var app = (App)Application.Current;
            bool resumeMonitoring = app.TemporarilyStopMonitoring();
            try
            {
                using (var service = new Services.MailCenterService(_config))
                    await service.LoadInboxAsync(acc, 1, System.Threading.CancellationToken.None);
                MessageBox.Show("✓ " + acc.Protocol.ToString().ToUpperInvariant() + " 连接与认证成功", "连接测试");
            }
            catch (Exception ex) { MessageBox.Show("测试失败: " + ex.Message, "错误"); }
            finally
            {
                Theme.SetButtonLoading(_btnTest, false);
                app.ResumeMonitoring(resumeMonitoring);
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
        private ListBox _list;

        public class RuleRow
        {
            public Models.RuleConfig Rule { get; set; }
            public string Name => Rule.Name;
            public string MatchSummary
            {
                get
                {
                    int keywords = Rule.SubjectKeywords == null ? 0 : Rule.SubjectKeywords.Count;
                    int patterns = Rule.BodyPatterns == null ? 0 : Rule.BodyPatterns.Count;
                    return keywords + " 个主题关键词  ·  " + patterns + " 个正文正则";
                }
            }
            public string OutputSummary => (Rule.NotifyWithCode ? "提取验证码" : "") +
                (Rule.NotifyWithCode && Rule.NotifyWithLink ? " · " : "") +
                (Rule.NotifyWithLink ? "提取链接" : "");
        }

        public RulesEditorWindow(Services.ConfigService config)
        {
            _config = config;
            Title = "规则编辑器";
            Width = 780; Height = 560;
            MinWidth = 680; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.BgB;

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = "匹配规则", Foreground = Theme.AccentB, FontSize = 22, FontWeight = FontWeights.Bold });
            titleRow.Children.Add(new TextBlock { Text = "  识别验证码与确认链接邮件", Foreground = Theme.TextDimB, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 3) });
            header.Children.Add(titleRow);
            header.Children.Add(new TextBlock { Text = "主题关键词或正文正则任一命中即触发；发件人白名单留空表示不限制。", Foreground = Theme.TextDimB, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            _list = new ListBox
            {
                ItemsSource = _rows,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            _list.ItemTemplate = BuildRuleTemplate();
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 9, 12, 9)));
            itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 2, 0, 2)));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Theme.SelectionB));
            itemStyle.Triggers.Add(selected);
            _list.ItemContainerStyle = itemStyle;
            _list.MouseDoubleClick += (s, e) => EditSelected();
            var card = Theme.Card(_list);
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            btnPanel.Children.Add(Theme.CreateButton("＋ 添加规则", AddRule, true));
            btnPanel.Children.Add(Theme.CreateButton("编辑", EditSelected));
            btnPanel.Children.Add(Theme.CreateButton("删除", DeleteSelected));
            btnPanel.Children.Add(Theme.CreateButton("测试选中", TestSelected));
            Grid.SetRow(btnPanel, 2);
            root.Children.Add(btnPanel);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            footer.Children.Add(Theme.CreateButton("保存并关闭", () => { SaveBack(); DialogResult = true; }, true));
            footer.Children.Add(Theme.CreateButton("取消", () => DialogResult = false));
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
            LoadFromConfig();
        }

        private DataTemplate BuildRuleTemplate()
        {
            var template = new DataTemplate(typeof(RuleRow));
            var grid = new FrameworkElementFactory(typeof(Grid));
            var left = new FrameworkElementFactory(typeof(StackPanel));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(TextBlock.ForegroundProperty, Theme.TextB);
            name.SetValue(TextBlock.FontSizeProperty, 14.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            left.AppendChild(name);
            var summary = new FrameworkElementFactory(typeof(TextBlock));
            summary.SetBinding(TextBlock.TextProperty, new Binding("MatchSummary"));
            summary.SetValue(TextBlock.ForegroundProperty, Theme.TextDimB);
            summary.SetValue(TextBlock.FontSizeProperty, 12.0);
            summary.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
            left.AppendChild(summary);
            grid.AppendChild(left);
            var output = new FrameworkElementFactory(typeof(TextBlock));
            output.SetBinding(TextBlock.TextProperty, new Binding("OutputSummary"));
            output.SetValue(TextBlock.ForegroundProperty, Theme.AccentB);
            output.SetValue(TextBlock.FontSizeProperty, 12.0);
            output.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            output.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            grid.AppendChild(output);
            template.VisualTree = grid;
            return template;
        }

        private void LoadFromConfig()
        {
            _rows.Clear();
            foreach (var r in _config.Current.Rules ?? Enumerable.Empty<Models.RuleConfig>())
                _rows.Add(new RuleRow { Rule = CloneRule(r) });
        }

        private RuleRow SelectedRow() => _list.SelectedItem as RuleRow;

        private void AddRule()
        {
            var dlg = new RuleEditDialog(null) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _rows.Add(new RuleRow { Rule = dlg.Result() });
            _list.SelectedIndex = _rows.Count - 1;
        }

        private void EditSelected()
        {
            var row = SelectedRow();
            if (row == null) { MessageBox.Show("请先选中一个规则。"); return; }
            int index = _rows.IndexOf(row);
            var dlg = new RuleEditDialog(row.Rule) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _rows[index] = new RuleRow { Rule = dlg.Result() };
            _list.SelectedIndex = index;
        }

        private void DeleteSelected()
        {
            var row = SelectedRow();
            if (row == null) { MessageBox.Show("请先选中一个规则。"); return; }
            if (MessageBox.Show("确定删除规则 \"" + row.Name + "\"？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                _rows.Remove(row);
        }

        private void TestSelected()
        {
            var row = SelectedRow();
            if (row == null) { MessageBox.Show("请先选中一个规则。"); return; }
            new RuleTestDialog(row.Rule) { Owner = this }.ShowDialog();
        }

        private void SaveBack()
        {
            _config.Current.Rules = _rows.Select(r => CloneRule(r.Rule)).ToList();
            _config.Save();
        }

        private static Models.RuleConfig CloneRule(Models.RuleConfig r)
        {
            return new Models.RuleConfig
            {
                Name = r.Name,
                SubjectKeywords = new List<string>(r.SubjectKeywords ?? new List<string>()),
                BodyPatterns = new List<string>(r.BodyPatterns ?? new List<string>()),
                SenderWhitelist = new List<string>(r.SenderWhitelist ?? new List<string>()),
                NotifyWithCode = r.NotifyWithCode,
                NotifyWithLink = r.NotifyWithLink
            };
        }
    }

    public class RuleEditDialog : Window
    {
        private TextBox _tbName, _tbKeywords, _tbPatterns, _tbSenders;
        private CheckBox _chkCode, _chkLink;
        private readonly Models.RuleConfig _editing;

        public RuleEditDialog(Models.RuleConfig editing)
        {
            _editing = editing;
            Title = editing == null ? "添加规则" : "编辑规则: " + (editing.Name ?? "");
            Width = 620; Height = 660;
            MinWidth = 560; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.BgB;

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            header.Children.Add(new TextBlock { Text = editing == null ? "添加匹配规则" : "编辑匹配规则", Foreground = Theme.AccentB, FontSize = 21, FontWeight = FontWeights.Bold });
            header.Children.Add(new TextBlock { Text = "关键词与正则任一命中即可触发，可在保存前测试。", Foreground = Theme.TextDimB, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            root.Children.Add(header);

            var form = new StackPanel();
            _tbName = AddField(form, "规则名称", false, 34);
            _tbKeywords = AddField(form, "主题关键词", false, 34);
            AddHint(form, "示例：验证码, verification code, OTP");
            _tbPatterns = AddField(form, "正文正则", true, 112);
            AddHint(form, "每行一个正则，捕获组 1 作为验证码。示例：(?:验证码|code)[^0-9]{0,10}(\\d{4,8})");
            _tbSenders = AddField(form, "发件人白名单（可选）", false, 34);
            AddHint(form, "示例：noreply@example.com, @github.com, mail.google.com；留空表示不限制发件人。");

            var output = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _chkCode = new CheckBox { Content = "提取验证码", Foreground = Theme.TextB, Margin = new Thickness(0, 0, 24, 0) };
            _chkLink = new CheckBox { Content = "提取确认链接", Foreground = Theme.TextB };
            output.Children.Add(_chkCode);
            output.Children.Add(_chkLink);
            form.Children.Add(output);
            var scroll = new ScrollViewer
            {
                Content = form,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var card = Theme.Card(scroll);
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var test = Theme.CreateButton("测试此规则", TestRule);
            footer.Children.Add(test);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(Theme.CreateButton("保存", SaveAndClose, true));
            right.Children.Add(Theme.CreateButton("取消", () => DialogResult = false));
            Grid.SetColumn(right, 1);
            footer.Children.Add(right);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;

            if (editing != null)
            {
                _tbName.Text = editing.Name ?? "";
                _tbKeywords.Text = string.Join(", ", editing.SubjectKeywords ?? new List<string>());
                _tbPatterns.Text = string.Join(Environment.NewLine, editing.BodyPatterns ?? new List<string>());
                _tbSenders.Text = string.Join(", ", editing.SenderWhitelist ?? new List<string>());
                _chkCode.IsChecked = editing.NotifyWithCode;
                _chkLink.IsChecked = editing.NotifyWithLink;
            }
            else
            {
                _chkCode.IsChecked = true;
            }
        }

        private TextBox AddField(Panel panel, string label, bool multiline, double height)
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = Theme.TextB, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            var tb = new TextBox
            {
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                Height = height,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Theme.StyleTextBox(tb);
            panel.Children.Add(tb);
            return tb;
        }

        private void AddHint(Panel panel, string text)
        {
            panel.Children.Add(new TextBlock { Text = text, Foreground = Theme.TextDimB, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -1, 0, 12) });
        }

        private void SaveAndClose()
        {
            string error = ValidateRule();
            if (error != null) { MessageBox.Show(error, "无法保存"); return; }
            DialogResult = true;
        }

        private void TestRule()
        {
            string error = ValidateRule();
            if (error != null) { MessageBox.Show(error, "无法测试"); return; }
            new RuleTestDialog(Result()) { Owner = this }.ShowDialog();
        }

        private string ValidateRule()
        {
            if (string.IsNullOrWhiteSpace(_tbName.Text)) return "请输入规则名称。";
            var rule = Result();
            if (rule.SubjectKeywords.Count == 0 && rule.BodyPatterns.Count == 0) return "请至少填写一个主题关键词或正文正则。";
            foreach (var pattern in rule.BodyPatterns)
            {
                try { System.Text.RegularExpressions.Regex.Match("", pattern); }
                catch (ArgumentException ex) { return "正文正则无效：\n" + pattern + "\n\n" + ex.Message; }
            }
            if (!rule.NotifyWithCode && !rule.NotifyWithLink) return "请至少选择一种提醒内容。";
            return null;
        }

        public Models.RuleConfig Result()
        {
            return new Models.RuleConfig
            {
                Name = (_tbName.Text ?? "").Trim(),
                SubjectKeywords = SplitCsv(_tbKeywords.Text),
                BodyPatterns = SplitPatterns(_tbPatterns.Text),
                SenderWhitelist = SplitCsv(_tbSenders.Text),
                NotifyWithCode = _chkCode.IsChecked ?? false,
                NotifyWithLink = _chkLink.IsChecked ?? false
            };
        }

        private static List<string> SplitCsv(string value)
        {
            return (value ?? "").Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        private static List<string> SplitPatterns(string value)
        {
            return (value ?? "").Split(new[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }
    }

    public class RuleTestDialog : Window
    {
        private readonly Models.RuleConfig _rule;
        private TextBox _tbSubject, _tbBody, _tbFrom;
        private TextBlock _result;

        public RuleTestDialog(Models.RuleConfig rule)
        {
            _rule = rule;
            Title = "测试规则: " + (rule.Name ?? "");
            Width = 620; Height = 560;
            MinWidth = 540; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.BgB;

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock { Text = "规则测试", Foreground = Theme.AccentB, FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) });

            var form = new StackPanel();
            _tbSubject = Field(form, "邮件主题", false, 34);
            _tbSubject.Text = "您的验证码";
            _tbFrom = Field(form, "发件人", false, 34);
            _tbFrom.Text = "test@example.com";
            _tbBody = Field(form, "邮件正文", true, 150);
            _tbBody.Text = "你的验证码是 ASFE466，5分钟内有效。";
            _result = new TextBlock { Text = "填写邮件样本后点击“运行测试”。", Foreground = Theme.TextDimB, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
            form.Children.Add(_result);
            var card = Theme.Card(form);
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            footer.Children.Add(Theme.CreateButton("运行测试", RunTest, true));
            footer.Children.Add(Theme.CreateButton("关闭", Close));
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;
        }

        private TextBox Field(Panel panel, string label, bool multiline, double height)
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = Theme.TextB, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            var tb = new TextBox { AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, Height = height, Margin = new Thickness(0, 0, 0, 12), VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
            Theme.StyleTextBox(tb);
            panel.Children.Add(tb);
            return tb;
        }

        private void RunTest()
        {
            var engine = new Services.ClassificationEngine();
            var result = engine.Evaluate(_tbSubject.Text, _tbBody.Text, _tbFrom.Text, "规则测试", new List<Models.RuleConfig> { _rule });
            if (result.Matched)
            {
                _result.Foreground = Theme.AccentB;
                _result.Text = "✓ 规则已命中\n验证码：" + (result.Code ?? "未提取") + "\n链接：" + (result.Url ?? "未提取");
            }
            else
            {
                _result.Foreground = Theme.DangerB;
                _result.Text = "未命中。请检查主题关键词、正文正则和发件人白名单是否与样本一致。";
            }
        }
    }
}


