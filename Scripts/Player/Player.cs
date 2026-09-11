using Godot;
using RTS.Data;

namespace RTS.Core
{
	public partial class Player : Node2D
	{
		// 建议：TeamId 保持为 int，但在初始化时确保能被 Main 识别
		[Export] public int TeamId { get; set; } = 0;

		public RaceData Race { get; private set; }
		public PlayerData PlayerData { get; private set; } // 改名与场景树一致
		public PlayerController Controller { get; private set; }

		public override void _Ready()
		{
			// 1. 按照你截图的层级获取子节点
			Race = GetNodeOrNull<RaceData>("Race");
			PlayerData = GetNodeOrNull<PlayerData>("PlayerData");
			Controller = GetNodeOrNull<PlayerController>("PlayerController");

			// 2. 核心初始化逻辑
			if (Race != null && PlayerData != null)
			{
				PlayerData.Initialize(Race);

				// 移除原有的 UI 初始化代码，让 UserUI 通过 _Process 轮询这里的数据
				GD.Print($"[Player] {Name} (Team:{TeamId}) 初始化成功，种族: {Race.RaceName}");
			}
			else
			{
				GD.PrintErr($"[Player] {Name} 初始化失败：确保场景树中有 Race 和 PlayerData 节点");
			}
		}
	}
}
