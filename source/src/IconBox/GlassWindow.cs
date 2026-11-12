using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IconBox;

public class GlassWindow : Window
{
	protected Grid Root = new Grid();

	protected Border Surface = new Border();

	protected Grid ContentGrid = new Grid();

	protected bool LightTheme;
	// 非分层模式：放弃 AllowsTransparency（分层窗口在桌面成为前台时 DWM 不合成其内容，
	// 导致 Win+D 后 Dock 不可见）。改为不透明窗口 + 圆角 Region 裁剪，DWM 稳定合成。
	protected bool NonLayered;

	protected double GlassPercent = 72.0;

	// 软件渲染（RenderCapability.Tier < 2）时整窗阴影/背景模糊极贵，自动降级保证动画流畅
	private static readonly bool SlowRender = (RenderCapability.Tier >> 16) < 2;

	// 阴影/毛玻璃效果实例复用：每次打开面板不再重建 Effect（重建会强制整窗重新栅格化，造成首帧卡顿）
	private DropShadowEffect surfaceShadow;

	private System.Windows.Media.Effects.BlurEffect backdropBlur;

	private BitmapImage cachedBackdrop;

	private string backdropKey = "";

	private string materialSig = "";

	private double lastGlassPercent = -1.0;

	private bool glassApplied;

	protected bool MotionEnabled = true;

	// 主动隐藏的通行证：游戏模式等主动 Hide() 前先置位；外部广播（显示桌面等）引发的
	// WM_SHOWWINDOW(hide) 没有通行证一律拦截，防止 Dock/组件被系统藏掉。
	internal bool ExternalHideAllowed;

	internal void HideWithConsent()
	{
		ExternalHideAllowed = true;
		Hide();
	}

	// —— 显示桌面（Win+D / 任务栏「显示桌面」）守护 ——
	// Explorer 在「显示桌面」时把桌面窗口 Progman 提到最前，并把 Dock 这类工具窗口
	// 排到它后面；Dock 自身 Topmost=false（避免盖住全屏应用），于是看起来就是
	// 「按 Win+D 后 Dock 消失」。这里的做法是：前台一旦变回桌面，就立即把本窗口
	// 重新排到桌面窗口之上（仍在普通层，不抢普通应用的位置）。
	private static readonly IntPtr HwndTopMost = new IntPtr(-1);

	internal bool KeepsAboveDesktop;

	private bool topmostGuardArmed;

	private Native.WinEventDelegate desktopWatchProc;
	private IntPtr desktopWatchHook = IntPtr.Zero;
	private System.Windows.Threading.DispatcherTimer desktopGuardTimer;
	private int desktopGuardTicks;

	// 判断本窗口是否仍需压在桌面之上；需要时立即重新排到桌面窗口之后（桌面之上）。
	// 前台不是桌面时是否仍需保持 Topmost（默认 false：降回普通层，避免浮在其它应用之上）。
	// Dock 覆写为「仅当用户开启『始终置顶』时才保持」。
	protected virtual bool KeepTopmostWhenNotDesktop()
	{
		return false;
	}

	// 显示/隐藏与 z 序是否已由子类自己的状态机负责（Dock 跟随任务栏时就是这种情况）。
	// 为 true 时这里完全不再抬升窗口：否则本守护的 HWND_TOPMOST 与子类的 HWND_BOTTOM
	// 会按帧互相打回，表现为 Win+D 后闪烁、位置跳动，最后停在窗口之上。
	protected virtual bool ManagesOwnDesktopVisibility()
	{
		return false;
	}

	// 是否需要窗口投影（Dock 贴屏底常驻，投影只会在机身外侧留下一圈淡阴影，故 Dock 直接关闭）。
	protected virtual bool UsesWindowShadow()
	{
		return true;
	}

