using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IconBox;

// 分类标签（底部胶囊）里那七个小图标。
//
// 为什么单独一套（用户 2026-11-20 批注：「图标风格尽量简约一点……至少线条粗细要一样」）：
// 原来用的是 Polish.Icon —— 它对每个图标都用 Stretch.Uniform 把「各自包围盒」拉到同一个像素尺寸，
// 于是包围盒大的图形被压得多、小的被拉得少，缩放比不同 → 线宽各不相同；再加上 note/music/wrench
// 这些图形本身细节多，缩到 15px 就糊成一团。
// 这里的做法：所有图形画在**同一个 24×24 栅格**上、用**同一个线宽**，再整体等比缩放到目标尺寸，
// 因此线宽、圆角、光学大小自然一致；图形也只保留最必需的 2–3 个元素。
internal static class CategoryGlyphs
{
	private const double Grid = 24.0;

	// 24 栅格下的线宽；缩到 16px 时约 1.2 物理像素，与 13px 文字的字重相称。
	private const double Stroke = 2.0;

	internal static FrameworkElement ForId(string id, Brush color, double size)
	{
		string data = DataFor(id);
		if (data == null)
		{
			return null;
		}
		Path path = new Path
		{
			Data = Geometry.Parse(data),
			Stroke = color,
			StrokeThickness = Stroke,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			// 关键：不做 Stretch。栅格坐标系原样保留，外层 Viewbox 只做一次统一缩放。
			Stretch = Stretch.None,
			Width = Grid,
			Height = Grid,
			SnapsToDevicePixels = true
		};
		return new Viewbox
		{
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform,
			Child = path,
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	private static string DataFor(string id)
	{
		if (string.Equals(id, AppCategoryRules.Office, System.StringComparison.OrdinalIgnoreCase))
		{
			// 文档：一张圆角纸 + 两条文字线（去掉折角，15px 下折角只会变成一个小疙瘩）
			return "M7,4.2 H17 A2.2,2.2 0 0 1 19.2,6.4 V17.6 A2.2,2.2 0 0 1 17,19.8 H7 A2.2,2.2 0 0 1 4.8,17.6 V6.4 A2.2,2.2 0 0 1 7,4.2 Z"
				+ " M8.6,9.8 H15.4 M8.6,13.8 H13";
		}
		if (string.Equals(id, AppCategoryRules.Media, System.StringComparison.OrdinalIgnoreCase))
		{
			// 音符：一个符头 + 符干 + 一句符尾（原来是双符头 + 横梁，缩小后是一团黑）
			return "M12.2,16.9 A2.5,2.5 0 1 1 7.2,16.9 A2.5,2.5 0 1 1 12.2,16.9 Z"
				+ " M12.2,16.9 V5.4 L17.6,6.8 V10.4";
		}
		if (string.Equals(id, AppCategoryRules.Dev, System.StringComparison.OrdinalIgnoreCase))
		{
			// AI：四角星（比「两栏窗口」更能一眼认出是 AI 分类）；腰部加宽，缩到 16px 才不至于只剩四条细线
			return "M12,3.4 L14.5,9.5 L20.6,12 L14.5,14.5 L12,20.6 L9.5,14.5 L3.4,12 L9.5,9.5 Z";
		}
		if (string.Equals(id, AppCategoryRules.Net, System.StringComparison.OrdinalIgnoreCase))
		{
			// 网络：三格信号（原来用两道 wifi 弧，24 栅格缩到 16px 后两弧几乎粘在一起，只剩一团）
			return "M6.6,18.6 V15.2 M12,18.6 V11.4 M17.4,18.6 V7.4";
		}
		if (string.Equals(id, AppCategoryRules.Tool, System.StringComparison.OrdinalIgnoreCase))
		{
			// 工具箱：一个箱体 + 提手（去掉锁扣等小零件）
			return "M6.6,9.2 H17.4 A2.2,2.2 0 0 1 19.6,11.4 V17.4 A2.2,2.2 0 0 1 17.4,19.6 H6.6 A2.2,2.2 0 0 1 4.4,17.4 V11.4 A2.2,2.2 0 0 1 6.6,9.2 Z"
				+ " M9.2,9.2 V7.4 A1.6,1.6 0 0 1 10.8,5.8 H13.2 A1.6,1.6 0 0 1 14.8,7.4 V9.2";
		}
		if (string.Equals(id, AppCategoryRules.System, System.StringComparison.OrdinalIgnoreCase))
		{
			// 系统：两条轨道 + 两个拨杆（用直线代替圆钮——小尺寸下圆钮一定会结成实心点）
			return "M4.4,8.4 H19.6 M4.4,15.6 H19.6 M9.2,5.6 V11.2 M14.8,12.8 V18.4";
		}
		if (string.Equals(id, AppCategoryRules.File, System.StringComparison.OrdinalIgnoreCase))
		{
			// 文件：文件夹（原来是一张纸，与「办公」的纸张重复，且线宽是另一套）
			return "M4.6,8.2 A1.8,1.8 0 0 1 6.4,6.4 H10.2 L12.4,8.6 H17.6 A1.8,1.8 0 0 1 19.4,10.4 V16.8 A1.8,1.8 0 0 1 17.6,18.6 H6.4 A1.8,1.8 0 0 1 4.6,16.8 Z";
		}
		return null;
	}
}
