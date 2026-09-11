using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FixMath.NET;
using RTS.Data;
using FP = FixMath.NET.Fix64;


namespace RTS.Simulation
{
	public partial class SimWorld
	{
		// P1-2 内部性能探针（纯 C#，无 Godot 依赖；由 SimManager 读取打印）
		public bool ProfileEnabled;
		public readonly Dictionary<string, double> ProfileMs = new();
		public int ProfileTickCount;

		private void ProfileRun(string name, Action body)
		{
			if (!ProfileEnabled)
			{
				body();
				return;
			}
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			body();
			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 /
				System.Diagnostics.Stopwatch.Frequency;
			ProfileMs[name] = ProfileMs.GetValueOrDefault(name) + ms;
		}

		public void Tick()
		{
			ProfileTickCount++;
			ProfileRun("GridSync", () => Grid.SyncStructureBlocking(Structures));
			ProfileRun("FieldSources", RefreshFieldSources);

			// 挂起路径优先：先重置预算再补算，保证挂起队列不被行动阶段的指令挤占
			//（否则后期单位多、指令多时，挂起永远等不到预算 → 全军原地冻结）
			PathBudgetRemaining = PathBudgetPerTick;
			ProfileRun("PendingPaths", ProcessPendingPaths);

			if (CarpetSpreadCooldown > FP.Zero)
			{
				CarpetSpreadCooldown -= FixedDelta;

				// CD 结束：恢复 1 次充能；只要还没满就继续计时充能
				if (CarpetSpreadCooldown <= FP.Zero && CarpetSpreadCharges < CarpetSpreadMaxCharges)
				{
					CarpetSpreadCharges++;

					if (CarpetSpreadCharges < CarpetSpreadMaxCharges)
						CarpetSpreadCooldown = CarpetSpreadCooldownTime;
					else
						CarpetSpreadCooldown = FP.Zero;
				}
			}

			ProfileRun("CarpetSpreads", TickCarpetSpreads);
			ProfileRun("SelfCreep", TickSelfCreep);
			ProfileRun("SkillCooldowns", TickSkillCooldowns);

			ProfileRun("RemoveDead", RemoveDeadEntities);

			// Buff 持续效果与到期 + 英雄能量恢复
			ProfileRun("BuffsAndHero", () =>
			{
				foreach (var unit in Units.Values)
				{
					unit.Buffs?.Tick(FixedDelta);

					// 恶魔存续时间：到期自动死亡；离开己方立场时寿命消耗 ×3
					if (unit.LifespanTimer > FP.Zero)
					{
						var lg = Grid.WorldToGrid(unit.Position);
						FP rate = unit.IsDemonUnit && !IsOnField(unit.TeamID, lg.X, lg.Y)
							? (FP)3m
							: FP.One;
						unit.LifespanTimer -= FixedDelta * rate;
						if (unit.LifespanTimer <= FP.Zero)
						{
							unit.LifespanTimer = FP.Zero;
							unit.IsDead = true;
						}
					}

					if (unit.HeroMaxEnergy > FP.Zero && unit.HeroEnergyRegenPerSecond > FP.Zero)
					{
						unit.HeroEnergy += unit.HeroEnergyRegenPerSecond * FixedDelta;

						if (unit.HeroEnergy > unit.HeroMaxEnergy)
							unit.HeroEnergy = unit.HeroMaxEnergy;
					}
				}

				foreach (var structure in Structures.Values)
				{
					structure.Buffs?.Tick(FixedDelta);

					if (structure.LifespanTimer > FP.Zero)
					{
						structure.LifespanTimer -= FixedDelta;
						if (structure.LifespanTimer <= FP.Zero)
							structure.IsDead = true;
					}
				}
			});

			ProfileRun("DeployStates", TickDeployStates);
			ProfileRun("MovementIntent", TickUnitMovementIntent);
			ProfileRun("AmmoRecovery", TickAmmoRecovery);
			ProfileRun("CommandEnergy", TickCommandEnergy);
			ProfileRun("Projectiles", TickProjectiles);

			ProfileRun("EntityCollisions", ResolveEntityCollisions);

			// 重要修复：
			// 原文件里地形碰撞执行了两遍。
			// 这里只保留一次，避免单位被墙体额外推挤。
			ProfileRun("TerrainCollisions", ResolveTerrainCollisions);

			// 空中单位只受地图边界限制（不撞墙/建筑，但不出图）
			ProfileRun("ClampAir", ClampAirUnitsToMapBounds);
		}

