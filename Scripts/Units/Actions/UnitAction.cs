using Godot;
using System;
using RTS.Core;

using RTS.Data;
namespace RTS.Actions
{
	[GlobalClass]
	public abstract partial class UnitAction : Node
	{
		[Export] public Texture2D Icon;
		[Export] public int SlotIndex = 0;
		[Export] public string ActionName { get; set; } = "Base";
		[Export(PropertyHint.Flags)] public ActionLayer Layer { get; set; } = ActionLayer.None;
		[Export(PropertyHint.Flags)] public ActionLayer BlockingLayers { get; set; } = ActionLayer.None;
		[Export] public bool Queueable { get; set; } = true;

		protected IEntity _unit;
		/// <summary>动作所属实体（供 UI 层取单位 ID 做本地化）。</summary>
		public IEntity Unit => _unit;
		public bool IsActive { get; private set; } = false;
		public event Action<UnitAction> OnFinished;

		// 是否可被冲突指令打断（机动模式等持续技能置 false，只能等自然结束）
		public virtual bool CanBeInterrupted => true;

		public virtual void Initialize(IEntity unit) => _unit = unit;
		public virtual bool CanExecute() => true;

		public virtual void OnEnter() { IsActive = true; }
		public virtual void OnUpdate(double delta) { }
		public virtual void OnExit() { IsActive = false; }
		public virtual void Setup(RTS.Simulation.FPVector2 targetPos, IEntity targetObj) { }
		public virtual bool TryPayCost()
		{
			return true;
		}

		// 技能冷却（默认无；IonBreath 等主动技能重写）
		public virtual float GetCooldownRemaining() => 0f;
		public virtual float GetCooldownMax() => 0f;

		// 动作自身的确定性状态哈希（脱步定位用；默认无）
		public virtual long GetDeterministicExtraHash() => 0;

		protected void Finish()
		{
			IsActive = false;
			OnFinished?.Invoke(this);
		}
	}
}
