using System;

namespace RTS.Actions
{
	[Flags] // 关键：允许通过 | 运算符进行组合
	public enum ActionLayer
	{
		None = 0,

		// --- 基础生理层 ---
		Movement    = 1 << 0,  // 腿/引擎：移动、巡逻
		Weapon      = 1 << 1,  // 武器/炮塔：攻击、修理、采集

		// --- 技能与状态层 ---
		Ability     = 1 << 2,  // 主动技能：大和炮、施法、兴奋剂
		Status      = 1 << 3,  // 状态：护盾、Buff（通常不互斥）
		Transformation = 1 << 4, // 变形：架起坦克、起飞（通常锁全身）

		// --- 生产层 ---
		Production  = 1 << 5,  // 内部工厂：造兵、研究（完全独立于身体动作）

		// --- 预留扩展层 ---
		Custom1     = 1 << 6,  // 备用：比如“乘客槽位”
		Custom2     = 1 << 7,  // 备用
		Custom3     = 1 << 8,  // 备用

		// --- 辅助快捷方式 ---
		// 身体部分（通常用于被晕眩时锁死）
		Body        = Movement | Weapon | Ability | Transformation,
		// 全选
		All         = ~0
	}
}
