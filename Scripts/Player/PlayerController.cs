using Godot;
using System.Collections.Generic;
using RTS.Units;
using RTS.Data;
using System.Linq;
using RTS.Actions.Implementation;

namespace RTS.Core
{
	public partial class PlayerController : Node
	{
		private List<IEntity> _selectedEntities = new();
		private Player _player;
		private float _pruneTimer = 0f;
		public List<IEntity> SelectedEntities => _selectedEntities;

		public override void _Ready() => _player = GetParent<Player>();

		// 自动清理已死亡/已释放的选中实体：选中的单位死亡后节点会被释放，
		// 若不从列表移除，动作面板/信息面板每帧读取第一个实体就会踩到
		// 已释放节点 → 原生 AccessViolation（8 倍速下选中单位死得快，必崩）。
		public override void _Process(double delta)
		{
			_pruneTimer += (float)delta;
			if (_pruneTimer < 0.25f)
				return;
			_pruneTimer = 0f;

			for (int i = _selectedEntities.Count - 1; i >= 0; i--)
			{
				var e = _selectedEntities[i];
				if (e == null ||
					!GodotObject.IsInstanceValid(e as GodotObject) ||
					e.IsDeadOrNull())
				{
					// 节点已死/已释放，不能再调 SetSelectionVisual（会碰节点）
					_selectedEntities.RemoveAt(i);
				}
			}
		}

		public void SelectEntities(List<IEntity> entities)
		{
			DeselectAll();
			foreach (var e in entities)
			{
				// 沙虫节段等身体附属单位不可选中
				if (e is Unit u &&
					RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitName)?.IsSegment == true)
					continue;
				_selectedEntities.Add(e);
				SetSelectionVisual(e, true);
			}
		}

		public void DeselectAll()
		{
			foreach (var e in _selectedEntities)
				if (GodotObject.IsInstanceValid(e as Node)) SetSelectionVisual(e, false);
			_selectedEntities.Clear();
		}

		private void SetSelectionVisual(IEntity e, bool state)
		{
			if (e is Unit u) u.SetSelected(state);
			else if (e is Structure s) s.VisualsModule?.SetSelected(state);
		}

		public void IssueUnitOrder(Unit unit, string type, Vector2 pos, IEntity target = null, bool queue = false)
		{
			// 基础安全检查
			if (unit.IsDeadOrNull()) return;
			if (unit.TeamID != _player.TeamId) return;
			if (unit.Brain == null) { GD.PrintErr($"[Order] 严重错误：单位 {unit.Name} 缺少 Brain (UnitActionController) 模块。"); return; }

			// 建造指令：目标必须是半成品建筑
			if (type == "Build")
			{
				if (target is Structure s)
				{
					string actionId = "Build_" + s.StructureName;
					if (unit.Brain.GetAction<BuildAction>(actionId) != null)
					{
						unit.Brain.StartAction(actionId, new RTS.Simulation.FPVector2((FixMath.NET.Fix64)pos.X, (FixMath.NET.Fix64)pos.Y), target, false, queue);
					}
					else
					{
						GD.PrintErr($"[Order] 失败：单位 {unit.Name} 找不到名为 '{actionId}' 的动作！请检查 BuildAction 配置。");
					}
				}
				else GD.PrintErr($"[Order] 错误：Build 指令的目标不是 Structure 类型。当前目标: {target?.GetType()}");
				return;
			}

			// 通用指令：移动/攻击移动传 null 目标，攻击/采集传目标实体
			var actualTarget = (type is "Move" or "AttackMove") ? null : target;
			switch (type)
			{
				case "Move" or "AttackMove" or "Attack" or "Harvest":
					unit.Brain.StartAction(type, new RTS.Simulation.FPVector2((FixMath.NET.Fix64)pos.X, (FixMath.NET.Fix64)pos.Y), actualTarget, false, queue);
					break;
				default:
					GD.PrintErr($"[Order] 未知的指令类型: {type}");
					break;
			}
		}

		public List<Unit> GetMySelectedUnits() => _selectedEntities
			.OfType<Unit>().Where(u => u.TeamID == _player.TeamId && !u.IsDeadOrNull()).ToList();
	}
}
