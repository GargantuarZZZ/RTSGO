using Godot;
using RTS.Data;
using RTS.Simulation;

namespace RTS.Actions.Implementation
{
	// 轨道控制中心 - 资源交换：消耗一份资源+能量，立即获得另一份资源（数值来自建筑配置表）
	[GlobalClass]
	public partial class ResourceExchangeAction : AbilityActionBase
	{
		[Export] public Godot.Collections.Dictionary<ResourceType, float> Costs { get; set; } = new();
		[Export] public Godot.Collections.Dictionary<ResourceType, float> Reward { get; set; } = new();
		[Export] public string DisplayNameText = "";
		// 资源交换按钮用场景节点名作为指令 ID（金属交换 / 气体交换各一个实例）
		protected override string ActionId => Name;

		public override bool CanExecute()
		{
			return RTS.World.Game.GetPlayerByTeam(_unit?.TeamID ?? -1)?.PlayerData?.HasResources(Costs) == true;
		}

		public override bool TryPayCost()
		{
			// 不在 TryPayCost 扣费：CanExecute 还要在扣费后检查一次资源
			return true;
		}

		public override void OnEnter()
		{
			base.OnEnter();

			var pd = RTS.World.Game.GetPlayerByTeam(_unit?.TeamID ?? -1)?.PlayerData;
			if (pd != null && pd.TryConsumeResources(Costs))
				pd.AddResources(Reward);

			Finish();
		}
	}
}
