using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MailPulse.UI
{
    /// <summary>Modern frameless toast: rounded, shadowed, slide-in animation, 30s countdown bar.</summary>
    public class ToastWindow : Window
    {
        private static int _offsetIndex = 0;

        public ToastWindow(Models.ClassifyResult result, Action onClose)
        {
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Width = 420; Height = 232;
            Background = Brushes.Transparent;
            Opacity = 0;

            var card = new Border
            {
                Background = Theme.SurfaceB,
                BorderBrush = Theme.BorderB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(10),
                Padding = new Thickness(16, 12, 16, 10)
            };
            card.Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.55,
                Color = Color.FromRgb(0, 0, 0)
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var stack = new StackPanel();

            // header
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border
            {
                Width = 26, Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Theme.AccentB,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.Child = new TextBlock { Text = "✉", Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0);
            header.Children.Add(icon);
            var title = new TextBlock
            {
                Text = (result.AccountName ?? "新邮件") + " · 新邮件",
                Foreground = Theme.TextB,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            stack.Children.Add(header);

            if (!string.IsNullOrEmpty(result.From))
                stack.Children.Add(new TextBlock
                {
                    Text = "发件人 " + result.From,
                    Foreground = Theme.TextDimB,
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

            stack.Children.Add(new TextBlock
            {
                Text = result.Summary ?? "",
                Foreground = Theme.TextB,
                FontSize = 13,
                Margin = new Thickness(0, 7, 0, 4),
                MaxHeight = 40,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // code row
            if (!string.IsNullOrEmpty(result.Code))
            {
                var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                var codeBox = new Border
                {
                    Background = Theme.SurfaceHiB,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 4, 12, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                codeBox.Child = new TextBlock { Text = result.Code, Foreground = Theme.CodeB, FontSize = 24, FontWeight = FontWeights.Bold };
                codeRow.Children.Add(codeBox);
                codeRow.Children.Add(Theme.CreateButton("复制", () =>
                {
                    try { Clipboard.SetText(result.Code); } catch { }
                    try { result.MarkAsRead?.Invoke(); } catch { }
                    Close();
                }));
                stack.Children.Add(codeRow);
            }

            if (!string.IsNullOrEmpty(result.Url))
            {
                var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
                linkRow.Children.Add(Theme.CreateButton("打开链接", () =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.Url) { UseShellExecute = true }); } catch { }
                    try { result.MarkAsRead?.Invoke(); } catch { }
                    Close();
                }));
                stack.Children.Add(linkRow);
            }

            if (string.IsNullOrEmpty(result.Code) && string.IsNullOrEmpty(result.Url))
                stack.Children.Add(new TextBlock
                {
                    Text = "⚠ 规则命中，但未提取到验证码或链接，请打开邮件查看",
                    Foreground = Theme.CodeB,
                    FontSize = 12,
                    Margin = new Thickness(0, 8, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });

            var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dismiss = Theme.CreateButton("忽略", () => { try { result.MarkAsRead?.Invoke(); } catch { } Close(); }, true);
            dismiss.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(dismiss, 1);
            footer.Children.Add(dismiss);
            stack.Children.Add(footer);

            Grid.SetRow(stack, 0);
            root.Children.Add(stack);

            // countdown progress bar
            var progress = new Border
            {
                Height = 3,
                Background = Theme.SurfaceHiB,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(10, 0, 10, 6)
            };
            var bar = new Border
            {
                Width = 380,
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Theme.AccentB,
                CornerRadius = new CornerRadius(1.5)
            };
            progress.Child = bar;
            Grid.SetRow(progress, 1);
            root.Children.Add(progress);

            card.Child = root;
            Content = card;

            Loaded += (s, e) =>
            {
                double x = SystemParameters.WorkArea.Right - Width - 14;
                double y = SystemParameters.WorkArea.Bottom - Height - 14 - ((_offsetIndex++) * (Height + 10)) % Math.Max(1, SystemParameters.WorkArea.Height - Height - 30);
                Left = x; Top = y;

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                BeginAnimation(OpacityProperty, fadeIn);

                var translate = new TranslateTransform();
                RenderTransform = translate;
                var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                translate.BeginAnimation(TranslateTransform.YProperty, slide);

                var barWidth = Math.Max(1, progress.ActualWidth);
                bar.Width = barWidth;
                var shrink = new DoubleAnimation(barWidth, 0, TimeSpan.FromSeconds(30));
                bar.BeginAnimation(FrameworkElement.WidthProperty, shrink);
            };
            Closed += (s, e) => onClose?.Invoke();

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += (s, e) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }
}
