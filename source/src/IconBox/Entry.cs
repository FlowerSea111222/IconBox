using System.Collections.Generic;
using System.Runtime.Serialization;

namespace IconBox;

[DataContract]
public class Entry
{
	[DataMember]
	public string Name;

	[DataMember]
	public string Path;

	[DataMember]
	public bool Pinned;

	[DataMember(EmitDefaultValue = false)]
	public bool IsFolder;

	[DataMember(EmitDefaultValue = false)]
	public string IconPath;

	[DataMember(EmitDefaultValue = false)]
	public string LaunchTarget;

	[DataMember(EmitDefaultValue = false)]
	public string LaunchArguments;

	[DataMember(EmitDefaultValue = false)]
	public string ProductInfo;

	[DataMember(EmitDefaultValue = false)]
	public string WindowTarget;

	// 应用面板分类：手动归类结果（分类 id，见 AppCategoryRules）。为空表示自动推断。
	[DataMember(EmitDefaultValue = false)]
	public string Category;
}
