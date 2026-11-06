using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IconBox;

// Windows 11 的「透明效果」开关（设置 → 个性化 → 颜色）：开启时任务栏是亚克力半透明、
// 会透出壁纸颜色，关闭后任务栏变为不透明纯色。HKCU\...\Themes\Personalize 下的
// EnableTransparency 是 Windows 唯一受支持的任务栏透明开关，改完必须广播
// WM_SETTINGCHANGE("ImmersiveColorSet") 才会立即重绘（实测无需重启资源管理器）。
//
// 备注：SetWindowCompositionAttribute 的 ACCENT_* 策略在 Win11 22H2+ 的任务栏上不再产生
// 真实透明（实测把 ACCENT_ENABLE_TRANSPARENTGRADIENT / ACRYLIC / BLUR 全部试过，任务栏
// 只会被刷成纯色，壁纸完全透不出来），所以这里不走那条路。
public static class TaskbarTransparency
{
	private const string KeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";

	private const string ValueName = "EnableTransparency";

	private const uint WM_SETTINGCHANGE = 0x001A;

	private const uint WM_THEMECHANGED = 0x031A;

	private const uint SMTO_ABORTIFHUNG = 0x0002;

	private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

	public static bool Enabled
	{
		get
		{
			try
			{
				using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
				{
					object value = key?.GetValue(ValueName);
					if (value is int num)
					{
						return num != 0;
					}
					// 值不存在时按 Windows 默认（透明效果开启）处理。
					return true;
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
				return true;
			}
		}
	}

	public static bool Set(bool enabled)
	{
		try
		{
			using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
			{
				if (key == null)
				{
					return false;
				}
				key.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return false;
		}
		Notify();
		return Enabled == enabled;
	}

	// ImmersiveColorSet 让 shell 重新读取颜色与透明设置；WM_THEMECHANGED 兜底触发主题重绘。
	// 每次广播上限 300ms：个别挂起窗口由 SMTO_ABORTIFHUNG 直接跳过，不会拖住 UI 线程。
	private static void Notify()
	{
		try
		{
			IntPtr result;
			SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 300u, out result);
			SendMessageTimeout(HWND_BROADCAST, WM_THEMECHANGED, IntPtr.Zero, "", SMTO_ABORTIFHUNG, 300u, out result);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}
}
