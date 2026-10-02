using Godot;
using System.Collections.Generic;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Data;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions.Implementation
{
	/// <summary>
	/// 武库鸟 - 集群释放（AI 指挥系统）。
	///
	/// 表格：指定部署点，选择自爆/四轴/全部，以每秒 4 架释放当前库存；
	/// 释放结束后 cd 12 秒。
	///
	/// 与其它面板技能一样，Action 只负责"按钮 + 选点"，真正的释放
	/// 在 SimManager.TickArsenalRelease 里按固定步长确定性投放
	/// （库存计数进状态哈希，主线程改它会变成非确定）。
	///
	/// 继承 AbilityActionBase 而不是 UnitAction：基类提供 ActionId /
	/// Layer / BlockingLayers 的样板与 Setup 形态。
	/// </summary>
	[GlobalClass]
	public partial class ClusterReleaseAction : AliveUnitAbilityAction
	{
		[Export] public string DisplayNameText = "集群释放";

		private FPVector2 _targetPos;
		private bool _hasTarget;

		protected override string ActionId => "ClusterRelease";

		/// <summary>库存为空时置灰（冷却由模拟侧判定）。</summary>
		public override bool CanExecute()
		{
			if (_unit?.LogicEntity is not SimUnit carrier)
				return false;
			return carrier.InventoryKamikaze + carrier.InventoryQuad > 0;
		}

		public override void Setup(FPVector2 targetPos, IEntity targetObj)
		{
			_targetPos = targetPos;
			_hasTarget = true;
		}

		public override void OnEnter()
		{
			base.OnEnter();
			// 实际投放交给模拟层：这里只把"部署点"写进模拟状态
			if (_unit?.LogicEntity is SimUnit carrier && _hasTarget)
			{
				carrier.ClusterReleaseTarget = _targetPos;
				carrier.ClusterReleaseActive = true;
				carrier.ClusterReleaseAccum = FP.Zero;
			}
			Finish();
		}
	}
}
