using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace IconBox;

[DataContract]
public class Preferences
{
	[DataMember]
	public List<Entry> Apps = new List<Entry>();

	[DataMember(EmitDefaultValue = false)]
	public List<string> DockOrder = new List<string>();

	[DataMember]
	public int FormatVersion;

	[DataMember]
	public bool Light;

	[DataMember]
	public double DockGap = 10.0;

	[DataMember]
	public bool Imported;

	[DataMember]
	public int PanelSize = 1;

	[DataMember]
	public double BoardIconSize = 48.0;

	[DataMember]
	public double BoardIconGap = 5.0;

	[DataMember(EmitDefaultValue = false)]
	public double? OpacityPercent = 72.0;

	[DataMember(EmitDefaultValue = false)]
	public List<string> Removed = new List<string>();

	[DataMember(EmitDefaultValue = false)]
	public List<HotkeySetting> Hotkeys = HotkeyRules.Defaults();

	// 桌面开关的期望状态：开机启动后自动应用，无需每次手动再开
	[DataMember]
	public bool HideTaskbar;

	[DataMember]
	public bool HideDesktopIcons;

	[DataMember]
	public bool AutoHideTaskbar;

	// 自动清理桌面：隐藏「已归类到面板」的桌面图标（不删除文件，只设为隐藏属性）。
	[DataMember]
	public bool AutoCleanDesktopIcons;

	// 是否连「文档 / 文件夹」也从桌面收起。默认只收起快捷方式：
	// 文件被标成「隐藏 + 系统」后，浏览器的上传对话框默认看不到它们（问题 2）。
	public bool AutoCleanFiles;

	// 应用面板分类栏的顺序 / 名称 / 增删（null = 使用内置默认分类）。
	[DataMember(EmitDefaultValue = false)]
	public List<CategoryDef> Categories;

	// 分类结构版本：用于给老配置补上后来新增的内置分类（1 = 已含「文件」分类）。
	[DataMember(EmitDefaultValue = false)]
	public int CategorySchema;

	[DataMember(EmitDefaultValue = false)]
	public DockOptions Dock = new DockOptions();

	[DataMember(EmitDefaultValue = false)]
	public PlacementOptions Placement = new PlacementOptions();

	[DataMember]
	public bool AutoCheckUpdates;

	// 桌面新增图标（快捷方式/程序）自动加入启动台
	[DataMember]
	public bool AutoDesktopImport = true;

	[DataMember]
	public bool UpdateDirectConnection;

	[DataMember]
	public bool BoardDismissOnDeactivate;

	[DataMember]
	public bool BoardHideSearch;

	[DataMember]
	public bool GameMode;

	[DataMember]
	public bool AutoGameMode;

	[DataMember]
	public MaterialOptions BoardMaterial = new MaterialOptions();
	[DataMember(EmitDefaultValue = false)]
	public GlobalStyleOptions GlobalStyle = new GlobalStyleOptions();

	[DataMember(EmitDefaultValue = false)]
	public ControlCenterOptions ControlCenter = new ControlCenterOptions();

	[DataMember(EmitDefaultValue = false)]
	public Dictionary<string, string> SystemIconGlyphs;

	[DataMember(EmitDefaultValue = false)]
	public Dictionary<string, string> SystemIconImages;

	[DataMember(EmitDefaultValue = false)]
	public List<string> RemovedIdentities = new List<string>();

	[DataMember]
	public long LastUpdateCheckUtc;

	[DataMember]
	public bool HotkeyMigrationConflict;

