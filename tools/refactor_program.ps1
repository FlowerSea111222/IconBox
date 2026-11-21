$p = 'C:\Users\13793\Documents\deepseek-harness\default-workspace\iconbox\source\src\IconBox\Program.cs'
$l = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($p, [Text.Encoding]::UTF8))
if ($l[48] -notmatch 'STAThread') { throw ('anchor 49 mismatch: ' + $l[48]) }
if ($l[51] -notmatch 'netease-ui-bridge') { throw ('anchor 52 mismatch: ' + $l[51]) }
if ($l[99].Trim() -ne '}') { throw ('anchor 100 mismatch: ' + $l[99]) }

$dispatch = @'
		// 命令行入口统一在这里分派：正常启动只有一条路径，后台/维护动作各走一个独立方法。
		switch (LaunchModeOf(args))
		{
		case LaunchMode.NeteaseUiBridge:
			return NeteaseUiReader.Run();
		case LaunchMode.SelfTest:
			return SelfTest.Run();
		case LaunchMode.HidePublicDesktop:
			return RunHidePublicDesktop();
		case LaunchMode.RestoreShell:
			return RunRestoreShell();
		}
'@

$helpers = @'
	internal enum LaunchMode
	{
		Normal,
		NeteaseUiBridge,
		SelfTest,
		HidePublicDesktop,
		RestoreShell
	}

	// 把 argv[0] 映射成一个模式：Main 之后只做一次 switch，不再堆一串并列的 if。
	private static LaunchMode LaunchModeOf(string[] args)
	{
		if (args.Length == 0)
		{
			return LaunchMode.Normal;
		}
		switch (args[0])
		{
		case "--netease-ui-bridge":
			return LaunchMode.NeteaseUiBridge;
		case "--self-test":
			return LaunchMode.SelfTest;
		case "--hide-public-desktop":
			return LaunchMode.HidePublicDesktop;
		case "--restore-shell":
			return LaunchMode.RestoreShell;
		default:
			return LaunchMode.Normal;
		}
	}

	// 以管理员身份隐藏公共桌面里「已收进面板」的快捷方式：由设置页按钮以 runas 启动，
	// 只做这一件事就退出，不进入常驻的主流程。
	private static int RunHidePublicDesktop()
	{
		try
		{
			Preferences preferences = Store.Load(mergeJournal: false);
			int num = DesktopCleaner.HidePublicDesktop(preferences?.Apps);
			MessageBox.Show((num > 0) ? ("已隐藏 " + num + " 个公共桌面的快捷方式。") : "公共桌面没有需要隐藏的快捷方式。", "IconBox");
			return 0;
		}
		catch (Exception ex)
		{
			MessageBox.Show(ex.Message, "IconBox");
			return 1;
		}
	}

	// 一键恢复任务栏与桌面图标：已有实例在跑就转发给它处理，否则本进程直接恢复。
	private static int RunRestoreShell()
	{
		try
		{
			if (Native.RequestRestore())
			{
				return 0;
			}
			try
			{
				TaskbarAutoHide.ShowAndClear();
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
			DesktopVisibility.ShowAll();
			MessageBox.Show("已执行任务栏和桌面图标恢复。", "IconBox");
			return 0;
		}
		catch (Exception ex2)
		{
			MessageBox.Show(ex2.Message, "IconBox");
			return 1;
		}
	}

'@

$newDispatch = [System.Collections.Generic.List[string]]($dispatch -split "`r?`n")
$newHelpers = [System.Collections.Generic.List[string]]($helpers -split "`r?`n")
# 去掉 here-string 末尾多出的空行
while ($newDispatch.Count -gt 0 -and $newDispatch[$newDispatch.Count - 1] -eq '') { $newDispatch.RemoveAt($newDispatch.Count - 1) }
while ($newHelpers.Count -gt 0 -and $newHelpers[$newHelpers.Count - 1] -eq '') { $newHelpers.RemoveAt($newHelpers.Count - 1) }

$l.RemoveRange(51, 49)
$l.InsertRange(51, $newDispatch)
$l.InsertRange(48, $newHelpers)

$text = ($l -join "`r`n")
if ($l[$l.Count - 1] -ne '') { $text += "`r`n" }
[IO.File]::WriteAllText($p, $text, (New-Object System.Text.UTF8Encoding($false)))
'written ' + $l.Count + ' lines'
