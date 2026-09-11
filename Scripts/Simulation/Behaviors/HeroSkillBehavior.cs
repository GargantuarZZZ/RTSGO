using System.Collections.Generic;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	// 英雄技能效果参数（由 SimManager 从 UnitConfig 读取后传入，行为类不依赖 Godot 配置资源）
	public struct HeroSkillArgs
	{
		public FP Cost;
		public FP DurationSeconds;
		public FP RangeTiles;
		public FP MoveSpeedBonus;
		public FP EnemyMovePenalty;
		public FP Multiplier;
		public FP Damage;
		public FP AttackRangeBonus;
	}

	// 英雄技能行为基类：确定性效果实现，可被不同单位复用/子类化
	public abstract class HeroSkillBehavior
	{
		public abstract bool Execute(
			SimWorld world,
			SimUnit hero,
			HeroSkillArgs args,
			SimEntity target,
			FPVector2 targetPos);
	}

	// 勾魂（地狱领主）：魅惑敌方单位，使其不受控制地走向施法者
	public sealed class CharmSkillBehavior : HeroSkillBehavior
	{
		public override bool Execute(SimWorld world, SimUnit hero, HeroSkillArgs args, SimEntity target, FPVector2 targetPos)
		{
			if (target is not SimUnit victim || victim.TeamID == hero.TeamID || victim.TeamID <= 0)
				return false;

			victim.Buffs.AddBuff(
				"DemonCharm", 1,
				args.DurationSeconds,
				FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, hero.ID);
			return true;
		}
	}

	// 咆哮（火焰巨魔）：范围内己方加速、敌方减速
	public sealed class RoarSkillBehavior : HeroSkillBehavior
	{
		public override bool Execute(SimWorld world, SimUnit hero, HeroSkillArgs args, SimEntity target, FPVector2 targetPos)
		{
			FP radius = args.RangeTiles * (FP)64m;
			FP radiusSq = radius * radius;

			foreach (var u in world.Units.Values)
			{
				if (u == null || u.IsDead)
					continue;

				if (!FPVector2.IsWithinRangeSq(u.Position, hero.Position, radiusSq))
					continue;

				if (u.TeamID == hero.TeamID && args.MoveSpeedBonus > FP.Zero)
				{
					u.Buffs.AddBuff(
						"DemonRoarAlly", 1, args.DurationSeconds,
						FP.One, FP.One, FP.Zero,
						FP.One + args.MoveSpeedBonus, FP.Zero, FP.Zero, hero.ID);
				}
				else if (u.TeamID != hero.TeamID && u.TeamID > 0 && args.EnemyMovePenalty > FP.Zero)
				{
					u.Buffs.AddBuff(
						"DemonRoarEnemy", 1, args.DurationSeconds,
						FP.One, FP.One, FP.Zero,
						FP.One - args.EnemyMovePenalty, FP.Zero, FP.Zero, hero.ID);
				}
			}

			return true;
		}
	}

	// 烟雾弹（泰伦指挥车）：半径 3 格内敌方单位射程 -2
	public sealed class TerranSmokeBehavior : HeroSkillBehavior
	{
		public override bool Execute(SimWorld world, SimUnit hero, HeroSkillArgs args, SimEntity target, FPVector2 targetPos)
		{
			FP radius = args.RangeTiles * (FP)64m;
			FP radiusSq = radius * radius;

			foreach (var u in world.Units.Values)
			{
				if (u == null || u.IsDead || u.TeamID == hero.TeamID || u.TeamID <= 0)
					continue;
				if (!FPVector2.IsWithinRangeSq(u.Position, targetPos, radiusSq))
					continue;

				u.Buffs.AddStatBuff(
					"TerranSmoke", 1, args.DurationSeconds,
					FP.One, args.AttackRangeBonus, FP.Zero, hero.ID);
			}

			return true;
		}
	}

	// 干扰弹（泰伦指挥车，科技解锁）：半径 3 格内敌方无法使用技能
	public sealed class TerranDisruptBehavior : HeroSkillBehavior
	{
		public override bool Execute(SimWorld world, SimUnit hero, HeroSkillArgs args, SimEntity target, FPVector2 targetPos)
		{
			FP radius = args.RangeTiles * (FP)64m;
			FP radiusSq = radius * radius;

			foreach (var u in world.Units.Values)
			{
				if (u == null || u.IsDead || u.TeamID == hero.TeamID || u.TeamID <= 0)
					continue;
				if (!FPVector2.IsWithinRangeSq(u.Position, targetPos, radiusSq))
					continue;

				u.Buffs.AddBuff(
					"TerranDisrupt", 1, args.DurationSeconds,
					FP.One, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, hero.ID);
			}

			return true;
		}
	}

	// 狂热武装（地狱领主）：选中己方生产建筑，20 秒内自动生产速度翻倍
	public sealed class BoostSkillBehavior : HeroSkillBehavior
	{
		public override bool Execute(SimWorld world, SimUnit hero, HeroSkillArgs args, SimEntity target, FPVector2 targetPos)
		{
			if (target is not SimStructure st ||
				st.TeamID != hero.TeamID ||
				st.CurrentState != SimStructure.StructureState.Active)
				return false;

			st.AutoProduceBoostMultiplier = args.Multiplier;
			st.AutoProduceBoostTimer = args.DurationSeconds;
			return true;
		}
	}

	// 冲锋（火焰巨魔）：朝指定点冲刺，沿途碰撞造成伤害并击退
	public sealed class DashSkillBehavior : HeroSkillBehavior
	{
		public override bool Execute(SimWorld world, SimUnit hero, HeroSkillArgs args, SimEntity target, FPVector2 targetPos)
		{
			FPVector2 dir = (targetPos - hero.Position).Normalized();
			if (dir.X == FP.Zero && dir.Y == FP.Zero)
				return false;

			FP maxDist = args.RangeTiles * (FP)64m;
			FP dist = FP.Sqrt(FPVector2.DistanceSquared(hero.Position, targetPos));
			FP dashDist = dist > maxDist ? maxDist : dist;

			hero.DashTarget = hero.Position + dir * dashDist;
			hero.DashTimer = (FP)0.5m;
			hero.DashDamage = args.Damage;
			hero.HasTarget = false;
			hero.Velocity = FPVector2.Zero;
			return true;
		}
	}

	// 英雄技能行为注册表：SkillBehaviorId -> 实现类
	public static class HeroSkillBehaviors
	{
		private static readonly Dictionary<string, HeroSkillBehavior> _registry = new()
		{
			["DemonCharm"] = new CharmSkillBehavior(),
			["DemonRoar"] = new RoarSkillBehavior(),
			["DemonBoost"] = new BoostSkillBehavior(),
			["DemonDash"] = new DashSkillBehavior(),
			["TerranSmoke"] = new TerranSmokeBehavior(),
			["TerranDisrupt"] = new TerranDisruptBehavior()
		};

		public static HeroSkillBehavior Get(string behaviorId)
		{
			return !string.IsNullOrEmpty(behaviorId) && _registry.TryGetValue(behaviorId, out var b)
				? b
				: null;
		}
	}
}
