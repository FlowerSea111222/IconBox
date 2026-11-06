using System.Collections.Generic;

namespace IconBox;

public sealed class DockPinResult
{
	public int Added;

	public int Skipped;

	public List<string> Paths = new List<string>();
}
