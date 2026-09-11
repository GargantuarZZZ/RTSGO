using Godot;
using RTS.Simulation;

namespace RTS.Units
{
	// 工人建造：施工/献祭/远程蓝图由 BuildAction 驱动，本模块只提供建造参数
	[GlobalClass]
	public partial class WorkerBuildBehavior : BuildBehavior
	{
		public override void Tick(Structure structure, float fixedDelta)
		{
		}
	}
}
