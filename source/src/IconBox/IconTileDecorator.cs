using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconBox
{
	// 磁贴图标底板装饰器（最小工具）：
	// 统一提供「苹果圆角底板」的圆角比例与底板配色，供 BoardWindow 的
	// FileTypePlaceholder / 磁贴 Border 复用，避免各处魔法数。
	internal static class IconTileDecorator
	{
		// 圆角半径（相对方块边长），约 22.7%，接近 iOS/macOS squircle。
		internal const double RadiusRatio = 0.227;

		private static readonly SolidColorBrush PlateDark;
		private static readonly SolidColorBrush PlateLight;

		static IconTileDecorator()
		{
			// 深色玻璃：半透明珍珠白底（与 Tokens.Dark.Fill 同族，略微提亮让 glyph 更清晰）
			SolidColorBrush dark = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
			dark.Freeze();
			PlateDark = dark;
			// 浅色玻璃：近白实心底
			SolidColorBrush light = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
			light.Freeze();
			PlateLight = light;
		}

		internal static SolidColorBrush PlateBrush(bool light)
		{
			return light ? PlateLight : PlateDark;
		}

		// 判定「几乎没有可辨识内容」的丑图标（典型：cmd.exe 命令行 —— 一块带透明留白的
		// 近乎纯灰/黑的方块 glyph）。判定在「原始图标」上做（不是已加底板的成品），否则
		// 中性底板会把边缘填满、指标失真。
		// 经验标定（对全网格 89+ 图标实测）：
		//   cmd.exe: edge≈0.00 opaque≈0.79 avg≈35 sat≈2
		//   正常深色品牌 logo（Oopz/抖音/MCHOSE/WeGame）edge≥0.5 或 op≈1.0 或彩度高 → 不命中。
		// 规则：四边几乎全透明 + 不透明度中等(0.40~0.87) + 整体偏暗 + 几乎无彩度。
		internal static bool IsBlankIcon(ImageSource source)
		{
			try
			{
				if (source == null)
				{
					return false;
				}
				BitmapSource bs = source as BitmapSource;
				if (bs == null)
				{
					return false;
				}
				if (bs.Format != PixelFormats.Pbgra32)
				{
					bs = new FormatConvertedBitmap(bs, PixelFormats.Pbgra32, null, 0);
				}
				int w = bs.PixelWidth;
				int h = bs.PixelHeight;
				if (w < 8 || h < 8)
				{
					return false;
				}
				int stride = w * 4;
				byte[] px = new byte[stride * h];
				bs.CopyPixels(px, stride, 0);
				long opaque = 0;
				long total = 0;
				long sumLum = 0;
				long sumSat = 0;
				for (int y = 0; y < h; y += 3)
				{
					int row = y * stride;
					for (int x = 0; x < w; x += 3)
					{
						int i = row + x * 4;
						total++;
						if (px[i + 3] > 16)
						{
							opaque++;
							int b = px[i];
							int g = px[i + 1];
							int r = px[i + 2];
							sumLum += (r * 30 + g * 59 + b * 11) / 100;
							int mx = Math.Max(r, Math.Max(g, b));
							int mn = Math.Min(r, Math.Min(g, b));
							if (mx > 0)
							{
								sumSat += ((mx - mn) * 100) / mx;
							}
						}
					}
				}
				if (opaque < total * 0.30)
				{
					// 透明 glyph（只有一小撮像素不透明）由 NormalizePlate 的 glyph 底板处理。
					return false;
				}
				int edgeHit = 0;
				int edgeTot = 0;
				for (int x = 0; x < w; x += 2)
				{
					if (px[x * 4 + 3] > 16) { edgeHit++; }
					if (px[(h - 1) * stride + x * 4 + 3] > 16) { edgeHit++; }
					edgeTot += 2;
				}
				for (int y = 0; y < h; y += 2)
				{
					if (px[y * stride + 3] > 16) { edgeHit++; }
					if (px[y * stride + (w - 1) * 4 + 3] > 16) { edgeHit++; }
					edgeTot += 2;
				}
				double edgeRatio = edgeTot > 0 ? (double)edgeHit / edgeTot : 0.0;
				double opaqueRatio = (double)opaque / Math.Max(1L, total);
				double avgLum = (double)sumLum / Math.Max(1L, opaque);
				double satAvg = (double)sumSat / Math.Max(1L, opaque);
				if (edgeRatio < 0.15 && opaqueRatio >= 0.40 && opaqueRatio <= 0.87 && avgLum < 60.0 && satAvg < 8.0)
				{
					return true;
				}
			}
			catch
			{
			}
			return false;
		}
	}
}
