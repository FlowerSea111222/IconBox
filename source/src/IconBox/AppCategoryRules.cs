using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace IconBox;

// 分类的可序列化定义：持久化到 Preferences.Categories。
[DataContract]
public class CategoryDef
{
	[DataMember]
	public string Id;

	[DataMember]
	public string Name;

	[DataMember(EmitDefaultValue = false)]
	public string Glyph;

	// 图标字体（可选）。为空表示跟随界面默认字体；「文件」分类用 Segoe UI Symbol 的文档图标。
	[DataMember(EmitDefaultValue = false)]
	public string Font;
}

// 应用面板（Board）分类栏的取数规则。
// 优先使用用户手动归类（Entry.Category，拖到分类标签即写入），
// 没有手动归类时按名称 / 路径 / 启动目标 / 产品信息里的关键词自动推断。
//
// 推断算法：对每个分类累计“最具体命中的关键词长度”，取分值最高者（同分按规则顺序）。
// 这样 “微信输入法” 会被更长的 “输入法” 判到工具类，而不是被 “微信” 抢到社交类；
// 短关键词（qq / hub / run 等）另有精确名称匹配兜底，避免误伤。
public static class AppCategoryRules
{
	public const string All = "all";

	public const string Office = "office";

	public const string Social = "social";

	public const string Dev = "dev";

	public const string Design = "design";

	public const string Media = "media";

	public const string Game = "game";

	public const string Net = "net";

	public const string Security = "security";

	public const string Tool = "tool";

	public const string System = "system";

	public const string Other = "other";

	// 「文件」分类：桌面上的普通文件（文档 / 图片 / 音视频 / 压缩包等）统一归到这里，
	// 不再按关键词散落到办公 / 设计 / 影音。文件夹、快捷方式与可执行程序不参与。
	public const string File = "file";

	public sealed class Category
	{
		public string Id;

		public string Name;

		public string Glyph;

		public string Font;
	}

	private sealed class Rule
	{
		public string Id;

		public string[] Keywords;

		// 短名 / 易误判的名字，按“整个名称完全相等”命中，权重高于普通关键词
		public string[] ExactNames = new string[0];

		// 路径提示只做小幅加权（+5），用于给系统目录里的工具补一点倾向，
		// 但不会压过更具体的关键词，例如 WindowsPowerShell 仍然归开发类。
		public string[] PathHints = new string[0];

		// 反关键词：命中即整条规则作废（例如「微信开发者工具」不该归社交）。
		public string[] AntiKeywords = new string[0];
	}

	// 「全部」不再是一个真实分类：它只是内部虚拟视图 id（显示所有应用），
	// 不出现在分类表里，也不参与归类 / 排序 / 重命名 / 删除。
	public static readonly Category[] DefaultCategories = new Category[]
	{
		new Category
		{
			Id = Office,
			Name = "办公",
			Glyph = "▤"
		},
		new Category
		{
			Id = Social,
			Name = "社交",
			Glyph = "◍"
		},
		new Category
		{
			Id = Dev,
			Name = "开发",
			Glyph = "▥"
		},
		new Category
		{
			Id = Design,
			Name = "设计",
			Glyph = "◫"
		},
		new Category
		{
			Id = Media,
			Name = "影音",
			Glyph = "♪"
		},
		new Category
		{
			Id = Game,
			Name = "游戏",
			Glyph = "◈"
		},
		new Category
		{
			Id = Net,
			Name = "网络",
			Glyph = "◌"
		},
		new Category
		{
			Id = Security,
			Name = "安全",
			Glyph = "▩"
		},
		new Category
		{
			Id = Tool,
			Name = "工具",
			Glyph = "✦"
		},
		new Category
		{
			Id = System,
			Name = "系统",
			Glyph = "⚙"
		},
		new Category
		{
			Id = File,
			Name = "文件",
			Glyph = "\uD83D\uDDCF",
			Font = "Segoe UI Symbol"
		},
		new Category
		{
			Id = Other,
			Name = "其他",
			Glyph = "▣"
		}
	};

