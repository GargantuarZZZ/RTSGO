using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;

namespace RTS.Actions.Implementation
{
	/// <summary>
	/// 技能动作公因式提取：
	/// 十几个“Ability 层 + 不可排队 + _Ready 设 ActionName”的技能动作（潜地/吞噬/叶犬/
	/// 共振波/地震波/虫洞/架设/驻扎/切换弹种/兴奋剂/雷达/轨道炮…）共享同一套样板，
	/// 全部收敛到 AbilityActionBase。子类只声明 ActionId，再按需覆盖 CanExecute/OnEnter 等。
	/// </summary>
	public abstract partial class AbilityActionBase : UnitAction
	{
		/// <summary>锁步指令 ID（ActionName），基类在 _Ready 里赋值。</summary>
		protected abstract string ActionId { get; }

		/// <summary>占用层：默认只占 Ability；架设等需要额外打断移动时覆盖。</summary>
		protected virtual ActionLayer Blocking => ActionLayer.Ability;

		public override void _Ready()
		{
			ActionName = ActionId;
			Layer = ActionLayer.Ability;
			BlockingLayers = Blocking;
			Queueable = false;
		}

		// StartAction 用 TryPayCost 做执行闸门：返回 false 会让通用派发（架设/驻扎/兴奋剂等）
		// 直接放弃执行（按钮亮着但点了没反应）。技能费用都在各自模拟处理器里结算，
		// 这里应返回 true 表示无前置扣费、放行执行。
		public override bool TryPayCost() => true;
	}

	/// <summary>目标必须是存活单位（单位技能：潜地、吞噬、架设、驻扎、兴奋剂、切换弹种等）。</summary>
	public abstract partial class AliveUnitAbilityAction : AbilityActionBase
	{
		public override bool CanExecute()
		{
			return _unit?.LogicEntity is SimUnit;
		}
	}

	/// <summary>目标必须是已完工建筑（建筑技能：全能叶犬、共振波、地震波、制造虫洞、切换模式等）。</summary>
	public abstract partial class CompletedStructureAbilityAction : AbilityActionBase
	{
		public override bool CanExecute()
		{
			return !(_unit is Structure s && s.CurrentState != Structure.StructureState.Completed);
		}
	}

	/// <summary>
	/// 能量消耗技能（轨道控制中心：雷达 / 轨道炮）：
	/// Initialize 时从建筑配置表读能量成本，CanExecute = 建筑完工 + 能量足够。
	/// 子类只提供 ActionId 与 GetEnergyCost。
	/// </summary>
	public abstract partial class EnergyCostAbilityAction : CompletedStructureAbilityAction
	{
		private Godot.Collections.Dictionary<ResourceType, float> _costs = new();

		protected abstract float GetEnergyCost(StructureConfig cfg);

		public override void Initialize(IEntity unit)
		{
			base.Initialize(unit);

			var cfg = ConfigDatabase.GetStructure(unit?.DisplayName ?? "");
			if (cfg != null && GetEnergyCost(cfg) > 0f)
			{
				_costs = new Godot.Collections.Dictionary<ResourceType, float>
				{
					{ ResourceType.Energy, GetEnergyCost(cfg) }
				};
			}
		}

		public override bool CanExecute()
		{
			if (_unit == null)
				return false;

			if (_unit is Structure s && s.CurrentState != Structure.StructureState.Completed)
				return false;

			return RTS.World.Game.GetPlayerByTeam(_unit.TeamID)?.PlayerData?.HasResources(_costs) == true;
		}
	}
}
