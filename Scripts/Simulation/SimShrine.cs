// 圣地逻辑实体：占领进度/占领方/奖励计时全部使用定点数，纳入世界哈希
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	public class SimShrine : SimStructure
	{
		public FP CaptureProgress;
		public int CapturingTeam = -1;
		public FP RewardTimer;

		public SimShrine(int id, int teamId, FPVector2 centerPos, SimVector2I gridPos, int gridSize)
			: base(id, teamId, centerPos, gridPos, gridSize)
		{
		}

		public override long GetStateHash()
		{
			long hash = base.GetStateHash();
			hash ^= (long)(CaptureProgress * (FP)1000m);
			hash ^= CapturingTeam;
			hash ^= (long)(RewardTimer * (FP)1000m);
			return hash;
		}
	}
}
