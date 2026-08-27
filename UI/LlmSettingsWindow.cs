using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MailPulse.UI
{
    public class LlmSettingsWindow : Window
    {
        private readonly Services.ConfigService _config;
        private ObservableCollection<LlmRow> _rows = new ObservableCollection<LlmRow>();
        private ListBox _list;
        private CheckBox _chkFallback;
        private TextBox _tbPrompt;
        private Button _btnTest;

        public class LlmRow
        {
            public string Name { get; set; }
            public string Protocol { get; set; }
            public string Model { get; set; }
            public bool Enabled { get; set; }
            public string ProtocolText
            {
                get
                {
                    switch (Protocol)
                    {
                        case "OpenAiChat": return "OpenAI Chat";
                        case "OpenAiResponses": return "OpenAI Responses";
                        case "Anthropic": return "Anthropic";
                        default: return Protocol;
                    }
                }
            }
            public override string ToString() => Name + "  [" + ProtocolText + "]  " + Model + (Enabled ? "  ✓" : "  ✗");
        }

        public LlmSettingsWindow(Services.ConfigService config)
        {
            _config = config;
            Title = "LLM 设置";
            Width = 760; Height = 620;
            MinWidth = 640; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.BgB;

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // header
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = "LLM 智能分类", Foreground = Theme.AccentB, FontSize = 22, FontWeight = FontWeights.Bold });
            titleRow.Children.Add(new TextBlock { Text = "  规则未命中时由大模型兜底判断并提取", Foreground = Theme.TextDimB, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 3) });
            header.Children.Add(titleRow);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // enable toggle
            _chkFallback = new CheckBox
            {
                Content = "启用 LLM 兜底（本地规则未命中时调用）",
                Foreground = Theme.TextB,
                IsChecked = _config.Current.LlmFallbackEnabled,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _chkFallback.Click += (s, e) => _config.Current.LlmFallbackEnabled = _chkFallback.IsChecked ?? false;
            Grid.SetRow(_chkFallback, 1);
            root.Children.Add(_chkFallback);

            // config list
            var listCard = Theme.Card(BuildList());
            Grid.SetRow(listCard, 2);
            root.Children.Add(listCard);

            // list buttons
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            btnPanel.Children.Add(Theme.CreateButton("＋ 添加配置", () => EditConfig(null), true));
            btnPanel.Children.Add(Theme.CreateButton("编辑", () =>
            {
                var cfg = Selected();
                if (cfg == null) { MessageBox.Show("请先选中一个配置。"); return; }
                EditConfig(cfg);
            }));
            btnPanel.Children.Add(Theme.CreateButton("删除", DeleteSelected));
            _btnTest = Theme.CreateButton("测试选中", TestSelected);
            btnPanel.Children.Add(_btnTest);
            Grid.SetRow(btnPanel, 3);
            root.Children.Add(btnPanel);

            // prompt editor
            var promptCard = new Border
            {
                Background = Theme.SurfaceB,
                BorderBrush = Theme.BorderB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 16, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            var promptPanel = new Grid();
            promptPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            promptPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var promptHeader = new Grid();
            promptHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            promptHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            promptHeader.Children.Add(new TextBlock { Text = "提示词（支持 {subject} / {body} 占位符）", Foreground = Theme.TextB, FontSize = 13, FontWeight = FontWeights.SemiBold });

            var resetBtn = Theme.CreateButton("恢复默认", () => { _tbPrompt.Text = Models.AppConfig.DefaultLlmPrompt; });
            Grid.SetColumn(resetBtn, 1);
            promptHeader.Children.Add(resetBtn);
            Grid.SetRow(promptHeader, 0);
            promptPanel.Children.Add(promptHeader);

            _tbPrompt = new TextBox
            {
                Text = _config.Current.LlmPrompt,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 260,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Theme.StyleTextBox(_tbPrompt);
            Grid.SetRow(_tbPrompt, 1);
            promptPanel.Children.Add(_tbPrompt);
            promptCard.Child = promptPanel;
            Grid.SetRow(promptCard, 4);
            root.Children.Add(promptCard);

            // footer
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            footer.Children.Add(Theme.CreateButton("保存并关闭", () => { Save(); DialogResult = true; }, true));
            footer.Children.Add(Theme.CreateButton("取消", () => DialogResult = false));
            Grid.SetRow(footer, 5);
            root.Children.Add(footer);

            Content = root;
            ReloadRows();
        }

        private FrameworkElement BuildList()
        {
            _list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = _rows,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MaxHeight = 180
            };
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
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

        private void ReloadRows()
        {
            _rows.Clear();
            _list.SelectedIndex = -1;
            foreach (var c in _config.Current.Llms ?? Enumerable.Empty<Models.LlmConfig>())
                _rows.Add(new LlmRow { Name = c.Name, Protocol = c.Protocol.ToString(), Model = c.Model, Enabled = c.Enabled });
        }

        private Models.LlmConfig Selected()
        {
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= (_config.Current.Llms?.Count ?? 0)) return null;
            return _config.Current.Llms[idx];
        }

        private void EditConfig(Models.LlmConfig existing)
        {
            var dlg = new LlmConfigDialog(existing) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var cfg = dlg.Result();
            if (existing == null)
            {
                if (_config.Current.Llms == null) _config.Current.Llms = new List<Models.LlmConfig>();
                _config.Current.Llms.Add(cfg);
            }
            else
            {
                cfg.Id = existing.Id;
                int i = _config.Current.Llms.IndexOf(existing);
                _config.Current.Llms[i] = cfg;
            }
            Save();
            ReloadRows();
        }

        private void DeleteSelected()
        {
            var cfg = Selected();
            if (cfg == null) { MessageBox.Show("请先选中一个配置。"); return; }
            if (MessageBox.Show("确定删除 LLM 配置 \"" + cfg.Name + "\" ?", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _config.Current.Llms.Remove(cfg);
            Save();
            ReloadRows();
        }

        private async void TestSelected()
        {
            var cfg = Selected();
            if (cfg == null) { MessageBox.Show("请先选中一个配置。"); return; }
            Theme.SetButtonLoading(_btnTest, true, "测试中…");
            try
            {
                var cls = new Services.LlmClassifier();
                var result = await Task.Run(() =>
                    cls.ClassifyAsync("您的验证码", "你的验证码是 ASFE466，5分钟内有效。", "test@example.com",
                        "测试", cfg, _config.Current.LlmPrompt, System.Threading.CancellationToken.None));
                if (result.Matched)
                    MessageBox.Show("✓ LLM 识别为验证码邮件\n验证码: " + (result.Code ?? "无") + "\n链接: " + (result.Url ?? "无"), "测试成功");
                else
                    MessageBox.Show("LLM 调用成功，但判定为非紧急邮件（或未提取到内容）。\n若确认 API 正常，请检查提示词。", "测试结果");
            }
            catch (Exception ex)
            {
                MessageBox.Show("测试失败: " + ex.Message, "错误");
            }
            finally { Theme.SetButtonLoading(_btnTest, false); }
        }

        private void Save()
        {
            _config.Current.LlmPrompt = _tbPrompt.Text;
            _config.Current.LlmFallbackEnabled = _chkFallback.IsChecked ?? false;
            _config.Save();
        }
    }

    // ──────────────── LLM Config Dialog ────────────────

    public class LlmConfigDialog : Window
    {
        private TextBox _tbName, _tbBase, _tbKey, _tbModel, _tbTimeout;
        private ComboBox _cbProtocol;
        private CheckBox _chkEnabled;
        private Models.LlmConfig _editing;

        public Models.LlmConfig Result() => Build();

        public LlmConfigDialog(Models.LlmConfig editing)
        {
            _editing = editing;
            Title = editing == null ? "添加 LLM 配置" : "编辑 LLM 配置: " + (editing.Name ?? "");
            Width = 520; Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Theme.BgB;

            var g = new Grid { Margin = new Thickness(20) };
            for (int i = 0; i < 8; i++)
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int r = 0;
            AddLabel(g, r, "名称");
            _tbName = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbName);
            Grid.SetRow(_tbName, r); Grid.SetColumn(_tbName, 1); g.Children.Add(_tbName); r++;

            AddLabel(g, r, "协议类型");
            _cbProtocol = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleComboBox(_cbProtocol, 220);
            _cbProtocol.Items.Add("OpenAI Chat (兼容大多数)");
            _cbProtocol.Items.Add("OpenAI Responses");
            _cbProtocol.Items.Add("Anthropic Messages");
            _cbProtocol.SelectedIndex = 0;
            _cbProtocol.SelectionChanged += (s, e) => { if (_editing == null) { _tbBase.Text = Models.LlmConfig.DefaultBaseUrl(FromCombo()); _tbModel.Text = Models.LlmConfig.DefaultModel(FromCombo()); } };
            Grid.SetRow(_cbProtocol, r); Grid.SetColumn(_cbProtocol, 1); g.Children.Add(_cbProtocol); r++;

            AddLabel(g, r, "Base URL");
            _tbBase = new TextBox { Text = "https://api.openai.com/v1", Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbBase);
            Grid.SetRow(_tbBase, r); Grid.SetColumn(_tbBase, 1); g.Children.Add(_tbBase); r++;

            AddLabel(g, r, "API Key");
            _tbKey = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbKey);
            Grid.SetRow(_tbKey, r); Grid.SetColumn(_tbKey, 1); g.Children.Add(_tbKey); r++;

            AddLabel(g, r, "模型");
            _tbModel = new TextBox { Text = "gpt-4o-mini", Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbModel);
            Grid.SetRow(_tbModel, r); Grid.SetColumn(_tbModel, 1); g.Children.Add(_tbModel); r++;

            AddLabel(g, r, "超时(秒)");
            _tbTimeout = new TextBox { Text = "8", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(_tbTimeout, 80);
            Grid.SetRow(_tbTimeout, r); Grid.SetColumn(_tbTimeout, 1); g.Children.Add(_tbTimeout); r++;

            _chkEnabled = new CheckBox { Content = "启用此配置", IsChecked = true, Foreground = Theme.TextB, Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetRow(_chkEnabled, r); Grid.SetColumn(_chkEnabled, 0); Grid.SetColumnSpan(_chkEnabled, 2);
            g.Children.Add(_chkEnabled); r++;

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var ok = Theme.CreateButton("确定", () =>
            {
                if (string.IsNullOrWhiteSpace(_tbName.Text) || string.IsNullOrWhiteSpace(_tbBase.Text) ||
                    string.IsNullOrWhiteSpace(_tbKey.Text) || string.IsNullOrWhiteSpace(_tbModel.Text))
                { MessageBox.Show("名称、Base URL、API Key、模型不能为空。"); return; }
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
                _cbProtocol.SelectedIndex = (int)editing.Protocol;
                _tbBase.Text = editing.BaseUrl ?? "";
                _tbModel.Text = editing.Model ?? "";
                _tbTimeout.Text = editing.TimeoutSeconds.ToString();
                _chkEnabled.IsChecked = editing.Enabled;
                _tbKey.Text = "";
            }
        }

        private Models.LlmProtocol FromCombo()
        {
            int i = _cbProtocol.SelectedIndex;
            if (i == 1) return Models.LlmProtocol.OpenAiResponses;
            if (i == 2) return Models.LlmProtocol.Anthropic;
            return Models.LlmProtocol.OpenAiChat;
        }

        private void AddLabel(Grid grid, int row, string text)
        {
            var l = Theme.Label(text);
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            grid.Children.Add(l);
        }

        private Models.LlmConfig Build()
        {
            int.TryParse(_tbTimeout.Text, out int timeout);
            if (timeout < 3) timeout = 3;
            return new Models.LlmConfig
            {
                Name = _tbName.Text,
                Protocol = FromCombo(),
                BaseUrl = _tbBase.Text.Trim(),
                Model = _tbModel.Text.Trim(),
                EncryptedApiKey = string.IsNullOrEmpty(_tbKey.Text)
                    ? (_editing?.EncryptedApiKey ?? "")
                    : Services.SecureStore.Protect(_tbKey.Text),
                TimeoutSeconds = timeout,
                Enabled = _chkEnabled.IsChecked ?? true,
            };
        }
    }
}


