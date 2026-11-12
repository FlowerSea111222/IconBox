using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IconBox;

public class Controller
{
	private sealed class ColorOwner : System.Windows.Forms.IWin32Window
	{
		public IntPtr Handle { get; private set; }

		public ColorOwner(Window w)
		{
			Handle = new WindowInteropHelper(w).Handle;
		}
	}

	public Preferences Prefs;

	public DockWindow Dock;

	public BoardWindow Board;

	public NotifyIcon Tray;

	private bool importing;

	public bool ShuttingDown;

	private readonly bool persistConfiguration;

	private bool settingsOpen;

	private Window settingsWindow;

	private System.Windows.Controls.TabControl settingsTabs;

	private bool removalMigrated;

	private bool glassSuppressed;

	private readonly BoardEditHistory editHistory = new BoardEditHistory();

	// 面板内容变化后延迟同步桌面（隐藏/恢复已归类图标），合并连续的增删操作。
	private readonly DispatcherTimer desktopCleanSync = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(900.0)
	};


	public ControlCenterWindow ControlCenter;

	public bool CanUndoRemoval => editHistory.CanUndo;

	public bool GlassSuppressed => glassSuppressed;

	private static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

	public bool CanRedoBoard => editHistory.CanRedo;

	public string UndoLabel => editHistory.UndoLabel;

	public bool UiBusy
	{
		get
		{
			if (!settingsOpen)
			{
				return Board.ModalBusy;
			}
			return true;
		}
	}

	private Window OwnerWindow
	{
		get
		{
			if (!Board.IsVisible)
			{
				return Dock;
			}
			return Board;
		}
	}

	public Controller()
		: this(desktop: true)
	{
	}

	internal Controller(bool desktop)
	{
		persistConfiguration = desktop;
		if (!desktop)
		{
			Prefs = new Preferences();
			Board = new BoardWindow(this);
			Dock = new DockWindow(this);
			return;
		}
		Prefs = Store.Load();
		// 静态对话框（FeatureUi.Dialog）没有 Controller 引用，这里把主题同步过去。
		FeatureUi.Light = Prefs.Light;
		AppCategoryRules.Initialize(Prefs.Categories);
		desktopCleanSync.Tick += delegate
		{
			desktopCleanSync.Stop();
			RunDesktopCleanSync();
		};
		Store.Trace("START phase=prefs ms=" + (Environment.TickCount - Program.ProcessStartTick));
		// 关机/注销时窗口被系统销毁会触发 Closed 事件；标记正在退出，防止组件被误判为"用户关闭"而全部禁用
		SystemEvents.SessionEnding += (object s, SessionEndingEventArgs e) =>
		{
			ShuttingDown = true;
		};
		if (!string.IsNullOrEmpty(Store.RecoveryMessage))
		{
			System.Windows.MessageBox.Show(Store.RecoveryMessage, "IconBox 配置恢复");
		}
		if (Prefs.Removed.Count > 0 || Prefs.RemovedIdentities.Count > 0)
		{
			MigrateRemovalIdentities();
		}
		Board = new BoardWindow(this);
		Dock = new DockWindow(this);
		Store.Trace("START phase=windows ms=" + (Environment.TickCount - Program.ProcessStartTick));
		try
		{
			// 渲染环境诊断：后台采集一次 OS/渲染层级/显示器 DPI/GPU（WMI 较慢，不阻塞启动）
			System.Threading.Tasks.Task.Run((Action)RenderDiagnostics.DumpOnce);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		DesktopWatcher.Start(this);
		Store.Trace("START phase=watcher ms=" + (Environment.TickCount - Program.ProcessStartTick));
		EnsureSystemEntries();
		ApplyShellPreferences();
		ApplyDesktopClean();
		Store.Trace("START phase=shell ms=" + (Environment.TickCount - Program.ProcessStartTick));
		ApplyGlobalStyle();
		Store.Trace("START phase=style ms=" + (Environment.TickCount - Program.ProcessStartTick));
		DispatcherTimer memoryTrimTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(90)
		};
		memoryTrimTimer.Tick += delegate
		{
			try
			{
				if (!UiBusy && !Board.IsVisible && ControlCenter == null && settingsWindow == null && (Dock.ContextMenu == null || !Dock.ContextMenu.IsOpen))
				{
					GC.Collect(2, GCCollectionMode.Optimized, false);
					Native.TrimWorkingSet();
				}
			}
			catch (Exception ex2)
			{
				Store.Log(ex2);
			}
		};
		memoryTrimTimer.Start();
		glassSuppressed = Prefs.GameMode || (Prefs.AutoGameMode && FullscreenState.Active());
		Tray = new NotifyIcon
		{
			Text = "IconBox v1.0.33 · 玻璃启动台",
			Icon = (Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location) ?? SystemIcons.Application),
			Visible = true
		};
		RefreshTrayMenu();
		Store.Trace("START phase=tray ms=" + (Environment.TickCount - Program.ProcessStartTick));
		Tray.DoubleClick += (object s, EventArgs e) =>
		{
			Toggle(force: true);
		};
		// 先完成 Dock 的首帧显示。更新检查、通知监听、桌面同步与组件恢复都放到
		// Dispatcher 空闲时执行，避免把开机启动的首个可见画面压在非关键初始化后面。
		// 启动时顺手清掉「文件已被删除」的失效条目（否则面板里会一直留着方块占位图标）。
		if (PruneMissingEntries() > 0)
		{
			Save();
		}
		Dock.Refresh();
		if (!GlassSuppressed)
		{
			Dock.Show();
		}
		Dock.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(CompleteDeferredStartup));
	}

	private void CompleteDeferredStartup()
	{
		try
		{
			TryAutoUpdate();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		try
		{
			// 应用通知红点：监听系统通知（Win10/11）。无权限或系统不支持时功能自动停用并记日志。
			NotificationCenter.Start(this);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		try
		{
			// 微信专用通知检测（通知气泡 WinEvent Hook），与系统通知监听互补，互不干扰。
			WeChatNotifier.Start(this);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		Store.Trace("START phase=services ms=" + (Environment.TickCount - Program.ProcessStartTick));
		if (Prefs.HotkeyMigrationConflict)
		{
			Tray.ShowBalloonTip(4500, "快捷键升级提示", "原有组合与 Ctrl+Alt+H 冲突，已保留原设置并关闭新增项。可在设置中录制新组合。", ToolTipIcon.Info);
			Save();
		}
		if (!Prefs.Imported)
		{
			Toggle(force: true);
			Import();
		}
		else
		{
			// 已导入过的老用户：每次启动补收桌面新增的文件夹/图片/文档（已存在的跳过，已移除的不再加回）
			SyncDesktop();
		}
		ApplyGameMode();
		WarmIconCache();
	}

	// —— 图标缓存预热：大面板首次打开慢，是因为几十个磁贴的图标解码/提取全部堆在
	// 首次打开的那一瞬间。现在把与面板完全相同的图标管线（MediaThumb.TileIcon）整批
	// 交给 IconPipeline 后台线程提取，填满内存+磁盘缓存，完全不占用 UI 线程。
	private void WarmIconCache()
	{
		try
		{
			List<Entry> list = new List<Entry>();
			foreach (Entry a in Prefs.Apps)
			{
				if (a != null && !list.Any((Entry e) => string.Equals(e.Path, a.Path, StringComparison.OrdinalIgnoreCase)))
				{
					list.Add(a);
				}
			}
			list.Add(RecycleBin.Create());
			list.Add(SystemPlaces.CreateComputer());
			if (list.Count == 0)
			{
				return;
			}
			Store.Trace("WARM icons start n=" + list.Count);
			IconPipeline.Warm(list, Prefs.Dock.ShowShortcutOverlays);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private string Shortcut(int id)
	{
		return HotkeyRules.Label(Prefs.Hotkeys.First((HotkeySetting k) => k.ActionId == id));
	}

	// 开机启动后自动应用用户保存的桌面开关状态（隐藏任务栏 / 隐藏桌面图标 / 任务栏自动隐藏）
	private void ApplyShellPreferences()
	{
		try
		{
			if (Prefs.HideTaskbar)
			{
				try
				{
					if (TaskbarAutoHide.Enabled)
					{
						TaskbarAutoHide.Set(enabled: false);
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
				DesktopVisibility.SetHidden(taskbar: true, hide: true);
			}
			else if (Prefs.AutoHideTaskbar)
			{
				DesktopVisibility.SetHidden(taskbar: true, hide: false);
				TaskbarAutoHide.Set(enabled: true);
			}
			if (Prefs.HideDesktopIcons)
			{
				DesktopVisibility.SetHidden(taskbar: false, hide: true);
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 自动清理桌面：把已被面板收录的桌面图标隐藏起来（改隐藏属性，不删文件）。
	public void SetDesktopClean(bool enabled)
	{
		try
		{
			Prefs.AutoCleanDesktopIcons = enabled;
			Save();
			if (enabled)
			{
				int num = DesktopCleaner.Apply(Prefs.Apps, Prefs.AutoCleanFiles);
				Board.SetStatus(num > 0 ? ("已从桌面隐藏 " + num + " 个已收录的图标") : "桌面已是干净的：没有需要隐藏的已收录图标");
			}
			else
			{
				int num2 = DesktopCleaner.Restore();
				Board.SetStatus(num2 > 0 ? ("已恢复 " + num2 + " 个桌面图标") : "没有需要恢复的桌面图标");
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 设置页 / 托盘：把桌面图标全部恢复回来。
	// 顺手把「自动清除」关掉，否则下一次同步（面板增删图标后 900ms）又会把它们藏起来，
	// 用户会以为恢复没生效。
	public void RestoreDesktopIcons()
	{
		try
		{
			bool flag = Prefs.AutoCleanDesktopIcons;
			Prefs.AutoCleanDesktopIcons = false;
			Save();
			int num = DesktopCleaner.RestoreAll(Prefs.Apps);
			// 面板里收录、桌面上却已经没有的软件，顺手补一个桌面快捷方式回来。
			int num2 = DesktopCleaner.PlaceOnDesktop(Prefs.Apps);
			string text = ((num > 0) ? ("已恢复 " + num + " 个桌面图标") : "没有被隐藏的桌面图标");
			if (num2 > 0)
			{
				text = text + "，并把 " + num2 + " 个面板软件放回桌面";
			}
			if (flag)
			{
				text += "；已关闭「自动清除桌面图标」";
			}
			Board.SetStatus(text);
			// 恢复桌面图标后刷新面板里的软件图标，避免继续显示失效状态。
			Board.RefreshIcons();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 面板内容变化后，把桌面同步成「只留没归类的东西」。
	// 必须在 Dispatcher 上执行，并且用防抖合并连续的增删操作。
	public void ScheduleDesktopCleanSync()
	{
		if (!Prefs.AutoCleanDesktopIcons)
		{
			return;
		}
		try
		{
			desktopCleanSync.Stop();
			desktopCleanSync.Start();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private void RunDesktopCleanSync()
	{
		try
		{
			if (Prefs.AutoCleanDesktopIcons)
			{
				DesktopCleaner.Sync(Prefs.Apps, Prefs.AutoCleanFiles);
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 手动执行一次「立即同步」（设置页按钮）：不改变开关状态，只按当前开关值跑一遍。
	public void SyncDesktopCleanNow()
	{
		try
		{
			if (!Prefs.AutoCleanDesktopIcons)
			{
				Board.SetStatus("请先打开「自动清除桌面图标」开关");
				return;
			}
			int num = DesktopCleaner.Sync(Prefs.Apps);
			int restored = DesktopCleaner.LastSyncRestored;
			if (num > 0 && restored > 0)
			{
				Board.SetStatus("已同步：新隐藏 " + num + " 个，恢复 " + restored + " 个图标");
			}
			else if (num > 0)
			{
				Board.SetStatus("已同步：本次新隐藏 " + num + " 个图标");
			}
			else if (restored > 0)
			{
				Board.SetStatus("已同步：恢复显示 " + restored + " 个已移出面板的图标");
			}
			else
			{
				Board.SetStatus("桌面与面板已经一致，无需处理");
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 启动时按开关状态执行一次；开着就补一遍（新收录的图标也一并隐藏）。
	private void ApplyDesktopClean()
	{
		try
		{
			DesktopCleaner.LoadHistory();
			if (!Prefs.AutoCleanDesktopIcons)
			{
				return;
			}
			System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(delegate
			{
				try
				{
					int num = DesktopCleaner.Apply(Prefs.Apps, Prefs.AutoCleanFiles);
					if (num > 0)
					{
						Store.Trace("DESKTOP clean startup n=" + num);
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			}));
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	public void RefreshTrayMenu()
	{
		ContextMenuStrip contextMenuStrip = Tray.ContextMenuStrip;
		ContextMenuStrip contextMenuStrip2 = new ContextMenuStrip();
		contextMenuStrip2.Items.Add("打开应用面板   " + Shortcut(1), null, (object s, EventArgs e) =>
		{
			Toggle(force: true);
		});
		contextMenuStrip2.Items.Add(Prefs.GameMode ? "退出游戏模式" : "进入游戏模式", null, (object s, EventArgs e) =>
		{
			SetGameMode(!Prefs.GameMode);
		});
		if (Prefs.AutoGameMode)
		{
			contextMenuStrip2.Items.Add("暂停自动游戏模式", null, (object s, EventArgs e) =>
			{
				Prefs.AutoGameMode = false;
				Prefs.GameMode = false;
				Save();
				ApplyGameMode();
				RefreshTrayMenu();
			});
		}
		contextMenuStrip2.Items.Add("添加软件   " + Shortcut(4), null, (object s, EventArgs e) =>
		{
			AddFromAnywhere();
		});
		contextMenuStrip2.Items.Add("导入开始菜单应用", null, (object s, EventArgs e) =>
		{
			Import();
		});
		contextMenuStrip2.Items.Add("外观与快捷键   " + Shortcut(5), null, (object s, EventArgs e) =>
		{
			Settings();
		});
		contextMenuStrip2.Items.Add("隐藏 / 显示任务栏   " + Shortcut(2), null, (object s, EventArgs e) =>
		{
			ShellAction(1);
		});
		contextMenuStrip2.Items.Add("隐藏 / 显示桌面图标   " + Shortcut(3), null, (object s, EventArgs e) =>
		{
			ShellAction(2);
		});
		contextMenuStrip2.Items.Add("恢复任务栏与桌面图标   " + Shortcut(6), null, (object s, EventArgs e) =>
		{
			ShellAction(3);
		});
		contextMenuStrip2.Items.Add("恢复被收纳的桌面图标", null, (object s, EventArgs e) =>
		{
			RestoreDesktopIcons();
		});
		contextMenuStrip2.Items.Add("Windows 任务栏自动隐藏   " + Shortcut(7), null, (object s, EventArgs e) =>
		{
			ToggleTaskbarAutoHide();
		});
		contextMenuStrip2.Items.Add("外观方案", null, (object s, EventArgs e) =>
		{
			ManageSchemes();
		});
		contextMenuStrip2.Items.Add("打开配置文件夹", null, (object s, EventArgs e) =>
		{
			Process.Start(new ProcessStartInfo(Store.Folder)
			{
				UseShellExecute = true
			});
		});
		contextMenuStrip2.Items.Add("检查更新", null, async (object s, EventArgs e) =>
		{
			await OnlineUpdate.CheckManualAsync(null);
		});
		contextMenuStrip2.Items.Add("测试微信通知红点（调试）", null, (object s, EventArgs e) =>
		{
			WeChatNotifier.TestBadge();
		});
		contextMenuStrip2.Items.Add(new ToolStripSeparator());
		contextMenuStrip2.Items.Add("退出 IconBox", null, (object s, EventArgs e) =>
		{
			Quit();
		});
		Tray.ContextMenuStrip = contextMenuStrip2;
		contextMenuStrip?.Dispose();
	}

	// 启动后自动检查更新（每天最多一次）；发现新版本只托盘提醒，下载由用户触发
	private async void TryAutoUpdate()
	{
		try
		{
			long now = DateTime.UtcNow.ToFileTimeUtc();
			// 关闭「自动检查更新」时不要在启动阶段发起任何网络请求：
			// 未签名程序每次启动就连外网，Windows 防火墙/安全软件会反复询问。
			if (!Prefs.AutoCheckUpdates)
			{
				return;
			}
			if (now - Prefs.LastUpdateCheckUtc < 864000000000L)
			{
				return;
			}
			Prefs.LastUpdateCheckUtc = now;
			Save();
			OnlineUpdate.ReleaseInfo update = await OnlineUpdate.LatestAvailableAsync();
			if (update != null)
			{
				string summary = OnlineUpdate.ReleaseNotesSummary(update, 200);
				Tray.ShowBalloonTip(6000, "发现新版本 v" + update.TagName.TrimStart('v', 'V'), string.IsNullOrEmpty(summary) ? "设置 → 通用 → 检查更新，一键升级到最新版。" : summary, ToolTipIcon.Info);
			}
		}
		catch
		{
		}
	}

	public void Save()
	{
		if (!persistConfiguration)
		{
			return;
		}
		try
		{
			DockOrderRules.Normalize(Prefs);
			Store.Save(Prefs);
			lastSaveFailure = null;
		}
		catch (Exception ex)
		{
			ReportSaveFailure(ex);
		}
	}

	// 保存失败不再弹模态对话框（拖动滑块时会连续弹很多次），改成非阻塞的状态栏提示 + 落盘记录
	private string lastSaveFailure;

	private DateTime lastSaveFailureAt;

	private void ReportSaveFailure(Exception ex)
	{
		Store.Log(ex);
		string text = ex.GetType().Name + "：" + ex.Message;
		if (ex.InnerException != null)
		{
			text = text + " / " + ex.InnerException.Message;
		}
		if (text == lastSaveFailure && (DateTime.UtcNow - lastSaveFailureAt).TotalSeconds < 8.0)
		{
			return;
		}
		lastSaveFailure = text;
		lastSaveFailureAt = DateTime.UtcNow;
		Store.WriteFailureReport("布局保存失败：" + text);
		try
		{
			Board.SetStatus("布局保存失败：" + text);
		}
		catch
		{
		}
	}

	public void Refresh()
	{
		int num = PruneMissingEntries();
		Save();
		Dock.Refresh();
		Board.Render();
		// 面板内容变了就让桌面跟上：已收录的隐藏、被移出的恢复。
		ScheduleDesktopCleanSync();
		if (num > 0)
		{
			Board.SetStatus("已清理 " + num + " 个失效条目");
		}
	}

	// 清理「文件 / 文件夹已被删除」的失效条目：只处理真实磁盘路径，且要求所在目录仍然存在
	// （盘符没挂上、目录被改名时不动，避免把移动硬盘里的条目误删）。
	private int PruneMissingEntries()
	{
		int num = 0;
		for (int i = Prefs.Apps.Count - 1; i >= 0; i--)
		{
			Entry entry = Prefs.Apps[i];
			if (entry == null)
			{
				Prefs.Apps.RemoveAt(i);
				num++;
				continue;
			}
			string path = entry.Path;
			if (string.IsNullOrWhiteSpace(path) || path.StartsWith("::", StringComparison.Ordinal) || !Path.IsPathRooted(path))
			{
				continue;
			}
			if (File.Exists(path) || Directory.Exists(path))
			{
				continue;
			}
			string parent = null;
			try
			{
				parent = Path.GetDirectoryName(path);
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
			{
				continue;
			}
			Prefs.Apps.RemoveAt(i);
			num++;
		}
		if (num > 0)
		{
			Store.Trace("PRUNE missing entries n=" + num);
		}
		return num;
	}

	public void Toggle(bool force = false)
	{
		if (!GlassSuppressed)
		{
			// 只有「可见且已经是前台」才算开着；可见但没激活时再点一次应该是把它叫到前面，
			// 否则表现就是「点了没反应 / 反而消失」。
			if (!force && Board.IsVisible && Board.IsActive)
			{
				Board.CollapseSmooth();
			}
			else
			{
				Board.Open();
				try
				{
					Board.Activate();
				}
				catch
				{
				}
			}
		}
	}

	public void ToggleControlCenter()
	{
		if (!GlassSuppressed)
		{
			Dock.CloseRunningList();
			if (ControlCenter == null)
			{
				ControlCenter = new ControlCenterWindow(this);
			}
			ControlCenter.Toggle();
		}
	}

	public void ApplyGlobalStyle()
	{
		GlobalStyleOptions g = Prefs.GlobalStyle;
		g.Normalize();
		if (!g.Unify)
		{
			return;
		}
		Prefs.Dock.Material = g.Material.Copy();
		Prefs.BoardMaterial = g.Material.Copy();
		Prefs.Dock.OpacityPercent = g.OpacityPercent;
		Prefs.OpacityPercent = g.OpacityPercent;
		Prefs.Dock.BlurRadius = g.Blur;
		Prefs.Dock.EdgeStrength = g.Edge;
		Prefs.Dock.Radius = g.Radius;
		Save();
	}

	public void SetSystemIconGlyph(string kind, string glyph)
	{
		if (string.IsNullOrWhiteSpace(kind))
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(glyph))
		{
			if (Prefs.SystemIconGlyphs != null)
			{
				Prefs.SystemIconGlyphs.Remove(kind);
			}
			if (Prefs.SystemIconImages != null)
			{
				Prefs.SystemIconImages.Remove(kind);
			}
		}
		else
		{
			if (Prefs.SystemIconGlyphs == null)
			{
				Prefs.SystemIconGlyphs = new Dictionary<string, string>();
			}
			Prefs.SystemIconGlyphs[kind] = SystemIconActions.GlyphKind(glyph);
			if (Prefs.SystemIconImages != null)
			{
				Prefs.SystemIconImages.Remove(kind);
			}
		}
		Save();
		Dock.Refresh(capture: false);
	}

	public void ChooseSystemIconImage(string kind)
	{
		if (string.IsNullOrWhiteSpace(kind))
		{
			return;
		}
		Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "图片与图标|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.gif;*.svg|SVG 矢量图标|*.svg|所有文件|*.*",
			Title = "选择图标图片（支持 PNG/JPG/BMP/ICO/GIF/SVG）"
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			string stored;
			if (dialog.FileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
			{
				// WPF 不能直接解码 SVG：先栅格化为 128px PNG 再使用
				stored = SvgIconConverter.TryConvert(dialog.FileName, 128);
				if (stored == null)
				{
					System.Windows.MessageBox.Show("无法解析该 SVG 文件（仅支持标准矢量路径/矩形/圆形/线条）。\n\n建议使用图标库导出的 PNG 版本。", "IconBox");
					return;
				}
			}
			else
			{
				stored = FeatureUi.StoreIcon(dialog.FileName);
			}
			Prefs.SystemIconImages[kind] = stored;
			Prefs.SystemIconGlyphs.Remove(kind);
			Save();
			Dock.Refresh(capture: false);
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("无法加载该图片。\n\n" + ex.Message, "IconBox");
		}
	}

	public void Launch(Entry entry)
	{
		try
		{
			// 用户点击图标 = 已看到该应用：按任务栏惯例熄灭其通知红点。
			// QQ 等应用把已读通知长期留在通知中心，不能只靠系统事件熄灭。
			try
			{
				NotificationCenter.ClearEntry(entry?.Path ?? "");
			}
			catch
			{
			}
			if (SystemPlaces.IsComputer(entry))
			{
				Native.Computer();
				Board.CollapseSmooth();
				return;
			}
			if (RecycleBin.IsEntry(entry))
			{
				Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
				{
					UseShellExecute = true
				});
				Board.CollapseSmooth();
				return;
			}
			if (entry.IsFolder || Directory.Exists(entry.Path) || !RunningWindows.Activate(entry))
			{
				// 冷却保护：启动器类入口（汽水音乐等）主程序窗口与入口路径不同，
				// 激活判定失败但 12 秒内刚启动过且进程仍在 → 不再重复拉起。
				// 防连点保护：只挡「2.5 秒内的重复点击」，避免把小概率的连击变成两个实例。
				// 原来沿用 12 秒冷却，导致「进程在后台、窗口没起来」的应用点击后被静默吞掉
				// （用户看到的就是「图标点了没反应」）。
				if (RunningWindows.RecentlyLaunched(entry.Path, 2500) && RunningWindows.ActiveLaunchPids(entry.Path).Count > 0)
				{
					Store.Trace("LAUNCH cooldown path=" + entry.Path);
					Board.CollapseSmooth();
					return;
				}
				Process process = Process.Start(new ProcessStartInfo(entry.Path)
				{
					UseShellExecute = true
				});
				Store.Trace("LAUNCH start path=" + entry.Path + " pid=" + ((process == null) ? 0 : process.Id));
				// 记录本次启动的进程：窗口路径匹配失败（提权/UWP/启动器转主程序）时，
				// 用它兜底实现"再点 Dock 图标最小化"。
				if (process != null)
				{
					try
					{
						RunningWindows.NoteLaunch(entry.Path, process.Id, process.ProcessName);
					}
					catch
					{
					}
				}
			}
			Board.CollapseSmooth();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			Store.Trace("LAUNCH failed path=" + ((entry == null) ? "" : entry.Path) + " err=" + ex.Message);
			System.Windows.MessageBox.Show("无法打开「" + entry.Name + "」。\n入口可能已移动或卸载。\n\n" + ex.Message, "IconBox");
		}
	}

	public void Add(IEnumerable<string> paths, bool manual = true)
	{
		if (!manual)
		{
			MigrateRemovalIdentities();
		}
		BoardEditState before = BeginBoardEdit();
		int num = 0;
		foreach (string path2 in paths)
		{
			string path = EntryPathRules.Normalize(path2);
			if ((!File.Exists(path) && !Directory.Exists(path)) || !LayoutRules.AllowImport(path, Prefs.Removed, manual))
			{
				continue;
			}
			Entry entry = Prefs.Apps.FirstOrDefault((Entry a) => string.Equals(EntryPathRules.Normalize(a.Path), path, StringComparison.OrdinalIgnoreCase));
			if (entry != null)
			{
				continue;
			}
			Entry entry2 = new Entry
			{
				Name = EntryPathRules.Name(path),
				Path = path,
				IsFolder = Directory.Exists(path)
			};
			LaunchInfoService.Enrich(entry2);
			// 手动添加也要挡住「同一应用的不同快捷方式」：扫描列表已按目标 exe 去重，这里是兜底。
			if (manual && Prefs.Apps.Any((Entry a) => RemovalRules.Same(a, entry2)))
			{
				continue;
			}
			// 非手动导入（桌面读取）也收录文件夹与文档/媒体文件——桌面文件夹、图片等都能进面板
			if (manual || ((LaunchInfoService.Primary(entry2) || entry2.IsFolder || DocumentRules.Supported(entry2.Path)) && !RemovalRules.Blocked(Prefs, entry2) && !Prefs.Apps.Any((Entry a) => RemovalRules.Same(a, entry2))))
			{
				if (manual)
				{
					RemovalRules.Forget(Prefs, entry2);
				}
				Prefs.Apps.Add(entry2);
				num++;
			}
		}
		if (num > 0)
		{
			EndBoardEdit(before, "添加入口");
			Refresh();
		}
	}

	public void Choose()
	{
		Board.ModalBusy = true;
		try
		{
			new BatchAddWindow(this).ShowDialog();
		}
		finally
		{
			Board.ModalBusy = false;
		}
	}

	public void AddFromAnywhere()
	{
		Toggle(force: true);
		Choose();
	}

	public void Opacity(double percent)
	{
		Prefs.OpacityPercent = LayoutRules.GlassOpacity(percent);
		Board.SetGlassOpacity(Prefs.OpacityPercent.Value);
	}

	public void Remove(Entry a)
	{
		RemoveMany(new string[1] { a.Path });
	}

	// 开机时把回收站入口补进面板；用户手动移除（移除记录仍在）后不再补回
	private void EnsureSystemEntries()
	{
		try
		{
			EnsureOne(RecycleBin.Create(), RecycleBin.Clsid);
			EnsureOne(SystemPlaces.CreateComputer(), SystemPlaces.ComputerClsid);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 系统入口（回收站/此电脑）默认加入面板；用户手动移除过的不再自动加回
	private void EnsureOne(Entry entry, string key)
	{
		bool exists = Prefs.Apps.Any((Entry a) => string.Equals(a.Path, key, StringComparison.OrdinalIgnoreCase));
		bool removed = Prefs.Removed.Any((string r) => string.Equals(r, key, StringComparison.OrdinalIgnoreCase));
		if (!exists && !removed)
		{
			Prefs.Apps.Add(entry);
			if (persistConfiguration)
			{
				Save();
			}
		}
	}

	// 拖文件到回收站：送入系统回收站（可恢复）
	public int DeleteToRecycleBin(string[] paths)
	{
		int num = RecycleBin.Delete(paths);
		Board.SetStatus((num > 0) ? ("已删除 " + num + " 项到回收站，可在回收站恢复") : "未找到可删除的文件");
		return num;
	}

	public void RemoveMany(IEnumerable<string> paths)
	{
		HashSet<string> targets = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
		targets.RemoveWhere((string path) => !Prefs.Apps.Any((Entry a) => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)));
		int num = Prefs.Apps.Count((Entry a) => targets.Contains(a.Path));
		if (num == 0)
		{
			return;
		}
		Board.ModalBusy = true;
		try
		{
			string messageBoxText = "移除已选的 " + num + " 个入口？\n本次从面板与 Dock 移除，原软件和磁盘文件保留。\n移除后可点击「撤销」。";
			if ((Board.IsVisible ? System.Windows.MessageBox.Show(Board, messageBoxText, "IconBox", MessageBoxButton.YesNo) : System.Windows.MessageBox.Show(messageBoxText, "IconBox", MessageBoxButton.YesNo)) != MessageBoxResult.Yes)
			{
				return;
			}
			foreach (Entry item in Prefs.Apps.Where((Entry a) => targets.Contains(a.Path)))
			{
				LaunchInfoService.Enrich(item);
			}
			BoardEditState before = BeginBoardEdit();
			EntryRules.Remove(Prefs, targets);
			EndBoardEdit(before, "移除入口");
			Board.FinishSelection();
			Refresh();
			Board.SetStatus("已移除 " + num + " 个入口 · 可撤销本次移除");
		}
		finally
		{
			Board.ModalBusy = false;
		}
	}

	// 图标整理：自动移除指向同一款软件的多余入口（文件分类、文件夹与 Shell 特殊项不参与）。
	public void OrganizeIcons()
	{
		List<Entry> plan = DuplicateRules.Plan(Prefs);
		HashSet<string> targets = new HashSet<string>(plan.Select((Entry a) => a.Path), StringComparer.OrdinalIgnoreCase);
		int num = Prefs.Apps.Count((Entry a) => targets.Contains(a.Path));
		if (num == 0)
		{
			Board.SetStatus("没有发现重复的软件图标");
			return;
		}
		Board.ModalBusy = true;
		try
		{
			string messageBoxText = "发现 " + num + " 个重复的软件图标。\n整理后每款软件只保留一个入口；桌面快捷方式、磁盘上的文件与已安装的软件都不会被删除，「文件」分类不受影响。\n整理后可点击「撤销」。";
			if ((Board.IsVisible ? System.Windows.MessageBox.Show(Board, messageBoxText, "IconBox", MessageBoxButton.YesNo) : System.Windows.MessageBox.Show(messageBoxText, "IconBox", MessageBoxButton.YesNo)) != MessageBoxResult.Yes)
			{
				return;
			}
			BoardEditState before = BeginBoardEdit();
			EntryRules.Remove(Prefs, targets);
			EndBoardEdit(before, "整理图标");
			Board.FinishSelection();
			Refresh();
			Board.SetStatus("已整理 " + num + " 个重复图标 · 可撤销");
		}
		finally
		{
			Board.ModalBusy = false;
		}
	}
	public void UndoRemoval()
	{
		UndoBoard();
	}

	public void Pin(Entry a)
	{
		if (!a.Pinned && Prefs.Apps.Count((Entry x) => x.Pinned) >= 20)
		{
			System.Windows.MessageBox.Show("最多固定 20 个入口。请先取消一个固定。", "IconBox");
			return;
		}
		BoardEditState before = BeginBoardEdit();
		a.Pinned = !a.Pinned;
		EndBoardEdit(before, "调整 Dock 固定");
		Refresh();
	}

	private static IEnumerable<string> Shortcuts(string root)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}
		Stack<string> pending = new Stack<string>();
		pending.Push(root);
		int count = 0;
		while (pending.Count > 0 && count < 2500)
		{
			string path = pending.Pop();
			string[] files;
			string[] dirs;
			try
			{
				files = Directory.GetFiles(path);
				dirs = Directory.GetDirectories(path);
			}
			catch
			{
				continue;
			}
			string[] array = files;
			foreach (string text in array)
			{
				switch (Path.GetExtension(text).ToLowerInvariant())
				{
				case ".lnk":
				case ".appref-ms":
				case ".url":
					if (StartupEntryRules.Candidate(text))
					{
						count++;
						yield return text;
					}
					break;
				}
			}
			string[] array2 = dirs;
			foreach (string text2 in array2)
			{
				try
				{
					if ((File.GetAttributes(text2) & FileAttributes.ReparsePoint) == 0)
					{
						pending.Push(text2);
					}
				}
				catch
				{
				}
			}
		}
	}

	// 桌面图标排序权重：软件/快捷方式最前 → 文件夹 → 文档与媒体 → 其他文件
	private static int ImportRank(string path)
	{
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext == ".lnk" || ext == ".url" || ext == ".appref-ms" || ext == ".exe" || ext == ".bat" || ext == ".cmd")
		{
			return 0;
		}
		if (Directory.Exists(path))
		{
			return 1;
		}
		if (DocumentRules.Supported(path))
		{
			return 2;
		}
		return 3;
	}

	public async void Import()
	{
		if (importing)
		{
			return;
		}
		importing = true;
		Board.SetStatus("正在读取桌面…");
		try
		{
			// 首次导入只读桌面：快捷方式（.lnk/.url/.appref-ms）、文件夹与普通文件全部收录，
			// 排序 = 软件/快捷方式 → 文件夹 → 文档与媒体 → 其他文件，组内按中文拼音排序。
			string[] paths = await Task.Run(() =>
			{
				List<string> collected = new List<string>();
				string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
				string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
				foreach (string root in new string[2] { desktop, commonDesktop }.Where((string r) => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
				{
					if (!Directory.Exists(root))
					{
						continue;
					}
					foreach (string child in Directory.GetFileSystemEntries(root))
					{
						try
						{
							string ext = Path.GetExtension(child).ToLowerInvariant();
							if (ext == ".lnk" || ext == ".appref-ms" || ext == ".url")
							{
								if (StartupEntryRules.Candidate(child))
								{
									collected.Add(child);
								}
							}
							else if (Directory.Exists(child) || File.Exists(child))
							{
								collected.Add(child);
							}
						}
						catch
						{
						}
					}
				}
				return collected.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy((string p) => ImportRank(p))
					.ThenBy((string p) => Path.GetFileNameWithoutExtension(p), StringComparer.Create(new CultureInfo("zh-CN"), ignoreCase: true))
					.ToArray();
			});
			if (ShuttingDown)
			{
				return;
			}
			int count = Prefs.Apps.Count;
			Add(paths, manual: false);
			Prefs.Imported = true;
			if (!Prefs.Apps.Any((Entry a) => a.Pinned))
			{
				string[] common = new string[12]
				{
					"edge", "chrome", "firefox", "微信", "wechat", "qq", "steam", "blender", "maya", "剪映",
					"capcut", "code"
				};
				foreach (Entry item in Prefs.Apps.Where((Entry a) => common.Any((string k) => a.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).Take(7))
				{
					item.Pinned = true;
				}
			}
			Refresh();
			Board.SetStatus("已导入 " + (Prefs.Apps.Count - count) + " 个入口 · 可拖入其他软件");
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			Board.SetStatus("导入失败，可使用右上角添加");
		}
		finally
		{
			importing = false;
		}
	}

	// 每次启动补收桌面新增的文件夹 / 图片 / 文档（幂等：已存在跳过、已移除的不再加回）
	private async void SyncDesktop()
	{
		if (importing)
		{
			return;
		}
		importing = true;
		try
		{
			string[] paths = await Task.Run(() =>
			{
				List<string> collected = new List<string>();
				string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
				string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
				foreach (string root in new string[2] { desktop, commonDesktop }.Where((string r) => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
				{
					if (!Directory.Exists(root))
					{
						continue;
					}
					foreach (string child in Directory.GetFileSystemEntries(root))
					{
						try
						{
							string ext = Path.GetExtension(child).ToLowerInvariant();
							if (ext == ".lnk" || ext == ".url" || ext == ".appref-ms")
							{
								continue;
							}
							if (Directory.Exists(child) || (File.Exists(child) && DocumentRules.Supported(child)))
							{
								collected.Add(child);
							}
						}
						catch
						{
						}
					}
				}
				return collected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			});
			if (ShuttingDown)
			{
				return;
			}
			Add(paths, manual: false);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		finally
		{
			importing = false;
		}
	}

	public System.Windows.Controls.ContextMenu Menu(Entry a, string tint = "", int opacityPercent = 100)
	{
		System.Windows.Controls.ContextMenu menu = MenuTheme.Create(Prefs.Light, Prefs.Dock.Accent, tint, opacityPercent);
		Action<string, Action> action = (string title, Action action2) =>
		{
			System.Windows.Controls.MenuItem menuItem3 = new System.Windows.Controls.MenuItem
			{
				Header = title
			};
			menuItem3.Click += (object s, RoutedEventArgs e) =>
			{
				// 先收起菜单、下一帧再执行动作：动作里弹出的确认框/新窗口若在菜单关闭前
				// 弹出，会被置顶的 ContextMenu 遮住，表现为"点了没反应"。
				menu.IsOpen = false;
				Dock.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(action2));
			};
			menu.Items.Add(menuItem3);
		};
		action("打开", () =>
		{
			Launch(a);
		});
		action("打开文件所在位置", () =>
		{
			try
			{
				FileShellActions.Location(a);
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show(ex.Message, "IconBox");
			}
		});
		action("文件属性", () =>
		{
			try
			{
				FileShellActions.Properties(a.Path);
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show(ex.Message, "IconBox");
			}
		});
		action("系统右键菜单（管理员运行等）", () =>
		{
			Dock.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
			{
				ShellContextMenu.ShowAtCursor(a.Path, Dock);
			}));
		});
		action(a.Pinned ? "取消 Dock 固定" : "固定到 Dock", () =>
		{
			Pin(a);
		});
		action("前移一位", () =>
		{
			int num = Prefs.Apps.IndexOf(a);
			if (num > 0)
			{
				Prefs.Apps.RemoveAt(num);
				Prefs.Apps.Insert(num - 1, a);
				Refresh();
			}
		});
		action("后移一位", () =>
		{
			int num = Prefs.Apps.IndexOf(a);
			if (num < Prefs.Apps.Count - 1)
			{
				Prefs.Apps.RemoveAt(num);
				Prefs.Apps.Insert(num + 1, a);
				Refresh();
			}
		});
		action("修改名称与图标", () =>
		{
			Customize(a);
		});
		action("移除入口", () =>
		{
			Remove(a);
		});
		return menu;
	}

	public System.Windows.Controls.ContextMenu DockMenu(Entry a)
	{
		System.Windows.Controls.ContextMenu menu = MenuTheme.Create(Prefs.Light, Prefs.Dock.Accent, Prefs.Dock.Material.Tint, (int)Prefs.Dock.OpacityPercent);
		// 菜单内容在 Opened 时用后台维护的窗口快照填充——构建全程零 Shell 调用、零同步扫描，右键即开不卡顿
		menu.Opened += (object s, RoutedEventArgs e) =>
		{
			Stopwatch timer = Stopwatch.StartNew();
			FillDockMenu(menu, a);
			timer.Stop();
			Store.Trace("DOCKMENU fill ms=" + timer.ElapsedMilliseconds + " entry=" + a.Name);
		};
		return menu;
	}

	private void FillDockMenu(System.Windows.Controls.ContextMenu menu, Entry a)
	{
		menu.Items.Clear();
		void add(string title, Action action2)
		{
			System.Windows.Controls.MenuItem item = new System.Windows.Controls.MenuItem
			{
				Header = title
			};
			item.Click += (object s, RoutedEventArgs e) =>
			{
				menu.IsOpen = false;
				Dock.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(action2));
			};
			menu.Items.Add(item);
		}

		// 菜单来源按运行状态区分：运行中 → 任务栏式窗口菜单置顶；未运行 → 启动与管理菜单。
		// 文件/快捷方式的 Shell 菜单（管理员运行等）只作为最底部的懒加载子菜单，不再冒充任务栏菜单。
		List<RunningWindow> list = RunningWindows.For(a);
		if (list.Count > 0)
		{
			// —— 任务栏式窗口区（置顶）：一窗一行，行内有 还原/最小化/最大化/关闭 子菜单 ——
			foreach (RunningWindow item in list)
			{
				RunningWindow window = item;
				System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
				{
					Header = WindowLabel(window),
					ToolTip = window.Title
				};
				menuItem.Click += (object ss, RoutedEventArgs ee) =>
				{
					menu.IsOpen = false;
					RunningWindows.ActivateHandle(window.Handle);
				};
				FillWindowOps(menuItem, window);
				menu.Items.Add(menuItem);
			}
			add("关闭所有窗口", () =>
			{
				RunningWindows.CloseAll(a);
			});
			menu.Items.Add(new Separator());
			add("打开新窗口", () =>
			{
				NewInstance(a);
			});
		}
		else
		{
			add("打开", () =>
			{
				Launch(a);
			});
		}
		if (!a.IsFolder && !Directory.Exists(a.Path))
		{
			add("打开文件所在位置", () =>
			{
				try
				{
					FileShellActions.Location(a);
				}
				catch (Exception ex)
				{
					System.Windows.MessageBox.Show(ex.Message, "IconBox");
				}
			});
			add("文件属性", () =>
			{
				try
				{
					FileShellActions.Properties(a.Path);
				}
				catch (Exception ex)
				{
					System.Windows.MessageBox.Show(ex.Message, "IconBox");
				}
			});
		}
		menu.Items.Add(new Separator());
		System.Windows.Controls.MenuItem addNew = new System.Windows.Controls.MenuItem
		{
			Header = "添加程序 / 文件 / 文件夹"
		};
		System.Windows.Controls.MenuItem file = new System.Windows.Controls.MenuItem
		{
			Header = "添加软件 / 文件 / 快捷方式…"
		};
		file.Click += (object s, RoutedEventArgs e) =>
		{
			ChooseDockFiles();
		};
		addNew.Items.Add(file);
		System.Windows.Controls.MenuItem folder = new System.Windows.Controls.MenuItem
		{
			Header = "添加文件夹…"
		};
		folder.Click += (object s, RoutedEventArgs e) =>
		{
			ChooseDockFolder();
		};
		addNew.Items.Add(folder);
		menu.Items.Add(addNew);
		add("修改名称与图标", () =>
		{
			Customize(a);
		});
		menu.Items.Add(new Separator());
		add("从 Dock 移除", () =>
		{
			Pin(a);
		});

		// —— 文件菜单（管理员运行 / 属性 / 杀软扫描等）：懒加载子菜单，位于最底部，与任务栏式菜单严格区分 ——
		System.Windows.Controls.MenuItem shell = new System.Windows.Controls.MenuItem
		{
			Header = "文件菜单（管理员运行等）"
		};
		shell.Items.Add(new System.Windows.Controls.MenuItem
		{
			Header = "正在加载…",
			IsEnabled = false
		});
		shell.SubmenuOpened += (object s, RoutedEventArgs e) =>
		{
			if (shell.Tag != null)
			{
				return;
			}
			shell.Tag = true;
			LoadShellMenu(shell, a.Path);
		};
		menu.Items.Add(shell);
	}

	// 任务栏式「打开新窗口」：绕过激活逻辑，直接启动一个新实例。
	private void NewInstance(Entry entry)
	{
		try
		{
			Process process = Process.Start(new ProcessStartInfo(entry.Path)
			{
				UseShellExecute = true
			});
			if (process != null)
			{
				try
				{
					RunningWindows.NoteLaunch(entry.Path, process.Id, process.ProcessName);
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("无法打开「" + entry.Name + "」。\n入口可能已移动或卸载。\n\n" + ex.Message, "IconBox");
		}
	}

	// 给窗口菜单项挂 还原/最小化/最大化/关闭 子菜单（含快捷键提示样式：纯状态判定，无重活）
	private static void FillWindowOps(System.Windows.Controls.MenuItem menuItem, RunningWindow window)
	{
		void op(string title, Action action2)
		{
			System.Windows.Controls.MenuItem item = new System.Windows.Controls.MenuItem
			{
				Header = title
			};
			item.Click += (object s, RoutedEventArgs e) =>
			{
				action2();
			};
			menuItem.Items.Add(item);
		}
		if (RunningWindows.IsMinimized(window.Handle))
		{
			op("还原", () => RunningWindows.Restore(window.Handle));
		}
		else
		{
			op("最小化", () => RunningWindows.Minimize(window.Handle));
		}
		if (RunningWindows.IsMaximized(window.Handle))
		{
			op("还原", () => RunningWindows.Restore(window.Handle));
		}
		else
		{
			op("最大化", () => RunningWindows.Maximize(window.Handle));
		}
		op("关闭窗口", () => RunningWindows.Close(window.Handle));
	}

	// 首次展开「系统右键菜单」时后台构建原生菜单模型，完成后无缝替换占位项
	private void LoadShellMenu(System.Windows.Controls.MenuItem shell, string path)
	{
		Stopwatch timer = Stopwatch.StartNew();
		IntPtr hwnd = new WindowInteropHelper(Dock).Handle;
		ShellContextMenu.BuildAsync(path, delegate(List<ShellMenuEntry> model)
		{
			timer.Stop();
			Store.Trace("SHELLMENU ready ms=" + timer.ElapsedMilliseconds + " items=" + model.Count + " path=" + path);
			Dock.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
			{
				if (!shell.IsSubmenuOpen)
				{
					return;
				}
				shell.Items.Clear();
				foreach (ShellMenuEntry item in model)
				{
					shell.Items.Add(RenderShellEntry(item, path, hwnd));
				}
			}));
		}, delegate
		{
			timer.Stop();
			Store.Trace("SHELLMENU failed ms=" + timer.ElapsedMilliseconds + " path=" + path);
			Dock.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
			{
				if (!shell.IsSubmenuOpen)
				{
					return;
				}
				shell.Items.Clear();
				shell.Items.Add(new System.Windows.Controls.MenuItem
				{
					Header = "原生菜单不可用",
					IsEnabled = false
				});
			}));
		});
	}

	// 原生菜单模型 → WPF 菜单项（递归子菜单；命令在宿主线程执行）
	private static System.Windows.Controls.Control RenderShellEntry(ShellMenuEntry item, string path, IntPtr hwnd)
	{
		if (item.Separator)
		{
			return new Separator();
		}
		System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
		{
			Header = item.Text,
			IsEnabled = !item.Disabled,
			IsCheckable = item.Checked,
			IsChecked = item.Checked
		};
		if (item.Sub != null && item.Sub.Count > 0)
		{
			foreach (ShellMenuEntry sub in item.Sub)
			{
				menuItem.Items.Add(RenderShellEntry(sub, path, hwnd));
			}
		}
		menuItem.Click += (object s, RoutedEventArgs e) =>
		{
			menuItem.IsEnabled = false;
			ShellContextMenu.InvokeAsync(path, item.Id, hwnd);
		};
		return menuItem;
	}

	internal static ToggleButton VisibilitySwitch()
	{
		// 与 iOS 开关同构：关=中性灰轨道，开=系统蓝轨道，旋钮恒为白色。
		bool light = FeatureUi.Light;
		ToggleButton toggleButton = new ToggleButton
		{
			Width = 50.0,
			Height = 26.0,
			Cursor = System.Windows.Input.Cursors.Hand,
			VerticalAlignment = VerticalAlignment.Center
		};
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.Name = "Track";
		frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(13.0));
		frameworkElementFactory.SetValue(Border.BackgroundProperty, Tokens.Brush(light ? Tokens.Light.FillStrong : Tokens.Dark.FillStrong));
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory2.Name = "Thumb";
		frameworkElementFactory2.SetValue(FrameworkElement.WidthProperty, 20.0);
		frameworkElementFactory2.SetValue(FrameworkElement.HeightProperty, 20.0);
		frameworkElementFactory2.SetValue(FrameworkElement.MarginProperty, new Thickness(3.0));
		frameworkElementFactory2.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
		frameworkElementFactory2.SetValue(Border.CornerRadiusProperty, new CornerRadius(10.0));
		frameworkElementFactory2.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.White);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		ControlTemplate controlTemplate = new ControlTemplate(typeof(ToggleButton))
		{
			VisualTree = frameworkElementFactory
		};
		Trigger trigger = new Trigger
		{
			Property = ToggleButton.IsCheckedProperty,
			Value = true
		};
		trigger.Setters.Add(new Setter(Border.BackgroundProperty, Tokens.AccentBrush(light), "Track"));
		trigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Right, "Thumb"));
		controlTemplate.Triggers.Add(trigger);
		toggleButton.Template = controlTemplate;
		return toggleButton;
	}

	public void Settings(int selectedPage = -1, bool keepPanel = false)
	{
		if (settingsOpen)
		{
			if (settingsWindow != null)
			{
				if (selectedPage >= 0 && settingsTabs != null)
				{
					settingsTabs.SelectedIndex = Math.Max(0, Math.Min(6, selectedPage));
				}
				// 与 Dock 其它图标一致：设置窗口在前台时再点 → 最小化；否则恢复并激活。
				// 显式指定页面（selectedPage>=0）时总是恢复并激活，不做最小化。
				IntPtr handle = new WindowInteropHelper(settingsWindow).Handle;
				if (selectedPage < 0 && handle != IntPtr.Zero && RunningWindows.IsForeground(handle))
				{
					settingsWindow.WindowState = WindowState.Minimized;
					Store.Trace("SETTINGS toggle minimize hwnd=" + handle.ToInt64());
				}
				else
				{
					if (settingsWindow.WindowState == WindowState.Minimized)
					{
						settingsWindow.WindowState = WindowState.Normal;
					}
					settingsWindow.Activate();
					RunningWindows.MarkForeground(handle);
					Store.Trace("SETTINGS toggle activate hwnd=" + handle.ToInt64());
				}
			}
			return;
		}
		settingsOpen = true;
		DispatcherTimer preview = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(100.0)
		};
		DispatcherTimer stateTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(700.0)
		};
		bool flag = false;
		bool cleaned = false;
		Action cleanup = () =>
		{
			Store.Trace("SETTINGS cleanup");
			if (cleaned)
			{
				return;
			}
			cleaned = true;
			settingsOpen = false;
			// 从分类面板打开设置时设置窗被临时置顶，关闭时复位，避免之后从托盘/快捷键打开仍压住别的应用。
			if (settingsWindow != null)
			{
				try { settingsWindow.Topmost = false; } catch { }
			}
			settingsWindow = null;
			settingsTabs = null;
			System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() =>
			{
				try
				{
					GC.Collect(2, GCCollectionMode.Optimized, false);
					Native.TrimWorkingSet();
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			}));
			RunningWindows.SettingsWindowHandle = IntPtr.Zero;
			preview.Stop();
			stateTimer.Stop();
			Dock.PinForPreview(false);
			Board.KeepOpenForSettings = false;
			Save();
			try
			{
				if (!ShuttingDown)
				{
					Dock.Refresh();
				}
			}
			catch (Exception ex2)
			{
				Store.Log(ex2);
			}
		};
		try
		{
			// 走正常收起流程：直接 Hide() 会跳过浮层清理（媒体预览/搜索下拉会残留），
			// 也没有淡出动画。
			// 从分类面板里点进来时（keepPanel）不收面板：设置窗口开着期间它还留在下面（问题 3）。
			if (keepPanel)
			{
				Board.KeepOpenForSettings = true;
			}
			else
			{
				Board.CollapseSmooth();
			}
			// 设置窗口跟随应用主题（此前固定深色，浅色主题下与其余界面割裂）。
			bool settingsLight = Prefs.Light;
			Window w = new Window
			{
				Title = "IconBox · 个性化与控制 v1.0.33",
				Width = Math.Min(940.0, SystemParameters.WorkArea.Width - 40.0),
				Height = Math.Min(790.0, SystemParameters.WorkArea.Height - 50.0),
				MinWidth = 560.0,
				ResizeMode = ResizeMode.CanResize,
				WindowStartupLocation = WindowStartupLocation.CenterScreen,
				Background = Tokens.Surface(settingsLight),
				Foreground = Tokens.Label(settingsLight)
			};
			settingsWindow = w;
			w.Closed += (object s, EventArgs e) =>
			{
				cleanup();
			};
			Rect workArea = SystemParameters.WorkArea;
			double[] array = SettingsPlacementRules.Vertical(workArea.Top, workArea.Height, Dock.RestingTop, Dock.Height, 790.0);
			w.WindowStartupLocation = WindowStartupLocation.Manual;
			w.Left = workArea.Left + (workArea.Width - w.Width) / 2.0;
			w.Top = array[0];
			w.Height = array[1];
			Polish.Apply(w, settingsLight);
			Grid grid = new Grid
			{
				Margin = new Thickness(0.0)
			};
			// 两列：左侧固定的侧边栏（导航胶囊）+ 右侧内容区。顶部标题横跨两列。
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(232.0)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
			grid.RowDefinitions.Add(new RowDefinition());
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = GridLength.Auto
			});
			w.Content = grid;
			StackPanel header = new StackPanel
			{
				Margin = new Thickness(28.0, 26.0, 28.0, 0.0)
			};
			header.Children.Add(new TextBlock
			{
				Text = "把桌面，变成你的空间",
				FontFamily = Tokens.Type.Display,
				FontSize = 26.0,
				FontWeight = Tokens.Type.SemiBold,
				Foreground = Tokens.Label(settingsLight),
				Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
			});
			header.Children.Add(new TextBlock
			{
				Text = "ICONBOX  /  个性化与桌面控制",
				Foreground = Tokens.LabelSecondary(settingsLight),
				FontSize = Tokens.Type.Callout,
				Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
			});
			Grid.SetColumnSpan(header, 2);
			grid.Children.Add(header);
			System.Windows.Controls.TabControl tabs = new System.Windows.Controls.TabControl
			{
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				TabStripPlacement = System.Windows.Controls.Dock.Left,
				Margin = new Thickness(18.0, 12.0, 18.0, 0.0),
				Padding = new Thickness(0.0, 6.0, 0.0, 0.0)
			};
			Grid.SetRow(tabs, 1);
			Grid.SetColumn(tabs, 0);
			Grid.SetColumnSpan(tabs, 2);
			grid.Children.Add(tabs);
			settingsTabs = tabs;
			Func<string, string, StackPanel> func = (string heading, string icon) =>
			{
				Border border = Polish.Section(heading, "调整即时生效并自动保存，关闭窗口不会丢失。", settingsLight);
				StackPanel stackPanel10 = (StackPanel)border.Child;
				TextElement.SetForeground(stackPanel10, Tokens.Label(settingsLight));
				StackPanel stackPanel11 = new StackPanel
				{
					Margin = new Thickness(24.0, 14.0, 26.0, 8.0)
				};
				stackPanel11.Children.Add(border);
				// 侧边栏导航项：图标 + 文字，选中态为强调色胶囊（由 TabItem 样式提供）。
				StackPanel navHeader = new StackPanel
				{
					Orientation = System.Windows.Controls.Orientation.Horizontal
				};
				FrameworkElement navIcon = Polish.Icon(icon, Tokens.LabelSecondary(settingsLight), 18.0);
				navIcon.Margin = new Thickness(0.0, 0.0, 11.0, 0.0);
				navIcon.VerticalAlignment = VerticalAlignment.Center;
				navHeader.Children.Add(navIcon);
				navHeader.Children.Add(new TextBlock
				{
					Text = heading,
					VerticalAlignment = VerticalAlignment.Center
				});
				TabItem newItem = new TabItem
				{
					Header = navHeader,
					Content = new ScrollViewer
					{
						Content = stackPanel11,
						VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
						HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
					}
				};
				newItem.Tag = navIcon;
				Grid.SetRow(newItem, 1);
				Grid.SetColumn(newItem, 0);
				tabs.Items.Add(newItem);
				return stackPanel10;
			};
			StackPanel stackPanel2 = func("面板与主题", "image");
			StackPanel stackPanel3 = func("Dock DIY", "layout");
			// 大面板实时预览：材质 / 色调 / 透明度 / 光泽任一项变化后立即重绘。
			// 设置打开时大面板是收起的，看不到效果，所以这里给一块与面板同源材质的预览。
			Grid panelPreviewHost = new Grid
			{
				Height = 148.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
			};
			panelPreviewHost.Children.Add(new Border
			{
				CornerRadius = new CornerRadius(14.0),
				Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(96, 122, 158), System.Windows.Media.Color.FromRgb(38, 46, 62), 42.0)
			});
			Border panelPreviewGlass = new Border
			{
				Margin = new Thickness(16.0),
				CornerRadius = new CornerRadius(20.0),
				BorderThickness = new Thickness(1.0),
				BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(52, 255, 255, 255))
			};
			panelPreviewHost.Children.Add(panelPreviewGlass);
			StackPanel panelPreviewTiles = new StackPanel
			{
				Orientation = System.Windows.Controls.Orientation.Horizontal,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = System.Windows.VerticalAlignment.Center
			};
			for (int previewIndex = 0; previewIndex < 5; previewIndex++)
			{
				panelPreviewTiles.Children.Add(new Border
				{
					Width = 40.0,
					Height = 40.0,
					Margin = new Thickness(7.0, 0.0, 7.0, 0.0),
					CornerRadius = new CornerRadius(11.0),
					Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(46, 255, 255, 255)),
					BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(66, 255, 255, 255)),
					BorderThickness = new Thickness(1.0)
				});
			}
			panelPreviewHost.Children.Add(panelPreviewTiles);
			stackPanel2.Children.Add(panelPreviewHost);
			stackPanel2.Children.Add(new TextBlock
			{
				Text = "大面板玻璃实时预览 · 调节下方任一项，这里立刻变化（设置会即时生效并自动保存）",
				Foreground = Tokens.LabelSecondary(settingsLight),
				FontSize = 12.0,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
			});
			Action refreshPreview = () =>
			{
				try
				{
					MaterialOptions material = Prefs.BoardMaterial ?? new MaterialOptions();
					panelPreviewGlass.Background = GlassMaterials.Fill(material, Prefs.Light, Prefs.OpacityPercent ?? 72.0, Prefs.Dock.Accent);
					panelPreviewGlass.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(40.0 + material.Gloss * 90.0), 255, 255, 255));
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			};
			refreshPreview();
			StackPanel stackPanel4 = func("快捷键与开关", "control");
			StackPanel stackPanel6 = func("通用", "sliders");
			StackPanel feedbackPage = func("在线反馈", "note");
			feedbackPage.Children.Add(new TextBlock
			{
				Text = "有想法、想改样式，或发现了 Bug？\n直接发邮件到 1379350278@qq.com，截图可以作为附件一起发给我。\n\n小提示：提交问题前，可以先用其他 AI 帮你排查确认，让描述更准确、修复更快。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Tokens.LabelSecondary(settingsLight),
				FontSize = 14.0,
				LineHeight = 24.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
			});
			WrapPanel feedbackButtons = new WrapPanel();
			System.Windows.Controls.Button feedbackOpen = new System.Windows.Controls.Button
			{
				Content = "发邮件反馈",
				Padding = new Thickness(24.0, 10.0, 24.0, 10.0),
				Cursor = System.Windows.Input.Cursors.Hand
			};
			feedbackOpen.Click += (object s, RoutedEventArgs e) =>
			{
				try
				{
					Process.Start("mailto:1379350278@qq.com?subject=" + Uri.EscapeDataString("IconBox 反馈建议"));
				}
				catch
				{
				}
			};
			System.Windows.Controls.Button feedbackCopy = new System.Windows.Controls.Button
			{
				Content = "复制邮箱",
				Padding = new Thickness(20.0, 10.0, 20.0, 10.0),
				Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
				Cursor = System.Windows.Input.Cursors.Hand
			};
			feedbackCopy.Click += (object s, RoutedEventArgs e) =>
			{
				try
				{
					System.Windows.Clipboard.SetText("1379350278@qq.com");
					feedbackCopy.Content = "已复制";
				}
				catch
				{
				}
			};
			feedbackButtons.Children.Add(feedbackOpen);
			feedbackButtons.Children.Add(feedbackCopy);
			feedbackPage.Children.Add(feedbackButtons);
			// 侧边栏图标随选中态换色：选中为白色，未选中为次要文字色（原先图标恒灰，与白字不协调）。
			Action paintNavIcons = delegate
			{
				foreach (object navItemObject in tabs.Items)
				{
					TabItem navItem = navItemObject as TabItem;
					if (navItem == null)
					{
						continue;
					}
					FrameworkElement navGlyph = navItem.Tag as FrameworkElement;
					if (navGlyph == null)
					{
						continue;
					}
					System.Windows.Media.Brush navInk = navItem.IsSelected ? System.Windows.Media.Brushes.White : Tokens.LabelSecondary(settingsLight);
					System.Windows.Shapes.Path navPath = navGlyph as System.Windows.Shapes.Path;
					if (navPath != null)
					{
						navPath.Fill = navInk;
						continue;
					}
					TextBlock navText = navGlyph as TextBlock;
					if (navText != null)
					{
						navText.Foreground = navInk;
					}
				}
			};
			UpdateNotice.Build(tabs);
			tabs.SelectedIndex = Math.Max(0, Math.Min(6, selectedPage));
			tabs.SelectionChanged += (object navSender, SelectionChangedEventArgs navArgs) => paintNavIcons();
			paintNavIcons();
			DockOptions draft = Prefs.Dock.Copy();
			bool loading = false;
			preview.Tick += (object s, EventArgs e) =>
			{
				preview.Stop();
				Dock.Refresh(capture: false);
			};
			Action live = () =>
			{
				if (!loading)
				{
					draft.ItemOrder = (Prefs.Dock.ItemOrder ?? new List<string>()).ToList();
					draft.RunningOrder = (Prefs.Dock.RunningOrder ?? new List<string>()).ToList();
				draft.Normalize();
				Prefs.Dock = draft.Copy();
				Save();
				refreshPreview();
				if (!preview.IsEnabled)
					{
						preview.Start();
					}
				}
			};
			Func<StackPanel, string, double, double, double, double, string, Action<double>, Slider> func2 = (StackPanel panel, string label, double min, double max, double value3, double tick, string unit, Action<double> change) =>
			{
				TextBlock caption = new TextBlock
				{
					Text = label + "  " + value3.ToString("0.##") + unit,
					Margin = new Thickness(0.0, 12.0, 0.0, 6.0),
					Foreground = Tokens.Label(settingsLight)
				};
				panel.Children.Add(caption);
				Slider control = new Slider
				{
					Minimum = min,
					Maximum = max,
					Value = value3,
					TickFrequency = tick,
					IsSnapToTickEnabled = true,
					Margin = new Thickness(0.0, 0.0, 5.0, 4.0)
				};
				panel.Children.Add(control);
				control.ValueChanged += (object s, RoutedPropertyChangedEventArgs<double> e) =>
				{
					caption.Text = label + "  " + control.Value.ToString("0.##") + unit;
					change(control.Value);
				};
				return control;
			};
			Func<StackPanel, string, bool, Action<bool>, System.Windows.Controls.CheckBox> func3 = (StackPanel panel, string label, bool value3, Action<bool> change) =>
			{
				// iOS 开关行：左侧说明文字（长文案可换行）+ 右侧拨杆，替代原来的方块复选框。
				System.Windows.Controls.CheckBox checkBox = new System.Windows.Controls.CheckBox
				{
					Content = new TextBlock
					{
						Text = label,
						TextWrapping = TextWrapping.Wrap,
						Foreground = Tokens.Label(settingsLight),
						FontSize = Tokens.Type.Body,
						LineHeight = 18.0,
						VerticalAlignment = VerticalAlignment.Center
					},
					IsChecked = value3,
					Style = (Style)w.Resources["Switch"],
					Margin = new Thickness(0.0, 7.0, 0.0, 7.0)
				};
				panel.Children.Add(checkBox);
				checkBox.Checked += (object s, RoutedEventArgs e) =>
				{
					change(obj: true);
				};
				checkBox.Unchecked += (object s, RoutedEventArgs e) =>
				{
					change(obj: false);
				};
				return checkBox;
			};
			Func<StackPanel, string, string[], int, Action<int>, System.Windows.Controls.ComboBox> func4 = (StackPanel panel, string label, string[] items, int selectedIndex, Action<int> change) =>
			{
				panel.Children.Add(new TextBlock
				{
					Text = label,
					Margin = new Thickness(0.0, 12.0, 0.0, 6.0)
				});
				System.Windows.Controls.ComboBox control = new System.Windows.Controls.ComboBox
				{
					ItemsSource = items,
					SelectedIndex = selectedIndex,
					Height = 32.0
				};
				panel.Children.Add(control);
				control.SelectionChanged += (object s, SelectionChangedEventArgs e) =>
				{
					if (control.SelectedIndex >= 0)
					{
						change(control.SelectedIndex);
					}
				};
				return control;
			};
			Action liveGlobal = delegate
			{
				live();
				ApplyGlobalStyle();
				refreshPreview();
				Board.Render();
			};
			GlobalStyleOptions g = Prefs.GlobalStyle;
			func3(stackPanel2, "统一 Dock / 大面板外观（开启后下方全局样式同时作用于两处）", g.Unify, (bool unify) =>
			{
				g.Unify = unify;
				if (unify)
				{
					g.Material = Prefs.Dock.Material.Copy();
					g.OpacityPercent = Prefs.Dock.OpacityPercent;
					g.Blur = Prefs.Dock.BlurRadius;
					g.Edge = Prefs.Dock.EdgeStrength;
					g.Radius = Prefs.Dock.Radius;
				}
				liveGlobal();
			});
			func4(stackPanel2, "材质风格", new string[4] { "标准毛玻璃", "轻盈渐变", "纯净实色", "明亮渐变" }, g.Material.Kind, (int kind) =>
			{
				g.Material.Kind = kind;
				liveGlobal();
			});
			string[] tintNames = new string[7] { "跟随强调色", "雾白", "雾蓝", "暖灰", "墨蓝", "雾紫", "自定义…" };
			string[] tintValues = new string[7] { "", "#E4E9F2", "#A9C4EB", "#C9C2B4", "#16213A", "#B7A6E8", "" };
			int tintIndex = Array.IndexOf(tintValues, g.Material.Tint ?? "");
			if (tintIndex < 0)
			{
				tintIndex = (string.IsNullOrEmpty(g.Material.Tint) ? 0 : 6);
			}
			func4(stackPanel2, "玻璃色调", tintNames, tintIndex, (int tint) =>
			{
				if (tint == 6)
				{
					string custom = FeatureUi.Ask(w, "自定义玻璃色调", "输入 #RRGGBB 十六进制颜色（留空 = 跟随强调色）：");
					if (custom != null)
					{
						g.Material.Tint = custom.Trim();
					}
				}
				else
				{
					g.Material.Tint = tintValues[tint];
				}
				g.Material.Normalize();
				liveGlobal();
			});
			func2(stackPanel2, "统一透明度", 10.0, 100.0, g.OpacityPercent, 5.0, "%", (double opacity) =>
			{
				g.OpacityPercent = opacity;
				liveGlobal();
			});
			func2(stackPanel2, "统一玻璃模糊", 0.0, 40.0, g.Blur, 2.0, "", (double blur) =>
			{
				g.Blur = blur;
				liveGlobal();
			});
			func2(stackPanel2, "统一边缘高光", 0.2, 2.0, g.Edge, 0.1, " 倍", (double edge) =>
			{
				g.Edge = edge;
				liveGlobal();
			});
			func2(stackPanel2, "Dock 圆角", 0.0, 48.0, g.Radius, 2.0, " px", (double radius) =>
			{
				g.Radius = radius;
				liveGlobal();
			});
			func3(stackPanel2, "使用浅色玻璃", Prefs.Light, (bool light) =>
			{
				Prefs.Light = light;
				FeatureUi.Light = light;
				live();
			});
			func4(stackPanel2, "大面板尺寸", new string[3] { "舒适 · 中号", "宽阔 · 大号", "铺满 · 全屏" }, Prefs.PanelSize, (int panelSize) =>
			{
				Prefs.PanelSize = panelSize;
				Prefs.Placement.PanelWidth = (Prefs.Placement.PanelHeight = null);
				Save();
			});
			func2(stackPanel2, "大面板透明度", 0.0, 100.0, 100.0 - (Prefs.OpacityPercent ?? 72.0), 5.0, "%", (double num3) =>
			{
				Opacity(100.0 - num3);
				Save();
				refreshPreview();
			});
			func3(stackPanel2, "启用界面动效", draft.MotionEnabled, (bool motionEnabled) =>
			{
				draft.MotionEnabled = motionEnabled;
				live();
			});
			func2(stackPanel2, "动效强度", 0.5, 1.5, draft.MotionLevel, 0.1, " 倍", (double motionLevel) =>
			{
				draft.MotionLevel = motionLevel;
				live();
			});
			func2(stackPanel2, "玻璃模糊", 0.0, 40.0, draft.BlurRadius, 2.0, "", (double blurRadius) =>
			{
				draft.BlurRadius = blurRadius;
				live();
			});
			func2(stackPanel2, "边缘高光", 0.2, 2.0, draft.EdgeStrength, 0.1, " 倍", (double edgeStrength) =>
			{
				draft.EdgeStrength = edgeStrength;
				live();
			});
			func3(stackPanel2, "锁定大面板位置和尺寸", Prefs.Placement.PanelLocked, (bool panelLocked) =>
			{
				Prefs.Placement.PanelLocked = panelLocked;
				Save();
			});
			func3(stackPanel2, "点击外部自动收起大面板（关闭时方便从桌面拖入文件）", Prefs.BoardDismissOnDeactivate, (bool boardDismissOnDeactivate) =>
			{
				Prefs.BoardDismissOnDeactivate = boardDismissOnDeactivate;
				Save();
			});
			func3(stackPanel2, "隐藏大面板搜索栏", Prefs.BoardHideSearch, (bool boardHideSearch) =>
			{
				Prefs.BoardHideSearch = boardHideSearch;
				Save();
				Board.ApplyDisplayOptions();
			});
			func3(stackPanel2, "显示快捷方式箭头角标（大面板与 Dock）", draft.ShowShortcutOverlays, (bool showShortcutOverlays) =>
			{
				draft.ShowShortcutOverlays = showShortcutOverlays;
				Native.ClearIconCache();
				live();
				Board.RefreshIcons();
				Save();
			});
			func4(stackPanel2, "系统入口图标风格", IconPacks.Names, draft.IconPack, (int iconPack) =>
			{
				draft.IconPack = iconPack;
				// 切换内置图标风格：清除自定义上传的图片（图片会压住内置风格），
				// 但保留逐个入口指定的内置样式——切回原风格时仍然显示修改后的图标。
				// 例外：运行列表入口（running）的用户覆盖永久保留（用户指定：改过就一直用改后的）。
				if (Prefs.SystemIconImages != null)
				{
					foreach (string key in Prefs.SystemIconImages.Keys.Where((string k) => k != "running").ToList())
					{
						Prefs.SystemIconImages.Remove(key);
					}
				}
				live();
			});
			stackPanel2.Children.Add(new TextBlock
			{
				Text = "优先读取软件自带的高分辨率图标；仅在加载时处理，不新增后台任务。关闭后清除图标缓存。原始图标像素低时效果有限。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Tokens.LabelSecondary(settingsLight),
				Margin = new Thickness(0.0, 2.0, 0.0, 8.0)
			});
			BuildMaterialEditor(stackPanel2, "大面板玻璃材质 DIY", Prefs.BoardMaterial, () =>
			{
				Prefs.BoardMaterial.Normalize();
				Board.SetMaterial(Prefs.BoardMaterial);
				Board.RefreshIcons();
				Save();
				refreshPreview();
			});
			WrapPanel wrapPanel = new WrapPanel
			{
				Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
			};
			stackPanel2.Children.Add(wrapPanel);
			wrapPanel.Children.Add(FeatureUi.Button("外观方案", () =>
			{
				Save();
				if (ManageSchemes(w))
				{
					draft = Prefs.Dock.Copy();
					w.Close();
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("重置面板位置和尺寸", () =>
			{
				Prefs.Placement.PanelLeft = (Prefs.Placement.PanelTop = (Prefs.Placement.PanelWidth = (Prefs.Placement.PanelHeight = null)));
				Save();
			}));
			stackPanel2.Children.Add(new TextBlock
			{
				Text = "面板平滑展开、图标逐项淡入、悬停轻抬；关闭动效后直接呈现。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Tokens.LabelSecondary(settingsLight),
				Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
			});
			System.Windows.Controls.ComboBox presets = func4(stackPanel3, "一键风格", new string[4] { "自定义", "紧凑", "均衡", "宽松" }, 0, (int num3) =>
			{
			});
			Slider icon = func2(stackPanel3, "图标尺寸", 32.0, 64.0, draft.IconSize, 2.0, " px", (double iconSize) =>
			{
				draft.IconSize = iconSize;
				live();
			});
			// Dock 栏高度：0 = 自动（按图标尺寸推算），32–140px 直接指定固定高度，自由度更高。
			Slider dockHeight = func2(stackPanel3, "Dock 栏高度", 0.0, 140.0, draft.Height, 2.0, " px（0=自动）", (double height2) =>
			{
				draft.Height = height2;
				live();
			});
			Slider spacing = func2(stackPanel3, "图标间距", 0.0, 20.0, draft.Spacing, 1.0, " px", (double spacing2) =>
			{
				draft.Spacing = spacing2;
				live();
			});
			Slider radius = func2(stackPanel3, "圆角", 0.0, 48.0, draft.Radius, 2.0, " px", (double radius2) =>
			{
				draft.Radius = radius2;
				live();
			});
			Slider opacity = func2(stackPanel3, "Dock 透明度", 0.0, 100.0, 100.0 - draft.OpacityPercent, 5.0, "%", (double num3) =>
			{
				draft.OpacityPercent = 100.0 - num3;
				live();
			});
			Slider zoom = func2(stackPanel3, "悬停放大", 1.0, 1.6, draft.Zoom, 0.05, " 倍", (double zoom2) =>
			{
				draft.Zoom = zoom2;
				live();
			});
			System.Windows.Controls.CheckBox labels = func3(stackPanel3, "显示图标名称", draft.ShowLabels, (bool showLabels) =>
			{
				draft.ShowLabels = showLabels;
				live();
			});
			func3(stackPanel3, "显示运行列表入口（正在运行的程序）", draft.ShowRunningList, (bool showRunningList) =>
			{
				draft.ShowRunningList = showRunningList;
				live();
			});
			func3(stackPanel3, "显示控制中心入口（Wi-Fi / 蓝牙 / 音量 / 亮度）", draft.ShowControlCenter, (bool showControlCenter) =>
			{
				draft.ShowControlCenter = showControlCenter;
				live();
			});
			func4(stackPanel3, "强调色", new string[6] { "晴空蓝", "雾紫", "薄荷绿", "暖金", "玫瑰粉", "月光灰" }, draft.Accent, (int accent) =>
			{
				draft.Accent = accent;
				liveGlobal();
			});
			func3(stackPanel3, "显示分隔线", draft.ShowSeparator, (bool showSeparator) =>
			{
				draft.ShowSeparator = showSeparator;
				live();
			});
			func3(stackPanel3, "Dock 保持在窗口最上方", draft.AlwaysOnTop, (bool alwaysOnTop) =>
			{
				draft.AlwaysOnTop = alwaysOnTop;
				live();
			});
			func3(stackPanel3, "显示软件运行状态", draft.ShowRunning, (bool showRunning) =>
			{
				draft.ShowRunning = showRunning;
				live();
			});
			func3(stackPanel3, "Dock 自动隐藏，靠近所在屏幕底边唤出", draft.AutoHide, (bool autoHide) =>
			{
				draft.AutoHide = autoHide;
				live();
			});
			// 跟随 Windows 任务栏自动隐藏：任务栏下沉时 Dock 一起沉到最底，任务栏弹出时 Dock 跟着上升，
			// 且 Dock 始终停在任务栏上方、不遮挡任务栏。
			func3(stackPanel3, "跟随 Windows 任务栏自动隐藏（Dock 随任务栏升降，始终在其上方）", draft.FollowTaskbar, (bool followTaskbar) =>
			{
				draft.FollowTaskbar = followTaskbar;
				live();
			});
			func2(stackPanel3, "自动隐藏延迟", 200.0, 3000.0, draft.HideDelay, 100.0, " ms", (double hideDelay) =>
			{
				draft.HideDelay = hideDelay;
				live();
			});
			func2(stackPanel3, "底边触发范围", 3.0, 48.0, draft.EdgeRange, 1.0, " px", (double edgeRange) =>
			{
				draft.EdgeRange = edgeRange;
				live();
			});
			func3(stackPanel3, "弹簧弹出效果（也可右键 Dock 切换）", draft.SpringReveal, (bool springReveal) =>
			{
				draft.SpringReveal = springReveal;
				live();
			});
			func3(stackPanel3, "锁定 Dock 位置", Prefs.Placement.DockLocked, (bool dockLocked) =>
			{
				Prefs.Placement.DockLocked = dockLocked;
				live();
			});
			stackPanel3.Children.Add(FeatureUi.Button("重置 Dock 位置", () =>
			{
				Prefs.Placement.DockCenter = (Prefs.Placement.DockLeft = (Prefs.Placement.DockTop = null));
				Dock.ResetSettledTop();
				live();
			}));
			func4(stackPanel3, "水平位置", new string[3] { "靠左", "居中", "靠右" }, draft.Alignment, (int alignment) =>
			{
				draft.Alignment = alignment;
				Prefs.Placement.DockCenter = (Prefs.Placement.DockLeft = null);
				live();
			});
			func2(stackPanel3, "左右微调", -500.0, 500.0, draft.Offset, 10.0, " px", (double offset) =>
			{
				draft.Offset = offset;
				Prefs.Placement.DockCenter = (Prefs.Placement.DockLeft = null);
				live();
			});
			func2(stackPanel3, "距工作区底边", 0.0, 160.0, Prefs.DockGap, 2.0, " px", (double dockGap) =>
			{
				Prefs.DockGap = dockGap;
				Prefs.Placement.DockTop = null;
				Dock.ResetSettledTop();
				live();
			});
			func2(stackPanel3, "固定应用显示数量", 3.0, 20.0, draft.MaxPinned, 1.0, " 个", (double num3) =>
			{
				draft.MaxPinned = (int)num3;
				live();
			});
			stackPanel3.Children.Add(new TextBlock
			{
				Text = "可固定 20 个入口。数量超限或屏幕放不下时，其余入口收进「更多」。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Tokens.LabelSecondary(settingsLight),
				Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
			});
			presets.SelectionChanged += (object s, SelectionChangedEventArgs e) =>
			{
				if (presets.SelectedIndex >= 1)
				{
					int selectedIndex = presets.SelectedIndex;
					loading = true;
					icon.Value = selectedIndex switch
					{
						3 => 56, 
						1 => 34, 
						_ => 44, 
					};
					spacing.Value = selectedIndex switch
					{
						3 => 10, 
						1 => 2, 
						_ => 6, 
					};
					radius.Value = selectedIndex switch
					{
						3 => 36, 
						1 => 20, 
						_ => 28, 
					};
					opacity.Value = selectedIndex switch
					{
						3 => 22, 
						1 => 35, 
						_ => 28, 
					};
					zoom.Value = selectedIndex switch
					{
						3 => 1.3, 
						1 => 1.1, 
						_ => 1.2, 
					};
					labels.IsChecked = selectedIndex == 3;
					loading = false;
					live();
				}
			};
			stackPanel4.Children.Add(new TextBlock
			{
				Text = "快捷键与桌面开关",
				FontSize = 18.0
			});
			stackPanel4.Children.Add(new TextBlock
			{
				Text = "可点击录制直接按组合键，改完即时生效。右侧开关即时操作。\n隐藏开关：开启彻底隐藏任务栏，关闭显示；自动开关：Windows 任务栏自动隐藏（悬停底部边缘弹出）。\n两者互斥：开启其中一个会自动关闭另一个。注意：彻底隐藏任务栏窗口与 Wallpaper Engine 等动态壁纸可能冲突（表现为背景白屏），如遇白屏请改用自动隐藏。\n修改组合时下方会实时探测冲突（可用/被占用）。快捷键在 IconBox 后台运行时始终有效：关闭面板不影响，托盘「退出」才失效；组合被其他软件占用时会每 5 秒自动重试，对方释放后自动接管。",
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 8.0, 0.0, 18.0)
			});
			Dictionary<string, uint> keyChoices = HotkeyRules.Keys();
			Dictionary<int, Tuple<System.Windows.Controls.CheckBox, System.Windows.Controls.ComboBox, System.Windows.Controls.ComboBox>> controls = new Dictionary<int, Tuple<System.Windows.Controls.CheckBox, System.Windows.Controls.ComboBox, System.Windows.Controls.ComboBox>>();
			Dictionary<int, ToggleButton> switches = new Dictionary<int, ToggleButton>();
			Dictionary<int, TextBlock> info = new Dictionary<int, TextBlock>();
			bool syncing = false;
			foreach (HotkeySetting binding in Prefs.Hotkeys.OrderBy((HotkeySetting k) => k.ActionId))
			{
				int id = binding.ActionId;
				StackPanel stackPanel7 = new StackPanel
				{
					Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
				};
				stackPanel4.Children.Add(stackPanel7);
				Grid grid2 = new Grid();
				double[] array2 = new double[4] { 1.7, 1.7, 0.8, 1.1 };
				foreach (double value in array2)
				{
					grid2.ColumnDefinitions.Add(new ColumnDefinition
					{
						Width = new GridLength(value, GridUnitType.Star)
					});
				}
				grid2.ColumnDefinitions.Insert(3, new ColumnDefinition
				{
					Width = new GridLength(64.0)
				});
				stackPanel7.Children.Add(grid2);
				System.Windows.Controls.CheckBox enabled = new System.Windows.Controls.CheckBox
				{
					Content = new TextBlock
					{
						Text = HotkeyRules.Actions[id - 1],
						TextWrapping = TextWrapping.Wrap
					},
					IsChecked = binding.Enabled,
					Foreground = Tokens.Label(settingsLight),
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
				};
				grid2.Children.Add(enabled);
				System.Windows.Controls.ComboBox modifiers = new System.Windows.Controls.ComboBox
				{
					ItemsSource = HotkeyRules.ModifierNames,
					SelectedIndex = Array.IndexOf(HotkeyRules.ModifierValues, binding.Modifiers),
					Height = 32.0,
					Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
					IsEnabled = binding.Enabled
				};
				Grid.SetColumn(modifiers, 1);
				grid2.Children.Add(modifiers);
				System.Windows.Controls.ComboBox key = new System.Windows.Controls.ComboBox
				{
					ItemsSource = keyChoices.Keys.ToList(),
					SelectedItem = keyChoices.First((KeyValuePair<string, uint> k) => k.Value == binding.Key).Key,
					Height = 32.0,
					Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
					IsEnabled = binding.Enabled
				};
				Grid.SetColumn(key, 2);
				grid2.Children.Add(key);
				enabled.Checked += (object s, RoutedEventArgs e) =>
				{
					modifiers.IsEnabled = true;
					key.IsEnabled = true;
				};
				enabled.Unchecked += (object s, RoutedEventArgs e) =>
				{
					modifiers.IsEnabled = false;
					key.IsEnabled = false;
				};
				controls[id] = Tuple.Create(enabled, modifiers, key);
				System.Windows.Controls.Button button = new System.Windows.Controls.Button
				{
					Content = "录制",
					Height = 32.0,
					Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
				};
				Grid.SetColumn(button, 3);
				grid2.Children.Add(button);
				button.Click += (object s, RoutedEventArgs e) =>
				{
					List<HotkeySetting> keys = Dock.SuspendHotkeys();
					HotkeySetting captured;
					try
					{
						captured = FeatureUi.Record(w, id);
					}
					finally
					{
						Dock.ResumeHotkeys(keys);
					}
					if (captured != null)
					{
						enabled.IsChecked = true;
						modifiers.SelectedIndex = Array.IndexOf(HotkeyRules.ModifierValues, captured.Modifiers);
						key.SelectedItem = keyChoices.First((KeyValuePair<string, uint> x) => x.Value == captured.Key).Key;
					}
				};
				if (id == 2 || id == 3 || id == 7)
				{
					StackPanel stackPanel8 = new StackPanel
					{
						Orientation = System.Windows.Controls.Orientation.Horizontal,
						VerticalAlignment = VerticalAlignment.Center
					};
					Grid.SetColumn(stackPanel8, 4);
					grid2.Children.Add(stackPanel8);
					ToggleButton toggleButton = VisibilitySwitch();
					if (id == 7)
					{
						// 以用户偏好为准（与「隐藏任务栏」互斥），不读运行时 ABS_AUTOHIDE 位：
						// 隐藏任务栏内部也走 ABS_AUTOHIDE，读位会让两个开关同时显示开启。
						toggleButton.IsChecked = Prefs.AutoHideTaskbar && !Prefs.HideTaskbar;
					}
					else
					{
						toggleButton.IsChecked = DesktopVisibility.DesiredHidden(id == 2);
					}
					toggleButton.ToolTip = ((id == 7) ? "开启 Windows 任务栏自动隐藏" : "开启隐藏，关闭显示");
					stackPanel8.Children.Add(toggleButton);
					stackPanel8.Children.Add(new TextBlock
					{
						Text = ((id == 7) ? "自动" : "隐藏"),
						Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
						VerticalAlignment = VerticalAlignment.Center,
						Foreground = Tokens.Label(settingsLight)
					});
					switches[id] = toggleButton;
					toggleButton.Checked += (object s, RoutedEventArgs e) =>
					{
						if (!syncing)
						{
							if (id == 7)
							{
								ToggleTaskbarAutoHide(true);
							}
							else
							{
								ShellAction(id - 1, true);
							}
							// 隐藏任务栏与自动隐藏互斥：开启其一，界面同步关闭另一个开关
							syncing = true;
							try
							{
								if (switches.TryGetValue((id == 7) ? 2 : 7, out ToggleButton other))
								{
									other.IsChecked = false;
								}
							}
							finally
							{
								syncing = false;
							}
						}
					};
					toggleButton.Unchecked += (object s, RoutedEventArgs e) =>
					{
						if (!syncing)
						{
							if (id == 7)
							{
								ToggleTaskbarAutoHide(false);
							}
							else
							{
								ShellAction(id - 1, false);
							}
						}
					};
				}
				TextBlock textBlock = new TextBlock
				{
					Foreground = Tokens.LabelSecondary(settingsLight),
					FontSize = 11.0,
					TextWrapping = TextWrapping.Wrap,
					Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
				};
				stackPanel7.Children.Add(textBlock);
				info[id] = textBlock;
			}
			Action updateState = () =>
			{
				syncing = true;
				foreach (HotkeySetting hotkey in Prefs.Hotkeys)
				{
					string text2 = ((!hotkey.Enabled) ? "快捷键已关闭" : (Dock.HotkeyActive(hotkey.ActionId) ? "快捷键已注册" : "快捷键未注册，请换组合或重新注册"));
					if (controls.TryGetValue(hotkey.ActionId, out Tuple<System.Windows.Controls.CheckBox, System.Windows.Controls.ComboBox, System.Windows.Controls.ComboBox> row) && row.Item1.IsChecked == true)
					{
						try
						{
							int mi = row.Item2.SelectedIndex;
							string kn = (string)row.Item3.SelectedItem;
							if (mi >= 0 && kn != null)
							{
								uint mods = HotkeyRules.ModifierValues[mi];
								uint vk = keyChoices[kn];
								if (mods != hotkey.Modifiers || vk != hotkey.Key || !hotkey.Enabled)
								{
									string probe = Dock.ProbeHotkey(mods, vk);
									text2 = ((probe == null) ? "新组合可用，改完即时生效" : ((probe.Length == 0) ? "该组合已被其他程序占用，请换一个" : "该组合与本程序「" + probe + "」重复，请换一个"));
								}
							}
						}
						catch
						{
						}
					}
					else if (controls.TryGetValue(hotkey.ActionId, out row) && row.Item1.IsChecked == false && hotkey.Enabled)
					{
						text2 = "已取消启用，保存后关闭";
					}
					if (hotkey.ActionId == 2 || hotkey.ActionId == 3)
					{
						bool taskbar = hotkey.ActionId == 2;
						bool? flag2 = DesktopVisibility.IsHidden(taskbar);
						string text3 = text2;
						string text4 = ((!flag2.HasValue) ? "未找到系统窗口" : (flag2.Value ? "隐藏" : "显示"));
						text2 = text3 + " · 实际状态：" + text4;
						switches[hotkey.ActionId].IsChecked = DesktopVisibility.DesiredHidden(taskbar);
					}
					if (hotkey.ActionId == 7)
					{
						try
						{
							// 与「隐藏任务栏」互斥的偏好状态，而非运行时 ABS_AUTOHIDE 位
							bool enabled2 = Prefs.AutoHideTaskbar && !Prefs.HideTaskbar;
							switches[7].IsChecked = enabled2;
							text2 = text2 + " · 自动隐藏：" + (enabled2 ? "开启" : "关闭");
						}
						catch
						{
							text2 += " · 未找到 Windows 任务栏";
						}
					}
					info[hotkey.ActionId].Text = text2;
				}
				syncing = false;
			};
			stateTimer.Tick += (object s, EventArgs e) =>
			{
				updateState();
			};
			updateState();
			stateTimer.Start();
			// 快捷键改动即时生效：不再需要「保存并完成」按钮，任何改动都立刻应用并写入配置。
			Action applyHotkeys = delegate
			{
				if (syncing)
				{
					return;
				}
				try
				{
					List<HotkeySetting> hotkeys = (from row in controls
						select new HotkeySetting
						{
							ActionId = row.Key,
							Enabled = (row.Value.Item1.IsChecked == true),
							Modifiers = HotkeyRules.ModifierValues[Math.Max(0, row.Value.Item2.SelectedIndex)],
							Key = keyChoices[(string)row.Value.Item3.SelectedItem]
						} into k
						orderby k.ActionId
						select k).ToList();
					if (HotkeyRules.Same(Prefs.Hotkeys, hotkeys))
					{
						return;
					}
					if (!Dock.ApplyHotkeys(hotkeys, out var error))
					{
						Store.Trace("HOTKEY apply refused: " + error);
						Board.SetStatus(error);
						updateState();
						return;
					}
					Prefs.Hotkeys = hotkeys.Select((HotkeySetting k) => k.Copy()).ToList();
					Save();
					RefreshTrayMenu();
					updateState();
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			};
			foreach (KeyValuePair<int, Tuple<System.Windows.Controls.CheckBox, System.Windows.Controls.ComboBox, System.Windows.Controls.ComboBox>> hotkeyRow in controls)
			{
				hotkeyRow.Value.Item1.Checked += (object s, RoutedEventArgs e) => applyHotkeys();
				hotkeyRow.Value.Item1.Unchecked += (object s, RoutedEventArgs e) => applyHotkeys();
				hotkeyRow.Value.Item2.SelectionChanged += (object s, SelectionChangedEventArgs e) => applyHotkeys();
				hotkeyRow.Value.Item3.SelectionChanged += (object s, SelectionChangedEventArgs e) => applyHotkeys();
			}
			WrapPanel wrapPanel2 = new WrapPanel();
			stackPanel4.Children.Add(wrapPanel2);
			System.Windows.Controls.Button button2 = new System.Windows.Controls.Button
			{
				Content = "恢复默认快捷键",
				Padding = new Thickness(12.0, 7.0, 12.0, 7.0),
				Margin = new Thickness(0.0, 0.0, 8.0, 6.0)
			};
			wrapPanel2.Children.Add(button2);
			button2.Click += (object s, RoutedEventArgs e) =>
			{
				foreach (HotkeySetting k in HotkeyRules.Defaults())
				{
					Tuple<System.Windows.Controls.CheckBox, System.Windows.Controls.ComboBox, System.Windows.Controls.ComboBox> tuple = controls[k.ActionId];
					tuple.Item1.IsChecked = true;
					tuple.Item2.SelectedIndex = Array.IndexOf(HotkeyRules.ModifierValues, k.Modifiers);
					tuple.Item3.SelectedItem = keyChoices.First((KeyValuePair<string, uint> x) => x.Value == k.Key).Key;
				}
			};
			System.Windows.Controls.Button button3 = new System.Windows.Controls.Button
			{
				Content = "重新注册当前快捷键",
				Padding = new Thickness(12.0, 7.0, 12.0, 7.0),
				Margin = new Thickness(0.0, 0.0, 8.0, 6.0)
			};
			wrapPanel2.Children.Add(button3);
			button3.Click += (object s, RoutedEventArgs e) =>
			{
				System.Windows.MessageBox.Show(w, Dock.RetryHotkeys(), "快捷键注册结果");
				updateState();
			};
			System.Windows.Controls.Button button4 = new System.Windows.Controls.Button
			{
				Content = "显示任务栏与桌面图标",
				Padding = new Thickness(12.0, 7.0, 12.0, 7.0),
				Margin = new Thickness(0.0, 0.0, 8.0, 6.0)
			};
			wrapPanel2.Children.Add(button4);
			button4.Click += (object s, RoutedEventArgs e) =>
			{
				ShellAction(3);
				updateState();
			};
			stackPanel4.Children.Add(new TextBlock
			{
				Text = "隐藏状态仅在本次运行中维持；正常退出恢复启动前状态。组合键占用不会阻止直接点击开关。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Tokens.LabelSecondary(settingsLight),
				Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
			});
			bool value2 = false;
			try
			{
				value2 = StartupService.Enabled;
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
			System.Windows.Controls.CheckBox startupCheck = new System.Windows.Controls.CheckBox
			{
				Content = "登录 Windows 后自动启动 IconBox",
				IsChecked = value2,
				Foreground = Tokens.Label(settingsLight),
				Margin = new Thickness(0.0, 10.0, 0.0, 12.0)
			};
			stackPanel6.Children.Add(startupCheck);
			TextBlock startupInfo = new TextBlock
			{
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
			};
			stackPanel6.Children.Add(startupInfo);
			Action startupState = () =>
			{
				try
				{
					startupInfo.Text = (StartupService.Enabled ? "当前已开启。自动启动使用本程序所在路径；移动程序后，请关闭再开启以更新路径。" : "当前未开启。默认关闭，仅在你打开此开关后启用。");
				}
				catch (Exception ex2)
				{
					startupInfo.Text = "无法读取开机启动设置：" + ex2.Message;
				}
			};
			bool startupSync = false;
			int startupMode = 0;
			try
			{
				startupMode = StartupService.Mode;
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
			Action<bool> setStartup = (bool enabled2) =>
			{
				if (startupSync)
				{
					return;
				}
				try
				{
					StartupService.Set(enabled2, startupMode);
					startupState();
				}
				catch (Exception ex2)
				{
					startupSync = true;
					try
					{
						startupCheck.IsChecked = StartupService.Enabled;
					}
					catch
					{
						startupCheck.IsChecked = false;
					}
					startupSync = false;
					System.Windows.MessageBox.Show(w, ex2.Message, "无法更新开机启动");
				}
			};
			startupCheck.Checked += (object s, RoutedEventArgs e) =>
			{
				setStartup(obj: true);
			};
			startupCheck.Unchecked += (object s, RoutedEventArgs e) =>
			{
				setStartup(obj: false);
			};
			startupState();
			// 开机启动优先级：立即启动（注册表 Run，登录即拉起）或桌面就绪后启动（启动文件夹，稍晚但更稳）。
			func4(stackPanel6, "开机启动优先级", new string[2] { "登录后立即启动（最快）", "桌面就绪后启动（稍晚，更稳）" }, startupMode, (int mode) =>
			{
				startupMode = mode;
				if (startupCheck.IsChecked == true)
				{
					try
					{
						StartupService.Set(enabled: true, mode);
					}
					catch (Exception ex2)
					{
						System.Windows.MessageBox.Show(w, ex2.Message, "无法更新开机启动");
					}
				}
			});
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "关闭此开关会移除本软件的启动项。便携更新请保留完整程序文件夹，双击 EXE 即可运行。",
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap
			});
			func3(stackPanel6, "自动识别桌面新增图标并加入启动台", Prefs.AutoDesktopImport, (bool autoDesktopImport) =>
			{
				Prefs.AutoDesktopImport = autoDesktopImport;
				Save();
			});
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "开启后，桌面上新建的快捷方式（.lnk / .url / .exe）会自动出现在启动台大面板里，不会自动固定到 Dock。",
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap
			});
			System.Windows.Controls.CheckBox cleanCheck = func3(stackPanel6, "自动清除桌面上已归类到面板的图标", Prefs.AutoCleanDesktopIcons, delegate(bool on)
			{
				SetDesktopClean(on);
			});
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "开启后，凡是已经收进面板（Dock / 启动台）的桌面快捷方式和文件，会从桌面隐藏起来，让桌面只留下还没归类的东西。只改文件的「隐藏 + 系统」两个属性（这样即使资源管理器开着「显示隐藏的项目」也不会只显示成灰图标），不会删除、移动或修改文件内容；关掉开关会把这两个属性原样去掉、图标恢复显示。也可以随时点下面的「恢复桌面所有图标」或右键托盘图标恢复；恢复时会把这个开关一起关掉，免得下一次同步又把图标藏回去。",
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap
			});
			// 文档 / 文件夹默认不收起：它们一旦被标成「隐藏 + 系统」，网站的上传对话框
			// 会直接过滤掉，用户就选不到桌面上的文件（问题 2）。
			func3(stackPanel6, "把文档和文件夹也一起从桌面收起（隐藏后文件对话框里看不到）", Prefs.AutoCleanFiles, delegate(bool on)
			{
				Prefs.AutoCleanFiles = on;
				Save();
				if (Prefs.AutoCleanDesktopIcons)
				{
					int num = DesktopCleaner.Apply(Prefs.Apps, Prefs.AutoCleanFiles);
					Board.SetStatus((num > 0) ? ("已更新 " + num + " 个桌面条目") : "桌面上没有需要改动的条目");
					Board.RefreshIcons();
				}
			});
			// 面板增删图标后会自动同步（900ms 防抖）；这里是手动补一次，方便刚整理完立刻看到效果。
			WrapPanel desktopCleanRow = new WrapPanel
			{
				Margin = new Thickness(0.0, 8.0, 0.0, 4.0)
			};
			desktopCleanRow.Children.Add(FeatureUi.Button("立即同步桌面图标", () =>
			{
				SyncDesktopCleanNow();
			}));
			desktopCleanRow.Children.Add(FeatureUi.Button("恢复桌面所有图标", () =>
			{
				if (cleanCheck != null && cleanCheck.IsChecked == true)
				{
					// 先按正常流程关掉开关（去掉历史里记录的条目），再整体兜底扫一遍桌面。
					cleanCheck.IsChecked = false;
				}
				RestoreDesktopIcons();
			}));
			desktopCleanRow.Children.Add(FeatureUi.Button("以管理员身份隐藏公共桌面的图标", () =>
			{
				// 公共桌面（C:\Users\Public\Desktop）是所有用户共用的，普通权限改不了属性：
				// 只在用户主动点击时开一个提权实例，让它只做这一件事。
				try
				{
					Process.Start(new ProcessStartInfo
					{
						FileName = Assembly.GetExecutingAssembly().Location,
						Arguments = "--hide-public-desktop",
						UseShellExecute = true,
						Verb = "runas"
					});
				}
				catch (Exception ex)
				{
					Store.Log(ex);
					Board.SetStatus("已取消提权，公共桌面的图标没有改动");
				}
			}));
			stackPanel6.Children.Add(desktopCleanRow);
			// 自动隐藏 Windows 任务栏：直接写入系统 AppBar 的 ABS_AUTOHIDE 位（与托盘「Windows 任务栏自动隐藏」同源）。
			Func<bool> taskbarAutoHideState = () =>
			{
				try
				{
					return Prefs.AutoHideTaskbar;
				}
				catch
				{
					return false;
				}
			};
			bool taskbarSync = false;
			System.Windows.Controls.CheckBox taskbarAutoHideCheck = null;
			// 「彻底隐藏任务栏」与「自动隐藏」互斥，两个开关需要互相同步视觉状态。
			bool hideTaskbarSync = false;
			System.Windows.Controls.CheckBox hideTaskbarCheck = null;
			taskbarAutoHideCheck = func3(stackPanel6, "自动隐藏 Windows 任务栏（鼠标移到屏幕边缘时弹出）", taskbarAutoHideState(), delegate(bool on)
			{
				if (taskbarSync)
				{
					return;
				}
				try
				{
					ToggleTaskbarAutoHide(on);
					if (on)
					{
						// 自动隐藏与彻底隐藏互斥：开启自动隐藏时同步取消彻底隐藏的勾选。
						hideTaskbarSync = true;
						if (hideTaskbarCheck != null)
						{
							hideTaskbarCheck.IsChecked = false;
						}
						hideTaskbarSync = false;
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
					taskbarSync = true;
					taskbarAutoHideCheck.IsChecked = taskbarAutoHideState();
					taskbarSync = false;
				}
			});
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "开启后由 Windows 原生接管：任务栏平时收起，鼠标移到屏幕底部（或任务栏所在边缘）自动弹出。与 Dock 的「隐藏任务栏」互斥，开启本项会自动关闭彻底隐藏。",
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap
			});
			// 彻底隐藏 Windows 任务栏：SW_HIDE 掉全部任务栏窗口（Shell_TrayWnd / Shell_SecondaryTrayWnd），
			// 并挂 WinEvent 钩子在 Explorer 试图重新显示时立刻压回，所以鼠标触边、按 Win 键都不会弹出。
			hideTaskbarCheck = func3(stackPanel6, "彻底隐藏 Windows 任务栏（鼠标靠边也不会弹出）", DesktopVisibility.DesiredHidden(taskbar: true), delegate(bool on)
			{
				if (hideTaskbarSync)
				{
					return;
				}
				try
				{
					ShellAction(1, on);
					if (on)
					{
						taskbarSync = true;
						if (taskbarAutoHideCheck != null)
						{
							taskbarAutoHideCheck.IsChecked = false;
						}
						taskbarSync = false;
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
					hideTaskbarSync = true;
					hideTaskbarCheck.IsChecked = DesktopVisibility.DesiredHidden(taskbar: true);
					hideTaskbarSync = false;
				}
			});
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "开启后任务栏被直接隐藏（不是 Windows 的「自动隐藏」）：鼠标移到屏幕底部、按 Win 键都不会把它呼出来，适合把任务栏完全交给 Dock。与上面的「自动隐藏」互斥，开启本项会自动关闭自动隐藏。关闭开关即恢复任务栏；程序退出时也会自动恢复。",
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap
			});
			// 任务栏透明：对应 Windows 的「透明效果」开关，写注册表后广播重绘，立即生效。
			bool transparencySync = false;
			System.Windows.Controls.CheckBox taskbarTransparencyCheck = null;
			taskbarTransparencyCheck = func3(stackPanel6, "Windows 任务栏透明（关闭后任务栏变为不透明纯色）", TaskbarTransparency.Enabled, delegate(bool on)
			{
				if (transparencySync)
				{
					return;
				}
				if (!TaskbarTransparency.Set(on))
				{
					transparencySync = true;
					taskbarTransparencyCheck.IsChecked = TaskbarTransparency.Enabled;
					transparencySync = false;
				}
			});
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "对应 Windows 设置 → 个性化 → 颜色 → 透明效果。开启时任务栏透出壁纸颜色（亚克力半透明），关闭后变为不透明纯色；改完立即生效，无需重启资源管理器。这是 Windows 系统级设置，会同时影响开始菜单与操作中心的透明效果。",
				Foreground = Tokens.LabelSecondary(settingsLight),
				TextWrapping = TextWrapping.Wrap
			});
			BuildGameSettings(stackPanel6);
			stackPanel6.Children.Add(FeatureUi.Button("找回旧版入口", () =>
			{
				RecoverHistoricalEntries(w);
			}));
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "在线更新",
				FontSize = 18.0,
				Margin = new Thickness(0.0, 22.0, 0.0, 10.0)
			});
			TextBlock updateStatus = new TextBlock
			{
				Text = "当前版本 v" + OnlineUpdate.CurrentVersion() + " · 更新只替换程序文件，配置与自定义图标不受影响。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Tokens.LabelSecondary(settingsLight)
			};
			System.Windows.Controls.ProgressBar updateProgress = new System.Windows.Controls.ProgressBar
			{
				Height = 6.0,
				Maximum = 100.0,
				Visibility = Visibility.Collapsed
			};
			WrapPanel updateRow = new WrapPanel
			{
				Margin = new Thickness(0.0, 12.0, 0.0, 6.0)
			};
			updateRow.Children.Add(FeatureUi.Button("检查更新", async () =>
			{
				await OnlineUpdate.CheckManualAsync(w, updateStatus, updateProgress);
			}));
			stackPanel6.Children.Add(updateRow);
			stackPanel6.Children.Add(updateProgress);
			stackPanel6.Children.Add(updateStatus);
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "完整备份与恢复",
				FontSize = 18.0,
				Margin = new Thickness(0.0, 22.0, 0.0, 10.0)
			});
			stackPanel6.Children.Add(new TextBlock
			{
				Text = "备份布局、快捷键、外观方案与自定义图标。软件文件保留在原位置；开机启动按当前电脑设置保留。",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Tokens.LabelSecondary(settingsLight)
			});
			WrapPanel wrapPanel4 = new WrapPanel
			{
				Margin = new Thickness(0.0, 12.0, 0.0, 6.0)
			};
			wrapPanel4.Children.Add(FeatureUi.Button("导出完整备份", () =>
			{
				ExportFullBackup();
			}));
			wrapPanel4.Children.Add(FeatureUi.Button("恢复完整备份", () =>
			{
				w.Close();
				Dock.Dispatcher.BeginInvoke((Action)(() =>
				{
					RestoreFullBackup();
				}));
			}));
			stackPanel6.Children.Add(wrapPanel4);
			w.Show();
			// 分类面板是 Topmost 的玻璃窗（GlassWindow 构造函数统一 Topmost = true），普通设置窗会被它压在下面；
			// 从面板打开设置时把设置窗也置顶，保证「设置界面显示在分类面板之上」（关闭时 cleanup 复位）。
			w.Topmost = keepPanel && Board.IsVisible;
			// 设置窗口开着时把 Dock 常驻显示：调 Dock DIY / 面板主题可以当场看到效果。
			Dock.PinForPreview(true);
			RunningWindows.SettingsWindowHandle = new WindowInteropHelper(w).Handle;
			Store.Trace("SETTINGS open hwnd=" + RunningWindows.SettingsWindowHandle.ToInt64());
			flag = true;
		}
		finally
		{
			if (!flag)
			{
				if (settingsWindow != null)
				{
					settingsWindow.Close();
				}
				cleanup();
			}
		}
	}

	public void ShellAction(int action, bool? hidden = null)
	{
		try
		{
			string text;
			switch (action)
			{
			case 1:
			{
				bool flag = hidden ?? (!DesktopVisibility.DesiredHidden(taskbar: true));
				if (flag && TaskbarAutoHide.Enabled)
				{
					TaskbarAutoHide.Set(enabled: false);
				}
				text = DesktopVisibility.SetHidden(taskbar: true, flag);
				Prefs.HideTaskbar = flag;
				Prefs.AutoHideTaskbar = false;
				Save();
				break;
			}
			case 2:
				text = (hidden.HasValue ? DesktopVisibility.SetHidden(taskbar: false, hidden.Value) : DesktopVisibility.ToggleIcons());
				Prefs.HideDesktopIcons = DesktopVisibility.DesiredHidden(taskbar: false);
				Save();
				break;
			default:
				try
				{
					TaskbarAutoHide.ShowAndClear();
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
				DesktopVisibility.ShowAll();
				Prefs.HideTaskbar = false;
				Prefs.HideDesktopIcons = false;
				Prefs.AutoHideTaskbar = false;
				Save();
				text = "已执行任务栏与桌面图标恢复";
				break;
			}
			Board.SetStatus(text);
			if (Tray != null)
			{
				Tray.ShowBalloonTip(1500, "IconBox", text, ToolTipIcon.Info);
			}
		}
		catch (Exception ex2)
		{
			Store.Log(ex2);
			System.Windows.MessageBox.Show("无法切换系统界面：" + ex2.Message, "IconBox");
		}
	}

	public void Quit()
	{
		if (ShuttingDown)
		{
			return;
		}
		try
		{
			if (settingsWindow != null)
			{
				settingsWindow.Close();
			}
			Store.Save(Prefs);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			System.Windows.MessageBox.Show("退出前保存失败，软件仍保持运行，避免丢失当前内容。\n" + ex.Message, "IconBox");
			return;
		}
		ShuttingDown = true;
		try
		{
			ControlCenter?.Close();
		}
		catch
		{
		}
		try
		{
			DesktopVisibility.RestoreOriginal();
		}
		catch (Exception ex2)
		{
			Store.Log(ex2);
		}
		try
		{
			TaskbarAutoHide.Restore();
		}
		catch (Exception ex3)
		{
			Store.Log(ex3);
		}
		if (Tray != null)
		{
			Tray.Visible = false;
			Tray.Dispose();
		}
		Dock.ReleaseHotkey();
		System.Windows.Application.Current.Shutdown();
	}

	private void RecoverHistoricalEntries(Window owner)
	{
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			List<Entry> list = EntryRecoveryRules.Candidates(Prefs, Store.HistoricalEntries());
			if (list.Count == 0)
			{
				System.Windows.MessageBox.Show(owner, "历史配置中没有找到缺失入口。若旧版已多次覆盖配置，仍可能需要从你保留的完整备份中恢复。", "找回旧版入口");
				return;
			}
			Window w = FeatureUi.Dialog("找回旧版入口", owner, 760.0, 580.0);
			DockPanel dockPanel = new DockPanel
			{
				Margin = new Thickness(22.0)
			};
			w.Content = dockPanel;
			TextBlock element = new TextBlock
			{
				Text = "从升级前配置、安全副本和完整备份中找回入口。勾选后合并到当前布局；当前 DIY 保留。标注为已移除的入口默认不勾选。",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
			};
			DockPanel.SetDock(element, System.Windows.Controls.Dock.Top);
			dockPanel.Children.Add(element);
			WrapPanel wrapPanel = new WrapPanel();
			DockPanel.SetDock(wrapPanel, System.Windows.Controls.Dock.Bottom);
			dockPanel.Children.Add(wrapPanel);
			StackPanel stackPanel = new StackPanel();
			dockPanel.Children.Add(new ScrollViewer
			{
				Content = stackPanel,
				VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
			});
			Dictionary<Entry, System.Windows.Controls.CheckBox> choices = new Dictionary<Entry, System.Windows.Controls.CheckBox>();
			foreach (Entry item in list)
			{
				bool flag = RemovalRules.Blocked(Prefs, item);
				System.Windows.Controls.CheckBox checkBox = new System.Windows.Controls.CheckBox();
				checkBox.Content = new TextBlock
				{
					Text = item.Name + (flag ? " 【曾移除】" : "") + "\n" + item.Path,
					TextWrapping = TextWrapping.Wrap
				};
				checkBox.IsChecked = !flag;
				checkBox.Margin = new Thickness(0.0, 6.0, 0.0, 10.0);
				System.Windows.Controls.CheckBox checkBox2 = checkBox;
				choices.Add(item, checkBox2);
				stackPanel.Children.Add(checkBox2);
			}
			wrapPanel.Children.Add(FeatureUi.Button("全选", () =>
			{
				foreach (System.Windows.Controls.CheckBox value in choices.Values)
				{
					value.IsChecked = true;
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("取消全选", () =>
			{
				foreach (System.Windows.Controls.CheckBox value2 in choices.Values)
				{
					value2.IsChecked = false;
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("合并所选入口", () =>
			{
				List<Entry> list2 = (from x in choices
					where x.Value.IsChecked == true
					select x.Key).ToList();
				if (list2.Count != 0)
				{
					FlushBackupState();
					string text = Path.Combine(Store.Folder, "backups", "找回入口前_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".gbbackup");
					Directory.CreateDirectory(Path.GetDirectoryName(text));
					BackupFiles.Create(Store.Folder, text);
					BoardEditState before = BeginBoardEdit();
					int num = EntryRecoveryRules.Restore(Prefs, list2);
					EndBoardEdit(before, "找回旧版入口");
					Refresh();
					System.Windows.MessageBox.Show(w, "已合并 " + num + " 个入口。", "找回完成");
					w.Close();
				}
			}));
			w.ShowDialog();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			System.Windows.MessageBox.Show(owner, ex.Message, "找回未完成");
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
	}

	public DockPinResult PinDockPaths(IEnumerable<string> paths, DockDropTarget target)
	{
		List<Entry> list = new List<Entry>();
		foreach (string item in paths.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(item))
			{
				continue;
			}
			string path = EntryPathRules.Normalize(item);
			Entry entry = Prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
			if (entry == null)
			{
				if (!File.Exists(path) && !Directory.Exists(path))
				{
					continue;
				}
				entry = new Entry
				{
					Name = EntryPathRules.Name(path),
					Path = path,
					IsFolder = Directory.Exists(path)
				};
				LaunchInfoService.Enrich(entry);
			}
			if (string.IsNullOrEmpty(entry.LaunchTarget) && File.Exists(entry.Path))
			{
				LaunchInfoService.Enrich(entry);
			}
			list.Add(entry);
		}
		BoardEditState before = BeginBoardEdit();
		DockPinResult dockPinResult = DockPinRules.Apply(Prefs, list, target);
		DockLayoutRules.SyncApps(Prefs);
		if (target != null)
		{
			IEnumerable<string> enumerable;
			if (!target.Before)
			{
				enumerable = dockPinResult.Paths.AsEnumerable().Reverse();
			}
			else
			{
				IEnumerable<string> paths2 = dockPinResult.Paths;
				enumerable = paths2;
			}
			foreach (string item2 in enumerable)
			{
				DockLayoutRules.Move(Prefs, item2, target.Path, target.Before);
			}
		}
		EndBoardEdit(before, "拖入 Dock 固定");
		Dock.Refresh(capture: false);
		Board.Render();
		Board.SetStatus((dockPinResult.Paths.Count == 0) ? "未添加入口" : ("已放入 Dock " + dockPinResult.Paths.Count + " 个 · Ctrl+Z 撤销" + ((dockPinResult.Skipped > 0) ? ("；已达 20 个上限，跳过 " + dockPinResult.Skipped + " 个") : "")));
		return dockPinResult;
	}

	private void MigrateRemovalIdentities()
	{
		if (removalMigrated)
		{
			return;
		}
		removalMigrated = true;
		foreach (Entry item in Prefs.Apps.Where((Entry a) => string.IsNullOrWhiteSpace(a.LaunchTarget)))
		{
			try
			{
				LaunchInfoService.Enrich(item);
			}
			catch
			{
			}
		}
		foreach (string path in Prefs.Removed.ToList())
		{
			if (Prefs.Apps.Any((Entry a) => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)) || (!File.Exists(path) && !Directory.Exists(path)))
			{
				continue;
			}
			try
			{
				Entry removed = LaunchInfoService.Read(path);
				if (!Prefs.Apps.Any((Entry a) => RemovalRules.Same(a, removed)))
				{
					RemovalRules.Remember(Prefs, removed);
				}
			}
			catch
			{
			}
		}
		RemovalRules.ReconcileActive(Prefs);
		Save();
	}

	public void ChooseDockFiles()
	{
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
			{
				Title = "添加软件或文件到 Dock",
				Multiselect = true,
				Filter = "所有文件|*.*"
			};
			if (openFileDialog.ShowDialog(OwnerWindow) != true)
			{
				return;
			}
			Add(openFileDialog.FileNames, manual: true);
			BoardEditState before = BeginBoardEdit();
			string[] fileNames = openFileDialog.FileNames;
			foreach (string path in fileNames)
			{
				Entry entry = Prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, EntryPathRules.Normalize(path), StringComparison.OrdinalIgnoreCase));
				if (entry != null && !entry.Pinned && Prefs.Apps.Count((Entry a) => a.Pinned) < 20)
				{
					entry.Pinned = true;
				}
			}
			EndBoardEdit(before, "添加到 Dock");
			Refresh();
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
	}

	public void ChooseDockFolder()
	{
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			string text = FeatureUi.PickFolder(OwnerWindow, "选择要固定到 Dock 的文件夹");
			if (text == null)
			{
				return;
			}
			Add(new string[1] { text }, manual: true);
			BoardEditState before = BeginBoardEdit();
			Entry entry = Prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, EntryPathRules.Normalize(text), StringComparison.OrdinalIgnoreCase));
			if (entry != null && !entry.Pinned && Prefs.Apps.Count((Entry a) => a.Pinned) < 20)
			{
				entry.Pinned = true;
			}
			EndBoardEdit(before, "添加文件夹到 Dock");
			Refresh();
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
	}

	public void SetGameMode(bool enabled)
	{
		Prefs.GameMode = enabled;
		Save();
		ApplyGameMode();
		RefreshTrayMenu();
	}

	public void CheckGameMode()
	{
		if (Prefs.AutoGameMode || Prefs.GameMode || glassSuppressed)
		{
			ApplyGameMode();
		}
	}

	public void ApplyGameMode()
	{
		Dock.GamePolling(Prefs.AutoGameMode);
		bool flag = GameModeRules.Suppressed(Prefs.GameMode, Prefs.AutoGameMode, Prefs.AutoGameMode && FullscreenState.Active(), desktop: false);
		if (flag == glassSuppressed)
		{
			return;
		}
		glassSuppressed = flag;
		if (flag)
		{
			Board.Hide();
			Dock.HideWithConsent();
		}
		else
		{
			Dock.Refresh(capture: false);
			Dock.Show();
		}
		if (Tray != null)
		{
			RefreshTrayMenu();
		}
	}

	private void BuildGameSettings(StackPanel panel)
	{
		panel.Children.Add(new TextBlock
		{
			Text = "游戏模式",
			FontSize = 18.0,
			Margin = new Thickness(0.0, 22.0, 0.0, 10.0)
		});
		System.Windows.Controls.CheckBox checkBox = new System.Windows.Controls.CheckBox
		{
			Content = "游戏模式：隐藏 Dock 和大面板",
			IsChecked = Prefs.GameMode,
			Margin = new Thickness(0.0, 6.0, 0.0, 8.0)
		};
		panel.Children.Add(checkBox);
		checkBox.Checked += (object s, RoutedEventArgs e) =>
		{
			SetGameMode(enabled: true);
		};
		checkBox.Unchecked += (object s, RoutedEventArgs e) =>
		{
			SetGameMode(enabled: false);
		};
		System.Windows.Controls.CheckBox checkBox2 = new System.Windows.Controls.CheckBox
		{
			Content = "检测全屏应用时自动进入（默认关闭）",
			IsChecked = Prefs.AutoGameMode,
			Margin = new Thickness(0.0, 6.0, 0.0, 8.0)
		};
		panel.Children.Add(checkBox2);
		checkBox2.Checked += (object s, RoutedEventArgs e) =>
		{
			Prefs.AutoGameMode = true;
			Save();
			ApplyGameMode();
		};
		checkBox2.Unchecked += (object s, RoutedEventArgs e) =>
		{
			Prefs.AutoGameMode = false;
			Save();
			ApplyGameMode();
		};
		panel.Children.Add(new TextBlock
		{
			Text = "游戏模式期间玻璃窗口完全隐藏，避免触碰底边误唤醒。右键系统托盘可退出；自动模式离开全屏后恢复 Dock 和大面板。设置不会被清空。",
			TextWrapping = TextWrapping.Wrap
		});
	}

	private void BuildMaterialEditor(StackPanel panel, string title, MaterialOptions options, Action changed)
	{
		options.Normalize();
		panel.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 17.0,
			Margin = new Thickness(0.0, 18.0, 0.0, 8.0)
		});
		System.Windows.Controls.ComboBox types = new System.Windows.Controls.ComboBox
		{
			ItemsSource = new string[4] { "磨砂玻璃", "清透玻璃", "亚克力", "渐变玻璃" },
			SelectedIndex = options.Kind,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		panel.Children.Add(types);
		types.SelectionChanged += (object s, SelectionChangedEventArgs e) =>
		{
			if (types.SelectedIndex >= 0)
			{
				options.Kind = types.SelectedIndex;
				changed();
			}
		};
		System.Windows.Controls.TextBox tint = new System.Windows.Controls.TextBox
		{
			Text = options.Tint,
			MinHeight = 36.0,
			ToolTip = "输入 #RRGGBB，例如 #334A70；留空使用当前主题颜色"
		};
		panel.Children.Add(new TextBlock
		{
			Text = "材质色调（#RRGGBB，留空随主题）",
			Margin = new Thickness(0.0, 6.0, 0.0, 5.0)
		});
		panel.Children.Add(tint);
		panel.Children.Add(FeatureUi.Button("应用色调", () =>
		{
			string text = tint.Text.Trim();
			if (text.Length > 0 && !Regex.IsMatch(text, "^#[a-fA-F0-9]{6}$"))
			{
				System.Windows.MessageBox.Show("请输入 #RRGGBB 格式的颜色，例如 #334A70。", "IconBox");
			}
			else
			{
				options.Tint = text;
				changed();
			}
		}));
		Action<string, double, Action<double>> action = (string label, double value, Action<double> set) =>
		{
			TextBlock caption = new TextBlock
			{
				Text = label + " " + Math.Round(value * 100.0) + "%",
				Margin = new Thickness(0.0, 8.0, 0.0, 4.0)
			};
			panel.Children.Add(caption);
			Slider control = new Slider
			{
				Minimum = 0.0,
				Maximum = 1.0,
				Value = value,
				TickFrequency = 0.05,
				IsSnapToTickEnabled = true
			};
			panel.Children.Add(control);
			control.ValueChanged += (object s, RoutedPropertyChangedEventArgs<double> e) =>
			{
				set(control.Value);
				caption.Text = label + " " + Math.Round(control.Value * 100.0) + "%";
				changed();
			};
		};
		action("反光", options.Gloss, (double v) =>
		{
			options.Gloss = v;
		});
		action("细纹理", options.Grain, (double v) =>
		{
			options.Grain = v;
		});
		System.Windows.Controls.CheckBox checkBox = new System.Windows.Controls.CheckBox
		{
			Content = "显示材质阴影",
			IsChecked = options.Shadow,
			Margin = new Thickness(0.0, 10.0, 0.0, 5.0)
		};
		panel.Children.Add(checkBox);
		checkBox.Checked += (object s, RoutedEventArgs e) =>
		{
			options.Shadow = true;
			changed();
		};
		checkBox.Unchecked += (object s, RoutedEventArgs e) =>
		{
			options.Shadow = false;
			changed();
		};
	}

	public BoardEditState BeginBoardEdit()
	{
		return BoardEditState.Capture(Prefs);
	}

	public void EndBoardEdit(BoardEditState before, string label)
	{
		editHistory.Remember(before, Prefs, label);
		Save();
	}

	public void UndoBoard()
	{
		string text = editHistory.Undo(Prefs);
		if (text != null)
		{
			Board.FinishSelection();
			Refresh();
			Board.SetStatus("已撤销：" + text);
		}
	}

	public void RedoBoard()
	{
		string text = editHistory.Redo(Prefs);
		if (text != null)
		{
			Board.FinishSelection();
			Refresh();
			Board.SetStatus("已重做：" + text);
		}
	}

	public void ReorderEntry(string path, Entry target)
	{
		Entry entry = Prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
		if (entry != null && entry != target)
		{
			BoardEditState before = BeginBoardEdit();
			int val = Prefs.Apps.IndexOf(target);
			Prefs.Apps.Remove(entry);
			Prefs.Apps.Insert(Math.Min(val, Prefs.Apps.Count), entry);
			EndBoardEdit(before, "调整图标顺序");
			Refresh();
		}
	}

	// 拖动排序提交：sequence 是拖放结束时当前页磁贴的最终顺序（含被拖项），
	// sliceOffset 是当前页在 Apps 列表中的起始下标。仅当顺序实际变化时才写入历史。
	public bool ReorderToSequence(string[] sequence, int sliceOffset)
	{
		List<Entry> sliceNow = Prefs.Apps.Skip(sliceOffset).Take(sequence.Length).ToList();
		if (sliceNow.Count != sequence.Length)
		{
			return false;
		}
		// 安全网：过滤视图下页内顺序与 Apps 切片不一致，直接放弃提交（交由 Render 复原）
		HashSet<string> seqSet = new HashSet<string>(sequence, StringComparer.OrdinalIgnoreCase);
		if (seqSet.Count != sliceNow.Count || sliceNow.Any((Entry a) => !seqSet.Contains(a.Path)))
		{
			return false;
		}
		List<Entry> target = new List<Entry>();
		foreach (string path in sequence)
		{
			Entry entry = Prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
			if (entry == null)
			{
				return false;
			}
			target.Add(entry);
		}
		bool changed = false;
		for (int i = 0; i < target.Count; i++)
		{
			if (sliceNow[i] != target[i])
			{
				changed = true;
				break;
			}
		}
		if (!changed)
		{
			return false;
		}
		BoardEditState before = BeginBoardEdit();
		foreach (Entry item in sliceNow)
		{
			Prefs.Apps.Remove(item);
		}
		for (int i = 0; i < target.Count; i++)
		{
			Prefs.Apps.Insert(sliceOffset + i, target[i]);
		}
		EndBoardEdit(before, "调整图标顺序");
		Refresh();
		Board.Render(animate: true);
		return true;
	}

	// 分类视图下的拖动排序提交。
	// Prefs.Apps 是「全部」的全局顺序，过滤视图里同一页的图标在 Apps 中并不连续，
	// 所以不能按页切片写回。做法是：把这一页涉及的 Apps 槽位按新顺序重新填一遍，
	// 其它分类的图标保持所在槽位不动（只调整相对顺序，不跨分类搬运）。
	public bool ReorderCategorySequence(string categoryId, string[] sequence)
	{
		if (string.IsNullOrEmpty(categoryId) || sequence == null || sequence.Length == 0)
		{
			return false;
		}
		// 参与排序的是「该分类全部分页」的槽位；本页顺序只覆盖本页那一段。
		List<int> slots = new List<int>();
		Dictionary<int, Entry> slotEntry = new Dictionary<int, Entry>();
		for (int i = 0; i < Prefs.Apps.Count; i++)
		{
			Entry app = Prefs.Apps[i];
			if (app == null || !string.Equals(AppCategoryRules.Resolve(app), categoryId, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			slots.Add(i);
			slotEntry[i] = app;
		}
		if (slots.Count == 0)
		{
			return false;
		}
		// 找到本页图标在分类内序列中的起始偏移。
		// 拖动之后 sequence[0] 已经是新顺序的第一个图标，不能再用它定位页首；
		// 这里用「本页图标 Path 集合 == 分类序列某一段的 Path 集合」精确匹配出 offset。
		int offset = -1;
		HashSet<string> seqPaths = new HashSet<string>(sequence.Where((string p) => p != null), StringComparer.OrdinalIgnoreCase);
		for (int cand2 = 0; cand2 + sequence.Length <= slots.Count && offset < 0; cand2++)
		{
			HashSet<string> seg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int m3 = 0; m3 < sequence.Length; m3++)
			{
				Entry slot4 = slotEntry[slots[cand2 + m3]];
				if (slot4 != null && slot4.Path != null)
				{
					seg.Add(slot4.Path);
				}
			}
			if (seg.SetEquals(seqPaths))
			{
				offset = cand2;
			}
		}
		if (offset < 0 || offset + sequence.Length > slots.Count)
		{
			return false;
		}
		List<Entry> categoryOrder = slots.Select((int s) => slotEntry[s]).ToList();
		// 按 Path 分组取候选：同一个快捷方式有可能被收录多条（Path 相同、Entry 不同），
		// 用 Dictionary<string,Entry> 会退化成「最后一条」，重排后同一 Entry 被写进多个槽位，
		// 表现为图标重复、原位置条目丢失。这里按出现顺序逐个消费候选，保持「一对一」。
		Dictionary<string, Queue<Entry>> byPath = new Dictionary<string, Queue<Entry>>(StringComparer.OrdinalIgnoreCase);
		foreach (Entry entry in categoryOrder)
		{
			if (entry == null || entry.Path == null)
			{
				continue;
			}
			Queue<Entry> queue;
			if (!byPath.TryGetValue(entry.Path, out queue))
			{
				queue = new Queue<Entry>();
				byPath[entry.Path] = queue;
			}
			queue.Enqueue(entry);
		}
		// 先把候选队列按本页序列的顺序消费一遍，确保每个 sequence 项拿到一个未被占用的 Entry。
		List<Entry> picked = new List<Entry>(sequence.Length);
		for (int k = 0; k < sequence.Length; k++)
		{
			Queue<Entry> queue2;
			if (sequence[k] == null || !byPath.TryGetValue(sequence[k], out queue2) || queue2.Count == 0)
			{
				return false;
			}
			picked.Add(queue2.Dequeue());
		}
		// 序列里的 Path 集合必须与本页覆盖的槽位集合完全一致，否则说明状态已错位，放弃提交。
		HashSet<Entry> pickedSet = new HashSet<Entry>(picked);
		if (pickedSet.Count != picked.Count)
		{
			return false;
		}
		HashSet<Entry> sliceSet = new HashSet<Entry>();
		for (int j2 = 0; j2 < sequence.Length; j2++)
		{
			Entry slotNow = categoryOrder[offset + j2];
			if (slotNow != null)
			{
				sliceSet.Add(slotNow);
			}
		}
		if (!pickedSet.SetEquals(sliceSet))
		{
			return false;
		}
		bool changed = false;
		for (int k2 = 0; k2 < sequence.Length; k2++)
		{
			if (!ReferenceEquals(categoryOrder[offset + k2], picked[k2]))
			{
				changed = true;
			}
			categoryOrder[offset + k2] = picked[k2];
		}
		if (!changed)
		{
			return false;
		}
		BoardEditState before = BeginBoardEdit();
		for (int m = 0; m < slots.Count; m++)
		{
			Prefs.Apps[slots[m]] = categoryOrder[m];
		}
		EndBoardEdit(before, "调整图标顺序");
		Refresh();
		Board.Render(animate: true);
		return true;
	}

	private void FlushBackupState()
	{
		Store.Save(Prefs);
	}

	public void ExportFullBackup()
	{
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
			{
				FileName = "IconBox_完整备份_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".gbbackup",
				Filter = "IconBox 完整备份|*.gbbackup"
			};
			if (saveFileDialog.ShowDialog(OwnerWindow) == true)
			{
				FlushBackupState();
				BackupFiles.Create(Store.Folder, saveFileDialog.FileName);
				System.Windows.MessageBox.Show(OwnerWindow, "布局、快捷键、外观方案与自定义图标已备份。", "备份完成");
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			System.Windows.MessageBox.Show(OwnerWindow, ex.Message, "备份失败");
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
	}

	public void RestoreFullBackup()
	{
		if (settingsWindow != null)
		{
			settingsWindow.Close();
		}
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
			{
				Filter = "IconBox 完整备份|*.gbbackup"
			};
			if (openFileDialog.ShowDialog(OwnerWindow) != true)
			{
				return;
			}
			BackupFiles.Read(openFileDialog.FileName);
			if (System.Windows.MessageBox.Show(OwnerWindow, "恢复此备份中的布局、快捷键与便签？\n当前配置会先自动保存为恢复前备份。开机启动开关按当前电脑保留。", "恢复完整备份", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
			{
				FlushBackupState();
				string text = Path.Combine(Store.Folder, "backups", "恢复前_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".gbbackup");
				Directory.CreateDirectory(Path.GetDirectoryName(text));
				BackupFiles.Create(Store.Folder, text);
				List<HotkeySetting> hotkeys = Prefs.Hotkeys.Select((HotkeySetting k) => k.Copy()).ToList();
				BackupFiles.Restore(openFileDialog.FileName, Store.Folder);
				Prefs = Store.Load(mergeJournal: false);
				bool flag = Dock.ApplyHotkeys(Prefs.Hotkeys, out var error);
				if (!flag)
				{
					Prefs.Hotkeys = hotkeys;
				}
				editHistory.Clear();
				bool isVisible = Board.IsVisible;
				Refresh();
				RefreshTrayMenu();
				if (isVisible)
				{
					Board.Open();
				}
				System.Windows.MessageBox.Show(OwnerWindow, "配置已恢复。恢复前备份已保存到：\n" + text + (flag ? "" : ("\n快捷键与系统冲突，已保留原快捷键：" + error)), "恢复完成");
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			System.Windows.MessageBox.Show(OwnerWindow, ex.Message, "恢复失败");
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
	}

	public void AddFolder()
	{
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			string text = FeatureUi.PickFolder(OwnerWindow, "选择要放入玻璃板的文件夹（添加文件夹本身）");
			if (text != null)
			{
				Add(new string[1] { text });
				Board.ClearSearch();
				Board.SetStatus("已添加文件夹入口 · 点击打开，右键可固定到 Dock");
			}
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
	}

	// —— 分类栏编辑（顺序 / 名称 / 增删）——
	// 分类列表由 AppCategoryRules 持有运行时状态，这里负责持久化到 Preferences 并刷新界面。
	private void PersistCategories()
	{
		Prefs.Categories = AppCategoryRules.Snapshot();
		Save();
		Board.RefreshCategoryBar();
	}

	public void ReorderCategories(IList<string> orderedIds)
	{
		if (AppCategoryRules.Reorder(orderedIds))
		{
			PersistCategories();
		}
	}

	public bool RenameCategory(string id, string name)
	{
		if (!AppCategoryRules.Rename(id, name))
		{
			return false;
		}
		PersistCategories();
		return true;
	}

	public string AddCategory(string name)
	{
		string text = AppCategoryRules.Add(name);
		if (text == null)
		{
			return null;
		}
		PersistCategories();
		return text;
	}

	public bool RemoveCategory(string id)
	{
		string removedName = AppCategoryRules.Name(id);
		if (!AppCategoryRules.Remove(id))
		{
			return false;
		}
		// 被删分类里的应用回到自动推断；若当前正停在该分类，切回“全部”。
		foreach (Entry app in Prefs.Apps)
		{
			if (app != null && !string.IsNullOrEmpty(app.Category) && string.Equals(app.Category.Trim(), id, StringComparison.OrdinalIgnoreCase))
			{
				app.Category = null;
			}
		}
		Save();
		Prefs.Categories = AppCategoryRules.Snapshot();
		Save();
		Board.RefreshCategoryBar();
		Board.ResetCategoryIfMissing();
		Board.SetStatus("已删除分类「" + removedName + "」");
		return true;
	}

	// 删除所有 0 项的分类（只保护「全部」，内置分类同样是空的就一起删）。
	// 返回实际删除的数量；有删除时分类栏会重建。
	public int RemoveEmptyCategories()
	{
		List<string> list = new List<string>();
		foreach (AppCategoryRules.Category category in AppCategoryRules.Categories)
		{
			string id = category.Id;
			if (string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (AppCategoryRules.Count(Prefs.Apps, id) <= 0)
			{
				list.Add(id);
			}
		}
		if (list.Count == 0)
		{
			return 0;
		}
		foreach (string id2 in list)
		{
			AppCategoryRules.Remove(id2);
			foreach (Entry app in Prefs.Apps)
			{
				if (app != null && !string.IsNullOrEmpty(app.Category) && string.Equals(app.Category.Trim(), id2, StringComparison.OrdinalIgnoreCase))
				{
					app.Category = null;
				}
			}
		}
		Save();
		Prefs.Categories = AppCategoryRules.Snapshot();
		Save();
		Board.RefreshCategoryBar();
		Board.ResetCategoryIfMissing();
		Board.SetStatus("已删除 " + list.Count + " 个空分类");
		return list.Count;
	}

	public void ResetCategories()
	{
		AppCategoryRules.ResetDefaults();
		Prefs.Categories = null;
		Save();
		Board.RefreshCategoryBar();
		Board.ResetCategoryIfMissing();
		Board.SetStatus("已恢复默认分类");
	}

	public async void ToggleTaskbarAutoHide(bool? enabled = null)
	{
		try
		{
			bool flag = enabled ?? (!TaskbarAutoHide.Enabled);
			if (flag)
			{
				DesktopVisibility.SetHidden(taskbar: true, hide: false);
			}
			TaskbarAutoHide.Set(flag);
			Prefs.AutoHideTaskbar = flag;
			Prefs.HideTaskbar = false;
			Save();
			Board.SetStatus("已请求" + (flag ? "启用" : "关闭") + " Windows 任务栏自动隐藏");
			await Task.Delay(250);
			if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.HasShutdownStarted)
			{
				Dock.Refresh(capture: false);
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			System.Windows.MessageBox.Show(ex.Message, "Windows 任务栏自动隐藏");
		}
	}

	public void Customize(Entry entry)
	{
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			Window w = FeatureUi.Dialog("名称与图标", OwnerWindow, 500.0, 280.0);
			StackPanel stackPanel = new StackPanel
			{
				Margin = new Thickness(22.0)
			};
			w.Content = stackPanel;
			stackPanel.Children.Add(new TextBlock
			{
				Text = "显示名称",
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			});
			System.Windows.Controls.TextBox name = new System.Windows.Controls.TextBox
			{
				Text = entry.Name,
				MinHeight = 38.0
			};
			stackPanel.Children.Add(name);
			string icon = entry.IconPath;
			TextBlock info = new TextBlock
			{
				Text = (string.IsNullOrWhiteSpace(icon) ? "使用软件原图标" : "已使用自定义图标"),
				Margin = new Thickness(0.0, 12.0, 0.0, 12.0)
			};
			stackPanel.Children.Add(info);
			WrapPanel wrapPanel = new WrapPanel();
			stackPanel.Children.Add(wrapPanel);
			wrapPanel.Children.Add(FeatureUi.Button("选择图标", () =>
			{
				Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
				{
					Filter = "图标图片|*.png;*.ico;*.jpg;*.jpeg;*.bmp"
				};
				if (openFileDialog.ShowDialog(w) == true)
				{
					try
					{
						icon = FeatureUi.StoreIcon(openFileDialog.FileName);
						info.Text = "已选择图标，点击保存应用";
					}
					catch (Exception ex)
					{
						System.Windows.MessageBox.Show(w, ex.Message, "无法读取图标");
					}
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("恢复原图标", () =>
			{
				icon = null;
				info.Text = "使用软件原图标";
			}));
			stackPanel.Children.Add(FeatureUi.Button("保存", () =>
			{
				if (!string.IsNullOrWhiteSpace(name.Text))
				{
					BoardEditState before = BeginBoardEdit();
					entry.Name = name.Text.Trim();
					entry.IconPath = icon;
					EndBoardEdit(before, "自定义名称与图标");
					Refresh();
					w.Close();
				}
			}));
			w.ShowDialog();
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
	}

	public bool ManageSchemes(Window owner = null)
	{
		bool applied = false;
		bool modalBusy = Board.ModalBusy;
		Board.ModalBusy = true;
		try
		{
			Window w = FeatureUi.Dialog("外观方案", owner ?? OwnerWindow, 650.0, 480.0);
			DockPanel dockPanel = new DockPanel
			{
				Margin = new Thickness(22.0)
			};
			w.Content = dockPanel;
			TextBlock element = new TextBlock
			{
				Text = "保存配色、尺寸、位置、图标和应用布局；切换保留后来新增的入口。",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
			};
			DockPanel.SetDock(element, System.Windows.Controls.Dock.Top);
			dockPanel.Children.Add(element);
			WrapPanel wrapPanel = new WrapPanel();
			DockPanel.SetDock(wrapPanel, System.Windows.Controls.Dock.Bottom);
			dockPanel.Children.Add(wrapPanel);
			System.Windows.Controls.ListBox list = new System.Windows.Controls.ListBox
			{
				Margin = new Thickness(0.0, 0.0, 0.0, 14.0),
				DisplayMemberPath = "Name"
			};
			dockPanel.Children.Add(list);
			Dictionary<AppearanceScheme, string> files = new Dictionary<AppearanceScheme, string>();
			Action reload = () =>
			{
				files.Clear();
				string[] array = SchemeStore.Files();
				foreach (string text in array)
				{
					try
					{
						AppearanceScheme key = SchemeStore.Load(text);
						files[key] = text;
					}
					catch
					{
					}
				}
				list.ItemsSource = null;
				list.ItemsSource = files.Keys.OrderBy((AppearanceScheme s) => s.Name).ToList();
			};
			reload();
			wrapPanel.Children.Add(FeatureUi.Button("保存当前", () =>
			{
				string text = FeatureUi.Ask(w, "方案名称");
				if (text != null)
				{
					AppearanceScheme data = FeatureRules.Capture(Prefs, text);
					SchemeStore.Embed(data);
					SchemeStore.Save(data, Path.Combine(SchemeStore.Folder, Guid.NewGuid().ToString("N") + ".json"));
					reload();
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("应用方案", () =>
			{
				if (!(list.SelectedItem is AppearanceScheme key))
				{
					return;
				}
				try
				{
					AppearanceScheme appearanceScheme = SchemeStore.Load(files[key]);
					SchemeStore.Materialize(appearanceScheme);
					FeatureRules.Apply(Prefs, appearanceScheme);
					editHistory.Clear();
					applied = true;
					Refresh();
					RefreshTrayMenu();
					Board.Hide();
					w.Close();
				}
				catch (Exception ex)
				{
					System.Windows.MessageBox.Show(w, ex.Message, "无法应用方案");
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("导出", () =>
			{
				if (list.SelectedItem is AppearanceScheme key)
				{
					Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
					{
						FileName = "IconBox_外观方案.json",
						Filter = "外观方案|*.json"
					};
					if (saveFileDialog.ShowDialog(w) == true)
					{
						File.Copy(files[key], saveFileDialog.FileName, overwrite: true);
					}
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("导入", () =>
			{
				Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
				{
					Filter = "外观方案|*.json"
				};
				if (openFileDialog.ShowDialog(w) != true)
				{
					return;
				}
				try
				{
					SchemeStore.Save(SchemeStore.Load(openFileDialog.FileName), Path.Combine(SchemeStore.Folder, Guid.NewGuid().ToString("N") + ".json"));
					reload();
				}
				catch (Exception ex)
				{
					System.Windows.MessageBox.Show(w, ex.Message, "无法导入方案");
				}
			}));
			wrapPanel.Children.Add(FeatureUi.Button("删除方案", () =>
			{
				if (list.SelectedItem is AppearanceScheme key && System.Windows.MessageBox.Show(w, "删除保存的方案？当前外观保持。", "IconBox", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
				{
					File.Delete(files[key]);
					reload();
				}
			}));
			w.ShowDialog();
		}
		finally
		{
			Board.ModalBusy = modalBusy;
		}
		if (applied && settingsWindow != null && owner != settingsWindow)
		{
			settingsWindow.Close();
		}
		return applied;
	}

	public bool ReorderDock(string path, string target, bool before)
	{
		BoardEditState before2 = BeginBoardEdit();
		if (!Dock.MoveLayout(path, target, before))
		{
			return false;
		}
		EndBoardEdit(before2, "调整 Dock 顺序");
		Dock.Refresh(capture: false);
		Board.Render();
		return true;
	}

	public void StepDock(string path, int step)
	{
		BoardEditState before = BeginBoardEdit();
		if (Dock.StepLayout(path, step))
		{
			EndBoardEdit(before, "调整 Dock 顺序");
			Dock.Refresh(capture: false);
			Board.Render();
		}
	}

	// 系统入口右键菜单"隐藏此入口"：等价于设置页中的入口开关（应用面板不可隐藏）。
	public void HideSystemEntry(string key)
	{
		if (key == DockLayoutRules.ControlCenter)
		{
			Prefs.Dock.ShowControlCenter = false;
		}
		else if (key == DockLayoutRules.RunningList)
		{
			Prefs.Dock.ShowRunningList = false;
		}
		else
		{
			return;
		}
		Store.Save(Prefs);
		Dock.Refresh(capture: false);
	}

	private void AddDockWindowMenus(System.Windows.Controls.ContextMenu menu, Entry entry)
	{
		if (entry.IsFolder || Directory.Exists(entry.Path))
		{
			return;
		}
		System.Windows.Controls.MenuItem opened = new System.Windows.Controls.MenuItem
		{
			Header = "切换已打开的窗口"
		};
		System.Windows.Controls.MenuItem associate = new System.Windows.Controls.MenuItem
		{
			Header = "关联正在运行的窗口",
			ToolTip = "用于游戏、特殊启动器或无法自动识别的软件；关联后点击切回该软件"
		};
		menu.Items.Add(opened);
		menu.Items.Add(associate);
		menu.Opened += (object s, RoutedEventArgs e) =>
		{
			// 用后台维护的窗口快照填充（无同步扫描）；随后异步增量刷新，完成后再重填一次
			Stopwatch timer = Stopwatch.StartNew();
			FillWindowMenu(opened, associate, entry);
			timer.Stop();
			Store.Trace("DOCKMENU windows ms=" + timer.ElapsedMilliseconds + " entry=" + entry.Name);
			RunningWindows.RefreshAsync(delegate
			{
				if (!menu.IsOpen)
				{
					return;
				}
				menu.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
				{
					if (menu.IsOpen)
					{
						FillWindowMenu(opened, associate, entry);
					}
				}));
			});
		};
	}

	private void FillWindowMenu(System.Windows.Controls.MenuItem opened, System.Windows.Controls.MenuItem associate, Entry entry)
	{
		opened.Items.Clear();
		associate.Items.Clear();
		List<RunningWindow> list = RunningWindows.For(entry);
		opened.IsEnabled = list.Count > 0;
		foreach (RunningWindow item in list)
		{
			RunningWindow selected = item;
			System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
			{
				Header = WindowLabel(selected),
				ToolTip = selected.Title
			};
			menuItem.Click += (object ss, RoutedEventArgs ee) =>
			{
				RunningWindows.ActivateHandle(selected.Handle);
			};
			FillWindowOps(menuItem, selected);
			opened.Items.Add(menuItem);
		}
		List<RunningWindow> list2 = RunningWindows.Snapshot();
		associate.IsEnabled = list2.Count > 0 || !string.IsNullOrEmpty(entry.WindowTarget);
		foreach (RunningWindow item2 in list2)
		{
			RunningWindow selected2 = item2;
			System.Windows.Controls.MenuItem menuItem2 = new System.Windows.Controls.MenuItem
			{
				Header = WindowLabel(selected2),
				ToolTip = selected2.Path,
				IsCheckable = true,
				IsChecked = string.Equals(entry.WindowTarget, selected2.Path, StringComparison.OrdinalIgnoreCase)
			};
			menuItem2.Click += (object ss, RoutedEventArgs ee) =>
			{
				BoardEditState before = BeginBoardEdit();
				entry.WindowTarget = selected2.Path;
				EndBoardEdit(before, "关联 Dock 窗口");
				Refresh();
			};
			associate.Items.Add(menuItem2);
		}
		if (!string.IsNullOrEmpty(entry.WindowTarget))
		{
			associate.Items.Add(new Separator());
			System.Windows.Controls.MenuItem menuItem3 = new System.Windows.Controls.MenuItem
			{
				Header = "取消手动关联"
			};
			menuItem3.Click += (object ss, RoutedEventArgs ee) =>
			{
				BoardEditState before = BeginBoardEdit();
				entry.WindowTarget = null;
				EndBoardEdit(before, "取消 Dock 窗口关联");
				Refresh();
			};
			associate.Items.Add(menuItem3);
		}
	}

	private static string WindowLabel(RunningWindow window)
	{
		string text = (string.IsNullOrWhiteSpace(window.Title) ? Path.GetFileNameWithoutExtension(window.Path) : window.Title);
		if (text.Length <= 72)
		{
			return text;
		}
		return text.Substring(0, 69) + "…";
	}
}
