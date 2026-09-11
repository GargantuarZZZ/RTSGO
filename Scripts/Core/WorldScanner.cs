using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Units;

namespace RTS.Core
{
	public static class WorldScanner
	{
		private static IEnumerable<IEntity> AllEntities(SceneTree tree) =>
			tree.GetNodesInGroup("entities").OfType<IEntity>().Where(e => !e.IsDeadOrNull());

		// 沙虫节段等身体附属单位：不可被点击/框选
		private static bool IsSelectable(IEntity e)
		{
			if (e is Unit u)
				return RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitName)?.IsSegment != true;
			return true;
		}

		// 多足：己方单位必须在控制范围内（或无需控制）才可选中/操作
		public static bool IsOperable(IEntity e, int localPlayerID)
		{
			if (e == null)
				return false;
			if (e.TeamID != localPlayerID)
				return true;
			if (e is Structure)
				return true;

			// 操控限制只对多足机械生效
			if (RTS.World.Game.GetPlayerByTeam(e.TeamID)?.Race?.RaceName != "Wanderer")
				return true;

			if (e is Unit u && u.LogicEntity is RTS.Simulation.SimUnit su)
			{
				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitName);
				if (cfg == null || cfg.NoControlNeeded || su.NoControlNeeded)
					return true;
				if (cfg.ControlRangeTiles > 0)
					return true; // 控制单位自身始终可操作

				return RTS.Core.SimManager.IsInControlRange(su);
			}

			return true;
		}

		// 查找己方已完工的轨道控制中心（雷达/轨道炮/资源交换共用，OrbitalPanelUI 与 UserController 各有一份副本，收敛到这里）
		public static Structure FindOwnedOrbitalControl(SceneTree tree, int localTeam)
		{
			foreach (Node node in tree.GetNodesInGroup("entities"))
			{
				if (node is Structure s &&
					s.StructureName == "OrbitalControl" &&
					s.TeamID == localTeam &&
					s.CurrentState == Structure.StructureState.Completed &&
					!s.IsDeadOrNull())
				{
					return s;
				}
			}
			return null;
		}

		// 射线检测：找鼠标下的实体
		public static IEntity Raycast(SceneTree tree, Vector2 pos, int localPlayerID)
		{
			return AllEntities(tree)
				.Where(IsSelectable)
				.Where(e => {
					// 1. 过滤敌方不可见蓝图 (原有逻辑)
					if (e is Structure s && s.CurrentState == Structure.StructureState.Blueprint && s.TeamID != localPlayerID) return false;

					// 过滤不可见单位（2D/3D 通用）
					if (e is Node2D n2d && !n2d.Visible) return false;
					if (e is Node3D n3d && !n3d.Visible) return false;

					// 3. 距离判定
					return e.GlobalPosition.DistanceTo(pos) < (e.IsStructure ? 64f : 32f);
				})
				.MinBy(e => e.GlobalPosition.DistanceTo(pos));
		}

		// 框选检测
		public static List<IEntity> BoxSelect(SceneTree tree, Rect2 box, int localPlayerID)
		{
			return AllEntities(tree)
				.Where(IsSelectable)
				.Where(e => e.TeamID == localPlayerID)
				.Where(e => !(e is Structure s && s.CurrentState == Structure.StructureState.Blueprint)) // 忽略蓝图
				.Where(e => (e is Node2D n && n.Visible) || (e is Node3D n3 && n3.Visible))
				.Where(e => box.HasPoint(e.GlobalPosition))
				.ToList();
		}

		// 旁观者框选：不限队伍，只要求可见且非蓝图（只能看，不能控制）
		public static List<IEntity> BoxSelectAll(SceneTree tree, Rect2 box)
		{
			return AllEntities(tree)
				.Where(IsSelectable)
				.Where(e => !(e is Structure s && s.CurrentState == Structure.StructureState.Blueprint))
				.Where(e => (e is Node2D n && n.Visible) || (e is Node3D n3 && n3.Visible))
				.Where(e => box.HasPoint(e.GlobalPosition))
				.ToList();
		}

		// 全屏同类选择 (Ctrl+点击)
		public static List<IEntity> ScreenSelect(SceneTree tree, IEntity target, Rect2 screenRect, int localPlayerID)
		{
			if (target == null || target.TeamID != localPlayerID) return new List<IEntity>();

			string typeName = target is Unit u ? u.UnitName : (target is Structure s ? s.StructureName : "");
			if (string.IsNullOrEmpty(typeName)) return new List<IEntity>();

			return AllEntities(tree)
				.Where(IsSelectable)
				.Where(e => e.TeamID == localPlayerID &&
							!(e is Structure s && s.CurrentState == Structure.StructureState.Blueprint) &&
							((e is Node2D n && n.Visible) || (e is Node3D n3 && n3.Visible)) &&
							screenRect.HasPoint(e.GlobalPosition) &&
							(e is Unit u2 && u2.UnitName == typeName || e is Structure s2 && s2.StructureName == typeName))
				.ToList();
		}

		// 旁观者全屏同类选择：不限队伍
		public static List<IEntity> ScreenSelectAll(SceneTree tree, IEntity target, Rect2 screenRect)
		{
			if (target == null) return new List<IEntity>();
			string typeName = target is Unit u ? u.UnitName : (target is Structure s ? s.StructureName : "");
			if (string.IsNullOrEmpty(typeName)) return new List<IEntity>();

			return AllEntities(tree)
				.Where(IsSelectable)
				.Where(e => !(e is Structure s2 && s2.CurrentState == Structure.StructureState.Blueprint) &&
							((e is Node2D n && n.Visible) || (e is Node3D n3 && n3.Visible)) &&
							screenRect.HasPoint(e.GlobalPosition) &&
							(e is Unit u2 && u2.UnitName == typeName || e is Structure s3 && s3.StructureName == typeName))
				.ToList();
		}
	}
}
