// File: res://Scripts/Units/Actions/Implementations/HarvestAction.cs
using Godot;
using RTS.Data;
using RTS.Simulation;
using RTS.Units;
using RTS.World;
using RTS.Core;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	[GlobalClass]
	public partial class HarvestAction : UnitAction
	{
		[Export] public float HarvestPower = 5.0f;
		// 采集到达距离（格）：0 = 默认手臂距离（15 世界单位）；远程建造型由工厂设为建造距离
		[Export(PropertyHint.Range, "0,99,1")]
		public int HarvestRangeTiles = 0;
		private enum HarvestState { ToSource, Harvesting, ToBase, Delivering }
		private HarvestState _internalState = HarvestState.ToSource;

		// 是否正在装载（真正在采）：采矿特效只在此时显示
		public bool IsHarvesting => _internalState == HarvestState.Harvesting;
		// 本帧是否真的采到了资源（矿空/装不下时也不算“正在挖”）
		public bool HarvestedThisTick { get; private set; }
		private FP _harvestPowerFP = (FP)5m;

		public IEntity Source { get; private set; }
		public IEntity SourceEntity => Source;
		private IEntity _dropOffTarget;

		// 使用纯边缘距离，无需再加半个建筑半径；
		// 手臂长度取一个合理值，边/角均按此交互
		private FP _interactRange = (FP)15m;

		public override void _Ready()
		{
			ActionName = "Harvest";
			Layer = ActionLayer.Movement | ActionLayer.Body;
			Queueable = true;
			_harvestPowerFP = (FP)HarvestPower;
			if (HarvestRangeTiles > 0)
				_interactRange = (FP)(HarvestRangeTiles * 64);
		}

		public override void Initialize(IEntity owner)
		{
			base.Initialize(owner);

			// 远程建造型（多足 Builder）：采集距离 = 建造距离（从建造行为模块一次性复制参数）
			if (owner is Unit u &&
				u.GetNodeOrNull<WorkerBuildBehavior>("BuildBehavior") is { } wb &&
				wb.BuildRangeTiles > 1)
				_interactRange = (FP)(wb.BuildRangeTiles * 64);
		}

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj) {
	if (targetObj is ResourceStructure resource) SetSource(resource);
	else if (targetObj == null && Source != null) _internalState = HarvestState.ToSource;
}

		public void SetSource(IEntity source)
		{
			Source = source;
			_dropOffTarget = null;
			_internalState = HarvestState.ToSource;
		}

		public override void OnEnter()
		{
			base.OnEnter();
			// 采矿期间忽略碰撞（幽灵），避免被建筑/单位卡住
			if (_unit.LogicEntity is SimUnit simUnit) simUnit.IsGhost = true;
		}

		public override void OnExit()
		{
			base.OnExit();
			// 动作结束/打断时恢复碰撞
			if (_unit.LogicEntity is SimUnit simUnit) simUnit.IsGhost = false;
		}

		public override void OnUpdate(double delta)
		{
			HarvestedThisTick = false;

			if (_unit.IsDeadOrNull() || _unit.HarvestModule == null || _unit.LogicEntity is not SimUnit simUnit)
			{ Finish(); return; }

			switch (_internalState)
			{
				case HarvestState.ToSource:
					if (Source == null || Source.IsDeadOrNull() || Source.LogicEntity == null) { Finish(); return; }

					// 正方形包围盒边缘距离
					FP distToSourceEdgeSq = GetSqrDistanceToEdge(simUnit, Source.LogicEntity);

					if (distToSourceEdgeSq <= _interactRange * _interactRange)
					{
						simUnit.HasTarget = false;
						simUnit.Velocity = FPVector2.Zero;
						_internalState = HarvestState.Harvesting;
						simUnit.CargoType = Source.LogicEntity.ResourceType;
					}
					else
					{
						// A* 寻路，无视目标阻挡
						simUnit.CommandMove(Source.LogicEntity.Position, Source.LogicEntity.ID);
					}
					break;

			case HarvestState.Harvesting:
				if (Source == null || Source.IsDeadOrNull() || Source.LogicEntity == null) { Finish(); return; }
				FP powerFP = _harvestPowerFP * SimManager.Instance.World.FixedDelta;

				// 采集科技倍率（洞穴真菌育种等）
				var harvestPd = RTS.World.Game.GetPlayerByTeam(_unit.TeamID)?.PlayerData;
				if (harvestPd != null)
					powerFP *= (FP)TechEffects.GetHarvesterIncomeMultiplier(harvestPd);

				if (powerFP > Source.LogicEntity.ResourceAmount) powerFP = Source.LogicEntity.ResourceAmount;
					FP spaceLeft = simUnit.CargoCapacity - simUnit.CargoAmount;
					if (powerFP > spaceLeft) powerFP = spaceLeft;

					Source.LogicEntity.ResourceAmount -= powerFP;
					simUnit.CargoAmount += powerFP;
					if (powerFP > FP.Zero)
						HarvestedThisTick = true;

					if (simUnit.CargoAmount >= simUnit.CargoCapacity || Source.LogicEntity.ResourceAmount <= FP.Zero)
					{
						if (IsAutoSubmitHarvest())
						{
							// 自动提交：装满直接到账，不需要交付建筑
							_internalState = HarvestState.Delivering;
						}
						else
						{
							_dropOffTarget = FindNearestDropOff();
							if (_dropOffTarget == null)
							{
								GD.Print($"[Harvest] 无交付点 team={_unit.TeamID} cargoType={simUnit.CargoType} amount={simUnit.CargoAmount} src={Source?.DisplayName}");
								Finish();
								return;
							}
							_internalState = HarvestState.ToBase;
						}
					}
					break;

				case HarvestState.ToBase:
					if (_dropOffTarget == null || _dropOffTarget.IsDeadOrNull() || _dropOffTarget.LogicEntity == null)
					{
						_dropOffTarget = FindNearestDropOff();
						if (_dropOffTarget == null)
						{
							GD.Print($"[Harvest] 交付点失效 team={_unit.TeamID} cargoType={simUnit.CargoType} amount={simUnit.CargoAmount}");
							Finish();
							return;
						}
					}

					// 正方形包围盒边缘距离
					FP distToBaseEdgeSq = GetSqrDistanceToEdge(simUnit, _dropOffTarget.LogicEntity);

					if (distToBaseEdgeSq <= _interactRange * _interactRange)
					{
						simUnit.HasTarget = false;
						simUnit.Velocity = FPVector2.Zero;
						_internalState = HarvestState.Delivering;
					}
					else
					{
						// A* 寻路，无视目标阻挡
						simUnit.CommandMove(_dropOffTarget.LogicEntity.Position, _dropOffTarget.LogicEntity.ID);
					}
					break;

				case HarvestState.Delivering:
					FP deliveryAmount = simUnit.CargoAmount;
					simUnit.CargoAmount = FP.Zero;
					ResourceType type = (ResourceType)simUnit.CargoType;

					var player = Game.GetPlayerByTeam(_unit.TeamID);
					if (player != null && player.PlayerData != null)
					{
						// 纳米种族：所有采集资源统一转化为纳米机器人
						ResourceType addType = player.Race?.RaceName == "Nano"
							? ResourceType.NanoBots
							: type;
						// P1-1：难度=资源倍率，机器人采集交付按倍率放大/缩小
						player.PlayerData.AddResource(
							addType,
							deliveryAmount * (RTS.Core.SimManager.Instance?.GetBotResourceMultiplier(_unit.TeamID) ?? FP.One));
					}

					if (Source != null && !Source.IsDeadOrNull()) _internalState = HarvestState.ToSource;
					else Finish();
					break;
			}
		}

		// 计算点到包围盒（AABB）或圆形的最短边缘距离
		private FP GetSqrDistanceToEdge(SimUnit unit, SimEntity target)
		{
			// 如果目标是方方正正的建筑 (网格系统)
			if (target is SimStructure structEnt)
			{
				FP tileSize = (FP)SimManager.Instance.World.Grid.TileSize;
				FP minX = (FP)(structEnt.GridPosition.X) * tileSize;
				FP minY = (FP)(structEnt.GridPosition.Y) * tileSize;
				FP maxX = minX + (FP)(structEnt.GridWidth) * tileSize;
				FP maxY = minY + (FP)(structEnt.GridHeight) * tileSize;

				// 找出现在单位离这个方块的哪个边最近 (模拟钳制)
				FP clampX = unit.Position.X;
				if (clampX < minX) clampX = minX;
				else if (clampX > maxX) clampX = maxX;

				FP clampY = unit.Position.Y;
				if (clampY < minY) clampY = minY;
				else if (clampY > maxY) clampY = maxY;

				// 算出真正的 X 和 Y 方向上的直线偏差
				FP dx = unit.Position.X - clampX;
				FP dy = unit.Position.Y - clampY;

				// 最后一步：减去小兵自己身体的半径，得到的就是“手臂离墙面”的绝对净距离！
				FP dist = FP.Sqrt(dx * dx + dy * dy) - unit.Radius;
				if (dist < FP.Zero) dist = FP.Zero; // 防止穿模时出现负数

				return dist * dist;
			}
			// 如果目标是其他圆形单位
			else
			{
				FP dx = unit.Position.X - target.Position.X;
				FP dy = unit.Position.Y - target.Position.Y;
				FP distSq = dx * dx + dy * dy;

				FP dist = FP.Sqrt(distSq) - target.Radius - unit.Radius;
				if (dist < FP.Zero) dist = FP.Zero;
				return dist * dist;
			}
		}

		private IEntity FindNearestDropOff()
		{
			if (_unit.LogicEntity == null || SimManager.Instance == null) return null;

			int carryingType = _unit.LogicEntity.CargoType;

			var best = SimManager.Instance.World.FindNearestStructure(
				_unit.LogicEntity.Position,
				FP.MaxValue,
				s =>
				{
					if (s.TeamID != _unit.TeamID)
						return false;

					// O(1) 提取表现层实体，确认已建成、可交付且接受当前资源
					if (SimManager.Instance.FindEntityById(s.ID) is not Structure b ||
						b.CurrentState != Structure.StructureState.Completed ||
						!b.IsResourceDropOff)
						return false;

					return b.AcceptableResources.Contains((ResourceType)carryingType);
				});

			return best != null ? SimManager.Instance.FindEntityById(best.ID) : null;
		}

		private bool IsAutoSubmitHarvest()
		{
			if (_unit is Unit u)
			{
				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitName);
				return cfg?.AutoSubmitHarvest == true;
			}

			return false;
		}

		public override long GetDeterministicExtraHash()
		{
			long hash = (long)_internalState;
			hash ^= Source?.LogicEntity?.ID ?? -1;
			hash ^= _dropOffTarget?.LogicEntity?.ID ?? -1;
			return hash;
		}
	}
}
