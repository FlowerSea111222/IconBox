using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IconBox;

public static class TaskbarAutoHide
{
	private const string StuckRectsKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StuckRects3";

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct APPBARDATA
	{
		public uint Size;

		public IntPtr Window;

		public uint Message;

		public uint Edge;

		public RECT Rect;

		public IntPtr Param;
	}

	private static string Journal => Path.Combine(Store.Folder, "taskbar-autohide.json");

	public static bool Enabled => (State() & 1) != 0;

	[DllImport("shell32.dll")]
	private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string name);

	private static APPBARDATA Data()
	{
		APPBARDATA result = new APPBARDATA
		{
			Size = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
			Window = FindWindow("Shell_TrayWnd", null)
		};
		if (result.Window == IntPtr.Zero)
		{
			throw new InvalidOperationException("未找到 Windows 任务栏");
		}
		return result;
	}

	private static uint State()
	{
		APPBARDATA data = Data();
		return (uint)SHAppBarMessage(4u, ref data).ToUInt64();
	}

	private static void Apply(uint state)
	{
		// ABM_SETSTATE 必须传 hWnd = NULL：传任务栏句柄时 Windows 会静默忽略整次调用，
		// 状态位不变、任务栏也不动（Win11 实测如此），所以这里不能用 Data() 的句柄。
		APPBARDATA data = new APPBARDATA
		{
			Size = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
			Window = IntPtr.Zero,
			Param = new IntPtr(state)
		};
		SHAppBarMessage(10u, ref data);
		// 校验：个别 Win11 构建上 hWnd=NULL 的 ABM_SETSTATE 会被静默忽略（状态位未变）。
		// 若读回的状态与期望不符，再用任务栏句柄兜底发一次，最大化兼容不同 shell 版本。
		if (((uint)SHAppBarMessage(4u, ref data).ToUInt64() & 1u) != (state & 1u))
		{
			IntPtr tray = FindWindow("Shell_TrayWnd", null);
			if (tray != IntPtr.Zero)
			{
				APPBARDATA fb = new APPBARDATA
				{
					Size = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
					Window = tray,
					Param = new IntPtr(state)
				};
				SHAppBarMessage(10u, ref fb);
			}
		}
	}

	// ABM_SETSTATE 只改 shell 的内存状态，不写注册表；explorer 重启后会按
	// StuckRects3\Settings 第 9 个字节的 0x01 位重新初始化，所以必须同步落盘，
	// 否则重启资源管理器或重启系统后自动隐藏就丢了。
	private static void Persist(bool enabled)
	{
		try
		{
			using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StuckRectsKey, writable: true))
			{
				if (key == null || !(key.GetValue("Settings") is byte[] settings) || settings.Length < 9)
				{
					return;
				}
				byte current = settings[8];
				byte next = (enabled ? (byte)(current | 1) : (byte)(current & 0xFE));
				if (next == current)
				{
					return;
				}
				settings[8] = next;
				key.SetValue("Settings", settings, RegistryValueKind.Binary);
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	public static void Set(bool enabled)
	{
		uint num = State();
		if ((num & 1) != 0 != enabled)
		{
			Apply(FeatureRules.AutoHideState(num, enabled));
		}
		Persist(enabled);
	}

	// 自动隐藏现在是持久化设置（Prefs.AutoHideTaskbar），状态由 Set() 直接落盘到注册表，
	// 不再需要"开启前状态"日志。旧实现在退出时把状态回滚成开启前的值，导致用户每次
	// 关掉程序设置就丢失（开启后退出会被改回关闭，关闭后退出又会被改回开启）。
	// 这里只清理历史遗留的日志文件，不改动任务栏状态；真正要关闭请用 ShowAndClear()。
	public static void Restore()
	{
		try
		{
			if (File.Exists(Journal))
			{
				File.Delete(Journal);
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	public static void ShowAndClear()
	{
		Apply(FeatureRules.AutoHideState(State(), enabled: false));
		Persist(enabled: false);
		if (File.Exists(Journal))
		{
			File.Delete(Journal);
		}
	}
}
