using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using IconBox;

internal static class Program
{
	private static int Main()
	{
		Console.OutputEncoding = Encoding.UTF8;
		string layout = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconBox", "layout.json");
		if (!File.Exists(layout))
		{
			Console.WriteLine("layout.json not found");
			return 2;
		}
		Dictionary<string, object> root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(layout, Encoding.UTF8));
		List<Entry> existing = new List<Entry>();
		object appsObject;
		if (root.TryGetValue("Apps", out appsObject) && appsObject is object[] apps)
		{
			foreach (object item in apps)
			{
				if (!(item is Dictionary<string, object> map))
				{
					continue;
				}
				existing.Add(new Entry
				{
					Name = Text(map, "Name"),
					Path = Text(map, "Path"),
					LaunchTarget = Text(map, "LaunchTarget"),
					LaunchArguments = Text(map, "LaunchArguments"),
					IsFolder = Bool(map, "IsFolder")
				});
			}
		}
		Console.WriteLine("existing_entries=" + existing.Count);
		List<string> scan = new List<string>();
		string[] roots = new string[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.Programs),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
			Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
		};
		foreach (string root2 in roots.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(root2) || !Directory.Exists(root2))
			{
				continue;
			}
			try
			{
				scan.AddRange(Directory.GetFiles(root2, "*.lnk", SearchOption.AllDirectories));
				scan.AddRange(Directory.GetFiles(root2, "*.url", SearchOption.AllDirectories));
			}
			catch (Exception ex)
			{
				Console.WriteLine("scan warn: " + ex.Message);
			}
		}
		Console.WriteLine("scanned_shortcuts=" + scan.Count);
		string[] menuRoots = new string[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.Programs),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
		};
		ScanResult raw = ImportScanner.Scan(menuRoots, true, System.Threading.CancellationToken.None, 2500, 10000, true);
		Console.WriteLine("scanner_startmenu_paths=" + raw.Paths.Count + " skipped=" + raw.Skipped + " limited=" + raw.Limited);
		List<string> filtered = raw.Paths.Where((string path) => LaunchInfoService.Primary(LaunchInfoService.Read(path))).ToList();
		Console.WriteLine("after_launchonly=" + filtered.Count);
		List<string> menuCandidates = EntryRules.Candidates(filtered, existing);
		Console.WriteLine("startmenu_candidates=" + menuCandidates.Count);
		HashSet<string> dbgNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Entry item in existing)
		{
			if (!string.IsNullOrWhiteSpace(item.Name)) dbgNames.Add(item.Name.Trim());
			string s1 = EntryPathRules.Stem(item.Path); if (!string.IsNullOrWhiteSpace(s1)) dbgNames.Add(s1.Trim());
			string s2 = EntryPathRules.Stem(item.LaunchTarget); if (!string.IsNullOrWhiteSpace(s2)) dbgNames.Add(s2.Trim());
		}
		HashSet<string> dbgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Entry item in existing) { string id = RemovalRules.Identity(item); if (!string.IsNullOrWhiteSpace(id)) dbgIds.Add(id); }
		int shown = 0;
		int nameHits = 0, idHits = 0, pathHits = 0;
		foreach (string path in filtered)
		{
			string norm = EntryPathRules.Normalize(path);
			if (knownBaseContains(existing, norm)) { pathHits++; continue; }
			string stem = EntryPathRules.Stem(path);
			if (!string.IsNullOrWhiteSpace(stem) && dbgNames.Contains(stem.Trim())) { nameHits++; if (shown < 6) { Console.WriteLine("  NAME-HIT " + Path.GetFileName(path)); shown++; } continue; }
			string[] res = ScanDedupRules.Resolve(path);
			if (res == null) continue;
			string id2 = "target|" + WindowActivationRules.NormalizePath(res[0]) + "|" + (res[1] ?? "").Trim();
			if (dbgIds.Contains(id2)) { idHits++; if (shown < 12) { Console.WriteLine("  ID-HIT " + Path.GetFileName(path) + " -> " + id2); shown++; } }
		}
		ScanDedupRules.Release();
		Console.WriteLine("pathHits=" + pathHits + " nameHits=" + nameHits + " idHits=" + idHits + " total=" + filtered.Count);
		List<string> list = EntryRules.Candidates(scan, existing);
		Console.WriteLine("new_candidates=" + list.Count);
		HashSet<string> known = new HashSet<string>(existing.Select((Entry a) => EntryPathRules.Normalize(a.Path)), StringComparer.OrdinalIgnoreCase);
		List<string> old = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in scan)
		{
			string text = EntryPathRules.Normalize(item);
			if (!string.IsNullOrWhiteSpace(text) && !known.Contains(text) && seen.Add(text))
			{
				old.Add(text);
			}
		}
		Console.WriteLine("old_candidates=" + old.Count);
		Console.WriteLine("--- filtered out (previously listed as duplicates) ---");
		foreach (string item2 in old)
		{
			if (!list.Contains(item2))
			{
				string[] resolved = ScanDedupRules.Resolve(item2);
				string target = (resolved == null) ? "(unresolved)" : resolved[0] + "  args=" + resolved[1];
				Console.WriteLine("  " + Path.GetFileName(item2) + "  [stem=" + EntryPathRules.Stem(item2) + "] " + target);
			}
		}
		int falsePos = 0;
		foreach (string item in list)
		{
			string norm = EntryPathRules.Normalize(item);
			foreach (Entry ex in existing)
			{
				if (string.Equals(EntryPathRules.Normalize(ex.Path), norm, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  FALSE-POS path: " + item); falsePos++; break; }
			}
		}
		Console.WriteLine("false_positives_path=" + falsePos);
		Console.WriteLine("--- remaining candidates (" + list.Count + ") ---");
		foreach (string item3 in list.Take(40))
		{
			Console.WriteLine("  " + Path.GetFileName(item3));
		}
		ScanDedupRules.Release();
		return 0;
	}

	private static bool knownBaseContains(List<Entry> existing, string normalized)
	{
		foreach (Entry item in existing)
		{
			if (string.Equals(EntryPathRules.Normalize(item.Path), normalized, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}
	private static string Text(Dictionary<string, object> map, string key)
	{
		object value;
		if (!map.TryGetValue(key, out value) || value == null)
		{
			return "";
		}
		return Convert.ToString(value);
	}

	private static bool Bool(Dictionary<string, object> map, string key)
	{
		object value;
		return map.TryGetValue(key, out value) && value != null && Convert.ToBoolean(value);
	}
}
