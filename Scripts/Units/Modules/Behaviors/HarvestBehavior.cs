using Godot;
using RTS.Core;
using RTS.Simulation;

namespace RTS.Units
{
	// 采集行为基类：确定性逻辑由 SimManager 每帧驱动。
	// 新采集方式（工人搬运 / 远程光束 / 范围自动）都继承它，实体通过工厂/科技挂载。
	public abstract partial class HarvestBehavior : Node
	{
		public abstract void Tick(SimWorld world, SimEntity entity, PlayerData owner);
	}
}