	// —— 用户可编辑的分类列表（顺序 / 名称 / 增删）——
	// 首次使用（没有保存过自定义列表）时用 DefaultCategories；之后以用户列表为准。
	private static List<Category> list;

	// 内置分类的固定图标，重命名时保留；新增分类给默认图标。
	private static readonly Dictionary<string, string> builtInGlyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		{ All, "▦" },
		{ Office, "▤" },
		{ Social, "◍" },
		{ Dev, "▥" },
		{ Design, "◫" },
		{ Media, "♪" },
		{ Game, "◈" },
		{ Net, "◌" },
		{ Security, "▩" },
		{ Tool, "✦" },
		{ System, "⚙" },
		{ File, "\uD83D\uDDCF" },
		{ Other, "▣" }
	};

	// 内置分类的图标字体（为空 = 跟随界面默认字体）。
	private static readonly Dictionary<string, string> builtInFonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		{ File, "Segoe UI Symbol" }
	};

	private static void EnsureInit()
	{
		if (list == null)
		{
			list = DefaultCategories.Select((Category c) => new Category
			{
				Id = c.Id,
				Name = c.Name,
				Glyph = c.Glyph,
				Font = c.Font
			}).ToList();
		}
	}

	public static Category[] Categories
	{
		get
		{
			EnsureInit();
			return list.ToArray();
		}
	}

	// 启动时按保存的配置初始化；null / 空 = 使用默认分类。
	public static void Initialize(IEnumerable<CategoryDef> defs)
	{
		list = null;
		EnsureInit();
		if (defs == null)
		{
			return;
		}
		List<Category> loaded = new List<Category>();
		foreach (CategoryDef def in defs)
		{
			if (def == null || string.IsNullOrWhiteSpace(def.Id) || string.IsNullOrWhiteSpace(def.Name))
			{
				continue;
			}
			string id = def.Id.Trim();
			if (loaded.Any((Category c) => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			loaded.Add(new Category
			{
				Id = id,
				Name = def.Name.Trim(),
				Glyph = (string.IsNullOrWhiteSpace(def.Glyph) ? GlyphFor(id) : def.Glyph),
				Font = (string.IsNullOrWhiteSpace(def.Font) ? FontFor(id) : def.Font)
			});
		}
		if (loaded.Count == 0)
		{
			return;
		}
		// “全部”不是真实分类：历史上残留的 all 条目直接丢弃，也不补。
		loaded.RemoveAll((Category c) => string.Equals(c.Id, All, StringComparison.OrdinalIgnoreCase));
		if (loaded.Count == 0)
		{
			return;
		}
		list = loaded;
	}

	public static List<CategoryDef> Snapshot()
	{
		EnsureInit();
		return list.Select((Category c) => new CategoryDef
		{
			Id = c.Id,
			Name = c.Name,
			Glyph = c.Glyph,
			Font = c.Font
		}).ToList();
	}

	public static string GlyphFor(string id)
	{
		if (id != null && builtInGlyphs.TryGetValue(id, out string glyph))
		{
			return glyph;
		}
		return "◆";
	}

	public static string FontFor(string id)
	{
		if (id != null && builtInFonts.TryGetValue(id, out string font))
		{
			return font;
		}
		return null;
	}

	public static bool IsBuiltIn(string id)
	{
		return id != null && builtInGlyphs.ContainsKey(id);
	}

	// 拖动排序：按给定 id 顺序重排；未知 id 忽略，缺失的追加到末尾。
	public static bool Reorder(IList<string> orderedIds)
	{
		EnsureInit();
		if (orderedIds == null || orderedIds.Count == 0)
		{
			return false;
		}
		List<Category> ordered = new List<Category>();
		foreach (string id in orderedIds)
		{
			if (string.IsNullOrWhiteSpace(id))
			{
				continue;
			}
			Category found = list.FirstOrDefault((Category c) => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
			if (found != null && !ordered.Contains(found))
			{
				ordered.Add(found);
			}
		}
		foreach (Category category in list)
		{
			if (!ordered.Contains(category))
			{
				ordered.Add(category);
			}
		}
		bool changed = ordered.Count != list.Count || ordered.Where((Category c, int i) => !string.Equals(c.Id, list[i].Id, StringComparison.OrdinalIgnoreCase)).Any();
		if (changed)
		{
			list = ordered;
		}
		return changed;
	}

	// 重命名；“全部”也允许改名。返回 false 表示名字非法或未变化。
	public static bool Rename(string id, string name)
	{
		EnsureInit();
		if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		Category category = list.FirstOrDefault((Category c) => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
		if (category == null)
		{
			return false;
		}
		string text = name.Trim();
		if (text.Length > 12)
		{
			text = text.Substring(0, 12);
		}
		if (string.Equals(category.Name, text, StringComparison.Ordinal))
		{
			return false;
		}
		category.Name = text;
		return true;
	}

	// 新增自定义分类（不参与关键词自动归类，只能手动拖入）。返回新分类 id。
	public static string Add(string name)
	{
		EnsureInit();
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}
		string text = name.Trim();
		if (text.Length > 12)
		{
			text = text.Substring(0, 12);
		}
		string id = "c" + Guid.NewGuid().ToString("N").Substring(0, 8);
		list.Add(new Category
		{
			Id = id,
			Name = text,
			Glyph = "◆"
		});
		return id;
	}

	// 删除分类；“全部”不可删。被删分类下的应用会自动回落到关键词推断。
	public static bool Remove(string id)
	{
		EnsureInit();
		if (string.IsNullOrWhiteSpace(id) || string.Equals(id, All, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		Category category = list.FirstOrDefault((Category c) => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
		if (category == null)
		{
			return false;
		}
		list.Remove(category);
		return true;
	}

	public static void ResetDefaults()
	{
		list = null;
		EnsureInit();
	}

	private static readonly Rule[] Rules = new Rule[]
	{
		new Rule
		{
			Id = System,
			Keywords = new string[]
			{
				"回收站", "此电脑", "file explorer", "资源管理器", "control panel", "控制面板",
				"task manager", "任务管理器", "character map", "字符映射表", "disk cleanup", "磁盘清理",
				"dfrgui", "驱动器优化", "memory diagnostics", "内存诊断", "odbc", "recoverydrive",
				"registry editor", "注册表", "resource monitor", "资源监视", "steps recorder", "步骤记录",
				"system configuration", "系统配置", "system information", "系统信息", "iscsi",
				"administrative tools", "remote desktop", "远程桌面", "magnify", "放大镜", "narrator",
				"屏幕阅读", "on-screen keyboard", "屏幕键盘", "voiceaccess", "语音访问", "livecaptions",
				"实时字幕", "command prompt", "diskmgmt", "disk management", "磁盘管理", "设备管理器", "命令提示符", "cmd.exe"
			},
			ExactNames = new string[] { "run", "运行", "cmd", "explorer", "taskmgr" },
			PathHints = new string[] { "\\windows\\system32", "\\windows\\syswow64", "\\windows\\regedit.exe", "\\windows\\system32\\" }
		},
		new Rule
		{
			Id = Dev,
			ExactNames = new string[] { "inno setup compiler" },
			Keywords = new string[]
			{
				"powershell", "command prompt", "node.js", "nodejs", "install additional tools for node",
				"deepseek harness", "visual studio", "vscode", "git bash", "git.exe", "github desktop",
				"python", "windows terminal", "developer", "调试", "编译", "jdk", "java", "maven", "gradle",
				"docker", "wireshark", "postman", "navicat", "android studio", "idea", "pycharm", "webstorm", "opencode", "windsurf", "cursor", "trae", "clion", "goland"
			}
		},
		new Rule
		{
			Id = Design,
			Keywords = new string[]
			{
				"cad", "autocad", "photoshop", "illustrator", "lightroom", "premiere", "after effects",
				"audition", "剪映", "capcut", "blender", "sketchup", "3ds max", "c4d", "coreldraw",
				"solidworks", "proe", "ug", "catia", "revit", "看图", "绘图", "制图", "建模", "设计"
			}
		},
		new Rule
		{
			Id = Media,
			AntiKeywords = new string[] { "输入法", "加速器", "开发者工具" },
			Keywords = new string[]
			{
				"potplayer", "media player", "网易云", "spotify", "vlc", "music", "音乐", "抖音",
				"bilibili", "哔哩哔哩", "播放器", "qq音乐", "酷狗", "kugou", "foobar", "kmplayer",
				"爱奇艺", "腾讯视频", "优酷", "youku", "iqiyi", "录屏", "obs"
			}
		},
		new Rule
		{
			Id = Game,
			Keywords = new string[]
			{
				"steam", "wegame", "英雄联盟", "league of legends", "永劫无间", "naraka", "加速器",
				"epic games", "battle.net", "uplay", "ubisoft", "xbox", "minecraft", "游戏", "game"
			}
		},
		new Rule
		{
			Id = Social,
			AntiKeywords = new string[] { "输入法", "开发者工具", "developer tools" },
			Keywords = new string[]
			{
				"微信", "wechat", "weixin", "qq", "腾讯会议", "tencent meeting", "小鱼易连", "oopz",
				"discord", "telegram", "钉钉", "dingtalk", "飞书", "lark", "中建通", "会议", "meeting",
				"邮箱", "outlook", "thunderbird", "foxmail", "企业微信", "whatsapp", "zoom", "teams"
			}
		},
		new Rule
		{
			Id = Office,
			AntiKeywords = new string[] { "输入法" },
			Keywords = new string[]
			{
				"wps", "office", "word", "excel", "powerpoint", "ppt", "pdf", "foxit", "acrobat",
				"notion", "flowus", "folia", "清单", "笔记", "note", "报告", "文档", "xmind", "visio",
				"project", "腾讯文档", "石墨", "印象笔记", "onedrive", "obsidian", "notion", "语雀", "typora", "logseq", "joplin", "表格", "演示"
			}
		},
		new Rule
		{
			Id = Net,
			Keywords = new string[]
			{
				"firefox", "microsoft edge", "msedge", "chrome", "浏览器", "browser", "ego", "云盘",
				"网盘", "netdisk", "adrive", "onedrive", "obsidian", "notion", "语雀", "typora", "logseq", "joplin", "dropbox", "vpn", "clash", "v2ray", "proxy",
				"代理", "atrust", "蒲公英", "sakura", "oppo 互联", "互联", "投屏", "向日葵", "todesk",
				"anydesk", "teamviewer", "ftp", "winscp", "xshell", "securecrt"
			}
		},
		new Rule
		{
			Id = Security,
			Keywords = new string[]
			{
				"火绒", "huorong", "安全软件", "杀毒", "antivirus", "defender", "360安全", "360杀毒",
				"金山毒霸", "卡巴斯基", "kaspersky", "防火墙", "firewall", "hips", "沙箱"
			}
		},
		new Rule
		{
			Id = Tool,
			Keywords = new string[]
			{
				"pixpin", "quicker", "deskbox", "nvidia", "mchose", "a hub", "hub", "everything",
				"bandizip", "7-zip", "winrar", "snipaste", "utools", "豆包", "workbuddy", "输入法",
				"水印相机", "解压", "截图", "桌面整理", "驱动", "硬件", "显卡", "磁盘工具", "分区",
				"清理", "优化", "工具", "计算器", "记事本", "画图", "翻译", "词典", "录屏工具", "magpie", "edithere", "alma"
			}
		}
	};

	public static Category Find(string id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return null;
		}
		EnsureInit();
		string text = id.Trim();
		foreach (Category category in list)
		{
			if (string.Equals(category.Id, text, StringComparison.OrdinalIgnoreCase) || string.Equals(category.Name, text, StringComparison.Ordinal))
			{
				return category;
			}
		}
		return null;
	}

	public static string Name(string id)
	{
		Category category = Find(id);
		return (category == null) ? "全部" : category.Name;
	}

	public static string Normalize(string value)
	{
		Category category = Find(value);
		return (category == null) ? null : category.Id;
	}

	// 这些扩展名代表「程序 / 快捷方式」而不是「文件」：仍按关键词自动归类。
	private static readonly HashSet<string> NonFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".lnk", ".url", ".appref-ms", ".exe", ".msi", ".msix", ".bat", ".cmd",
		".ps1", ".vbs", ".jar", ".com", ".scr", ".pif"
	};

	// 桌面上的普通文件：文档 / 图片 / 音视频 / 压缩包等都算文件，统一进「文件」分类。
	// 桌面上的文件夹同样算「文件」，也进「文件」分类。
	public static bool IsFileEntry(Entry entry)
	{
		if (entry == null)
		{
			return false;
		}
		string path = entry.Path ?? "";
		// Shell 特殊项（此电脑 / 回收站等）的路径形如 ::{GUID}，不是真实文件路径。
		if (path.Length == 0 || path.StartsWith("::", StringComparison.Ordinal) || !Path.IsPathRooted(path))
		{
			return false;
		}
		// 文件夹没有扩展名，先判定，避免被下面的扩展名逻辑漏掉。
		if (entry.IsFolder)
		{
			return true;
		}
		// 有些历史条目没有把 IsFolder 存进 layout.json：路径没有扩展名时按磁盘实际状态补判，
		// 免得用户刚建的空文件夹被归到「其他」。
		if (Path.GetExtension(path).Length == 0 && Directory.Exists(path))
		{
			return true;
		}
		string extension;
		try
		{
			extension = Path.GetExtension(path);
		}
		catch
		{
			return false;
		}
		if (string.IsNullOrEmpty(extension))
		{
			return false;
		}
		return !NonFileExtensions.Contains(extension);
	}

	// 归类入口：手动值优先，其次桌面普通文件统一进「文件」，最后按关键词自动推断。
	// 文件与手动分类「文件」时仍归「文件」：桌面文件只能待在「文件」分类里，
	// 否则把它拖到「文件」标签上会被判成 manual=file，反而从「文件」里消失。
	public static string Resolve(Entry entry)
	{
		if (entry == null)
		{
			return Other;
		}
		bool fileEntry = IsFileEntry(entry);
		string manual = Normalize(entry.Category);
		if (manual != null && !string.Equals(manual, All, StringComparison.OrdinalIgnoreCase))
		{
			if (fileEntry && string.Equals(manual, File, StringComparison.OrdinalIgnoreCase))
			{
				return File;
			}
			return manual;
		}
		if (fileEntry)
		{
			return File;
		}
		return Guess(entry);
	}

	public static int Count(IEnumerable<Entry> apps, string id)
	{
		if (apps == null)
		{
			return 0;
		}
		if (string.Equals(id, All, StringComparison.OrdinalIgnoreCase))
		{
			return apps.Count();
		}
		int num = 0;
		foreach (Entry app in apps)
		{
			if (string.Equals(Resolve(app), id, StringComparison.OrdinalIgnoreCase))
			{
				num++;
			}
		}
		return num;
	}

	// ——— 归类打分 ———
	// 只在「名称 / 产品名 / 目标程序名 / 目录名 / 完整路径」里找关键词：越可靠的字段权重越高。
	// 拉丁关键词要求词边界，"obs" 就不会命中 "obsidian"，"qq" 也不会命中一个更长的英文单词。
	private const int NameWeight = 3;

	private const int ProductWeight = 2;

	private const int StemWeight = 2;

	private const int FolderWeight = 1;

	private const int PathWeight = 1;

	// 低于这个分数的命中一律当作「没认出来」：宁可落到「其他」，也不硬塞进某个分类。
	private const int MinGuessScore = 45;

	public sealed class GuessHit
	{
		public string Id;

		public int Score;

		public string Keyword;

		public string Field;
	}

	/// 把入口拆成几个可比较的字段（全部小写、标点统一成空格），用于打分与排查。
	internal sealed class GuessContext
	{
		public string Name;

		public string Product;

		public string Stem;

		public string Folders;

		public string Full;

		public static GuessContext From(Entry entry)
		{
			if (entry == null)
			{
				return null;
			}
			string target = entry.LaunchTarget ?? "";
			string path = entry.Path ?? "";
			return new GuessContext
			{
				Name = Clean(entry.Name),
				Product = Clean(entry.ProductInfo),
				Stem = Clean(StemOf(target, path)),
				Folders = Clean(FoldersOf(string.IsNullOrEmpty(target) ? path : target)),
				Full = Clean(path + " " + target)
			};
		}

		/// 小写 + 标点归一：括号、连接符、路径分隔符都当空格，避免「（64 位）」这类后缀干扰词边界。
		internal static string Clean(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return "";
			}
			string lower = text.ToLowerInvariant();
			global::System.Text.StringBuilder builder = new global::System.Text.StringBuilder(lower.Length);
			foreach (char c in lower)
			{
				builder.Append(IsSeparator(c) ? ' ' : c);
			}
			string spaced = builder.ToString();
			while (spaced.IndexOf("  ", StringComparison.Ordinal) >= 0)
			{
				spaced = spaced.Replace("  ", " ");
			}
			return spaced.Trim();
		}

		private static bool IsSeparator(char c)
		{
			switch (c)
			{
			case '(':
			case ')':
			case '[':
			case ']':
			case '{':
			case '}':
			case '（':
			case '）':
			case '【':
			case '】':
			case '_':
			case '-':
			case '+':
			case '.':
			case ',':
			case '/':
			case '\\':
			case '|':
			case ':':
			case ';':
			case '·':
			case '\t':
				return true;
			default:
				return false;
			}
		}

		private static string StemOf(string target, string fallback)
		{
			string path = string.IsNullOrEmpty(target) ? fallback : target;
			try
			{
				return global::System.IO.Path.GetFileNameWithoutExtension(path ?? "");
			}
			catch
			{
				return "";
			}
		}

		private static string FoldersOf(string path)
		{
			try
			{
				return global::System.IO.Path.GetDirectoryName(path ?? "") ?? "";
			}
			catch
			{
				return "";
			}
		}
	}

	/// 返回每个分类的打分（降序），用于排查「为什么它被分到这一类」。
	public static List<GuessHit> Explain(Entry entry)
	{
		List<GuessHit> hits = new List<GuessHit>();
		GuessContext context = GuessContext.From(entry);
		if (context == null)
		{
			return hits;
		}
		foreach (Rule rule in Rules)
		{
			string keyword;
			string field;
			int score = ScoreRule(rule, context, out keyword, out field);
			if (score > 0)
			{
				hits.Add(new GuessHit
				{
					Id = rule.Id,
					Score = score,
					Keyword = keyword,
					Field = field
				});
			}
		}
		hits.Sort((GuessHit a, GuessHit b) => b.Score.CompareTo(a.Score));
		return hits;
	}

	private static int ScoreRule(Rule rule, GuessContext context, out string keyword, out string field)
	{
		keyword = null;
		field = null;
		int best = 0;
		foreach (string exact in rule.ExactNames)
		{
			if (exact.Length != 0 && string.Equals(context.Name, GuessContext.Clean(exact), StringComparison.Ordinal))
			{
				int exactScore = 200 + exact.Length;
				if (exactScore > best)
				{
					best = exactScore;
					keyword = exact;
					field = "name";
				}
			}
		}
		if (!Blocked(rule, context))
		{
			int[] weights = new int[5] { NameWeight, ProductWeight, StemWeight, FolderWeight, PathWeight };
			string[] fields = new string[5] { context.Name, context.Product, context.Stem, context.Folders, context.Full };
			string[] names = new string[5] { "name", "product", "stem", "folder", "path" };
			foreach (string word in rule.Keywords)
			{
				string needle = ((word == null) ? "" : word.ToLowerInvariant());
				if (needle.Length == 0)
				{
					continue;
				}
				for (int i = 0; i < fields.Length; i++)
				{
					int quality = MatchQuality(needle, fields[i]);
					if (quality > 0)
					{
						int score = quality * weights[i] + Math.Min(needle.Length, 12);
						if (score > best)
						{
							best = score;
							keyword = word;
							field = names[i];
						}
					}
				}
			}
			foreach (string hint in rule.PathHints)
			{
				if (best > 0 && hint.Length != 0 && context.Full.IndexOf(hint.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
				{
					best += 5;
					break;
				}
			}
		}
		return best;
	}

	/// 反关键词：命中就整条规则作废（例如「微信开发者工具」不该归社交，「微信输入法」不该归社交）。
	private static bool Blocked(Rule rule, GuessContext context)
	{
		foreach (string anti in rule.AntiKeywords)
		{
			if (anti.Length != 0 && context.Name.IndexOf(anti.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	/// 词边界打分：完全相等 100、词首 80、整词 60、词尾 45、词中 20（只有拉丁词才判边界）。
	private static int MatchQuality(string keyword, string text)
	{
		if (text.Length == 0 || keyword.Length == 0)
		{
			return 0;
		}
		int index = text.IndexOf(keyword, StringComparison.Ordinal);
		if (index < 0)
		{
			return 0;
		}
		if (text.Length == keyword.Length)
		{
			return 100;
		}
		bool left = IsBoundary(text, index - 1);
		bool right = IsBoundary(text, index + keyword.Length);
		if (!IsLatin(keyword))
		{
			// 中文关键词没有词边界概念，噪声很低，直接给高分
			return (left && right) ? 80 : 70;
		}
		if (left && right)
		{
			return 80;
		}
		if (left)
		{
			// 只命中词首（如 obs → obsidian）说明不是同一个词，给较低权重
			return 45;
		}
		if (right)
		{
			return 45;
		}
		return 20;
	}

	private static bool IsLatin(string text)
	{
		foreach (char c in text)
		{
			if (c > '\u007f')
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsBoundary(string text, int index)
	{
		if (index < 0 || index >= text.Length)
		{
			return true;
		}
		char c = text[index];
		return c < 'a' || c > 'z' ? (c < '0' || c > '9') && c != '_' : false;
	}

	private static string Guess(Entry entry)
	{
		GuessContext context = GuessContext.From(entry);
		string best = null;
		int bestScore = MinGuessScore - 1;
		foreach (Rule rule in Rules)
		{
			int score = ScoreRule(rule, context, out string _, out string _);
			if (score > bestScore)
			{
				bestScore = score;
				best = rule.Id;
			}
		}
		if (best != null)
		{
			return best;
		}
		if (!entry.IsFolder)
		{
			string extension = "";
			try
			{
				extension = Path.GetExtension(entry.Path ?? "").ToLowerInvariant();
			}
			catch
			{
				extension = "";
			}
			if (extension == ".pdf" || extension == ".doc" || extension == ".docx" || extension == ".xls" || extension == ".xlsx" || extension == ".ppt" || extension == ".pptx" || extension == ".txt" || extension == ".md" || extension == ".csv")
			{
				return Office;
			}
			if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".gif" || extension == ".bmp" || extension == ".webp" || extension == ".psd" || extension == ".ai" || extension == ".svg" || extension == ".dwg")
			{
				return Design;
			}
			if (extension == ".mp4" || extension == ".mkv" || extension == ".avi" || extension == ".mov" || extension == ".wmv" || extension == ".mp3" || extension == ".wav" || extension == ".flac" || extension == ".ape" || extension == ".m4a")
			{
				return Media;
			}
			if (extension == ".exe" || extension == ".msi" || extension == ".lnk" || extension == ".bat" || extension == ".ps1" || extension == ".zip" || extension == ".rar" || extension == ".7z")
			{
				return Tool;
			}
		}
		return Other;
	}
}