	internal void GuardAgainstDesktop()
	{
		try
		{
			if (!IsLoaded || !KeepsAboveDesktop)
			{
				return;
			}
			if (ManagesOwnDesktopVisibility())
			{
				if (desktopGuardTimer != null)
				{
					desktopGuardTimer.Stop();
				}
				return;
			}
			// 只在「前台确实是桌面」时才抬升：平时保持普通层（不遮其它应用）。
			// 若前台不是桌面（例如浏览器/全屏应用在前），主动把窗口降回非置顶，
			// 以免此前被抬为 TOPMOST 后一直浮在其它窗口之上（用户反馈：Dock 浮在浏览器上层）。
			if (!DesktopWindowPolicy.DesktopForeground())
			{
				if (!KeepTopmostWhenNotDesktop())
				{
					try { Topmost = false; } catch { }
				}
				return;
			}
			IntPtr handle = new WindowInteropHelper(this).Handle;
			if (handle == IntPtr.Zero)
			{
				return;
			}
			// 立即尝试一次单拍抬升。
			BurstLift(handle);
			// 启动异步多拍重试：Explorer「显示桌面」会把工具窗口反复打回隐藏，单拍常只闪一帧。
			// 用 DispatcherTimer 每 ~50ms 重复抬升若干拍，全程不阻塞 UI 线程。
			desktopGuardTicks = 0;
			if (desktopGuardTimer == null)
			{
				desktopGuardTimer = new System.Windows.Threading.DispatcherTimer();
				desktopGuardTimer.Interval = TimeSpan.FromMilliseconds(50.0);
				desktopGuardTimer.Tick += (object s, EventArgs e) =>
				{
					desktopGuardTicks++;
					IntPtr h = new WindowInteropHelper(this).Handle;
					if (!IsLoaded || !KeepsAboveDesktop || h == IntPtr.Zero || desktopGuardTicks > 24)
					{
						desktopGuardTimer.Stop();
						return;
					}
					BurstLift(h);
				};
			}
			desktopGuardTimer.Stop();
			desktopGuardTimer.Start();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 单拍抬升：被隐藏则先 SHOWNOACTIVATE，再置顶到桌面之上。
	private void BurstLift(IntPtr handle)
	{
		try
		{
			// 每拍都无条件先 SW_SHOWNOACTIVATE：Explorer 每次打回隐藏后，HWND 可见性可能短暂为真，
			// 若只在「不可见时」再 Show 会漏掉这一拍，导致画面闪烁不定。
			DesktopWindowPolicy.ShowNoActivate(handle);
			DesktopWindowPolicy.LiftAboveDesktop(handle);
			// 强制 DWM 重新合成：启动首帧或 Win+D 后，即便样式正确 DWM 也可能不画，翻转一次 ex-style 即可上屏。
			DesktopWindowPolicy.ForceRecomposite(handle);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 非分层窗口的圆角裁剪：用 CreateRoundRectRgn + SetWindowRgn 让不透明窗口也有圆角。
	// Region 坐标基于窗口自身的物理像素（相对窗口左上角），radius 为 DIP 需按 DPI 换算。
	private void ApplyRoundedRegion(double radius)
	{
		try
		{
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			if (hwnd == IntPtr.Zero) return;
			if (!Native.GetWindowRect(hwnd, out Native.RECT r)) return;
			int w = r.Right - r.Left, h = r.Bottom - r.Top;
			if (w <= 0 || h <= 0) return;
			int dpi = Native.DpiOf(hwnd);
			int er = (int)Math.Round(radius * 2.0 * dpi / 96.0);
			IntPtr rgn = Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, er, er);
			if (rgn != IntPtr.Zero) Native.SetWindowRgn(hwnd, rgn, true);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 前台变化监听：只在前台确实变成桌面窗口时才动作，避免和普通窗口切换互相抢层级。
	// 用 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 而不是 SystemEvents.ForegroundChange
	// （后者在 net48 的 Microsoft.Win32.SystemEvents 里没有该事件）。
	internal void ArmDesktopWatch()
	{
		if (topmostGuardArmed)
		{
			return;
		}
		topmostGuardArmed = true;
		desktopWatchProc = OnDesktopForegroundChanged;
		IntPtr handle = new WindowInteropHelper(this).Handle;
		desktopWatchHook = Native.SetWinEventHook(3u, 3u, handle, desktopWatchProc, 0u, 0u, 0u);
		if (desktopWatchHook == IntPtr.Zero)
		{
			Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook: desktop foreground watch"));
		}
		Closed += (object s, EventArgs e) =>
		{
			if (desktopWatchHook != IntPtr.Zero)
			{
				Native.UnhookWinEvent(desktopWatchHook);
				desktopWatchHook = IntPtr.Zero;
			}
		};
	}

	private void OnDesktopForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
	{
		try
		{
			if (!KeepsAboveDesktop || !IsLoaded)
			{
				return;
			}
			if (!DesktopWindowPolicy.DesktopForeground())
			{
				return;
			}
			// 让 Explorer 把桌面窗口排完序之后再压回去。
			Dispatcher.BeginInvoke((Action)(() => Dispatcher.BeginInvoke((Action)GuardAgainstDesktop)));
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	protected double MotionLevel = 1.0;

	protected double BlurRadius = 18.0;

	protected double EdgeStrength = 1.0;

	protected MaterialOptions CurrentMaterial = new MaterialOptions();

	protected int AccentIndex;

	protected double MaterialBlur
	{
		get
		{
			if (CurrentMaterial.Kind != 1)
			{
				if (CurrentMaterial.Kind != 2)
				{
					return BlurRadius;
				}
				return Math.Min(40.0, BlurRadius * 1.3);
			}
			return BlurRadius * 0.25;
		}
	}

	protected System.Windows.Media.Brush TextBrush => new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromRgb(25, 28, 34) : System.Windows.Media.Color.FromRgb(247, 247, 249));

	protected System.Windows.Media.Brush MutedBrush => new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromRgb(65, 69, 77) : System.Windows.Media.Color.FromRgb(184, 188, 198));

	protected void ConfigureGlass(DockOptions options, MaterialOptions material = null)
	{
		BlurRadius = options.BlurRadius;
		EdgeStrength = options.EdgeStrength;
		AccentIndex = options.Accent;
		CurrentMaterial = (material ?? options.Material ?? new MaterialOptions()).Copy();
	}

	public void SetMaterial(MaterialOptions material)
	{
		CurrentMaterial = material.Copy();
		SetGlassOpacity(GlassPercent);
		RefreshBackdropStyle();
	}

	protected static ControlTemplate GripTemplate(bool corner = false)
	{
		FrameworkElementFactory frameworkElementFactory;
		if (corner)
		{
			// macOS 风格缩放角标：两条圆头斜线，替代原来的 “◢” 字符。
			frameworkElementFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
			frameworkElementFactory.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M2.5,10.5 L10.5,2.5 M2.5,5.5 L5.5,2.5"));
			frameworkElementFactory.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 180, 195, 215)));
			frameworkElementFactory.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.4);
			frameworkElementFactory.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
			frameworkElementFactory.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
			frameworkElementFactory.SetValue(FrameworkElement.WidthProperty, 13.0);
			frameworkElementFactory.SetValue(FrameworkElement.HeightProperty, 13.0);
		}
		else
		{
			frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
			frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.0));
			frameworkElementFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 180, 195, 215)));
		}
		return new ControlTemplate(typeof(Thumb))
		{
			VisualTree = frameworkElementFactory
		};
	}

	public GlassWindow(double radius, bool nonLayered = false)
	{
		NonLayered = nonLayered;
		WindowStyle = WindowStyle.None;
		AllowsTransparency = !NonLayered;
		// 非分层窗口背景：与玻璃底色同调（46,50,60），使 Region 硬裁剪的边缘与玻璃同色，
		// 避免深色底 (28,34,44) 在圆角/直边交界处露出硬边造成「毛糙」观感。
		Background = (NonLayered ? (System.Windows.Media.Brush)new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 50, 60)) : System.Windows.Media.Brushes.Transparent);
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		Topmost = true;
		Content = Root;
		// 非分层模式无阴影留白（Region 已裁剪圆角），分层模式保留 14px 阴影缓冲。
		Root.Margin = new Thickness(NonLayered ? 0.0 : 14.0);
		Surface.CornerRadius = new CornerRadius(radius);
		Surface.BorderThickness = new Thickness(1.0);
		// 统一阴影：更大更柔（贴近 macOS 浮窗的扩散感），由 DesignTokens 单点维护。
		surfaceShadow = Tokens.WindowShadow();
		Surface.Effect = (UsesWindowShadow() ? surfaceShadow : null);
		Root.Children.Add(Surface);
		Root.Children.Add(ContentGrid);
		SourceInitialized += (object s, EventArgs e) =>
		{
			if (this is DockWindow)
			{
				DesktopWindowPolicy.Apply(this);
			}
			// 窗口生命周期诊断：记录首次句柄创建的相对时刻，用于对照黑屏发生前的操作时序。
			Store.Trace("WINDOW init cls=" + GetType().Name + " ms=" + (Environment.TickCount - Program.ProcessStartTick));
			try
			{
				if (!Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0u))
				{
					Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowDisplayAffinity: allow desktop capture"));
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
			// 非分层模式：用 Win32 Region 给窗口做圆角裁剪（分层窗口本可透明圆角，
			// 但分层窗口在桌面前台时 DWM 不合成，故 Dock 改非分层 + Region 裁剪）。
			if (NonLayered)
			{
				ApplyRoundedRegion(radius);
				// 尺寸变化时重建 Region（Region 坐标基于物理像素，需按 DPI 换算）。
				SizeChanged += (object s2, System.Windows.SizeChangedEventArgs e2) => ApplyRoundedRegion(radius);
			}
			// 屏蔽「显示桌面」的最小化广播：Dock 与组件保持可见（与系统任务栏同等待遇）。
			// 不校验 lParam：键盘触发（Win+D / Win+M）时 lParam=0，点击任务栏右侧
			// 「显示桌面」竖条触发时 lParam 为鼠标坐标——两种都要拦，且本窗口没有
			// 最小化按钮，任何外部 SC_MINIMIZE 都是广播而非用户主动操作。
			HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
			{
				if (msg == 274 && ((wp.ToInt64() & 0xFFF0) == 0xF020))
				{
					handled = true;
					return IntPtr.Zero;
				}
				// 拦截外部隐藏：显示桌面若以 ShowWindow(SW_HIDE) 方式收起工具窗口
				// （toolwindow 不参与最小化流程），会在 WM_SHOWWINDOW(wParam=0) 出现。
				// 只有带通行证的主动 Hide()（游戏模式等）放行，其余一律拦下。
				if (this is DockWindow && msg == 24 && wp == IntPtr.Zero)
				{
					if (ExternalHideAllowed)
					{
						ExternalHideAllowed = false;
					}
					else
					{
						handled = true;
						return IntPtr.Zero;
					}
				}
				// 显示器变化、DPI 变化 → 背景缓存失效并延迟重捕，避免 Dock 上残留旧壁纸底图。
				if (msg == 0x001A && wp.ToInt64() == 0x14)
				{
					Store.Trace("RENDER wallpaper change → backdrop refresh");
					ScheduleBackdropRefresh(LightTheme);
				}
				else if (msg == 0x007E)
				{
					Store.Trace("RENDER display change → backdrop refresh");
					ScheduleBackdropRefresh(LightTheme);
				}
				else if (msg == 0x02E0)
				{
					Store.Trace("RENDER dpi change → backdrop refresh");
					ScheduleBackdropRefresh(LightTheme);
				}
				// 「显示桌面」（Win+D / 任务栏右下角竖条）会把桌面窗口提到最前并把
				// 工具窗口排到它后面：拦下最小化/隐藏还不够，这里在前台切换、窗口位置
				// 变化、以及 Explorer 的「显示桌面」通知后重新压回桌面之上。
				if (msg == 0x0006 || msg == 0x0047)
				{
					Dispatcher.BeginInvoke((Action)GuardAgainstDesktop);
				}
				else if (msg == 0x0318 && wp.ToInt64() == 0x052C)
				{
					Dispatcher.BeginInvoke((Action)GuardAgainstDesktop);
				}
				return IntPtr.Zero;
			});
		};
		StateChanged += (object s, EventArgs e) =>
		{
			// 兜底：若显示桌面仍以其他途径最小化本窗口，立即还原（Dock/组件无最小化需求）。
			// 注意不能加 IsVisible 判断——WPF 中最小化状态的窗口 IsVisible 恒为 false，
			// 加了该条件这段兜底就是死代码（正是此前 Win+D 仍然失效的原因）。
			if (WindowState == WindowState.Minimized)
			{
				WindowState = WindowState.Normal;
			}
		};
	}

	protected Button Button(string label, RoutedEventHandler click, double width = 80.0)
	{
		Button b = new Button
		{
			Content = label,
			Width = width,
			Height = 36.0,
			Margin = new Thickness(4.0),
			Foreground = TextBrush,
			Cursor = Cursors.Hand
		};
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12.0));
		frameworkElementFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(LightTheme ? 38 : 25), byte.MaxValue, byte.MaxValue, byte.MaxValue)));
		frameworkElementFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(42, byte.MaxValue, byte.MaxValue, byte.MaxValue)));
		frameworkElementFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(ContentPresenter));
		frameworkElementFactory2.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		frameworkElementFactory2.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		b.Template = new ControlTemplate(typeof(Button))
		{
			VisualTree = frameworkElementFactory
		};
		b.MouseEnter += (object s, MouseEventArgs e) =>
		{
			if (MotionEnabled)
			{
				b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.78, TimeSpan.FromMilliseconds(120.0)));
			}
			else
			{
				b.Opacity = 0.78;
			}
		};
		b.MouseLeave += (object s, MouseEventArgs e) =>
		{
			if (MotionEnabled)
			{
				b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150.0)));
			}
			else
			{
				b.Opacity = 1.0;
			}
		};
		b.PreviewMouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(MotionEnabled ? 65 : 0)));
		};
		b.PreviewMouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(MotionEnabled ? 100 : 0)));
		};
		if (click != null)
		{
			b.Click += click;
		}
		return b;
	}

	public void Backdrop(bool light)
	{
		LightTheme = light;
		SetGlassOpacity(GlassPercent);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? ((System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent) : ((System.Windows.Media.Brush)new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 145.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 35.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), 90.0)));
		if (IsVisible)
		{
			RefreshBackdropStyle();
			return;
		}
		(from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b).ToList().ForEach((Border b) =>
		{
			Root.Children.Remove(b);
		});
		if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
		{
			return;
		}
		IntPtr hwnd;
		try
		{
			hwnd = new WindowInteropHelper(this).EnsureHandle();
		}
		catch (InvalidOperationException)
		{
			// 窗口已被 Close()：WPF 禁止为已关闭窗口重建句柄，直接放弃本次背景捕获。
			// （控制中心/小组件关闭后仍可能有刷新路径触发 Backdrop，属正常竞态。）
			return;
		}
		if (!Native.GetWindowRect(hwnd, out Native.RECT rect) || rect.Right - rect.Left < 56 || rect.Bottom - rect.Top < 56)
		{
			return;
		}
		// 物理像素坐标捕获：不再用 TransformToDevice（主屏 DPI 矩阵在混合 DPI 多显示器下算错区域）。
		// 缓存键用物理矩形 + DPI，显示器/DPI/壁纸变化后自然失效重捕。
		int dpi = Native.DpiOf(hwnd);
		string key = rect.Left + "_" + rect.Top + "_" + rect.Right + "_" + rect.Bottom + "_" + dpi + "_" + (LightTheme ? 1 : 0);
		if (cachedBackdrop != null && backdropKey == key)
		{
			AddBackdrop(cachedBackdrop);
			return;
		}
		CaptureRegion(rect, dpi);
	}

	private void RemoveBackdropBorders()
	{
		(from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b).ToList().ForEach((Border b) =>
		{
			Root.Children.Remove(b);
		});
	}

	// 捕获指定物理矩形区域作为毛玻璃底图（1/8 缩放 PNG 往返，写入 cachedBackdrop/backdropKey 并挂载）。
	private void CaptureRegion(Native.RECT rect, int dpi)
	{
		long captureStart = Environment.TickCount;
		try
		{
			int margin = (int)Math.Round((NonLayered ? 0.0 : 14.0) * dpi / 96.0);
			int sourceX = rect.Left + margin;
			int sourceY = rect.Top + margin;
			int num = Math.Max(1, rect.Right - rect.Left - 2 * margin);
			int num2 = Math.Max(1, rect.Bottom - rect.Top - 2 * margin);
			using Bitmap bitmap = new Bitmap(num, num2);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.CopyFromScreen(sourceX, sourceY, 0, 0, new System.Drawing.Size(num, num2));
			}
			using Bitmap bitmap2 = new Bitmap(bitmap, Math.Max(1, num / 8), Math.Max(1, num2 / 8));
			using MemoryStream memoryStream = new MemoryStream();
			bitmap2.Save(memoryStream, ImageFormat.Png);
			memoryStream.Position = 0L;
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.StreamSource = memoryStream;
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			cachedBackdrop = bitmapImage;
			backdropKey = rect.Left + "_" + rect.Top + "_" + rect.Right + "_" + rect.Bottom + "_" + dpi + "_" + (LightTheme ? 1 : 0);
			AddBackdrop(cachedBackdrop);
			if (RenderDiagnostics.Verbose)
			{
				Store.Trace("RENDER capture cls=" + GetType().Name + " rect=" + rect.Left + "," + rect.Top + " " + num + "x" + num2 + " ms=" + (Environment.TickCount - captureStart) + " rel=" + (Environment.TickCount - Program.ProcessStartTick));
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private System.Windows.Threading.DispatcherTimer backdropRefreshTimer;

	// 壁纸/显示器/DPI 变化后的重捕调度：1.2 秒合并窗口，多个广播（切换壁纸会连发多条）只捕一次。
	public void ScheduleBackdropRefresh(bool light)
	{
		if (backdropRefreshTimer == null)
		{
			backdropRefreshTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(1200.0)
			};
			backdropRefreshTimer.Tick += delegate
			{
				backdropRefreshTimer.Stop();
				RecaptureBackdrop(LightTheme);
			};
		}
		backdropRefreshTimer.Stop();
		backdropRefreshTimer.Start();
	}

	// 可见状态下重捕：临时把窗口移出屏幕（避免 CopyFromScreen 捕到自身形成反馈），
	// 按原物理矩形捕获后再移回并刷新画刷。仅在壁纸/显示变化这类低频事件触发，不会高频闪动。
	public void RecaptureBackdrop(bool light)
	{
		if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
		{
			cachedBackdrop = null;
			backdropKey = "";
			return;
		}
		if (!IsVisible)
		{
			cachedBackdrop = null;
			backdropKey = "";
			Backdrop(light);
			return;
		}
		try
		{
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			if (!Native.GetWindowRect(hwnd, out Native.RECT rect) || rect.Right - rect.Left < 56 || rect.Bottom - rect.Top < 56)
			{
				return;
			}
			int dpi = Native.DpiOf(hwnd);
			double oldLeft = Left;
			double oldTop = Top;
			Left = -20000.0;
			Top = -20000.0;
			cachedBackdrop = null;
			backdropKey = "";
			RemoveBackdropBorders();
			CaptureRegion(rect, dpi);
			Left = oldLeft;
			Top = oldTop;
			RefreshBackdropStyle();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private void AddBackdrop(BitmapImage bitmapImage)
	{
		if (!SlowRender && backdropBlur == null)
		{
			backdropBlur = new BlurEffect
			{
				Radius = MaterialBlur,
				RenderingBias = RenderingBias.Performance
			};
		}		Border border = new Border
		{
			Tag = "backdrop",
			CornerRadius = Surface.CornerRadius,
			Background = new ImageBrush(bitmapImage)
			{
				Stretch = Stretch.Fill
			},
			Opacity = (LightTheme ? 0.8 : 0.66)
		};
		border.Clip = new RectangleGeometry(new Rect(0.0, 0.0, Width - 28.0, Height - 28.0), Surface.CornerRadius.TopLeft, Surface.CornerRadius.TopLeft);
		border.Effect = backdropBlur;
		Border element = new Border
		{
			Tag = "backdrop",
			Clip = border.Clip,
			Child = border,
			Opacity = GlassPercent / 100.0
		};
		Root.Children.Insert(0, element);
	}

	private void RefreshBackdropStyle()
	{
		if (!SlowRender && backdropBlur == null)
		{
			backdropBlur = new BlurEffect
			{
				Radius = MaterialBlur,
				RenderingBias = RenderingBias.Performance
			};
		}
		foreach (Border item in from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b)
		{
			double bw = Math.Max(1.0, Width - (NonLayered ? 0.0 : 28.0));
			double bh = Math.Max(1.0, Height - (NonLayered ? 0.0 : 28.0));
			RectangleGeometry clip = (RectangleGeometry)(item.Clip = new RectangleGeometry(new Rect(0.0, 0.0, bw, bh), Surface.CornerRadius.TopLeft, Surface.CornerRadius.TopLeft));
			if (item.Child is Border border)
			{
				border.Clip = clip;
				border.CornerRadius = Surface.CornerRadius;
				border.Opacity = (LightTheme ? 0.8 : 0.66);
				border.Effect = backdropBlur;
			}
		}
	}

	protected void ReleaseBackdrop()
	{
		(from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b).ToList().ForEach((Border b) =>
		{
			Root.Children.Remove(b);
		});
	}

	public void SetGlassOpacity(double percent)
	{
		GlassPercent = LayoutRules.GlassOpacity(percent);
		string sig = (LightTheme ? 1 : 0) + "|" + CurrentMaterial.Kind + "|" + CurrentMaterial.Gloss + "|" + CurrentMaterial.Grain + "|" + CurrentMaterial.Tint + "|" + AccentIndex + "|" + CurrentMaterial.Shadow;
		if (glassApplied && sig == materialSig && Math.Abs(GlassPercent - lastGlassPercent) < 0.001)
		{
			return;
		}
		lastGlassPercent = GlassPercent;
		materialSig = sig;
		glassApplied = true;
		if (surfaceShadow == null)
		{
			surfaceShadow = Tokens.WindowShadow();
		}
		Surface.Background = GlassMaterials.Fill(CurrentMaterial, LightTheme, GlassPercent, AccentIndex);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? System.Windows.Media.Brushes.Transparent : Polish.Line(LightTheme));
		Surface.Effect = ((GlassPercent == 0.0 || !CurrentMaterial.Shadow || SlowRender || !UsesWindowShadow()) ? null : surfaceShadow);
		foreach (Border item in from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b)
		{
			item.Opacity = GlassPercent / 100.0;
		}
		Border border = Root.Children.OfType<Border>().FirstOrDefault((Border b) => b.Tag as string == "material");
		if (border == null)
		{
			border = new Border
			{
				Tag = "material",
				IsHitTestVisible = false
			};
			Root.Children.Insert(Math.Min(1, Root.Children.Count), border);
		}
		border.CornerRadius = Surface.CornerRadius;
		Grid grid = (Grid)(border.Child = new Grid());
		border.Opacity = GlassPercent / 100.0;
		if (CurrentMaterial.Gloss > 0.0)
		{
			grid.Children.Add(new Border
			{
				CornerRadius = Surface.CornerRadius,
				Background = new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Round(CurrentMaterial.Gloss * 90.0), byte.MaxValue, byte.MaxValue, byte.MaxValue), Colors.Transparent, 90.0)
			});
		}
		if (CurrentMaterial.Grain > 0.0)
		{
			grid.Children.Add(new Border
			{
				CornerRadius = Surface.CornerRadius,
				Background = new ImageBrush(GlassMaterials.Grain())
				{
					TileMode = TileMode.Tile,
					ViewportUnits = BrushMappingMode.Absolute,
					Viewport = new Rect(0.0, 0.0, 32.0, 32.0)
				},
				Opacity = CurrentMaterial.Grain
			});
		}
	}

	protected void AnimateOpen()
	{
		BeginAnimation(UIElement.OpacityProperty, null);
		Root.RenderTransform = Transform.Identity;
		Opacity = 1.0;
		if (!MotionEnabled)
		{
			return;
		}
		double level = Math.Max(0.15, MotionLevel);
		Root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		ScaleTransform scaleTransform = new ScaleTransform(1.0 - 0.035 * level, 1.0 - 0.035 * level);
		TranslateTransform translateTransform = new TranslateTransform(0.0, 14.0 * level);
		TransformGroup transformGroup = new TransformGroup();
		transformGroup.Children.Add(scaleTransform);
		transformGroup.Children.Add(translateTransform);
		Root.RenderTransform = transformGroup;
		TimeSpan duration = TimeSpan.FromMilliseconds(260.0);
		CubicEase easing = new CubicEase
		{
			EasingMode = EasingMode.EaseOut
		};
		BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, duration)
		{
			EasingFunction = easing
		});
		DoubleAnimation scaleAnimation = new DoubleAnimation(1.0, duration)
		{
			EasingFunction = easing
		};
		scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
		scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
		DoubleAnimation liftAnimation = new DoubleAnimation(0.0, duration)
		{
			EasingFunction = easing
		};
		translateTransform.BeginAnimation(TranslateTransform.YProperty, liftAnimation);
	}
}
