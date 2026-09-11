using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	// SimManager 领域拆分：主文件保留核心编排（Tick 顺序/指令分发/哈希），
	// 各领域系统按文件组织，后续方法按 Skills / Economy / RaceSystems 迁入。
	public partial class SimManager
	{
		// 无来源直接扣血（恶魔立场/献祭等持续伤害用，不经过攻防管线）
		private static void DirectDamage(SimEntity ent, FP amount)
		{
			if (ent == null || ent.IsDead || amount <= FP.Zero)
				return;

			ent.Hp -= amount;
			if (ent.Hp <= FP.Zero)
			{
				ent.Hp = FP.Zero;
				ent.IsDead = true;
			}
		}
	}
}
