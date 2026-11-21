using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconBox;

public static class GlassMaterials
{
	private static ImageSource grain;

	public static Brush Fill(MaterialOptions options, bool light, double percent, int accent = 0)
	{
		if (percent <= 0.0)
		{
			return Brushes.Transparent;
		}
		Color color;
		if (string.IsNullOrEmpty(options.Tint))
		{
			// 深色玻璃底色：原先 (24,26,32) 偏黑，用户反馈 Dock 毛玻璃「太黑」。
			// 苹果的深色玻璃是「中性灰 + 一点环境色」，不是黑：底色提亮到 (52,56,66)，
			// 再掺 16% 强调色，透出来的墙纸色会更接近 macOS 的深色材质而不是墨块。
			color = (light ? Color.FromRgb(234, 237, 244) : Color.FromRgb(52, 56, 66));
			// "跟随强调色"：把强调色以 16% 掺入玻璃底色（与菜单同源思路），切换强调色时玻璃可见变化
			Color accentColor = IconPacks.AccentColor(accent);
			color = Color.FromRgb((byte)((double)color.R + ((double)(int)accentColor.R - (double)color.R) * 0.16), (byte)((double)color.G + ((double)(int)accentColor.G - (double)color.G) * 0.16), (byte)((double)color.B + ((double)(int)accentColor.B - (double)color.B) * 0.16));
		}
		else
		{
			color = (Color)ColorConverter.ConvertFromString(options.Tint);
		}
		color.A = (byte)Math.Round(percent * 255.0 / 100.0);
		if (options.Kind != 3 && options.Kind != 1)
		{
			// 纯色玻璃也带一点自上而下的透光（顶部略亮、底部回到本色），
			// 苹果的玻璃「看起来有厚度」靠的正是这点极小的竖向亮度差，而不是加白边。
			if (color.A < 8)
			{
				return new SolidColorBrush(color);
			}
			Color topColor = color;
			int lift = light ? 6 : 10;
			topColor.R = (byte)Math.Min(255, topColor.R + lift);
			topColor.G = (byte)Math.Min(255, topColor.G + lift);
			topColor.B = (byte)Math.Min(255, topColor.B + lift);
			LinearGradientBrush sheen = new LinearGradientBrush(topColor, color, 90.0);
			sheen.Freeze();
			return sheen;
		}
		Color startColor = color;
		startColor.R = (byte)Math.Min(255, startColor.R + 35);
		startColor.G = (byte)Math.Min(255, startColor.G + 35);
		startColor.B = (byte)Math.Min(255, startColor.B + 35);
		Color endColor = color;
		if (options.Kind == 1)
		{
			startColor.A = (byte)((double)(int)color.A * 0.65);
			endColor.A = (byte)((double)(int)color.A * 0.25);
		}
		return new LinearGradientBrush(startColor, endColor, 65.0);
	}

	public static ImageSource Grain()
	{
		if (grain != null)
		{
			return grain;
		}
		byte[] array = new byte[4096];
		uint num = 912367u;
		for (int i = 0; i < array.Length; i += 4)
		{
			num = num * 1664525 + 1013904223;
			byte b = (byte)(num >> 24);
			array[i] = (array[i + 1] = (array[i + 2] = b));
			array[i + 3] = 60;
		}
		BitmapSource bitmapSource = BitmapSource.Create(32, 32, 96.0, 96.0, PixelFormats.Bgra32, null, array, 128);
		bitmapSource.Freeze();
		grain = bitmapSource;
		return bitmapSource;
	}
}
