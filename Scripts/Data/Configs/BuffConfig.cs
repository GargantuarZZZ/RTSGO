using Godot;

namespace RTS.Data.Configs
{
	// Buff 静态配置：策划在 .tres 里调配数值，
	// 运行时由调用方展开成数值注入纯逻辑 BuffContainer。
	[GlobalClass]
	public partial class BuffConfig : Resource
	{
		[ExportGroup("Identity")]
		[Export] public string BuffId { get; set; } = "";
		[Export] public string DisplayName { get; set; } = "";

		[ExportGroup("Timing")]
		[Export(PropertyHint.Range, "0,999,0.1")]
		public float Duration { get; set; } = 5f;

		[Export(PropertyHint.Range, "1,99,1")]
		public int MaxStacks { get; set; } = 1;

		[ExportGroup("Modifiers")]
		[Export] public float DamageMultiplier { get; set; } = 1f;
		[Export] public float IncomingDamageMultiplier { get; set; } = 1f;
		[Export(PropertyHint.Range, "0,999,0.1")]
		public float ArmorBonus { get; set; } = 0f;
		[Export] public float MoveSpeedMultiplier { get; set; } = 1f;
		[Export(PropertyHint.Range, "0,999,0.1")]
		public float HpRegenPerSecond { get; set; } = 0f;
		[Export(PropertyHint.Range, "0,999,0.1")]
		public float ShieldRegenPerSecond { get; set; } = 0f;

		// 预留自定义空间：后续可实现“每 Tick 回调”等特殊效果
		[Export] public string OnTickEffectId { get; set; } = "";

		public bool IsValidConfig() => !string.IsNullOrEmpty(BuffId);
	}
}
