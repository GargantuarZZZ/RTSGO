using Godot;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	// 自动施工：无需工人，按配置建造时间推进并同步回血（纳米建筑 / 恶魔献祭建筑）
	[GlobalClass]
	public partial class AutoBuildBehavior : BuildBehavior
	{
		[Export] public float BuildTime = 10f;

		public override void Tick(Structure structure, float fixedDelta)
		{
			if (structure == null || !structure.IsUnderConstruction || BuildTime <= 0f)
				return;

			float progress = fixedDelta / BuildTime;
			// 植物：单位蓝图在生命树 12 格范围内建造速度翻倍（设计表规则）
			if (IsNearFriendlyLifeTree(structure))
				progress *= 2f;
			structure.AdvanceProgress(progress);

			// 施工期间同步回血（与工人建造一致：10% -> 100%）
			if (structure.LifeModule != null)
				structure.LifeModule.Heal(structure.LifeModule.MaxHp * 0.9f * progress);
		}

		private static bool IsNearFriendlyLifeTree(Structure structure)
		{
			var world = RTS.Core.SimManager.Instance?.World;
			var logic = structure?.LogicEntity;
			if (world == null || logic == null || structure.TeamID <= 0)
				return false;

			FP rangeSq = (FP)(12 * 64) * (FP)(12 * 64);
			foreach (var s in world.Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != structure.TeamID ||
					s.StructureTypeId != "PlantLifeTree" ||
					s.CurrentState != RTS.Simulation.SimStructure.StructureState.Active)
					continue;
				if (RTS.Simulation.FPVector2.DistanceSquared(s.Position, logic.Position) <= rangeSq)
					return true;
			}
			return false;
		}
	}
}
