using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace IconBox;

// 读取 Windows 主任务栏（Shell_TrayWnd）的实时几何，用于让 Dock 跟随任务栏自动隐藏升降。
// 关键事实（Win11 实测）：任务栏自动隐藏时窗口并不会「缩短」，而是整体向屏幕外平移——
//   隐藏态：Top 被推到≈屏幕底边（屏幕内只剩 1~2px 触发边），Bottom 仍>屏幕底；
//   弹出态：Top 回到屏幕底上方，整条 48px 可见。
// 因此不能用 Bottom-Top（恒为完整高度），必须用「任务栏矩形与所在显示器矩形的竖直交集」。
public static class TaskbarState
{
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	private struct MONITORINFO
	{
		public uint cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
	}

	private const uint MONITOR_DEFAULTTONEAREST = 2;

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string name);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

	public static IntPtr Handle()
	{
		return FindWindow("Shell_TrayWnd", null);
	}

	// 主任务栏当前「可见高度」（与 GetWindowRect 同坐标系的像素）。隐藏时≈1~2px，弹出时为完整高度。
	// 用任务栏矩形与所在显示器矩形的竖直交集衡量——两者都由 user32 在同一 DPI 虚拟化上下文返回，坐标一致。
	public static int VisibleHeight()
	{
		IntPtr h = Handle();
		if (h == IntPtr.Zero || !IsWindowVisible(h))
		{
			return 0;
		}
		if (!GetWindowRect(h, out RECT r))
		{
			return 0;
		}
		int limit = r.Bottom;
		IntPtr mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
		if (mon != IntPtr.Zero)
		{
			MONITORINFO mi = new MONITORINFO
			{
				cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO))
			};
			if (GetMonitorInfo(mon, ref mi))
			{
				limit = mi.rcMonitor.Bottom;
			}
		}
		int visible = Math.Min(r.Bottom, limit) - r.Top;
		return Math.Max(0, visible);
	}

	// 任务栏是否处于「弹出态」：可见高度达到阈值（自动隐藏时隐藏态≈1~2px，弹出态≈完整高度）。
	public static bool IsRevealed(double screenBottomDip, double dpiScale)
	{
		int h = VisibleHeight();
		int threshold = (int)Math.Max(6.0, 12.0 * dpiScale);
		return h >= threshold;
	}

	// 任务栏所在显示器的垂直底边（物理像素，与 VisibleHeight 同一 GetMonitorInfo 坐标系）。
	// 用于 Dock「跟随任务栏」时的绝对定位参照，避免与 WorkArea（可能被 DPI 虚拟化/工作区缓存影响）不一致。
	public static int ScreenBottomPx()
	{
		return MonitorBottomPx(Handle());
	}

	// 以给定窗口（通常传 Dock 自身句柄）所在显示器底边为准（物理像素），
	// 避免多显示器时误取到别的任务栏/显示器。
	public static int ScreenBottomPxFor(IntPtr window)
	{
		return MonitorBottomPx(window);
	}

	private static int MonitorBottomPx(IntPtr window)
	{
		IntPtr mon = (window != IntPtr.Zero) ? MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
		if (mon != IntPtr.Zero)
		{
			MONITORINFO mi = new MONITORINFO
			{
				cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO))
			};
			if (GetMonitorInfo(mon, ref mi))
			{
				return mi.rcMonitor.Bottom;
			}
		}
		return 0;
	}
}
