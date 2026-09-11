using Godot;
using RTS.Core;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	[GlobalClass]
	public partial class ResearchAction : UnitAction
	{
		[Export] public string TechId = "";
		[Export] public string DisplayNameText = "";

		public override void _Ready()
		{
			ActionName = "Research_" + TechId;
			Queueable = false;
		}

		public override bool CanExecute()
		{
			if (_unit == null || string.IsNullOrEmpty(TechId))
				return false;

			if (_unit is Structure s && s.CurrentState != Structure.StructureState.Completed)
				return false;

			var player = RTS.World.Game.GetPlayerByTeam(_unit.TeamID);
			if (player?.PlayerData == null)
				return false;

			var pd = player.PlayerData;
			if (pd.IsResearching)
				return false;

			var cfg = ConfigDatabase.GetTech(TechId);
			if (cfg == null)
				return false;

			// 可循环科技允许重复研究；普通科技只研究一次
			if (pd.HasTech(TechId) && !cfg.IsRepeatable)
				return false;

			foreach (string req in cfg.RequiredTechIds)
			{
				if (!pd.HasTech(req))
					return false;
			}

			if (cfg.RequiresHarvester && !HasCompletedHarvester(player.TeamId))
				return false;

			if (!string.IsNullOrEmpty(cfg.ExclusiveGroup) && HasTechInGroup(pd, cfg.ExclusiveGroup))
				return false;

			return pd.HasResources(GetScaledCost(cfg, pd));
		}

		// 纯 C# 集合：CanExecute / 确定性扣费都在模拟线程执行，
		// Godot.Collections.Dictionary 在模拟线程 new 会触发 Handle is not initialized 崩溃
		public static System.Collections.Generic.Dictionary<ResourceType, float> GetScaledCost(TechConfig cfg, PlayerData pd)
		{
			var cost = new System.Collections.Generic.Dictionary<ResourceType, float>();
			if (cfg == null)
				return cost;

			float multiplier = 1f;

			if (cfg.IsRepeatable && pd != null)
			{
				int level = pd.GetTechLevel(cfg.TechId);
				multiplier = Mathf.Pow(cfg.RepeatCostMultiplier, level);
			}

			foreach (var kvp in cfg.Cost)
				cost[kvp.Key] = kvp.Value * multiplier;

			return cost;
		}

		private static bool HasTechInGroup(PlayerData pd, string group)
		{
			foreach (string techId in pd.ResearchedTechs)
			{
				var cfg = ConfigDatabase.GetTech(techId);
				if (cfg != null && cfg.ExclusiveGroup == group)
					return true;
			}

			return false;
		}

		private static bool HasCompletedHarvester(int teamId)
		{
			if (SimManager.Instance?.World == null)
				return false;

			foreach (var sim in SimManager.Instance.World.Structures.Values)
			{
				if (sim.TeamID != teamId || sim.IsDead || sim.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;

				if (SimManager.Instance.FindEntityById(sim.ID) is Structure st && st.StructureName == "NanoHarvester")
					return true;
			}

			return false;
		}
	}
}
