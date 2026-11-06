using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;

namespace IconBox;

public static class Store
{
	public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconBox");

	public static readonly string FileName = Path.Combine(Folder, "layout.json");

	private static readonly object saveLock = new object();

	private static byte[] lastSaved;

	private static string lastInventory;

	private static DateTime lastSnapshot;

	public static string RecoveryMessage { get; private set; }

	static Store()
	{
		MigrateFromLegacyFolder();
	}

	// 更名迁移（GlassBoard → IconBox）：旧配置在 %LOCALAPPDATA%\GlassBoard，
	// 首次以 IconBox 启动时整目录复制过来，避免用户丢失布局、备份与图标。
	private static void MigrateFromLegacyFolder()
	{
		try
		{
			string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBoard");
			if (!Directory.Exists(legacy) || Directory.Exists(Folder))
			{
				return;
			}
			CopyTree(legacy, Folder);
			Trace("MIGRATE config folder from " + legacy);
		}
		catch (Exception ex)
		{
			Log(ex);
		}
	}

	private static void CopyTree(string from, string to)
	{
		Directory.CreateDirectory(to);
		string[] files = Directory.GetFiles(from);
		foreach (string file in files)
		{
			File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
		}
		string[] directories = Directory.GetDirectories(from);
		foreach (string directory in directories)
		{
			CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
		}
	}

	public static void Trace(string message)
	{
		try
		{
			Directory.CreateDirectory(Folder);
			string text = Path.Combine(Folder, "session.log");
			if (File.Exists(text) && new FileInfo(text).Length > 1048576)
			{
				File.Move(text, text + "." + DateTime.Now.Ticks);
			}
			File.AppendAllText(text, DateTime.Now.ToString("s") + " [" + Assembly.GetExecutingAssembly().GetName().Version?.ToString() + "] " + message + Environment.NewLine);
		}
		catch (Exception ex)
		{
			NotifyWriteFailure(ex);
		}
	}

	public static void Log(Exception ex)
	{
		try
		{
			Directory.CreateDirectory(Folder);
			File.AppendAllText(Path.Combine(Folder, "error.log"), DateTime.Now.ToString("s") + " [" + Assembly.GetExecutingAssembly().GetName().Version?.ToString() + "] " + ex?.ToString() + Environment.NewLine);
			return;
		}
		catch
		{
		}
		NotifyWriteFailure(ex);
	}

	// 配置目录写不进去时（权限、被占用、杀软拦截）退到临时目录，保证失败仍有记录可查
	private static int writeFailureNotified;

	private static void NotifyWriteFailure(Exception ex)
	{
		try
		{
			if (System.Threading.Interlocked.Exchange(ref writeFailureNotified, 1) == 1)
			{
				return;
			}
			File.AppendAllText(Path.Combine(Path.GetTempPath(), "IconBox-error.log"), DateTime.Now.ToString("s") + " [" + Assembly.GetExecutingAssembly().GetName().Version?.ToString() + "] 配置目录写入失败：" + Folder + Environment.NewLine + ex + Environment.NewLine);
		}
		catch
		{
		}
	}

	/// <summary>把一次保存失败追加到临时目录，便于配置目录不可写时仍能诊断。</summary>
	public static void WriteFailureReport(string message)
	{
		try
		{
			File.AppendAllText(Path.Combine(Path.GetTempPath(), "IconBox-error.log"), DateTime.Now.ToString("s") + " [" + Assembly.GetExecutingAssembly().GetName().Version?.ToString() + "] " + message + Environment.NewLine);
		}
		catch
		{
		}
	}

	private static byte[] Encode<T>(T value)
	{
		using MemoryStream memoryStream = new MemoryStream();
		new DataContractJsonSerializer(typeof(T)).WriteObject((Stream)memoryStream, (object)value);
		return memoryStream.ToArray();
	}

	public static Preferences ReadLayout(byte[] bytes)
	{
		if (bytes == null || bytes.Length > 8388608)
		{
			throw new InvalidDataException("配置文件过大");
		}
		using MemoryStream stream = new MemoryStream(bytes, writable: false);
		Preferences preferences = (Preferences)new DataContractJsonSerializer(typeof(Preferences)).ReadObject((Stream)stream);
		if (preferences == null || preferences.Apps == null)
		{
			throw new InvalidDataException("配置缺少入口清单");
		}
		return preferences;
	}

