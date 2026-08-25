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

            b.MouseEnter += (s, e) => { if (b.IsEnabled) b.Background = Brush(hover); };
            b.MouseLeave += (s, e) => { if (b.IsEnabled) b.Background = Brush(idle); };
            b.IsEnabledChanged += (s, e) =>
            {
                if (b.IsEnabled) b.Background = Brush(idle);
                else b.Background = Brush(DisabledBg);
            };
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
            sel.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            itemStyle.Triggers.Add(sel);
            c.ItemContainerStyle = itemStyle;
            c.Resources = new ResourceDictionary { [typeof(ComboBoxItem)] = itemStyle };

            // fully themed ComboBox template: dark popup, explicit foreground
            var template = new ControlTemplate(typeof(ComboBox));
            var grid = new FrameworkElementFactory(typeof(Grid));

            var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
            toggle.SetValue(Control.BackgroundProperty, Brush(InputBg));
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


