param([int]$ProcId,[string]$Out,[string]$Class='TWizardForm')
$code=@"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public class WCap {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public struct RECT { public int L,T,R,B; }
  public static IntPtr Find(uint pid, string cls) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      var sb = new StringBuilder(256); GetClassName(h, sb, 256);
      if (sb.ToString() == cls && IsWindowVisible(h)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static bool Snap(IntPtr h, string path) {
    RECT r; if (!GetWindowRect(h, out r)) return false;
    int w=r.R-r.L, ht=r.B-r.T; if (w<=0||ht<=0) return false;
    using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc(); bool ok = PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc);
      if (!ok) return false;
      bmp.Save(path, ImageFormat.Png);
    }
    return true;
  }
}
"@
$dpi = Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();' -Name Dpi -Namespace WD -PassThru
[void]$dpi::SetProcessDPIAware()
if (-not ('WCap' -as [type])) { Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing,System.Windows.Forms }
$h=[WCap]::Find([uint32]$ProcId,$Class)
if ($h -eq [IntPtr]::Zero) { Write-Output 'NO_WINDOW'; exit 1 }
Write-Output ('snap=' + [WCap]::Snap($h,$Out))