using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MailPulse.UI
{
    public enum ThemeMode { Light, Dark }

    /// <summary>Shared theme palette with switchable light/dark modes.</summary>
    public static class Theme
    {
        public static Color Bg, Surface, SurfaceHi, Border, Accent, AccentHi, Danger,
                           Text, TextDim, CodeColor, BadgeBg, SelectionBg, AltRowBg,
                           Hover, DisabledBg, InputBg;

        public static void Apply(ThemeMode mode)
        {
            if (mode == ThemeMode.Dark)
            {
                Bg        = C(0x16, 0x18, 0x1E);
                Surface   = C(0x21, 0x24, 0x2D);
                SurfaceHi = C(0x2A, 0x2E, 0x3A);
                Border    = C(0x38, 0x3D, 0x4A);
                Accent    = C(0x4F, 0x8C, 0xFF);
                AccentHi  = C(0x6B, 0xA3, 0xFF);
                Danger    = C(0xE5, 0x48, 0x4D);
                Text      = C(0xEC, 0xEE, 0xF4);
                TextDim   = C(0x98, 0x9E, 0xAC);
                CodeColor = C(0xFF, 0xD8, 0x66);
                BadgeBg   = C(0x2E, 0x46, 0x6B);
                SelectionBg = C(0x2E, 0x46, 0x6B);
                AltRowBg  = C(0x26, 0x29, 0x33);
                Hover     = C(0x36, 0x3B, 0x49);
                DisabledBg= C(0x1D, 0x20, 0x28);
                InputBg   = C(0x18, 0x1B, 0x22);
            }
            else
            {
                Bg        = C(0xF3, 0xF4, 0xF7);
                Surface   = C(0xFF, 0xFF, 0xFF);
                SurfaceHi = C(0xE9, 0xEB, 0xF0);
                Border    = C(0xD3, 0xD8, 0xE0);
                Accent    = C(0x2F, 0x6F, 0xED);
                AccentHi  = C(0x4D, 0x84, 0xF5);
                Danger    = C(0xDC, 0x26, 0x26);
                Text      = C(0x1F, 0x23, 0x28);
                TextDim   = C(0x6B, 0x72, 0x80);
                CodeColor = C(0xB4, 0x53, 0x09);
                BadgeBg   = C(0xDC, 0xE9, 0xFD);
                SelectionBg = C(0xDB, 0xE9, 0xFF);
                AltRowBg  = C(0xF7, 0xF8, 0xFA);
                Hover     = C(0xDC, 0xE1, 0xE8);
                DisabledBg= C(0xF1, 0xF2, 0xF5);
                InputBg   = C(0xFF, 0xFF, 0xFF);
            }
        }

        public static ThemeMode ParseMode(string s)
            => string.Equals(s, "Dark", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Dark : ThemeMode.Light;

        private static Color C(int r, int g, int b) => Color.FromRgb((byte)r, (byte)g, (byte)b);

        public static Brush Brush(Color c) => new SolidColorBrush(c);
        public static Brush BgB        => Brush(Bg);
        public static Brush SurfaceB   => Brush(Surface);
        public static Brush SurfaceHiB => Brush(SurfaceHi);
        public static Brush BorderB    => Brush(Border);
        public static Brush AccentB    => Brush(Accent);
        public static Brush AccentHiB  => Brush(AccentHi);
        public static Brush DangerB    => Brush(Danger);
        public static Brush TextB      => Brush(Text);
        public static Brush TextDimB   => Brush(TextDim);
        public static Brush CodeB      => Brush(CodeColor);
        public static Brush BadgeB     => Brush(BadgeBg);
        public static Brush SelectionB => Brush(SelectionBg);
        public static Brush AltRowB    => Brush(AltRowBg);

        /// <summary>Rounded modern button with hover highlight.</summary>
        public static Button CreateButton(string text, Action onClick, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(20, 10, 20, 10),
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = TextB,
                BorderThickness = new Thickness(1),
                Focusable = false
            };
            ApplyButtonStyle(b, primary);
            b.Click += (s, e) => onClick();
            return b;
        }

        public static void ApplyButtonStyle(Button b, bool primary = false)
        {
            Color idle = primary ? Accent : SurfaceHi;
            Color hover = primary ? AccentHi : Hover;
            Color border = primary ? Accent : Border;
            Color disabled = Desaturate(Accent, 0.55);
            b.Resources["MailPulse.DisabledButtonBackground"] = Brush(disabled);
            b.Background = Brush(idle);
            b.BorderBrush = Brush(border);
            if (primary) b.Foreground = Brushes.White;

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            borderFactory.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(7));
            borderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(System.Windows.Controls.Border.SnapsToDevicePixelsProperty, true);
            var contentFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            contentFactory.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentFactory.SetValue(System.Windows.Controls.ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            borderFactory.AppendChild(contentFactory);
            template.VisualTree = borderFactory;
            b.Template = template;

            b.MouseEnter += (s, e) => { if (b.IsEnabled && !(b.Tag is ButtonLoadingState)) b.Background = Brush(hover); };
            b.MouseLeave += (s, e) => { if (b.IsEnabled && !(b.Tag is ButtonLoadingState)) b.Background = Brush(idle); };
            b.IsEnabledChanged += (s, e) =>
            {
                if (b.IsEnabled) b.Background = Brush(idle);
                else b.Background = Brush(disabled);
            };
        }

        private static Color Desaturate(Color color, double amount)
        {
            double gray = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
            byte Blend(byte value) => (byte)Math.Round(value * (1 - amount) + gray * amount);
            return Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
        }

        private sealed class ButtonLoadingState
        {
            public object Content;
            public Brush Background;
            public Brush BorderBrush;
            public Brush Foreground;
            public System.Windows.Input.Cursor Cursor;
            public bool IsHitTestVisible;
        }

        /// <summary>Shows a visible busy indicator while preventing duplicate clicks.</summary>
        public static void SetButtonLoading(Button button, bool loading, string text = "处理中…")
        {
            if (button == null) return;
            if (loading)
            {
                if (button.Tag is ButtonLoadingState) return;
                button.Tag = new ButtonLoadingState
                {
                    Content = button.Content,
                    Background = button.Background,
                    BorderBrush = button.BorderBrush,
                    Foreground = button.Foreground,
                    Cursor = button.Cursor,
                    IsHitTestVisible = button.IsHitTestVisible
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new ProgressBar
                {
                    IsIndeterminate = true,
                    Width = 34,
                    Height = 5,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                content.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                });
                button.Content = content;
                button.Background = AccentB;
                button.BorderBrush = AccentB;
                button.Foreground = Brushes.White;
                button.Cursor = System.Windows.Input.Cursors.Wait;
                button.IsHitTestVisible = false;
                return;
            }

            var state = button.Tag as ButtonLoadingState;
            if (state == null) return;
            button.Content = state.Content;
            button.Background = state.Background;
            button.BorderBrush = state.BorderBrush;
            button.Foreground = state.Foreground;
            button.Cursor = state.Cursor;
            button.IsHitTestVisible = state.IsHitTestVisible;
            button.Tag = null;
            if (!button.IsEnabled)
                button.Background = button.Resources.Contains("MailPulse.DisabledButtonBackground")
                    ? button.Resources["MailPulse.DisabledButtonBackground"] as Brush
                    : Brush(DisabledBg);
        }

        public static void StyleTextBox(TextBox t, double width = double.NaN)
        {
            t.Background = Brush(InputBg);
            t.Foreground = TextB;
            t.BorderBrush = BorderB;
            t.BorderThickness = new Thickness(1);
            t.Padding = new Thickness(8, 5, 8, 5);
            t.FontSize = 13;
            t.CaretBrush = TextB;
            t.VerticalContentAlignment = t.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center;
            if (!double.IsNaN(width)) t.Width = width;
            t.GotFocus += (s, e) => t.BorderBrush = AccentB;
            t.LostFocus += (s, e) => t.BorderBrush = BorderB;
        }

        public static void StyleComboBox(ComboBox c, double width = double.NaN)
        {
            c.Foreground = TextB;
            c.FontSize = 13;
            if (!double.IsNaN(width)) c.Width = width;

            // readable dropdown items in both themes
            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush(InputBg)));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, TextB));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var hover = new Trigger { Property = Control.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(SurfaceHi)));
            itemStyle.Triggers.Add(hover);
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Control.BackgroundProperty, AccentB));
            // Theme text color preserves contrast on both the light selection fill
            // and the dark-theme selection fill, including the hovered state.
            sel.Setters.Add(new Setter(Control.ForegroundProperty, TextB));
            itemStyle.Triggers.Add(sel);
            c.ItemContainerStyle = itemStyle;
            c.Resources = new ResourceDictionary { [typeof(ComboBoxItem)] = itemStyle };

            // fully themed ComboBox template: dark popup, explicit foreground
            var template = new ControlTemplate(typeof(ComboBox));
            var grid = new FrameworkElementFactory(typeof(Grid));

            var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
            toggle.SetValue(Control.BackgroundProperty, Brush(InputBg));
            toggle.SetValue(Control.ForegroundProperty, TextB);
            toggle.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, TextB);
            toggle.SetValue(Control.BorderBrushProperty, BorderB);
            toggle.SetValue(Control.BorderThicknessProperty, new Thickness(1));
            toggle.SetValue(Control.CursorProperty, System.Windows.Input.Cursors.Hand);
            toggle.SetValue(Control.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            toggle.SetValue(Control.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            toggle.SetValue(Control.FocusableProperty, false);
            toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

            var tbTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton));
            var tbBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            tbBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            tbBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            tbBorder.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            tbBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var tbGrid = new FrameworkElementFactory(typeof(Grid));
            var arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.SetValue(TextBlock.TextProperty, "\u25BC");
            arrow.SetValue(TextBlock.ForegroundProperty, TextDimB);
            arrow.SetValue(TextBlock.FontSizeProperty, 9.0);
            arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 9, 0));
            tbGrid.AppendChild(arrow);
            tbBorder.AppendChild(tbGrid);
            tbTemplate.VisualTree = tbBorder;
            toggle.SetValue(Control.TemplateProperty, tbTemplate);
            grid.AppendChild(toggle);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
            content.SetValue(ContentPresenter.ContentTemplateSelectorProperty, new TemplateBindingExtension(ComboBox.ItemTemplateSelectorProperty));
            content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
            // SelectionBoxItem can retain the popup item's white foreground in light mode.
            // Force the collapsed selected value to use the current theme text color.
            content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, TextB);
            grid.AppendChild(content);

            var popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup));
            popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true);
            popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Bottom);
            popup.SetValue(System.Windows.Controls.Primitives.Popup.StaysOpenProperty, false);
            popup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty,
                new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            var popupBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            popupBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty, Brush(InputBg));
            popupBorder.SetValue(System.Windows.Controls.Border.BorderBrushProperty, BorderB);
            popupBorder.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            popupBorder.SetValue(System.Windows.Controls.Border.MarginProperty, new Thickness(0, 2, 0, 0));
            popupBorder.SetBinding(System.Windows.Controls.Border.MinWidthProperty,
                new Binding("ActualWidth") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            popupBorder.AppendChild(items);
            popup.AppendChild(popupBorder);
            grid.AppendChild(popup);

            template.VisualTree = grid;
            c.Template = template;
        }

        /// <summary>Rounded, theme-aware context menu used by list actions.</summary>
        public static void StyleContextMenu(ContextMenu menu)
        {
            menu.Background = Brushes.Transparent;
            menu.BorderThickness = new Thickness(0);
            menu.Padding = new Thickness(0);
            menu.HasDropShadow = true;

            var menuTemplate = new ControlTemplate(typeof(ContextMenu));
            var shell = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            shell.SetValue(System.Windows.Controls.Border.BackgroundProperty, SurfaceB);
            shell.SetValue(System.Windows.Controls.Border.BorderBrushProperty, BorderB);
            shell.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            shell.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(9));
            shell.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(5));
            shell.SetValue(System.Windows.Controls.Border.SnapsToDevicePixelsProperty, true);
            var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            shell.AppendChild(presenter);
            menuTemplate.VisualTree = shell;
            menu.Template = menuTemplate;

            var itemStyle = new Style(typeof(MenuItem));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, TextB));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11, 8, 16, 8)));
            itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            itemStyle.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));

            var itemTemplate = new ControlTemplate(typeof(MenuItem));
            var itemBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            itemBorder.Name = "ItemBorder";
            itemBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            itemBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            var itemGrid = new FrameworkElementFactory(typeof(Grid));
            var iconColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
            iconColumn.SetValue(ColumnDefinition.WidthProperty, new GridLength(25));
            var textColumn = new FrameworkElementFactory(typeof(ColumnDefinition));
            textColumn.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            itemGrid.AppendChild(iconColumn);
            itemGrid.AppendChild(textColumn);
            var icon = new FrameworkElementFactory(typeof(ContentPresenter));
            icon.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
            icon.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            itemGrid.AppendChild(icon);
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(Grid.ColumnProperty, 1);
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            header.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            itemGrid.AppendChild(header);
            itemBorder.AppendChild(itemGrid);
            itemTemplate.VisualTree = itemBorder;
            var highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlighted.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Hover)));
            itemTemplate.Triggers.Add(highlighted);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.OpacityProperty, 0.45));
            itemTemplate.Triggers.Add(disabled);
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));

            var separatorStyle = new Style(typeof(Separator));
            separatorStyle.Setters.Add(new Setter(Control.HeightProperty, 1.0));
            separatorStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(8, 5, 8, 5)));
            separatorStyle.Setters.Add(new Setter(Control.BackgroundProperty, BorderB));
            var separatorTemplate = new ControlTemplate(typeof(Separator));
            var separatorBorder = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            separatorBorder.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            separatorTemplate.VisualTree = separatorBorder;
            separatorStyle.Setters.Add(new Setter(Control.TemplateProperty, separatorTemplate));

            menu.Resources[typeof(MenuItem)] = itemStyle;
            menu.Resources[typeof(Separator)] = separatorStyle;
        }

        public static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextDimB,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
        }

        public static TextBlock Title(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextB,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            };
        }

        /// <summary>Rounded card container with border.</summary>
        public static Border Card(UIElement child)
        {
            return new Border
            {
                Background = SurfaceB,
                BorderBrush = BorderB,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Child = child
            };
        }
    }
}


