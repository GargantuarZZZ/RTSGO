using Godot;
using RTS.Data;

// 实体通用配置基类（单位/建筑共用）：生命、护盾、视野、防御
[GlobalClass]
public partial class EntityConfig : Resource
{
	[ExportGroup("Vitals")]
	[Export(PropertyHint.Range, "1,999999,1")]
	public float MaxHp { get; set; } = 100f;

	[Export(PropertyHint.Range, "0,999999,1")]
	public float MaxShield { get; set; } = 0f;

	[Export(PropertyHint.Range, "0,999999,1")]
	public float VisionRange { get; set; } = 350f;

	[Export] public ArmorType ArmorType { get; set; } = ArmorType.Light;

	[ExportGroup("Defenses")]
	[Export(PropertyHint.Range, "0,999999,1")]
	public float DefKinetic { get; set; } = 0f;

	[Export(PropertyHint.Range, "0,999999,1")]
	public float DefThermal { get; set; } = 0f;

	[Export(PropertyHint.Range, "0,999999,1")]
	public float DefExplosive { get; set; } = 0f;

	[Export(PropertyHint.Range, "0,999999,1")]
	public float DefEM { get; set; } = 0f;

	[Export(PropertyHint.Range, "0,999999,1")]
	public float DefBeam { get; set; } = 0f;

	[ExportGroup("Cost")]
	[Export]
	public Godot.Collections.Dictionary<ResourceType, float> Costs { get; set; } = new();

	[Export(PropertyHint.Range, "0.1,9999,0.1")]
	public float BuildTime { get; set; } = 1f;

	public virtual bool IsValidConfig() => true;
}
