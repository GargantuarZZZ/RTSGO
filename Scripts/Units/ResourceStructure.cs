using Godot;
using RTS.Data;
using RTS.Simulation;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	[GlobalClass]
	public partial class ResourceStructure : Structure
	{
		[Export] public ResourceModule ResModule;

		public override void _Ready()
		{
			// 1. 加入组，确保 WorldScanner 和渲染层能识别
			AddToGroup("entities");
			AddToGroup("structures");
			// 中立资源点全图可见（不参与点亮视野）
			SetMeta("GlobalVision", true);

			// 2. 初始化模块引用 (ResModule, Visuals 等)
			InitializeModules();

			// 3. 设置正确属性并调用基类注册
			// 确保 GridSize 为 1 且 TeamID 为 -1（中立）
			TeamID = -1;
			GridSize = 1;

			// 调用基类方法：这会自动计算 GridPosition，实现对齐，并注入到 SimWorld
			SnapAndRegister();

			// 4. 手动初始化资源模块，完成立即注入
			ResModule?.Initialize(this);
		}

		public override bool IsDeadOrNull()
		{
			if (!GodotObject.IsInstanceValid(this)) return true;
			// 只有 LogicEntity 存在且资源量大于 0 时，才允许被选中
			return LogicEntity == null || LogicEntity.ResourceAmount <= FP.Zero;
		}

		protected override void InitializeModules()
		{
			base.InitializeModules();
			if (ResModule != null)
			{
				// Keep the callback in managed code; no StringName lookup on the dying node.
				ResModule.Depleted += () => OnResourceDepleted();
			}
		}

		private void OnResourceDepleted()
		{
			OnDestroyed();
		}
	}
}
