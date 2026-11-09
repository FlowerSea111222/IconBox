using System;
using System.IO;

namespace IconBox;

public static class EntryPathRules
{
	public static string Normalize(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return path;
		}
		string fullPath = Path.GetFullPath(path);
		string pathRoot = Path.GetPathRoot(fullPath);
		if (!string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
		{
			return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		return pathRoot;
	}

	/// <summary>路径的文件主名（不含扩展名），用于扫描去重时按名称比对。</summary>
	public static string Stem(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		try
		{
			return Path.GetFileNameWithoutExtension(path) ?? "";
		}
		catch
		{
			return "";
		}
	}

	public static string Name(string path)
	{
		if (!Directory.Exists(path))
		{
			if (!DocumentRules.Supported(path))
			{
				return Path.GetFileNameWithoutExtension(path);
			}
			return Path.GetFileName(path);
		}
		DirectoryInfo directoryInfo = new DirectoryInfo(Normalize(path));
		if (!string.IsNullOrEmpty(directoryInfo.Name))
		{
			return directoryInfo.Name;
		}
		return directoryInfo.FullName;
	}
}
