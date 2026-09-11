using Godot;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;

// 地狱城：显示当前绑定的怨灵数量（alive / 上限）
public partial class BoundCountLabel3D : StructureCountLabel3D
{
	protected override string LabelNodeName => "BoundCountLabel";
	protected override Color LabelColor => new Color(1f, 0.9f, 0.4f, 1f);
	protected override string CountKey => "hud.bound_count";

	protected override bool TryGetCount(Structure st, SimStructure sim, SimWorld world, out int alive, out int max)
	{
		var cfg = ConfigDatabase.GetStructure(st.StructureName);
		if (cfg == null || cfg.AutoProduceMaxBound <= 0)
		{
			alive = 0;
			max = 0;
			return false;
		}

		int count = 0;
		foreach (var u in world.Units.Values)
		{
			if (!u.IsDead && u.BoundStructureId == sim.ID)
				count++;
		}
		alive = count;
		max = cfg.AutoProduceMaxBound;
		return true;
	}
}
