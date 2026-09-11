using Godot;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;

// 泰伦藻类工厂：显示当前驻扎工人数量（alive / 上限），样式与怨灵计数一致
public partial class GarrisonCountLabel3D : StructureCountLabel3D
{
	protected override string LabelNodeName => "GarrisonCountLabel";
	protected override Color LabelColor => new Color(0.6f, 1f, 0.7f, 1f);
	protected override string CountKey => "hud.garrison_count";

	protected override bool TryGetCount(Structure st, SimStructure sim, SimWorld world, out int alive, out int max)
	{
		var cfg = ConfigDatabase.GetStructure(st.StructureName);
		if (cfg == null || cfg.GarrisonCapacity <= 0)
		{
			alive = 0;
			max = 0;
			return false;
		}
		alive = sim.GarrisonedCount;
		max = cfg.GarrisonCapacity;
		return true;
	}
}
