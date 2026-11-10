using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;

namespace IconBox;

public static class DesktopWindowPolicy
{
	[DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
	private static extern int GetWindowLong(IntPtr window, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
	private static extern int SetWindowLong(IntPtr window, int index, int value);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

	public static int ToolStyle(int style)
	{
		// WS_EX_TOOLWINDOW(0x80) 会被 Explorer「显示桌面」当作工具窗口隐藏（Win+D 后 Dock 消失的真因）；
		// 改用 WS_EX_NOACTIVATE(0x08000000)：同样不抢焦点、不进 Alt+Tab 焦点循环，但不会被显示桌面隐藏。
		return (style | 0x08000000) & -262145 & ~0x80;
	}

	public static void Apply(Window window)
	{
		try
		{
			IntPtr handle = new WindowInteropHelper(window).Handle;
			SetWindowLong(handle, -20, ToolStyle(GetWindowLong(handle, -20)));
			if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 55u))
			{
				Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "刷新桌面窗口类型失败"));
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// DWM 对「工具窗口/桌面窗口」的合成状态有缓存：启动时即使已是 NOACTIVATE 非分层，
	// 首次 Show 后 DWM 仍可能不合成（画面上是白/透明）。用「翻转 ex-style + SetWindowPos」
	// 强制 DWM 重新求值该窗口的合成状态，Dock 即会稳定上屏。开销极小（仅两次 SetWindowLong）。
	public static void ForceRecomposite(IntPtr handle)
	{
		if (handle == IntPtr.Zero) return;
		try
		{
			int ex = GetWindowLong(handle, -20);
			// 先临时加回 TOOLWINDOW（DWM 记录一次“隐藏”事件），再还原为正确样式，
			// 触发一次完整的可见性重算——这是让分层/桌面窗口在 Win+D 后重新上屏的关键。
			SetWindowLong(handle, -20, ex | 0x80);
			SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x37u);
			SetWindowLong(handle, -20, ex);
			SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x37u);
		}
		catch (Exception ex2)
		{
			Store.Log(ex2);
		}
	}

	public static Rect WorkArea(Window window, bool pointer = false, bool fullScreen = false)
	{
		try
		{
			IntPtr handle = new WindowInteropHelper(window).Handle;
			Screen screen;
			if (pointer)
			{
				screen = Screen.FromPoint(Cursor.Position);
			}
			else if (handle != IntPtr.Zero)
			{
				screen = Screen.FromHandle(handle);
			}
			else if (!double.IsNaN(window.Left) && !double.IsNaN(window.Top))
			{
				PresentationSource presentationSource = PresentationSource.FromVisual(window);
				System.Windows.Point point = ((presentationSource != null && presentationSource.CompositionTarget != null) ? presentationSource.CompositionTarget.TransformToDevice : Matrix.Identity).Transform(new System.Windows.Point(window.Left, window.Top));
				screen = Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
			}
			else
			{
				screen = Screen.PrimaryScreen;
			}
			Rectangle rectangle = (fullScreen ? screen.Bounds : screen.WorkingArea);
			PresentationSource presentationSource2 = PresentationSource.FromVisual(window);
			Matrix matrix = ((presentationSource2 != null && presentationSource2.CompositionTarget != null) ? presentationSource2.CompositionTarget.TransformFromDevice : Matrix.Identity);
			if (presentationSource2 == null)
			{
				using Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);
				matrix = new Matrix(96.0 / (double)graphics.DpiX, 0.0, 0.0, 96.0 / (double)graphics.DpiY, 0.0, 0.0);
			}
			System.Windows.Point point2 = matrix.Transform(new System.Windows.Point(rectangle.Left, rectangle.Top));
			System.Windows.Point point3 = matrix.Transform(new System.Windows.Point(rectangle.Right, rectangle.Bottom));
			return new Rect(point2, point3);
		}
		catch
		{
			return SystemParameters.WorkArea;
		}
	}

	// —— 显示桌面（Win+D）后的回归置顶 ——
	// Explorer 在「显示桌面」时把桌面窗口 Progman/WorkerW 提到最前，并把 Dock 这类
	// 工具窗口排到它后面。Dock 自身 Topmost=false（避免盖住全屏应用），因此不能只靠
	// 窗口样式解决：前台变回桌面时，把它重新排到「桌面窗口」之上，同时在视觉上保持
	// 非最顶层（紧接着桌面窗口，仍会被普通应用/全屏游戏盖住）。
	private static readonly IntPtr HwndTop = new IntPtr(0);

	private static readonly IntPtr HwndTopMost = new IntPtr(-1);

	private const uint SwpNoSize = 0x0001u;

	private const uint SwpNoMove = 0x0002u;

	private const uint SwpNoActivate = 0x0010u;

	private const uint SwpNoOwnerZOrder = 0x0200u;

	private const uint SwpNoSendChanging = 0x0400u;

	private const uint SwpFlags = SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow; // 0x53 = NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW

	private const uint SwpShowWindow = 0x0040u;

	private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, System.Text.StringBuilder buffer, int size);

	[DllImport("user32.dll")]
	private static extern IntPtr GetShellWindow();

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr window);


