using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IconBox;

/// <summary>
/// 前台窗口类型：Dock 的可见性不再只看「有没有被当成全屏抑制」，而是按类型分档决策
/// （问卷 dock-taskbar-rules-questionnaire.md 的 3.1 / 3.2 / 3.3 / 3.4 与 Q7.x）。
/// </summary>
internal enum ForegroundKind
{
	Desktop,
	Normal,
	Maximized,
	Fullscreen,
	SystemOverlay
}

internal static class ForegroundRules
{
	[DllImport("user32.dll")]
	private static extern bool IsZoomed(IntPtr window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, StringBuilder text, int length);

	/// <summary>
	/// 判定当前前台窗口属于哪一档。monitor* 是 Dock 所在显示器的物理像素矩形。
	/// </summary>
	public static ForegroundKind Classify(int monitorLeft, int monitorTop, int monitorRight, int monitorBottom)
	{
		try
		{
			IntPtr window = Native.GetForegroundWindow();
			if (window == IntPtr.Zero)
			{
				return ForegroundKind.Normal;
			}
			if (DesktopWindowPolicy.DesktopForeground())
			{
				return ForegroundKind.Desktop;
			}
			string name = ClassName(window);
			// 通知中心 / 快速设置 / 任务视图：Dock 让开（Q7.5、Q7.6）。
			if (name == "XamlExplorerHostIslandWindow" || name == "MultitaskingViewFrame" || name == "TaskSwitcherWnd" || name == "Windows.UI.Core.CoreWindow")
			{
				return ForegroundKind.SystemOverlay;
			}
			if (!Native.GetWindowRect(window, out Native.RECT rect))
			{
				return ForegroundKind.Normal;
			}
			// 先判「最大化」：Win11 自动隐藏任务栏时，最大化窗口的矩形同样铺满整个屏幕，
			// 若先按矩形判全屏，就会把普通的最大化窗口当成全屏 → 问卷 3.1（T2 与任务栏同步）失效。
			if (IsZoomed(window))
			{
				return ForegroundKind.Maximized;
			}
			if (rect.Left <= monitorLeft + 2 && rect.Top <= monitorTop + 2 && rect.Right >= monitorRight - 2 && rect.Bottom >= monitorBottom - 2)
			{
				return ForegroundKind.Fullscreen;
			}
			// 无边框的「贴满屏幕」窗口（Win11 自动隐藏任务栏时，最大化窗口底边就是屏幕底边）。
			if (rect.Bottom >= monitorBottom - 2 && rect.Top > monitorTop + 8)
			{
				return ForegroundKind.Maximized;
			}
			return ForegroundKind.Normal;
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return ForegroundKind.Normal;
		}
	}

	private static string ClassName(IntPtr window)
	{
		StringBuilder text = new StringBuilder(256);
		return (GetClassName(window, text, text.Capacity) > 0) ? text.ToString() : string.Empty;
	}
}
