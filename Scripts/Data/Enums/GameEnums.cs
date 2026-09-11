// 文件路径: res://Scripts/Data/Enums/GameEnums.cs
namespace RTS.Data
{
	// 伤害类型
	public enum DamageType
	{
		Kinetic,    // 动能
		Thermal,    // 热能
		Explosive,  // 爆破
		EM,         // 电磁
		Beam        // 光束
	}

	// 护甲类型
	public enum ArmorType
	{
		Light,      // 轻甲
		Armored,    // 重甲
		Biological, // 生物
		Mechanical, // 机械
		Structure   // 建筑
	}

	// 武器射程类型 (注意：这里名字是 WeaponRangeType)
	public enum WeaponRangeType
	{
		Melee,      // 近战
		Ranged      // 远程
	}
	public enum CreepType
	{
		None = 0,
		PlantCreep = 1,
		DemonCreep = 2,
		NanoCreep = 3,
		Any=99
	}
}
