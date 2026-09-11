// File: res://Scripts/Units/Actions/Implementations/RebuildWreckageAction.cs
using Godot;
using RTS.Core;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 洞穴族：基于地下残骸快速重建原建筑（重建耗时 = 原建造时间 / 10）。
	// 残骸血量 = 原建筑 10 倍，工人走到残骸旁自动施工，完工后原建筑直接落成。
	[GlobalClass]
	public partial class RebuildWreckageAction : UnitAction
	{
		[Export] public string DisplayNameText = "重建";
		private Structure _targetWreckage;

		public override void _Ready()
		{
			ActionName = "RebuildWreckage";
			Layer = ActionLayer.Movement | ActionLayer.Weapon;
			Queueable = false;
		}

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity target)
		{
			_targetWreckage = target as Structure;
		}

		public override void OnUpdate(double delta)
		{
			if (_targetWreckage == null || _targetWreckage.IsDeadOrNull() ||
				_unit?.LogicEntity is not SimUnit simUnit)
			{
				Finish();
				return;
			}

			var sim = _targetWreckage.SimStructureData;
			if (sim == null || sim.IsDead || string.IsNullOrEmpty(sim.RebuildTargetId))
			{
				Finish();
				return;
			}

			var origCfg = ConfigDatabase.GetStructure(sim.RebuildTargetId);
			if (origCfg == null)
			{
				Finish();
				return;
			}

			FP radius = (FP)((_targetWreckage.GridSize * 64.0f) / 2.0f);
			if (!FPVector2.IsWithinRange(simUnit.Position, sim.Position, radius + (FP)192m))
			{
				simUnit.CommandMove(sim.Position, sim.ID);
				return;
			}

			simUnit.HasTarget = false;
			simUnit.Velocity = FPVector2.Zero;

			float rebuildTime = Mathf.Max(1f, origCfg.BuildTime / 10f);
			sim.RebuildProgress += (FP)SimManager.Instance.World.FixedDelta / (FP)rebuildTime;

			if (sim.RebuildProgress < FP.One)
				return;

			// 重建完成：生成原建筑（直接完工）并移除残骸（模拟层确定性处理）
			string spawnId = sim.RebuildTargetId;
			int spawnTeam = sim.TeamID;
			float spawnX = (float)sim.Position.X;
			float spawnY = (float)sim.Position.Y;
			sim.IsDead = true;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				var ent = EntitySpawner.Instance?.SpawnEntity(
					spawnId, spawnTeam, new FPVector2((FP)spawnX, (FP)spawnY));
				if (ent is RTS.Units.Structure rebuilt)
				{
					rebuilt.InitAsBlueprint(new Godot.Collections.Dictionary<ResourceType, float>());
					rebuilt.PromoteFromBlueprint();
					rebuilt.AdvanceProgress(1f);
					rebuilt.LifeModule?.SetHealthRaw(rebuilt.LifeModule.MaxHp);
				}

				if (GodotObject.IsInstanceValid(_targetWreckage))
					_targetWreckage.QueueFree();
			});
			Finish();
		}
	}
}
