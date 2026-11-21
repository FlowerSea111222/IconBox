using System;
using System.Windows;
using System.Windows.Media;

namespace IconBox;

/// <summary>
/// 苹果风格的玻璃边缘与光泽参数。
///
/// Liquid Glass 的关键观感不是「加一层白边」：真实的玻璃边缘会折射环境光，
/// 迎光的那一侧（左上）最亮，背光侧（右下）只剩很淡的一道反射，中间过渡柔和。
/// 这里把原来「上下方向 145→35 的单向渐变」换成沿对角线的方向性镜面边，
/// 并给表面加一层很薄的顶部提亮，让玻璃看起来有厚度而不是一块半透明的塑料。
///
/// 注意：本类只负责「边缘与光泽」的取值，玻璃填充色仍由 <see cref="GlassMaterials"/> 决定，
/// 亮度与模糊的开关仍受材质设置和「减弱动态效果 / 高对比度」约束。
/// </summary>
public static class AppleGlass
{
	/// <summary>方向性镜面边缘：左上亮、右下暗，沿对角线过渡。</summary>
	public static Brush Rim(bool light, double strength)
	{
		double s = Max(0.0, Min(2.0, strength));
		if (s <= 0.01)
		{
			return Brushes.Transparent;
		}
		// 亮面玻璃（浅色主题）本身背景很亮，边缘再压白会「糊掉」，
		// 所以浅色用更低的透明度，并把右下那道反射压得更薄，形成一条细硬的玻璃口。
		double topAlpha = light ? 0.50 : 0.62;
		double midAlpha = light ? 0.05 : 0.09;
		double lowAlpha = light ? 0.14 : 0.22;
		double bottomAlpha = light ? 0.30 : 0.40;
		LinearGradientBrush brush = new LinearGradientBrush
		{
			StartPoint = new Point(0.0, 0.0),
			EndPoint = new Point(1.0, 1.0),
			MappingMode = BrushMappingMode.RelativeToBoundingBox
		};
		brush.GradientStops.Add(new GradientStop(Silver(topAlpha, s), 0.0));
		brush.GradientStops.Add(new GradientStop(Silver(midAlpha, s), 0.42));
		brush.GradientStops.Add(new GradientStop(Silver(lowAlpha, s), 0.74));
		brush.GradientStops.Add(new GradientStop(Silver(bottomAlpha, s), 1.0));
		brush.Freeze();
		return brush;
	}

	/// <summary>表面顶部的一层薄提亮（模拟玻璃厚度带来的透光），越往下越透明。</summary>
	public static Brush Sheen(bool light)
	{
		LinearGradientBrush brush = new LinearGradientBrush
		{
			StartPoint = new Point(0.5, 0.0),
			EndPoint = new Point(0.5, 1.0),
			MappingMode = BrushMappingMode.RelativeToBoundingBox
		};
		brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(light ? 110 : 76), 255, 255, 255), 0.0));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(light ? 34 : 26), 255, 255, 255), 0.34));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.62));
		brush.Freeze();
		return brush;
	}

	/// <summary>
	/// 磨砂玻璃的最小模糊半径：低于这个值就没有「材质感」，只剩半透明色块。
	/// 取 18 与默认「玻璃模糊 = 18」一致，用户把它调低时仍然保留苹果式的磨砂质感。
	/// </summary>
	public const double MinBlur = 18.0;

	/// <summary>按下时的缩放（苹果的即时反馈是手指压下去的瞬间缩小，不等 click）。</summary>
	public const double PressScale = 0.955;

	/// <summary>按下缩放的时长，毫秒。</summary>
	public const double PressInMs = 90.0;

	/// <summary>抬起回弹的时长，毫秒（带一点回弹曲线）。</summary>
	public const double PressOutMs = 210.0;

	private static Color Silver(double alpha, double strength)
	{
		double value = Min(1.0, alpha * strength);
		return Color.FromArgb((byte)Math.Round(value * 255.0), 255, 255, 255);
	}

	private static double Min(double a, double b)
	{
		return (a < b) ? a : b;
	}

	private static double Max(double a, double b)
	{
		return (a > b) ? a : b;
	}
}
