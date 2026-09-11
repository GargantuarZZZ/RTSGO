// File: res://Scripts/Simulation/SwarmManager.cs
using System.Collections.Generic;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	public class SwarmManager
	{
		private readonly List<SimUnit> _neighborScratch = new();

		public void CalculateVelocity(SimUnit unit, SimWorld world)
		{
			if (unit.IsDead) return;

			// 短路径重试冷却递减（每单位每 tick 一次）
			if (unit.ShortPathRetryTicks > 0)
				unit.ShortPathRetryTicks--;

			// 寻路预算挂起：原地待命，等补算路径（避免朝目标直冲穿墙）
			if (unit.PathPending)
			{
				unit.Velocity = FPVector2.Zero;
				return;
			}

			// 泰伦架设：架设中/已架设完全停住
			if (unit.IsDeployed || unit.IsDeployBusy)
			{
				unit.Velocity = FPVector2.Zero;
				unit.HasTarget = false;
				return;
			}

			// 纳米巨兽：离开地毯后无法移动
			if (unit.RequiresCarpet)
			{
				var gridPos = world.Grid.WorldToGrid(unit.Position);
				if (world.CreepGrid.GetActiveCreep(gridPos.X, gridPos.Y) != RTS.Data.CreepType.NanoCreep)
				{
					unit.Velocity = FPVector2.Zero;
					return;
				}
			}

			FP effectiveMax = unit.GetEffectiveMaxSpeed();

			// 机动B：在纳米地毯上移速加成
			if (unit.SpeedOnCreepMultiplier > FP.One)
			{
				var gridPos = world.Grid.WorldToGrid(unit.Position);
				if (world.CreepGrid.GetActiveCreep(gridPos.X, gridPos.Y) == RTS.Data.CreepType.NanoCreep)
					effectiveMax *= unit.SpeedOnCreepMultiplier;
			}

			// 恶魔急行军：站在立场上移速加成
			if (unit.FieldSpeedMultiplier > FP.One)
			{
				var fieldGrid = world.Grid.WorldToGrid(unit.Position);
				if (world.IsOnField(unit.TeamID, fieldGrid.X, fieldGrid.Y))
					effectiveMax *= unit.FieldSpeedMultiplier;
			}

			FPVector2 separation = FPVector2.Zero;
			FPVector2 seek = FPVector2.Zero;

			// 1. 顺着 A* 节点走
			if (unit.HasTarget)
			{
				FPVector2 diff = unit.TargetPosition - unit.Position;
				FP distSq = diff.MagnitudeSquared();

				bool isLastWaypoint = unit.CurrentWaypointIndex >= unit.Path.Count - 1;
				// 终点圈 10m（需要急停），中间切角圈 32m
				FP stopDist = isLastWaypoint ? (FP)10m : (FP)32m;

				if (distSq <= stopDist * stopDist)
				{
					unit.CurrentWaypointIndex++;
					if (unit.CurrentWaypointIndex < unit.Path.Count)
					{
						unit.TargetPosition = unit.Path[unit.Path.Count > 0 ? unit.CurrentWaypointIndex : 0];
					}
					else
					{
						// 精确停稳：飞行单位直接落在目标点（地面单位步长也可能越过终点，
						// 但落点由碰撞/寻路兜底；统一清速停住避免滑行）
						if (unit.IsAir)
							unit.Position = unit.TargetPosition;
						unit.HasTarget = false;
						unit.Velocity = FPVector2.Zero;
						// 保留路径：重复的同目标指令（右键连点/到达判定回环）不再重启移动
#if DEBUG
						FP distToFinalDbg = FP.Sqrt(FPVector2.DistanceSquared(unit.Position, unit.FinalTargetPosition));
						if (distToFinalDbg > (FP)192m)
							world.DiagnosticSink?.Invoke($"[MoveDebug] 早停 unit={unit.ID} {unit.UnitTypeId} t={unit.TeamID} 距终点={distToFinalDbg / (FP)64m:F1}格 path={unit.Path?.Count ?? 0} wp={unit.CurrentWaypointIndex}");
#endif
						return;
					}
				}

				diff = unit.TargetPosition - unit.Position;
				FP dist = FP.Sqrt(diff.MagnitudeSquared());

				// 未到达终点前始终保持最大速度（不使用减速半径）
				if (dist > FP.Zero) seek = (diff / dist) * effectiveMax;
			}

			// 2. 斥力计算 (幽灵单位不受到任何推挤，也不会推挤别人)
			if (!unit.IsGhost)
			{
				// 空间索引候选 + 按 ID 排序，累加顺序与原全量遍历完全一致
				FP queryRadius = unit.Radius + (FP)world.SpatialGrid.MaxUnitRadius;
				world.SpatialGrid.QueryNeighbors(unit.Position, queryRadius, _neighborScratch);
				if (_neighborScratch.Count > 1)
					_neighborScratch.Sort((a, b) => a.ID.CompareTo(b.ID));

				foreach (var other in _neighborScratch)
				{
					if (unit.ID == other.ID || other.IsDead || other.IsGhost) continue;
					if (world.IgnoresUnitPush(unit, other)) continue;
					separation += GetPushForce(unit.Position, unit.Radius, other.Position, other.Radius);
				}

				foreach (var s in world.Structures.Values)
				{
					if (s.IsDead) continue;
					// 建筑按矩形推挤（严格长宽，不做圆形）
					separation += GetStructurePushForce(unit, s, world) * (FP)1.5m;
				}
			}

			// 3. 最终速度合成
			unit.Velocity = (seek * unit.SeekWeight) + (separation * unit.SeparationWeight);

			// 限制最高速
			if (unit.Velocity.MagnitudeSquared() > effectiveMax * effectiveMax)
				unit.Velocity = unit.Velocity.Normalized() * effectiveMax;
		}

		private FPVector2 GetPushForce(FPVector2 posA, FP radiusA, FPVector2 posB, FP radiusB)
		{
			FP distSq = FPVector2.DistanceSquared(posA, posB);
			FP min = radiusA + radiusB;
			if (distSq < min * min && distSq > FP.Zero)
			{
				FP dist = FP.Sqrt(distSq);
				return (posA - posB) / dist * (min - dist) * (FP)8m;
			}
			return FPVector2.Zero;
		}

		// 单位圆 vs 建筑矩形：距离取到矩形最近点
		private FPVector2 GetStructurePushForce(SimUnit unit, SimStructure s, SimWorld world)
		{
			FP t = (FP)world.Grid.TileSize;
			FP minX = (FP)s.GridPosition.X * t;
			FP minY = (FP)s.GridPosition.Y * t;
			FP maxX = minX + (FP)s.GridWidth * t;
			FP maxY = minY + (FP)s.GridHeight * t;

			FP closestX = unit.Position.X;
			if (closestX < minX) closestX = minX;
			else if (closestX > maxX) closestX = maxX;

			FP closestY = unit.Position.Y;
			if (closestY < minY) closestY = minY;
			else if (closestY > maxY) closestY = maxY;

			FP dx = unit.Position.X - closestX;
			FP dy = unit.Position.Y - closestY;
			FP distSq = dx * dx + dy * dy;
			FP radius = unit.Radius;

			if (distSq >= radius * radius)
				return FPVector2.Zero;

			if (distSq > FP.Zero)
			{
				FP dist = FP.Sqrt(distSq);
				return new FPVector2(dx / dist, dy / dist) * (radius - dist) * (FP)8m;
			}

			// 圆心在矩形内部：沿最小穿透轴推出
			FP left = unit.Position.X - minX;
			FP right = maxX - unit.Position.X;
			FP top = unit.Position.Y - minY;
			FP bottom = maxY - unit.Position.Y;
			FP minSide = left;
			if (right < minSide) minSide = right;
			if (top < minSide) minSide = top;
			if (bottom < minSide) minSide = bottom;

			FPVector2 dir;
			if (minSide == left) dir = new FPVector2(-FP.One, FP.Zero);
			else if (minSide == right) dir = new FPVector2(FP.One, FP.Zero);
			else if (minSide == top) dir = new FPVector2(FP.Zero, -FP.One);
			else dir = new FPVector2(FP.Zero, FP.One);

			return dir * radius * (FP)8m;
		}
	}
}
