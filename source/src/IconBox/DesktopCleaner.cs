using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace IconBox;

// 自动清理桌面：把「已经被面板收录」的桌面图标从桌面隐藏，让桌面保持干净。
//
// 关键约定：
//  1. 只改文件属性，绝不删除、移动或改变文件内容。
//     隐藏时同时设置 HIDDEN + SYSTEM 两个位：只有 HIDDEN 时，Explorer 一旦开了
//     「显示隐藏的项目」图标就只是变灰；加上 SYSTEM 后 Windows 会把它当成
//     「受保护的操作系统文件」，在默认设置下彻底不显示（和 desktop.ini 一样）。
//     恢复时把这两个位一起去掉（我们从不处理本来就带 SYSTEM 的文件，所以安全）。
//  2. 只处理桌面目录里的顶层条目（不递归子文件夹），并且跳过：
//     - 已经隐藏的（说明是我们或用户设置过）
//     - 系统 / 隐藏属性的
//     - desktop.ini
//     - 「此电脑 / 回收站 / 网络 / 控制面板」这类快捷方式（面板里当作内置入口）
//  3. 面板里的条目按「全路径」比对，快捷方式解析出的目标路径也算命中，
//     这样同一个程序无论收录的是 .lnk 还是 exe 都能认出来。
//  4. 关掉开关时把本次隐藏过的条目原样恢复（去掉 HIDDEN + SYSTEM），
//     启动时若开关是开的则不重复恢复，避免一开一关反复刷 Explorer。
public static class DesktopCleaner
{
	// 记录本次运行里被我们隐藏过的路径，关闭开关时只恢复这些。
	private static readonly List<string> HiddenByUs = new List<string>();

	private const string HideListFile = "desktop-clean-hidden.txt";

	public static int HiddenCount
	{
		get
		{
			lock (HiddenByUs)
			{
				return HiddenByUs.Count;
			}
		}
	}

	// 最近一次 Sync 恢复显示的条目数，供设置页状态提示使用。
	public static int LastSyncRestored { get; private set; }

	private static string HideListPath
	{
		get
		{
			return Path.Combine(Store.Folder, HideListFile);
		}
	}

