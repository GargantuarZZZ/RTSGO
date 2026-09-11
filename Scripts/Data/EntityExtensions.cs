// File: res://Scripts/Data/EntityExtensions.cs
using Godot;
using System;
using RTS.Data;
using RTS.Units;
using RTS.Actions;
using RTS.Actions.Implementation;

namespace RTS.Core
{
	public static class EntityExtensions
	{
		public static SceneTree GetTree(this IEntity entity)
		{
			return (entity as Node)?.GetTree();
		}

		// 替换 FindClosestEnemy 方法：
		public static IEntity FindClosestEnemy(this IEntity self, float radius, Func<IEntity, bool> filter = null)
		{
			if (self.LogicEntity == null || RTS.Core.SimManager.Instance == null) return null;

			var world = RTS.Core.SimManager.Instance.World;
			FixMath.NET.Fix64 radiusSq = (FixMath.NET.Fix64)(radius * radius);

			// 单位与建筑分开找最近，再按“距离近者优先、ID 小者决胜”合并（与旧逻辑一致）
			var bestUnit = world.FindNearestUnit(
				self.LogicEntity.Position,
				radiusSq,
				u =>
				{
					if (u.ID == self.LogicEntity.ID || u.TeamID == -1)
						return false;
					// 中立不打中立：中立阵营（TeamID<=0）不会自动索敌任何中立目标
					if (self.TeamID <= 0 && u.TeamID <= 0)
						return false;
					// P1-5：同组盟友不是敌人（2v2）；中立塔(-2)仍可打
					if (!RTS.Core.SimManager.Instance.AreTeamsHostile(self.TeamID, u.TeamID))
						return false;

					var entity = RTS.Core.SimManager.Instance.FindEntityById(u.ID);
					return entity != null &&
						GodotObject.IsInstanceValid((GodotObject)entity) &&
						(filter == null || filter(entity));
				});

			IEntity closest = null;
			if (bestUnit != null)
			{
				var bestNode = RTS.Core.SimManager.Instance.FindEntityById(bestUnit.ID);
				if (bestNode != null && GodotObject.IsInstanceValid((GodotObject)bestNode))
					closest = bestNode;
			}

			FixMath.NET.Fix64 bestDistSq = bestUnit != null
				? RTS.Simulation.FPVector2.DistanceSquared(self.LogicEntity.Position, bestUnit.Position)
				: FixMath.NET.Fix64.MaxValue;
			int bestId = bestUnit?.ID ?? int.MaxValue;

			var bestStruct = world.FindNearestStructure(
				self.LogicEntity.Position,
				radiusSq,
				s =>
				{
					if (s.TeamID == -1)
						return false;
					// 中立不打中立：中立建筑不自动攻击其他中立建筑/资源点/圣地
					if (self.TeamID <= 0 && s.TeamID <= 0)
						return false;
					// P1-5：同组盟友建筑不是敌人
					if (!RTS.Core.SimManager.Instance.AreTeamsHostile(self.TeamID, s.TeamID))
						return false;

					var entity = RTS.Core.SimManager.Instance.FindEntityById(s.ID);
					if (entity == null || (filter != null && !filter(entity)))
						return false;
					if (entity is Structure st && st.CurrentState == Structure.StructureState.Blueprint)
						return false;
					if (entity is RTS.Units.ShrineStructure)
						return false;

					return true;
				});

			if (bestStruct != null)
			{
				FixMath.NET.Fix64 structDistSq = RTS.Simulation.FPVector2.DistanceSquared(
					self.LogicEntity.Position, bestStruct.Position);

				if (structDistSq < bestDistSq || (structDistSq == bestDistSq && bestStruct.ID < bestId))
					return RTS.Core.SimManager.Instance.FindEntityById(bestStruct.ID);
			}

			return closest;
		}

		// 自动索敌：射程内任一武器可命中的最近敌人
		public static IEntity FindAutoAttackTarget(this IEntity self, float searchRadius)
		{
			return self.FindClosestEnemy(searchRadius, e =>
				self.CombatModule != null && self.CombatModule.CanAnyWeaponTarget(e));
		}

		// 自动索敌并下令攻击（停止/防御行为的统一入口）
		public static bool TryAutoAttack(this IEntity self, float searchRadius)
		{
			var enemy = self.FindAutoAttackTarget(searchRadius);
			if (enemy == null)
				return false;

			self.CommandAttack(enemy, false);
			return true;
		}

		// --- 统一通过 StartAction 下发指令，避免直接调用 SetTarget ---

		public static void CommandMoveTo(this IEntity self, RTS.Simulation.FPVector2 targetPos, bool queue = false)
{
	if (self.IsDeadOrNull()) return;
	self.Brain?.StartAction("Move", targetPos, null, false, queue);
}

public static void CommandStop(this IEntity self)
{
	if (self.IsDeadOrNull()) return;
	self.Brain?.StartAction("Stop", false);
}

public static void CommandAttack(this IEntity self, IEntity target, bool queue = false)
{
	if (self.IsDeadOrNull() || target == null) return;
	self.Brain?.StartAction("Attack", RTS.Simulation.FPVector2.Zero, target, false, queue);
}

public static void CommandAttackMove(this IEntity self, RTS.Simulation.FPVector2 targetPos, bool queue = false)
{
	if (self.IsDeadOrNull()) return;
	self.Brain?.StartAction("AttackMove", targetPos, null, false, queue);
}

public static void CommandHarvest(this IEntity self, IEntity source, bool queue = false)
{
	if (self.IsDeadOrNull() || source == null) return;
	self.Brain?.StartAction("Harvest", RTS.Simulation.FPVector2.Zero, source, false, queue);
}
	}
}
