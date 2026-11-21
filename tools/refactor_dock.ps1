$root = 'C:\Users\13793\Documents\deepseek-harness\default-workspace\iconbox\source\src\IconBox'
$enc = New-Object System.Text.UTF8Encoding($false)

function SaveFile($path, $lines) {
	$text = ($lines -join "`r`n")
	if ($lines[$lines.Count - 1] -ne '') { $text += "`r`n" }
	[IO.File]::WriteAllText($path, $text, $enc)
}

# ---------------- T3: DockWindow.cs ----------------
$dw = Join-Path $root 'DockWindow.cs'
$l = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($dw, [Text.Encoding]::UTF8))
if ($l[1095] -notmatch 'private void HideDockWithOverlay\(\)') { throw ('hide anchor: ' + $l[1095]) }
if ($l[1122].Trim() -ne '}') { throw ('hide end anchor: ' + $l[1122]) }
if ($l[1459] -notmatch 'double dpi = followDpi;') { throw ('sample anchor: ' + $l[1459]) }
if ($l[1479].Trim() -ne '}') { throw ('sample end anchor: ' + $l[1479]) }

# 1) 采样段（后段先改）
$sample = @'
			double dpi = followDpi;
			// 底边取 Dock 自身所在显示器的底边；任务栏露出高度由 Snapshot 一次采样给出
			// （句柄缓存 5 秒，Explorer 重启后自动失效重取）。
			double screenBottom = DockScreenBottomDip();
			int trayVisiblePx;
			int trayBottomPx;
			TaskbarState.Snapshot(out trayVisiblePx, out trayBottomPx);
			// 连续取值：任务栏露出多少，Dock 就抬高多少；收回时自然滑回屏幕最底。
			double taskbarDip = Math.Max(0.0, (double)trayVisiblePx / dpi);
			// 任务栏刚露头那几像素的高度读数会来回抖（1~3DIP），归零，避免 Dock 在底部轻微抽动。
			if (taskbarDip < 4.0)
			{
				taskbarDip = 0.0;
			}
'@
$l.RemoveRange(1459, 21)
$l.InsertRange(1459, [System.Collections.Generic.List[string]]($sample -split "`r?`n"))

# 2) HideDockWithOverlay 收敛（28 行）
$hide = @'
	private void HideDockWithOverlay()
	{
		if (!IsVisible || renderedHidden) return;
		dockDismissed = true;
		renderedHidden = true;
		if (C.Prefs.Dock.FollowTaskbar)
		{
			followTargetTop = double.NaN;
			followHoldUntil = DateTime.MinValue;
			followMoveTicks = 0L;
			ApplyZOrder(false);
		}
		AnimateDockVisibility(hide: true, DockHiddenTop());
		Store.Trace(C.Prefs.Dock.FollowTaskbar ? "DOCK hide with overlay follow" : "DOCK hide with overlay hover");
	}
'@
$l.RemoveRange(1095, 28)
$l.InsertRange(1095, [System.Collections.Generic.List[string]]($hide -split "`r?`n"))

# 3) 目标值辅助方法（插在「浮层因鼠标离开而收起」注释之前）
$helpers = @'
	// Dock 所在显示器的底边（DIP）：优先用 Dock 自身句柄取显示器，取不到才回退工作区底边。
	private double DockScreenBottomDip()
	{
		double dpi = (followDpi > 0.0) ? followDpi : VisualTreeHelper.GetDpi(this).DpiScaleY;
		if (dpi <= 0.0) dpi = 1.0;
		int bottomPx = TaskbarState.ScreenBottomPxFor(dockHandle);
		return (bottomPx > 0) ? ((double)bottomPx / dpi) : dockScreenArea.Bottom;
	}

	// Dock 收起后停的位置：跟随模式滑到屏幕底边外 8DIP，悬停模式只保留 20DIP 的露出边。
	private double DockHiddenTop()
	{
		return C.Prefs.Dock.FollowTaskbar ? (DockScreenBottomDip() + 8.0) : (dockScreenArea.Bottom - 20.0);
	}

'@
$l.InsertRange(1093, [System.Collections.Generic.List[string]]($helpers -split "`r?`n"))
SaveFile $dw $l
'dock ok'

# ---------------- T2: TaskbarState.cs ----------------
$ts = Join-Path $root 'TaskbarState.cs'
$t = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($ts, [Text.Encoding]::UTF8))
if ($t[46] -notmatch 'public static IntPtr Handle\(\)') { throw ('handle anchor: ' + $t[46]) }
if ($t[105] -notmatch 'public static int VisibleHeight\(\)') { throw ('vh anchor: ' + $t[105]) }
if ($t[146].Trim() -ne '}') { throw ('vh end anchor: ' + $t[146]) }

$t.RemoveRange(46, 5)
$v = @'
	// 主任务栏当前「可见高度」（物理像素）。隐藏时≈1~2px，弹出时为完整高度。
	// 只保留一条采样路径：内部复用 Snapshot（同一份句柄缓存与显示器求交）。
	public static int VisibleHeight()
	{
		int visiblePx;
		int monitorBottomPx;
		return Snapshot(out visiblePx, out monitorBottomPx) ? visiblePx : 0;
	}
'@
$t.RemoveRange(100, 42)
$t.InsertRange(100, [System.Collections.Generic.List[string]]($v -split "`r?`n"))
SaveFile $ts $t
'taskbar ok'
