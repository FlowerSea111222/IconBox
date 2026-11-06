using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconBox
{
	// 图标视觉统一器（v2 —— 苹果圆角底板）：
	// Normalize 把任意来源图标统一到一块「圆角方形底板（squircle）」上：
	//   · 透明底 glyph（只有 alpha 图形）→ 置入中性淡色圆角底板，glyph 放大到 ~64% 画布居中，
	//     视觉大小与全彩方形图标一致，不再「偏小飘忽」；
	//   · 正方形全出血图标（自带背景块，如 FlowUs/Command）→ 透明底 + 裁到 ~84% 画布，
	//     由磁贴层的 Border+Clip 提供统一底板；
	//   · 宽高不一的自由图标（部分子部件 / 宽图）→ 透明底 + 内接裁到 84% 画布居中。
	// 结果用 DrawingImage 矢量合成（不走 RenderTargetBitmap 栅格化），缩放由渲染管线 GPU 完成。
	// 过程只依赖一次 CopyPixels 字节数组，结果按源引用缓存 + 冻结，多磁贴共享同一对象。
	internal static class IconNormalizer
	{
		private const int CanvasSize = 128;
		// 全彩/方形图标在画布内占的比例（磁贴 Border 提供底板与内边距）
		private const double PlateFill = 0.90;
		// 透明 glyph 放大后占画布的比例（底色由 glyph 版画进去，故可直接铺满）
		private const double GlyphFill = 0.62;
		// 圆角半径（相对画布边长）：约 22.7%，接近 iOS/macOS squircle 的观感
		private const double RadiusRatio = 0.227;
		// 透明 glyph 底板配色（深色玻璃：深色半透明；浅色玻璃：白色实心）——与 Tokens.Surface 同族
		private static readonly System.Windows.Media.Color GlyphPlateDark = System.Windows.Media.Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF);
		private static readonly System.Windows.Media.Color GlyphPlateLight = System.Windows.Media.Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF);
		private const byte EdgeStrokeAlpha = 0x18; // 1px 浅描边（白色极淡），统一玻璃层上的轮廓

		// 原始源 → 统一图形（按引用缓存，图标源本身有全局缓存，引用稳定）
		private static readonly Dictionary<ImageSource, ImageSource> glyphCache = new Dictionary<ImageSource, ImageSource>();
		// 底板配色会随主题/毛玻璃变化，所以以 (源引用, 深浅) 为键
		private static readonly Dictionary<string, ImageSource> tileCache = new Dictionary<string, ImageSource>();

		private static readonly object glyphLock = new object();

		// Dock / 菜单 / 列表用：只规范大小，不加底板（保持原透明外观）。
		internal static ImageSource Normalize(ImageSource source)
		{
			if (source == null)
			{
				return null;
			}
			lock (glyphLock)
			{
				if (glyphCache.TryGetValue(source, out ImageSource cached))
				{
					return cached;
				}
			}
			ImageSource result = DoGlyph(source);
			lock (glyphLock)
			{
				if (glyphCache.ContainsKey(source))
				{
					result = glyphCache[source];
				}
				else
				{
					glyphCache.Add(source, result);
					Prune();
				}
			}
			return result;
		}

		// 磁贴用：把图标落到统一的苹果圆角底板上。
		//   source         —— 原始图标（通常 256px）；
		//   light          —— 主题深浅（决定中性底板配色）；
		//   isOpaquePlate  —— 已知是自带底色的方形全彩图标（如 FileTypePlaceholder 画出的卡片），
		//                     跳过 glyph/全出血推断，直接做透明圆角方形，避免被误上色。
		internal static ImageSource NormalizePlate(ImageSource source, bool light, bool isOpaquePlate)
		{
			if (source == null)
			{
				return null;
			}
			// 深色主题下白底 glyph 版底板要压暗一些，浅色主题下用近白
			string key = (light ? "L" : "D") + (isOpaquePlate ? "/p" : "/a");
			Dictionary<string, ImageSource> perSource = null;
			lock (glyphLock)
			{
				// tileCache 以父源引用 -> 子字典（key 是配色/模式）的方式二级缓存
				if (!tileSub.TryGetValue(source, out perSource))
				{
					perSource = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
					tileSub.Add(source, perSource);
				}
				if (perSource.TryGetValue(key, out ImageSource hit))
				{
					return hit;
				}
			}
			ImageSource result = DoPlate(source, light, isOpaquePlate);
			lock (glyphLock)
			{
				if (!tileSub.TryGetValue(source, out perSource))
				{
					perSource = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
					tileSub.Add(source, perSource);
				}
				if (!perSource.ContainsKey(key))
				{
					perSource.Add(key, result);
				}
				else
				{
					result = perSource[key];
				}
			}
			return result;
		}

		private static readonly Dictionary<ImageSource, Dictionary<string, ImageSource>> tileSub = new Dictionary<ImageSource, Dictionary<string, ImageSource>>();

		private static void Prune()
		{
			if (glyphCache.Count > 600)
			{
				int drop = glyphCache.Count - 500;
				foreach (KeyValuePair<ImageSource, ImageSource> pair in glyphCache)
				{
					glyphCache.Remove(pair.Key);
					if (--drop <= 0)
					{
						break;
					}
				}
			}
			if (tileSub.Count > 500)
			{
				int drop2 = tileSub.Count - 420;
				foreach (KeyValuePair<ImageSource, Dictionary<string, ImageSource>> pair2 in tileSub)
				{
					tileSub.Remove(pair2.Key);
					if (--drop2 <= 0)
					{
						break;
					}
				}
			}
		}

		// ── 共用：把任意 ImageSource 转成 Pbgra32 位图 + 像素数组 ────────────────────
		private static BitmapSource ToPbgra(ImageSource source, out byte[] pixels, out int w, out int h)
		{
			BitmapSource src;
			if (source is BitmapSource bitmap)
			{
				src = bitmap;
			}
			else
			{
				// 矢量图标（DrawingImage 等）先栅格化到 256×256 再走统一流程
				RenderTargetBitmap raster = new RenderTargetBitmap(256, 256, 96.0, 96.0, PixelFormats.Pbgra32);
				DrawingVisual visual = new DrawingVisual();
				using (DrawingContext dc = visual.RenderOpen())
				{
					dc.DrawImage(source, new Rect(0.0, 0.0, 256.0, 256.0));
				}
				raster.Render(visual);
				src = raster;
			}
			if (src.Format != PixelFormats.Pbgra32)
			{
				src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
			}
			w = src.PixelWidth;
			h = src.PixelHeight;
			int stride = w * 4;
			pixels = new byte[stride * h];
			src.CopyPixels(pixels, stride, 0);
			return src;
		}

		// 扫描透明边界（隔 2 采样，误差 ±1px 视觉不可感知）
		private static bool ContentBounds(byte[] pixels, int w, int h, out int minX, out int minY, out int maxX, out int maxY)
		{
			int stride = w * 4;
			minX = w;
			minY = h;
			maxX = -1;
			maxY = -1;
			for (int y = 0; y < h; y += 2)
			{
				int row = y * stride;
				for (int x = 0; x < w; x += 2)
				{
					if (pixels[row + x * 4 + 3] > 0)
					{
						if (x < minX) { minX = x; }
						if (x > maxX) { maxX = x; }
						if (y < minY) { minY = y; }
						if (y > maxY) { maxY = y; }
					}
				}
			}
			return maxX >= minX && maxY >= minY;
		}

		// 透明边界 + 中性色空白边扫描。
		// 部分软件导出的图标把图形画在一块纯白/纯灰的实心方块中间，方块四周留一圈与角点同色的
		// 空白；这种图 alpha 全不透明，只按透明裁边去不掉，磁贴上就是「图标外圈一圈空白」。
		// 这里额外把「与角点同色且是中性色（R≈G≈B）」的不透明像素当作可裁掉的空白，
		// 彩色背景（如整块蓝色方块）不受影响，避免把真实底板裁掉。
		private static bool ContentBoundsTrimmed(byte[] pixels, int w, int h, out int minX, out int minY, out int maxX, out int maxY)
		{
			int stride = w * 4;
			int[] corners = new int[4]
			{
				0,
				(w - 1) * 4,
				(h - 1) * stride,
				(h - 1) * stride + (w - 1) * 4
			};
			int cr = -1;
			int cg = -1;
			int cb = -1;
			foreach (int idx in corners)
			{
				if (pixels[idx + 3] > 200)
				{
					cb = pixels[idx];
					cg = pixels[idx + 1];
					cr = pixels[idx + 2];
					break;
				}
			}
			bool neutral = cr >= 0 && Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb)) <= 12;
			if (!ContentBounds(pixels, w, h, out minX, out minY, out maxX, out maxY))
			{
				return false;
			}
			if (!neutral)
			{
				return true;
			}
			int alphaW = maxX - minX + 1;
			int alphaH = maxY - minY + 1;
			int tMinX = maxX;
			int tMinY = maxY;
			int tMaxX = minX;
			int tMaxY = minY;
			for (int y = minY; y <= maxY; y++)
			{
				int row = y * stride;
				for (int x = minX; x <= maxX; x++)
				{
					int i = row + x * 4;
					byte alpha = pixels[i + 3];
					if (alpha <= 16)
					{
						continue;
					}
					if (alpha > 200 && Math.Abs(pixels[i + 2] - cr) <= 10 && Math.Abs(pixels[i + 1] - cg) <= 10 && Math.Abs(pixels[i] - cb) <= 10)
					{
						continue;
					}
					if (x < tMinX) { tMinX = x; }
					if (x > tMaxX) { tMaxX = x; }
					if (y < tMinY) { tMinY = y; }
					if (y > tMaxY) { tMaxY = y; }
				}
			}
			// 保护：裁掉的量超过 40% 时不做中性色裁剪——那说明这块中性色底板本身就是图标设计的一部分
			// （例如白底圆角方块 + 彩色标记），继续裁会把底板裁掉、观感反而变差。
			if (tMaxX < tMinX || tMaxY < tMinY)
			{
				return true;
			}
			int trimW = tMaxX - tMinX + 1;
			int trimH = tMaxY - tMinY + 1;
			if ((double)trimW < (double)alphaW * 0.6 || (double)trimH < (double)alphaH * 0.6)
			{
				return true;
			}
			minX = tMinX;
			minY = tMinY;
			maxX = tMaxX;
			maxY = tMaxY;
			return true;
		}

		// 统计最外圈 alpha 占用率：> 0.85 视为「全出血方块」（自带背景，透明底即可），
		// < 0.35 视为「透明的 glyph/自由形状」（需要放到中性底板上升权）。
		private static double OuterEdgeFillRatio(byte[] pixels, int w, int h)
		{
			int stride = w * 4;
			int hit = 0;
			int total = 0;
			// 上/下两行
			for (int x = 0; x < w; x += 2)
			{
				if (pixels[x * 4 + 3] > 16) { hit++; }
				int bottom = (h - 1) * stride + x * 4 + 3;
				if (pixels[bottom] > 16) { hit++; }
				total += 2;
			}
			// 左/右两列
			for (int y = 0; y < h; y += 2)
			{
				if (pixels[y * stride + 3] > 16) { hit++; }
				if (pixels[y * stride + (w - 1) * 4 + 3] > 16) { hit++; }
				total += 2;
			}
			return (total == 0) ? 0.0 : ((double)hit / total);
		}

		// ── Dock 模式：只裁边距 + 缩放到画布内，透明底（不加底板）──────────────────
		private static ImageSource DoGlyph(ImageSource source)
		{
			try
			{
				byte[] pixels;
				int w;
				int h;
				BitmapSource pbgra = ToPbgra(source, out pixels, out w, out h);
				int minX, minY, maxX, maxY;
				if (!ContentBoundsTrimmed(pixels, w, h, out minX, out minY, out maxX, out maxY))
				{
					return source;
				}
				Int32Rect bounds = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
				BitmapSource content = new CroppedBitmap(pbgra, bounds);
				double scale = Math.Min(CanvasSize * 0.88 / bounds.Width, CanvasSize * 0.88 / bounds.Height);
				double dw = bounds.Width * scale;
				double dh = bounds.Height * scale;
				DrawingImage result = new DrawingImage(new ImageDrawing(content,
					new Rect((CanvasSize - dw) / 2.0, (CanvasSize - dh) / 2.0, dw, dh)));
				result.Freeze();
				return result;
			}
			catch
			{
				return source;
			}
		}

		// 生成圆角矩形几何（squircle 观感：用较多点的圆角，半径 ~22.7%）
		private static StreamGeometry SquircleRect(double c, double r)
		{
			StreamGeometry geo = new StreamGeometry();
			using (StreamGeometryContext ctx = geo.Open())
			{
				ctx.BeginFigure(new Point(r, 0.0), true, true);
				ctx.LineTo(new Point(c - r, 0.0), true, false);
				ctx.ArcTo(new Point(c, r), new Size(r, r), 0.0, false, SweepDirection.Clockwise, true, false);
				ctx.LineTo(new Point(c, c - r), true, false);
				ctx.ArcTo(new Point(c - r, c), new Size(r, r), 0.0, false, SweepDirection.Clockwise, true, false);
				ctx.LineTo(new Point(r, c), true, false);
				ctx.ArcTo(new Point(0.0, c - r), new Size(r, r), 0.0, false, SweepDirection.Clockwise, true, false);
				ctx.LineTo(new Point(0.0, r), true, false);
				ctx.ArcTo(new Point(r, 0.0), new Size(r, r), 0.0, false, SweepDirection.Clockwise, true, false);
			}
			geo.Freeze();
			return geo;
		}

		// ── 磁贴模式：统一到圆角底板 ─────────────────────────────────────────────
		private static ImageSource DoPlate(ImageSource source, bool light, bool isOpaquePlate)
		{
			try
			{
				byte[] pixels;
				int w;
				int h;
				BitmapSource pbgra = ToPbgra(source, out pixels, out w, out h);
				int minX, minY, maxX, maxY;
				bool hasContent = ContentBoundsTrimmed(pixels, w, h, out minX, out minY, out maxX, out maxY);
				if (!hasContent)
				{
					// 完全空图：画一个中性底板占位，避免再出现「空方块」
					return DrawGlyphPlate(null, light);
				}

				double edge = OuterEdgeFillRatio(pixels, w, h);
				bool opaquePlate = isOpaquePlate || edge >= 0.85;

				Int32Rect bounds = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
				BitmapSource content = new CroppedBitmap(pbgra, bounds);

				if (opaquePlate)
				{
					// 自带背景的方形全彩图标：透明圆角方形（磁贴层 Border+Clip 负责底板），占 84% 画布
					double side = CanvasSize * PlateFill;
					double scale = Math.Min(side / bounds.Width, side / bounds.Height);
					double dw = bounds.Width * scale;
					double dh = bounds.Height * scale;
					return DrawContent(content, dw, dh);
				}

				if (edge <= 0.35)
				{
					// 透明 glyph：置入中性圆角底板，glyph 放大到 ~62% 画布居中
					return DrawGlyphPlate(content, light);
				}

				// 介于两者之间（自由形状、宽图）：透明底 + 内接 84% 居中
				double side2 = CanvasSize * PlateFill;
				double scale2 = Math.Min(side2 / bounds.Width, side2 / bounds.Height);
				return DrawContent(content, bounds.Width * scale2, bounds.Height * scale2);
			}
			catch
			{
				return source;
			}
		}

		// 透明底 + 居中内容（方形全彩/自由形状用）
		private static ImageSource DrawContent(BitmapSource content, double dw, double dh)
		{
			double x = (CanvasSize - dw) / 2.0;
			double y = (CanvasSize - dh) / 2.0;
			DrawingGroup group = new DrawingGroup();
			// 极淡的白描边（贴玻璃层上做统一轮廓，几乎看不出，但让边缘更「结实」）
			using (DrawingContext dc = group.Open())
			{
				dc.DrawImage(content, new Rect(x, y, dw, dh));
			}
			DrawingImage result = new DrawingImage(group);
			result.Freeze();
			return result;
		}

		// 圆角底板 + glyph（透明 glyph 用）
		private static ImageSource DrawGlyphPlate(BitmapSource content, bool light)
		{
			double r = CanvasSize * RadiusRatio;
			StreamGeometry rect = SquircleRect(CanvasSize, r);
			System.Windows.Media.Color fill = light ? GlyphPlateLight : GlyphPlateDark;
			DrawingGroup group = new DrawingGroup();
			using (DrawingContext dc = group.Open())
			{
				// 底板
				dc.DrawGeometry(new SolidColorBrush(fill), null, rect);
				// 1px 浅描边
				Pen stroke = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(EdgeStrokeAlpha, 0xFF, 0xFF, 0xFF)), 1.0);
				stroke.Freeze();
				dc.DrawGeometry(null, stroke, rect);
				// glyph
				if (content != null)
				{
					double side = CanvasSize * GlyphFill;
					double scale = Math.Min(side / content.PixelWidth, side / content.PixelHeight);
					double dw = content.PixelWidth * scale;
					double dh = content.PixelHeight * scale;
					dc.DrawImage(content, new Rect((CanvasSize - dw) / 2.0, (CanvasSize - dh) / 2.0, dw, dh));
				}
			}
			DrawingImage result = new DrawingImage(group);
			result.Freeze();
			return result;
		}
	}
}
