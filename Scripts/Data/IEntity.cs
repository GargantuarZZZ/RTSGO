// 文件: res://Scripts/Data/IEntity.cs
using Godot;
using System.Collections.Generic;
using RTS.Units;
using RTS.Actions;
using RTS.Simulation; // 模拟层依赖

namespace RTS.Data
{
	public interface IEntity
	{
		// 确定性模拟数据
		SimEntity LogicEntity { get; }

		int TeamID { get; set; }

		// 警告：仅供 UI 渲染、特效、音效读取位置！严禁用于距离判定！
		Godot.Vector2 GlobalPosition { get; }

		bool IsStructure { get; }
		Vector2I GridPosition { get; }
		bool IsDeadOrNull();
		float VisionRange { get; }
		string DisplayName { get; }
		Texture2D Icon { get; }

		List<EntityAction> GetAvailableActions();
		UnitLife LifeModule { get; }
		UnitVisuals VisualsModule { get; }
		UnitCombat CombatModule { get; }
		UnitHarvest HarvestModule { get; }
		UnitActionController Brain { get; }
	}
}