	private static void Atomic(string path, byte[] bytes, string backup = null)
	{
		string text = path + ".write-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				fileStream.Write(bytes, 0, bytes.Length);
				fileStream.Flush(flushToDisk: true);
			}
			ReplaceWithRetry(text, path, backup, bytes);
		}
		finally
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
		}
	}

	// 杀毒/备份软件扫描刚写完的文件时会短暂独占它，让 File.Replace 抛 IOException。
	// 这类冲突几十到几百毫秒内就会消失，所以重试几次，最后兜底直接覆盖写入。
	private static void ReplaceWithRetry(string temp, string path, string backup, byte[] bytes)
	{
		int[] delays = new int[6] { 40, 90, 160, 260, 400, 600 };
		Exception failure = null;
		for (int i = 0; i <= delays.Length; i++)
		{
			if (!File.Exists(temp))
			{
				if (File.Exists(path))
				{
					return;
				}
				File.WriteAllBytes(temp, bytes);
			}
			try
			{
				if (File.Exists(path))
				{
					File.Replace(temp, path, backup);
				}
				else
				{
					File.Move(temp, path);
				}
				return;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				failure = ex;
				if (i >= delays.Length)
				{
					break;
				}
				System.Threading.Thread.Sleep(delays[i]);
			}
		}
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
			fileStream.Write(bytes, 0, bytes.Length);
			fileStream.Flush(flushToDisk: true);
		}
		catch
		{
			throw failure;
		}
	}

	private static IEnumerable<string> RecoveryPaths()
	{
		List<string> list = new List<string>
		{
			FileName + ".last-good",
			FileName + ".previous",
			FileName + ".tmp"
		};
		string path = Path.Combine(Folder, "snapshots");
		if (Directory.Exists(path))
		{
			list.AddRange(Directory.GetFiles(path, "layout-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(30));
			list.AddRange(Directory.GetFiles(path, "upgrade-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(20));
		}
		list.AddRange(Directory.GetFiles(Folder, "layout.json.backup-*").Concat(Directory.GetFiles(Folder, "layout.json.before-*")).OrderByDescending(File.GetLastWriteTimeUtc)
			.Take(30));
		return list;
	}

	private static void ArchiveUpgrade(byte[] bytes)
	{
		try
		{
			string text = Path.Combine(Folder, "snapshots");
			Directory.CreateDirectory(text);
			string text2 = UpdateRules.Hash(bytes);
			string path = Path.Combine(text, "upgrade-" + text2 + ".json");
			if (!File.Exists(path))
			{
				Atomic(path, bytes);
			}
		}
		catch (Exception ex)
		{
			Log(ex);
		}
	}

	public static Preferences Load(bool mergeJournal = true)
	{
		lock (saveLock)
		{
			Directory.CreateDirectory(Folder);
			RecoveryMessage = null;
			lastSaved = null;
			bool flag = false;
			Preferences preferences = null;
			byte[] bytes = null;
			if (File.Exists(FileName))
			{
				try
				{
					bytes = File.ReadAllBytes(FileName);
					preferences = ReadLayout(bytes);
				}
				catch (Exception ex)
				{
					flag = true;
					Log(ex);
					try
					{
						File.Copy(FileName, FileName + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: false);
					}
					catch
					{
					}
				}
			}
			if (preferences == null)
			{
				foreach (string item in RecoveryPaths())
				{
					if (File.Exists(item))
					{
						try
						{
							bytes = File.ReadAllBytes(item);
							preferences = ReadLayout(bytes);
							RecoveryMessage = "已从安全副本恢复布局。可在设置 → 通用 → 找回旧版入口中检查历史条目。";
						}
						catch (Exception ex2)
						{
							flag = true;
							Log(ex2);
							continue;
						}
						break;
					}
				}
			}
			if (preferences == null)
			{
				if (flag)
				{
					throw new InvalidDataException("配置和安全副本均无法读取，原文件已保留。请使用完整备份恢复，避免覆盖原配置。配置目录：" + Folder);
				}
				preferences = new Preferences();
			}
			else if (preferences.FormatVersion < 2)
			{
				ArchiveUpgrade(bytes);
			}
			if ((RecoveryMessage != null) & mergeJournal)
			{
				MergeRecoveryJournal(preferences);
			}
			preferences.Normalize();
			return preferences;
		}
	}

	private static void MergeRecoveryJournal(Preferences prefs)
	{
		try
		{
			string path = Path.Combine(Folder, "removed.json");
			if (!File.Exists(path))
			{
				return;
			}
			using FileStream stream = File.OpenRead(path);
			RemovalJournal journal = (RemovalJournal)new DataContractJsonSerializer(typeof(RemovalJournal)).ReadObject((Stream)stream);
			if (journal != null)
			{
				journal.Merge(prefs);
				prefs.Apps.RemoveAll((Entry a) => !(journal.ActivePaths ?? new List<string>()).Contains(a.Path, StringComparer.OrdinalIgnoreCase) && RemovalRules.Blocked(prefs, a));
			}
		}
		catch (Exception ex)
		{
			Log(ex);
		}
	}

	public static void Save(Preferences p)
	{
		lock (saveLock)
		{
			Directory.CreateDirectory(Folder);
			p.FormatVersion = 2;
			RemovalRules.ReconcileActive(p);
			byte[] array = Encode(p);
			ReadLayout(array);
			if (lastSaved != null && array.SequenceEqual(lastSaved))
			{
				return;
			}
			string backup = null;
			if (File.Exists(FileName))
			{
				try
				{
					ReadLayout(File.ReadAllBytes(FileName));
					backup = FileName + ".previous";
				}
				catch
				{
				}
			}
			Atomic(FileName, array, backup);
			lastSaved = array;
			try
			{
				Atomic(FileName + ".last-good", array);
			}
			catch (Exception ex)
			{
				Log(ex);
			}
			try
			{
				Atomic(Path.Combine(Folder, "removed.json"), Encode(new RemovalJournal
				{
					Paths = p.Removed.ToList(),
					Identities = p.RemovedIdentities.ToList(),
					ActivePaths = p.Apps.Select((Entry a) => a.Path).ToList()
				}));
			}
			catch (Exception ex2)
			{
				Log(ex2);
			}
			string text;
			try
			{
				text = BoardEditState.Capture(p).Signature();
			}
			catch (Exception ex4)
			{
				Log(ex4);
				return;
			}
			if (!(text != lastInventory) && !((DateTime.UtcNow - lastSnapshot).TotalSeconds >= 60.0))
			{
				return;
			}
			try
			{
				string text2 = Path.Combine(Folder, "snapshots");
				Directory.CreateDirectory(text2);
				Atomic(Path.Combine(text2, "layout-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json"), array);
				lastSnapshot = DateTime.UtcNow;
				lastInventory = text;
				foreach (string item in Directory.GetFiles(text2, "layout-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(30))
				{
					File.Delete(item);
				}
			}
			catch (Exception ex3)
			{
				Log(ex3);
			}
		}
	}

	private static Preferences ReadBackupLayout(string path)
	{
		using FileStream stream = File.OpenRead(path);
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		List<ZipArchiveEntry> list = zipArchive.Entries.Where((ZipArchiveEntry e) => e.FullName == "layout.json").ToList();
		if (list.Count != 1 || list[0].Length > 8388608)
		{
			throw new InvalidDataException("备份布局不完整或过大");
		}
		using Stream stream2 = list[0].Open();
		using MemoryStream memoryStream = new MemoryStream();
		byte[] array = new byte[16384];
		int num;
		while ((num = stream2.Read(array, 0, array.Length)) > 0)
		{
			if (memoryStream.Length + num > 8388608)
			{
				throw new InvalidDataException("备份布局过大");
			}
			memoryStream.Write(array, 0, num);
		}
		return ReadLayout(memoryStream.ToArray());
	}

	public static List<Entry> HistoricalEntries()
	{
		List<Entry> list = new List<Entry>();
		Directory.CreateDirectory(Folder);
		foreach (string item in RecoveryPaths().ToList().Distinct(StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				if (File.Exists(item))
				{
					Preferences preferences = ReadLayout(File.ReadAllBytes(item));
					list.AddRange(preferences.Apps.Where((Entry a) => a != null && !string.IsNullOrWhiteSpace(a.Path)).Select(FeatureRules.Copy));
				}
			}
			catch
			{
			}
		}
		string path = Path.Combine(Folder, "backups");
		if (Directory.Exists(path))
		{
			foreach (string item2 in Directory.GetFiles(path, "*.gbbackup").OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
			{
				try
				{
					Preferences preferences2 = ReadBackupLayout(item2);
					list.AddRange(preferences2.Apps.Where((Entry a) => a != null && !string.IsNullOrWhiteSpace(a.Path)).Select(FeatureRules.Copy));
				}
				catch
				{
				}
			}
		}
		return EntryRules.NormalizeEntries(list);
	}
}
