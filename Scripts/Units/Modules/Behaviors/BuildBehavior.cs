using Godot;
using RTS.Simulation;

namespace RTS.Units
{
	// 建造行为基类：施工推进策略由 SimManager 每帧驱动。
	// 新建造方式（工人施工 / 自动施工 / 献祭 / 远程蓝图）都继承它，实体通过工厂挂载。
	public abstract partial class BuildBehavior : Node
	{
		// 建造距离（格）：所有建造子模块共享，BuildAction 从这里读取
		[Export(PropertyHint.Range, "1,99,1")]
		public int BuildRangeTiles = 1;

		public abstract void Tick(Structure structure, float fixedDelta);
	}

}
