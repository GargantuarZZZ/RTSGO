// File: res://Scripts/Simulation/SimRandom.cs
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	public class SimRandom
	{
		private uint _state;

		// 只读暴露内部状态，用于世界哈希（不推进随机序列）
		public uint State => _state;

		public SimRandom(uint seed)
		{
			_state = seed;
		}

		// 生成下一个随机整数
		public uint Next()
		{
			// 经典的 LCG 算法参数
			_state = _state * 1664525u + 1013904223u;
			return _state;
		}

		// 生成 [0,1) 定点数
		public FP NextFP()
		{
			// 取模 10000 得到 0~9999，再除以 10000 得到 0~0.9999
			return (FP)(Next() % 10000) / (FP)10000m;
		}
	}
}
