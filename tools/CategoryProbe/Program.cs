using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using IconBox;

internal static class Program
{
	private static string S(Dictionary<string, object> dict, string key)
	{
		return dict.ContainsKey(key) ? Convert.ToString(dict[key]) : null;
	}

	private static void Main()
	{
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconBox", "layout.json");
		if (!File.Exists(path))
		{
			Console.WriteLine("layout.json not found");
			return;
		}
		JavaScriptSerializer serializer = new JavaScriptSerializer();
		serializer.MaxJsonLength = int.MaxValue;
		Dictionary<string, object> root = serializer.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
		IEnumerable rawApps = (root == null) ? null : (root.ContainsKey("Apps") ? root["Apps"] as IEnumerable : null);
		if (rawApps == null)
		{
			Console.WriteLine("Apps missing");
			return;
		}

		Console.WriteLine("name\tmanual\tauto\ttop1\twhy1\ttop2\ttop3");
		Dictionary<string, int> histogram = new Dictionary<string, int>();
		int total = 0;
		foreach (object raw in rawApps)
		{
			Dictionary<string, object> dict = raw as Dictionary<string, object>;
			if (dict == null)
			{
				continue;
			}
			Entry entry = new Entry
			{
				Name = S(dict, "Name"),
				Path = S(dict, "Path"),
				LaunchTarget = S(dict, "LaunchTarget"),
				LaunchArguments = S(dict, "LaunchArguments"),
				ProductInfo = S(dict, "ProductInfo"),
				Category = S(dict, "Category"),
				Pinned = dict.ContainsKey("Pinned") && Convert.ToBoolean(dict["Pinned"])
			};
			string auto = AppCategoryRules.Resolve(entry);
			total++;
			histogram[auto] = (histogram.ContainsKey(auto) ? histogram[auto] : 0) + 1;
			List<AppCategoryRules.GuessHit> hits = AppCategoryRules.Explain(entry);
			string top1 = "-", why1 = "-", top2 = "-", top3 = "-";
			if (hits != null && hits.Count > 0)
			{
				top1 = hits[0].Id + ":" + hits[0].Score;
				why1 = hits[0].Field + ":" + hits[0].Keyword;
				if (hits.Count > 1) top2 = hits[1].Id + ":" + hits[1].Score;
				if (hits.Count > 2) top3 = hits[2].Id + ":" + hits[2].Score;
			}
			Console.WriteLine(string.Join("\t", new string[] { entry.Name, entry.Category, auto, top1, why1, top2, top3 }));
		}
		Console.WriteLine("--- histogram total=" + total + " ---");
		foreach (KeyValuePair<string, int> pair in histogram.OrderByDescending(p => p.Value))
		{
			Console.WriteLine(pair.Key + "=" + pair.Value);
		}
	}
}