	// 图标风格迁移版本：1 = 已把旧的默认图标包（Lucide，index 0）切到苹果 SF Symbols（index 4）。
	[DataMember]
	public int IconSchema;

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		AutoDesktopImport = true;
		BoardIconSize = 48.0;
		BoardIconGap = 5.0;
	}

	public void Normalize()
	{
		// 分类列表：丢弃非法条目；null / 空列表保持 null（= 用内置默认分类）。
		if (Categories != null)
		{
			Categories = Categories.Where((CategoryDef x) => x != null && !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name)).ToList();
			foreach (CategoryDef category in Categories)
			{
				category.Id = category.Id.Trim();
				category.Name = category.Name.Trim();
				if (category.Name.Length > 12)
				{
					category.Name = category.Name.Substring(0, 12);
				}
			}
			// 去掉重复 id
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Categories = Categories.Where((CategoryDef x) => seen.Add(x.Id)).ToList();
			if (Categories.Count == 0)
			{
				Categories = null;
			}
		}
		// 分类结构迁移：老配置保存过分类列表但没有「文件」分类，这里补进去（插在「其他」前面）。
		// 只做一次，用 CategorySchema 标记，避免把用户主动删掉的内置分类又加回来。
		if (CategorySchema < 1)
		{
			CategorySchema = 1;
			if (Categories != null && !Categories.Any((CategoryDef x) => string.Equals(x.Id, AppCategoryRules.File, StringComparison.OrdinalIgnoreCase)))
			{
				CategoryDef fileDef = new CategoryDef
				{
					Id = AppCategoryRules.File,
					Name = "文件",
					Glyph = AppCategoryRules.GlyphFor(AppCategoryRules.File),
					Font = AppCategoryRules.FontFor(AppCategoryRules.File)
				};
				int otherIndex = Categories.FindIndex((CategoryDef x) => string.Equals(x.Id, AppCategoryRules.Other, StringComparison.OrdinalIgnoreCase));
				if (otherIndex >= 0)
				{
					Categories.Insert(otherIndex, fileDef);
				}
				else
				{
					Categories.Add(fileDef);
				}
			}
		}
		// 分类结构迁移 2：早期「文件」分类用的是 ▯（空心矩形，看起来像方块），改成文档图标。
		if (CategorySchema < 2)
		{
			CategorySchema = 2;
			if (Categories != null)
			{
				CategoryDef fileDef = Categories.FirstOrDefault((CategoryDef x) => string.Equals(x.Id, AppCategoryRules.File, StringComparison.OrdinalIgnoreCase));
				if (fileDef != null && string.IsNullOrWhiteSpace(fileDef.Font))
				{
					fileDef.Glyph = AppCategoryRules.GlyphFor(AppCategoryRules.File);
					fileDef.Font = AppCategoryRules.FontFor(AppCategoryRules.File);
				}
			}
		}
		// 图标风格迁移：新版本默认改用苹果 SF Symbols（index 4）。
		// 只迁移「仍是旧默认值 0」的配置，用户手动选过的其它风格保持不动。
		if (IconSchema < 1)
		{
			IconSchema = 1;
			if (Dock != null && Dock.IconPack == 0)
			{
				Dock.IconPack = 4;
			}
		}
		if (BoardMaterial == null)
		{
			BoardMaterial = new MaterialOptions();
		}
		BoardMaterial.Normalize();
		if (GlobalStyle == null)
		{
			GlobalStyle = new GlobalStyleOptions();
		}
		GlobalStyle.Normalize();
		if (ControlCenter == null)
		{
			ControlCenter = new ControlCenterOptions();
		}
		if (SystemIconGlyphs == null)
		{
			SystemIconGlyphs = new Dictionary<string, string>();
		}
		foreach (string key in SystemIconGlyphs.Keys.ToList())
		{
			if (!SystemIconActions.IsValid(SystemIconGlyphs[key]))
			{
				SystemIconGlyphs.Remove(key);
			}
		}
		if (SystemIconImages == null)
		{
			SystemIconImages = new Dictionary<string, string>();
		}
		foreach (string key in SystemIconImages.Keys.ToList())
		{
			if (string.IsNullOrWhiteSpace(SystemIconImages[key]) || !File.Exists(SystemIconImages[key]))
			{
				SystemIconImages.Remove(key);
			}
		}
		if (RemovedIdentities == null)
		{
			RemovedIdentities = new List<string>();
		}
		if (Apps == null)
		{
			Apps = new List<Entry>();
		}
		Apps = Apps.Where((Entry a) => a != null && !string.IsNullOrWhiteSpace(a.Path)).ToList();
		foreach (Entry app in Apps)
		{
			if (string.IsNullOrWhiteSpace(app.Name))
			{
				app.Name = Path.GetFileNameWithoutExtension(app.Path);
			}
		}
		Apps = EntryRules.NormalizeEntries(Apps);
		RemovalRules.ReconcileActive(this);
		DockOrderRules.Normalize(this);
		BoardIconSize = LayoutRules.Clamp(BoardIconSize, 28.0, 80.0, 48.0);
		BoardIconGap = LayoutRules.Clamp(BoardIconGap, 0.0, 20.0, 5.0);
		DockGap = LayoutRules.Clamp(DockGap, 0.0, 160.0, 10.0);
		PanelSize = Math.Max(0, Math.Min(2, PanelSize));
		OpacityPercent = LayoutRules.GlassOpacity(OpacityPercent ?? 72.0);
		if (Removed == null)
		{
			Removed = new List<string>();
		}
		bool flag = Hotkeys != null && Hotkeys.All((HotkeySetting k) => k == null || k.ActionId != 7);
		Hotkeys = HotkeyRules.Normalize(Hotkeys);
		HotkeyMigrationConflict = flag && !Hotkeys[6].Enabled;
		if (Dock == null)
		{
			Dock = new DockOptions
			{
				OpacityPercent = OpacityPercent.Value
			};
		}
		Dock.Normalize();
		DockLayoutRules.SyncApps(this);
		if (Placement == null)
		{
			Placement = new PlacementOptions();
		}
		Placement.Normalize();
	}
}
