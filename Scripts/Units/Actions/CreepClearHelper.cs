using Godot;
using RTS.Data;
using RTS.Simulation;
using RTS.Units;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions
{
	// 统一的“清敌方纳米菌毯”入口：与普通攻击共用武器冷却/射程/特效/溅射
	public static class CreepClearHelper
	{
		public static bool TryFireAtCreep(IEntity unit, SimWorld world)
		{
			if (unit?.CombatModule?.ActiveWeapon == null || world == null || unit.LogicEntity == null)
				return false;

			// 菌毯索敌 = 攻击射程 + 友军视野
			float scanRange = unit.CombatModule.GetAttackRange();
			if (scanRange <= 0f)
				return false;

			int rangeTiles = Mathf.Max(1, Mathf.CeilToInt(scanRange / world.Grid.TileSize));
			var center = world.Grid.WorldToGrid(unit.LogicEntity.Position);

			var candidates = new System.Collections.Generic.List<(int X, int Y, FP Dist)>();

			for (int x = center.X - rangeTiles; x <= center.X + rangeTiles; x++)
			{
				for (int y = center.Y - rangeTiles; y <= center.Y + rangeTiles; y++)
				{
					if (world.CreepGrid.GetActiveCreep(x, y) != CreepType.NanoCreep)
						continue;
					int owner = world.CreepGrid.GetOwner(x, y);
					// 己方/盟友（2v2 同组）菌毯不清除：只清敌对菌毯
					if (RTS.Core.SimManager.Instance == null ||
						!RTS.Core.SimManager.Instance.AreTeamsHostile(unit.TeamID, owner))
						continue;

					// 友军视野外的菌毯无法锁定
					if (unit.TeamID > 0)
					{
						FP cellHalf = (FP)(world.Grid.TileSize / 2);
						FP cellX = (FP)(x * world.Grid.TileSize) + cellHalf;
						FP cellY = (FP)(y * world.Grid.TileSize) + cellHalf;

						if (!world.IsInTeamVision(unit.TeamID, new FPVector2(cellX, cellY), FP.Zero))
							continue;
					}

					FP dx = (FP)(x - center.X);
					FP dy = (FP)(y - center.Y);
					FP d = dx * dx + dy * dy;

					// 射程是圆形：方块扫描但只收圆形范围内的格
					if (d > (FP)(rangeTiles * rangeTiles))
						continue;

					candidates.Add((x, y, d));
				}
			}

			if (candidates.Count == 0)
				return false;

			candidates.Sort((a, b) => a.Dist == b.Dist
				? (a.X == b.X ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X))
				: (a.Dist < b.Dist ? -1 : 1));

			FP halfTile = (FP)world.Grid.TileSize / (FP)2m;
			int shots = RTS.Data.Configs.ConfigDatabase.GetUnit(unit.DisplayName)?.MultiShotTargets ?? 1;
			int fireCount = Mathf.Min(shots, candidates.Count);

			// 攻击一次：最多 15 条不同的线同时打 15 个不同的菌毯格（第一发吃冷却）
			for (int i = 0; i < fireCount; i++)
			{
				var t = candidates[i];
				var pos = new FPVector2(
					(FP)(t.X * world.Grid.TileSize) + halfTile,
					(FP)(t.Y * world.Grid.TileSize) + halfTile);

				if (i == 0)
				{
					unit.VisualsModule?.AimAt(new Vector2((float)pos.X, (float)pos.Y));
					// 冷却未好：整轮齐射都不打，直接返回（否则连发会绕过冷却）
					if (!unit.CombatModule.FireAtGround(pos))
						return false;
				}
				else
					unit.CombatModule.FireGroundVolley(pos, false);

				// 即时武器：每条线命中即清对应格；弹道武器由落地统一结算
				if (unit.CombatModule.LastGroundWeapon is not ProjectileWeapon)
					world.CreepGrid.DestroyNanoCreep(t.X, t.Y);
			}

			// 一次齐射后所有武器进入冷却：不会每帧换武器连续齐射
			unit.CombatModule.ConsumeAllWeaponCooldowns();

			return true;
		}
	}
}
