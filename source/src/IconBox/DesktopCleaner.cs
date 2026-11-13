using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

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

	// 面板里每个入口真正指向的目标程序（用于按“目标相同”识别桌面上的快捷方式）。
	private static HashSet<string> PanelTargets(IEnumerable<Entry> apps)
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
			string text = (app.LaunchTarget ?? string.Empty).Trim().Trim('\"');
			if (text.Length == 0)
			{
				string[] array = ScanDedupRules.Resolve((app.Path ?? string.Empty).Trim());
				text = ((array != null) ? (array[0] ?? string.Empty) : string.Empty).Trim();
			}
			AddTarget(hashSet, text);
		}
		return hashSet;
	}

	private static void AddTarget(HashSet<string> set, string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		string text = path.Trim().Trim('\"');
		if (text.Length == 0 || text.Contains("://") || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("::{", StringComparison.Ordinal))
		{
			return;
		}
		try
		{
			set.Add(WindowActivationRules.NormalizePath(Path.GetFullPath(text)));
		}
		catch
		{
			set.Add(WindowActivationRules.NormalizePath(text));
		}
	}

	// 桌面条目是否属于面板：路径直接命中，或它是个快捷方式且目标命中。
	private static bool InPanel(HashSet<string> paths, HashSet<string> targets, string path, string fullPath)
	{
		if (paths.Contains(fullPath))
		{
			return true;
		}
		string[] array = ScanDedupRules.Resolve(path);
		if (array == null || string.IsNullOrWhiteSpace(array[0]))
		{
			return false;
		}
		string text = array[0].Trim().Trim('\"');
		string item;
		try
		{
			item = WindowActivationRules.NormalizePath(Path.GetFullPath(text));
		}
		catch
		{
			item = WindowActivationRules.NormalizePath(text);
		}
		return targets.Contains(item);
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

	private static bool InHistory(string fullPath)
	{
		lock (HiddenByUs)
		{
			return HiddenByUs.Contains(fullPath, StringComparer.OrdinalIgnoreCase);
		}
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
	public static int Apply(IEnumerable<Entry> apps, bool hideFiles = false)
	{
		HashSet<string> hashSet = PanelPaths(apps);
		HashSet<string> hashSet2 = PanelTargets(apps);
		bool computer = HasShellIcon(apps, "::{20D04FE0-3AEA-1069-A2D9-08002B30309D}");
		bool recycle = HasShellIcon(apps, "::{645FF040-5081-101B-9F08-00AA002F954E}");
		if (hashSet.Count == 0 && hashSet2.Count == 0 && !computer && !recycle)
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
				string fullPath;
				try
				{
					fullPath = Path.GetFullPath(path);
				}
				catch
				{
					continue;
				}
				// 「隐藏 + 系统」的条目会被 Excluded 挡掉；但那是我们自己标上去的，
				// 得放进来才能按新规则把文档 / 文件夹恢复显示（问题 2）。
				bool inHistory = InHistory(fullPath);
				if (Excluded(fileName, path) && !inHistory && (hideFiles || fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				bool owned = InPanel(hashSet, hashSet2, path, fullPath) || IsDriveShortcut(path);
				// 默认只收起快捷方式。文档 / 文件夹被标成「隐藏 + 系统」后，
				// 浏览器的上传对话框会默认把它们过滤掉——用户就选不到桌面上的文件了。
				bool isShortcutFile = !Directory.Exists(path) && (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
				if (owned && !hideFiles && !isShortcutFile)
				{
					// 旧版本把文档也标过隐藏：顺手恢复回来，免得继续在文件对话框里「消失」。
					if (IsHidden(path) && SetHidden(path, hidden: false))
					{
						Store.Trace("DESKTOP clean unhide file " + fileName);
					}
					continue;
				}
				// 公共桌面上的快捷方式一般要管理员权限才能改属性；面板里的软件仍然尝试一次，失败由 SetHidden 自己吞掉。
				if (SharedDesktop(path) && !owned)
				{
					continue;
				}
				if (IsHidden(path))
				{
					// 旧版本隐藏过的条目只有 HIDDEN 位，Explorer 开着「显示隐藏的项目」时只是变灰。
					// 只要它还在面板里，就补上 SYSTEM 位（并记进历史，方便以后恢复）。
					if (owned && NeedsSystemUpgrade(path) && SetHidden(path, hidden: true))
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
				if (owned && SetHidden(path, hidden: true))
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
		// 此电脑 / 回收站不是文件，是 Explorer 的「桌面图标」，只能改注册表开关。
		if (computer && HideShellIcon("::{20D04FE0-3AEA-1069-A2D9-08002B30309D}", hidden: true))
		{
			num++;
		}
		if (recycle && HideShellIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", hidden: true))
		{
			num++;
		}
		if (num > 0)
		{
			SaveHistory();
			foreach (string dir2 in DesktopDirs())
			{
				NotifyShellDir(dir2);
			}
			NotifyShellChanged();
		}
		return num;
	}

	// 同步一次：隐藏「已收录但还露在桌面」的图标，同时把「已从面板移出」的图标恢复显示。
	// 面板增删图标后由 Controller 防抖调用，让桌面始终跟面板内容一致。
	public static int Sync(IEnumerable<Entry> apps, bool hideFiles = false)
	{
		int hidden = Apply(apps);
		HashSet<string> panel = PanelPaths(apps);
		HashSet<string> targets = PanelTargets(apps);
		int restored = RestoreOrphans(delegate(string path)
		{
			return panel.Contains(path) || InPanel(panel, targets, path, path) || IsDriveShortcut(path);
		});
		// 面板里已经没有「此电脑 / 回收站」时，把这两个桌面图标放回来。
		if (!HasShellIcon(apps, "::{20D04FE0-3AEA-1069-A2D9-08002B30309D}") && HideShellIcon("::{20D04FE0-3AEA-1069-A2D9-08002B30309D}", hidden: false))
		{
			restored++;
			NotifyShellChanged();
		}
		if (!HasShellIcon(apps, "::{645FF040-5081-101B-9F08-00AA002F954E}") && HideShellIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", hidden: false))
		{
			restored++;
			NotifyShellChanged();
		}
		LastSyncRestored = restored;
		if (hidden > 0 || restored > 0)
		{
			Store.Trace("DESKTOP clean sync hide=" + hidden + " restore=" + restored);
		}
		return hidden;
	}

	// 只恢复「我们隐藏过、但现在已经不在面板里」的条目；用户自己隐藏的不动。
	private static int RestoreOrphans(Func<string, bool> owned)
	{
		List<string> orphans;
		lock (HiddenByUs)
		{
			orphans = HiddenByUs.Where((string p) => !owned(p)).ToList();
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
	// 只恢复「我们隐藏过、但现在已经不在面板里」的条目；用户自己隐藏的不动。
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
		// 顺带把「此电脑 / 回收站」这类被收起的桌面图标放回来。
		bool flag3 = HideShellIcon("::{20D04FE0-3AEA-1069-A2D9-08002B30309D}", hidden: false);
		bool flag4 = HideShellIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", hidden: false);
		if (flag3 || flag4)
		{
			num++;
			NotifyShellChanged();
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

	// 把面板里已经收录、桌面上却还没有的软件补齐成桌面快捷方式。
	// 只处理软件本身：跳过文件夹与 shell: / ::{GUID} / ms- 这类虚拟入口。
	public static int PlaceOnDesktop(IEnumerable<Entry> apps)
	{
		if (apps == null)
		{
			return 0;
		}
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
		{
			return 0;
		}
		HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> existingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[] items;
		try
		{
			items = Directory.GetFileSystemEntries(desktop);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return 0;
		}
		foreach (string item in items)
		{
			existingNames.Add(Path.GetFileNameWithoutExtension(item));
			string[] resolved = ScanDedupRules.Resolve(item);
			if (resolved != null)
			{
				existingTargets.Add(WindowActivationRules.NormalizePath(resolved[0]) + "|" + (resolved[1] ?? string.Empty).Trim());
			}
		}
		int num = 0;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Entry app in apps)
		{
			if (app == null)
			{
				continue;
			}
			string path = (app.Path ?? string.Empty).Trim();
			if (path.Length == 0 || app.IsFolder || Directory.Exists(path))
			{
				continue;
			}
			if (path.Contains("://") || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::{", StringComparison.Ordinal) || path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string full2 = SafeFullPath(path);
			// 本来就躺在桌面目录里的条目不用再放一份。
			if (full2 != null && full2.StartsWith(desktop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string target = ResolveTarget(app);
			// 只把真正的可执行程序放回桌面：文档、网址、shell 虚拟项一律跳过。
			if (target.Length == 0 || !File.Exists(target))
			{
				continue;
			}
			string extension = Path.GetExtension(target);
			if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string key = WindowActivationRules.NormalizePath(target) + "|" + (app.LaunchArguments ?? string.Empty).Trim();
			if (existingTargets.Contains(key) || !hashSet.Add(key))
			{
				continue;
			}
			string name = CleanFileName(string.IsNullOrWhiteSpace(app.Name) ? Path.GetFileNameWithoutExtension(path) : app.Name);
			if (name.Length == 0)
			{
				continue;
			}
			string full = Path.Combine(desktop, name + ".lnk");
			int num2 = 2;
			while (existingNames.Contains(Path.GetFileNameWithoutExtension(full)) || File.Exists(full))
			{
				full = Path.Combine(desktop, name + " (" + num2 + ").lnk");
				num2++;
			}
			if (!CreateShortcut(full, target, app.LaunchArguments, app.IconPath))
			{
				continue;
			}
			existingNames.Add(Path.GetFileNameWithoutExtension(full));
			existingTargets.Add(key);
			num++;
		}
		if (num > 0)
		{
			NotifyShellDir(desktop);
		}
		Store.Trace("DESKTOP place n=" + num);
		return num;
	}

	// 面板里是否收录了「此电脑」/「回收站」这类 shell 桌面图标。
	private static bool HasShellIcon(IEnumerable<Entry> apps, string clsid)
	{
		if (apps == null)
		{
			return false;
		}
		foreach (Entry app in apps)
		{
			if (app == null)
			{
				continue;
			}
			string path = (app.Path ?? string.Empty).Trim();
			string target = (app.LaunchTarget ?? string.Empty).Trim();
			if (path.Equals(clsid, StringComparison.OrdinalIgnoreCase) || target.Equals(clsid, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	// 磁盘根目录的快捷方式（本地磁盘 (F).lnk 之类）：和系统图标一样属于「不属于任何软件」的杂物，一并收起。
	private static bool IsDriveShortcut(string path)
	{
		if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string[] array = ScanDedupRules.Resolve(path);
		if (array == null || string.IsNullOrWhiteSpace(array[0]))
		{
			return false;
		}
		string text = array[0].Trim().Trim('\"').TrimEnd(new char[1] { (char)92 });
		return text.Length == 2 && text[1] == (char)58;
	}

	// 此电脑 / 回收站：写 HKCU 的 HideDesktopIcons 开关，返回是否真的改了。
	private static bool HideShellIcon(string clsid, bool hidden)
	{
		bool flag = false;
		// 注册表里的值名就是 {CLSID}，不能带 shell 命名空间前缀 "::"。
		string text2 = clsid.TrimStart(new char[1] { (char)58 });
		// 面板里的「此电脑」写的是 A2D9（历史笔误），系统真正认的是 A2D8。
		if (text2.Equals("{20D04FE0-3AEA-1069-A2D9-08002B30309D}", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
		}
		string[] array = new string[2] { "NewStartPanel", "ClassicStartMenu" };
		foreach (string text3 in array)
		{
			try
			{
				using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\" + text3))
				{
					if (registryKey == null)
					{
						continue;
					}
					// 旧版本误写过的 "::{...}" 值名顺手清掉。
					try
					{
						registryKey.DeleteValue(clsid, throwOnMissingValue: false);
					}
					catch
					{
					}
					object value = registryKey.GetValue(text2);
					int num = ((value is int) ? ((int)value) : 0);
					int num2 = (hidden ? 1 : 0);
					if (num == num2)
					{
						continue;
					}
					registryKey.SetValue(text2, num2, RegistryValueKind.DWord);
					flag = true;
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
		return flag;
	}

	private static void NotifyShellChanged()
	{
		try
		{
			SHChangeNotify(134217728, 0u, null, IntPtr.Zero);
		}
		catch
		{
		}
	}

	private static string SafeFullPath(string path)
	{
		try
		{
			return Path.GetFullPath(path);
		}
		catch
		{
			return null;
		}
	}

	// 取条目真正要启动的程序：优先 LaunchTarget，其次解 .lnk/.url，最后退回 Path。
	// 公共桌面（C:\Users\Public\Desktop）是所有用户共用的，普通权限改不了属性；
	// 由提权实例（--hide-public-desktop）调用，把已归类的公共快捷方式一起收起。
	public static int HidePublicDesktop(IEnumerable<Entry> apps)
	{
		int num = 0;
		HashSet<string> targets = PanelTargets(apps);
		if (targets.Count == 0)
		{
			return 0;
		}
		string folder = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
		if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
		{
			return 0;
		}
		foreach (string item in Directory.EnumerateFileSystemEntries(folder))
		{
			try
			{
				if (string.Equals(Path.GetFileName(item), "desktop.ini", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				FileAttributes attributes = File.GetAttributes(item);
				if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
				{
					continue;
				}
				string[] array = ScanDedupRules.Resolve(item);
				string text = ((array != null) ? (array[0] ?? string.Empty) : string.Empty).Trim().Trim((char)34);
				if (text.Length == 0)
				{
					continue;
				}
				string text2;
				try
				{
					text2 = WindowActivationRules.NormalizePath(Path.GetFullPath(text));
				}
				catch
				{
					text2 = WindowActivationRules.NormalizePath(text);
				}
				if (!targets.Contains(text2))
				{
					continue;
				}
				if (SetHidden(item, hidden: true))
				{
					num++;
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
		if (num > 0)
		{
			NotifyShellDir(folder);
		}
		Store.Trace("DESKTOP public hide n=" + num);
		return num;
	}

	// 公共桌面里「已经收进面板、但还没隐藏」的快捷方式。
	// 普通权限改不了 C:\Users\Public\Desktop 的属性，同步时只能跳过；这里把它们列出来，
	// 供设置页显示数量、状态栏提示用户点一次提权按钮。
	public static List<string> PendingPublicDesktop(IEnumerable<Entry> apps)
	{
		List<string> list = new List<string>();
		try
		{
			HashSet<string> targets = PanelTargets(apps);
			if (targets.Count == 0)
			{
				return list;
			}
			string folder = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
			if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
			{
				return list;
			}
			foreach (string item in Directory.EnumerateFileSystemEntries(folder))
			{
				try
				{
					string fileName = Path.GetFileName(item);
					if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					if (IsHidden(item))
					{
						continue;
					}
					string[] array = ScanDedupRules.Resolve(item);
					string text = ((array != null) ? (array[0] ?? string.Empty) : string.Empty).Trim().Trim((char)34);
					if (text.Length == 0)
					{
						continue;
					}
					string text2;
					try
					{
						text2 = WindowActivationRules.NormalizePath(Path.GetFullPath(text));
					}
					catch
					{
						text2 = WindowActivationRules.NormalizePath(text);
					}
					if (targets.Contains(text2))
					{
						list.Add(fileName);
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			}
		}
		catch (Exception ex2)
		{
			Store.Log(ex2);
		}
		return list;
	}

	private static string ResolveTarget(Entry app)
	{
		if (!string.IsNullOrWhiteSpace(app.LaunchTarget))
		{
			return app.LaunchTarget.Trim().Trim('\"');
		}
		string path = (app.Path ?? string.Empty).Trim();
		string[] array = ScanDedupRules.Resolve(path);
		if (array != null && !string.IsNullOrWhiteSpace(array[0]))
		{
			return array[0].Trim().Trim('\"');
		}
		return path;
	}

	private static string CleanFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return string.Empty;
		}
		char[] invalid = Path.GetInvalidFileNameChars();
		System.Text.StringBuilder builder = new System.Text.StringBuilder(name.Length);
		foreach (char c in name.Trim())
		{
			builder.Append(Array.IndexOf(invalid, c) >= 0 ? (char)95 : c);
		}
		string text = builder.ToString().Trim().TrimEnd(new char[1] { (char)46 });
		if (text.Length > 80)
		{
			text = text.Substring(0, 80).TrimEnd();
		}
		return text;
	}

	private static bool CreateShortcut(string linkPath, string targetPath, string arguments, string iconPath)
	{
		object shell = null;
		object shortcut = null;
		try
		{
			Type typeFromProgID = Type.GetTypeFromProgID("WScript.Shell");
			if (typeFromProgID == null)
			{
				return false;
			}
			shell = Activator.CreateInstance(typeFromProgID);
			shortcut = typeFromProgID.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[1] { linkPath });
			Type type = shortcut.GetType();
			type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[1] { targetPath });
			if (!string.IsNullOrWhiteSpace(arguments))
			{
				type.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[1] { arguments });
			}
			if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
			{
				type.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[1] { iconPath });
			}
			type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
			return File.Exists(linkPath);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return false;
		}
		finally
		{
			if (shortcut != null && Marshal.IsComObject(shortcut))
			{
				Marshal.ReleaseComObject(shortcut);
			}
			if (shell != null && Marshal.IsComObject(shell))
			{
				Marshal.ReleaseComObject(shell);
			}
		}
	}
}
