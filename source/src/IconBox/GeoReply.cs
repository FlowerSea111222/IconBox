using System.Collections.Generic;
using System.Runtime.Serialization;

namespace IconBox;

[DataContract]
public class GeoReply
{
	[DataMember(Name = "results")]
	public List<GeoPlace> Results;
}
