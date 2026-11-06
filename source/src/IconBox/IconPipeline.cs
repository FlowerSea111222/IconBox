using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace IconBox;

// 图标后台加载管线：
// 把「磁盘缓存读取 / PNG 解码 / Shell 图标提取 / 缩略图提取 / SVG 光栅化 / 磁盘缓存写入」
// 全部移出 UI 线程。UI 线程只做内存缓存命中检查（FeatureUi.Cached，纯字典查询）；
// 未命中时立即返回 null（调用方先渲染占位符），由单后台 STA 线程串行提取
// （串行避免 Shell COM 并发问题），完成后把已冻结的 ImageSource 派发回 UI 线程回调换图。
// 启动预热（Controller.WarmIconCache）同样走本管线，不再用空闲定时器占用 UI 线程。
public static class IconPipeline
{
	private sealed class IconJob
	{
		public Entry Entry;
		public bool Overlays;
		public bool Tile;                  // true：磁贴管线 MediaThumb.TileIcon；false：Dock 管线 FeatureUi.Icon
		public bool Light;                 // 磁贴底板配色（浅色主题=true）
		public Action<ImageSource> Callback;
		public bool DoneSentinel;          // 预热完成哨兵（无图标工作，只回写完成日志）
		public int WarmTotal;
	}

	private static readonly BlockingCollection<IconJob> queue = new BlockingCollection<IconJob>();
	private static readonly Thread worker;
	private static Dispatcher ui;

	static IconPipeline()
	{
		worker = new Thread(Work)
		{
			IsBackground = true,
			Name = "IconPipeline"
		};
		worker.SetApartmentState(ApartmentState.STA);
		worker.Start();
	}

	// 请求图标：内存缓存命中 → 同步返回（不触发回调）；未命中 → 返回 null 并后台提取，
	// 完成后在 UI 线程回调。必须在 UI 线程调用。
	public static ImageSource Request(Entry entry, bool overlays, bool sharper, bool tile, Action<ImageSource> onReady, bool light = false)
	{
		if (entry == null)
		{
			return null;
		}
		if (ui == null)
		{
			ui = Dispatcher.CurrentDispatcher;
		}
		ImageSource hit = FeatureUi.Cached(entry, overlays, sharper);
		if (hit != null)
		{
			// Dock 命中即返回；磁贴命中需要走底板（Cached 返回的是 Dock 规范化结果，无底板）
			if (!tile)
			{
				return hit;
			}
			// 命中缓存的是「已归一化」图标，像素统计特征已失真；这里另取原始图标做
			// 丑图标判定（命中缓存时原始图标也已在 Native 内存缓存里，取用极廉）。
			// 命中返回 null 交由调用方铺占位卡片（避免黑块）。
			if (IconTileDecorator.IsBlankIcon(FeatureUi.Icon(entry, overlays, sharper, normalize: false)))
			{
				return null;
			}
			return IconNormalizer.NormalizePlate(hit, light, false);
		}
		queue.Add(new IconJob
		{
			Entry = entry,
			Overlays = overlays,
			Tile = tile,
			Light = light,
			Callback = onReady
		});
		return null;
	}

	// 启动预热：整批入队（无回调），后台逐个提取填满内存 + 磁盘缓存
	public static void Warm(List<Entry> entries, bool overlays, bool light = false)
	{
		if (ui == null)
		{
			ui = Dispatcher.CurrentDispatcher;
		}
		if (entries == null)
		{
			return;
		}
		int n = 0;
		foreach (Entry e in entries)
		{
			if (e == null)
			{
				continue;
			}
			n++;
			queue.Add(new IconJob
			{
				Entry = e,
				Overlays = overlays,
				Tile = true,
				Light = light
			});
		}
		queue.Add(new IconJob
		{
			DoneSentinel = true,
			WarmTotal = n
		});
	}

	private static void Work()
	{
		foreach (IconJob req in queue.GetConsumingEnumerable())
		{
			if (req.DoneSentinel)
			{
				Store.Trace("WARM icons done n=" + req.WarmTotal);
				continue;
			}
			ImageSource result = null;
			try
			{
				result = (req.Tile ? MediaThumb.TileIcon(req.Entry, req.Overlays, req.Light) : FeatureUi.Icon(req.Entry, req.Overlays, true));
			}
			catch
			{
			}
			Action<ImageSource> callback = req.Callback;
			if (callback != null)
			{
				Dispatcher dispatcher = ui;
				if (dispatcher != null)
				{
					try
					{
						dispatcher.BeginInvoke((Action)(() => callback(result)), DispatcherPriority.Background);
					}
					catch
					{
					}
				}
			}
		}
	}
}
