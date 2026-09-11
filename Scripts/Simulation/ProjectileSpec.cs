using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	// 纯逻辑弹体规格：由 WeaponConfig 映射而来。
	// 模拟层不依赖 Godot 配置资源，确定性测试项目可直接编译。
	public struct ProjectileSpec
	{
		public ProjectileMotion Motion;
		public FP Speed;
		public FP HitRadius;
		public FP MaxTravel;
		public FP LobDuration;
		public FP LobHeight;
		public bool FootprintScaledDamage;
		public FP ImpactRadius;
		public FP ImpactDamagePerFootprintSq;
		public FP ImpactSlowMoveMultiplier;
		public FP ImpactSlowAttackMultiplier;
		public FP ImpactSlowSeconds;
	}
}
