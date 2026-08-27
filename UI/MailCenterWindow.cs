using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MailPulse.UI
{
    public class MailCenterWindow : Window
    {
        private readonly Services.ConfigService _config;
        private readonly Services.MailCenterService _service;
        private readonly Services.ClassificationEngine _classification = new Services.ClassificationEngine();
        private readonly Services.LlmClassifier _llm = new Services.LlmClassifier();
        private readonly Services.MailTranslationService _translator = new Services.MailTranslationService();
        private readonly ObservableCollection<Services.MailListItem> _rows = new ObservableCollection<Services.MailListItem>();
        private ComboBox _accounts;
        private ListBox _list;
        private TextBlock _status, _subject, _meta, _empty, _translationInfo;
        private TextBox _body;
        private WebBrowser _htmlBody;
        private Button _refresh, _reply, _markRead, _delete, _testExtract, _translate, _translationAlternate;
        private MenuItem _listReadState, _listReply, _listDelete;
        private CancellationTokenSource _listCts, _messageCts;
        private Services.MailMessageContent _currentMessage;
        private CancellationTokenSource _translationCts;
        private Services.MailTranslation _translation;
        private Services.MailTranslationSession _translationSession;
        private Services.HtmlMailLayout _htmlLayout;
        private bool _htmlNavigated;
        private bool _htmlDomReady;
        private readonly HashSet<int> _htmlAppliedUnits = new HashSet<int>();
        private readonly HashSet<int> _htmlAppliedAttributes = new HashSet<int>();
        private bool _showingTranslation;

        public MailCenterWindow(Services.ConfigService config)
        {
            _config = config;
            _service = new Services.MailCenterService(config);
            Title = "邮件中心 - MailPulse";
            Width = 1180; Height = 760;
            MinWidth = 900; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.BgB;
            BuildUi();
            Loaded += async (s, e) => await RefreshAsync();
            Closed += (s, e) =>
            {
                _listCts?.Cancel(); _messageCts?.Cancel();
                _translationCts?.Cancel(); _translationCts = null;
                _service.Dispose();
            };
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel();
            title.Children.Add(new TextBlock { Text = "邮件中心", Foreground = Theme.AccentB, FontSize = 24, FontWeight = FontWeights.Bold });
            title.Children.Add(new TextBlock { Text = "查看最近邮件，并使用当前账号直接回复或写新邮件。", Foreground = Theme.TextDimB, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
            header.Children.Add(title);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _accounts = new ComboBox { ItemsSource = _config.Current.Accounts, DisplayMemberPath = "Name", MinWidth = 180, Margin = new Thickness(0, 0, 12, 0) };
            Theme.StyleComboBox(_accounts, 200);
            actions.Children.Add(_accounts);
            _refresh = Theme.CreateButton("刷新", async () => await RefreshAsync());
            actions.Children.Add(_refresh);
            actions.Children.Add(Theme.CreateButton("＋ 写邮件", ComposeNew, true));
            if (_config.Current.Accounts.Count > 0) _accounts.SelectedIndex = 0;
            _accounts.SelectionChanged += async (s, e) => await RefreshAsync();
            Grid.SetColumn(actions, 1); header.Children.Add(actions);
            root.Children.Add(header);

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(365) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _status = new TextBlock { Text = "准备加载邮件…", Foreground = Theme.TextDimB, FontSize = 12, Margin = new Thickness(6, 2, 0, 10) };
            left.Children.Add(_status);
            _list = BuildMessageList();
            Grid.SetRow(_list, 1); left.Children.Add(_list);
            var leftCard = Theme.Card(left);
            leftCard.Padding = new Thickness(8);
            main.Children.Add(leftCard);

            var preview = BuildPreview();
            Grid.SetColumn(preview, 2); main.Children.Add(preview);
            Grid.SetRow(main, 1); root.Children.Add(main);
            Content = root;
        }

        private ListBox BuildMessageList()
        {
            var list = new ListBox
            {
                ItemsSource = _rows,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            var template = new DataTemplate(typeof(Services.MailListItem));
            var panel = new FrameworkElementFactory(typeof(Grid));
            panel.SetValue(Grid.MarginProperty, new Thickness(8, 7, 8, 7));
            var row1 = new FrameworkElementFactory(typeof(RowDefinition));
            row1.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
            var row2 = new FrameworkElementFactory(typeof(RowDefinition));
            row2.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
            panel.AppendChild(row1); panel.AppendChild(row2);
            var colDot = new FrameworkElementFactory(typeof(ColumnDefinition));
            colDot.SetValue(ColumnDefinition.WidthProperty, new GridLength(18));
            var colText = new FrameworkElementFactory(typeof(ColumnDefinition));
            colText.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var colDate = new FrameworkElementFactory(typeof(ColumnDefinition));
            colDate.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            panel.AppendChild(colDot); panel.AppendChild(colText); panel.AppendChild(colDate);

            var dot = new FrameworkElementFactory(typeof(TextBlock));
            dot.SetValue(TextBlock.TextProperty, "●");
            dot.SetValue(TextBlock.ForegroundProperty, Theme.AccentB);
            dot.SetValue(TextBlock.FontSizeProperty, 9.0);
            dot.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetBinding(UIElement.VisibilityProperty, new Binding("IsUnread") { Converter = new BooleanToVisibilityConverter() });
            panel.AppendChild(dot);

            var from = new FrameworkElementFactory(typeof(TextBlock));
            from.SetBinding(TextBlock.TextProperty, new Binding("From"));
            from.SetValue(TextBlock.ForegroundProperty, Theme.TextB);
            from.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            from.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            from.SetValue(Grid.ColumnProperty, 1); panel.AppendChild(from);
            var date = new FrameworkElementFactory(typeof(TextBlock));
            date.SetBinding(TextBlock.TextProperty, new Binding("DateText"));
            date.SetValue(TextBlock.ForegroundProperty, Theme.TextDimB);
            date.SetValue(TextBlock.FontSizeProperty, 11.0);
            date.SetValue(Grid.ColumnProperty, 2); panel.AppendChild(date);
            var subject = new FrameworkElementFactory(typeof(TextBlock));
            subject.SetBinding(TextBlock.TextProperty, new Binding("Subject"));
            subject.SetValue(TextBlock.ForegroundProperty, Theme.TextDimB);
            subject.SetValue(TextBlock.FontSizeProperty, 12.5);
            subject.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
            subject.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            subject.SetValue(Grid.RowProperty, 1); subject.SetValue(Grid.ColumnProperty, 1);
            subject.SetValue(Grid.ColumnSpanProperty, 2); panel.AppendChild(subject);
            template.VisualTree = panel;
            list.ItemTemplate = template;

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 1, 0, 1)));
            style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Theme.SelectionB));
            style.Triggers.Add(selected);
            list.ItemContainerStyle = style;
            list.SelectionChanged += async (s, e) => await LoadSelectedAsync();
            list.PreviewMouseRightButtonDown += (s, e) =>
            {
                var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
                if (row != null) row.IsSelected = true;
            };
            list.KeyDown += async (s, e) =>
            {
                if (e.Key != Key.Delete || list.SelectedItem == null) return;
                e.Handled = true;
                await DeleteSelectedAsync();
            };

            var menu = new ContextMenu();
            _listReadState = new MenuItem();
            _listReadState.Icon = MenuIcon("\uE8D7");
            _listReadState.Click += async (s, e) => await ToggleSelectedReadStateAsync(false);
            _listReply = new MenuItem { Header = "回复" };
            _listReply.Icon = MenuIcon("\uE97A");
            _listReply.Click += (s, e) => ReplyCurrent();
            _listDelete = new MenuItem { Header = "删除" };
            _listDelete.Icon = MenuIcon("\uE74D", true);
            _listDelete.Click += async (s, e) => await DeleteSelectedAsync();
            menu.Items.Add(_listReadState);
            menu.Items.Add(_listReply);
            menu.Items.Add(new Separator());
            menu.Items.Add(_listDelete);
            menu.Opened += (s, e) => UpdateMessageActions();
            Theme.StyleContextMenu(menu);
            list.ContextMenu = menu;
            return list;
        }

        private static TextBlock MenuIcon(string glyph, bool danger = false)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = danger ? Theme.DangerB : Theme.TextDimB,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static T FindAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null && !(source is T)) source = VisualTreeHelper.GetParent(source);
            return source as T;
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var found = FindDescendant<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private Border BuildPreview()
        {
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new Grid { Margin = new Thickness(8, 4, 8, 14) };
            for (int i = 0; i < 3; i++) top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var info = new StackPanel();
            _subject = new TextBlock { Text = "选择一封邮件", Foreground = Theme.TextB, FontSize = 21, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
            _meta = new TextBlock { Text = "", Foreground = Theme.TextDimB, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            info.Children.Add(_subject); info.Children.Add(_meta); top.Children.Add(info);
            var messageActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            _testExtract = Theme.CreateButton("测试提取", async () => await TestExtractionAsync());
            _markRead = Theme.CreateButton("标为已读", async () => await ToggleSelectedReadStateAsync(true));
            _delete = Theme.CreateButton("删除", async () => await DeleteSelectedAsync());
            _reply = Theme.CreateButton("回复", ReplyCurrent, true);
            _translate = Theme.CreateButton("翻译为中文", async () => await TranslateCurrentAsync());
            _translate.ToolTip = "使用第一个已启用的 LLM 配置，将主题和文本正文发送至该服务翻译；不发送附件和图片。";
            _translationAlternate = Theme.CreateButton("查看原文", () =>
            {
                if (_translationCts != null) _translationCts.Cancel();
                else ShowOriginalMessage();
            });
            _translationAlternate.Visibility = Visibility.Collapsed;
            _translate.IsEnabled = false;
            _testExtract.IsEnabled = false; _markRead.IsEnabled = false; _delete.IsEnabled = false; _reply.IsEnabled = false;
            messageActions.Children.Add(_testExtract); messageActions.Children.Add(_markRead); messageActions.Children.Add(_delete); messageActions.Children.Add(_reply);
            messageActions.Children.Add(_translate); messageActions.Children.Add(_translationAlternate);
            foreach (Button button in messageActions.Children) button.Margin = new Thickness(0, 0, 8, 6);
            Grid.SetRow(messageActions, 1); top.Children.Add(messageActions);
            _translationInfo = new TextBlock
            {
                Text = "翻译使用现有 LLM 配置，仅发送主题与文本正文。",
                Foreground = Theme.TextDimB, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(_translationInfo, 2); top.Children.Add(_translationInfo);
            panel.Children.Add(top);

            var bodyGrid = new Grid();
            _body = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Theme.TextB,
                FontSize = 14,
                Padding = new Thickness(8),
                IsReadOnlyCaretVisible = false
            };
            _htmlBody = new WebBrowser { Visibility = Visibility.Collapsed };
            _htmlBody.Navigating += HtmlBodyNavigating;
            SetBrowserSilent(_htmlBody);
            _htmlBody.LoadCompleted += (s, e) =>
            {
                // The woven document finished loading: mark it ready and apply any units that
                // completed while it was navigating, so progress is never lost to a reload.
                if (_htmlNavigated)
                {
                    _htmlDomReady = true;
                    ApplyHtmlUnitDeltas();
                }
            };
            _empty = new TextBlock { Text = "邮件正文将在这里显示", Foreground = Theme.TextDimB, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            bodyGrid.Children.Add(_body); bodyGrid.Children.Add(_htmlBody); bodyGrid.Children.Add(_empty);
            Grid.SetRow(bodyGrid, 1); panel.Children.Add(bodyGrid);
            return Theme.Card(panel);
        }

        private Models.AccountConfig CurrentAccount => _accounts.SelectedItem as Models.AccountConfig;

        private async Task RefreshAsync()
        {
            var account = CurrentAccount;
            _listCts?.Cancel();
            _messageCts?.Cancel();
            _rows.Clear(); ClearPreview();
            if (account == null)
            {
                _status.Text = "请先在主界面添加邮箱账号。";
                return;
            }
            _listCts = new CancellationTokenSource();
            var operation = _listCts;
            Theme.SetButtonLoading(_refresh, true, "加载中…");
            _status.Text = "正在加载 " + account.Name + "…";
            try
            {
                var rows = await _service.LoadInboxAsync(account, 60, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                foreach (var row in rows) _rows.Add(row);
                _status.Text = rows.Count == 0 ? "收件箱为空" : "最近 " + rows.Count + " 封邮件 · 右键邮件可进行操作";
                if (_rows.Count > 0) _list.SelectedIndex = 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _status.Text = "加载失败";
                MessageBox.Show(this, "无法读取邮件：\n" + ex.Message, "邮件中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { if (ReferenceEquals(_listCts, operation)) Theme.SetButtonLoading(_refresh, false); }
        }

        private async Task LoadSelectedAsync()
        {
            var item = _list.SelectedItem as Services.MailListItem;
            var account = CurrentAccount;
            _messageCts?.Cancel();
            _currentMessage = null;
            ResetTranslation();
            _reply.IsEnabled = false; _testExtract.IsEnabled = false; _markRead.IsEnabled = false; _delete.IsEnabled = false;
            if (item == null || account == null) { ClearPreview(); return; }
            _delete.IsEnabled = true;
            UpdateMessageActions();
            _subject.Text = item.Subject;
            _meta.Text = item.From + "  ·  正在加载正文…";
            _body.Text = ""; _body.Visibility = Visibility.Visible;
            _htmlBody.Visibility = Visibility.Collapsed;
            _empty.Text = "正在加载邮件正文…"; _empty.Visibility = Visibility.Visible;
            _messageCts = new CancellationTokenSource();
            var operation = _messageCts;
            try
            {
                var message = await _service.LoadMessageAsync(account, item.Id, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _currentMessage = message;
                _meta.Text = "发件人：" + message.From + "\n收件人：" + message.To + "  ·  " + message.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                ShowOriginalMessage();
                _reply.IsEnabled = true;
                _testExtract.IsEnabled = true;
                UpdateMessageActions();
                if (item.IsUnread)
                    _ = AutoMarkReadAfterDelayAsync(item, account, operation);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _empty.Text = "正文加载失败：" + ex.Message;
                _empty.Visibility = Visibility.Visible;
            }
        }

        private async Task AutoMarkReadAfterDelayAsync(Services.MailListItem item,
            Models.AccountConfig account, CancellationTokenSource operation)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!item.IsUnread || !ReferenceEquals(_list.SelectedItem, item) ||
                    !ReferenceEquals(CurrentAccount, account) || !ReferenceEquals(_messageCts, operation))
                    return;

                await _service.MarkAsReadAsync(account, item.Id, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_list.SelectedItem, item) || !ReferenceEquals(_messageCts, operation)) return;
                item.IsUnread = false;
                _list.Items.Refresh();
                UpdateMessageActions();
                _status.Text = "已阅读 5 秒，邮件已自动标为已读";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Services.Logger.Warn("auto mark read failed: " + ex.Message);
            }
        }

        private void ClearPreview()
        {
            _currentMessage = null;
            ResetTranslation();
            if (_subject == null) return;
            _subject.Text = "选择一封邮件"; _meta.Text = ""; _body.Text = "";
            _body.Visibility = Visibility.Visible; _htmlBody.Visibility = Visibility.Collapsed;
            _empty.Text = "邮件正文将在这里显示"; _empty.Visibility = Visibility.Visible;
            _reply.IsEnabled = false; _testExtract.IsEnabled = false; _markRead.IsEnabled = false; _delete.IsEnabled = false;
        }

        private async Task ToggleSelectedReadStateAsync(bool showButtonLoading)
        {
            var item = _list.SelectedItem as Services.MailListItem;
            var account = CurrentAccount;
            if (item == null || account == null) return;
            bool markAsRead = item.IsUnread;
            if (showButtonLoading) Theme.SetButtonLoading(_markRead, true, "标记中…");
            if (_listReadState != null) _listReadState.IsEnabled = false;
            try
            {
                await _service.SetReadStateAsync(account, item.Id, markAsRead, CancellationToken.None);
                item.IsUnread = !markAsRead;
                _list.Items.Refresh();
                _status.Text = markAsRead ? "邮件已标记为已读" : "邮件已标记为未读";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "标记失败：\n" + ex.Message, "邮件中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (showButtonLoading) Theme.SetButtonLoading(_markRead, false);
                UpdateMessageActions();
            }
        }

        private void UpdateMessageActions()
        {
            var item = _list?.SelectedItem as Services.MailListItem;
            var account = CurrentAccount;
            bool selected = item != null && account != null;
            bool supportsReadState = selected &&
                (Services.MailCenterService.IsGraphAccount(account) || account.Protocol == Models.MailProtocol.Imap);
            string readText = item != null && !item.IsUnread ? "标为未读" : "标为已读";

            if (_markRead != null)
            {
                _markRead.Content = readText;
                _markRead.IsEnabled = supportsReadState;
            }
            if (_delete != null) _delete.IsEnabled = selected;
            if (_testExtract != null) _testExtract.IsEnabled = selected && _currentMessage != null;
            UpdateTranslationActions();
            if (_listReadState != null)
            {
                _listReadState.Header = readText;
                _listReadState.IsEnabled = supportsReadState;
                _listReply.IsEnabled = selected && _currentMessage != null;
                _listDelete.IsEnabled = selected;
            }
        }

        private async Task DeleteSelectedAsync()
        {
            var item = _list.SelectedItem as Services.MailListItem;
            var account = CurrentAccount;
            if (item == null || account == null) return;
            if (MessageBox.Show(this, "确定删除邮件“" + item.Subject + "”吗？\n\n此操作会同步到邮件服务器。",
                "删除邮件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _messageCts?.Cancel();
            Theme.SetButtonLoading(_delete, true, "删除中…");
            _markRead.IsEnabled = false; _reply.IsEnabled = false;
            bool deleted = false;
            int deletedIndex = _list.SelectedIndex;
            try
            {
                await _service.DeleteAsync(account, item.Id, CancellationToken.None);
                deleted = true;
                _rows.Remove(item);
                ClearPreview();
                _status.Text = "邮件已删除 · 剩余 " + _rows.Count + " 封";
                if (_rows.Count > 0) _list.SelectedIndex = Math.Min(Math.Max(0, deletedIndex), _rows.Count - 1);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "删除失败：\n" + ex.Message, "邮件中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Theme.SetButtonLoading(_delete, false);
                if (!deleted)
                {
                    _delete.IsEnabled = true;
                    UpdateMessageActions();
                    _reply.IsEnabled = _currentMessage != null;
                }
            }
        }

        private void ResetTranslation()
        {
            var previous = _translationCts;
            _translationCts = null;
            previous?.Cancel();
            _translation = null;
            _translationSession = null;
            _htmlLayout = null;
            _htmlNavigated = false;
            _htmlDomReady = false;
            _htmlAppliedUnits.Clear();
            _htmlAppliedAttributes.Clear();
            _showingTranslation = false;
            if (_translationInfo != null) _translationInfo.Text = "翻译使用现有 LLM 配置，仅发送主题与文本正文。";
            UpdateTranslationActions();
        }

        private void UpdateTranslationActions()
        {
            if (_translate == null) return;
            bool busy = _translationCts != null;
            Theme.SetButtonLoading(_translate, busy, "翻译中…");
            _translate.IsEnabled = _currentMessage != null;
            if (!busy) _translate.Content = _translation != null ? "查看译文" :
                _htmlLayout != null && _htmlLayout.CompletedJobs > 0 ? "继续翻译" :
                _translationSession != null && _translationSession.CompletedParts > 0 ? "继续翻译" : "翻译为中文";
            _translationAlternate.Content = busy ? "取消翻译" : "查看原文";
            _translationAlternate.Visibility = busy || _showingTranslation ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowOriginalMessage()
        {
            var message = _currentMessage;
            if (message == null) return;
            _showingTranslation = false;
            _htmlNavigated = false;
            _htmlDomReady = false;
            _htmlAppliedUnits.Clear();
            _htmlAppliedAttributes.Clear();
            _subject.Text = message.Subject;
            if (!string.IsNullOrWhiteSpace(message.BodyHtml))
            {
                _body.Visibility = Visibility.Collapsed;
                _htmlBody.Visibility = Visibility.Visible;
                _htmlBody.NavigateToString(BuildHtmlDocument(message.BodyHtml));
            }
            else
            {
                _htmlBody.Visibility = Visibility.Collapsed;
                _body.Visibility = Visibility.Visible;
                _body.Text = message.Body;
                _body.ScrollToHome();
            }
            _empty.Visibility = string.IsNullOrWhiteSpace(message.Body) &&
                string.IsNullOrWhiteSpace(message.BodyHtml) ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = "这封邮件没有可显示的文本正文";
            if (_translation != null) _translationInfo.Text = "正在查看原文 · 可切换回已生成的译文，无需再次请求 LLM。";
            UpdateTranslationActions();
        }

        private void ShowTranslation()
        {
            ShowTranslation(false);
        }

        private void ShowTranslation(bool partial)
        {
            // HTML messages are translated in place inside their own document (see
            // RenderHtmlTranslation); the text path below only handles plain-text mail.
            if (_currentMessage != null && !string.IsNullOrWhiteSpace(_currentMessage.BodyHtml))
            {
                if (_htmlLayout != null)
                    RenderHtmlTranslation(_htmlLayout.Build(), _htmlLayout.CompletedJobs, _htmlLayout.TotalJobs);
                return;
            }
            // Derive the view from the session so completed segments appear progressively;
            // a finished session's merge equals the stored full translation.
            Services.MailTranslation view;
            int remainingSegments = 0;
            if (_translationSession != null &&
                (partial || _translation == null ? _translationSession.CompletedParts : _translationSession.TotalParts) > 0)
            {
                view = Services.MailTranslationService.Merge(_translationSession);
                remainingSegments = _translationSession.TotalParts - _translationSession.CompletedParts;
            }
            else
            {
                if (_translation == null) return;
                view = _translation;
            }
            _showingTranslation = true;
            _subject.Text = view.Subject ?? "";
            _htmlBody.Visibility = Visibility.Collapsed;
            _body.Visibility = Visibility.Visible;
            if (partial)
            {
                // Progressive plain-text merge: keep the reader's scroll position instead of
                // snapping to the top on every segment arrival.
                var scroller = FindDescendant<ScrollViewer>(_body);
                double offset = scroller?.VerticalOffset ?? 0;
                _body.Text = view.Body ?? "";
                if (scroller != null) scroller.ScrollToVerticalOffset(offset);
            }
            else
            {
                _body.Text = view.Body ?? "";
                _body.ScrollToHome();
            }
            _empty.Visibility = Visibility.Collapsed;
            _translationInfo.Text = remainingSegments > 0
                ? "简体中文 · 并行翻译中，还剩 " + remainingSegments + " 段将自动补全；未完成段落暂时显示原文。"
                : "简体中文 · AI 译文仅供参考；图片和原始排版请查看原文。";
            UpdateTranslationActions();
        }

        // Rebuilds and navigates the woven HTML document: original tags, images, links and
        // inline styles are preserved; translated text replaces the source text in place.
        private void RenderHtmlTranslation(string htmlDoc, int completed, int total)
        {
            // This (re)navigates with a fresh snapshot that already contains every completed
            // unit, so the DOM patch bookkeeping restarts from that point.
            _htmlNavigated = true;
            _htmlDomReady = false;
            // Snapshot completion counts are not contiguous indexes. Reapply every completed
            // ID after navigation; patches are idempotent, including completions during loading.
            _htmlAppliedUnits.Clear();
            _htmlAppliedAttributes.Clear();
            _showingTranslation = true;
            if (_htmlLayout != null && !string.IsNullOrWhiteSpace(_htmlLayout.TranslatedSubject))
                _subject.Text = _htmlLayout.TranslatedSubject;
            _body.Visibility = Visibility.Collapsed;
            _htmlBody.Visibility = Visibility.Visible;
            _htmlBody.NavigateToString(BuildHtmlDocument(htmlDoc ?? ""));
            _empty.Visibility = Visibility.Collapsed;
            int remaining = total - completed;
            _translationInfo.Text = remaining > 0
                ? "简体中文 · 已翻译 " + completed + "/" + total + " 段，其余自动补全；图片、链接与原始排版原样保留。"
                : "简体中文 · AI 译文 · 图片、链接与原始排版已原样保留。";
            UpdateTranslationActions();
        }

        // Completion order is arbitrary. Track successful patches by ID, never by count.
        private void ApplyHtmlUnitDeltas()
        {
            if (!_htmlDomReady || _htmlLayout == null) return;
            ApplyHtmlUnitDeltas(
                (i, k, text) => TryApplyHtmlScript("mpApply", i, k, text),
                (i, name, text) => TryApplyHtmlScript("mpApplyAttribute", i, name, text));
        }

        private bool TryApplyHtmlScript(string function, params object[] args)
        {
            try { return Equals(_htmlBody.InvokeScript(function, args), true); }
            catch { return false; } // A loading document is retried on LoadCompleted/next delta.
        }

        private void ApplyHtmlUnitDeltas(Func<int, int, string, bool> applyText,
            Func<int, string, string, bool> applyAttribute)
        {
            var layout = _htmlLayout;
            if (layout == null) return;
            lock (layout)
            {
                for (int i = 0; i < layout.Units.Count; i++)
                {
                    var unit = layout.Units[i];
                    if (!unit.Done || _htmlAppliedUnits.Contains(i)) continue;
                    for (int k = 0; k < unit.Fragments.Count; k++)
                        if (!applyText(i, k, unit.Fragments[k].Translation)) return;
                    _htmlAppliedUnits.Add(i);
                }
                if (layout.AttributesDone)
                {
                    for (int i = 0; i < layout.Attributes.Count; i++)
                    {
                        if (_htmlAppliedAttributes.Contains(i)) continue;
                        var attribute = layout.Attributes[i];
                        if (!applyAttribute(i, attribute.Name, attribute.Translated)) return;
                        _htmlAppliedAttributes.Add(i);
                    }
                }
            }
        }

        private async Task TranslateCurrentAsync()
        {
            var message = _currentMessage;
            var messageOperation = _messageCts;
            if (message == null || messageOperation == null || _translationCts != null) return;
            if (_translation != null) { ShowTranslation(); return; }
            if (messageOperation.IsCancellationRequested) return;
            var cfg = Services.LlmClassifier.FirstEnabled(_config.Current.Llms);
            if (cfg == null)
            {
                MessageBox.Show(this, "请先在主界面的 LLM 设置中添加并启用配置，填写模型和 API Key。\n\n邮件翻译不需要开启验证码的 LLM 兜底开关。",
                    "邮件翻译", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool isHtml = !string.IsNullOrWhiteSpace(message.BodyHtml);
            var operation = CancellationTokenSource.CreateLinkedTokenSource(messageOperation.Token);
            _translationCts = operation;
            UpdateTranslationActions();
            var elapsed = Stopwatch.StartNew();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            int completedParts = 0, totalParts = 0;
            Action updateProgress = () =>
            {
                if (!ReferenceEquals(_translationCts, operation) || operation.IsCancellationRequested || _showingTranslation) return;
                _translationInfo.Text = "翻译中 · 已完成 " + completedParts + "/" + totalParts +
                    " 段 · 本次已等待 " + (int)elapsed.Elapsed.TotalSeconds + " 秒 · 可取消，原文仍可阅读。";
            };
            timer.Tick += (s, e) => updateProgress();
            try
            {
                if (isHtml)
                {
                    if (_htmlLayout == null) _htmlLayout = Services.HtmlMailLayout.Parse(message.BodyHtml);
                    completedParts = _htmlLayout.CompletedJobs;
                    totalParts = _htmlLayout.TotalJobs;
                    var htmlProgress = new Progress<Services.HtmlTranslationProgress>(value =>
                    {
                        completedParts = value.CompletedUnits;
                        totalParts = value.TotalUnits;
                        updateProgress();
                        if (!ReferenceEquals(_translationCts, operation) || operation.IsCancellationRequested ||
                            !ReferenceEquals(_currentMessage, message) || value == null || value.CompletedUnits <= 0) return;
                        if (!_htmlNavigated)
                        {
                            // First completion: switch to the woven document once.
                            _htmlNavigated = true;
                            _htmlDomReady = false;
                            RenderHtmlTranslation(value.HtmlSnapshot, value.CompletedUnits, value.TotalUnits);
                        }
                        else
                        {
                            // Later completions patch the loaded document in place (mpApply),
                            // preserving the reader's scroll position.
                            ApplyHtmlUnitDeltas();
                            _translationInfo.Text = "简体中文 · 已翻译 " + value.CompletedUnits + "/" + value.TotalUnits +
                                " 段；未完成内容暂时保留原文。";
                        }
                    });
                    updateProgress();
                    timer.Start();
                    string woven = await _translator.TranslateHtmlAsync(_htmlLayout, message.Subject, cfg,
                        operation.Token, htmlProgress);
                    operation.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_translationCts, operation) || !ReferenceEquals(_currentMessage, message)) return;
                    _translation = new Services.MailTranslation
                    {
                        Subject = _htmlLayout.TranslatedSubject ?? message.Subject,
                        Body = "" // html translations render in the web view, not the text box
                    };
                    if (_htmlNavigated)
                    {
                        // The live document already has every unit patched in place; just mark
                        // completion and refresh the caption instead of reloading (keeps scroll).
                        ApplyHtmlUnitDeltas();
                        if (_htmlLayout != null && !string.IsNullOrWhiteSpace(_htmlLayout.TranslatedSubject))
                            _subject.Text = _htmlLayout.TranslatedSubject;
                        _translationInfo.Text = "简体中文 · AI 译文 · 图片、链接与原始排版已原样保留。";
                        UpdateTranslationActions();
                    }
                    else RenderHtmlTranslation(woven, _htmlLayout.TotalJobs, _htmlLayout.TotalJobs);
                }
                else
                {
                    if (_translationSession == null || !_translationSession.MatchesConfiguration(cfg))
                        _translationSession = _translator.CreateSession(message.Subject, message.Body, cfg);
                    completedParts = _translationSession.CompletedParts;
                    totalParts = _translationSession.TotalParts;
                    var progress = new Progress<Services.MailTranslationProgress>(value =>
                    {
                        completedParts = value.CompletedParts;
                        totalParts = value.TotalParts;
                        updateProgress();
                        if (!ReferenceEquals(_translationCts, operation) || operation.IsCancellationRequested ||
                            !ReferenceEquals(_currentMessage, message) || value == null || value.CompletedParts <= 0) return;
                        ShowTranslation(partial: true);
                    });
                    updateProgress();
                    timer.Start();
                    var translated = await _translator.TranslateAsync(_translationSession, operation.Token, progress);
                    operation.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_translationCts, operation) || !ReferenceEquals(_currentMessage, message)) return;
                    _translation = translated;
                    ShowTranslation();
                }
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(_translationCts, operation))
                    _translationInfo.Text = "翻译已取消，原邮件保持不变。已完成的段落会在重试时复用。";
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_translationCts, operation) || operation.IsCancellationRequested) return;
                // Restore controls before opening a modal error dialog (which may be obscured by another window).
                timer.Stop();
                _translationCts = null;
                UpdateTranslationActions();
                _translationInfo.Text = "翻译未完成，已完成的段落保留，点击翻译按钮可重试。";
                if (_showingTranslation)
                {
                    if (isHtml && _htmlLayout != null)
                        RenderHtmlTranslation(_htmlLayout.Build(), _htmlLayout.CompletedJobs, _htmlLayout.TotalJobs);
                    else if (_translationSession != null) ShowTranslation(partial: true);
                }
                Services.Logger.Warn("mail translation ui failed: " + ex.GetType().Name + ": " + ex.Message);
                MessageBox.Show(this, "翻译失败：\n" + ex.Message, "邮件翻译", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                timer.Stop();
                if (ReferenceEquals(_translationCts, operation))
                {
                    _translationCts = null;
                    UpdateTranslationActions();
                }
                operation.Dispose();
            }
        }

        private async Task TestExtractionAsync()
        {
            var item = _list.SelectedItem as Services.MailListItem;
            var account = CurrentAccount;
            var message = _currentMessage;
            var operation = _messageCts;
            if (item == null || account == null || message == null || operation == null) return;

            Theme.SetButtonLoading(_testExtract, true, "测试中…");
            string stage = "本地规则";
            try
            {
                operation.Token.ThrowIfCancellationRequested();
                string subject = Services.TextEncodingRepair.Repair(message.Subject ?? "");
                string body = Services.TextEncodingRepair.Repair(message.Body ?? "");
                string from = Services.TextEncodingRepair.Repair(message.From ?? "");
                var result = _classification.Evaluate(subject, body, from, account.Name, _config.Current.Rules);

                if (!result.Matched && _config.Current.LlmFallbackEnabled)
                {
                    var llmConfig = Services.LlmClassifier.FirstEnabled(_config.Current.Llms);
                    if (llmConfig != null)
                    {
                        stage = "本地规则未命中，已执行 LLM 回退";
                        result = await _llm.ClassifyAsync(subject, body, from, account.Name,
                            llmConfig, _config.Current.LlmPrompt, operation.Token);
                    }
                    else stage = "本地规则未命中，LLM 回退未配置";
                }

                operation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_list.SelectedItem, item) || !ReferenceEquals(_messageCts, operation)) return;
                result.BodyPreview = Services.MailMonitorService.CreateBodyPreview(body);
                if (Services.MailCenterService.IsGraphAccount(account) || account.Protocol == Models.MailProtocol.Imap)
                    result.MarkAsRead = () => _ = MarkTestedMessageReadAsync(account, item);

                if (!result.Matched)
                {
                    MessageBox.Show(this,
                        "测试流程已完成：\n\n✓ 邮件正文读取\n✓ 乱码修复\n✓ " + stage +
                        "\n✗ 未提取到验证码或确认链接\n\n请检查规则关键词、正文正则或 LLM 设置。",
                        "验证码提取测试", MessageBoxButton.OK, MessageBoxImage.Information);
                    _status.Text = "测试完成 · 未命中验证码规则";
                    return;
                }

                var toast = new ToastWindow(result, () => { });
                toast.Show();
                string details = !string.IsNullOrWhiteSpace(result.Code) ? "验证码 " + result.Code
                    : !string.IsNullOrWhiteSpace(result.Url) ? "确认链接" : "提醒条件";
                _status.Text = "测试成功 · " + stage + " · 已提取" + details + "并展示提醒弹窗";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show(this, "测试失败：\n" + ex.Message, "验证码提取测试",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Theme.SetButtonLoading(_testExtract, false);
                UpdateMessageActions();
            }
        }

        private async Task MarkTestedMessageReadAsync(Models.AccountConfig account, Services.MailListItem item)
        {
            if (account == null || item == null || !item.IsUnread) return;
            try
            {
                await _service.SetReadStateAsync(account, item.Id, true, CancellationToken.None);
                item.IsUnread = false;
                _list.Items.Refresh();
                UpdateMessageActions();
                _status.Text = "测试邮件已标记为已读";
            }
            catch (Exception ex) { Services.Logger.Warn("test toast mark read failed: " + ex.Message); }
        }

        private void ComposeNew()
        {
            var dlg = new ComposeMailDialog(_config, CurrentAccount, "", "", "") { Owner = this };
            dlg.ShowDialog();
        }

        private void ReplyCurrent()
        {
            if (_currentMessage == null) return;
            string subject = _currentMessage.Subject ?? "";
            if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)) subject = "Re: " + subject;
            string quoted = "\n\n---------------- 原邮件 ----------------\n" + _currentMessage.Body;
            var dlg = new ComposeMailDialog(_config, CurrentAccount, _currentMessage.From, subject, quoted) { Owner = this };
            dlg.ShowDialog();
        }

        private void HtmlBodyNavigating(object sender, System.Windows.Navigation.NavigatingCancelEventArgs e)
        {
            if (e.Uri == null || string.Equals(e.Uri.Scheme, "about", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            if (!string.Equals(e.Uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Uri.Scheme, "mailto", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Services.Logger.Warn("open mail link failed: " + ex.Message); }
        }

        private static string BuildHtmlDocument(string html)
        {
            html = SanitizeMailHtml(html);
            string background = ColorHex(Theme.Surface);
            string foreground = ColorHex(Theme.Text);
            string accent = ColorHex(Theme.Accent);
            string style = DocumentStyle(background, foreground, accent);
            // mpApply lets the app patch translated blocks in the already-loaded document
            // (see data-mp markers written by HtmlMailLayout) so the reader keeps scroll
            // position while segments arrive.
            const string script = "<script>function mpApply(i,k,t){var el=document.querySelector('[data-mp=\\\"'+i+'\\\"][data-frag=\\\"'+k+'\\\"]');" +
                "if(!el)return false;el.textContent=t;return true;}" +
                "function mpApplyAttribute(i,n,t){var el=document.querySelector('[data-mp-attr-'+i+']');" +
                "if(!el)return false;el.setAttribute(n,t);return true;}</script>";
            string body = html + script;
            if (Regex.IsMatch(html, "<head[^>]*>", RegexOptions.IgnoreCase))
                return Regex.Replace(body, "<head([^>]*)>", "<head$1>" + style, RegexOptions.IgnoreCase);
            return "<!doctype html><html><head>" + style + "</head><body>" + body + "</body></html>";
        }

        private static string DocumentStyle(string background, string foreground, string accent)
        {
            return "<meta charset=\"utf-8\"><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">" +
                "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">" +
                "<style>html,body{margin:0;padding:0;background:" + background + ";color:" + foreground +
                ";font-family:'Segoe UI','Microsoft YaHei',sans-serif;font-size:14px;line-height:1.6;}" +
                "body{padding:10px 12px;box-sizing:border-box;overflow-wrap:anywhere;}" +
                "img{max-width:100%;height:auto;}a{color:" + accent + ";text-decoration:none;}" +
                "a:hover{text-decoration:underline;}table{max-width:100%;}pre{white-space:pre-wrap;}</style>";
        }

        private static string SanitizeMailHtml(string html)
        {
            html = html ?? "";
            html = Regex.Replace(html, "<script[\\s\\S]*?</script>|<iframe[\\s\\S]*?</iframe>|<object[\\s\\S]*?</object>|<embed[^>]*>",
                "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "\\s+on[a-z]+\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+)",
                "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "\\s+target\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+)",
                "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<meta[^>]+http-equiv\\s*=\\s*['\"]?refresh['\"]?[^>]*>",
                "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<meta[^>]+charset\\s*=\\s*[^>]+>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<meta[^>]+http-equiv\\s*=\\s*['\"]?content-type['\"]?[^>]*>",
                "", RegexOptions.IgnoreCase);
            return html;
        }

        private static string ColorHex(Color color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private static void SetBrowserSilent(WebBrowser browser)
        {
            browser.LoadCompleted += (s, e) =>
            {
                try
                {
                    var field = typeof(WebBrowser).GetField("_axIWebBrowser2",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var activeX = field?.GetValue(browser);
                    activeX?.GetType().InvokeMember("Silent", System.Reflection.BindingFlags.SetProperty,
                        null, activeX, new object[] { true });
                }
                catch { }
            };
        }
    }

    public class ComposeMailDialog : Window
    {
        private readonly Services.ConfigService _config;
        private readonly Services.MailCenterService _service;
        private ComboBox _account;
        private TextBox _to, _cc, _subject, _body;
        private TextBlock _status;
        private Button _send;

        public ComposeMailDialog(Services.ConfigService config, Models.AccountConfig selected, string to, string subject, string body)
        {
            _config = config; _service = new Services.MailCenterService(config);
            Title = "写邮件"; Width = 720; Height = 650; MinWidth = 600; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Theme.BgB;
            Closed += (s, e) => _service.Dispose();
            var root = new Grid { Margin = new Thickness(22) };
            for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var heading = new TextBlock { Text = "写邮件", Foreground = Theme.AccentB, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 15) };
            Grid.SetColumnSpan(heading, 2); root.Children.Add(heading);
            _account = new ComboBox { ItemsSource = config.Current.Accounts, DisplayMemberPath = "Name", Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleComboBox(_account);
            _account.SelectedItem = selected ?? config.Current.Accounts.FirstOrDefault();
            AddField(root, 1, "发件账号", _account);
            _to = StyledText(to); AddField(root, 2, "收件人", _to);
            _cc = StyledText(""); AddField(root, 3, "抄送", _cc);
            _subject = StyledText(subject); AddField(root, 4, "主题", _subject);
            _body = StyledText(body); _body.AcceptsReturn = true; _body.TextWrapping = TextWrapping.Wrap;
            _body.VerticalContentAlignment = VerticalAlignment.Top;
            _body.HorizontalContentAlignment = HorizontalAlignment.Left;
            _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _body.MinHeight = 260;
            Grid.SetRow(_body, 5); Grid.SetColumn(_body, 0); Grid.SetColumnSpan(_body, 2); root.Children.Add(_body);

            var footer = new Grid { Margin = new Thickness(0, 15, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _status = new TextBlock { Text = "多个地址可用逗号分隔", Foreground = Theme.TextDimB, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            footer.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _send = Theme.CreateButton("发送", async () => await SendAsync(), true);
            buttons.Children.Add(_send); buttons.Children.Add(Theme.CreateButton("取消", Close));
            Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
            Grid.SetRow(footer, 6); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer);
            Content = root;
        }

        private TextBox StyledText(string value)
        {
            var box = new TextBox { Text = value ?? "", Margin = new Thickness(0, 3, 0, 3) };
            Theme.StyleTextBox(box); return box;
        }

        private static void AddField(Grid grid, int row, string label, Control control)
        {
            var text = Theme.Label(label); Grid.SetRow(text, row); grid.Children.Add(text);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1); grid.Children.Add(control);
        }

        private async Task SendAsync()
        {
            var account = _account.SelectedItem as Models.AccountConfig;
            if (account == null) { MessageBox.Show(this, "请选择发件账号。"); return; }
            if (string.IsNullOrWhiteSpace(_to.Text)) { MessageBox.Show(this, "请填写收件人。"); return; }
            Theme.SetButtonLoading(_send, true, "发送中…"); _status.Text = "正在发送…";
            try
            {
                await _service.SendAsync(account, _to.Text, _cc.Text, _subject.Text, _body.Text, CancellationToken.None);
                MessageBox.Show(this, "邮件已发送。", "邮件中心", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                _status.Text = "发送失败";
                string hint = Services.MailCenterService.IsGraphAccount(account)
                    ? "请检查收件人地址和 Microsoft Graph 授权。"
                    : "请检查账号中的 SMTP 服务器、端口和授权码。";
                MessageBox.Show(this, "发送失败：\n" + ex.Message + "\n\n" + hint, "邮件中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { Theme.SetButtonLoading(_send, false); }
        }
    }
}
