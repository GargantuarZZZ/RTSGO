using Godot;
using RTS.Data;

namespace RTS.Units
{
	/// <summary>
	/// 实体模块公因式：UnitLife / UnitCombat / UnitHarvest / ResourceModule
	/// 原来各自声明一份 `_owner` 字段 + `Initialize(IEntity)` + `Tick(double)` 生命周期，
	/// 统一收敛到这里。子类按需覆盖 Initialize / Tick。
	/// </summary>
	public partial class EntityModuleBase : Node
	{
		protected IEntity _owner;
		public IEntity EntityOwner => _owner;

		public virtual void Initialize(IEntity owner)
		{
			_owner = owner;
		}

		public virtual void Tick(double delta)
		{
		}
	}
}
