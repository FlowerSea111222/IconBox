using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace IconBox;

/// <summary>
/// 扫描去重用的快捷方式解析器。
/// 批量添加扫描开始菜单/桌面时，同一个应用常常有多个 .lnk（用户名目录与公共目录各一份、
/// 桌面快捷方式与开始菜单各一份），路径不同但指向同一个可执行文件。只比较路径会把它们
/// 当成新图标列出来，所以这里额外把快捷方式解析成「目标 exe + 启动参数」，
/// 再和已添加条目的身份（见 RemovalRules.Identity）比对。
/// 为了让批量扫描不至于卡顿，WScript.Shell 实例只创建一次，扫描结束统一释放。
/// </summary>
public static class ScanDedupRules
{
	public static bool IsShortcut(string path)
	{
		string extension = Path.GetExtension(path ?? "");
		return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>解析快捷方式，返回目标路径与启动参数；失败时返回 null（调用方按路径去重处理）。</summary>
	public static string[] Resolve(string path)
	{
		string text = path ?? "";
		string extension = Path.GetExtension(text);
		if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				string text2 = StartupEntryRules.ReadUrl(text) ?? "";
				return string.IsNullOrWhiteSpace(text2) ? null : new string[2] { text2, "" };
			}
			catch
			{
				return null;
			}
		}
		if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		object obj = null;
		try
		{
			obj = Shell();
			if (obj == null)
			{
				return null;
			}
			object obj2 = obj.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, obj, new object[1] { text });
			if (obj2 == null)
			{
				return null;
			}
			try
			{
				string text3 = (string)obj2.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, obj2, null);
				string text4 = (string)obj2.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, obj2, null);
				if (string.IsNullOrWhiteSpace(text3))
				{
					return null;
				}
				return new string[2] { text3, text4 ?? "" };
			}
			finally
			{
				if (Marshal.IsComObject(obj2))
				{
					Marshal.ReleaseComObject(obj2);
				}
			}
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 候选快捷方式是否已经出现在面板里：主名命中已添加名称，或「目标 + 参数」与已添加条目身份相同。
	/// 只对快捷方式生效，普通文件仍由调用方按路径去重。
	/// </summary>
	public static bool AlreadyAdded(string path, HashSet<string> identities, HashSet<string> names)
	{
		if (!IsShortcut(path))
		{
			return false;
		}
		string text = EntryPathRules.Stem(path);
		if (!string.IsNullOrWhiteSpace(text) && names.Contains(text.Trim()))
		{
			return true;
		}
		string[] array = Resolve(path);
		if (array == null)
		{
			return false;
		}
		string item = "target|" + WindowActivationRules.NormalizePath(array[0]) + "|" + (array[1] ?? "").Trim();
		return identities.Contains(item);
	}

	private static object shell;

	private static bool shellFailed;

	private static object Shell()
	{
		if (shell != null || shellFailed)
		{
			return shell;
		}
		try
		{
			Type typeFromProgID = Type.GetTypeFromProgID("WScript.Shell");
			shell = ((typeFromProgID == null) ? null : Activator.CreateInstance(typeFromProgID));
		}
		catch
		{
			shell = null;
		}
		shellFailed = shell == null;
		return shell;
	}

	public static void Release()
	{
		if (shell != null && Marshal.IsComObject(shell))
		{
			try
			{
				Marshal.ReleaseComObject(shell);
			}
			catch
			{
			}
		}
		shell = null;
		shellFailed = false;
	}
}
