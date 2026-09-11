using System;
using System.Collections.Generic;
using System.Linq;
using FixMath.NET;
using RTS.Core;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
	internal static partial class Program
	{
		private static int _failures = 0;

		private static void Check(bool condition, string name)
		{
			Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + name);
			if (!condition)
				_failures++;
		}

		private static SimWorld CreateWorld()
		{
			var w = new SimWorld();

			// 地面
			for (int x = -8; x <= 8; x++)
			{
				for (int y = -8; y <= 8; y++)
				{
					w.Grid.TerrainCells.Add(new SimVector2I(x, y));
				}
			}

			// 一堵墙
			for (int y = -3; y <= 3; y++)
				w.Grid.StaticObstacles.Add(new SimVector2I(2, y));

			// 单位
			for (int i = 0; i < 12; i++)
			{
				var u = new SimUnit(
					w.GetNextEntityId(),
					(i % 2) + 1,
					new FPVector2((FP)(-6 + (i % 4) * 2), (FP)(-6 + (i / 4) * 2))
				);
				u.MaxSpeed = (FP)200;
				u.Radius = (FP)15;
				u.Hp = (FP)100;
				u.MaxHp = (FP)100;
				w.AddUnit(u);
			}

			// 普通建筑：2x2 占地的方块
			var structure = new SimStructure(
				w.GetNextEntityId(),
				-1,
				new FPVector2((FP)8, (FP)8),
				new SimVector2I(8, 8),
				2
			);
			structure.Hp = (FP)500;
			structure.MaxHp = (FP)500;
			structure.CurrentState = SimStructure.StructureState.Active;
			w.AddStructure(structure);

			// 圣地
			var shrine = new SimShrine(
				w.GetNextEntityId(),
				-1,
				new FPVector2((FP)(-8), (FP)8),
				new SimVector2I(-8, 8),
				2
			);
			shrine.Hp = (FP)1000;
			shrine.MaxHp = (FP)1000;
			shrine.CurrentState = SimStructure.StructureState.Active;
			w.AddStructure(shrine);

			return w;
		}

		private static void IssueRandomCommands(SimWorld w, SimRandom rng)
		{
			int unitCount = w.Units.Count;
			if (unitCount == 0)
				return;

			var units = new List<SimUnit>(w.Units.Values);
			int cmdCount = (int)(rng.Next() % 3u) + 1;

			for (int i = 0; i < cmdCount; i++)
			{
				int idx = (int)(rng.Next() % (uint)unitCount);
				int tx = (int)(rng.Next() % 3000u) - 1500;
				int ty = (int)(rng.Next() % 3000u) - 1500;
				units[idx].CommandMove(w, new FPVector2((FP)tx, (FP)ty));
			}

			if (rng.Next() % 5u == 0 && w.Units.Count > 1)
			{
				var all = new List<SimUnit>(w.Units.Values);
				w.AddProjectile(new SimProjectile(all[0].Position, all[1], (FP)400, (FP)16, (FP)10, 0));
			}
		}

		private static void RunScriptedComparison()
		{
			Console.WriteLine("== 测试1: 双世界 500 Tick 随机指令对拍 ==");
			var a = CreateWorld();
			var b = CreateWorld();
			var rngA = new SimRandom(777u);
			var rngB = new SimRandom(777u);
			bool ok = true;

			for (int tick = 0; tick < 500; tick++)
			{
				IssueRandomCommands(a, rngA);
				IssueRandomCommands(b, rngB);
				a.Tick();
				b.Tick();

				if (a.GetWorldHash() != b.GetWorldHash())
				{
					Console.WriteLine($"  第 {tick} Tick 哈希不一致！ A={a.GetWorldHash()} B={b.GetWorldHash()}");
					ok = false;
					break;
				}

				if (a.Units.Count != b.Units.Count ||
					a.Structures.Count != b.Structures.Count ||
					a.Projectiles.Count != b.Projectiles.Count)
				{
					Console.WriteLine($"  第 {tick} Tick 实体数量不一致！");
					ok = false;
					break;
				}
			}

			Check(ok, "500 Tick 双世界完全一致（路径/群体/碰撞/子弹）");
		}

		private static void RunHashSensitivity()
		{
			Console.WriteLine("== 测试2: 哈希敏感性 ==");
			long baseHash = CreateWorld().GetWorldHash();

			var w1 = CreateWorld();
			var mut1 = new List<SimUnit>(w1.Units.Values)[0];
			mut1.Position += new FPVector2((FP)1, (FP)1);
			Check(w1.GetWorldHash() != baseHash, "位置变化 => 哈希变化");

			var w2 = CreateWorld();
			new List<SimUnit>(w2.Units.Values)[0].Hp = (FP)99;
			Check(w2.GetWorldHash() != baseHash, "血量变化 => 哈希变化");

			var w3 = CreateWorld();
			w3.CreepGrid.AddCreep(0, 0, RTS.Data.CreepType.NanoCreep);
			Check(w3.GetWorldHash() != baseHash, "菌毯变化 => 哈希变化");

			var w4 = CreateWorld();
			w4.RNG.Next();
			Check(w4.GetWorldHash() != baseHash, "RNG 状态变化 => 哈希变化");

			var w5 = CreateWorld();
			new List<SimStructure>(w5.Structures.Values)[0].TeamID = 2;
			Check(w5.GetWorldHash() != baseHash, "建筑阵营变化 => 哈希变化");

			var w6 = CreateWorld();
			new List<SimStructure>(w6.Structures.Values)[0].ConstructionProgress = (FP)0.5m;
			Check(w6.GetWorldHash() != baseHash, "建造进度变化 => 哈希变化");

			var w7 = CreateWorld();
			var shrine = (SimShrine)new List<SimStructure>(w7.Structures.Values)[1];
			shrine.CaptureProgress = (FP)1m;
			shrine.CapturingTeam = 1;
			Check(w7.GetWorldHash() != baseHash, "圣地进度变化 => 哈希变化");

			var w8 = CreateWorld();
			new List<SimUnit>(w8.Units.Values)[0].IsGhost = true;
			Check(w8.GetWorldHash() != baseHash, "幽灵模式变化 => 哈希变化");

			var w9 = CreateWorld();
			var u9 = new List<SimUnit>(w9.Units.Values)[0];
			u9.CargoType = (int)ResourceType.Gas;
			u9.CargoAmount = (FP)25;
			Check(w9.GetWorldHash() != baseHash, "货舱变化 => 哈希变化");
		}

		private static void RunPathfindingTest()
		{
			Console.WriteLine("== 测试3: 寻路确定性 ==");
			var a = CreateWorld();
			var b = CreateWorld();
			var start = new FPVector2((FP)(-6), (FP)0);
			var end = new FPVector2((FP)6, (FP)0);
			var pathA = SimPathfinder.FindPath(a.Grid, a.Structures, start, end);
			var pathB = SimPathfinder.FindPath(b.Grid, b.Structures, start, end);

			Check(pathA.Count == pathB.Count, "双世界路径长度一致");

			bool same = pathA.Count == pathB.Count;
			for (int i = 0; i < Math.Min(pathA.Count, pathB.Count); i++)
			{
				if (pathA[i].X != pathB[i].X || pathA[i].Y != pathB[i].Y)
				{
					same = false;
					break;
				}
			}
			Check(same, "双世界路径点完全一致");

			var w2 = CreateWorld();
			for (int y = -5; y <= 5; y++)
				w2.Grid.StaticObstacles.Add(new SimVector2I(-1, y));
			w2.Grid.SyncStaticBlocking();
			var path2 = SimPathfinder.FindPath(w2.Grid, w2.Structures, start, end);

			bool diff = path2.Count != pathA.Count;
			if (!diff)
			{
				for (int i = 0; i < pathA.Count; i++)
				{
					if (pathA[i].X != path2[i].X || pathA[i].Y != path2[i].Y)
					{
						diff = true;
						break;
					}
				}
			}
			Check(diff, "障碍变化 => 路径变化");
		}

		private static void RunCreepHashTest()
		{
			Console.WriteLine("== 测试4: 菌毯哈希 ==");
			var a = CreateWorld();
			var b = CreateWorld();

			for (int r = 0; r < 3; r++)
			{
				for (int x = -r; x <= r; x++)
				{
					for (int y = -r; y <= r; y++)
					{
						a.CreepGrid.AddCreep(x, y, RTS.Data.CreepType.NanoCreep);
						b.CreepGrid.AddCreep(x, y, RTS.Data.CreepType.NanoCreep);
					}
				}
			}

			Check(a.GetWorldHash() == b.GetWorldHash(), "菌毯状态哈希一致");
			Check(a.GetWorldHash() != CreateWorld().GetWorldHash(), "菌毯状态纳入哈希");
		}

		private static void RunSectionHashTest()
		{
			Console.WriteLine("== 测试5: 区段哈希定位 ==");
			var baseWorld = CreateWorld();
			long c0 = baseWorld.GetCountersHash();
			long u0 = baseWorld.GetUnitsHash();
			long s0 = baseWorld.GetStructuresHash();
			long p0 = baseWorld.GetProjectilesHash();
			long k0 = baseWorld.GetCreepHash();
			long r0 = baseWorld.GetRngHash();

			var w1 = CreateWorld();
			new List<SimUnit>(w1.Units.Values)[0].Position += new FPVector2((FP)1, (FP)1);
			Check(
				w1.GetUnitsHash() != u0 &&
				w1.GetStructuresHash() == s0 &&
				w1.GetProjectilesHash() == p0 &&
				w1.GetCreepHash() == k0 &&
				w1.GetRngHash() == r0,
				"单位变化只影响 Units 区段"
			);

			var w2 = CreateWorld();
			w2.CreepGrid.AddCreep(3, 3, RTS.Data.CreepType.NanoCreep);
			Check(
				w2.GetCreepHash() != k0 &&
				w2.GetUnitsHash() == u0 &&
				w2.GetStructuresHash() == s0 &&
				w2.GetRngHash() == r0,
				"菌毯变化只影响 Creep 区段"
			);

			var w3 = CreateWorld();
			w3.RNG.Next();
			Check(
				w3.GetRngHash() != r0 &&
				w3.GetUnitsHash() == u0 &&
				w3.GetStructuresHash() == s0 &&
				w3.GetCreepHash() == k0,
				"RNG 变化只影响 RNG 区段"
			);

			var w4 = CreateWorld();
			new List<SimStructure>(w4.Structures.Values)[0].TeamID = 2;
			Check(
				w4.GetStructuresHash() != s0 &&
				w4.GetUnitsHash() == u0 &&
				w4.GetProjectilesHash() == p0,
				"建筑变化只影响 Structures 区段"
			);

			var w5 = CreateWorld();
			var all = new List<SimUnit>(w5.Units.Values);
			w5.AddProjectile(new SimProjectile(all[0].Position, all[1], (FP)400, (FP)16, (FP)10, 0));
			Check(
				w5.GetProjectilesHash() != p0 &&
				w5.GetUnitsHash() == u0 &&
				w5.GetStructuresHash() == s0,
				"子弹变化只影响 Projectiles 区段"
			);
		}

		private static void RunCombatBuffTest()
		{
			Console.WriteLine("== 测试6: 攻防与Buff判定 ==");
			var w = CreateWorld();
			var unit = new List<SimUnit>(w.Units.Values)[0];

			// 1. 护甲减伤
			unit.Defenses[(int)RTS.Data.DamageType.Kinetic] = (FP)3;
			var ctx = new DamageContext
			{
				DamageType = (int)RTS.Data.DamageType.Kinetic,
				BaseDamage = (FP)10,
				TargetId = unit.ID
			};
			Check(DamageResolver.Resolve(ctx, unit, null) == (FP)7m, "10 伤害 - 3 护甲 = 7");

			// 2. 伤害类型 x 护甲类型修正（动（动能 vs 生物 = 1.25 倍）
			unit.ArmorType = (int)RTS.Data.ArmorType.Biological;
			Check(DamageResolver.Resolve(ctx, unit, null) == (FP)9.5m, "动能对生物 1.25 倍 => (10*1.25)-3 = 9.5");

			// 3. 目标 Buff：受到伤害减// 3. 目标 Buff：受到伤害减免
			unit.Buffs.AddBuff("TestIncoming", 1, (FP)5, (FP)1, (FP)0.5m, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
			Check(DamageResolver.Resolve(ctx, unit, null) == (FP)4.75m, "目标受伤减半 Buff => 9.5*0.5 = 4.75");

			// 4. 攻击// 4. 攻击者 Buff：造成伤害加成
			var source = new List<SimUnit>(w.Units.Values)[1];
			source.Buffs.AddBuff("TestDamage", 1, (FP)5, (FP)1.5m, (FP)1, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
			Check(DamageResolver.Resolve(ctx, unit, source) == (FP)7.125m, "攻击者增伤 1.5 倍 => 4.75*1.5 = 7.125");

			// 5. 移// 5. 移速 Buff
			unit.Buffs.AddBuff("TestSpeed", 1, (FP)5, FP.One, FP.One, FP.Zero, (FP)1.5m, FP.Zero, FP.Zero, -1);
			Check(unit.GetEffectiveMaxSpeed() == (FP)200 * (FP)1.5m, "移速 Buff 1.5 倍生效");

			// 6. Buff 纳入世界哈希
			var clean = CreateWorld();
			long cleanHash = clean.GetWorldHash();
			var buffed = CreateWorld();
			new List<SimUnit>(buffed.Units.Values)[0].Buffs.AddBuff("HashBuff", 1, (FP)5, (FP)1.2m, (FP)1, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
			Check(buffed.GetWorldHash() != cleanHash, "Buff 状态纳入世界哈希");

			// 7. 最小伤害下// 7. 最小伤害下限
			var tank = new List<SimUnit>(w.Units.Values)[2];
			tank.Defenses[(int)RTS.Data.DamageType.Kinetic] = (FP)100;
			Check(DamageResolver.Resolve(new DamageContext { DamageType = (int)RTS.Data.DamageType.Kinetic, BaseDamage = (FP)5, TargetId = tank.ID }, tank, null) == (FP)1m, "伤害下限至少 1");
		}

		private static void RunCarpetSpreadTest()
		{
			Console.WriteLine("== 测试7: 纳米地毯 1 秒圆心蔓延 + 连续充能 ==");

			var w = CreateWorld();
			for (int x = -12; x <= 12; x++)
			{
				for (int y = -12; y <= 12; y++)
					w.Grid.TerrainCells.Add(new SimVector2I(x, y));
			}
			w.CreepGrid.AddCreep(0, 0, RTS.Data.CreepType.NanoCreep);

			Check(!w.StartCarpetSpread(3, 3, 1), "无菌毯的格子不能作为释放点");
			Check(w.StartCarpetSpread(0, 0, 1), "已有菌毯的格子可以作为释放点");

			// 0.5 秒（10 tick）：只铺到约 5 格半// 0.5 秒（10 tick）：只铺到约 5 格半径
			for (int i = 0; i < 10; i++)
				w.Tick();
			Check(w.CreepGrid.GetActiveCreep(5, 0) == RTS.Data.CreepType.NanoCreep, "0.5s 内 5 格半径内已铺");
			Check(w.CreepGrid.GetActiveCreep(9, 0) == RTS.Data.CreepType.None, "0.5s 内 9 格半径还没铺");

			// 1 秒（// 1 秒（第 10 tick）：铺满 10 格
			for (int i = 0; i < 10; i++)
				w.Tick();
			Check(w.CreepGrid.GetActiveCreep(9, 0) == RTS.Data.CreepType.NanoCreep, "1s 内 9 格半径已铺");
			Check(w.CreepGrid.GetActiveCreep(10, 0) == RTS.Data.CreepType.NanoCreep, "1s 内 10 格半径已铺");
			Check(w.CreepGrid.GetActiveCreep(11, 0) == RTS.Data.CreepType.None, "1s 内 11 格半径不铺");
			Check(w.CarpetSpreads.Count == 0, "1s 后扩散状态移除");

			// 敌我归属：同种刷新归属，异种菌毯不能覆盖
			Check(w.CreepGrid.GetOwner(6, 0) == 1, "菌毯记录归属");
			w.CreepGrid.AddCreep(6, 0, RTS.Data.CreepType.PlantCreep, 2);
			Check(w.CreepGrid.GetActiveCreep(6, 0) == RTS.Data.CreepType.NanoCreep, "异种菌毯不能覆盖");

			// 连续充能：从 1 层开始，只要不满 3 层就继续恢复
			var c = CreateWorld();
			for (int x = -12; x <= 12; x++)
			{
				for (int y = -12; y <= 12; y++)
					c.Grid.TerrainCells.Add(new SimVector2I(x, y));
			}
			c.CarpetSpreadCharges = 1;
			c.CarpetSpreadCooldown = c.CarpetSpreadCooldownTime;
			for (int i = 0; i < 600; i++)
				c.Tick();
			Check(c.CarpetSpreadCharges == 3, "不满 3 层会一直充能到底");
			Check(c.CarpetSpreadCooldown == FP.Zero, "满层后 CD 归零");
		}

		private static void RunTechSimTest()
		{
			Console.WriteLine("== 测试8: 科技效果（抗性 / 地毯生成 / 移速加成）==");

			// 1. 按伤害类型抗// 1. 按伤害类型抗性
			var w = CreateWorld();
			var unit = new List<SimUnit>(w.Units.Values)[0];
			unit.Buffs.AddResistBuff("R1", 1, FP.Zero, (FP)0.5m, FP.One, FP.One, FP.One, FP.One, -1);
			var dmg = DamageResolver.Resolve(
				new DamageContext { DamageType = (int)RTS.Data.DamageType.Kinetic, BaseDamage = (FP)10m, TargetId = unit.ID },
				unit,
				null
			);
			Check(dmg == (FP)5m, "动能抗性 50% => 伤害减半");

			// 2. 机动A：自身半径内生成地毯
			var w2 = CreateWorld();
			var mover = new List<SimUnit>(w2.Units.Values)[0];
			var g0 = w2.Grid.WorldToGrid(mover.Position);
			mover.SelfCreepRadius = 3;
			w2.Tick();
			Check(
				w2.CreepGrid.GetActiveCreep(g0.X + 2, g0.Y) == RTS.Data.CreepType.NanoCreep,
				"机动A机动A： 格半径内生成地毯"
			);

			// 3. 机动B：地毯上移速// 3. 机动B：地毯上移速快
			var w3 = new SimWorld();
			for (int x = -8; x <= 8; x++)
			{
				for (int y = -8; y <= 8; y++)
					w3.Grid.TerrainCells.Add(new SimVector2I(x, y));
			}
			var fast = new SimUnit(w3.GetNextEntityId(), 1, new FPVector2(FP.Zero, FP.Zero));
			fast.MaxSpeed = (FP)200m;
			fast.Radius = (FP)15m;
			fast.SpeedOnCreepMultiplier = (FP)2m;
			w3.AddUnit(fast);
			w3.CreepGrid.AddCreep(0, 0, RTS.Data.CreepType.NanoCreep);
			fast.CommandMove(w3, new FPVector2((FP)500m, FP.Zero));
			w3.Tick();
			FP speedSq = fast.Velocity.MagnitudeSquared();
			Check(
				speedSq > (FP)300m * (FP)300m && speedSq <= (FP)400m * (FP)400m,
				"机动B：地毯上移速为 400"
			);

			// 4. 光环数// 4. 光环数量 Buff：攻速倍率 / 射程修正
			var w4 = CreateWorld();
			var buffed = new List<SimUnit>(w4.Units.Values)[0];
			buffed.Buffs.AddStatBuff("AuraTest", 1, FP.Zero, (FP)1.5m, (FP)(-64m), FP.Zero, -1);
			Check(buffed.Buffs.GetAttackSpeedMultiplier() == (FP)1.5m, "攻速倍率 1.5 生效");
			Check(buffed.Buffs.GetAttackRangeBonus() == (FP)(-64m), "射程修正 -64 生效");

			// 5. 主动技能冷却：随时间减少且纳入哈希
			var w5 = CreateWorld();
			var caster = new List<SimUnit>(w5.Units.Values)[0];
			caster.SkillCooldown = (FP)1m;
			for (int i = 0; i < 25; i++)
				w5.Tick();
			Check(caster.SkillCooldown <= FP.Zero, "技能冷却随时间减少");

			var w6 = CreateWorld();
			new List<SimUnit>(w6.Units.Values)[0].SkillCooldown = (FP)5m;
			Check(w6.GetWorldHash() != CreateWorld().GetWorldHash(), "技能冷却纳入世界哈希");
		}

		private static void RunLongMixedScenario()
		{
			Console.WriteLine("== 测试9: 双世界 1000 Tick 高强度混合对拍 ==");
			var a = CreateWorld();
			var b = CreateWorld();
			var rngA = new SimRandom(20260817u);
			var rngB = new SimRandom(20260817u);
			bool ok = true;

			for (int tick = 0; tick < 1000; tick++)
			{
				IssueRandomCommands(a, rngA);
				IssueRandomCommands(b, rngB);

				// 周期性扰动：菌毯 / Buff / 建筑状// 周期性扰动：菌毯 / Buff / 建筑状态
				if (tick % 37 == 0)
				{
					int cxA = (int)(rngA.Next() % 17u) - 8;
					int cyA = (int)(rngA.Next() % 17u) - 8;
					int cxB = (int)(rngB.Next() % 17u) - 8;
					int cyB = (int)(rngB.Next() % 17u) - 8;
					a.CreepGrid.AddCreep(cxA, cyA, RTS.Data.CreepType.NanoCreep, 1);
					b.CreepGrid.AddCreep(cxB, cyB, RTS.Data.CreepType.NanoCreep, 1);
				}

				if (tick % 53 == 0 && a.Units.Count > 0)
				{
					var ua = new List<SimUnit>(a.Units.Values)[0];
					var ub = new List<SimUnit>(b.Units.Values)[0];
					ua.Buffs.AddBuff("MixBuff", 1, (FP)3m, (FP)1.2m, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
					ub.Buffs.AddBuff("MixBuff", 1, (FP)3m, (FP)1.2m, FP.One, FP.Zero, FP.One, FP.Zero, FP.Zero, -1);
					ua.TakeDamage((FP)8m, (int)RTS.Data.DamageType.Kinetic);
					ub.TakeDamage((FP)8m, (int)RTS.Data.DamageType.Kinetic);
				}

				if (tick % 71 == 0 && a.Structures.Count > 0)
				{
					var sa = new List<SimStructure>(a.Structures.Values)[0];
					var sb = new List<SimStructure>(b.Structures.Values)[0];
					sa.ConstructionProgress = (FP)0.4m;
					sb.ConstructionProgress = (FP)0.4m;
				}

				a.Tick();
				b.Tick();

				if (a.GetWorldHash() != b.GetWorldHash())
				{
					Console.WriteLine($"  第 {tick} Tick 哈希不一致！ A={a.GetWorldHash()} B={b.GetWorldHash()}");
					ok = false;
					break;
				}

				if (a.Units.Count != b.Units.Count ||
					a.Structures.Count != b.Structures.Count ||
					a.Projectiles.Count != b.Projectiles.Count ||
					a.CarpetSpreads.Count != b.CarpetSpreads.Count)
				{
					Console.WriteLine($"  第 {tick} Tick 实体数量不一致！");
					ok = false;
					break;
				}
			}

			Check(ok, "1000 Tick 混合场景（移速/子弹/菌毯/Buff/建造）完全一致");
		}

		private static void RunFormationTest()
		{
			Console.WriteLine("== 测试10: 群体编队阵型确定性 ==");

			var a = FormationSolver.SolveFP(40, new FPVector2(FP.Zero, FP.Zero), (FP)96m, new FPVector2((FP)(-1000m), FP.Zero));
			var b = FormationSolver.SolveFP(40, new FPVector2(FP.Zero, FP.Zero), (FP)96m, new FPVector2((FP)(-1000m), FP.Zero));
			Check(a.Count == 40 && b.Count == 40, "40 单位编队槽位数量正确");

			bool same = true;
			for (int i = 0; i < a.Count; i++)
			{
				if (a[i].X != b[i].X || a[i].Y != b[i].Y)
				{
					same = false;
					break;
				}
			}
			Check(same, "两次求解结果完全一致");

			var c = FormationSolver.SolveFP(40, new FPVector2(FP.Zero, FP.Zero), (FP)96m, new FPVector2((FP)1000m, FP.Zero));
			Check(a[0].X != c[0].X || a[0].Y != c[0].Y, "阵型朝向随移动方向变化");

			var one = FormationSolver.SolveFP(1, new FPVector2((FP)5m, (FP)6m), (FP)96m, FPVector2.Zero);
			Check(one.Count == 1 && one[0].X == (FP)5m && one[0].Y == (FP)6m, "单单位直接使用目标点");

			// 阵型槽位两两不重// 阵型槽位两两不重叠
			bool noOverlap = true;
			for (int i = 0; i < a.Count && noOverlap; i++)
			{
				for (int j = i + 1; j < a.Count; j++)
				{
					if (a[i].X == a[j].X && a[i].Y == a[j].Y)
					{
						noOverlap = false;
						break;
					}
				}
			}
			Check(noOverlap, "编队槽位两两不重叠");
		}

		private static void RunLargeGroupPathBudgetTest()
		{
			Console.WriteLine("== 测试11: 大编队寻路预算确定性（墙体 + 132 单位）==");
			var a = CreateWorld();
			var b = CreateWorld();

			// 一堵贯穿全场的墙，迫使每个单位都走真实 A*（预算按 ID 顺序分摊// 一堵贯穿全场的墙，迫使每个单位都走真实 A*（预算按 ID 顺序分摊）
			for (int y = -10; y <= 10; y++)
			{
				a.Grid.StaticObstacles.Add(new SimVector2I(1, y));
				b.Grid.StaticObstacles.Add(new SimVector2I(1, y));
			}
			a.Grid.SyncStaticBlocking();
			b.Grid.SyncStaticBlocking();

			for (int i = 0; i < 120; i++)
			{
				var ua = new SimUnit(a.GetNextEntityId(), 1, new FPVector2((FP)(-192m), (FP)(-60 + i * 2)));
				ua.MaxSpeed = (FP)200;
				ua.Radius = (FP)15;
				a.AddUnit(ua);

				var ub = new SimUnit(b.GetNextEntityId(), 1, new FPVector2((FP)(-192m), (FP)(-60 + i * 2)));
				ub.MaxSpeed = (FP)200;
				ub.Radius = (FP)15;
				b.AddUnit(ub);
			}

			// 同一 Tick 全部下令过墙：预算不足的进入 PathPending，后续按 ID 顺序补算
			foreach (var u in new List<SimUnit>(a.Units.Values))
				u.CommandMove(a, new FPVector2((FP)320m, u.Position.Y));
			foreach (var u in new List<SimUnit>(b.Units.Values))
				u.CommandMove(b, new FPVector2((FP)320m, u.Position.Y));

			bool ok = true;
			for (int tick = 0; tick < 240; tick++)
			{
				a.Tick();
				b.Tick();

				if (a.GetWorldHash() != b.GetWorldHash())
				{
					Console.WriteLine($"  第 {tick} Tick 哈希不一致！ A={a.GetWorldHash()} B={b.GetWorldHash()}");
					ok = false;
					break;
				}
			}

			int pendingA = a.Units.Values.Count(u => u.PathPending);
			int pendingB = b.Units.Values.Count(u => u.PathPending);
			Check(pendingA == pendingB, $"双世界挂起路径数一致（{pendingA}）");
			Check(pendingA == 0, "240 Tick 内所有路径已算完");
			Check(ok, "墙体大编队双世界哈希完全一致");
		}

		private static int Main(string[] args)
		{
			if (Array.IndexOf(args, "--wall-probe") >= 0) return RunWallProbe();

			Console.WriteLine("RTS 确定性核心测试开始");
			RunScriptedComparison();
			RunHashSensitivity();
			RunPathfindingTest();
			RunCreepHashTest();
			RunSectionHashTest();
			RunCombatBuffTest();
			RunCarpetSpreadTest();
			RunTechSimTest();
			RunLongMixedScenario();
			RunFormationTest();
			RunLargeGroupPathBudgetTest();
			RunRegressionTests();
			RunBlueprintTests();
			RunExpressionTests();
			RunTriggerTests();
			RunMapEditorTests();
			RunMapValidatorTests();
			RunTutorialTests();
			Console.WriteLine(_failures == 0 ? "全部测试通过" : $"{_failures} 项测试失败");
			return _failures == 0 ? 0 : 1;
		}
	}
}
