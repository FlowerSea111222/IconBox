using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IconBox;

public class BoardWindow : GlassWindow
{
	public bool ModalBusy;

	private Controller C;

	private System.Windows.Controls.TextBox Search;

	private Popup searchPopup;

	private System.Windows.Controls.ListBox searchList;

	private CancellationTokenSource searchCts;

	private int searchSerial;

	private List<SystemSearchResult> searchItems = new List<SystemSearchResult>();

	private UniformGrid Tiles;

	// 上一次播放逐块入场动画的时间戳：快速连续切换分类时跳过动画，避免动画排队叠加造成卡顿
	private int lastAnimateTick;

	// 多选态的两支画刷：原本每个磁贴各 new 一次，勾选时改状态只需复用，避免反复分配。
	private static readonly SolidColorBrush SelectionFill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, 111, 167, byte.MaxValue));

	private static readonly SolidColorBrush SelectionEdge = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 146, 187, byte.MaxValue));



	private WrapPanel categoryBar;

	// 分类行宿主：铺满整行，让标签居中，并接住「拖到空白处 = 移到最末」。
	private Grid categoryRow;

	private string activeCategory = "all";

	private readonly Dictionary<string, Border> categoryChips = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, TextBlock> categoryCounts = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

	private TextBlock Pager;

	private StackPanel PageNav;

	private TextBlock Status;

	private TextBlock Empty;

	private Grid layout;

	private int page;

	private int capacity = 35;

	private int columns = 7;

	private System.Windows.Point dragStart;

	private Entry dragEntry;

	private FrameworkElement searchHost;

	private bool editing;

	private readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private WrapPanel bulkBar;

	private RowDefinition bulkRow;

	private Popup mediaPreviewPopup;

	private DispatcherTimer mediaPreviewTimer;

	private Entry mediaPreviewEntry;

	private FrameworkElement mediaPreviewAnchor;

	private MediaElement mediaPreviewPlayer;

	private Border insertMarker;

	private int insertIndex = -1;

	private bool livePreview;

	private int liveIndex = -1;

	// 拖动开始时冻结的磁贴顺序，落点（插入下标）一律基于它计算，避免实时预览重排后落点漂移。
	private List<Border> dragBaseOrder;

	private TextBlock selectionCount;

	private System.Windows.Controls.Button selectAll;

	private System.Windows.Controls.Button clearSelection;

	private System.Windows.Controls.Button deleteSelected;

	private System.Windows.Controls.Button editSelection;

	private System.Windows.Controls.ContextMenu iconMenu;

	private Thumb moveGrip;

	private Thumb resizeGrip;

	private int visibilitySerial;

	private DispatcherTimer trimTimer;

	private readonly DispatcherTimer idleRelease = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(2.0)
	};

	private bool preparingView;

	private string pendingStatus;

	// 状态栏提示是临时的，写进去后自动消失，不能永久挂着旧消息。
	private DispatcherTimer statusTimer;

	private Border dragPreview;

	private System.Windows.Controls.Image dragPreviewIcon;

	private TextBlock dragPreviewText;

	private bool activeDrag;

	private readonly DispatcherTimer sizeRefresh = new DispatcherTimer(DispatcherPriority.Background)
	{
		Interval = TimeSpan.FromMilliseconds(100.0)
	};

	private bool resizingPanel;

	private bool refreshingSize;

	private bool iconsDirty = true;

	private System.Windows.Controls.Button pagePrevButton;

	private System.Windows.Controls.Button pageNextButton;

	internal bool HasView => layout != null;

	public void ClearSearch()
	{
		if (Search != null)
		{
			Search.Clear();
		}
	}

	public BoardWindow(Controller c)
		: base(42.0)
	{
		C = c;
		Title = "IconBox 应用启动台";
		AllowDrop = true;
		Root.PreviewMouseDown += delegate
		{
			if (Search != null && Search.IsKeyboardFocusWithin && !Search.IsMouseOver)
			{
				Keyboard.ClearFocus();
			}
		};
		MouseMove += delegate
		{
			if (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen && !IsCursorOverMediaPreview())
			{
				CancelMediaPreview();
			}
		};
		InitDragExperience();
		IsVisibleChanged += (object s, DependencyPropertyChangedEventArgs e) =>
		{
			if (IsVisible)
			{
				idleRelease.Stop();
			}
			else if (layout != null)
			{
				idleRelease.Start();
			}
		};
		idleRelease.Tick += (object s, EventArgs e) =>
		{
			if (IsVisible)
			{
				idleRelease.Stop();
			}
			else if (ReleaseHiddenView())
			{
				idleRelease.Stop();
			}
		};
		Closed += (object s, EventArgs e) =>
		{
			idleRelease.Stop();
			sizeRefresh.Stop();
		};
		PreviewKeyDown += (object s, System.Windows.Input.KeyEventArgs e) =>
		{
			if (Search != null)
			{
				if (e.Key == Key.Escape)
				{
					// Esc 逐层退：先关浮层，再退多选，最后才收起整个面板
					if (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen)
					{
						CancelMediaPreview();
					}
					else if ((searchPopup != null && searchPopup.IsOpen) || (Search != null && Search.Text.Length > 0))
					{
						// 关下拉的同时把搜索词也清掉，否则界面停在「筛选中」但看不到下拉。
						// 注意不能 ClearFocus：窗口没有键盘焦点后，下一次 Esc 根本到不了这里，
						// 用户就再也没法用 Esc 收起面板了。
						if (searchPopup != null)
						{
							searchPopup.IsOpen = false;
						}
						if (Search != null)
						{
							Search.Text = "";
							Keyboard.Focus(Search);
						}
					}
					else if (activeDrag)
					{
						// 拖放由 OLE 的结束回调收尾，这里不打断。
					}
					else if (editing)
					{
						SetSelectionMode(false);
					}
					else
					{
						CollapseSmooth();
					}
					e.Handled = true;
				}
				else if (!Search.IsKeyboardFocusWithin && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Z || e.Key == Key.Y))
				{
					if (e.Key == Key.Y || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
					{
						C.RedoBoard();
					}
					else
					{
						C.UndoBoard();
					}
					e.Handled = true;
				}
				else if (editing && !Search.IsKeyboardFocusWithin && e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
				{
					SelectFiltered();
					e.Handled = true;
				}
				else if (editing && !Search.IsKeyboardFocusWithin && e.Key == Key.Delete)
				{
					C.RemoveMany(selected.ToArray());
					e.Handled = true;
				}
				else if (e.Key == Key.Next)
				{
					ChangePage(1);
					e.Handled = true;
				}
				else if (e.Key == Key.Prior)
				{
					ChangePage(-1);
					e.Handled = true;
				}
				else if (e.Key == Key.Return && Search.IsKeyboardFocusWithin && !editing && searchItems.Count > 0)
				{
					OpenSearchResult(searchItems[0]);
					e.Handled = true;
				}
			}
		};
		Drop += ExternalDrop;
		Deactivated += (object s, EventArgs e) =>
		{
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (C.Prefs.BoardDismissOnDeactivate && IsVisible && !IsActive && !ModalBusy && !activeDrag && !IsContextMenuOpen() && !ShellContextMenu.Active && OwnedWindows.Count == 0 && Mouse.LeftButton != MouseButtonState.Pressed)
				{
					CollapseSmooth();
				}
			}));
		};
		moveGrip = new Thumb
		{
			Width = 60.0,
			Height = 8.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
			Cursor = System.Windows.Input.Cursors.SizeAll,
			ToolTip = "拖动面板位置"
		};
		Root.Children.Add(moveGrip);
		resizeGrip = new Thumb
		{
			Width = 20.0,
			Height = 20.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 0.0, 5.0, 5.0),
			Cursor = System.Windows.Input.Cursors.SizeNWSE,
			ToolTip = "拖动调整面板大小"
		};
		Root.Children.Add(resizeGrip);
		moveGrip.Template = GlassWindow.GripTemplate();
		resizeGrip.Template = GlassWindow.GripTemplate(corner: true);
		Closing += (object s, CancelEventArgs e) =>
		{
			if (!C.ShuttingDown)
			{
				e.Cancel = true;
				CollapseSmooth();
			}
		};
		moveGrip.DragStarted += (object s, DragStartedEventArgs e) =>
		{
			ModalBusy = true;
		};
		moveGrip.DragDelta += (object s, DragDeltaEventArgs e) =>
		{
			if (!C.Prefs.Placement.PanelLocked)
			{
				Rect workArea = SystemParameters.WorkArea;
				Left = FeatureRules.Position(Left + e.HorizontalChange, workArea.Left, workArea.Width, Width);
				Top = FeatureRules.Position(Top + e.VerticalChange, workArea.Top, workArea.Height, Height);
			}
		};
		InitResizeRefresh();
		resizeGrip.DragStarted += (object s, DragStartedEventArgs e) =>
		{
			ModalBusy = true;
			resizingPanel = true;
			sizeRefresh.Stop();
		};
		resizeGrip.DragDelta += (object s, DragDeltaEventArgs e) =>
		{
			if (!C.Prefs.Placement.PanelLocked)
			{
				Rect workArea = SystemParameters.WorkArea;
				Width = Math.Max(Math.Min(860.0, workArea.Width - 32.0), Math.Min(workArea.Width - 32.0, Width + e.HorizontalChange));
				Height = Math.Max(Math.Min(560.0, workArea.Height - 32.0), Math.Min(workArea.Height - 32.0, Height + e.VerticalChange));
				Left = FeatureRules.Position(Left, workArea.Left, workArea.Width, Width);
				Top = FeatureRules.Position(Top, workArea.Top, workArea.Height, Height);
			}
		};
		moveGrip.DragCompleted += (object s, DragCompletedEventArgs e) =>
		{
			try
			{
				SavePlacement();
				Backdrop(C.Prefs.Light);
			}
			finally
			{
				ModalBusy = false;
			}
		};
		resizeGrip.DragCompleted += (object s, DragCompletedEventArgs e) =>
		{
			try
			{
				resizingPanel = false;
				SavePlacement();
				QueueSizeRefresh();
				Backdrop(C.Prefs.Light);
			}
			finally
			{
				ModalBusy = false;
			}
		};
	}

	private void SavePlacement()
	{
		PlacementOptions placement = C.Prefs.Placement;
		placement.PanelLeft = Left;
		placement.PanelTop = Top;
		placement.PanelWidth = Width;
		placement.PanelHeight = Height;
		C.Save();
	}

	private void BuildForSize()
	{
		string text = ((C.Prefs.BoardHideSearch || Search == null) ? "" : Search.Text);
		columns = BoardIconRules.Columns(Width - 104.0, C.Prefs.BoardIconSize, C.Prefs.BoardIconGap);
		int num = BoardIconRules.Rows(Height - 224.0, C.Prefs.BoardIconSize, C.Prefs.BoardIconGap);
		capacity = columns * num;
		Build(num);
		Search.Text = text;
	}

	public void RefreshIcons()
	{
		iconsDirty = true;
		QueueSizeRefresh();
	}
	private void IconSettings()
	{
		System.Windows.Controls.ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.BoardMaterial.Tint, (int)(C.Prefs.OpacityPercent ?? 72.0));
		iconMenu = menu;
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			if (iconMenu == menu)
			{
				iconMenu = null;
			}
		};
		Action<string, double, double, double, Action<double>> action = (string name, double min, double max, double value, Action<double> change) =>
		{
			StackPanel stackPanel = new StackPanel
			{
				Width = 230.0
			};
			TextBlock label = new TextBlock
			{
				Text = name + "：" + Math.Round(value) + " px",
				Foreground = TextBrush,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			};
			stackPanel.Children.Add(label);
			Slider slider = new Slider
			{
				Minimum = min,
				Maximum = max,
				Value = value,
				TickFrequency = 1.0,
				IsSnapToTickEnabled = true,
				Style = (Resources[typeof(Slider)] as Style)
			};
			stackPanel.Children.Add(slider);
			slider.ValueChanged += (object s, RoutedPropertyChangedEventArgs<double> e) =>
			{
				label.Text = name + "：" + Math.Round(slider.Value) + " px";
				change(slider.Value);
				RefreshIcons();
				C.Save();
			};
			menu.Items.Add(new System.Windows.Controls.MenuItem
			{
				Header = stackPanel,
				StaysOpenOnClick = true
			});
		};
		action("图标大小", 28.0, 80.0, C.Prefs.BoardIconSize, (double v) =>
		{
			C.Prefs.BoardIconSize = v;
		});
		action("图标间距", 0.0, 20.0, C.Prefs.BoardIconGap, (double v) =>
		{
			C.Prefs.BoardIconGap = v;
		});
		System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
		{
			Header = "恢复默认"
		};
		menuItem.Click += (object s, RoutedEventArgs e) =>
		{
			C.Prefs.BoardIconSize = 48.0;
			C.Prefs.BoardIconGap = 5.0;
			RefreshIcons();
			C.Save();
		};
		menu.Items.Add(menuItem);
		menu.PlacementTarget = this;
		menu.IsOpen = true;
	}

	public void CollapseSmooth()
	{
		if (!IsVisible)
		{
			return;
		}
		// 媒体预览 / 搜索下拉 / 图标菜单都是独立 HWND 的 Popup，窗口隐藏后不会自己消失。
		// 必须先显式关掉，否则它们会留在桌面上（预览里的视频还会继续播放）。
		CancelMediaPreview();
		if (searchPopup != null)
		{
			searchPopup.IsOpen = false;
		}
		if (iconMenu != null)
		{
			iconMenu.IsOpen = false;
		}
		int serial = ++visibilitySerial;
		ScheduleTrim();
		if (!MotionEnabled)
		{
			Hide();
			return;
		}
		DoubleAnimation doubleAnimation = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(110.0))
		{
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseIn
			}
		};
		doubleAnimation.Completed += (object s, EventArgs e) =>
		{
			if (serial == visibilitySerial)
			{
				Hide();
			}
		};
		BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
	}

	private void ScheduleTrim()
	{
		if (trimTimer == null)
		{
			trimTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(3.0)
			};
			trimTimer.Tick += (object s, EventArgs e) =>
			{
				trimTimer.Stop();
				try
				{
					GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
					Native.TrimWorkingSet();
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			};
		}
		trimTimer.Stop();
		trimTimer.Start();
	}

	internal bool ReleaseHiddenView()
	{
		if (IsVisible || preparingView || ModalBusy || activeDrag || IsMouseCaptureWithin)
		{
			return false;
		}
		sizeRefresh.Stop();
		if (iconMenu != null)
		{
			iconMenu.IsOpen = false;
		}
		iconMenu = null;
		// 浮层与临时状态一起释放，别指望下次 Open() 里那两行兜底。
		if (searchPopup != null)
		{
			searchPopup.IsOpen = false;
		}
		searchPopup = null;
		searchList = null;
		searchItems = new List<SystemSearchResult>();
		selected.Clear();
		editing = false;
		pendingStatus = null;
		if (statusTimer != null)
		{
			statusTimer.Stop();
		}
		insertMarker = null;
		liveIndex = -1;
		dragBaseOrder = null;
		CloseMediaPreview();
		pagePrevButton = null;
		pageNextButton = null;
		categoryBar = null;
		categoryRow = null;
		EndDragPreview();
		ContentGrid.Children.Clear();
		ReleaseBackdrop();
		Search = null;
		Tiles = null;
		PageNav = null;
		Pager = (Status = (Empty = null));
		layout = null;
		bulkBar = null;
		bulkRow = null;
		selectionCount = null;
		selectAll = (clearSelection = (deleteSelected = (editSelection = null)));
		dragEntry = null;
		return true;
	}

	private bool IsContextMenuOpen()
	{
		if (iconMenu == null || !iconMenu.IsOpen)
		{
			if (Tiles != null)
			{
				return Tiles.Children.OfType<FrameworkElement>().Any((FrameworkElement x) => x.ContextMenu != null && x.ContextMenu.IsOpen);
			}
			return false;
		}
		return true;
	}

	public void Open()
	{
		idleRelease.Stop();
		if (trimTimer != null)
		{
			trimTimer.Stop();
		}
		preparingView = true;
		Stopwatch stopwatch = Stopwatch.StartNew();
		long num = 0L;
		long num2 = 0L;
		try
		{
			visibilitySerial++;
			Rect workArea = SystemParameters.WorkArea;
			double num3 = ((C.Prefs.PanelSize == 0) ? 980 : 1280);
			double num4 = ((C.Prefs.PanelSize == 0) ? 620 : 730);
			if (C.Prefs.PanelSize == 2)
			{
				num3 = workArea.Width - 32.0;
				num4 = workArea.Height - C.Dock.Height - 40.0;
			}
			PlacementOptions placement = C.Prefs.Placement;
			Width = Math.Min(placement.PanelWidth ?? num3, workArea.Width - 32.0);
			Height = Math.Min(placement.PanelHeight ?? num4, workArea.Height - 32.0);
			Height = Math.Max(Math.Min(400.0, workArea.Height - 130.0), Height);
			Width = Math.Max(Math.Min(860.0, workArea.Width - 32.0), Width);
			Height = Math.Max(Math.Min(560.0, workArea.Height - 32.0), Height);
			Left = FeatureRules.Position(placement.PanelLeft ?? (workArea.Left + (workArea.Width - Width) / 2.0), workArea.Left, workArea.Width, Width);
			Top = FeatureRules.Position(placement.PanelTop ?? (workArea.Top + (workArea.Height - C.Dock.Height - Height) / 2.0), workArea.Top, workArea.Height, Height);
			Thumb thumb = moveGrip;
			Visibility visibility = (resizeGrip.Visibility = (placement.PanelLocked ? Visibility.Collapsed : Visibility.Visible));
			Visibility visibility3 = visibility;
			thumb.Visibility = visibility3;
			editing = false;
			selected.Clear();
			LightTheme = C.Prefs.Light;
			MotionEnabled = C.Prefs.Dock.MotionEnabled;
			MotionLevel = C.Prefs.Dock.MotionLevel;
			ConfigureGlass(C.Prefs.Dock, C.Prefs.BoardMaterial);
			GlassPercent = C.Prefs.OpacityPercent ?? 72.0;
			Polish.Apply(this, LightTheme);
			// 打开面板默认停在第一个分类（用户需求：不再默认显示「全部」）。
			activeCategory = FirstCategoryId();
			page = 0;
			BuildForSize();
			num = stopwatch.ElapsedMilliseconds;
			Backdrop(C.Prefs.Light);
			num2 = stopwatch.ElapsedMilliseconds - num;
			Show();
			AnimateOpen();
			Keyboard.ClearFocus();
			Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() =>
			{
				try
				{
					Activate();
					Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
				}
				catch
				{
				}
			}));
			Store.Trace("BOARD open total=" + stopwatch.ElapsedMilliseconds + "ms build=" + num + "ms backdrop=" + num2 + "ms");
		}
		finally
		{
			preparingView = false;
		}
		// 面板刚从 Dock 弹出时鼠标常压在分类行上，先锁一小段时间，避免一打开就切分类
		LockCategoryHover(700);
		PruneEmptyCategories();
	}

	private void Build(int rows)
	{
		ContentGrid.Children.Clear();
		layout = new Grid
		{
			Margin = new Thickness(38.0, 22.0, 38.0, 20.0)
		};
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(55.0)
		});
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		bulkRow = new RowDefinition
		{
			Height = new GridLength(0.0)
		};
		layout.RowDefinitions.Add(bulkRow);
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(45.0)
		});
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		ContentGrid.Children.Add(layout);
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(116.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(80.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(40.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(40.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(44.0)
		});
		layout.Children.Add(grid);
		Border border = new Border
		{
			CornerRadius = new CornerRadius(16.0),
			BorderThickness = new Thickness(1.0),
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(25, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
			ClipToBounds = true,
			Height = 36.0,
			Margin = new Thickness(8.0, 4.0, 8.0, 4.0)
		};
		UIElement uIElement = (border.Child = new Grid());
		Grid grid2 = (Grid)uIElement;
		StackPanel stackPanel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(15.0, 0.0, 0.0, 0.0),
			IsHitTestVisible = false
		};
		Viewbox viewbox = new Viewbox
		{
			Width = 14.0,
			Height = 14.0,
			Stretch = Stretch.Uniform,
			VerticalAlignment = VerticalAlignment.Center
		};
		viewbox.Child = new System.Windows.Shapes.Path
		{
			Data = Geometry.Parse("M 2.9,2.9 m -4.3,0 a 4.3,4.3 0 1 0 8.6,0 a 4.3,4.3 0 1 0 -8.6,0 M 8.0,8.0 L 13.4,13.4"),
			Stroke = MutedBrush,
			StrokeThickness = 1.9,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			Width = 16.0,
			Height = 16.0,
			Stretch = Stretch.Uniform
		};
		TextBlock hint = new TextBlock
		{
			Text = "搜索系统应用、文件、文件夹",
			FontSize = 15.0,
			Foreground = MutedBrush,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		stackPanel.Children.Add(viewbox);
		stackPanel.Children.Add(hint);
		grid2.Children.Add(stackPanel);
		Search = new System.Windows.Controls.TextBox
		{
			FontSize = 15.0,
			Foreground = TextBrush,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			CaretBrush = TextBrush,
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(30.0, 0.0, 12.0, 0.0)
		};
		System.Windows.Media.Color idleBorder = System.Windows.Media.Color.FromArgb(90, byte.MaxValue, byte.MaxValue, byte.MaxValue);
		System.Windows.Media.Color color = IconPacks.AccentColor(C.Prefs.Dock.Accent);
		System.Windows.Media.Color focusBorder = System.Windows.Media.Color.FromArgb(60, color.R, color.G, color.B);
		Search.GotKeyboardFocus += delegate
		{
			border.BorderBrush = new SolidColorBrush(focusBorder);
		};
		Search.LostKeyboardFocus += delegate
		{
			border.BorderBrush = new SolidColorBrush(idleBorder);
		};
		Search.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			hint.Visibility = ((Search.Text.Length != 0) ? Visibility.Hidden : Visibility.Visible);
			RunSearch();
		};
		grid2.Children.Add(Search);
		searchHost = border;
		searchHost.Visibility = (C.Prefs.BoardHideSearch ? Visibility.Collapsed : Visibility.Visible);
		Grid.SetColumn(border, 0);
		grid.Children.Add(border);
		BuildSearchPopup(border);
		System.Windows.Controls.Button element = Button("+ 批量添加", (object s, RoutedEventArgs e) =>
		{
			C.Choose();
		}, 106.0);
		Grid.SetColumn(element, 1);
		grid.Children.Add(element);
		editSelection = Button(editing ? "完成" : "多选", null, 68.0);
		editSelection.ToolTip = "跨页勾选，一次移除多个入口";
		editSelection.Click += (object s, RoutedEventArgs e) =>
		{
			SetSelectionMode(!editing);
			Search.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
		};
		Grid.SetColumn(editSelection, 2);
		grid.Children.Add(editSelection);
		System.Windows.Controls.Button button = Button("添加", (object s, RoutedEventArgs e) =>
		{
			C.Settings();
		}, 30.0);
		button.Content = CustomIcons.Settings(18.0, TextBrush);
		button.ToolTip = "批量添加多个软件入口";
		Grid.SetColumn(button, 3);
		grid.Children.Add(button);
		System.Windows.Controls.Button button2 = Button("", (object s, RoutedEventArgs e) =>
		{
			MoreMenu();
		}, 32.0);
		button2.Content = Polish.Icon("more", TextBrush, 18.0);
		button2.ToolTip = "更多与显示选项";
		Grid.SetColumn(button2, 4);
		grid.Children.Add(button2);
		System.Windows.Controls.Button element2 = Button("", (object s, RoutedEventArgs e) =>
		{
			CollapseSmooth();
		}, 32.0);
		element2.Content = Polish.Icon("close", TextBrush, 16.0);
		Grid.SetColumn(element2, 5);
		grid.Children.Add(element2);
		WrapPanel wrapPanel = new WrapPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetRow(wrapPanel, 1);
		layout.Children.Add(wrapPanel);
		bulkBar = new WrapPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		Grid.SetRow(bulkBar, 2);
		layout.Children.Add(bulkBar);
		selectAll = Button("全选", (object s, RoutedEventArgs e) =>
		{
			SelectFiltered();
		}, 90.0);
		selectAll.ToolTip = "选中当前搜索结果的所有页；已有勾选会保留";
		bulkBar.Children.Add(selectAll);
		clearSelection = Button("清空选择", (object s, RoutedEventArgs e) =>
		{
			selected.Clear();
			SyncSelectionVisuals();
			UpdateSelectionBar();
		}, 90.0);
		bulkBar.Children.Add(clearSelection);
		deleteSelected = Button("移除所选", (object s, RoutedEventArgs e) =>
		{
			C.RemoveMany(selected.ToArray());
		}, 112.0);
		deleteSelected.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(byte.MaxValue, 155, 155));
		bulkBar.Children.Add(deleteSelected);
		selectionCount = new TextBlock
		{
			Foreground = MutedBrush,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0)
		};
		bulkBar.Children.Add(selectionCount);
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(0.0, 10.0, 0.0, 4.0)
		};
		Grid.SetRow(dockPanel, 3);
		layout.Children.Add(dockPanel);
		Grid grid3 = new Grid();
		dockPanel.Children.Add(grid3);
		// 分类标签整体居中：外层格铺满整行并作为「拖到空白处 = 移到最末」的落点。
		// 内层 WrapPanel 按内容宽度居中，标签不再从左侧起排。
		categoryRow = new Grid();
		Grid.SetRow(categoryRow, 6);
		layout.Children.Add(categoryRow);
		categoryBar = new WrapPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		categoryRow.Children.Add(categoryBar);
		BuildCategoryBar();
		Tiles = new UniformGrid
		{
			Columns = columns,
			Rows = rows,
			ClipToBounds = true,
			Background = System.Windows.Media.Brushes.Transparent,
			AllowDrop = true
		};
		Grid.SetRow(Tiles, 0);
		grid3.Children.Add(Tiles);
		Tiles.SizeChanged += (object s, SizeChangedEventArgs e) =>
		{
			QueueSizeRefresh();
		};
		Tiles.DragOver += (object s, System.Windows.DragEventArgs e) =>
		{
			if (DraggedEntries(e.Data).Length != 0 && e.OriginalSource == Tiles)
			{
				int num = InsertIndexFor(e.GetPosition(Tiles));
				if (num != liveIndex)
				{
					liveIndex = num;
					ApplyLivePreview(DraggedEntries(e.Data), num);
				}
				e.Effects = System.Windows.DragDropEffects.Move;
				e.Handled = true;
			}
		};
		Tiles.Drop += (object s, System.Windows.DragEventArgs e) =>
		{
			if (DraggedEntries(e.Data).Length != 0 && e.OriginalSource == Tiles)
			{
				HideInsertMarker();
				if (!CommitReorder(CurrentPageSequence()))
				{
					Render();
				}
				e.Handled = true;
			}
		};
		Tiles.DragLeave += (object s, System.Windows.DragEventArgs e) =>
		{
			HideInsertMarker();
		};
		insertMarker = new Border
		{
			Width = 3.0,
			CornerRadius = new CornerRadius(1.5),
			Background = new SolidColorBrush(IconPacks.AccentColor(C.Prefs.Dock.Accent)),
			Visibility = Visibility.Collapsed,
			IsHitTestVisible = false,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			RenderTransform = new TranslateTransform()
		};
		Grid.SetRow(insertMarker, 1);
		grid3.Children.Add(insertMarker);
		Empty = new TextBlock
		{
			Text = "将软件、文档或文件夹拖到这里\n面板默认保持打开；按 Esc 收起，也可点击「批量添加」",
			Visibility = Visibility.Collapsed,
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			Foreground = MutedBrush,
			FontSize = 19.0,
			LineHeight = 32.0
		};
		Grid.SetRow(Empty, 1);
		grid3.Children.Add(Empty);
		StackPanel stackPanel2 = (PageNav = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		});
		Grid.SetRow(stackPanel2, 4);
		layout.Children.Add(stackPanel2);
		pagePrevButton = Button("上一页", (object s, RoutedEventArgs e) =>
		{
			ChangePage(-1);
		}, 40.0);
		stackPanel2.Children.Add(pagePrevButton);
		Pager = new TextBlock
		{
			Width = 100.0,
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = TextBrush,
			FontSize = 14.0
		};
		stackPanel2.Children.Add(Pager);
		pageNextButton = Button("下一页", (object s, RoutedEventArgs e) =>
		{
			ChangePage(1);
		}, 40.0);
		stackPanel2.Children.Add(pageNextButton);
		Grid grid4 = new Grid();
		Grid.SetRow(grid4, 5);
		layout.Children.Add(grid4);
		Status = new TextBlock
		{
			Foreground = MutedBrush,
			FontSize = 11.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed
		};
		grid4.Children.Add(Status);
		Render(animate: true);
	}

	private List<Entry> Filtered()
	{
		List<Entry> list = C.Prefs.Apps.ToList();
		if (string.IsNullOrEmpty(activeCategory) || string.Equals(activeCategory, "all", StringComparison.OrdinalIgnoreCase))
		{
			return list;
		}
		return list.Where((Entry a) => string.Equals(AppCategoryRules.Resolve(a), activeCategory, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	private void BuildCategoryBar()
	{
		if (categoryBar == null)
		{
			return;
		}
		categoryBar.Children.Clear();
		categoryChips.Clear();
		categoryCounts.Clear();
		AppCategoryRules.Category[] categories = AppCategoryRules.Categories;
		foreach (AppCategoryRules.Category category in categories)
		{
			string id = category.Id;
			// 「全部」不再是分类标签（用户 m01602）：回到「显示全部」的入口是「再点一次当前分类」。
			// 样式（用户 m01786）：Apple 风格胶囊 — 真·全圆角（圆角 = 高度一半），未选中用中性填充 +
			// 1px 分隔线描边（玻璃背景上才看得出胶囊轮廓），选中用强调色低透明度底 + 强调色文字；
			// 图标是 SF Symbols 风格矢量图（线性，随状态换色）。
			Border chip = new Border
			{
				CornerRadius = new CornerRadius(CategoryChipRadius),
				Padding = new Thickness(12.0, 0.0, 13.0, 0.0),
				Margin = new Thickness(0.0, 0.0, Tokens.Space.Sm, Tokens.Space.Sm),
				BorderThickness = CategoryChipBorderThickness,
				// 高度必须显式给：CategoryChipRadius 是按 30 算的，但 Border 自己不会用这个常量，
				// 内容（TextBlock 行高 17 + Padding）会把它撑到 45px，胶囊比例就失真了。
				Height = CategoryChipHeight,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				SnapsToDevicePixels = true,
				UseLayoutRounding = true,
				Cursor = System.Windows.Input.Cursors.Hand,
				ToolTip = "鼠标悬停即切换到此分类；把应用图标拖到标签上即可归类；拖动标签可排序；右键可重命名 / 删除",
				AllowDrop = true,
				Tag = id
			};
			StackPanel stackPanel = new StackPanel
			{
				Orientation = System.Windows.Controls.Orientation.Horizontal,
				VerticalAlignment = VerticalAlignment.Center
			};
			stackPanel.Children.Add(CategoryGlyphElement(category));
			stackPanel.Children.Add(new TextBlock
			{
				Text = category.Name,
				FontSize = Tokens.Type.Headline,
				FontFamily = Tokens.Type.Text,
				FontWeight = Tokens.Type.Medium,
				VerticalAlignment = VerticalAlignment.Center
			});
			TextBlock textBlock = new TextBlock
			{
				Text = "",
				FontSize = Tokens.Type.Subhead,
				FontFamily = Tokens.Type.Text,
				Margin = new Thickness(Tokens.Space.Xs + 1.0, 0.0, 0.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center,
				Opacity = 0.72
			};
			stackPanel.Children.Add(textBlock);
			chip.Child = stackPanel;
			// 悬停切换用 220ms 延迟：面板从 Dock 弹出时鼠标会先划过最下面的分类行。
			// 立即切换会让图标区内容被莫名其妙地换掉。移开即取消。
			DispatcherTimer hoverSwitch = null;
			// 悬停只是「轻微提亮」，不改变选中态；选中态由 UpdateCategoryChips 统一上色。
			// 底与描边走同一组共享画刷，避免每次进出都 new 一个（也保证和 UpdateCategoryChips 的配色一致）。
			chip.MouseEnter += delegate
			{
				if (!string.Equals(id, activeCategory, StringComparison.OrdinalIgnoreCase))
				{
					chip.Background = CategoryChipHoverFill();
					chip.BorderBrush = CategoryChipIdleBorder();
				}
				if (Mouse.LeftButton == MouseButtonState.Pressed || Environment.TickCount < categoryHoverLockUntil)
				{
					return;
				}
				if (hoverSwitch == null)
				{
					hoverSwitch = new DispatcherTimer(DispatcherPriority.Input)
					{
						Interval = TimeSpan.FromMilliseconds(220.0)
					};
					hoverSwitch.Tick += delegate
					{
						hoverSwitch.Stop();
						if (chip.IsMouseOver && Mouse.LeftButton != MouseButtonState.Pressed && Environment.TickCount >= categoryHoverLockUntil)
						{
							SetCategory(id);
						}
					};
				}
				hoverSwitch.Stop();
				hoverSwitch.Start();
			};
			chip.MouseLeave += delegate
			{
				if (hoverSwitch != null)
				{
					hoverSwitch.Stop();
				}
				if (!string.Equals(id, activeCategory, StringComparison.OrdinalIgnoreCase))
				{
					chip.Background = CategoryChipIdleFill();
					chip.BorderBrush = CategoryChipIdleBorder();
				}
			};
			chip.MouseLeftButtonUp += delegate
			{
				// 点当前已选中的分类 = 取消筛选，回到「显示全部」。
				SetCategory(string.Equals(id, activeCategory, StringComparison.OrdinalIgnoreCase) ? null : id);
			};
			// —— 右键菜单：重命名 / 删除 / 新建 / 恢复默认 ——
			System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu();
			// 通用项（新建 / 恢复默认 / 删除空分类），每个分类标签都有。
			List<object> over = new List<object>();
			over.Add(new System.Windows.Controls.Separator());
			System.Windows.Controls.MenuItem addItem = new System.Windows.Controls.MenuItem
			{
				Header = "新建分类"
			};
			addItem.Click += delegate
			{
				AddCategoryInteractive();
			};
			over.Add(addItem);
			System.Windows.Controls.MenuItem resetItem = new System.Windows.Controls.MenuItem
			{
				Header = "恢复默认分类"
			};
			resetItem.Click += delegate
			{
				ResetCategoriesInteractive();
			};
			over.Add(resetItem);
			System.Windows.Controls.MenuItem cleanEmptyItem = new System.Windows.Controls.MenuItem
			{
				Header = "重命名分类"
			};
			cleanEmptyItem.Click += delegate
			{
				CleanEmptyCategoriesInteractive();
			};
			over.Add(cleanEmptyItem);
			System.Windows.Controls.MenuItem renameItem = new System.Windows.Controls.MenuItem
			{
				Header = "调整分类顺序"
			};
			renameItem.Click += delegate
			{
				RenameCategoryInteractive(id);
			};
			System.Windows.Controls.MenuItem deleteItem = new System.Windows.Controls.MenuItem
			{
				Header = "删除此分类"
			};
			deleteItem.Click += delegate
			{
				DeleteCategoryInteractive(id);
			};
			menu.Items.Add(renameItem);
			menu.Items.Add(deleteItem);
			foreach (object item in over)
			{
				menu.Items.Add(item);
			}
			chip.ContextMenu = menu;
			// —— 拖动排序：按住标签横向拖动，松手到目标位置 ——
			{
				System.Windows.Point dragStart = default(System.Windows.Point);
				bool dragArmed = false;
				chip.PreviewMouseLeftButtonDown += delegate(object s, System.Windows.Input.MouseButtonEventArgs e)
				{
					dragStart = e.GetPosition(categoryBar);
					dragArmed = true;
				};
				chip.PreviewMouseLeftButtonUp += delegate
				{
					dragArmed = false;
				};
				chip.PreviewMouseMove += delegate(object s, System.Windows.Input.MouseEventArgs e)
				{
					if (!dragArmed || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
					{
						return;
					}
					System.Windows.Point point = e.GetPosition(categoryBar);
					if (Math.Abs(point.X - dragStart.X) < 8.0 && Math.Abs(point.Y - dragStart.Y) < 8.0)
					{
						return;
					}
					dragArmed = false;
					try
					{
						System.Windows.DragDrop.DoDragDrop(chip, new System.Windows.DataObject(CategoryDragFormat, id), System.Windows.DragDropEffects.Move);
					}
					catch (Exception ex)
					{
						Store.Log(ex);
					}
				};
			}
			chip.DragEnter += delegate(object s, System.Windows.DragEventArgs e)
			{
				if (IsCategoryDrag(e) || DraggedEntries(e.Data).Length != 0)
				{
					// 拖放目标提示：强调色底 + 强调色描边（Apple 里这等于「可放置的高亮」）。
					System.Windows.Media.Color color = IconPacks.AccentColor(C.Prefs.Dock.Accent);
					chip.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(56, color.R, color.G, color.B));
					chip.BorderThickness = CategoryChipBorderThickness;
					chip.BorderBrush = new SolidColorBrush(color);
					e.Effects = System.Windows.DragDropEffects.Move;
					e.Handled = true;
				}
			};
			chip.DragOver += delegate(object s, System.Windows.DragEventArgs e)
			{
				if (IsCategoryDrag(e) || DraggedEntries(e.Data).Length != 0)
				{
					e.Effects = System.Windows.DragDropEffects.Move;
					e.Handled = true;
				}
			};
			chip.DragLeave += delegate
			{
				chip.BorderThickness = CategoryChipBorderThickness;
				UpdateCategoryChips();
			};
			chip.Drop += delegate(object s, System.Windows.DragEventArgs e)
			{
				string draggedCategory = CategoryDragId(e.Data);
				if (draggedCategory != null)
				{
					MoveCategoryBefore(draggedCategory, id);
					e.Handled = true;
					return;
				}
				string[] array = DraggedEntries(e.Data);
				if (array.Length != 0)
				{
					string[] array2 = array;
					foreach (string path in array2)
					{
						Entry entry = C.Prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
						AssignCategory(entry, id);
					}
					e.Handled = true;
				}
			};
			categoryBar.Children.Add(chip);
			categoryChips[id] = chip;
			categoryCounts[id] = textBlock;
		}
		// 拖到分类栏空白处 = 移到最末（整行都算落点，不只标签占的中间那一块）
		categoryBar.AllowDrop = true;
		categoryBar.DragOver += delegate(object s, System.Windows.DragEventArgs e)
		{
			if (IsCategoryDrag(e))
			{
				e.Effects = System.Windows.DragDropEffects.Move;
				e.Handled = true;
			}
		};
		categoryBar.Drop += delegate(object s, System.Windows.DragEventArgs e)
		{
			if (CategoryDropToEnd(e))
			{
				e.Handled = true;
			}
		};
		if (categoryRow != null)
		{
			categoryRow.AllowDrop = true;
			categoryRow.DragOver += delegate(object s, System.Windows.DragEventArgs e)
			{
				if (IsCategoryDrag(e))
				{
					e.Effects = System.Windows.DragDropEffects.Move;
					e.Handled = true;
				}
			};
			categoryRow.Drop += delegate(object s, System.Windows.DragEventArgs e)
			{
				if (CategoryDropToEnd(e))
				{
					e.Handled = true;
				}
			};
		}
		UpdateCategoryChips();
	}

	// 分类图标（用户 m01786）：内置分类用 SF Symbols 风格矢量图标（Polish.Icon），
	// 图标名来自 CategoryIconName；用户自定义分类没有对应图标时，退回原来的文字字形。
	// 返回的元素统一打 Tag = "category-glyph"，改选中态时按这个名字统一换色。
	private FrameworkElement CategoryGlyphElement(AppCategoryRules.Category category)
	{
		string key = CategoryIconName(category.Id);
		FrameworkElement element;
		if (key != null)
		{
			element = Polish.Icon(key, TextBrush, Tokens.Type.Headline + 2.0);
			element.Margin = new Thickness(0.0, 0.0, Tokens.Space.Xs + 1.0, 0.0);
			element.VerticalAlignment = VerticalAlignment.Center;
		}
		else if (string.Equals(category.Id, AppCategoryRules.File, StringComparison.OrdinalIgnoreCase))
		{
			element = FileCategoryGlyph();
		}
		else
		{
			TextBlock textBlock = new TextBlock
			{
				Text = category.Glyph,
				FontSize = Tokens.Type.Callout,
				Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center,
				Opacity = 0.9
			};
			if (!string.IsNullOrWhiteSpace(category.Font))
			{
				textBlock.FontFamily = new System.Windows.Media.FontFamily(category.Font);
			}
			element = textBlock;
		}
		element.Tag = "category-glyph";
		element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		element.RenderTransform = System.Windows.Media.Transform.Identity;
		return element;
	}

	// 改一个分类标签里图标元素的前景（图标元素可能是 Path / GeometryGroup Path / TextBlock）。
	private static void PaintCategoryGlyph(object glyph, System.Windows.Media.Brush brush)
	{
		if (glyph is System.Windows.Shapes.Path path)
		{
			if (path.Fill != null && path.Fill != System.Windows.Media.Brushes.Transparent)
			{
				path.Fill = brush;
			}
			else
			{
				path.Stroke = brush;
			}
			return;
		}
		if (glyph is TextBlock textBlock)
		{
			textBlock.Foreground = brush;
		}
	}

	// 内置分类 → SF Symbols 图标名；未收录（含用户自定义分类）返回 null。
	private static string CategoryIconName(string id)
	{
		if (string.Equals(id, AppCategoryRules.All, StringComparison.OrdinalIgnoreCase))
		{
			return "grid";
		}
		if (string.Equals(id, AppCategoryRules.Office, StringComparison.OrdinalIgnoreCase))
		{
			return "note";
		}
		if (string.Equals(id, AppCategoryRules.Social, StringComparison.OrdinalIgnoreCase))
		{
			return "bell";
		}
		if (string.Equals(id, AppCategoryRules.Dev, StringComparison.OrdinalIgnoreCase))
		{
			return "sidebar";
		}
		if (string.Equals(id, AppCategoryRules.Design, StringComparison.OrdinalIgnoreCase))
		{
			return "brush";
		}
		if (string.Equals(id, AppCategoryRules.Media, StringComparison.OrdinalIgnoreCase))
		{
			return "music";
		}
		if (string.Equals(id, AppCategoryRules.Game, StringComparison.OrdinalIgnoreCase))
		{
			return "play";
		}
		if (string.Equals(id, AppCategoryRules.Net, StringComparison.OrdinalIgnoreCase))
		{
			return "wifi";
		}
		if (string.Equals(id, AppCategoryRules.Security, StringComparison.OrdinalIgnoreCase))
		{
			return "lock";
		}
		if (string.Equals(id, AppCategoryRules.Tool, StringComparison.OrdinalIgnoreCase))
		{
			// 「工具」原用齿轮（settings），与设置页「通用」页签图标完全重复且不易辨识，改用工具箱。
			return "wrench";
		}
		if (string.Equals(id, AppCategoryRules.System, StringComparison.OrdinalIgnoreCase))
		{
			return "control";
		}
		if (string.Equals(id, AppCategoryRules.Other, StringComparison.OrdinalIgnoreCase))
		{
			return "folder";
		}
		return null;
	}

	// 文档图标：一页纸 + 右上角折角 + 三条文字线（11x13）。
	private FrameworkElement FileCategoryGlyph()
	{
		GeometryGroup geometryGroup = new GeometryGroup();
		geometryGroup.Children.Add(Geometry.Parse("M 0.5,0.5 L 7,0.5 L 10,3.5 L 10,12.5 L 0.5,12.5 Z"));
		geometryGroup.Children.Add(Geometry.Parse("M 7,0.5 L 7,3.5 L 10,3.5"));
		geometryGroup.Children.Add(Geometry.Parse("M 2.5,6.5 L 8,6.5"));
		geometryGroup.Children.Add(Geometry.Parse("M 2.5,8.5 L 8,8.5"));
		geometryGroup.Children.Add(Geometry.Parse("M 2.5,10.5 L 6,10.5"));
		return new System.Windows.Shapes.Path
		{
			Data = geometryGroup,
			StrokeThickness = 1.0,
			Stroke = TextBrush,
			Stretch = Stretch.None,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
		};
	}

	// ── 分类标签（chip）配色（用户 m01786）：玻璃背景是半透明毛玻璃，任何「半透明填充」
	//    都会和底下的桌面内容混在一起 —— 实测玻璃上 chip 填充与背景只差 1-2/255，
	//    1px BorderBrush 描边也被 DWM 合成摊成两行过渡色、根本看不出来。
	//    所以这里改成 Apple 分段控件（segmented control）的做法：
	//    chip 用「不透明中性底 + 无描边」，靠自身实心色块和背景的明度差成形；
	//    选中态换成不透明强调色底 + 白色文字，对比度更大，也更像一个「被按下的胶囊」。
	private const double CategoryChipHeight = 30.0;
	// 真·胶囊：圆角 = 高度一半。
	private const double CategoryChipRadius = CategoryChipHeight / 2.0;
	// 不再用 BorderBrush 描边（描不出来），保留零厚度以兼容旧调用点。
	private static readonly Thickness CategoryChipBorderThickness = new Thickness(0.0);
	private SolidColorBrush CategoryChipIdleFill()
	{
		// 未选中片：冷白（浅色）/ 冷雾白（深色）—— 之前是中性灰 (146,147,155)，
		// 叠在浅灰玻璃 (218,223,233) 上实测只有 (150,151,160)，整排看起来发灰发脏；
		// 改为带一点点蓝的冷白，明度拉到玻璃之上，读作「浮起的白色胶囊」而不是灰块。
		return Tokens.Brush(LightTheme
			? System.Windows.Media.Color.FromArgb(0xE8, 0xE7, 0xEE, 0xF9)
			: System.Windows.Media.Color.FromArgb(0x38, 0xE6, 0xEC, 0xF9));
	}

	private SolidColorBrush CategoryChipHoverFill()
	{
		// 悬停：同一冷白色系再提亮一档（浅色接近纯白，深色抬高雾白不透明度）。
		return Tokens.Brush(LightTheme
			? System.Windows.Media.Color.FromArgb(0xF7, 0xF3, 0xF7, 0xFE)
			: System.Windows.Media.Color.FromArgb(0x4E, 0xEC, 0xF1, 0xFC));
	}

	private SolidColorBrush CategoryChipIdleBorder()
	{
		// 不描边：冷白底色本身已经和玻璃拉开明度差，1 DIP 描边会被 DWM 合成摊成过渡色。
		// 留一个全透明 Brush 保持调用点不变。
		return Tokens.Brush(System.Windows.Media.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
	}

	private void UpdateCategoryChips()
	{
		if (categoryChips.Count == 0)
		{
			return;
		}
		System.Windows.Media.Color color = IconPacks.AccentColor(C.Prefs.Dock.Accent);
		// 分段控件选中片：不透明强调色实心底 + 白字（Apple 的做法是「反色块」，对比度最高）。
		// 未选中：实心中性灰底（见 CategoryChipIdleFill），两者明度差 >30 灰阶，一眼可分。
		// 悬停态由 MouseEnter/Leave 单独提亮一档。
		bool light = LightTheme;
		SolidColorBrush selectedFill = new SolidColorBrush(color);
		selectedFill.Freeze();
		SolidColorBrush selectedInk = new SolidColorBrush(Tokens.OnColor(color));
		selectedInk.Freeze();
		SolidColorBrush idleFill = CategoryChipIdleFill();
		SolidColorBrush idleBorder = CategoryChipIdleBorder();
		SolidColorBrush idleInk = new SolidColorBrush(light ? Tokens.Light.Label : Tokens.Dark.Label);
		foreach (KeyValuePair<string, Border> categoryChip in categoryChips)
		{
			bool flag = string.Equals(categoryChip.Key, activeCategory, StringComparison.OrdinalIgnoreCase);
			Border value = categoryChip.Value;
			value.Background = (flag ? selectedFill : idleFill);
			value.BorderBrush = idleBorder;
			value.BorderThickness = CategoryChipBorderThickness;
			value.Clip = null;
			if (value.Child is StackPanel stackPanel)
			{
				foreach (object child in stackPanel.Children)
				{
					if (child is TextBlock textBlock)
					{
						textBlock.Foreground = (flag ? selectedInk : idleInk);
					}
					else if (child != null && child is not StackPanel)
					{
						PaintCategoryGlyph(child, flag ? selectedInk : idleInk);
					}
				}
			}
			if (categoryCounts.TryGetValue(categoryChip.Key, out var value2))
			{
				value2.Text = AppCategoryRules.Count(C.Prefs.Apps, categoryChip.Key).ToString();
			}
		}
		// 已选中某个分类时，再点它一次 = 取消筛选，回到「显示全部」（用户 m01602：
		// 分类栏里不再有单独的「全部」标签，所以取消筛选的入口就是「再点当前分类」）。
		foreach (KeyValuePair<string, Border> categoryChip2 in categoryChips)
		{
			bool flag2 = string.Equals(categoryChip2.Key, activeCategory, StringComparison.OrdinalIgnoreCase);
			Border value3 = categoryChip2.Value;
			value3.ToolTip = (flag2 ? "再点一次取消筛选，显示全部应用；拖动标签可排序；右键可重命名 / 删除" : "鼠标悬停即切换到此分类；把应用图标拖到标签上即可归类；拖动标签可排序；右键可重命名 / 删除");
		}
	}

	// 空分类（0 项）直接从分类表里删除，而不是隐藏。
	// 例外：当前选中的分类（否则刚新建的空分类会立刻消失，没法往里面拖图标）
	// 以及正在拖动图标时（临时保留所有分类，才能把图标拖进空分类）。
	// 有删除时把活跃分类归位并重建分类栏；重入保护避免重建过程中的再次触发。
	private bool pruningCategories;

	// 只在本次进程第一次打开面板时自动清理一次：
	// 每次打开都删会让「刚新建、还没来得及放图标」的分类直接消失。

	private bool prunedThisSession;

	private void PruneEmptyCategories()
	{
		if (prunedThisSession || pruningCategories || activeDrag || !IsVisible)
		{
			return;
		}
		prunedThisSession = true;
		List<string> empties = null;
		foreach (AppCategoryRules.Category category in AppCategoryRules.Categories)
		{
			string id = category.Id;
			if (string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			// 只自动删内置的空分类（游戏/设计/安全/其他 这类默认就空的）。
			// 用户自建的分类一律不动：他们可能刚建好还没往里放图标。
			// 自动删掉会让人以为「新建分类」坏了。自建的留给右键「删除空分类」。
			if (!AppCategoryRules.IsBuiltIn(id))
			{
				continue;
			}
			if (string.Equals(id, activeCategory, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (AppCategoryRules.Count(C.Prefs.Apps, id) <= 0)
			{
				if (empties == null)
				{
					empties = new List<string>();
				}
				empties.Add(id);
			}
		}
		if (empties == null || empties.Count == 0)
		{
			return;
		}
		pruningCategories = true;
		try
		{
			foreach (string id2 in empties)
			{
				AppCategoryRules.Remove(id2);
			}
			C.Prefs.Categories = AppCategoryRules.Snapshot();
			C.Save();
			Store.Trace("CATEGORY prune empty n=" + empties.Count);
			RefreshCategoryBar();
			// 只在当前分类确实被删掉时才归位，别把用户正在看的分类和页码一起重置。
			ResetCategoryIfMissing();
			Render(animate: false);
			ApplyStatus("已删除 " + empties.Count + " 个空分类");
		}
		finally
		{
			pruningCategories = false;
		}
	}

	// 空分类（0 项）不再自动隐藏，而是直接从分类栏删除（避免残留空壳）。
	// 例外：全部、当前选中的分类（否则新建的空分类会立刻消失，无法往里面拖图标）。
	// 以及正在拖动图标时（临时保留所有分类，才能把图标拖进空分类）。
	private void ApplyCategoryChipVisibility()
	{
		foreach (KeyValuePair<string, Border> categoryChip in categoryChips)
		{
			categoryChip.Value.Visibility = Visibility.Visible;
		}
	}

	// id 为空 / "all" = 显示全部（「全部」不是分类标签，只是内部虚拟视图）。
	private void SetCategory(string id)
	{
		if (string.IsNullOrEmpty(id))
		{
			id = "all";
		}
		if (!string.Equals(activeCategory, id, StringComparison.OrdinalIgnoreCase))
		{
			activeCategory = id;
			page = 0;
			UpdateCategoryChips();
			Render(animate: true);
		}
	}

	private void AssignCategory(Entry entry, string id)
	{
		if (entry != null && !string.IsNullOrEmpty(id) && !string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
		{
			entry.Category = id;
			C.Save();
			UpdateCategoryChips();
			Render(animate: true);
			ApplyStatus("已归类：" + entry.Name + " → " + AppCategoryRules.Name(id));
		}
	}

	private const string CategoryDragFormat = "IconBox.CategoryId";

	// 编辑分类（新建 / 重命名 / 删除 / 排序）后短暂屏蔽悬停切换。
	// 否则鼠标仍停在原来的标签上，会立刻把刚切过去的分类又切走。
	private int categoryHoverLockUntil;

	private void LockCategoryHover(int ms)
	{
		categoryHoverLockUntil = Environment.TickCount + ms;
	}

	// 模态对话框会让面板失活；若不屏蔽 Deactivated 的自动收起，
	// 弹窗一出现面板就在背后消失，改完名字也看不到结果。
	private string AskModal(string title, string initial)
	{
		bool prev = ModalBusy;
		ModalBusy = true;
		try
		{
			return FeatureUi.Ask(this, title, initial);
		}
		finally
		{
			ModalBusy = prev;
		}
	}

	private bool ConfirmModal(string message, string title)
	{
		bool prev = ModalBusy;
		ModalBusy = true;
		try
		{
			return System.Windows.MessageBox.Show(this, message, title, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;
		}
		finally
		{
			ModalBusy = prev;
		}
	}

	private static bool IsCategoryDrag(System.Windows.DragEventArgs e)
	{
		return e != null && e.Data != null && e.Data.GetDataPresent(CategoryDragFormat);
	}

	private static string CategoryDragId(System.Windows.IDataObject data)
	{
		if (data == null || !data.GetDataPresent(CategoryDragFormat))
		{
			return null;
		}
		return data.GetData(CategoryDragFormat) as string;
	}

	// 拖到分类栏空白处（含标签两侧的留白）= 把被拖的分类移到最末。
	private bool CategoryDropToEnd(System.Windows.DragEventArgs e)
	{
		string text = CategoryDragId(e.Data);
		if (text == null)
		{
			return false;
		}
		MoveCategoryToEnd(text);
		return true;
	}

	// 把 draggedId 移动到 targetId 之前；两者相同时不动。
	private void MoveCategoryBefore(string draggedId, string targetId)
	{
		if (string.IsNullOrEmpty(draggedId) || string.Equals(draggedId, targetId, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		List<string> list = AppCategoryRules.Categories.Select((AppCategoryRules.Category c) => c.Id).ToList();
		if (!list.RemoveAll((string x) => string.Equals(x, draggedId, StringComparison.OrdinalIgnoreCase)).Equals(1))
		{
			return;
		}
		int num = list.FindIndex((string x) => string.Equals(x, targetId, StringComparison.OrdinalIgnoreCase));
		if (num < 0)
		{
			return;
		}
		list.Insert(num, draggedId);
		LockCategoryHover(900);
		C.ReorderCategories(list);
		ApplyStatus("已调整分类顺序");
	}

	private void MoveCategoryToEnd(string draggedId)
	{
		if (string.IsNullOrEmpty(draggedId))
		{
			return;
		}
		List<string> list = AppCategoryRules.Categories.Select((AppCategoryRules.Category c) => c.Id).ToList();
		if (!list.RemoveAll((string x) => string.Equals(x, draggedId, StringComparison.OrdinalIgnoreCase)).Equals(1))
		{
			return;
		}
		list.Add(draggedId);
		LockCategoryHover(900);
		C.ReorderCategories(list);
		ApplyStatus("已调整分类顺序");
	}

	// 分类列表变化后重建分类栏并刷新内容（不带动画，避免编辑时闪动）。
	public void RefreshCategoryBar()
	{
		BuildCategoryBar();
		Render(animate: false);
	}

	// 面板打开时的默认分类：优先第一个有图标的分类，其次第一个分类，兜底「全部」。
	private string FirstCategoryId()
	{
		string text = null;
		foreach (AppCategoryRules.Category category in AppCategoryRules.Categories)
		{
			if (string.Equals(category.Id, "all", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (text == null)
			{
				text = category.Id;
			}
			if (AppCategoryRules.Count(C.Prefs.Apps, category.Id) > 0)
			{
				return category.Id;
			}
		}
		return text ?? "all";
	}

	// 当前分类被删除时回到“全部”。
	public void ResetCategoryIfMissing()
	{
		if (AppCategoryRules.Find(activeCategory) == null)
		{
			activeCategory = "all";
			page = 0;
			UpdateCategoryChips();
			Render(animate: false);
		}
	}

	private void RenameCategoryInteractive(string id)
	{
		LockCategoryHover(900);
		string text = AskModal("重命名分类", AppCategoryRules.Name(id));
		LockCategoryHover(900);
		if (text == null)
		{
			return;
		}
		if (!C.RenameCategory(id, text))
		{
			ApplyStatus("分类名无效或未变化");
			return;
		}
		ApplyStatus("已重命名分类「" + AppCategoryRules.Name(id) + "」");
	}

	private void AddCategoryInteractive()
	{
		LockCategoryHover(900);
		string text = AskModal("新建分类", "新分类");
		LockCategoryHover(900);
		if (text == null)
		{
			return;
		}
		string text2 = C.AddCategory(text);
		if (text2 == null)
		{
			ApplyStatus("分类名无效或重复");
			return;
		}
		SetCategory(text2);
		ApplyStatus("已新建分类「" + AppCategoryRules.Name(text2) + "」。把图标拖到标签上即可归类");
	}

	private void DeleteCategoryInteractive(string id)
	{
		if (string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		string text = AppCategoryRules.Name(id);
		LockCategoryHover(900);
		if (!ConfirmModal("删除分类「" + text + "」？该分类下的应用会回到自动推断", "删除分类"))
		{
			return;
		}
		C.RemoveCategory(id);
		LockCategoryHover(900);
	}

	private void ResetCategoriesInteractive()
	{
		LockCategoryHover(900);
		if (!ConfirmModal("恢复默认分类？自定义分类与顺序会被清空（应用的归类会保留）", "恢复默认分类"))
		{
			return;
		}
		C.ResetCategories();
		LockCategoryHover(900);
	}

	// 手动清理：把 0 项的自定义分类直接删掉（内置分类不删）。
	private void CleanEmptyCategoriesInteractive()
	{
		LockCategoryHover(900);
		int num = C.RemoveEmptyCategories();
		LockCategoryHover(900);
		if (num == 0)
		{
			ApplyStatus("没有需要删除的空分类");
		}
	}

	private void BuildSearchPopup(Border anchor)
	{
		searchPopup = new Popup
		{
			PlacementTarget = anchor,
			Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
			StaysOpen = false,
			AllowsTransparency = true,
			PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
			VerticalOffset = 2.0
		};
		System.Windows.Media.Color color = (C.Prefs.Light ? System.Windows.Media.Color.FromRgb(250, 252, byte.MaxValue) : System.Windows.Media.Color.FromRgb(28, 32, 44));
		System.Windows.Media.Color color2 = (C.Prefs.Light ? System.Windows.Media.Color.FromRgb(32, 38, 53) : System.Windows.Media.Color.FromRgb(244, 246, 252));
		System.Windows.Media.Color color3 = (C.Prefs.Light ? System.Windows.Media.Color.FromRgb(210, 216, 226) : System.Windows.Media.Color.FromRgb(62, 72, 96));
		color.A = byte.MaxValue;
		color3.A = 180;
		searchList = new System.Windows.Controls.ListBox
		{
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			MaxHeight = 400.0
		};
		ScrollViewer.SetVerticalScrollBarVisibility(searchList, ScrollBarVisibility.Hidden);
		ScrollViewer.SetHorizontalScrollBarVisibility(searchList, ScrollBarVisibility.Disabled);
		Border border = new Border
		{
			CornerRadius = new CornerRadius(14.0),
			Background = new SolidColorBrush(color),
			BorderBrush = new SolidColorBrush(color3),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(6.0),
			MinWidth = 300.0,
			MaxWidth = 460.0,
			Child = searchList
		};
		Style style = new Style(typeof(System.Windows.Controls.ListBox));
		style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
		style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, new SolidColorBrush(color2)));
		style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0.0)));
		border.Resources[typeof(System.Windows.Controls.ListBox)] = style;
		Style style2 = new Style(typeof(ListBoxItem));
		style2.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
		style2.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(8.0, 6.0, 8.0, 6.0)));
		style2.Setters.Add(new Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch));
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8.0));
		frameworkElementFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
		frameworkElementFactory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
		style2.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
		{
			VisualTree = frameworkElementFactory
		}));
		border.Resources[typeof(ListBoxItem)] = style2;
		searchPopup.Child = border;
	}

	private void RunSearch()
	{
		int serial = ++searchSerial;
		if (searchCts != null)
		{
			searchCts.Cancel();
			searchCts.Dispose();
			searchCts = null;
		}
		string query = ((Search == null) ? "" : Search.Text.Trim());
		searchItems = new List<SystemSearchResult>();
		if (searchList != null)
		{
			searchList.ItemsSource = null;
		}
		if (query.Length == 0)
		{
			if (searchPopup != null)
			{
				searchPopup.IsOpen = false;
			}
			return;
		}
		if (searchPopup != null)
		{
			searchPopup.IsOpen = true;
		}
		searchCts = new CancellationTokenSource();
		CancellationToken ct = searchCts.Token;
		Task.Run(() =>
		{
			List<SystemSearchResult> instant = SystemSearch.Instant(query);
			if (serial != searchSerial || ct.IsCancellationRequested)
			{
				return;
			}
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (serial == searchSerial)
				{
					SetSearchResults(instant);
				}
			}));
			try
			{
				SystemSearch.Deep(query, (List<SystemSearchResult> batch2) =>
				{
					if (serial == searchSerial && !ct.IsCancellationRequested)
					{
						Dispatcher.BeginInvoke((Action)(() =>
						{
							if (serial == searchSerial)
							{
								SetSearchResults(batch2);
							}
						}));
					}
				}, ct);
			}
			catch (OperationCanceledException)
			{
			}
			catch
			{
			}
		});
	}

	private void SetSearchResults(List<SystemSearchResult> incoming)
	{
		if (searchList == null)
		{
			return;
		}
		Dictionary<string, bool> dictionary = new Dictionary<string, bool>();
		foreach (SystemSearchResult searchItem in searchItems)
		{
			dictionary[searchItem.Path.ToLowerInvariant()] = true;
		}
		foreach (SystemSearchResult item in incoming)
		{
			string key = item.Path.ToLowerInvariant();
			if (!dictionary.ContainsKey(key))
			{
				dictionary[key] = true;
				searchItems.Add(item);
			}
		}
		// 先按优先级（快捷方式 > 可执行文件 > 文件 > 文档），再按名字贴合度，最后按名字长度。
		// 只按名字排会让 Program Files 里的零散文件（jswpsapi.dll 之类）排到真正的应用前面。
		string searchQuery = (Search != null) ? Search.Text : "";
		searchItems.Sort((SystemSearchResult a, SystemSearchResult b) =>
		{
			int byRank = SystemSearch.Rank(b).CompareTo(SystemSearch.Rank(a));
			if (byRank != 0)
			{
				return byRank;
			}
			int byName = SystemSearch.NameScore(b, searchQuery).CompareTo(SystemSearch.NameScore(a, searchQuery));
			if (byName != 0)
			{
				return byName;
			}
			int byLength = (a.Name ?? "").Length.CompareTo((b.Name ?? "").Length);
			return (byLength != 0) ? byLength : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
		});
		List<ListBoxItem> list = new List<ListBoxItem>();
		int num = Math.Min(searchItems.Count, 60);
		for (int num2 = 0; num2 < num; num2++)
		{
			list.Add(MakeSearchRow(searchItems[num2]));
		}
		searchList.ItemsSource = list;
	}

	private ListBoxItem MakeSearchRow(SystemSearchResult r)
	{
		System.Windows.Media.Color color = (C.Prefs.Light ? System.Windows.Media.Color.FromRgb(32, 38, 53) : System.Windows.Media.Color.FromRgb(244, 246, 252));
		System.Windows.Media.Color color2 = (C.Prefs.Light ? System.Windows.Media.Color.FromRgb(58, 96, 205) : System.Windows.Media.Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));
		SolidColorBrush hoverBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, color2.R, color2.G, color2.B));
		ListBoxItem row = new ListBoxItem
		{
			Background = System.Windows.Media.Brushes.Transparent,
			Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
			Focusable = false
		};
		row.MouseEnter += delegate
		{
			row.Background = hoverBrush;
		};
		row.MouseLeave += delegate
		{
			row.Background = System.Windows.Media.Brushes.Transparent;
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		Border border = new Border
		{
			Width = 32.0,
			Height = 32.0,
			VerticalAlignment = VerticalAlignment.Center,
			CornerRadius = new CornerRadius(7.0),
			Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(22, color.R, color.G, color.B)),
			Child = new System.Windows.Controls.Image
			{
				Width = 26.0,
				Height = 26.0,
				Stretch = Stretch.Uniform,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		System.Windows.Controls.Image image = (System.Windows.Controls.Image)border.Child;
		if (r.IsFolder)
		{
			image.Source = FolderGlyph();
		}
		else
		{
			LoadSearchIcon(image, r.Path);
		}
		stackPanel.Children.Add(border);
		StackPanel stackPanel2 = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
			MaxWidth = 330.0
		};
		TextBlock element = new TextBlock
		{
			Text = r.Name,
			Foreground = new SolidColorBrush(color),
			FontSize = 14.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		stackPanel2.Children.Add(element);
		string text;
		if (r.IsFolder)
		{
			text = "文件";
		}
		else
		{
			string text2;
			switch (((r.Path.LastIndexOf('.') > r.Path.LastIndexOf('\\')) ? r.Path.Substring(r.Path.LastIndexOf('.') + 1) : "").ToLowerInvariant())
			{
			default:
				text2 = "文件";
				break;
			case "exe":
			case "lnk":
			case "url":
			case "appref-ms":
				text2 = "应用";
				break;
			}
			text = text2;
		}
		TextBlock element2 = new TextBlock
		{
			Text = text,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, color.R, color.G, color.B)),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		stackPanel2.Children.Add(element2);
		stackPanel.Children.Add(stackPanel2);
		row.Content = stackPanel;
		row.MouseLeftButtonUp += (object s, MouseButtonEventArgs e2) =>
		{
			OpenSearchResult(r);
		};
		row.PreviewMouseRightButtonUp += (object s, MouseButtonEventArgs e2) =>
		{
			e2.Handled = true;
			if (searchPopup != null)
			{
				searchPopup.IsOpen = false;
			}
			Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() =>
			{
				ShellContextMenu.ShowAtCursor(r.Path, this);
			}));
		};
		return row;
	}

	private void LoadSearchIcon(System.Windows.Controls.Image image, string path)
	{
		Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
		{
			try
			{
				ImageSource imageSource = Native.Icon(path, showShortcutOverlays: false, sharper: true);
				if (imageSource != null)
				{
					image.Source = imageSource;
				}
			}
			catch
			{
			}
		}));
	}

	private ImageSource FolderGlyph()
	{
		DrawingImage drawingImage = new DrawingImage(new DrawingGroup
		{
			Children = { (Drawing)new GeometryDrawing(new SolidColorBrush(System.Windows.Media.Color.FromArgb(byte.MaxValue, 242, 193, 104)), null, Geometry.Parse("M 2,5 L 8,5 L 10,7 L 18,7 L 18,17 L 2,17 Z")) }
		});
		drawingImage.Freeze();
		return drawingImage;
	}

	private void OpenSearchResult(SystemSearchResult r)
	{
		if (searchPopup != null)
		{
			searchPopup.IsOpen = false;
		}
		Keyboard.ClearFocus();
		SystemSearch.Open(r);
	}

	public void SetStatus(string text)
	{
		ApplyStatus(text);
	}

	private void ApplyStatus(string text)
	{
		// pendingStatus 是唯一的状态来源：Render 会用它回填 Status。
		// 如果这里不更新，下一次 Render 就会把刚写的提示覆盖成旧提示。
		pendingStatus = text;
		ShowStatus(text);
		if (statusTimer == null)
		{
			statusTimer = new DispatcherTimer(DispatcherPriority.Background);
			statusTimer.Interval = TimeSpan.FromSeconds(4.5);
			statusTimer.Tick += delegate
			{
				statusTimer.Stop();
				ApplyStatus("");
			};
		}
		statusTimer.Stop();
		if (!string.IsNullOrEmpty(text))
		{
			statusTimer.Start();
		}
	}

	// 只改显示，不动 pendingStatus / 计时器：Render 回填时用这个。
	// 否则每次重绘都会把倒计时重置，提示又变成永久挂着。
	private void ShowStatus(string text)
	{
		if (Status != null)
		{
			Status.Text = text ?? "";
			Status.Visibility = (string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void SetSelectionMode(bool enabled)
	{
		editing = enabled;
		selected.Clear();
		dragEntry = null;
		if (editSelection != null)
		{
			editSelection.Content = (enabled ? "完成" : "多选");
		}
		// 不再整片重建磁贴：只切换勾选框可见性与选中高亮
		SyncSelectionVisuals();
		UpdateSelectionBar();
	}

	// 就地同步所有磁贴的选中外观与勾选框状态（O(磁贴数) 的轻量属性写入，不重建可视树）。
	private void SyncSelectionVisuals()
	{
		if (Tiles == null)
		{
			return;
		}
		foreach (object child in Tiles.Children)
		{
			if (!(child is Border tile) || !(tile.Tag is Entry entry))
			{
				continue;
			}
			bool on = editing && selected.Contains(entry.Path);
			tile.Background = (on ? SelectionFill : System.Windows.Media.Brushes.Transparent);
			tile.BorderBrush = (on ? SelectionEdge : System.Windows.Media.Brushes.Transparent);
			if (!(tile.Child is Grid grid))
			{
				continue;
			}
			foreach (object item in grid.Children)
			{
				if (item is System.Windows.Controls.CheckBox box && box.Tag is Entry)
				{
					box.Visibility = (editing ? Visibility.Visible : Visibility.Collapsed);
					box.IsChecked = on;
				}
			}
		}
	}

	public void FinishSelection()
	{
		SetSelectionMode(enabled: false);
	}

	private void SelectFiltered()
	{
		foreach (Entry item in Filtered())
		{
			selected.Add(item.Path);
		}
		SyncSelectionVisuals();
		UpdateSelectionBar();
	}

	private void UpdateSelectionBar()
	{
		selected.RemoveWhere((string p) => !C.Prefs.Apps.Any((Entry a) => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase)));
		bool flag = editing;
		bulkBar.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		bulkRow.Height = (flag ? GridLength.Auto : new GridLength(0.0));
		selectAll.IsEnabled = editing;
		clearSelection.IsEnabled = editing && selected.Count > 0;
		deleteSelected.IsEnabled = editing && selected.Count > 0;
		deleteSelected.Content = "移除所选（" + selected.Count + "）";
		selectionCount.Text = (editing ? ("已选 " + selected.Count + " · 跨页保留") : "");
	}

	public void Render(bool animate = false)
	{
		CloseMediaPreview();
		HideInsertMarker();
		if (Tiles == null || (!IsVisible && !preparingView))
		{
			return;
		}
		// 快速连续切换分类（鼠标扫过分类标签）时不再叠加逐块入场动画。
		// 动画排队会让每帧要合成的图层成倍增加，这正是切换时卡顿的主要来源。
		bool animateNow = animate && MotionEnabled && Environment.TickCount - lastAnimateTick >= 260;
		if (animateNow)
		{
			lastAnimateTick = Environment.TickCount;
		}
		UpdateSelectionBar();
		Tiles.Children.Clear();
		List<Entry> list = Filtered();
		int num = LayoutRules.Pages(list.Count, capacity);
		page = Math.Min(page, num - 1);
		Pager.Text = page + 1 + " / " + num;
		// 分页栏只服务翻页：「全部」视图的入口已回到分类标签行，这里不再为筛选态保留空栏。
		bool multiPage = num > 1;
		PageNav.Visibility = (multiPage ? Visibility.Visible : Visibility.Collapsed);
		if (pagePrevButton != null)
		{
			pagePrevButton.Visibility = (multiPage ? Visibility.Visible : Visibility.Collapsed);
		}
		if (pageNextButton != null)
		{
			pageNextButton.Visibility = (multiPage ? Visibility.Visible : Visibility.Collapsed);
		}
		if (Pager != null)
		{
			Pager.Visibility = (multiPage ? Visibility.Visible : Visibility.Collapsed);
		}
		// 空态提示已按用户要求移除：不再在面板背景上叠加「还没有应用 / 拖到这里」文字。
		// 背后 bug 的根因是该提示此前从未被正确隐藏、始终叠在磁贴背景上；这里彻底隐藏它。
		Empty.Visibility = Visibility.Collapsed;
		UpdateCategoryChips();
		if (pendingStatus != null)
		{
			ShowStatus(pendingStatus);
		}
		int num2 = 0;
		foreach (Entry item in list.Skip(page * capacity).Take(capacity))
		{
			Tiles.Children.Add(Tile(item, num2++, animateNow));
		}
	}

	private void ChangePage(int delta)
	{
		int num = LayoutRules.Pages(Filtered().Count, capacity);
		page = Math.Max(0, Math.Min(num - 1, page + delta));
		Render(animate: true);
	}

	private void QueueMediaPreview(Entry entry, FrameworkElement anchor)
	{
		if (editing || !MediaThumb.IsPreviewable(entry.Path) || (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen && mediaPreviewAnchor == anchor))
		{
			return;
		}
		mediaPreviewEntry = entry;
		mediaPreviewAnchor = anchor;
		if (mediaPreviewTimer == null)
		{
			mediaPreviewTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(450.0)
			};
			mediaPreviewTimer.Tick += (object s, EventArgs e) =>
			{
				mediaPreviewTimer.Stop();
				Entry entry2 = mediaPreviewEntry;
				FrameworkElement frameworkElement = mediaPreviewAnchor;
				if (entry2 != null && frameworkElement != null)
				{
					OpenMediaPreview(entry2, frameworkElement);
				}
			};
		}
		mediaPreviewTimer.Stop();
		mediaPreviewTimer.Start();
	}

	private void CancelMediaPreview()
	{
		mediaPreviewTimer?.Stop();
		CloseMediaPreview();
	}

	private void OnMediaTileLeave()
	{
		if (mediaPreviewPopup == null || !mediaPreviewPopup.IsOpen)
		{
			CancelMediaPreview();
		}
	}

	private bool IsCursorOverMediaPreview()
	{
		System.Drawing.Point position = System.Windows.Forms.Cursor.Position;
		System.Windows.Point point = new System.Windows.Point(position.X, position.Y);
		if (mediaPreviewAnchor != null && mediaPreviewAnchor.IsVisible)
		{
			System.Windows.Point point2 = mediaPreviewAnchor.PointToScreen(new System.Windows.Point(0.0, 0.0));
			if (new Rect(point2.X, point2.Y, Math.Max(1.0, mediaPreviewAnchor.ActualWidth), Math.Max(1.0, mediaPreviewAnchor.ActualHeight)).Contains(point))
			{
				return true;
			}
		}
		if (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen && mediaPreviewPopup.Child is FrameworkElement { IsVisible: not false } frameworkElement)
		{
			System.Windows.Point point3 = frameworkElement.PointToScreen(new System.Windows.Point(0.0, 0.0));
			if (new Rect(point3.X, point3.Y, Math.Max(1.0, frameworkElement.ActualWidth), Math.Max(1.0, frameworkElement.ActualHeight)).Contains(point))
			{
				return true;
			}
		}
		return false;
	}

	private void OpenMediaPreview(Entry entry, FrameworkElement anchor)
	{
		CloseMediaPreview();
		mediaPreviewPopup = new Popup
		{
			PlacementTarget = anchor,
			Placement = PlacementMode.Top,
			VerticalOffset = -8.0,
			AllowsTransparency = true,
			PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
			IsHitTestVisible = true,
			StaysOpen = true,
			Child = BuildMediaCard(entry)
		};
		mediaPreviewPopup.Closed += (object s, EventArgs e) =>
		{
			if (mediaPreviewPlayer != null)
			{
				try
				{
					mediaPreviewPlayer.Stop();
				}
				catch
				{
				}
				mediaPreviewPlayer.Source = null;
				mediaPreviewPlayer = null;
			}
		};
		mediaPreviewPopup.IsOpen = true;
	}

	private FrameworkElement BuildMediaCard(Entry entry)
	{
		string extension = System.IO.Path.GetExtension(entry.Path);
		bool flag = MediaThumb.IsVideo(entry.Path);
		Grid host = new Grid
		{
			Width = 424.0,
			Height = 238.0,
			ClipToBounds = true
		};
		if (flag)
		{
			MediaElement mediaElement = new MediaElement
			{
				Stretch = Stretch.Uniform,
				LoadedBehavior = MediaState.Play,
				UnloadedBehavior = MediaState.Close,
				Volume = 0.0,
				ScrubbingEnabled = true
			};
			mediaElement.MediaFailed += (object s, ExceptionRoutedEventArgs e) =>
			{
				host.Children.Clear();
				host.Children.Add(new TextBlock
				{
					Text = "无法解码该视频（缺少系统解码器）",
					Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 155, 170)),
					FontSize = 13.0,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				});
			};
			mediaElement.Source = new Uri(entry.Path);
			mediaPreviewPlayer = mediaElement;
			host.Children.Add(mediaElement);
		}
		else
		{
			ImageSource imageSource = null;
			if (string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase))
			{
				imageSource = MediaThumb.SvgPreview(entry.Path, 480);
			}
			else
			{
				imageSource = MediaThumb.LoadFile(entry.Path, 480);
				if (imageSource == null)
				{
					imageSource = ShellThumbnail.Get(entry.Path, 480);
				}
			}
			if (imageSource != null)
			{
				System.Windows.Controls.Image image = new System.Windows.Controls.Image
				{
					Source = imageSource,
					MaxWidth = 424.0,
					MaxHeight = 238.0,
					Stretch = Stretch.Uniform
				};
				RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
				host.Children.Add(image);
			}
			else
			{
				host.Children.Add(new TextBlock
				{
					Text = "无法生成预览",
					Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 155, 170)),
					FontSize = 13.0,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				});
			}
		}
		string text = "上一页";
		try
		{
			text = FormatBytes(new FileInfo(entry.Path).Length);
		}
		catch
		{
		}
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(12.0, 9.0, 12.0, 11.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = entry.Name,
			FontSize = 13.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = System.Windows.Media.Brushes.White,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = (flag ? "视频 · " : "图片 · ") + text + " · 点击磁贴用默认应用打开",
			FontSize = 11.0,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 155, 170)),
			Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
		});
		StackPanel stackPanel2 = new StackPanel();
		stackPanel2.Children.Add(host);
		stackPanel2.Children.Add(stackPanel);
		Border border = new Border();
		border.CornerRadius = new CornerRadius(10.0);
		border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(247, 32, 35, 45));
		border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		border.BorderThickness = new Thickness(1.0);
		border.Child = stackPanel2;
		border.SnapsToDevicePixels = true;
		border.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			e.Handled = true;
			CloseMediaPreview();
			C.Launch(entry);
		};
		border.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (!IsCursorOverMediaPreview())
			{
				CancelMediaPreview();
			}
		};
		return border;
	}

	private void CloseMediaPreview()
	{
		mediaPreviewTimer?.Stop();
		if (mediaPreviewPopup != null)
		{
			mediaPreviewPopup.IsOpen = false;
			mediaPreviewPopup = null;
		}
		if (mediaPreviewPlayer != null)
		{
			MediaElement mediaElement = mediaPreviewPlayer;
			mediaPreviewPlayer = null;
			try
			{
				mediaElement.Stop();
			}
			catch
			{
			}
			mediaElement.Source = null;
		}
	}

	private static string FormatBytes(long bytes)
	{
		string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
		double num = bytes;
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		return num.ToString((num2 == 0) ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + array[num2];
	}

	private void TileSetIcon(StackPanel panel, ImageSource source)
	{
		if (source != null && panel != null && panel.Parent != null)
		{
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = source,
				Width = C.Prefs.BoardIconSize,
				Height = C.Prefs.BoardIconSize,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			if (panel.Children.Count > 0)
			{
				panel.Children.RemoveAt(0);
			}
			panel.Children.Insert(0, image);
		}
	}

	// 取不到系统图标时的占位图标：按文件类型画一张彩色文档卡片，不再使用「▣」方块。
	// 文件已被删除（路径不存在）时降低不透明度，视觉上区分「失效条目」。
	private FrameworkElement FileTypePlaceholder(Entry a)
	{
		double size = Math.Max(20.0, C.Prefs.BoardIconSize);
		bool isFolder = a.IsFolder || System.IO.Directory.Exists(a.Path);
		bool missing = !isFolder && !System.IO.File.Exists(a.Path);
		System.Windows.Media.Color color = (isFolder ? System.Windows.Media.Color.FromRgb(240, 191, 77) : FileTypeColor(a.Path));
		// 统一圆角底板：外框正好等于 BoardIconSize 方形（磁贴里所有图标占同一个视觉位）。
		// 内部再放一张「彩色文档卡片」，卡片边长 = 方块的 62%，圆角与外框同族。
		double card = size * 0.62;
		Grid page = new Grid
		{
			Width = size,
			Height = size,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			Opacity = (missing ? 0.55 : 1.0)
		};
		// 圆角底板（squircle 观感）
		page.Children.Add(new Border
		{
			Width = size,
			Height = size,
			CornerRadius = new CornerRadius(size * IconTileDecorator.RadiusRatio),
			Background = IconTileDecorator.PlateBrush(LightTheme)
		});
		// 彩色文档卡片：横向居中、垂直略下（贴近桌面图标观感）
		Grid cardGrid = new Grid
		{
			Width = card,
			Height = card * 1.12,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = System.Windows.VerticalAlignment.Center
		};
		cardGrid.Children.Add(new Border
		{
			CornerRadius = new CornerRadius(card * 0.13),
			Background = new System.Windows.Media.SolidColorBrush(color)
		});
		StackPanel lines = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(card * 0.18, 0.0, card * 0.18, card * 0.20)
		};
		double[] ratios = new double[3] { 1.0, 1.0, 0.62 };
		foreach (double ratio in ratios)
		{
			lines.Children.Add(new Border
			{
				Height = Math.Max(1.5, card * 0.072),
				Width = card * 0.64 * ratio,
				CornerRadius = new CornerRadius(card * 0.04),
				Background = System.Windows.Media.Brushes.White,
				Opacity = 0.92,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
				Margin = new Thickness(0.0, 0.0, 0.0, Math.Max(1.5, card * 0.075))
			});
		}
		cardGrid.Children.Add(lines);
		page.Children.Add(cardGrid);
		return page;
	}

	// 按扩展名给文件卡片配色（Word 蓝 / Excel 绿 / PPT 橙 / PDF 红…）。
	private static System.Windows.Media.Color FileTypeColor(string path)
	{
		string extension = "";
		try
		{
			extension = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
		}
		catch
		{
		}
		switch (extension)
		{
		case ".doc":
		case ".docx":
		case ".rtf":
		case ".odt":
		case ".wps":
			return System.Windows.Media.Color.FromRgb(43, 87, 154);
		case ".xls":
		case ".xlsx":
		case ".csv":
		case ".et":
			return System.Windows.Media.Color.FromRgb(33, 115, 70);
		case ".ppt":
		case ".pptx":
		case ".dps":
			return System.Windows.Media.Color.FromRgb(210, 71, 38);
		case ".pdf":
			return System.Windows.Media.Color.FromRgb(217, 48, 37);
		case ".txt":
		case ".md":
		case ".log":
		case ".ini":
		case ".json":
		case ".xml":
		case ".yml":
		case ".yaml":
			return System.Windows.Media.Color.FromRgb(107, 114, 128);
		case ".png":
		case ".jpg":
		case ".jpeg":
		case ".gif":
		case ".bmp":
		case ".webp":
		case ".svg":
		case ".psd":
		case ".ai":
		case ".dwg":
			return System.Windows.Media.Color.FromRgb(124, 58, 237);
		case ".mp4":
		case ".mkv":
		case ".avi":
		case ".mov":
		case ".wmv":
		case ".mp3":
		case ".wav":
		case ".flac":
		case ".ape":
		case ".m4a":
			return System.Windows.Media.Color.FromRgb(14, 165, 233);
		case ".zip":
		case ".rar":
		case ".7z":
		case ".tar":
		case ".gz":
			return System.Windows.Media.Color.FromRgb(180, 83, 9);
		case ".exe":
		case ".msi":
		case ".bat":
		case ".cmd":
		case ".ps1":
			return System.Windows.Media.Color.FromRgb(71, 85, 105);
		default:
			return System.Windows.Media.Color.FromRgb(100, 116, 139);
		}
	}

	private FrameworkElement Tile(Entry a, int index, bool animate)
	{
		bool isSelected = editing && selected.Contains(a.Path);
		Border tile = new Border
		{
			Tag = a,
			Background = (isSelected ? SelectionFill : System.Windows.Media.Brushes.Transparent),
			BorderThickness = new Thickness(1.0),
			BorderBrush = (isSelected ? SelectionEdge : System.Windows.Media.Brushes.Transparent),
			CornerRadius = new CornerRadius(18.0),
			Margin = new Thickness(C.Prefs.BoardIconGap),
			Padding = new Thickness(4.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			ToolTip = a.Name,
			AllowDrop = true
		};
		tile.PreviewMouseRightButtonUp += (object s, MouseButtonEventArgs e2) =>
		{
			e2.Handled = true;
			if (!editing)
			{
				CloseMediaPreview();
				Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() =>
				{
					ShellContextMenu.ShowAtCursor(a.Path, this, () =>
					{
						System.Windows.Controls.ContextMenu contextMenu = C.Menu(a, C.Prefs.BoardMaterial.Tint, (int)(C.Prefs.OpacityPercent ?? 72.0));
						contextMenu.Placement = PlacementMode.MousePoint;
						contextMenu.PlacementTarget = tile;
						// 兜底菜单必须挂回磁贴，否则 IsContextMenuOpen 看不见它。
						// 点面板外时面板会被收起而菜单还开着。
						tile.ContextMenu = contextMenu;
						contextMenu.Closed += delegate
						{
							if (object.ReferenceEquals(tile.ContextMenu, contextMenu))
							{
								tile.ContextMenu = null;
							}
						};
						contextMenu.IsOpen = true;
					});
				}));
			}
		};
		Grid grid = new Grid();
		tile.Child = grid;
		StackPanel stackPanel = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		grid.Children.Add(stackPanel);
		ImageSource imageSource = IconPipeline.Request(a, C.Prefs.Dock.ShowShortcutOverlays, sharper: true, tile: true, (ImageSource img) =>
		{
			if (stackPanel.Parent != null && tile.Child != null)
			{
				// 同步阶段先放的是占位卡片；撤销后放真图。管道对「丑图标」返回 null，
				// 此时保留文件类型占位卡片。
				stackPanel.Children.Clear();
				if (img == null)
				{
					stackPanel.Children.Add(FileTypePlaceholder(a));
				}
				else
				{
					TileSetIcon(stackPanel, img);
				}
			}
		}, light: LightTheme);
		if (imageSource != null)
		{
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = imageSource,
				Width = C.Prefs.BoardIconSize,
				Height = C.Prefs.BoardIconSize,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			stackPanel.Children.Add(image);
		}
		else
		{
			// 还没提取到图标，或判定为丑图标（管道返回 null）：铺文件类型占位卡片；
			// 后台完成拿到真图后由 TileSetIcon 换图。
			stackPanel.Children.Add(FileTypePlaceholder(a));
		}
		stackPanel.Children.Add(new TextBlock
		{
			Text = a.Name,
			Foreground = TextBrush,
			FontSize = 12.0,
			TextAlignment = TextAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = Math.Max(20.0, ((Tiles.ActualWidth > 0.0) ? Tiles.ActualWidth : (Width - 104.0)) / (double)columns - 2.0 * C.Prefs.BoardIconGap - 10.0)
		});
		grid.Children.Add(new Border
		{
			Tag = "running",
			Width = 5.0,
			Height = 5.0,
			CornerRadius = new CornerRadius(3.0),
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(135, 205, byte.MaxValue)),
			VerticalAlignment = VerticalAlignment.Bottom,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			ToolTip = "正在运行，点击切回窗口",
			Visibility = ((!C.Prefs.Dock.ShowRunning || !RunningWindows.IsRunning(a)) ? Visibility.Collapsed : Visibility.Visible)
		});
		// 勾选框始终存在、只切 Visibility：这样进出多选、勾选单个图标都不必重建整片磁贴。
		// 原来每次勾选都要 Render() 重建 70 棵可视树，那正是多选时卡顿的来源。
		System.Windows.Controls.CheckBox check = new System.Windows.Controls.CheckBox
		{
			Tag = a,
			IsChecked = isSelected,
			IsHitTestVisible = false,
			Focusable = false,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(2.0),
			Visibility = (editing ? Visibility.Visible : Visibility.Collapsed)
		};
		grid.Children.Add(check);
		ScaleTransform scale = new ScaleTransform(1.0, 1.0);
		TranslateTransform lift = new TranslateTransform();
		TransformGroup transformGroup = new TransformGroup();
		transformGroup.Children.Add(scale);
		transformGroup.Children.Add(lift);
		tile.RenderTransform = transformGroup;
		tile.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		Action<bool> hover = (bool over) =>
		{
			Border border = tile;
			// 选中态实时取，避免闭包里缓存了 isSelected 在勾选变化后把背景刷回去。
			bool on = editing && selected.Contains(a.Path);
			SolidColorBrush background = (on ? SelectionFill : (over ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(24, byte.MaxValue, byte.MaxValue, byte.MaxValue)) : System.Windows.Media.Brushes.Transparent));
			border.Background = background;
			border.BorderBrush = (on ? SelectionEdge : System.Windows.Media.Brushes.Transparent);
			if (MotionEnabled)
			{
				CubicEase easingFunction2 = new CubicEase
				{
					EasingMode = EasingMode.EaseOut
				};
				scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(over ? (1.0 + 0.025 * MotionLevel) : 1.0, TimeSpan.FromMilliseconds(170.0))
				{
					EasingFunction = easingFunction2
				});
				scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(over ? (1.0 + 0.025 * MotionLevel) : 1.0, TimeSpan.FromMilliseconds(170.0))
				{
					EasingFunction = easingFunction2
				});
				lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(over ? (-3.0 * MotionLevel) : 0.0, TimeSpan.FromMilliseconds(170.0))
				{
					EasingFunction = easingFunction2
				});
			}
		};
		tile.MouseEnter += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (!activeDrag)
			{
				hover(obj: true);
				QueueMediaPreview(a, tile);
			}
		};
		tile.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			hover(obj: false);
			OnMediaTileLeave();
		};
		if (animate && MotionEnabled)
		{
			CubicEase easingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			};
			// 错峰间隔与时长都收紧：动画整体窗口从约 420ms 降到约 250ms，卡顿持续感明显减轻。
			TimeSpan value = TimeSpan.FromMilliseconds(Math.Min(index, 10) * 8);
			tile.Opacity = 0.0;
			tile.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150.0))
			{
				BeginTime = value,
				EasingFunction = easingFunction
			});
			lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9.0 * MotionLevel, 0.0, TimeSpan.FromMilliseconds(170.0))
			{
				BeginTime = value,
				EasingFunction = easingFunction
			});
		}
		tile.MouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			dragStart = e.GetPosition(this);
			dragEntry = a;
		};
		tile.MouseMove += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (e.LeftButton == MouseButtonState.Pressed && dragEntry == a)
			{
				System.Windows.Point position = e.GetPosition(this);
				if (Math.Abs(position.X - dragStart.X) + Math.Abs(position.Y - dragStart.Y) > 10.0)
				{
					dragEntry = null;
					StartEntryDrag(tile, a);
				}
			}
		};
		tile.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			if (dragEntry == a)
			{
				dragEntry = null;
				CloseMediaPreview();
				if (editing)
				{
					if (!selected.Remove(a.Path))
					{
						selected.Add(a.Path);
					}
					SyncSelectionVisuals();
					UpdateSelectionBar();
					e.Handled = true;
				}
				else
				{
					C.Launch(a);
				}
			}
		};
		tile.DragOver += (object s, System.Windows.DragEventArgs e) =>
		{
			if (RecycleBin.IsEntry(a))
			{
				if (DraggedEntries(e.Data).Length != 0 || e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
				{
					e.Effects = System.Windows.DragDropEffects.Move;
					e.Handled = true;
				}
			}
			else if (DraggedEntries(e.Data).Length != 0)
			{
				// 落点必须基于「拖动开始时」的原始顺序计算。
				// 若用 Tiles.Children.IndexOf(tile)，这个集合已被上一次 ApplyLivePreview 重排过，
				// 连续 DragOver 会以漂移后的顺序反复重算，导致落点比指针实际指向的位置偏一格。
				int num = BaseIndexOf(tile);
				if (e.GetPosition(tile).X > tile.ActualWidth / 2.0)
				{
					num++;
				}
				if (num != liveIndex)
				{
					liveIndex = num;
					ApplyLivePreview(DraggedEntries(e.Data), num);
				}
				e.Effects = System.Windows.DragDropEffects.Move;
				e.Handled = true;
			}
		};
		tile.Drop += (object s, System.Windows.DragEventArgs e) =>
		{
			if (RecycleBin.IsEntry(a))
			{
				e.Handled = true;
				if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
				{
					C.DeleteToRecycleBin((string[])e.Data.GetData(System.Windows.DataFormats.FileDrop));
				}
				else
				{
					string[] array = DraggedEntries(e.Data);
					if (array.Length != 0 && !array.Any((string p) => RecycleBin.IsPath(p)))
					{
						string[] paths = (from p in array
							let a = C.Prefs.Apps.FirstOrDefault((Entry x) => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))
							where a != null && !string.IsNullOrEmpty(a.Path) && !a.Path.Contains("://") && !a.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !a.Path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)
							select a.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
						C.DeleteToRecycleBin(paths);
					}
				}
			}
			else if (DraggedEntries(e.Data).Length != 0)
			{
				HideInsertMarker();
				if (!CommitReorder(CurrentPageSequence()))
				{
					Render();
				}
				e.Handled = true;
			}
		};
		return tile;
	}

	// 提交图标拖动排序：全部分类按页切片写回全局顺序。
	// 具体分类下只重排该分类内的相对顺序（Apps 里同页图标并不连续）。
	private bool CommitReorder(string[] sequence)
	{
		if (string.IsNullOrEmpty(activeCategory) || string.Equals(activeCategory, "all", StringComparison.OrdinalIgnoreCase))
		{
			return C.ReorderToSequence(sequence, page * capacity);
		}
		return C.ReorderCategorySequence(activeCategory, sequence);
	}

	private string[] DraggedEntries(System.Windows.IDataObject data)
	{
		if (data.GetDataPresent("IconBox.Entries"))
		{
			return (string[])data.GetData("IconBox.Entries");
		}
		if (data.GetDataPresent("IconBox.Entry"))
		{
			return new string[1] { (string)data.GetData("IconBox.Entry") };
		}
		return new string[0];
	}

	private int InsertIndexFor(System.Windows.Point p)
	{
		if (columns <= 0 || Tiles.ActualWidth <= 0.0)
		{
			return Tiles.Children.Count;
		}
		double num = Tiles.ActualWidth / (double)columns;
		double num2 = ((Tiles.Rows > 0 && Tiles.ActualHeight > 0.0) ? (Tiles.ActualHeight / (double)Tiles.Rows) : 96.0);
		int num3 = Math.Max(0, Math.Min((int)(p.X / num), columns - 1));
		return Math.Min(Math.Max(0, Math.Min((int)(p.Y / num2), Math.Max(0, Tiles.Rows - 1))) * columns + num3, Tiles.Children.Count);
	}

	private void ShowInsertMarker(int idx)
	{
		if (Tiles == null || columns <= 0 || Tiles.ActualWidth <= 0.0)
		{
			HideInsertMarker();
			return;
		}
		int count = Tiles.Children.Count;
		int num = Math.Min(idx, count);
		double num2 = Tiles.ActualWidth / (double)columns;
		double num3 = ((Tiles.Rows > 0 && Tiles.ActualHeight > 0.0) ? (Tiles.ActualHeight / (double)Tiles.Rows) : 96.0);
		int num4 = num % columns;
		int num5 = num / columns;
		double x = (double)num4 * num2 + ((num >= count) ? num2 : 0.0) - 1.5;
		TranslateTransform translateTransform = (TranslateTransform)insertMarker.RenderTransform;
		translateTransform.X = x;
		translateTransform.Y = (double)num5 * num3 + 6.0;
		insertMarker.Height = Math.Max(24.0, num3 - 12.0);
		insertMarker.Visibility = Visibility.Visible;
		insertIndex = idx;
	}

	private void HideInsertMarker()
	{
		if (insertMarker != null)
		{
			insertMarker.Visibility = Visibility.Collapsed;
		}
		insertIndex = -1;
		liveIndex = -1;
		livePreview = false;
		dragBaseOrder = null;
	}

	// 落点基准：拖动开始后第一次用到时冻结当时的磁贴顺序，后续 DragOver 都基于它。
	private int BaseIndexOf(Border tile)
	{
		if (Tiles == null)
		{
			return 0;
		}
		if (dragBaseOrder == null)
		{
			dragBaseOrder = Tiles.Children.OfType<Border>().ToList();
		}
		int num = dragBaseOrder.IndexOf(tile);
		return ((num >= 0) ? num : Math.Max(0, Tiles.Children.IndexOf(tile)));
	}

	private string[] CurrentPageSequence()
	{
		List<string> list = new List<string>();
		foreach (Border item in Tiles.Children.OfType<Border>())
		{
			if (item.Tag is Entry entry)
			{
				list.Add(entry.Path);
			}
		}
		return list.ToArray();
	}

	private void ApplyLivePreview(string[] paths, int idx)
	{
		if (Tiles == null)
		{
			return;
		}
		// list 是「当前实际排列」，list4 是「拖动开始时的排列」。
		// 落点 idx 是基于 list4 算出来的，所以插入位置也要放到 list4 的坐标系里算。
		// 再用它去重排当前的 list（两者元素集合相同，变的是顺序不同）。
		List<Border> list = Tiles.Children.OfType<Border>().ToList();
		if (list.Count == 0)
		{
			return;
		}
		List<Border> list4 = ((dragBaseOrder != null) ? dragBaseOrder : list);
		if (!list4.Contains(list[0]) || list4.Count != list.Count)
		{
			list4 = list;
		}
		int num = Math.Max(1, Tiles.Columns);
		int num2 = Math.Max(1, Tiles.Rows);
		double num3 = ((Tiles.ActualWidth > 0.0) ? Tiles.ActualWidth : Width) / (double)num;
		double num4 = ((Tiles.ActualHeight > 0.0) ? Tiles.ActualHeight : Height) / (double)num2;
		Dictionary<Border, int> dictionary = new Dictionary<Border, int>();
		for (int i = 0; i < list.Count; i++)
		{
			dictionary[list[i]] = i;
		}
		List<Border> list2 = new List<Border>();
		foreach (string p in paths)
		{
			Border border = list.FirstOrDefault((Border b) => b.Tag is Entry entry && string.Equals(entry.Path, p, StringComparison.OrdinalIgnoreCase));
			if (border != null && !list2.Contains(border))
			{
				list2.Add(border);
			}
		}
		List<Border> list3 = new List<Border>(list.Except(list2).ToList());
		// idx 是在「含被拖项」的原始排列 list4 里算出来的落点；list3 已剔除被拖项。
		// 若被拖项在 list4 中原本位于落点之前，插入下标要减 1，否则整体后移一格。
		int shift = 0;
		for (int s = 0; s < list4.Count && s < idx; s++)
		{
			if (list2.Contains(list4[s]))
			{
				shift++;
			}
		}
		int num5 = Math.Max(0, Math.Min(idx - shift, list3.Count));
		for (int num6 = 0; num6 < list2.Count; num6++)
		{
			list3.Insert(num5 + num6, list2[num6]);
		}
		for (int num7 = 0; num7 < list3.Count; num7++)
		{
			if (Tiles.Children.IndexOf(list3[num7]) != num7)
			{
				Tiles.Children.Remove(list3[num7]);
				Tiles.Children.Insert(num7, list3[num7]);
			}
		}
		CubicEase easingFunction = new CubicEase
		{
			EasingMode = EasingMode.EaseOut
		};
		foreach (Border item in list3)
		{
			int num8 = dictionary[item];
			int num9 = list3.IndexOf(item);
			double num10 = ((double)(num8 % num) - (double)(num9 % num)) * num3;
			double num11 = ((double)(num8 / num) - (double)(num9 / num)) * num4;
			if (item.Child is FrameworkElement frameworkElement)
			{
				TranslateTransform translateTransform = frameworkElement.RenderTransform as TranslateTransform;
				if (translateTransform == null || translateTransform.IsFrozen)
				{
					translateTransform = (TranslateTransform)(frameworkElement.RenderTransform = new TranslateTransform());
				}
				translateTransform.X += num10;
				translateTransform.Y += num11;
				if (Math.Abs(translateTransform.X) > 0.5 || Math.Abs(translateTransform.Y) > 0.5)
				{
					translateTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120.0))
					{
						EasingFunction = easingFunction
					});
					translateTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120.0))
					{
						EasingFunction = easingFunction
					});
				}
			}
			item.Opacity = (list2.Contains(item) ? 0.3 : 1.0);
		}
		ShowInsertMarker(num5);
		livePreview = true;
	}

	private void ExternalDrop(object s, System.Windows.DragEventArgs e)
	{
		if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
		{
			C.Add((string[])e.Data.GetData(System.Windows.DataFormats.FileDrop));
			ClearSearch();
			e.Handled = true;
		}
	}

	public void UpdateRunning()
	{
		if (Tiles == null || !IsVisible)
		{
			return;
		}
		foreach (Border item in Tiles.Children.OfType<Border>())
		{
			Entry entry = item.Tag as Entry;
			Grid grid = item.Child as Grid;
			if (entry == null || grid == null)
			{
				continue;
			}
			foreach (Border item2 in from b in grid.Children.OfType<Border>()
				where b.Tag as string == "running"
				select b)
			{
				item2.Visibility = ((!C.Prefs.Dock.ShowRunning || !RunningWindows.IsRunning(entry)) ? Visibility.Collapsed : Visibility.Visible);
			}
		}
	}

	private void InitDragExperience()
	{
		StackPanel stackPanel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		dragPreviewIcon = new System.Windows.Controls.Image
		{
			Width = 28.0,
			Height = 28.0,
			Stretch = Stretch.Uniform,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		};
		dragPreviewText = new TextBlock
		{
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			MaxWidth = 230.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		stackPanel.Children.Add(dragPreviewIcon);
		stackPanel.Children.Add(dragPreviewText);
		dragPreview = new Border
		{
			Child = stackPanel,
			Padding = new Thickness(12.0, 9.0, 12.0, 9.0),
			CornerRadius = new CornerRadius(14.0),
			Background = Polish.Color("#E52A3447"),
			BorderBrush = Polish.Color("#80B8CEEF"),
			BorderThickness = new Thickness(1.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			IsHitTestVisible = false,
			Visibility = Visibility.Collapsed
		};
		Root.Children.Add(dragPreview);
		PreviewDragOver += (object s, System.Windows.DragEventArgs e) =>
		{
			string[] array = DraggedEntries(e.Data);
			bool dataPresent = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
			if ((array.Length != 0) | dataPresent)
			{
				if (!activeDrag)
				{
					activeDrag = true;
					UpdateCategoryChips();
				}
				System.Windows.Point position = e.GetPosition(Root);
				if (dragPreviewText.Text.Length == 0)
				{
					if (dataPresent)
					{
						string[] array2 = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
						dragPreviewText.Text = ((array2.Length == 1) ? System.IO.Path.GetFileName(array2[0]) : ("共 " + array2.Length + " 个项目"));
					}
					else
					{
						dragPreviewText.Text = "正在移动图标";
					}
				}
				dragPreview.RenderTransform = new TranslateTransform(position.X + 16.0, position.Y + 16.0);
				dragPreview.Visibility = Visibility.Visible;
			}
		};
		PreviewDragLeave += (object s, System.Windows.DragEventArgs e) =>
		{
			System.Windows.Point position = e.GetPosition(Root);
			if (position.X < 0.0 || position.Y < 0.0 || position.X > ActualWidth || position.Y > ActualHeight)
			{
				EndDragPreview();
				Render();
			}
		};
		PreviewDrop += (object s, System.Windows.DragEventArgs e) =>
		{
			EndDragPreview();
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (!activeDrag && IsVisible)
				{
					Render();
				}
			}));
		};
	}

	private void EndDragPreview()
	{
		activeDrag = false;
		dragPreview.Visibility = Visibility.Collapsed;
		dragPreviewText.Text = "";
		dragPreviewIcon.Source = null;
	}

	private void StartEntryDrag(FrameworkElement tile, Entry entry)
	{
		string[] array = ((editing && selected.Contains(entry.Path)) ? selected.ToArray() : new string[1] { entry.Path });
		System.Windows.DataObject dataObject = new System.Windows.DataObject();
		dataObject.SetData("IconBox.Entry", entry.Path);
		dataObject.SetData("IconBox.Entries", array);
		dragPreviewText.Text = ((array.Length == 1) ? entry.Name : (entry.Name + " 等 " + array.Length + " 项"));
		try
		{
			dragPreviewIcon.Source = FeatureUi.Icon(entry, C.Prefs.Dock.ShowShortcutOverlays, sharper: true);
		}
		catch
		{
			dragPreviewIcon.Source = null;
		}
		bool modalBusy = ModalBusy;
		ModalBusy = true;
		activeDrag = true;
		C.Dock.BeginBoardPinDrag();
		try
		{
			DragDrop.DoDragDrop(tile, dataObject, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
		}
		finally
		{
			C.Dock.EndBoardPinDrag();
			EndDragPreview();
			ModalBusy = modalBusy;
			Render();
		}
	}

	public void ApplyDisplayOptions()
	{
		if (C.Prefs.BoardHideSearch && Search != null)
		{
			Search.Clear();
			if (searchPopup != null)
			{
				searchPopup.IsOpen = false;
			}
		}
		if (searchHost != null)
		{
			searchHost.Visibility = (C.Prefs.BoardHideSearch ? Visibility.Collapsed : Visibility.Visible);
		}
		if (IsVisible)
		{
			BuildForSize();
			QueueSizeRefresh();
		}
	}

	private void MoreMenu()
	{
		System.Windows.Controls.ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.BoardMaterial.Tint, (int)(C.Prefs.OpacityPercent ?? 72.0));
		iconMenu = menu;
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			if (iconMenu == menu)
			{
				iconMenu = null;
			}
		};
		Action<string, Action, bool> action = (string label, Action action2, bool enabled) =>
		{
			System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
			{
				Header = label,
				IsEnabled = enabled
			};
			menuItem.Click += (object s, RoutedEventArgs e) =>
			{
				bool modalBusy = ModalBusy;
				ModalBusy = true;
				try
				{
					menu.IsOpen = false;
					action2();
				}
				finally
				{
					ModalBusy = modalBusy;
				}
			};
			menu.Items.Add(menuItem);
		};
		action("撤销 · Ctrl+Z", () =>
		{
			C.UndoBoard();
		}, C.CanUndoRemoval);
		action("重做 · Ctrl+Y", () =>
		{
			C.RedoBoard();
		}, C.CanRedoBoard);
		menu.Items.Add(new Separator());
		action("整理图标（删除重复）", () =>
		{
			C.OrganizeIcons();
		}, arg3: true);
		action("图标间距与默认大小", () =>
		{
			IconSettings();
		}, arg3: true);
		action("工作区", () =>
		{
			C.ManageWorkspaces();
		}, arg3: true);
		action("外观方案", () =>
		{
			C.ManageSchemes();
		}, arg3: true);
		menu.Items.Add(new Separator());
		action("导出完整备份", () =>
		{
			C.ExportFullBackup();
		}, arg3: true);
		action("恢复完整备份", () =>
		{
			C.RestoreFullBackup();
		}, arg3: true);
		menu.PlacementTarget = this;
		menu.IsOpen = true;
	}

	private void InitResizeRefresh()
	{
		sizeRefresh.Tick += (object s, EventArgs e) =>
		{
			sizeRefresh.Stop();
			ApplySizeRefresh();
		};
	}

	private void QueueSizeRefresh()
	{
		if (Tiles != null && !resizingPanel && !refreshingSize && (IsVisible || preparingView) && !sizeRefresh.IsEnabled)
		{
			sizeRefresh.Start();
		}
	}

	private void ApplySizeRefresh()
	{
		if (Tiles == null || resizingPanel || refreshingSize || (!IsVisible && !preparingView))
		{
			return;
		}
		refreshingSize = true;
		try
		{
			int num = BoardIconRules.Columns((Tiles.ActualWidth > 0.0) ? Tiles.ActualWidth : (Width - 104.0), C.Prefs.BoardIconSize, C.Prefs.BoardIconGap);
			int num2 = BoardIconRules.Rows((Tiles.ActualHeight > 0.0) ? Tiles.ActualHeight : (Height - 180.0), C.Prefs.BoardIconSize, C.Prefs.BoardIconGap);
			if (iconsDirty || columns != num || Tiles.Rows != num2)
			{
				iconsDirty = false;
				columns = num;
				capacity = num * num2;
				Tiles.Columns = num;
				Tiles.Rows = num2;
				Render();
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			SetStatus("面板刷新未完成，请重新打开面板");
		}
		finally
		{
			refreshingSize = false;
		}
	}

	internal void CheckResizeRefresh()
	{
		Open();
		string text = Search.Text;
		System.Windows.Controls.TextBox search = Search;
		UniformGrid tiles = Tiles;
		double[] array = new double[4] { 900.0, 1200.0, 920.0, 1260.0 };
		foreach (double width in array)
		{
			Width = width;
			Height = 620.0;
			UpdateLayout();
			QueueSizeRefresh();
			ApplySizeRefresh();
			UpdateLayout();
		}
		sizeRefresh.Stop();
		if (search != Search || tiles != Tiles || Search.Text != text)
		{
			throw new Exception("缩放不应重建搜索框或图标宿主");
		}
		Hide();
	}
}
