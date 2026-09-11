using RTS.Data;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
public partial class SimManager
{
	// SCV economy, supply, paid mixed production. One simple infantry ability, no micro planner.
	private sealed class UnionBotStrategy : BotRaceStrategy
	{
		public override void Tick(SimManager sim, BotBrain b, int tick)
		{
			sim.TickBotCombat(b.Team, tick);
			sim.TickBotBuild(b.Team, b.RaceCfg, tick);
			sim.TickBotBlueprintWatchdog(b.Team);
			sim.TickBotEconomy(b.Team, b.RaceCfg);
			sim.TickBotProduction(b.Team, b.RaceCfg, tick);
			sim.TickBotTech(b.Team, b.RaceCfg);
			sim.TickBotExpansion(b.Team, b.RaceCfg, tick);
		}

		public override bool HandleUnit(SimManager sim, int team, IEntity node, SimUnit unit, bool advancing)
		{
			var stim = node.Brain?.GetAction<RTS.Actions.Implementation.StimAction>("Stim");
			if (unit.CombatTargetId >= 0 && unit.Hp > unit.MaxHp / (FP)2 && stim?.CanExecute() == true)
				node.Brain.StartAction("Stim");
			return false;
		}
	}
}
}
