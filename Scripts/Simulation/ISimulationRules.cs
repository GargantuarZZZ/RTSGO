using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	// Host-owned match/technology state. Read deterministic data only, under
	// the world's synchronization lock; implementations must not call scene APIs.
	public interface ISimulationRules
	{
		bool AreTeamsHostile(int teamA, int teamB);
		FP GetLifestealFraction(int teamId);

		// 蓝图归属判定：只有"自己人"（同队或同组盟友）才被己方蓝图阻挡/推出/可见。
		// 中立（负队伍号）永远不算任何人的自己人。
		bool AreTeamsFriendly(int teamA, int teamB);
	}

	public sealed class DefaultSimulationRules : ISimulationRules
	{
		public static readonly DefaultSimulationRules Instance = new();
		private DefaultSimulationRules() { }
		public bool AreTeamsHostile(int teamA, int teamB) => teamA != teamB;
		public FP GetLifestealFraction(int teamId) => FP.Zero;
		public bool AreTeamsFriendly(int teamA, int teamB) =>
			teamA > 0 && teamB > 0 && teamA == teamB;
	}
}
