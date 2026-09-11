// File: res://Scripts/Units/Actions/Implementations/BuildAction.cs
using Godot;
using RTS.Data;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	[GlobalClass]
	public partial class BuildAction : UnitAction
	{
		[ExportGroup("Settings")]
		[Export] public string StructureName = "Barracks";
		[Export] public Godot.Collections.Dictionary<ResourceType, float> Costs { get; set; } = new();
		[Export] public int GridSize = 2;
		[Export] public CreepType RequiredCreep = CreepType.Any;
		[Export] public float BuildTime = 5.0f;
		[Export] public float BuildRange = 80.0f;

		private Structure _targetStruct;
		private int _buildRangeTilesCache = -1;
		public IEntity TargetStructure => _targetStruct;

		public override void Initialize(IEntity owner)
		{
			base.Initialize(owner);
			// 主线程一次性缓存建造距离（模拟线程禁止 GetNodeOrNull）
			_buildRangeTilesCache = 0;
			if (owner is Unit bu &&
				bu.GetNodeOrNull<WorkerBuildBehavior>("BuildBehavior") is { } wb &&
				wb.BuildRangeTiles > 0)
				_buildRangeTilesCache = wb.BuildRangeTiles;
		}

		public override void _Ready()
		{
			ActionName = "Build_" + StructureName;
			Layer = ActionLayer.Movement | ActionLayer.Weapon;
			Queueable = true;

			// 数值全部以配置表为准：场景预制体里的 Costs / GridSize / BuildTime 会被这里覆盖
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
			if (cfg != null)
			{
				Costs = cfg.Costs;
				GridSize = cfg.GridWidth;
				BuildTime = cfg.BuildTime;
				RequiredCreep = cfg.RequiredCreepType;
			}
		}

		public override bool CanExecute()
		{
			if (_unit == null)
				return false;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
			if (cfg == null)
				return true;

			var player = RTS.World.Game.GetPlayerByTeam(_unit.TeamID);
			if (player?.PlayerData == null)
				return false;

			// 唯一建筑：已拥有则禁止再建
			if (cfg.IsUnique)
			{
				var world = RTS.Core.SimManager.Instance?.World;
				if (world != null)
				{
					foreach (var sim in world.Structures.Values)
					{
						// 只算“已建成”的，蓝图/施工中的自己不能拦自己的建造指令
						if (!sim.IsDead && sim.TeamID == _unit.TeamID &&
							sim.StructureTypeId == StructureName &&
							sim.CurrentState == RTS.Simulation.SimStructure.StructureState.Active)
							return false;
					}
				}
			}

			// 占人口建筑：人口不够不能建（恶魔）。
			// 蓝图建筑豁免：多足牧羊人蓝图自身占人口但产出后提供人口，
			// 满人口时禁止放置会让多足永远卡死在人口上限。
			if (cfg.SupplyUsed > 0 && !StructureName.StartsWith("Blueprint_"))
			{
				int usedSupply = player.PlayerData.GetUsedSupply();
				int maxSupply = player.PlayerData.GetMaxSupply();

				if (usedSupply + cfg.SupplyUsed > maxSupply)
					return false;
			}

			if (cfg.RequiredTechIds.Count == 0)
				return true;

			foreach (string req in cfg.RequiredTechIds)
			{
				if (!player.PlayerData.HasTech(req))
					return false;
			}

			return true;
		}

		// 悬浮提示：列出未满足的建造前置条件
		public string GetRequirementTooltip()
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
			if (cfg == null || cfg.RequiredTechIds.Count == 0)
				return "";

			var names = new System.Collections.Generic.List<string>();

			foreach (string req in cfg.RequiredTechIds)
			{
				var tech = RTS.Data.Configs.ConfigDatabase.GetTech(req);
				names.Add(RTS.Settings.Localization.TrName(req));
			}

			return RTS.Settings.Localization.Tr("build.requires", string.Join(RTS.Settings.Localization.Tr("list_sep"), names));
		}

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity target) => _targetStruct = target as Structure;
		public override void OnEnter()
		{
			base.OnEnter();
			if (_targetStruct == null || _targetStruct.IsDeadOrNull()) Finish();
			// 秒建时蓝图已在派发瞬间完工，工人不再走过去
			else if (_targetStruct.CurrentState == Structure.StructureState.Completed) Finish();
		}

		public override void OnUpdate(double delta)
		{
			if (_targetStruct == null || _targetStruct.IsDeadOrNull() || _unit.LogicEntity is not SimUnit simUnit)
			{
				Finish();
				return;
			}

			// 等待蓝图的定点数实体生成完毕
			if (_targetStruct.LogicEntity == null)
				return;

			if (_targetStruct.IsUnderConstruction && _targetStruct.HasBuilder(_unit))
			{
				ConstructTick(delta);
			}
			else
			{
				MoveToTarget(simUnit, delta);
			}
		}

		private void MoveToTarget(SimUnit simUnit, double delta)
		{
			FP radius = (FP)((_targetStruct.GridSize * 64.0f) / 2.0f);
			// 建造距离以单位身上的建造行为模块配置为准（WorkerBuildBehavior.BuildRangeTiles）
			FP buildRangeFP = (FP)BuildRange;
			if (_buildRangeTilesCache > 0)
				buildRangeFP = (FP)(_buildRangeTilesCache * 64);
			// 施工判定圈放宽 1 格：工人尽量贴近蓝图边缘施工，
			// 又保留寻路终点不可达时的小余量
			FP safeDist = buildRangeFP + radius + (FP)64m;

			FPVector2 targetPos = _targetStruct.LogicEntity.Position;
			FP distSq = FPVector2.DistanceSquared(simUnit.Position, targetPos);

			if (distSq <= safeDist * safeDist)
			{
				simUnit.HasTarget = false;
				simUnit.Velocity = FPVector2.Zero;
				TryStartBuilding();
			}
			else
			{
				FPVector2 dir = (targetPos - simUnit.Position).Normalized();
				FP approachOffset = buildRangeFP * (FP)0.5m;
				if (approachOffset < (FP)16m)
					approachOffset = (FP)16m;
				FPVector2 approach = dir.X == FP.Zero && dir.Y == FP.Zero
					? targetPos
					: targetPos - dir * (radius + approachOffset);
				// 修复：目标点若落在蓝图占用格内，寻路终点不可达 → 工人原地站桩。
				// 绕开：改选一个蓝图侧面的可走点（距中心 radius+32 ≈ 128 < safeDist，
				// 工人走到即触发施工；终点不压蓝图格，寻路可达）。
				var bp = _targetStruct.GridPosition;
				int bpSize = _targetStruct.GridSize;
				var worldGrid = RTS.Core.SimManager.Instance.World.Grid;
				var ag = worldGrid.WorldToGrid(approach);
				bool pressed = ag.X >= bp.X && ag.X < bp.X + bpSize &&
					ag.Y >= bp.Y && ag.Y < bp.Y + bpSize;
				if (pressed)
				{
					FP sideLen = radius + approachOffset;
					FPVector2[] sides =
					{
						new FPVector2(targetPos.X, targetPos.Y - sideLen),
						new FPVector2(targetPos.X, targetPos.Y + sideLen),
						new FPVector2(targetPos.X - sideLen, targetPos.Y),
						new FPVector2(targetPos.X + sideLen, targetPos.Y)
					};
					FPVector2 bestSide = sides[0];
					FP bestDist = FP.MaxValue;
					foreach (var sp in sides)
					{
						var sg = worldGrid.WorldToGrid(sp);
						if (sg.X >= bp.X && sg.X < bp.X + bpSize &&
							sg.Y >= bp.Y && sg.Y < bp.Y + bpSize)
							continue;
						FP d = FPVector2.DistanceSquared(sp, simUnit.Position);
						if (d < bestDist)
						{
							bestDist = d;
							bestSide = sp;
						}
					}
					simUnit.CommandMove(bestSide, _targetStruct.LogicEntity.ID);
					return;
				}
				simUnit.CommandMove(approach, _targetStruct.LogicEntity.ID);
			}
		}

		private void TryStartBuilding()
		{
			if (_targetStruct.CurrentState == Structure.StructureState.Blueprint)
			{
				// 施工闸门：蓝图占地内还有单位（含正在被强制移出的自己人）时不许开工。
				// 单位会由 SimWorld.TickBlueprintEvictions 强制插队移出，这里只是等待。
				if (_targetStruct.LogicEntity is RTS.Simulation.SimStructure simBlueprint &&
					RTS.Core.SimManager.Instance?.World is RTS.Simulation.SimWorld simWorld &&
					!simWorld.IsFootprintClearOfUnits(simBlueprint))
				{
					return;
				}

				if (_targetStruct.PromoteFromBlueprint())
				{
					var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);

					if (cfg != null && cfg.RequiresSacrifice)
					{
						// 献祭：怨灵走到蓝图旁自我删除，建筑转为实体并自动施工
						if (_unit is Unit worker)
							worker.StartDeathVisual();

						Finish();
						return;
					}

					if (_targetStruct.CanAddBuilder())
					{
						_targetStruct.AddBuilder(_unit);
						if (_unit.LogicEntity is SimUnit builderSim)
							builderSim.IsConstructing = true;
					}
					else
						Finish();
				}
				else
				{
					// 蓝图可能已被另一名工人转施工或被看门狗取消（良性竞争），
					// Promote 失败路径已退款，这里不再刷错误日志。
					if (GodotObject.IsInstanceValid(_targetStruct))
						GD.Print($"[Build] 蓝图转施工让位 {StructureName} ({_targetStruct.CurrentState})");
					Finish();
				}
			}
			else if (_targetStruct.IsUnderConstruction && _targetStruct.CanAddBuilder())
			{
				_targetStruct.AddBuilder(_unit);
			}
			else if (!_targetStruct.IsUnderConstruction)
			{
				Finish();
			}
		}

		private void ConstructTick(double delta)
		{
			if (!_targetStruct.IsUnderConstruction) { Finish(); return; }
			float fixedDelta = (float)RTS.Core.SimManager.Instance.World.FixedDelta;
			float progress = fixedDelta / BuildTime;

			_targetStruct.AdvanceProgress(progress);

			float healAmount = (_targetStruct.LifeModule.MaxHp * 0.9f) * progress;
			_targetStruct.LifeModule?.Heal(healAmount);

		}

		public override void OnExit()
		{
			if (_unit?.LogicEntity is SimUnit exitSim)
				exitSim.IsConstructing = false;

			if (_targetStruct != null && GodotObject.IsInstanceValid(_targetStruct))
				_targetStruct.RemoveBuilder(_unit);
			if (_unit.LogicEntity is SimUnit simUnit) simUnit.HasTarget = false;
			base.OnExit();
		}

		public Vector2 GetTargetPos() => (_targetStruct as IEntity)?.GlobalPosition ?? Vector2.Zero;

		public override long GetDeterministicExtraHash()
		{
			return _targetStruct?.LogicEntity?.ID ?? -1;
		}
	}
}
