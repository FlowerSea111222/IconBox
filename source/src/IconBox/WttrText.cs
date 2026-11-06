using System.Runtime.Serialization;

namespace IconBox;

[DataContract]
public class WttrText
{
	[DataMember(Name = "value")]
	public string Value;
}
