// 表现层扩展：定点向量 -> Godot 向量（仅供渲染使用，禁止进入逻辑层）
using Godot;
using RTS.Simulation;

namespace RTS.Data
{
	public static class FPVector2GodotExtensions
	{
		public static Vector2 ToGodotVector2(this FPVector2 v)
		{
			return new Vector2((float)v.X, (float)v.Y);
		}
	}
}
