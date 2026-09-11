using Godot;
using RTS.Data;

namespace RTS.Units
{
	[GlobalClass]
	public partial class ResourceModule : EntityModuleBase
	{
		[Signal] public delegate void DepletedEventHandler();
		[Signal] public delegate void AmountChangedEventHandler(float current, float max);

		[Export] public ResourceType ResType { get; set; } = ResourceType.Money;
		[Export] public float MaxAmount { get; set; } = 1500.0f;

		private bool _statsInjected = false;
		private bool _hasEmittedDepleted = false;

		// 实时读取逻辑层剩余资源
		public float CurrentAmount => _owner?.LogicEntity != null ? (float)_owner.LogicEntity.ResourceAmount : MaxAmount;

	public override void Initialize(IEntity owner)
	{
		base.Initialize(owner);
		if (_owner?.LogicEntity != null)
			{
				_owner.LogicEntity.ResourceAmount = (FixMath.NET.Fix64)MaxAmount;
				_owner.LogicEntity.ResourceType = (int)ResType;
				_statsInjected = true;
			}
		}

		private void EnsureStatsInjected()
		{
			if (_statsInjected || _owner?.LogicEntity == null) return;
			_owner.LogicEntity.ResourceAmount = (FixMath.NET.Fix64)MaxAmount;
			_owner.LogicEntity.ResourceType = (int)ResType;
			_statsInjected = true;
		}

	public override void Tick(double delta)
		{
			if (_owner?.LogicEntity == null) return;
			EnsureStatsInjected();

			// 模拟线程禁止发 Godot 信号：事件入队，主线程统一派发
			RTS.Core.SimEventQueue.Enqueue(new RTS.Core.SimEvent(
				RTS.Core.SimEventType.ResourceAmountChanged,
				_owner.LogicEntity.ID,
				CurrentAmount,
				MaxAmount));

			if (_owner.LogicEntity.ResourceAmount <= FixMath.NET.Fix64.Zero && !_hasEmittedDepleted)
			{
				_hasEmittedDepleted = true;
				RTS.Core.SimEventQueue.Enqueue(new RTS.Core.SimEvent(
					RTS.Core.SimEventType.ResourceDepleted,
					_owner.LogicEntity.ID));
			}
		}

		/// <summary>主线程派发：还原原信号。</summary>
		public void NotifyAmountChanged(float current, float max)
		{
			EmitSignal(SignalName.AmountChanged, current, max);
		}

		/// <summary>主线程派发：还原 Depleted 信号。</summary>
		public void NotifyDepleted()
		{
			EmitSignal(SignalName.Depleted);
		}
	}
}
