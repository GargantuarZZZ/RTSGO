// File: res://Scripts/Units/Actions/Implementations/TrainUnitAction.cs
using Godot;
using System;
using RTS.Core;
using RTS.Data;
using RTS.World;
using RTS.Units;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	[GlobalClass]
	public partial class TrainUnitAction : UnitAction
	{
		[ExportGroup("Settings")]
		[Export] public string UnitName = "SCV";
		[Export] public Godot.Collections.Dictionary<ResourceType, float> Costs { get; set; } = new();
		[Export] public float BuildTime = 1.0f;

		// 定点数计时，避免浮点累加漂移
		private FP _timerFP = FP.Zero;
		private FP _buildTimeFP;

		public float GetProgress()
		{
			// UI 显示时转回 float
			if (_buildTimeFP <= FP.Zero) return 1.0f;
			return (float)(_timerFP / _buildTimeFP);
		}

		// 供确定性状态哈希使用（不依赖 HashCode，跨进程稳定）
		public long GetTimerRaw1000()
		{
			return (long)(_timerFP * (FP)1000m);
		}

		public override void _Ready()
		{
			ActionName = UnitName;
			Layer = ActionLayer.Production;
			BlockingLayers = ActionLayer.None;
			Queueable = true;

			// 数值全部以配置表为准：场景预制体里的 Costs / BuildTime 会被这里覆盖
			var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName);
			if (cfg != null)
			{
				Costs = cfg.Costs;
				BuildTime = cfg.BuildTime;
			}

			// 预先缓存定点数格式的建造时间
			_buildTimeFP = (FP)BuildTime;
		}

		public override bool TryPayCost()
		{
			// 建造中的建筑不能扣费训练
			if (_unit is Structure s && s.CurrentState != Structure.StructureState.Completed)
				return false;

			var player = Game.GetPlayerByTeam(_unit.TeamID);
			if (player == null || player.PlayerData == null) return false;

			// 科技门：单位配置里的 RequiredTechIds 未满足不能生产
			var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName);
			if (unitCfg != null)
			{
				foreach (string req in unitCfg.RequiredTechIds)
				{
					if (!player.PlayerData.HasTech(req))
						return false;
				}
			}

			// 人口上限：超出则禁止生产
			int unitSupply = unitCfg?.SupplyCost ?? 0;
			if (unitSupply > 0 &&
				player.PlayerData.GetUsedSupply() + unitSupply > player.PlayerData.GetMaxSupply())
			{
				return false;
			}

			bool success;
			float costMult = RTS.Core.TechEffects.GetProductionCostMultiplier(player.PlayerData);
			if (costMult < 1f)
			{
				// 模拟线程安全：用纯 C# 字典缩放后走 IReadOnlyDictionary 重载
				var scaled = new System.Collections.Generic.Dictionary<ResourceType, float>();
				foreach (var kvp in Costs)
					scaled[kvp.Key] = kvp.Value * costMult;
				success = player.PlayerData.TryConsumeResources(scaled);
			}
			else
			{
				success = player.PlayerData.TryConsumeResources(Costs);
			}
			return success;
		}

		public override bool CanExecute()
		{
			// 建造中的建筑不能训练单位
			if (_unit is Structure s && s.CurrentState != Structure.StructureState.Completed)
				return false;

			var player = Game.GetPlayerByTeam(_unit.TeamID);
			var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName);
			int unitSupply = unitCfg?.SupplyCost ?? 0;

			if (unitCfg != null && player?.PlayerData != null)
			{
				foreach (string req in unitCfg.RequiredTechIds)
				{
					if (!player.PlayerData.HasTech(req))
						return false;
				}
			}

			if (player?.PlayerData != null &&
				unitSupply > 0 &&
				player.PlayerData.GetUsedSupply() + unitSupply > player.PlayerData.GetMaxSupply())
			{
				return false;
			}

			return true;
		}

		public override void OnEnter()
		{
			base.OnEnter();
			_timerFP = FP.Zero;

			// 一体铸造等科技：生产时间乘配置倍率
			var player = Game.GetPlayerByTeam(_unit.TeamID);
			_buildTimeFP = (FP)BuildTime * (FP)RTS.Core.TechEffects.GetProductionTimeMultiplier(player?.PlayerData);
		}

		public override void OnUpdate(double delta)
		{
			// 使用 SimWorld 固定步长累加；禁止直接使用渲染 delta，否则会脱步
			_timerFP += SimManager.Instance.World.FixedDelta;

			if (_timerFP < _buildTimeFP)
				return;

			// 已建造完成：人口已满时保持完成状态等待空位（不退款、不消失），
			// 仍占用生产槽位，队列里的其他单位继续被阻塞。
			_timerFP = _buildTimeFP;

			if (!HasPopulationSpace())
				return;

			SpawnUnit();
			Finish();
		}

		private void SpawnUnit()
		{
			if (EntitySpawner.Instance == null || _unit.LogicEntity == null) return;

			// 出生前再查一次人口（等待期间可能仍被占满）：不足则继续等待，不退款
			if (!HasPopulationSpace())
				return;

			// 1. 出生位置：有集结点则朝集结点方向出生，否则用确定性随机角度
			FPVector2 centerPos = _unit.LogicEntity.Position;
			FP offsetDist = (FP)120m;
			FPVector2 spawnPosFP;
			var spawnStruct = _unit as Structure;
			FPVector2 spawnDir = FPVector2.Zero;

			if (spawnStruct != null && spawnStruct.RallyQueue.Count > 0)
			{
				FPVector2 d = spawnStruct.RallyQueue[0].TargetPos - centerPos;
				if (d.X != FP.Zero || d.Y != FP.Zero)
					spawnDir = d.Normalized();

				// 按建筑边长让开出生点：半宽 + 40
				offsetDist = (FP)(Math.Max(spawnStruct.GridSize * 64 / 2, 96) + 40);
			}

			if (spawnDir.X == FP.Zero && spawnDir.Y == FP.Zero)
			{
				// 纯逻辑层定点数随机角度 (0 ~ 2π)，双端一致
				FP randomAngle = SimManager.Instance.World.RNG.NextFP() * FP.Pi * (FP)2m;
				spawnDir = new FPVector2(-FP.Sin(randomAngle), FP.Cos(randomAngle));
			}

			spawnPosFP = centerPos + spawnDir * offsetDist;

			// 生成（含视觉节点）与集结点指令延迟到主线程执行；
			// 出生位置/人口/RNG 已在上面用确定性数据算好。
			string spawnUnitName = UnitName;
			int spawnTeam = _unit.TeamID;
			float spawnX = (float)spawnPosFP.X;
			float spawnY = (float)spawnPosFP.Y;
			var spawnStructRef = spawnStruct;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (EntitySpawner.Instance == null)
					return;

				IEntity newEntity = EntitySpawner.Instance.SpawnEntity(
					spawnUnitName,
					spawnTeam,
					new RTS.Simulation.FPVector2((FP)spawnX, (FP)spawnY));

				if (newEntity is Unit newUnit && spawnStructRef != null && GodotObject.IsInstanceValid(spawnStructRef))
				{
					var queue = spawnStructRef.RallyQueue;
					if (queue == null || queue.Count == 0)
						return;

					for (int i = 0; i < queue.Count; i++)
					{
						OrderData order = queue[i];
						bool isQueue = (i > 0);
						newUnit.Brain.StartAction(order.ActionName, order.TargetPos, order.TargetObj, false, isQueue);
					}
				}
			});
		}

		// 当前人口是否还能容纳本单位的 SupplyCost
		private bool HasPopulationSpace()
		{
			var player = Game.GetPlayerByTeam(_unit.TeamID);
			int supply = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName)?.SupplyCost ?? 0;

			if (player?.PlayerData == null || supply <= 0)
				return true;

			return player.PlayerData.GetUsedSupply() + supply <= player.PlayerData.GetMaxSupply();
		}
	}
}