		public bool TryReservePathBudget()
		{
			if (PathBudgetRemaining <= 0)
				return false;

			PathBudgetRemaining--;
			return true;
		}

		private void ProcessPendingPaths()
		{
			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead || !unit.PathPending)
					continue;

				if (!TryReservePathBudget())
					break;

				unit.CompletePendingPath();
			}
		}

		private void ClampAirUnitsToMapBounds()
		{
			if (!Grid.TerrainBoundsReady)
				Grid.RefreshTerrainBounds();
			if (!Grid.TerrainBoundsReady)
				return;

			FP minX = (FP)(Grid.TerrainMin.X * Grid.TileSize);
			FP minY = (FP)(Grid.TerrainMin.Y * Grid.TileSize);
			FP maxX = (FP)((Grid.TerrainMax.X + 1) * Grid.TileSize);
			FP maxY = (FP)((Grid.TerrainMax.Y + 1) * Grid.TileSize);

			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead || !unit.IsAir)
					continue;

				unit.Position = new FPVector2(
					Clamp(unit.Position.X, minX, maxX),
					Clamp(unit.Position.Y, minY, maxY)
				);
			}
		}

		public SimEntity FindSimEntity(int id)
		{
			if (Units.TryGetValue(id, out var unit))
				return unit;
			if (Structures.TryGetValue(id, out var structure))
				return structure;
			return null;
		}

		// 范围伤害（确定性）：中心命中点 + 半径，边缘按 AreaEdgeDamagePercent 衰减。
		// 只打敌方（排除攻击者阵营与中立 -1），攻击者的增伤 Buff 会正常生效。
		public void ApplyAreaDamage(
			FPVector2 center,
			FP radius,
			int edgeDamagePercent,
			int sourceId,
			FP baseDamage,
			int damageType,
			FP bonusDamage,
			int bonusArmorType)
		{
			if (radius <= FP.Zero || baseDamage <= FP.Zero)
				return;

			SimEntity source = FindSimEntity(sourceId);
			int sourceTeam = source?.TeamID ?? -999;
			FP radiusSq = radius * radius;
			FP edge = (FP)edgeDamagePercent / (FP)100m;

			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead || unit.TeamID == sourceTeam || unit.TeamID == -1)
					continue;

				FP distSq = FPVector2.DistanceSquared(center, unit.Position);
				if (distSq > radiusSq)
					continue;

				FP pct = FP.One;
				if (distSq > FP.Zero)
					pct = FP.One - (FP.One - edge) * FP.Sqrt(distSq / radiusSq);

				unit.TakeDamage(baseDamage * pct, damageType, bonusDamage, bonusArmorType, source);
			}

			foreach (var structure in Structures.Values)
			{
				if (structure == null || structure.IsDead || structure.TeamID == sourceTeam || structure.TeamID == -1)
					continue;

				FP distSq = FPVector2.DistanceSquared(center, structure.Position);
				if (distSq > radiusSq)
					continue;

				FP pct = FP.One;
				if (distSq > FP.Zero)
					pct = FP.One - (FP.One - edge) * FP.Sqrt(distSq / radiusSq);

				structure.TakeDamage(baseDamage * pct, damageType, bonusDamage, bonusArmorType, source);
			}
		}

		// 机动A：纳米巨兽在自己脚下持续生成地毯
		private void TickSelfCreep()
		{
			foreach (var unit in Units.Values)
			{
				if (unit.IsDead || unit.SelfCreepRadius <= 0)
					continue;

				var g = Grid.WorldToGrid(unit.Position);
				AddCreepCircle(g.X, g.Y, (FP)unit.SelfCreepRadius, CreepType.NanoCreep, unit.TeamID);
			}
		}

		private void TickSkillCooldowns()
		{
			foreach (var unit in Units.Values)
			{
				if (unit.SkillCooldown > FP.Zero)
					unit.SkillCooldown -= FixedDelta;
			}
		}

		// 面板技能：记录一次以 (CenterX, CenterY) 为圆心的地毯扩散
		public bool StartCarpetSpread(int centerX, int centerY, int ownerTeam)
		{
			if (CarpetSpreadCharges <= 0)
				return false;

			if (CreepGrid.GetActiveCreep(centerX, centerY) != CreepType.NanoCreep)
				return false;

			CarpetSpreads.Add(new CarpetSpreadState
			{
				CenterX = centerX,
				CenterY = centerY,
				OwnerTeam = ownerTeam,
				MaxRadius = CarpetSpreadRadiusTiles,
				TicksElapsed = 0,
				DurationTicks = Math.Max(1, (int)FP.Round(CarpetSpreadDuration / FixedDelta))
			});

			return true;
		}

		private void TickCarpetSpreads()
		{
			for (int i = CarpetSpreads.Count - 1; i >= 0; i--)
			{
				CarpetSpreadState spread = CarpetSpreads[i];
				spread.TicksElapsed++;

				if (spread.TicksElapsed >= spread.DurationTicks)
				{
					// 扩散完成：直接铺满最大半径，避免定点数累加误差影响边界格
					AddCreepCircle(spread.CenterX, spread.CenterY, spread.MaxRadius, CreepType.NanoCreep, spread.OwnerTeam);
					CarpetSpreads.RemoveAt(i);
					continue;
				}

				FP progress = (FP)spread.TicksElapsed / (FP)spread.DurationTicks;
				AddCreepCircle(spread.CenterX, spread.CenterY, spread.MaxRadius * progress, CreepType.NanoCreep, spread.OwnerTeam);
			}
		}

		private void AddCreepCircle(int centerX, int centerY, FP radius, CreepType type, int ownerTeam)
		{
			int scan = (int)FP.Ceiling(radius);

			for (int x = centerX - scan; x <= centerX + scan; x++)
			{
				for (int y = centerY - scan; y <= centerY + scan; y++)
				{
					var cell = new SimVector2I(x, y);

					if (!Grid.TerrainCells.Contains(cell))
						continue;
					if (Grid.StaticObstacles.Contains(cell))
						continue;

					FP dx = (FP)(x - centerX);
					FP dy = (FP)(y - centerY);

					if (dx * dx + dy * dy <= radius * radius)
						CreepGrid.AddCreep(x, y, type, ownerTeam);
				}
			}
		}

		private void RemoveDeadEntities()
		{
			RemoveDead(Units);
			RemoveDead(Structures);
		}

		// =========================================================
		// 泰伦：架设状态机（1 秒架设/收起）
		// =========================================================
		private void TickDeployStates()
		{
			foreach (var u in Units.Values)
			{
				if (u == null || u.IsDead || !u.IsDeployBusy)
					continue;

				u.DeployTimer -= FixedDelta;
				if (u.DeployTimer > FP.Zero)
					continue;

				if (u.DeployState == 1)
				{
					// 架设完成：最大/当前生命按比例翻倍（比例不变）
					if (u.DeployMaxHpMultiplier > FP.One)
					{
						FP bonus = u.MaxHp * (u.DeployMaxHpMultiplier - FP.One);
						u.DeployedHpBonus = bonus;
						u.MaxHp += bonus;
						u.Hp += bonus;
					}

					if (u.DeployRangeBonus > FP.Zero)
						u.Buffs.AddStatBuff("TerranDeployRange", 1, FP.Zero, FP.One, u.DeployRangeBonus, FP.Zero, -1);

					// 深埋工事：架设期间受伤 -15%
					if (u.DeployDamageReduction > FP.Zero && !u.Buffs.HasBuff("TerranEntrench"))
						u.Buffs.AddBuff("TerranEntrench", 1, FP.Zero, FP.One, FP.One - u.DeployDamageReduction, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);

					u.DeployState = 2;
				}
				else if (u.DeployState == 3)
				{
					// 收起完成：按比例恢复
					if (u.DeployedHpBonus > FP.Zero)
					{
						u.MaxHp -= u.DeployedHpBonus;
						u.Hp -= u.DeployedHpBonus;
						if (u.Hp <= FP.Zero)
							u.Hp = FP.One;
						u.DeployedHpBonus = FP.Zero;
					}

					u.Buffs.RemoveBuff("TerranDeployRange");
					u.Buffs.RemoveBuff("TerranEntrench");
					u.DeployState = 0;

					// 架设中收到的移动指令：收起后补执行
					if (u.PendingUndeployMove)
					{
						u.PendingUndeployMove = false;
						u.CommandMove(this, u.PendingMoveTarget);
					}
				}
			}
		}

		// 弹药恢复：范围内每秒回 10% 弹药；飞行单位只能在机场 5 格内回
		private void TickAmmoRecovery()
		{
			foreach (var u in Units.Values)
			{
				if (u == null || u.IsDead || u.MaxAmmo <= FP.Zero || u.Ammo >= u.MaxAmmo)
					continue;

				bool inRange = u.IsAir ? IsInAirAmmoRange(u) : IsInGroundAmmoRange(u);
				if (!inRange)
					continue;

				u.Ammo += u.MaxAmmo * (FP)0.005m; // 10% / 秒 / 20 tick
				if (u.Ammo > u.MaxAmmo)
					u.Ammo = u.MaxAmmo;
			}
		}

		// 指挥车：弹药范围内能量 +1/s，范围外 -1/s
		private void TickCommandEnergy()
		{
			foreach (var u in Units.Values)
			{
				if (u == null || u.IsDead || !u.EnergyRegenInAmmoRange || u.HeroMaxEnergy <= FP.Zero)
					continue;

				bool inRange = u.IsAir ? IsInAirAmmoRange(u) : IsInGroundAmmoRange(u);
				u.HeroEnergy += (inRange ? FP.One : -FP.One) * FixedDelta;
				if (u.HeroEnergy < FP.Zero)
					u.HeroEnergy = FP.Zero;
				if (u.HeroEnergy > u.HeroMaxEnergy)
					u.HeroEnergy = u.HeroMaxEnergy;
			}
		}

		private bool IsInGroundAmmoRange(SimUnit u)
		{
			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != u.TeamID || s.CurrentState != SimStructure.StructureState.Active)
					continue;
				if (s.AmmoRangeTiles <= 0 || s.AmmoRangeAffectsAir)
					continue;

				FP r = (FP)(s.AmmoRangeTiles * Grid.TileSize);
				if (FPVector2.IsWithinRange(s.Position, u.Position, r))
					return true;
			}
			return false;
		}

		private bool IsInAirAmmoRange(SimUnit u)
		{
			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != u.TeamID || s.CurrentState != SimStructure.StructureState.Active)
					continue;
				if (s.AmmoRangeTiles <= 0 || !s.AmmoRangeAffectsAir)
					continue;

				FP r = (FP)(s.AmmoRangeTiles * Grid.TileSize);
				if (FPVector2.IsWithinRange(s.Position, u.Position, r))
					return true;
			}
			return false;
		}

		private static void RemoveDead<T>(SortedDictionary<int, T> dict) where T : SimEntity
		{
			List<int> dead = new();

			foreach (var kvp in dict)
			{
				if (kvp.Value.IsDead)
					dead.Add(kvp.Key);
			}

			foreach (int id in dead)
				dict.Remove(id);
		}

		private void TickUnitMovementIntent()
		{
			SpatialGrid.Rebuild(Units.Values);

			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead)
					continue;

				_swarmManager.CalculateVelocity(unit, this);

				if (unit.HasTarget)
				{
					unit.LogicTick(FixedDelta);
				}
			}
		}

		// 该格是否在指定阵营的立场范围内（建筑或单位立场）
		public bool IsOnField(int team, int gx, int gy)
		{
			foreach (var e in _fieldSources)
			{
				if (e.TeamID != team)
					continue;

				if (IsInFieldRange(e.Position, GetFieldRadius(e), gx, gy))
					return true;
			}

			return false;
		}

		// 该格是否在任意阵营的立场范围内（恶魔离场惩罚用）
		public bool IsInAnyField(int gx, int gy)
		{
			foreach (var e in _fieldSources)
			{
				if (e.TeamID <= 0)
					continue;

				if (IsInFieldRange(e.Position, GetFieldRadius(e), gx, gy))
					return true;
			}

			return false;
		}

		// 立场源（建筑/单位）预收集：TickDemonFields/急行军等按立场源判定，避免每格全量扫单位
		private void RefreshFieldSources()
		{
			_fieldSources.Clear();

			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead || s.FieldRadius <= 0 ||
					s.CurrentState != SimStructure.StructureState.Active)
					continue;
				_fieldSources.Add(s);
			}

			foreach (var u in Units.Values)
			{
				if (u == null || u.IsDead || u.FieldRadius <= 0)
					continue;
				_fieldSources.Add(u);
			}
		}

		private static int GetFieldRadius(SimEntity e)
		{
			if (e is SimStructure st)
				return st.FieldRadius;
			if (e is SimUnit u)
				return u.FieldRadius;
			return 0;
		}

		private bool IsInFieldRange(FPVector2 center, int radiusTiles, int gx, int gy)
		{
			var c = Grid.WorldToGrid(center);
			int dx = gx - c.X;
			int dy = gy - c.Y;
			FP distSq = (FP)(dx * dx + dy * dy);
			FP rSq = (FP)(radiusTiles * radiusTiles);
			return distSq <= rSq;
		}

		// 友军视野：该点（含目标半径）是否被己方任一有视野单位/建筑照亮
		public bool IsInTeamVision(int team, FPVector2 pos, FP targetRadius)
		{
			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != team || s.VisionRange <= FP.Zero ||
					s.CurrentState != SimStructure.StructureState.Active)
					continue;

				FP range = s.VisionRange + targetRadius;
				if (FPVector2.IsWithinRange(s.Position, pos, range))
				return true;
			}

			// 空间索引候选：只用比“最大视野 + 目标半径”更大的圈捞候选，再按各自视野精确判定
			SpatialGrid.QueryNeighbors(pos, targetRadius + (FP)SpatialGrid.MaxVisionRange, _visionScratch);

			foreach (var u in _visionScratch)
			{
				if (u == null || u.IsDead || u.TeamID != team || u.VisionRange <= FP.Zero)
					continue;

				FP range = u.VisionRange + targetRadius;
				if (FPVector2.IsWithinRange(u.Position, pos, range))
					return true;
			}

			return false;
		}

		// 圆形范围最近单位（SortedDictionary 保证 ID 升序 + 距离相等按 ID 决胜，确定性）
		public SimUnit FindNearestUnit(FPVector2 center, FP radiusSq, Func<SimUnit, bool> filter, int excludeId = -1)
		{
			SimUnit best = null;
			FP bestDist = FP.MaxValue;

			// 超大半径（如“任意距离”）回退全量遍历，避免空间查询窗口爆炸；
			// 普通射程/视野查询用空间索引捞候选，结果与全量遍历完全一致（距离+ID 决胜）
			IEnumerable<SimUnit> candidates = Units.Values;
			if (radiusSq < (FP)(40000m * 40000m))
			{
				SpatialGrid.QueryNeighbors(center, FP.Sqrt(radiusSq), _nearestScratch);
				candidates = _nearestScratch;
			}

			foreach (var u in candidates)
			{
				if (u == null || u.IsDead || u.ID == excludeId || !filter(u))
					continue;

				FP distSq = FPVector2.DistanceSquared(center, u.Position);
				if (distSq > radiusSq)
					continue;

				if (distSq < bestDist || (distSq == bestDist && u.ID < (best?.ID ?? int.MaxValue)))
				{
					best = u;
					bestDist = distSq;
				}
			}

			return best;
		}

		// 圆形范围最近建筑（同上，确定性）
		public SimStructure FindNearestStructure(FPVector2 center, FP radiusSq, Func<SimStructure, bool> filter)
		{
			SimStructure best = null;
			FP bestDist = FP.MaxValue;

			foreach (var s in Structures.Values)
			{
				if (s == null || s.IsDead || !filter(s))
					continue;

				FP distSq = FPVector2.DistanceSquared(center, s.Position);
				if (distSq > radiusSq)
					continue;

				if (distSq < bestDist || (distSq == bestDist && s.ID < (best?.ID ?? int.MaxValue)))
				{
					best = s;
					bestDist = distSq;
				}
			}

			return best;
		}

		private void TickProjectiles()
		{
			for (int i = Projectiles.Count - 1; i >= 0; i--)
			{
				SimProjectile projectile = Projectiles[i];

				if (projectile == null)
				{
					Projectiles.RemoveAt(i);
					continue;
				}

				projectile.LogicTick(FixedDelta);

				// 命中结算统一入口：按弹体配置的命中行为分发
				if (projectile.HasHit)
				{
					HandleProjectileImpact(projectile);
					Projectiles.RemoveAt(i);
					continue;
				}

				if (projectile.IsDead)
				{
					Projectiles.RemoveAt(i);
				}
			}
		}

		// 命中行为统一分发：长宽²结算 / 溅射 / 对地点清菌毯
		private void HandleProjectileImpact(SimProjectile p)
		{
			if (p.FootprintScaledDamage)
			{
				ApplyFootprintScaledImpact(p);
				return;
			}

			if (p.AreaRadius > FP.Zero)
			{
				// 火药除沙科技：AOE 半径 +0.5 格
				if (p.Source is SimUnit su && su.AoeRadiusBonus > FP.Zero)
					p.AreaRadius += su.AoeRadiusBonus;

				// 溅射：命中点在半径内对所有敌方实体结算范围伤害（含攻击者 Buff）
				ApplyAreaDamage(
					p.Position,
					p.AreaRadius,
					p.AreaEdgeDamagePercent,
					p.SourceId,
					p.Damage,
					p.DamageType,
					p.BonusDamage,
					p.BonusDamageType);

				ClearCreepAtImpact(p.Position, p.AreaRadius, p.Source?.TeamID ?? -1);
				return;
			}

			// 无溅射的对地弹道（清菌毯）：只清落点单格
			if (p.HasPointTarget && CreepGrid != null)
			{
				var g = Grid.WorldToGrid(p.Position);
				int sourceTeam = p.Source?.TeamID ?? -1;
				CreepGrid.DestroyNanoCreepRadius(g.X, g.Y, 0, sourceTeam,
					owner => IsFriendlyCreepOwner(sourceTeam, owner));
			}
		}

		// 电浆炮类命中：伤害 = ImpactDamagePerFootprintSq × 目标长宽²，并给范围内敌人减速/减攻速
		private void ApplyFootprintScaledImpact(SimProjectile p)
		{
			int sourceTeam = p.Source?.TeamID ?? -1;
			FP radiusSq = p.ImpactRadius * p.ImpactRadius;

			foreach (var e in Units.Values)
			{
				if (e == null || e.IsDead || e.TeamID == sourceTeam || e.TeamID == -1)
					continue;

				if (FPVector2.DistanceSquared(p.Position, e.Position) > radiusSq)
					continue;

				FP dmg = p.ImpactDamagePerFootprintSq * e.FootprintTiles * e.FootprintTiles;

				e.TakeDamage(dmg, p.DamageType, FP.Zero, 0, p.Source);

				if (p.ImpactSlowSeconds > FP.Zero)
				{
					FP slow = p.ImpactSlowSeconds;
					e.Buffs.AddBuff("PlasmaSlow", 1, slow, FP.One, FP.One, FP.Zero, p.ImpactSlowMoveMultiplier, FP.Zero, FP.Zero, p.SourceId);
					e.Buffs.AddStatBuff("PlasmaSlowAS", 1, slow, p.ImpactSlowAttackMultiplier, FP.Zero, FP.Zero, p.SourceId);
				}
			}

			// 建筑也会被技能打中：伤害 = ImpactDamagePerFootprintSq × 长宽²
			foreach (var st in Structures.Values)
			{
				if (st == null || st.IsDead || st.TeamID == sourceTeam || st.TeamID == -1)
					continue;

				if (FPVector2.DistanceSquared(p.Position, st.Position) > radiusSq)
					continue;

			// 建筑按真实长宽结算（方形建筑 = 长×宽，与旧 GridSize² 一致）
			FP w = (FP)st.GridWidth;
			FP h = (FP)st.GridHeight;
			FP dmg = p.ImpactDamagePerFootprintSq * w * h;
				st.TakeDamage(dmg, p.DamageType, FP.Zero, 0, p.Source);
			}
		}

		private void ClearCreepAtImpact(FPVector2 center, FP radius, int sourceTeam)
		{
			if (CreepGrid == null || radius <= FP.Zero)
				return;

			var g = Grid.WorldToGrid(center);
			FP tiles = radius / (FP)Grid.TileSize;
			int radiusTiles = (int)tiles;
			if (tiles > (FP)radiusTiles)
				radiusTiles++;
			radiusTiles = Math.Max(1, radiusTiles);

			CreepGrid.DestroyNanoCreepRadius(g.X, g.Y, radiusTiles, sourceTeam,
				owner => IsFriendlyCreepOwner(sourceTeam, owner));
		}

		// 溅射清菌毯时排除己方与盟友（2v2 同组）菌毯
		private bool IsFriendlyCreepOwner(int sourceTeam, int owner)
		{
			if (sourceTeam <= 0)
				return owner == sourceTeam;
			return !Rules.AreTeamsHostile(sourceTeam, owner);
		}

		// Shepherd is the moving control anchor: allied bodies yield to it, not vice versa.
		internal bool IgnoresUnitPush(SimUnit unit, SimUnit other)
			=> unit.UnitTypeId == "Shepherd" && unit.TeamID > 0 && other.TeamID > 0 &&
				(unit.TeamID == other.TeamID || !Rules.AreTeamsHostile(unit.TeamID, other.TeamID));

		private void ResolveEntityCollisions()
		{
			SpatialGrid.Rebuild(Units.Values);

			foreach (var a in Units.Values)
			{
				if (a == null || a.IsDead)
					continue;

				FP queryRadius = a.Radius + (FP)SpatialGrid.MaxUnitRadius;
				SpatialGrid.QueryNeighbors(a.Position, queryRadius, _neighborScratch);
				if (_neighborScratch.Count > 1)
					_neighborScratch.Sort((x, y) => x.ID.CompareTo(y.ID));

				foreach (var b in _neighborScratch)
				{
					if (b == null || b.IsDead || b.ID <= a.ID)
						continue;

					// 空地之间无碰撞；空中单位之间互相碰撞。
					if (a.IsAir != b.IsAir)
						continue;

					// 非空中的幽灵仍互不碰撞。
					if ((a.IsGhost && !a.IsAir) || (b.IsGhost && !b.IsAir))
						continue;

					ResolveCollision(a, b);
				}

				foreach (var structure in Structures.Values)
				{
					if (structure == null || structure.IsDead)
						continue;

					// 空中/幽灵允许穿进建筑（空中翻墙，幽灵交矿）。
					if (a.IsAir || a.IsGhost)
						continue;

					ResolveCollision(a, structure);
				}
			}
		}

		private void ResolveTerrainCollisions()
		{
			foreach (var unit in Units.Values)
			{
				if (unit == null || unit.IsDead)
					continue;

				// 幽灵仍然不能穿自然地形；空中单位直接飞越墙体。
				if (unit.IsAir)
					continue;

				ResolveTerrainCollision(unit);
			}
		}

		private void ResolveCollision(SimEntity a, SimEntity b)
		{
			if (a == null || b == null)
				return;

			if (a.IsDead || b.IsDead)
				return;

			// 建筑是方块：单位按矩形碰撞推出，不做圆形
			if (b is SimStructure structure)
			{
				if (a is SimUnit groundUnit)
					PushUnitOutOfStructureRect(groundUnit, structure);
				return;
			}

			FP minDist = a.Radius + b.Radius;
			FP distSq = FPVector2.DistanceSquared(a.Position, b.Position);

			if (distSq < minDist * minDist && distSq > FP.Zero)
			{
				FP dist = FP.Sqrt(distSq);
				FP overlap = minDist - dist;
				FPVector2 resolveDir = (a.Position - b.Position) / dist;

				bool keepA = a is SimUnit ua && b is SimUnit ub && IgnoresUnitPush(ua, ub);
				bool keepB = b is SimUnit vb && a is SimUnit va && IgnoresUnitPush(vb, va);
				// Two allied shepherds may overlap; neither displaces the other's control area.
				if (!keepA) a.Position += resolveDir * (keepB ? overlap : overlap / (FP)2);
				if (!keepB) b.Position -= resolveDir * (keepA ? overlap : overlap / (FP)2);
			}
		}

		// 单位圆 vs 建筑矩形：推到矩形外（严格按长宽，不延伸到角外）
		private void PushUnitOutOfStructureRect(SimUnit unit, SimStructure structure)
		{
			// 蓝图只对"自己人"生效：敌方既看不见蓝图，也不该被它推开。
			// 自己人会被强制移出（见 SimManager.TickBlueprintEvictions），这里只兜底。
			if (!SimGrid.BlocksForTeam(structure, unit.TeamID))
				return;

			FP t = (FP)Grid.TileSize;
			FP minX = (FP)structure.GridPosition.X * t;
			FP minY = (FP)structure.GridPosition.Y * t;
			FP maxX = minX + (FP)structure.GridWidth * t;
			FP maxY = minY + (FP)structure.GridHeight * t;

			FP closestX = Clamp(unit.Position.X, minX, maxX);
			FP closestY = Clamp(unit.Position.Y, minY, maxY);
			FP dx = unit.Position.X - closestX;
			FP dy = unit.Position.Y - closestY;
			FP distSq = dx * dx + dy * dy;

			if (distSq >= unit.Radius * unit.Radius)
				return;

			if (distSq > FP.Zero)
			{
				FP dist = FP.Sqrt(distSq);
				unit.Position += new FPVector2(dx / dist, dy / dist) * (unit.Radius - dist);
				return;
			}

			// 圆心在矩形内部：沿最小穿透轴推到矩形外。
			// 深度重叠时必须一次推到位（到最近边外侧一个半径），
			// 旧实现只推 Radius+1，大建筑里的单位会被连续多帧来回抖。
			FP left = unit.Position.X - minX;
			FP right = maxX - unit.Position.X;
			FP top = unit.Position.Y - minY;
			FP bottom = maxY - unit.Position.Y;
			FP minSide = left;
			if (right < minSide) minSide = right;
			if (top < minSide) minSide = top;
			if (bottom < minSide) minSide = bottom;

			FP push;
			FPVector2 dir;
			if (minSide == left) { dir = new FPVector2(-FP.One, FP.Zero); push = left + unit.Radius; }
			else if (minSide == right) { dir = new FPVector2(FP.One, FP.Zero); push = right + unit.Radius; }
			else if (minSide == top) { dir = new FPVector2(FP.Zero, -FP.One); push = top + unit.Radius; }
			else { dir = new FPVector2(FP.Zero, FP.One); push = bottom + unit.Radius; }

			unit.Position += dir * push;
		}

		private void ResolveTerrainCollision(SimUnit unit)
		{
			if (unit == null || unit.IsDead)
				return;

			var gridPos = Grid.WorldToGrid(unit.Position);
			FP radiusSq = unit.Radius * unit.Radius;

			for (int x = -1; x <= 1; x++)
			{
				for (int y = -1; y <= 1; y++)
				{
					var checkCell = new SimVector2I(gridPos.X + x, gridPos.Y + y);

					if (!Grid.StaticObstacles.Contains(checkCell))
						continue;

					FP minX = (FP)(checkCell.X * Grid.TileSize);
					FP minY = (FP)(checkCell.Y * Grid.TileSize);
					FP maxX = minX + (FP)Grid.TileSize;
					FP maxY = minY + (FP)Grid.TileSize;

					FP closestX = Clamp(unit.Position.X, minX, maxX);
					FP closestY = Clamp(unit.Position.Y, minY, maxY);

					FP dx = unit.Position.X - closestX;
					FP dy = unit.Position.Y - closestY;
					FP distSq = dx * dx + dy * dy;

					if (distSq < radiusSq && distSq > FP.Zero)
					{
						FP dist = FP.Sqrt(distSq);
						FP overlap = unit.Radius - dist;

						FPVector2 resolveDir = new FPVector2(dx / dist, dy / dist);
						unit.Position += resolveDir * overlap;
					}
					else if (distSq == FP.Zero)
					{
						// 单位中心刚好在障碍格内部或边界上。
						// 使用稳定方向推出，避免除以 0。
						FPVector2 cellCenter = new FPVector2(
							minX + (FP)Grid.TileSize / (FP)2,
							minY + (FP)Grid.TileSize / (FP)2
						);

						FPVector2 dir = unit.Position - cellCenter;

						if (dir.MagnitudeSquared() == FP.Zero)
							dir = new FPVector2(FP.One, FP.Zero);
						else
							dir = dir.Normalized();

						unit.Position += dir * unit.Radius;
					}
				}
			}
		}

		private FP Clamp(FP value, FP min, FP max)
		{
			if (value < min)
				return min;

			if (value > max)
				return max;

			return value;
		}

		// =========================================================
		// Hash / Debug
		// =========================================================

	}
}
