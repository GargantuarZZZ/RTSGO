using Godot;
using RTS.Data;

namespace RTS.Units
{
	[GlobalClass]
	public partial class UnitHarvest : EntityModuleBase
	{
		[ExportGroup("Settings")]
		[Export] public float HarvestPower = 5.0f;
		[Export] public float MaxCapacity = 10.0f;

		private bool _statsInjected = false;

		// 直接读取逻辑层数据
		public float CurrentAmount => _owner?.LogicEntity != null ? (float)_owner.LogicEntity.CargoAmount : 0f;
		public ResourceType CurrentType => _owner?.LogicEntity != null ? (ResourceType)_owner.LogicEntity.CargoType : ResourceType.Metal;
		public IEntity CurrentTargetSource { get; set; }

		private void EnsureStatsInjected()
		{
			if (_statsInjected || _owner?.LogicEntity == null) return;
			_owner.LogicEntity.CargoCapacity = (FixMath.NET.Fix64)MaxCapacity;
			_owner.LogicEntity.CargoAmount = FixMath.NET.Fix64.Zero;
			_owner.LogicEntity.CargoType = (int)ResourceType.Metal;
			_statsInjected = true;
		}

	public override void Tick(double delta)
		{
			if (_owner?.LogicEntity == null) return;
			EnsureStatsInjected();
		}

		public bool IsFull()
		{
			if (_owner?.LogicEntity == null) return false;
			return _owner.LogicEntity.CargoAmount >= _owner.LogicEntity.CargoCapacity;
		}

		public float GetFillRatio()
		{
			if (_owner?.LogicEntity == null || _owner.LogicEntity.CargoCapacity <= FixMath.NET.Fix64.Zero) return 0f;
			return (float)(_owner.LogicEntity.CargoAmount / _owner.LogicEntity.CargoCapacity);
		}
	}
}
