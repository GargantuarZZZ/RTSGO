using Godot;

namespace RTS.Data.Configs
{
	/// <summary>
	/// 单项资源消耗配置。
	///
	/// 用途：
	/// - 单位生产消耗
	/// - 建筑建造消耗
	/// - 科技研究消耗
	/// - 技能释放消耗
	///
	/// 注意：
	/// 这是静态配置，不要存运行时资源数量。
	/// </summary>
	[GlobalClass]
	public partial class ResourceCostConfig : Resource
	{
		[Export] public ResourceType Type { get; set; } = ResourceType.Metal;

		[Export(PropertyHint.Range, "0,999999,1")]
		public int Amount { get; set; } = 0;

		public bool IsValid()
		{
			return Amount > 0;
		}

		public ResourceCostConfig CloneCost()
		{
			return new ResourceCostConfig
			{
				Type = Type,
				Amount = Amount
			};
		}

		public override string ToString()
		{
			return $"{Type}: {Amount}";
		}
	}
}
