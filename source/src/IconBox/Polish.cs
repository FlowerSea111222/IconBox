using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IconBox;

// 全局控件样式层。
// 设计原则（参考 Apple HIG，但落在 Windows/WPF 上）：
// · 少用描边、多用填充：控件的层次靠“底色深浅”表达，而不是一圈边框。
// · 圆角统一走 Tokens.Radius，控件 9px、卡片 12~16px。
// · 悬停/按下只做轻微变化（填充加深、透明度变化），不做位移。
// · 焦点态用强调色 2px 描边，保证键盘可用性。
public static class Polish
{
	private static readonly BoundedCache<string, Brush> brushes = new BoundedCache<string, Brush>(96, StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<bool, Dictionary<object, Style>> themes = new Dictionary<bool, Dictionary<object, Style>>();

	private static readonly FontFamily uiFont = Tokens.Type.Text;

	public static Brush Accent => Tokens.AccentBrush(light: false);

	// ── 基础取色 ────────────────────────────────────────────────────────────
	public static Brush Color(string hex)
	{
		if (brushes.TryGet(hex, out var value))
		{
			return value;
		}
		SolidColorBrush solidColorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
		solidColorBrush.Freeze();
		brushes.Put(hex, solidColorBrush);
		return solidColorBrush;
	}

	public static Brush Ink(bool light) => Tokens.Label(light);

	public static Brush Muted(bool light) => Tokens.LabelSecondary(light);

	// 卡片底色：浅色主题用高不透明白，深色主题用低不透明亮色叠加玻璃。
	public static Brush Card(bool light) => light ? Color("#B3FFFFFF") : Color("#2EFFFFFF");

	// 极细分隔线。
	public static Brush Line(bool light) => Tokens.Separator(light);

	// ── 模板辅助 ────────────────────────────────────────────────────────────
	private static string Hex(Color c) => "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

	private static string Pal(string xaml, bool light)
	{
		// 注意替换顺序：必须「长键优先」，否则 @FILL 会吃掉 @FILL2 的前缀，
		// 生成 "#14000000" + "2" 这种 9 位非法颜色（曾导致 XAML 报 "Token is not valid"）。
		Color accent = light ? Tokens.Light.Accent : Tokens.Dark.Accent;
		return xaml
			.Replace("@SURFACERAISED", Hex(light ? Tokens.Light.SurfaceRaised : Tokens.Dark.SurfaceRaised))
			.Replace("@SURFACE", Hex(light ? Tokens.Light.Surface : Tokens.Dark.Surface))
			.Replace("@LABEL3", Hex(light ? Tokens.Light.LabelTertiary : Tokens.Dark.LabelTertiary))
			.Replace("@LABEL2", Hex(light ? Tokens.Light.LabelSecondary : Tokens.Dark.LabelSecondary))
			.Replace("@LABEL", Hex(light ? Tokens.Light.Label : Tokens.Dark.Label))
			.Replace("@ACCENT20", Hex(Tokens.Tint(accent, 0.20)))
			.Replace("@ACCENT12", Hex(Tokens.Tint(accent, 0.12)))
			.Replace("@ACCENT", Hex(accent))
			.Replace("@FILL2", Hex(light ? Tokens.Light.FillStrong : Tokens.Dark.FillStrong))
			.Replace("@FILL", Hex(light ? Tokens.Light.Fill : Tokens.Dark.Fill))
			.Replace("@SEP", Hex(light ? Tokens.Light.Separator : Tokens.Dark.Separator))
			.Replace("@DANGER", Hex(light ? Tokens.Light.Red : Tokens.Dark.Red));
	}
	private static ControlTemplate Template(string xaml, bool light)
	{
		// 模板里用到 x:Name / x:Key，必须在根元素声明 x 前缀；
		// 统一在此补，避免每个模板字符串重复写。
		string text = Pal(xaml, light);
		int at = text.IndexOf("xmlns=", StringComparison.Ordinal);
		if (at >= 0 && text.IndexOf("xmlns:x", StringComparison.Ordinal) < 0)
		{
			text = text.Insert(at, "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' ");
		}
		try
		{
			return (ControlTemplate)XamlReader.Parse(text);
		}
		catch
		{
			System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gb-xaml-fail.txt"), text, System.Text.Encoding.UTF8);
			throw;
		}
	}

	private static Style StyleOf(Type type, ControlTemplate template, bool light)
	{
		Style style = new Style(type);
		style.Setters.Add(new Setter(Control.TemplateProperty, template));
		style.Setters.Add(new Setter(Control.ForegroundProperty, Tokens.Label(light)));
		style.Setters.Add(new Setter(Control.FontFamilyProperty, uiFont));
		style.Setters.Add(new Setter(Control.FontSizeProperty, Tokens.Type.Body));
		return style;
	}

	private static Binding Parent(string path)
	{
		return new Binding(path)
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
		};
	}

