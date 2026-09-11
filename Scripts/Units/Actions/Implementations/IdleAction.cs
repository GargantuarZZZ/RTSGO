// File: res://Scripts/Units/Actions/Implementations/IdleAction.cs
using Godot;
using System;
using RTS.Core;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions
{
	[GlobalClass]
	public partial class IdleAction : UnitAction
	{
		// 索敌节流：空闲单位每 0.5 秒才重新全量搜索一次
		private int _acquireCooldownTicks = 0;

		public override void _Ready()
		{
			ActionName = "Idle";
			Layer = ActionLayer.Body;
			Queueable = false;
		}

		public override void OnUpdate(double delta)
		{
			// 被勾魂的敌人不受控制地走向施法者，不能自己索敌
			if (_unit?.LogicEntity is SimUnit charmed && charmed.Buffs.HasBuff("DemonCharm"))
				return;

			// 自杀冲锋单位（熔岩旗手）：主动冲向最近敌人，碰到即自爆
			if (_unit is RTS.Units.Unit kamikaze)
			{
				var kamCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(kamikaze.UnitName);
				if (kamCfg != null && kamCfg.Kamikaze && TryKamikaze(kamikaze))
					return;
			}

			// 多足：无视野/不在控制范围的单位不自动索敌，只执行上一次指示
			if (_unit is RTS.Units.Unit wanderer &&
				RTS.World.Game.GetPlayerByTeam(wanderer.TeamID)?.Race?.RaceName == "Wanderer")
			{
				var wCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(wanderer.UnitName);

				if (wCfg != null && wanderer.LogicEntity is SimUnit wSim)
				{
					// 在控制范围内的单位按攻击射程自动开火（战车/电磁炮型）
					if (!wCfg.NoControlNeeded && !wSim.NoControlNeeded &&
						!RTS.Core.SimManager.IsInControlRange(wSim))
						return;
				}
			}

			// 医疗兵：空闲时自动治疗范围内最近受伤友军（数值全部来自配置表）
			if (_unit is RTS.Units.Unit healUnit && !_unit.IsStructure)
			{
				var healCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(healUnit.UnitName);
				if (healCfg != null && healCfg.CanHeal && healCfg.HealPerSecond > 0f && healCfg.HealRangeTiles > 0)
				{
					TryHealAlly(healUnit, healCfg);
					return;
				}
			}

			// 建造中/蓝图建筑不自动索敌
			if (_unit is Structure s && s.CurrentState != Structure.StructureState.Completed)
				return;

			if (_unit.CombatModule == null)
				return;

			// 多目标英雄（地狱领主 5×3）：一轮打多个不同敌人
			var idleCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(_unit.DisplayName);
			if (idleCfg != null && idleCfg.MultiShotTargets > 1 && _unit is RTS.Units.Unit multiUnit)
			{
				float multiRange = Mathf.Max(300f, _unit.CombatModule.GetAttackRange());
				// 索敌 = 攻击射程 + 友军视野
				var enemies = multiUnit.FindClosestEnemies(multiRange, e =>
					_unit.CombatModule.CanAnyWeaponTarget(e) &&
					RTS.Core.SimManager.IsTargetVisibleToTeam(_unit.TeamID, e), idleCfg.MultiShotTargets);

				if (enemies.Count > 0)
					multiUnit.PerformMultiShotVolley(enemies[0]);

				return;
			}

			// 索敌节流：空闲单位每 0.5 秒才重新全量搜索一次
			if (_acquireCooldownTicks > 0)
			{
				_acquireCooldownTicks--;
				return;
			}

			float searchRadius = _unit.IsStructure
				? _unit.CombatModule.GetAttackRange()
				: Mathf.Max(300f, _unit.CombatModule.GetAttackRange());

			// 索敌 = 攻击射程 + 友军视野
			var enemy = _unit.FindClosestEnemy(searchRadius, e =>
				_unit.CombatModule.CanAnyWeaponTarget(e) &&
				RTS.Core.SimManager.IsTargetVisibleToTeam(_unit.TeamID, e));

			if (enemy != null)
			{
				_unit.CommandAttack(enemy, false);
				return;
			}

			// 防御建筑或跑打单位：没有敌军时自动清理敌方纳米菌毯
			bool runAndGun = RTS.Data.Configs.ConfigDatabase.GetUnit(_unit.DisplayName)?.AttackMoveFiresWhileMoving == true;
			bool autoCreep = RTS.Data.Configs.ConfigDatabase.GetUnit(_unit.DisplayName)?.AutoClearCreep == true;

			if (_unit.CombatModule.ActiveWeapon != null && (_unit.IsStructure || runAndGun || autoCreep))
			{
				CreepClearHelper.TryFireAtCreep(_unit, RTS.Core.SimManager.Instance?.World);
			}

			_acquireCooldownTicks = 10; // 0.5 秒
		}

		private static void TryHealAlly(RTS.Units.Unit healer, RTS.Data.Configs.UnitConfig cfg)
		{
			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null || healer.LogicEntity is not SimUnit self)
				return;

			FP range = (FP)(cfg.HealRangeTiles * world.Grid.TileSize);
			FP rangeSq = range * range;

			SimUnit best = world.FindNearestUnit(self.Position, rangeSq, sim =>
				sim.TeamID == self.TeamID && sim.ID != self.ID &&
				sim.Hp < sim.MaxHp && !sim.CannotBeHealed);

			if (best == null)
				return;

			FP heal = (FP)cfg.HealPerSecond * world.FixedDelta;
			FP newHp = best.Hp + heal;
			best.Hp = newHp > best.MaxHp ? best.MaxHp : newHp;
		}

		private static bool TryKamikaze(RTS.Units.Unit kamikaze)
		{
			if (kamikaze.LogicEntity is not SimUnit self || kamikaze.CombatModule == null)
				return false;

			var enemy = kamikaze.FindClosestEnemy(420f);
			if (enemy?.LogicEntity == null)
				return false;

			FP dist = FP.Sqrt(FPVector2.DistanceSquared(self.Position, enemy.LogicEntity.Position));
			FP contact = self.Radius + enemy.LogicEntity.Radius + (FP)10m;

			if (dist <= contact)
			{
				kamikaze.StartDeathVisual(); // 触发自爆 + 留旗 + 死亡动画
				return true;
			}

			self.CommandMove(enemy.LogicEntity.Position, enemy.LogicEntity.ID);
			return true;
		}
	}
}
