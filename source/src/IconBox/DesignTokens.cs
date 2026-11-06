using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace IconBox;

// 设计令牌（单一事实来源）。
// 目标风格：简约、干净、克制的现代桌面观感（参考 Apple HIG 的系统色板与字阶），
// 但保留 IconBox 自己的玻璃材质语言。
//
// 约定：
// · 所有颜色以 Color 常量给出，浅色/深色成对提供；透明度用 alpha 表达，避免半透明叠加失真。
// · 字阶只用 Tokens.Type 里的几档，不要再在窗口里写裸 FontSize。
// · 圆角只用 Tokens.Radius 里的档位（控件 8 / 小卡片 12 / 面板 20 / 窗口 26）。
// · 间距用 4 的倍数（Tokens.Space）。
// · 动效时长与缓动统一走 Tokens.Motion，避免每个窗口各自造数。
public static class Tokens
{
	// ── 系统色板 ────────────────────────────────────────────────────────────
	// 深色（默认）：接近 macOS 深色模式的层次，但压暗一档以配合玻璃底图。
	public static class Dark
	{
		public static readonly Color Label = Color.FromRgb(0xF5, 0xF5, 0xF7);
		public static readonly Color LabelSecondary = Color.FromArgb(0xB0, 0xEB, 0xEB, 0xF0);
		public static readonly Color LabelTertiary = Color.FromArgb(0x66, 0xEB, 0xEB, 0xF0);
		public static readonly Color Separator = Color.FromArgb(0x3D, 0xD5, 0xE3, 0xFF);
		public static readonly Color Fill = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
		public static readonly Color FillStrong = Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF);
		public static readonly Color Surface = Color.FromArgb(0xE0, 0x1E, 0x22, 0x2B);
		public static readonly Color SurfaceRaised = Color.FromArgb(0xF2, 0x26, 0x2B, 0x36);
		public static readonly Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);
		public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
		public static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
		public static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
		public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
		public static readonly Color Teal = Color.FromRgb(0x64, 0xD2, 0xFF);
		public static readonly Color Purple = Color.FromRgb(0xBF, 0x5A, 0xF2);
		public static readonly Color Pink = Color.FromRgb(0xFF, 0x37, 0x5F);
		public static readonly Color Indigo = Color.FromRgb(0x5E, 0x5C, 0xE6);
		public static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);
	}

	// 浅色：白底玻璃，文字用近黑，分隔线保持极轻。
	public static class Light
	{
		public static readonly Color Label = Color.FromRgb(0x1C, 0x1C, 0x1E);
		public static readonly Color LabelSecondary = Color.FromArgb(0xCC, 0x3C, 0x3C, 0x43);
		public static readonly Color LabelTertiary = Color.FromArgb(0x5C, 0x3C, 0x3C, 0x43);
		public static readonly Color Separator = Color.FromArgb(0x33, 0x3C, 0x3C, 0x43);
		public static readonly Color Fill = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
		public static readonly Color FillStrong = Color.FromArgb(0x22, 0x00, 0x00, 0x00);
		public static readonly Color Surface = Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF);
		public static readonly Color SurfaceRaised = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
		public static readonly Color Accent = Color.FromRgb(0x00, 0x7A, 0xFF);
		public static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);
		public static readonly Color Orange = Color.FromRgb(0xFF, 0x95, 0x00);
		public static readonly Color Yellow = Color.FromRgb(0xFF, 0xCC, 0x00);
		public static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59);
		public static readonly Color Teal = Color.FromRgb(0x5A, 0xC8, 0xFA);
		public static readonly Color Purple = Color.FromRgb(0xAF, 0x52, 0xDE);
		public static readonly Color Pink = Color.FromRgb(0xFF, 0x2D, 0x55);
		public static readonly Color Indigo = Color.FromRgb(0x58, 0x56, 0xD6);
		public static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);
	}

	// ── 字阶 ────────────────────────────────────────────────────────────────
	public static class Type
	{
		// Win11 有 Segoe UI Variable；Win10 回退 Segoe UI；中文统一走雅黑 UI。
		public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI Semibold, Segoe UI, Microsoft YaHei UI");
		public static readonly FontFamily Text = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

		public const double LargeTitle = 26.0;
		public const double Title1 = 20.0;
		public const double Title2 = 15.0;
		public const double Headline = 13.0;
		public const double Body = 13.0;
		public const double Callout = 12.0;
		public const double Subhead = 11.0;
		public const double Caption = 10.0;

		public static readonly FontWeight Regular = FontWeights.Normal;
		public static readonly FontWeight Medium = FontWeights.Medium;
		public static readonly FontWeight SemiBold = FontWeights.SemiBold;
	}

	// ── 圆角 ────────────────────────────────────────────────────────────────
	public static class Radius
	{
		public const double Tiny = 5.0;
		public const double Small = 8.0;
		public const double Control = 9.0;
		public const double Card = 12.0;
		public const double Group = 16.0;
		public const double Panel = 20.0;
		public const double Window = 26.0;
	}

	// ── 间距（4 的倍数） ────────────────────────────────────────────────────
	public static class Space
	{
		public const double Xxs = 2.0;
		public const double Xs = 4.0;
		public const double Sm = 8.0;
		public const double Md = 12.0;
		public const double Lg = 16.0;
		public const double Xl = 20.0;
		public const double Xxl = 24.0;
		public const double Xxxl = 32.0;
	}

	// ── 动效 ────────────────────────────────────────────────────────────────
	public static class Motion
	{
		public const double Instant = 90.0;
		public const double Fast = 140.0;
		public const double Normal = 200.0;
		public const double Slow = 280.0;
		public const double Panel = 340.0;

		// 接近 Apple 的“减速进入”曲线：起步快、收尾稳。
		public static IEasingFunction Out => new CubicEase { EasingMode = EasingMode.EaseOut };
		public static IEasingFunction InOut => new CubicEase { EasingMode = EasingMode.EaseInOut };
		public static IEasingFunction In => new CubicEase { EasingMode = EasingMode.EaseIn };
	}

	// ── 阴影 ────────────────────────────────────────────────────────────────
	// 统一使用柔和、低不透明度的投影；窗口用大半径浅投影，卡片用小半径。
	public static DropShadowEffect WindowShadow()
	{
		DropShadowEffect effect = new DropShadowEffect
		{
			BlurRadius = 46.0,
			ShadowDepth = 14.0,
			Direction = 270.0,
			Opacity = 0.22,
			Color = Colors.Black,
			RenderingBias = RenderingBias.Performance
		};
		effect.Freeze();
		return effect;
	}

	public static DropShadowEffect CardShadow()
	{
		DropShadowEffect effect = new DropShadowEffect
		{
			BlurRadius = 18.0,
			ShadowDepth = 4.0,
			Direction = 270.0,
			Opacity = 0.16,
			Color = Colors.Black,
			RenderingBias = RenderingBias.Performance
		};
		effect.Freeze();
		return effect;
	}

	public static DropShadowEffect FloatingShadow()
	{
		DropShadowEffect effect = new DropShadowEffect
		{
			BlurRadius = 30.0,
			ShadowDepth = 8.0,
			Direction = 270.0,
			Opacity = 0.20,
			Color = Colors.Black,
			RenderingBias = RenderingBias.Performance
		};
		effect.Freeze();
		return effect;
	}

	// ── 画刷缓存 ────────────────────────────────────────────────────────────
	private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new Dictionary<Color, SolidColorBrush>(64);

	public static SolidColorBrush Brush(Color color)
	{
		if (BrushCache.TryGetValue(color, out SolidColorBrush cached))
		{
			return cached;
		}
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		BrushCache[color] = brush;
		return brush;
	}

	// ── 语义取色（按深浅主题返回同一语义的画刷） ───────────────────────────
	public static Brush Surface(bool light) => Brush(light ? Light.Surface : Dark.Surface);
	public static Brush SurfaceRaised(bool light) => Brush(light ? Light.SurfaceRaised : Dark.SurfaceRaised);
	public static Brush Label(bool light) => Brush(light ? Light.Label : Dark.Label);
	public static Brush LabelSecondary(bool light) => Brush(light ? Light.LabelSecondary : Dark.LabelSecondary);
	public static Brush LabelTertiary(bool light) => Brush(light ? Light.LabelTertiary : Dark.LabelTertiary);
	public static Brush Separator(bool light) => Brush(light ? Light.Separator : Dark.Separator);
	public static Brush Fill(bool light) => Brush(light ? Light.Fill : Dark.Fill);
	public static Brush FillStrong(bool light) => Brush(light ? Light.FillStrong : Dark.FillStrong);
	public static Brush AccentBrush(bool light) => Brush(light ? Light.Accent : Dark.Accent);
	public static Color AccentColor(bool light) => light ? Light.Accent : Dark.Accent;
	public static Brush Danger(bool light) => Brush(light ? Light.Red : Dark.Red);

	// 强调色按索引取色（与 IconPacks 的 6 档强调色索引保持一致）。
	// 已重新对齐到系统色：蓝 / 靛 / 青 / 橙 / 粉 / 灰。
	public static Color AccentAt(int index, bool light)
	{
		return index switch
		{
			1 => light ? Light.Purple : Dark.Purple,
			2 => light ? Light.Green : Dark.Green,
			3 => light ? Light.Orange : Dark.Orange,
			4 => light ? Light.Pink : Dark.Pink,
			5 => light ? Light.Gray : Dark.Gray,
			_ => light ? Light.Accent : Dark.Accent,
		};
	}

	// 在强调色上叠一层低透明度填充，用于“次要按钮”的底色（Apple 的 tinted button）。
	public static Color Tint(Color accent, double alpha)
	{
		return Color.FromArgb((byte)Math.Round(Math.Max(0.0, Math.Min(1.0, alpha)) * 255.0), accent.R, accent.G, accent.B);
	}

	// 依据背景明度自动选前景色（用于彩色底上的文字/图标，保证可读性）。
	public static Color OnColor(Color background)
	{
		double luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0;
		return (luminance > 0.62) ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White;
	}

	// WCAG 相对亮度对比度，供自测断言使用。
	public static double Contrast(Color a, Color b)
	{
		double la = RelativeLuminance(a);
		double lb = RelativeLuminance(b);
		double hi = Math.Max(la, lb);
		double lo = Math.Min(la, lb);
		return (hi + 0.05) / (lo + 0.05);
	}

	private static double RelativeLuminance(Color c)
	{
		static double Channel(byte v)
		{
			double s = v / 255.0;
			return (s <= 0.03928) ? (s / 12.92) : Math.Pow((s + 0.055) / 1.055, 2.4);
		}
		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}
}