	// ── 按钮 ────────────────────────────────────────────────────────────────
	// kind: secondary（默认，填充浅灰）/ primary（强调色实心）/ ghost（无底色）/ danger（红色淡底）
	public static Style ButtonStyle(bool light, string kind = "secondary")
	{
		string bg, hover, press, fg, border;
		switch (kind)
		{
			case "primary":
				bg = "@ACCENT";
				hover = Hex(Blend(light ? Tokens.Light.Accent : Tokens.Dark.Accent, Colors.White, 0.16));
				press = Hex(Blend(light ? Tokens.Light.Accent : Tokens.Dark.Accent, Colors.Black, 0.12));
				fg = "#FFFFFFFF";
				border = "Transparent";
				break;
			case "ghost":
				bg = "Transparent";
				hover = "@FILL";
				press = "@FILL2";
				fg = "@LABEL";
				border = "Transparent";
				break;
			case "danger":
				bg = Hex(Tokens.Tint(light ? Tokens.Light.Red : Tokens.Dark.Red, 0.14));
				hover = Hex(Tokens.Tint(light ? Tokens.Light.Red : Tokens.Dark.Red, 0.22));
				press = Hex(Tokens.Tint(light ? Tokens.Light.Red : Tokens.Dark.Red, 0.30));
				fg = "@DANGER";
				border = "Transparent";
				break;
			default:
				bg = "@FILL";
				hover = "@FILL2";
				press = "@FILL2";
				fg = "@LABEL";
				border = "Transparent";
				break;
		}
		string xaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>"
			+ "<Border x:Name='Chrome' Background='" + bg + "' BorderBrush='" + border + "' BorderThickness='1' CornerRadius='" + Tokens.Radius.Card.ToString("0.##") + "' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>"
			+ "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/></Border>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='" + hover + "'/></Trigger>"
			+ "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Chrome' Property='Background' Value='" + press + "'/><Setter TargetName='Chrome' Property='Opacity' Value='0.85'/></Trigger>"
			+ "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='Chrome' Property='Opacity' Value='0.38'/></Trigger>"
			+ "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='@ACCENT'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style style = StyleOf(typeof(Button), Template(xaml, light), light);
		style.Setters.Add(new Setter(Control.ForegroundProperty, fg == "#FFFFFFFF" ? Brushes.White : (Brush)new BrushConverter().ConvertFromString(Pal(fg, light))));
		style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0.0)));
		style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14.0, 6.0, 14.0, 6.0)));
		style.Setters.Add(new Setter(Control.FontSizeProperty, Tokens.Type.Body));
		style.Setters.Add(new Setter(Control.FontWeightProperty, Tokens.Type.Medium));
		style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
		style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32.0));
		return style;
	}

	private static Color Blend(Color a, Color b, double amount)
	{
		amount = Math.Max(0.0, Math.Min(1.0, amount));
		// 注意：类内存在同名方法 Color(string)，因此这里必须写全限定类型名。
		return System.Windows.Media.Color.FromArgb(a.A,
			(byte)Math.Round(a.R + (b.R - a.R) * amount),
			(byte)Math.Round(a.G + (b.G - a.G) * amount),
			(byte)Math.Round(a.B + (b.B - a.B) * amount));
	}

	// ── 窗口样式注入 ────────────────────────────────────────────────────────
	public static void Apply(Window w, bool light)
	{
		w.UseLayoutRounding = true;
		w.SnapsToDevicePixels = true;
		w.FontFamily = uiFont;
		w.Foreground = Ink(light);
		w.FontSize = Tokens.Type.Body;
		TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
		TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
		if (themes.TryGetValue(light, out var cached))
		{
			foreach (KeyValuePair<object, Style> item in cached)
			{
				if (w.Resources[item.Key] != item.Value)
				{
					w.Resources[item.Key] = item.Value;
				}
			}
			return;
		}

		w.Resources[typeof(Button)] = ButtonStyle(light);
		w.Resources["PrimaryButton"] = ButtonStyle(light, "primary");
		w.Resources["GhostButton"] = ButtonStyle(light, "ghost");
		w.Resources["DangerButton"] = ButtonStyle(light, "danger");
		MenuTheme.Install(w, light);

		// 文本框：填充式，无硬描边，聚焦时强调色细边。
		string textBoxXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>"
			+ "<Border x:Name='Chrome' Background='@FILL' BorderBrush='Transparent' BorderThickness='1' CornerRadius='" + Tokens.Radius.Control.ToString("0.##") + "' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>"
			+ "<ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>"
			+ "</Border>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@FILL2'/></Trigger>"
			+ "<Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='@ACCENT'/><Setter TargetName='Chrome' Property='Background' Value='@FILL'/></Trigger>"
			+ "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style textBox = StyleOf(typeof(TextBox), Template(textBoxXaml, light), light);
		textBox.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		textBox.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0.0)));
		textBox.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11.0, 6.0, 11.0, 6.0)));
		textBox.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34.0));
		textBox.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
		textBox.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Tokens.AccentBrush(light)));
		textBox.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, Tokens.Brush(Tokens.Tint(light ? Tokens.Light.Accent : Tokens.Dark.Accent, 0.35))));
		w.Resources[typeof(TextBox)] = textBox;

		// 下拉框
		string comboXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>"
			+ "<Grid>"
			+ "<ToggleButton x:Name='PART_Toggle' Focusable='False' ClickMode='Press' Background='@FILL' BorderBrush='Transparent' BorderThickness='1' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>"
			+ "<ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='" + Tokens.Radius.Control.ToString("0.##") + "'/></ControlTemplate></ToggleButton.Template>"
			+ "</ToggleButton>"
			+ "<ContentPresenter Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' Margin='12,0,32,0' VerticalAlignment='Center' IsHitTestVisible='False'/>"
			+ "<Path Data='M 1,1 L 5,5 L 9,1' Stroke='@LABEL2' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,13,1' IsHitTestVisible='False'/>"
			+ "<Popup x:Name='PART_Popup' Placement='Bottom' VerticalOffset='5' AllowsTransparency='True' IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}' StaysOpen='False'>"
			+ "<Border Background='@SURFACERAISED' BorderBrush='@SEP' BorderThickness='1' CornerRadius='" + Tokens.Radius.Card.ToString("0.##") + "' Padding='4' MinWidth='160'>"
			+ "<Border.Effect><DropShadowEffect BlurRadius='24' ShadowDepth='6' Opacity='0.20' Color='#000000'/></Border.Effect>"
			+ "<ScrollViewer MaxHeight='280' VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>"
			+ "</Border></Popup></Grid>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='PART_Toggle' Property='Background' Value='@FILL2'/></Trigger>"
			+ "<Trigger Property='IsDropDownOpen' Value='True'><Setter TargetName='PART_Toggle' Property='Background' Value='@FILL2'/></Trigger>"
			+ "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style combo = StyleOf(typeof(ComboBox), Template(comboXaml, light), light);
		combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12.0, 6.0, 32.0, 6.0)));
		combo.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32.0));
		combo.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
		w.Resources[typeof(ComboBox)] = combo;

		string comboItemXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ComboBoxItem'>"
			+ "<Border x:Name='Chrome' Background='Transparent' CornerRadius='" + Tokens.Radius.Small.ToString("0.##") + "' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>"
			+ "<ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@ACCENT12'/></Trigger>"
			+ "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@ACCENT20'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style comboItem = StyleOf(typeof(ComboBoxItem), Template(comboItemXaml, light), light);
		comboItem.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10.0, 7.0, 10.0, 7.0)));
		comboItem.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
		w.Resources[typeof(ComboBoxItem)] = comboItem;

		// 列表
		Style listBox = new Style(typeof(ListBox));
		listBox.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		listBox.Setters.Add(new Setter(Control.ForegroundProperty, Tokens.Label(light)));
		listBox.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0.0)));
		listBox.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
		w.Resources[typeof(ListBox)] = listBox;

		string listItemXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'>"
			+ "<Border x:Name='Chrome' Background='Transparent' CornerRadius='" + Tokens.Radius.Small.ToString("0.##") + "' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>"
			+ "<ContentPresenter VerticalAlignment='Center'/></Border>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@FILL'/></Trigger>"
			+ "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@ACCENT12'/><Setter Property='Foreground' Value='@ACCENT'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style listItem = StyleOf(typeof(ListBoxItem), Template(listItemXaml, light), light);
		listItem.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10.0, 8.0, 10.0, 8.0)));
		listItem.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0.0, 1.0, 0.0, 1.0)));
		w.Resources[typeof(ListBoxItem)] = listItem;

		// 标签页：分段控件观感（选中项为抬起底色）
		string tabXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TabItem'>"
			+ "<Border x:Name='Chrome' Background='Transparent' CornerRadius='10' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>"
			+ "<ContentPresenter ContentSource='Header' HorizontalAlignment='Left' VerticalAlignment='Center'/></Border>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@FILL'/></Trigger>"
			+ "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@ACCENT'/></Trigger>"
			+ "<Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='#FFFFFFFF'/></Trigger>"
			+ "<Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Effect'>"
			+ "<Setter.Value><DropShadowEffect BlurRadius='16' ShadowDepth='2' Direction='270' Opacity='0.28' Color='#FF0A84FF'/></Setter.Value></Setter></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style tab = StyleOf(typeof(TabItem), Template(tabXaml, light), light);
		tab.Setters.Add(new Setter(Control.ForegroundProperty, Tokens.LabelSecondary(light)));
		tab.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13.0, 10.0, 15.0, 10.0)));
		tab.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0.0, 3.0, 0.0, 3.0)));
		tab.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
		tab.Setters.Add(new Setter(Control.FontWeightProperty, Tokens.Type.Medium));
		tab.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
		// 让选中胶囊铺满左侧导航列（列宽 232 DIP − 左右各 18 DIP 边距 = 196 DIP），避免高亮只包住文字。
		tab.Setters.Add(new Setter(FrameworkElement.WidthProperty, 196.0));
		w.Resources[typeof(TabItem)] = tab;

		// TabControl：内容区铺满、无默认边框内衬，配合左侧导航。
		string tabControlXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TabControl'>"
			+ "<Grid Background='Transparent' KeyboardNavigation.TabNavigation='Local'>"
			+ "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>"
			+ "<TabPanel x:Name='HeaderPanel' Grid.Column='0' IsItemsHost='True' Panel.ZIndex='1' Margin='{TemplateBinding Padding}'/>"
			+ "<ContentPresenter x:Name='PART_SelectedContentHost' Grid.Column='1' ContentSource='SelectedContent'"
			+ " HorizontalAlignment='Stretch' VerticalAlignment='Stretch' SnapsToDevicePixels='{TemplateBinding SnapsToDevicePixels}'/>"
			+ "</Grid></ControlTemplate>";
		Style tabControl = StyleOf(typeof(TabControl), Template(tabControlXaml, light), light);
		w.Resources[typeof(TabControl)] = tabControl;

		// 滑块
		string sliderXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>"
			+ "<Grid Height='26' Background='Transparent'>"
			+ "<Border Height='4' VerticalAlignment='Center' CornerRadius='2' Background='@FILL2'/>"
			+ "<Track x:Name='PART_Track' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>"
			+ "<Track.DecreaseRepeatButton><RepeatButton Command='{x:Static Slider.DecreaseLarge}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Height='4' VerticalAlignment='Center' CornerRadius='2' Background='@ACCENT'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>"
			+ "<Track.IncreaseRepeatButton><RepeatButton Command='{x:Static Slider.IncreaseLarge}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>"
			+ "<Track.Thumb><Thumb Width='18' Height='18'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border CornerRadius='9' Background='#FFFFFFFF'><Border.Effect><DropShadowEffect BlurRadius='5' ShadowDepth='1' Opacity='0.28'/></Border.Effect></Border></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
			+ "</Track></Grid></ControlTemplate>";
		Style slider = new Style(typeof(Slider));
		slider.Setters.Add(new Setter(FrameworkElement.HeightProperty, 26.0));
		slider.Setters.Add(new Setter(Control.TemplateProperty, Template(sliderXaml, light)));
		w.Resources[typeof(Slider)] = slider;

		// 复选框：圆角方块 + 强调色填充
		string checkXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>"
			+ "<Grid Background='Transparent'>"
			+ "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>"
			+ "<Border x:Name='Box' Width='18' Height='18' CornerRadius='6' Background='@FILL' BorderBrush='@SEP' BorderThickness='1' VerticalAlignment='Center' SnapsToDevicePixels='True'>"
			+ "<Path x:Name='Mark' Data='M 2.5,6.5 L 5.5,9.5 L 11,3.5' Stroke='#FFFFFFFF' StrokeThickness='1.9' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Visibility='Collapsed' Stretch='None'/>"
			+ "</Border>"
			+ "<ContentPresenter Grid.Column='1' Margin='9,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>"
			+ "</Grid>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='@ACCENT'/></Trigger>"
			+ "<Trigger Property='IsChecked' Value='True'><Setter TargetName='Box' Property='Background' Value='@ACCENT'/><Setter TargetName='Box' Property='BorderBrush' Value='@ACCENT'/><Setter TargetName='Mark' Property='Visibility' Value='Visible'/></Trigger>"
			+ "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='@ACCENT'/></Trigger>"
			+ "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style check = StyleOf(typeof(CheckBox), Template(checkXaml, light), light);
		check.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0.0)));
		check.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
		w.Resources[typeof(CheckBox)] = check;

		// 开关（iOS 风格）：左侧文字、右侧拨杆。用于设置页的布尔项。
		string switchXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>"
			+ "<Grid Background='Transparent'>"
			+ "<Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>"
			+ "<ContentPresenter Grid.Column='0' VerticalAlignment='Center' RecognizesAccessKey='True'/>"
			+ "<Border x:Name='Track' Grid.Column='1' Width='44' Height='26' CornerRadius='13' Background='@FILL2' Margin='14,0,0,0' SnapsToDevicePixels='True'>"
			+ "<Border x:Name='Knob' Width='22' Height='22' CornerRadius='11' Background='#FFFFFFFF' HorizontalAlignment='Left' Margin='2,0,0,0'>"
			+ "<Border.RenderTransform><TranslateTransform x:Name='KnobShift' X='0'/></Border.RenderTransform>"
			+ "<Border.Effect><DropShadowEffect BlurRadius='4' ShadowDepth='1' Opacity='0.25'/></Border.Effect>"
			+ "</Border></Border></Grid>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsChecked' Value='True'>"
			+ "<Setter TargetName='Track' Property='Background' Value='@ACCENT'/>"
			+ "<Trigger.EnterActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='KnobShift' Storyboard.TargetProperty='X' To='18' Duration='0:0:0.18'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></DoubleAnimation.EasingFunction></DoubleAnimation></Storyboard></BeginStoryboard></Trigger.EnterActions>"
			+ "<Trigger.ExitActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='KnobShift' Storyboard.TargetProperty='X' To='0' Duration='0:0:0.18'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></DoubleAnimation.EasingFunction></DoubleAnimation></Storyboard></BeginStoryboard></Trigger.ExitActions>"
			+ "</Trigger>"
			+ "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style switchStyle = StyleOf(typeof(CheckBox), Template(switchXaml, light), light);
		switchStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
		switchStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
		w.Resources["Switch"] = switchStyle;

		// 单选
		string radioXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='RadioButton'>"
			+ "<Grid Background='Transparent'>"
			+ "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>"
			+ "<Border x:Name='Ring' Width='18' Height='18' CornerRadius='9' Background='@FILL' BorderBrush='@SEP' BorderThickness='1' VerticalAlignment='Center' SnapsToDevicePixels='True'>"
			+ "<Border x:Name='Dot' Width='9' Height='9' CornerRadius='4.5' Background='#FFFFFFFF' Visibility='Collapsed'/></Border>"
			+ "<ContentPresenter Grid.Column='1' Margin='9,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>"
			+ "</Grid>"
			+ "<ControlTemplate.Triggers>"
			+ "<Trigger Property='IsChecked' Value='True'><Setter TargetName='Ring' Property='Background' Value='@ACCENT'/><Setter TargetName='Ring' Property='BorderBrush' Value='@ACCENT'/><Setter TargetName='Dot' Property='Visibility' Value='Visible'/></Trigger>"
			+ "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>"
			+ "</ControlTemplate.Triggers></ControlTemplate>";
		Style radio = StyleOf(typeof(RadioButton), Template(radioXaml, light), light);
		radio.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
		w.Resources[typeof(RadioButton)] = radio;

		// 滚动条：细、圆、低存在感（垂直/水平各一套模板，避免水平条被撑成竖条）
		string scrollV = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>"
			+ "<Grid Background='Transparent' Width='10'>"
			+ "<Track x:Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True'>"
			+ "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>"
			+ "<Track.Thumb><Thumb MinHeight='28'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='Bar' Width='6' CornerRadius='3' Background='@FILL2' HorizontalAlignment='Center'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
			+ "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>"
			+ "</Track></Grid></ControlTemplate>";
		string scrollH = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>"
			+ "<Grid Background='Transparent' Height='10'>"
			+ "<Track x:Name='PART_Track' Orientation='Horizontal' IsDirectionReversed='False'>"
			+ "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>"
			+ "<Track.Thumb><Thumb MinWidth='28'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border x:Name='Bar' Height='6' CornerRadius='3' Background='@FILL2' VerticalAlignment='Center'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
			+ "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>"
			+ "</Track></Grid></ControlTemplate>";
		Style scroll = new Style(typeof(ScrollBar));
		scroll.Setters.Add(new Setter(Control.TemplateProperty, Template(scrollV, light)));
		scroll.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		scroll.Triggers.Add(new Trigger
		{
			Property = ScrollBar.OrientationProperty,
			Value = Orientation.Horizontal,
			Setters = { new Setter(Control.TemplateProperty, Template(scrollH, light)) }
		});
		w.Resources[typeof(ScrollBar)] = scroll;

		// 提示气泡：深色半透明、圆角、轻投影
		string toolTipXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ToolTip'>"
			+ "<Border Background='@SURFACERAISED' BorderBrush='@SEP' BorderThickness='1' CornerRadius='" + Tokens.Radius.Small.ToString("0.##") + "' Padding='9,5'>"
			+ "<Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='4' Opacity='0.18'/></Border.Effect>"
			+ "<ContentPresenter/></Border></ControlTemplate>";
		Style toolTip = StyleOf(typeof(ToolTip), Template(toolTipXaml, light), light);
		toolTip.Setters.Add(new Setter(Control.FontSizeProperty, Tokens.Type.Callout));
		toolTip.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9.0, 5.0, 9.0, 5.0)));
		w.Resources[typeof(ToolTip)] = toolTip;

		// 进度条（PART_Track 必须存在，否则 WPF 无法更新指示条宽度）
		string progressXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ProgressBar'>"
			+ "<Grid Height='4' VerticalAlignment='Center'>"
			+ "<Border x:Name='PART_Track' CornerRadius='2' Background='@FILL2'/>"
			+ "<Border x:Name='PART_Indicator' CornerRadius='2' Background='@ACCENT' HorizontalAlignment='Left'/>"
			+ "</Grid></ControlTemplate>";
		Style progress = new Style(typeof(ProgressBar));
		progress.Setters.Add(new Setter(Control.TemplateProperty, Template(progressXaml, light)));
		progress.Setters.Add(new Setter(FrameworkElement.HeightProperty, 4.0));
		w.Resources[typeof(ProgressBar)] = progress;

		// 分隔线样式交给 MenuTheme（菜单内分隔线由它统一），此处不再覆盖 typeof(Separator)，
		// 否则会把菜单里的分隔线换成普通横线。

		cached = new Dictionary<object, Style>();
		foreach (DictionaryEntry resource in w.Resources)
		{
			// 同时缓存 Type 键与字符串键（PrimaryButton/Switch 等命名样式），
			// 否则第二个同主题窗口只会拿到 Type 样式，命名样式会缺失。
			if (resource.Value is Style s)
			{
				s.Seal();
				cached[resource.Key] = s;
			}
		}
		themes[light] = cached;
	}

	// ── 组合件 ──────────────────────────────────────────────────────────────
	// 分组卡片：标题 + 说明 + 内容。用于设置页，替代“一堆裸控件堆叠”。
	public static Border Section(string title, string description, bool light)
	{
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = Tokens.Label(light),
			FontFamily = Tokens.Type.Display,
			FontSize = Tokens.Type.Title2,
			FontWeight = Tokens.Type.SemiBold
		});
		if (!string.IsNullOrWhiteSpace(description))
		{
			stackPanel.Children.Add(new TextBlock
			{
				Text = description,
				Foreground = Tokens.LabelSecondary(light),
				TextWrapping = TextWrapping.Wrap,
				FontSize = Tokens.Type.Callout,
				LineHeight = 18.0,
				Margin = new Thickness(0.0, 6.0, 0.0, 16.0)
			});
		}
		Border border = new Border
		{
			CornerRadius = new CornerRadius(Tokens.Radius.Panel),
			Background = Card(light),
			BorderBrush = Line(light),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(26.0, 24.0, 26.0, 26.0),
			Child = stackPanel,
			Margin = new Thickness(0.0, 0.0, 0.0, 18.0)
		};
		border.Effect = CardShadow();
		return border;
	}

	// 卡片柔和投影：克制（浅色更淡），仅用于设置页分组卡片。
	private static System.Windows.Media.Effects.DropShadowEffect CardShadow()
	{
		return new System.Windows.Media.Effects.DropShadowEffect
		{
			BlurRadius = 28.0,
			ShadowDepth = 2.0,
			Direction = 270.0,
			Opacity = 0.05,
			Color = System.Windows.Media.Colors.Black,
			RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
		};
	}

	public static TextBlock Title(string text, bool light) => new TextBlock
	{
		Text = text,
		Foreground = Tokens.Label(light),
		FontFamily = Tokens.Type.Display,
		FontSize = Tokens.Type.Title1,
		FontWeight = Tokens.Type.SemiBold
	};

	public static TextBlock Caption(string text, bool light) => new TextBlock
	{
		Text = text,
		Foreground = Tokens.LabelSecondary(light),
		FontSize = Tokens.Type.Callout,
		TextWrapping = TextWrapping.Wrap
	};

	// ── 图标（SF Symbols 风格的线性图标） ───────────────────────────────────
	// 统一 24×24 视框、1.7px 描边、圆头圆角；填充型图标（播放/暂停等）单独标记。
	public static FrameworkElement Icon(string name, Brush color, double size = 20.0)
	{
		(string data, bool filled) = IconPath(name);
		return new Path
		{
			Data = Geometry.Parse(data),
			Fill = (filled ? color : Brushes.Transparent),
			Stroke = (filled ? null : color),
			StrokeThickness = (filled ? 0.0 : 1.7),
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true
		};
	}

	private static (string, bool) IconPath(string name)
	{
		return name switch
		{
			// 系统/导航
			"settings" => ("M12,3.2 A2.2,2.2 0 1,0 12,7.6 A2.2,2.2 0 1,0 12,3.2 M12,3.2 L12,2 M12,21 L12,20.8 M3.2,12 L2,12 M22,12 L20.8,12 M5.8,5.8 L4.9,4.9 M19.1,19.1 L18.2,18.2 M5.8,18.2 L4.9,19.1 M19.1,4.9 L18.2,5.8", false),
			"close" => ("M6,6 L18,18 M18,6 L6,18", false),
			"plus" => ("M12,5 L12,19 M5,12 L19,12", false),
			"minus" => ("M5,12 L19,12", false),
			"check" => ("M4.5,12.5 L9.5,17.5 L19.5,6.5", false),
			"chevron-down" => ("M6,9.5 L12,15.5 L18,9.5", false),
			"chevron-up" => ("M6,14.5 L12,8.5 L18,14.5", false),
			"chevron-left" => ("M14.5,6 L8.5,12 L14.5,18", false),
			"chevron-right" => ("M9.5,6 L15.5,12 L9.5,18", false),
			"more" => ("M5,12 A1.4,1.4 0 1,0 5,12.1 M12,12 A1.4,1.4 0 1,0 12,12.1 M19,12 A1.4,1.4 0 1,0 19,12.1", true),
			"search" => ("M10.5,4 A6.5,6.5 0 1,0 10.5,17 A6.5,6.5 0 1,0 10.5,4 M15.2,15.2 L20,20", false),
			"grid" => ("M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z", false),
			"apps" => ("M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z", false),
			"folder" => ("M3,7.5 A1.5,1.5 0 0,1 4.5,6 L9,6 L11,8 L19.5,8 A1.5,1.5 0 0,1 21,9.5 L21,17.5 A1.5,1.5 0 0,1 19.5,19 L4.5,19 A1.5,1.5 0 0,1 3,17.5 Z", false),
			"note" => ("M6,3 L15,3 L19,7 L19,21 L5,21 L5,3 Z M14,3 L14,8 L19,8 M8.5,12 L15.5,12 M8.5,16 L14,16", false),
			"sun" => ("M12,8 A4,4 0 1,0 12,16 A4,4 0 1,0 12,8 M12,2.5 L12,5 M12,19 L12,21.5 M2.5,12 L5,12 M19,12 L21.5,12 M5.3,5.3 L7,7 M17,17 L18.7,18.7 M5.3,18.7 L7,17 M17,7 L18.7,5.3", false),
			"moon" => ("M20,14.5 A8.5,8.5 0 0,1 9.5,4 A8.5,8.5 0 1,0 20,14.5 Z", false),
			"bell" => ("M6,9 A6,6 0 0,1 18,9 L18,14 L20,17 L4,17 L6,14 Z M9.5,17 A2.5,2.5 0 0,0 14.5,17", false),
			"trash" => ("M4,7 L20,7 M9,7 L9,4.5 L15,4.5 L15,7 M6,7 L7,20.5 L17,20.5 L18,7 M10,11 L10,17 M14,11 L14,17", false),
			"lock" => ("M6.5,11 L17.5,11 L17.5,20 L6.5,20 Z M8.5,11 L8.5,7.5 A3.5,3.5 0 0,1 15.5,7.5 L15.5,11", false),
			"eye" => ("M2.5,12 C5,7 8.5,5 12,5 C15.5,5 19,7 21.5,12 C19,17 15.5,19 12,19 C8.5,19 5,17 2.5,12 Z M12,9 A3,3 0 1,0 12,15 A3,3 0 1,0 12,9", false),
			"refresh" => ("M20,12 A8,8 0 1,1 17.2,6 M20,3 L20,7 L16,7", false),
			"power" => ("M12,3 L12,11 M7.2,6.2 A7.5,7.5 0 1,0 16.8,6.2", false),
			"wifi" => ("M3,8.5 C8,4 16,4 21,8.5 M6,12.5 C9.5,9.5 14.5,9.5 18,12.5 M9,16.5 C11,14.8 13,14.8 15,16.5 M12,20 L12,20.1", false),
			"control" => ("M4,7 L14,7 M18,7 L20,7 M4,17 L6,17 M10,17 L20,17 M16,4.8 L16,9.2 M8,14.8 L8,19.2", false),
			"computer" => ("M3.5,5 L20.5,5 L20.5,16 L3.5,16 Z M8.5,20 L15.5,20 M12,16 L12,20", false),
			"recycle" => ("M7,5.5 L12,4 L17,5.5 M6,9 L4.5,14 L8,18 M18,9 L19.5,14 L16,18 M9.5,19 L14.5,19", false),
			"clock" => ("M12,3.5 A8.5,8.5 0 1,0 12,20.5 A8.5,8.5 0 1,0 12,3.5 M12,7.5 L12,12 L15.5,14", false),
			"calendar" => ("M4,6 L20,6 L20,20 L4,20 Z M4,10 L20,10 M8.5,3.5 L8.5,7 M15.5,3.5 L15.5,7", false),
			"image" => ("M4,5 L20,5 L20,19 L4,19 Z M4,16 L9,11 L13,15 L16,12.5 L20,16 M15.5,9 A1.3,1.3 0 1,0 15.5,9.1", false),
			"palette" => ("M12,3.5 A8.5,8.5 0 1,0 12,20.5 C14,20.5 14.5,19 13.5,17.8 C12.8,16.9 13.4,15.8 14.6,15.8 L17,15.8 C19,15.8 20.5,14.4 20.5,12.4 C20.5,7.5 16.7,3.5 12,3.5 Z M8,10 A1.2,1.2 0 1,0 8,10.1 M12,7.5 A1.2,1.2 0 1,0 12,7.6 M16,9.5 A1.2,1.2 0 1,0 16,9.6", false),
			"info" => ("M12,3.5 A8.5,8.5 0 1,0 12,20.5 A8.5,8.5 0 1,0 12,3.5 M12,11 L12,16.5 M12,7.8 L12,7.9", false),
			"warning" => ("M12,3.5 L21,19.5 L3,19.5 Z M12,9 L12,14 M12,16.8 L12,16.9", false),
			"download" => ("M12,3.5 L12,15 M7,10.5 L12,15.5 L17,10.5 M4,20 L20,20", false),
			"upload" => ("M12,20.5 L12,9 M7,13.5 L12,8.5 L17,13.5 M4,4 L20,4", false),
			"external" => ("M14,4 L20,4 L20,10 M20,4 L11,13 M17,14 L17,19.5 L4.5,19.5 L4.5,7 L10,7", false),
			"pin" => ("M9,3.5 L15,3.5 L14,9 L18,13 L6,13 L10,9 Z M12,13 L12,20.5", false),
			"drag" => ("M9,6 L9,6.1 M15,6 L15,6.1 M9,12 L9,12.1 M15,12 L15,12.1 M9,18 L9,18.1 M15,18 L15,18.1", true),
			"brush" => ("M4,20 C5.5,20 7,19.5 7.5,18 C8,16.5 7,15 5.5,15 C4,15 3,16 3,17.5 C3,19 3.5,20 4,20 Z M9.5,15.5 L18.5,6.5 A1.8,1.8 0 0,0 15.9,3.9 L6.9,12.9", false),
			"sidebar" => ("M3.5,5 L20.5,5 L20.5,19 L3.5,19 Z M9.5,5 L9.5,19", false),
			"layout" => ("M3.5,5 L20.5,5 L20.5,19 L3.5,19 Z M3.5,9.5 L20.5,9.5 M14,9.5 L14,19", false),
			// 媒体（填充型）
			"play" => ("M8,5.2 C7.4,4.9 6.8,5.3 6.8,6 L6.8,18 C6.8,18.7 7.4,19.1 8,18.8 L18.6,12.8 C19.2,12.4 19.2,11.6 18.6,11.2 Z", true),
			"pause" => ("M6.5,5 L9.5,5 L9.5,19 L6.5,19 Z M14.5,5 L17.5,5 L17.5,19 L14.5,19 Z", true),
			"previous" => ("M18,5.5 L9,12 L18,18.5 Z M6,5 L6,19", true),
			"next" => ("M6,5.5 L15,12 L6,18.5 Z M18,5 L18,19", true),
			"music" => ("M9,4 L19,2.2 L19,15.5 M9,4 L9,17.5 M9,17.5 A2.8,2.8 0 1,1 9,17.6 M19,15.5 A2.8,2.8 0 1,1 19,15.6", false),
			"repeat" => ("M4.5,8.5 L16.5,8.5 L19.5,11.5 M16.5,5.5 L19.5,8.5 L16.5,11.5 M19.5,15.5 L7.5,15.5 L4.5,12.5 M7.5,9.5 L4.5,12.5 L7.5,15.5", false),
			"shuffle" => ("M3,6.5 L7,6.5 L17,17.5 L21,17.5 M3,17.5 L7,17.5 L10.5,13.8 M13.5,10 L17,6.5 L21,6.5 M18,3.5 L21,6.5 L18,9.5 M18,14.5 L21,17.5 L18,20.5", false),
			"volume" => ("M4,9.5 L8,9.5 L12,6 L12,18 L8,14.5 L4,14.5 Z M15.5,9 A4,4 0 0,1 15.5,15 M18,6.5 A7.5,7.5 0 0,1 18,17.5", false),
			"brightness" => ("M12,8 A4,4 0 1,0 12,16 A4,4 0 1,0 12,8 M12,2.5 L12,4.5 M12,19.5 L12,21.5 M2.5,12 L4.5,12 M19.5,12 L21.5,12 M5.3,5.3 L6.7,6.7 M17.3,17.3 L18.7,18.7 M5.3,18.7 L6.7,17.3 M17.3,6.7 L18.7,5.3", false),
			"bluetooth" => ("M12,3 L12,21 M12,3 L17,8 L7,16 M12,21 L17,16 L7,8", false),
			_ => ("M5,4 L19,4 L19,20 L5,20 Z M8.5,2 L8.5,6 M15.5,2 L15.5,6", false),
		};
	}
}