	// 从 %LOCALAPPDATA%\IconBox 恢复上次运行隐藏的记录（进程重启后仍能一键还原）。
	public static void LoadHistory()
	{
		try
		{
			if (!File.Exists(HideListPath))
			{
				return;
			}
			lock (HiddenByUs)
			{
				HiddenByUs.Clear();
				foreach (string line in File.ReadAllLines(HideListPath))
				{
					string text = line.Trim();
					if (text.Length != 0)
					{
						HiddenByUs.Add(text);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private static void SaveHistory()
	{
		try
		{
			Directory.CreateDirectory(Store.Folder);
			string[] lines;
			lock (HiddenByUs)
			{
				lines = HiddenByUs.ToArray();
			}
			File.WriteAllLines(HideListPath, lines);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private static string[] DesktopDirs()
	{
		string[] array = new string[2]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
		};
		return array.Where((string d) => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	// 面板收录判断用的小写路径集合。
	private static HashSet<string> PanelPaths(IEnumerable<Entry> apps)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (apps == null)
		{
			return hashSet;
		}
		foreach (Entry app in apps)
		{
			if (app == null)
			{
				continue;
			}
			AddPath(hashSet, app.Path);
			AddPath(hashSet, app.LaunchTarget);
		}
		return hashSet;
	}

	private static void AddPath(HashSet<string> set, string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		string text = path.Trim().Trim('"');
		if (text.Length == 0 || text.Contains("://") || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		try
		{
			set.Add(Path.GetFullPath(text));
		}
		catch
		{
			set.Add(text);
		}
	}

	private static bool Excluded(string name, string path)
	{
		if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		// 回收站等：桌面上的系统文件夹快捷方式不参与清理
		try
		{
			FileAttributes attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.System) != 0)
			{
				return true;
			}
		}
		catch
		{
			return true;
		}
		return false;
	}

	private static bool IsHidden(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
		}
		catch
		{
			return false;
		}
	}

	// 老版本只加过 HIDDEN 位，这些条目需要补上 SYSTEM 位才能真正消失。
	private static bool NeedsSystemUpgrade(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.System) == 0;
		}
		catch
		{
			return false;
		}
	}

	// 隐藏 = HIDDEN + SYSTEM；恢复 = 同时去掉这两位。
	// 因为我们跳过本来就带 SYSTEM 的文件，所以恢复时清掉 SYSTEM 不会误伤别人的标记。
	private static bool SetHidden(string path, bool hidden)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(path);
			FileAttributes fileAttributes = (hidden ? (attributes | FileAttributes.Hidden | FileAttributes.System) : (attributes & ~(FileAttributes.Hidden | FileAttributes.System)));
			if (fileAttributes == attributes)
			{
				return false;
			}
			File.SetAttributes(path, fileAttributes);
			NotifyShell(path);
			return true;
		}
		catch (UnauthorizedAccessException ex)
		{
			// 公共桌面（C:\Users\Public\Desktop）等位置的快捷方式需要管理员权限才能改属性。
			// 这类条目直接跳过，不要每次启动都往 error.log 里刷异常。
			if (!deniedLogged)
			{
				deniedLogged = true;
				Store.Trace("DESKTOP clean skip denied path=" + path + " err=" + ex.Message);
			}
			return false;
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return false;
		}
	}

	private static bool deniedLogged;

	// 公共桌面里的快捷方式归全机所有，改属性需要管理员权限：直接不处理。
	private static bool SharedDesktop(string path)
	{
		try
		{
			string folder = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
			if (string.IsNullOrEmpty(folder))
			{
				return false;
			}
			return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	// 属性改完必须通知 Explorer，否则桌面视图会继续显示旧图标（或只把它画成灰色）。
	private static void NotifyShell(string path)
	{
		try
		{
			SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, path, IntPtr.Zero);
		}
		catch
		{
		}
	}

	private static void NotifyShellDir(string dir)
	{
		try
		{
			SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, dir, IntPtr.Zero);
		}
		catch
		{
		}
	}

	private const int SHCNE_UPDATEDIR = 0x1000;

	private const int SHCNE_UPDATEITEM = 0x2000;

	private const uint SHCNF_PATHW = 5u;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

	// 执行一次清理：把桌面上已被面板收录、尚未隐藏的条目设为隐藏。
	// 返回本次新隐藏的条目数。
	public static int Apply(IEnumerable<Entry> apps)
	{
		HashSet<string> hashSet = PanelPaths(apps);
		if (hashSet.Count == 0)
		{
			return 0;
		}
		int num = 0;
		foreach (string dir in DesktopDirs())
		{
			string[] entries;
			try
			{
				entries = Directory.GetFileSystemEntries(dir);
			}
			catch (Exception ex)
			{
				Store.Log(ex);
				continue;
			}
			foreach (string path in entries)
			{
				string fileName = Path.GetFileName(path);
				if (Excluded(fileName, path) || SharedDesktop(path))
				{
					continue;
				}
				string fullPath;
				try
				{
					fullPath = Path.GetFullPath(path);
				}
				catch
				{
					continue;
				}
				if (IsHidden(path))
				{
					// 旧版本隐藏过的条目只有 HIDDEN 位，Explorer 开着「显示隐藏的项目」时只是变灰。
					// 只要它还在面板里，就补上 SYSTEM 位（并记进历史，方便以后恢复）。
					if (hashSet.Contains(fullPath) && NeedsSystemUpgrade(path) && SetHidden(path, hidden: true))
					{
						num++;
						lock (HiddenByUs)
						{
							if (!HiddenByUs.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
							{
								HiddenByUs.Add(fullPath);
							}
						}
						Store.Trace("DESKTOP clean upgrade " + fileName);
					}
					continue;
				}
				if (hashSet.Contains(fullPath) && SetHidden(path, hidden: true))
				{
					num++;
					lock (HiddenByUs)
					{
						if (!HiddenByUs.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
						{
							HiddenByUs.Add(fullPath);
						}
					}
					Store.Trace("DESKTOP clean hide " + fileName);
				}
			}
		}
		if (num > 0)
		{
			SaveHistory();
			foreach (string dir in DesktopDirs())
			{
				NotifyShellDir(dir);
			}
		}
		return num;
	}

	// 同步一次：隐藏「已收录但还露在桌面」的图标，同时把「已从面板移出」的图标恢复显示。
	// 面板增删图标后由 Controller 防抖调用，让桌面始终跟面板内容一致。
	public static int Sync(IEnumerable<Entry> apps)
	{
		int hidden = Apply(apps);
		int restored = RestoreOrphans(PanelPaths(apps));
		LastSyncRestored = restored;
		if (hidden > 0 || restored > 0)
		{
			Store.Trace("DESKTOP clean sync hide=" + hidden + " restore=" + restored);
		}
		return hidden;
	}

	// 只恢复「我们隐藏过、但现在已经不在面板里」的条目；用户自己隐藏的不动。
	private static int RestoreOrphans(HashSet<string> panel)
	{
		List<string> orphans;
		lock (HiddenByUs)
		{
			orphans = HiddenByUs.Where((string p) => !panel.Contains(p)).ToList();
			foreach (string orphan in orphans)
			{
				HiddenByUs.Remove(orphan);
			}
		}
		if (orphans.Count == 0)
		{
			return 0;
		}
		int num = 0;
		foreach (string path in orphans)
		{
			if ((File.Exists(path) || Directory.Exists(path)) && SetHidden(path, hidden: false))
			{
				num++;
			}
		}
		SaveHistory();
		if (num > 0)
		{
			foreach (string dir in DesktopDirs())
			{
				NotifyShellDir(dir);
			}
		}
		return num;
	}

	// 恢复：把本次（含之前的会话）隐藏过的条目重新设为可见。
	// 返回恢复的条目数。
	public static int Restore()
	{
		List<string> list;
		lock (HiddenByUs)
		{
			list = HiddenByUs.ToList();
			HiddenByUs.Clear();
		}
		int num = 0;
		foreach (string path in list)
		{
			if ((File.Exists(path) || Directory.Exists(path)) && SetHidden(path, hidden: false))
			{
				num++;
			}
		}
		SaveHistory();
		if (num > 0)
		{
			Store.Trace("DESKTOP clean restore n=" + num);
			foreach (string dir in DesktopDirs())
			{
				NotifyShellDir(dir);
			}
		}
		return num;
	}

	// 恢复桌面上所有被本功能隐藏过的图标（不止历史文件里记着的那些）。
	// desktop-clean-hidden.txt 可能被清理或丢失，只按历史恢复会让用户以为「按钮没反应」，
	// 所以再兜底扫一遍桌面：同时带 HIDDEN + SYSTEM 的条目基本只可能是我们隐藏的
	// （清理时跳过本来就带 SYSTEM 的文件，恢复时不会碰到别人的标记）。
	public static int RestoreAll(IEnumerable<Entry> apps)
	{
		HashSet<string> panel = PanelPaths(apps);
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		lock (HiddenByUs)
		{
			foreach (string known in HiddenByUs)
			{
				hashSet.Add(known);
			}
			HiddenByUs.Clear();
		}
		foreach (string dir in DesktopDirs())
		{
			string[] entries;
			try
			{
				entries = Directory.GetFileSystemEntries(dir);
			}
			catch (Exception ex)
			{
				Store.Log(ex);
				continue;
			}
			foreach (string candidate in entries)
			{
				try
				{
					string fileName = Path.GetFileName(candidate);
					// desktop.ini / thumbs.db 是系统自己隐藏的文件，恢复它们只会在桌面上多出一个没用的文件。
					if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "thumbs.db", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					string full = Path.GetFullPath(candidate);
					// 面板里收录的软件，它的桌面快捷方式也要恢复（旧版本可能只加过 Hidden 位）。
					bool panelOwned = panel.Contains(full);
					if (SharedDesktop(candidate) && !panelOwned)
					{
						continue;
					}
					FileAttributes attributes = File.GetAttributes(candidate);
					// 桌面上的快捷方式（.lnk/.url）只要被隐藏就恢复：用户不会刻意把快捷方式设成隐藏。
					bool shortcut = fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
					bool dropped = ((attributes & FileAttributes.Hidden) != 0) && (panelOwned || shortcut || (attributes & FileAttributes.System) != 0);
					if (!dropped)
					{
						continue;
					}
					hashSet.Add(full);
				}
				catch (Exception ex2)
				{
					Store.Log(ex2);
				}
			}
		}
		int num = 0;
		int num2 = 0;
		foreach (string path in hashSet)
		{
			if (!File.Exists(path) && !Directory.Exists(path))
			{
				continue;
			}
			bool flag = panel.Contains(path);
			if (SetHidden(path, hidden: false))
			{
				num++;
				if (flag)
				{
					num2++;
				}
			}
		}
		// 历史文件已经清空，直接删掉，避免下次启动又把旧路径读回来。
		try
		{
			if (File.Exists(HideListPath))
			{
				File.Delete(HideListPath);
			}
		}
		catch
		{
		}
		foreach (string dir2 in DesktopDirs())
		{
			NotifyShellDir(dir2);
		}
		Store.Trace("DESKTOP restore all n=" + num + " panel=" + num2);
		return num;
	}
}
