using System;

namespace IconBox;

public static class PersonalizationChecks
{
	public static void Run()
	{
		Controller controller = new Controller(desktop: false);
		try
		{
			controller.Prefs.Dock.MotionEnabled = false;
			for (int i = 0; i < 6; i++)
			{
				controller.Prefs.Apps.Add(new Entry
				{
					Name = "center" + i,
					Path = "center" + i,
					Pinned = true
				});
			}
			controller.Prefs.Normalize();
			controller.Dock.Refresh(capture: false);
			double num = controller.Dock.Left + controller.Dock.Width / 2.0;
			controller.Prefs.Placement.DockLeft = controller.Dock.Left;
			controller.Prefs.Placement.DockCenter = num;
			controller.Prefs.Apps[0].Pinned = false;
			controller.Dock.Refresh(capture: false);
			if (Math.Abs(controller.Dock.Left + controller.Dock.Width / 2.0 - num) > 0.01)
			{
				throw new Exception("Dock 内容变动后中心偏移");
			}
		}
		finally
		{
			controller.ShuttingDown = true;
			controller.Dock.ReleaseHotkey();
			controller.Board.Close();
			controller.Dock.Close();
		}
	}
}