[DllImport("user32.dll")]
private static extern bool ShowWindow(IntPtr window, int command);

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT { public int Left, Top, Right, Bottom; }

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr window, out RECT rect);

	private static string ClassNameOf(IntPtr window)
	{
		System.Text.StringBuilder builder = new System.Text.StringBuilder(256);
		GetClassName(window, builder, builder.Capacity);
		return builder.ToString();
	}

	private static bool IsDesktopClass(string name)
	{
		return string.Equals(name, "Progman", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "WorkerW", StringComparison.OrdinalIgnoreCase);
	}

	// 前台是否是「桌面」本身（单击桌面 / Win+D 后 Progman 成为前台窗口）。
	// 单击桌面时焦点常在 WorkerW（承载壁纸/SHELLDLL_DefView 的宿主），同样视作桌面。
	public static bool DesktopForeground()
	{
		try
		{
			IntPtr foregroundWindow = GetForegroundWindow();
			if (foregroundWindow == IntPtr.Zero)
			{
				return false;
			}
			return IsDesktopClass(ClassNameOf(foregroundWindow));
		}
		catch
		{
			return false;
		}
	}

	// 找出桌面窗口（Progman，或承载壁纸的可见 WorkerW）作为置顶参照物。
	// 找不到时退回桌面左上角，等价于「压在壁纸之上」。
	private static IntPtr DesktopAnchor()
	{
		try
		{
			IntPtr shell = GetShellWindow();
			if (shell != IntPtr.Zero && IsDesktopClass(ClassNameOf(shell)))
			{
				return shell;
			}
			IntPtr anchor = IntPtr.Zero;
			EnumWindows(delegate(IntPtr window, IntPtr state)
			{
				if (anchor == IntPtr.Zero && IsWindowVisible(window) && string.Equals(ClassNameOf(window), "WorkerW", StringComparison.OrdinalIgnoreCase))
				{
					anchor = window;
				}
				return true;
			}, IntPtr.Zero);
			return anchor;
		}
		catch
		{
			return IntPtr.Zero;
		}
	}

	// 把窗口压到桌面之上：仅将其提为 TOPMOST（不改尺寸位置、不激活）。
	// 注意：绝不能 SetWindowPos 到 Progman 之后——实测会让窗口被排到桌面下方而不可见。
	// 全屏应用遮挡问题由桌面守卫的「仅桌面在前台才抬升」门控避免。
	public static bool LiftAboveDesktop(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			// 关键：只做「保持在 TOPMOST 带 + 不改尺寸位置」的最小动作。
			// 绝不 SetWindowPos 到 Progman 之后——那会把它排到桌面之下而不可见（实测 vis 立刻 False）。
			// 也绝不在此 Sleep 重试——跑在 UI 线程会冻结应用；多拍重试由 GlassWindow 的 DispatcherTimer 负责。
			SetWindowPos(window, HwndTopMost, 0, 0, 0, 0, SwpFlags | SwpShowWindow);
			return true;
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return false;
		}
	}


	// HWND 层真实可见性（不受 WPF 内部 IsVisible 状态干扰）。
	public static bool IsWindowShown(IntPtr window)
	{
		return window != IntPtr.Zero && IsWindowVisible(window);
	}

	// 让被「显示桌面」隐藏的窗口重新可见、不激活不抢焦点。
	// 关键：必须用 SW_SHOW(5)/SW_RESTORE(9)，SW_SHOWNOACTIVATE(4) 对已被 DWM 隐藏的窗口返回 False 且仍不可见（实测）。
	// 依次尝试 SW_SHOW(5) 与 SW_RESTORE(9)（9 在最小化/隐藏态恢复最可靠）。
	public static bool ShowNoActivate(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return false;
		}
		bool any = ShowWindow(window, 9);
		if (!IsWindowVisible(window))
		{
			any |= ShowWindow(window, 5);
		}
		return any || IsWindowVisible(window);
	}

	[DllImport("user32.dll")]
	private static extern IntPtr WindowFromPoint(POINT pt);

	[StructLayout(LayoutKind.Sequential)]
	public struct POINT { public int X; public int Y; }

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	// 判断屏幕上一个点（物理像素）是否「空着」：命中桌面/任务栏/我们自己的窗口都算空，
	// 命中别的应用的顶层窗口才算被占用。Dock 用它决定能否在桌面状态下常驻显示。
	public static bool PointIsFree(IntPtr own, int x, int y)
	{
		try
		{
			POINT pt = new POINT { X = x, Y = y };
			IntPtr hit = WindowFromPoint(pt);
			if (hit == IntPtr.Zero || hit == own)
			{
				return true;
			}
			string cls = ClassNameOf(hit);
			if (IsDesktopClass(cls) || string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) || string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			// 桌面的图标层是 Progman/WorkerW 的子窗口（SysListView32 "FolderView"），
			// WindowFromPoint 命中的是子窗口，所以要顺着祖先链再看一次，
			// 否则桌面上只要有图标就会被判成「被别的窗口占住」，Dock 永远不敢显示。
			IntPtr root = GetAncestor(hit, 2u);
			if (root != IntPtr.Zero && root != hit && IsDesktopClass(ClassNameOf(root)))
			{
				return true;
			}
			uint hitPid;
			GetWindowThreadProcessId(hit, out hitPid);
			uint ownPid;
			GetWindowThreadProcessId(own, out ownPid);
			return hitPid != 0u && hitPid == ownPid;
		}
		catch
		{
			return false;
		}
	}

	// 判断窗口中心点是否被别的顶层窗口盖住（用于验证「显示桌面」后是否真的看得见）。
	public static bool IsCoveredByOtherWindow(IntPtr window)
	{
		if (window == IntPtr.Zero)
		{
			return true;
		}
		RECT rect;
		if (!GetWindowRect(window, out rect))
		{
			return true;
		}
		POINT pt = new POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
		IntPtr hit = WindowFromPoint(pt);
		if (hit == window)
		{
			return false;
		}
		return IsCoveredWalk(hit, window, 0);
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetAncestor(IntPtr window, uint flags);

	private static bool IsCoveredWalk(IntPtr hit, IntPtr window, int depth)
	{
		IntPtr current = hit;
		for (int i = 0; i < 8 && current != IntPtr.Zero; i++)
		{
			if (current == window)
			{
				return false;
			}
			current = GetAncestor(current, 1u);
		}
		return true;
	}
}
