Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class OV {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Dock() {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      StringBuilder sb = new StringBuilder(300); GetWindowText(h, sb, 300);
      if (sb.ToString() == "IconBox Dock") { found = h; return false; }
      return true; }, IntPtr.Zero);
    return found;
  }
}
"@
[OV]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$dock = [OV]::Dock()
if ($dock -eq [IntPtr]::Zero) { throw 'dock not found' }
$el = [System.Windows.Automation.AutomationElement]::FromHandle($dock)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
"buttons=" + $all.Count
$target = $null
foreach ($b in $all) {
	if ($b.Current.Name -eq '控制中心') { $target = $b; break }
}
if ($target -eq $null) { throw 'control center button not found' }
$p = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$p.Invoke()
Start-Sleep -Milliseconds 1200
[OV]::SetCursorPos(1360, 950) | Out-Null
Start-Sleep -Milliseconds 500
"cursor placed inside overlay"
