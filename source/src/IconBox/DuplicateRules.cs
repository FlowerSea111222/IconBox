using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IconBox;

// 图标整理：找出指向同一款软件的多余入口（同一可执行文件 + 同一启动参数）。
// 「文件」分类里的桌面文件、文件夹与 Shell 特殊项（此电脑 / 回收站等）不参与整理：
// 它们的路径本身就是身份，不存在"同一款软件装了两遍"这种重复。
public static class DuplicateRules
{
	// 只表示"快捷方式从哪来"的启动参数，不改变启动行为，比较时忽略。
	private static readonly Regex SourceHint = new Regex("--source-shortcut-location(?:=|\\s+)(?:\"[^\"]*\"|\\S+)", RegexOptions.IgnoreCase);

	public static bool Organizable(Entry entry)
	{
		if (entry == null || entry.IsFolder)
		{
			return false;
		}
		string path = entry.Path ?? "";
		if (path.Length == 0 || path.StartsWith("::", StringComparison.Ordinal) || !Path.IsPathRooted(path))
		{
			return false;
		}
		// 文件分类（桌面上的普通文件）不参与整理。
		return !AppCategoryRules.IsFileEntry(entry);
	}

	public static string Arguments(string arguments)
	{
		string text = (arguments ?? "").Trim();
		if (text.Length == 0)
		{
			return "";
		}
		text = SourceHint.Replace(text, "").Trim();
		return Regex.Replace(text, "\\s+", " ");
	}

	public static string Key(Entry entry)
	{
		if (entry == null)
		{
			return null;
		}
		string text = WindowActivationRules.NormalizePath(entry.LaunchTarget);
		if (string.IsNullOrWhiteSpace(text))
		{
			text = WindowActivationRules.NormalizePath(entry.Path);
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		return text.TrimEnd('\\') + "|" + Arguments(entry.LaunchArguments);
	}

	// 返回需要移除的重复入口；每组保留一个（已固定 > 有手动分类 > 列表里靠前）。
	public static List<Entry> Plan(Preferences prefs)
	{
		List<Entry> list = new List<Entry>();
		if (prefs == null || prefs.Apps == null)
		{
			return list;
		}
		foreach (Entry app in prefs.Apps)
		{
			if (!Organizable(app))
			{
				continue;
			}
			// 桌面快捷方式常常还没解析出目标；解析后才能和开始菜单里的同一款软件对上。
			if (string.IsNullOrWhiteSpace(app.LaunchTarget) && string.Equals(Path.GetExtension(app.Path ?? ""), ".lnk", StringComparison.OrdinalIgnoreCase))
			{
				LaunchInfoService.Enrich(app);
			}
			list.Add(app);
		}
		Dictionary<string, Entry> keep = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		List<Entry> remove = new List<Entry>();
		foreach (Entry item in list)
		{
			string key = Key(item);
			if (key == null)
			{
				continue;
			}
			if (!keep.TryGetValue(key, out var value))
			{
				keep[key] = item;
				continue;
			}
			if (Better(item, value))
			{
				remove.Add(value);
				keep[key] = item;
			}
			else
			{
				remove.Add(item);
			}
		}
		HashSet<string> removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		return remove.Where((Entry a) => removed.Add(a.Path)).ToList();
	}

	private static bool Better(Entry candidate, Entry current)
	{
		if (candidate.Pinned != current.Pinned)
		{
			return candidate.Pinned;
		}
		bool flag = !string.IsNullOrWhiteSpace(candidate.Category);
		bool flag2 = !string.IsNullOrWhiteSpace(current.Category);
		if (flag != flag2)
		{
			return flag;
		}
		return false;
	}
}
