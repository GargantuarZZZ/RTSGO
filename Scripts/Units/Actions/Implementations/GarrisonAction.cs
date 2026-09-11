using Godot;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	// 泰伦工程兵驻扎藻类工厂：进入建筑提供肉，建筑被摧毁时回到战场
	[GlobalClass]
	public partial class GarrisonAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "驻扎";
		public IEntity Target { get; private set; }
		protected override string ActionId => "Garrison";

		public override void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj)
		{
			Target = targetObj;
		}

		public override void OnEnter()
		{
			base.OnEnter();
		}

		// 每 tick：先走向建筑（用逻辑层移动，与 MoveAction 一致），
		// 到建筑边缘（中心距离 ≤ 128 + 半宽）后进驻。
		// 原实现只在 OnEnter 判一次“中心 2 格内”，单位右键后不会自己走过去，
		// 且大建筑会“卡建筑盒”永远进不去。
		public override void OnUpdate(double delta)
		{
			if (Target is not Structure st || _unit?.LogicEntity is not SimUnit su ||
				st.SimStructureData == null)
			{
				Finish();
				return;
			}

			if (st.TeamID != su.TeamID)
			{
				Finish();
				return;
			}

			var cfg = ConfigDatabase.GetStructure(st.StructureName);
			if (cfg == null || cfg.GarrisonCapacity <= 0 ||
				st.SimStructureData.GarrisonedCount >= cfg.GarrisonCapacity)
			{
				Finish();
				return;
			}

			// 半宽（格）：中心到建筑边缘的距离，避免“卡建筑盒”判定
			int half = System.Math.Max(st.SimStructureData.GridWidth, st.SimStructureData.GridHeight) * 32;
			FP reach = (FP)(128 + half);
			if (!RTS.Simulation.FPVector2.IsWithinRange(
					su.Position, st.SimStructureData.Position, reach))
			{
				su.CommandMove(st.SimStructureData.Position);
				return;
			}

			su.HasTarget = false;
			RTS.Core.SimManager.Instance?.QueueGarrison(su.ID, st.SimStructureData.ID);
			Finish();
		}
	}
}
