// 仅供确定性测试编译使用：真实游戏里 SimManager 是 Godot Autoload 单例。
namespace RTS.Core
{
	public class SimManager
	{
		public static SimManager Instance { get; } = new SimManager();

		public RTS.Simulation.SimWorld World { get; } = new RTS.Simulation.SimWorld();
		public readonly object WorldLock = new();
	}
}
