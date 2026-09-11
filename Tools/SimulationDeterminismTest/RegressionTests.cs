using System.Collections.Generic;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
	internal static partial class Program
	{
		private static void RunRegressionTests()
		{
			RunShepherdPushTests();
			RunWallProbe(regression: true);
			System.Console.WriteLine("== Regression: path retry and world isolation ==");
			var a = CreateRetryWorld();
			var b = CreateRetryWorld();
			var ua = a.Units[1000];
			var ub = b.Units[1000];
			var target = new FPVector2((FP)(-320), (FP)320);
			foreach (var u in new[] { ua, ub })
			{
				// A path ended without progress while blocked. Its target is now reachable.
				u.Path = new List<FPVector2> { u.Position };
				u.CurrentWaypointIndex = 1;
				u.FinalTargetPosition = target;
				u.PathBuildDistSq = FPVector2.DistanceSquared(u.Position, target);
				u.ShortPathRetryTicks = 1;
				u.HasTarget = false;
				u.CommandMove(target);
			}
			Check(!ua.HasTarget && !ub.HasTarget, "retry waits while cooldown is active");
			a.Tick();
			b.Tick();
			ua.CommandMove(target);
			ub.CommandMove(target);
			Check(ua.HasTarget && ub.HasTarget, "expired retry resumes movement instead of restarting cooldown forever");
			Check(ua.Path != null && ua.Path.Count > 1, "retry replaces exhausted path after obstacle clears");
			bool same = true;
			for (int i = 0; i < 120; i++)
			{
				a.Tick();
				b.Tick();
				same &= a.GetWorldHash() == b.GetWorldHash();
			}
			Check(same, "retry is deterministic over 120 ticks");

			var isolatedA = CreateWorld();
			var isolatedB = CreateWorld();
			long untouchedHash = isolatedB.GetWorldHash();
			isolatedA.Units[1000].CommandMove(target);
			Check(isolatedB.GetWorldHash() == untouchedHash && !object.ReferenceEquals(isolatedA.SyncRoot, isolatedB.SyncRoot),
				"convenience command and lock belong to the unit's world");
			var sharedLock = new object();
			Check(object.ReferenceEquals(new SimWorld(sharedLock).SyncRoot, sharedLock), "host synchronization lock is preserved");

			var rules = new TestSimulationRules();
			var customWorld = new SimWorld(rules: rules);
			var shooter = new SimUnit(1, 1, FPVector2.Zero) { Hp = (FP)50, MaxHp = (FP)100 };
			var victim = new SimUnit(2, 2, FPVector2.Zero) { Hp = FP.One, MaxHp = FP.One };
			customWorld.AddUnit(shooter);
			customWorld.AddUnit(victim);
			victim.TakeDamage((FP)10, 0, source: shooter);
			Check(victim.IsDead && shooter.Hp > (FP)50 && rules.LifestealReads == 1, "kill effect reads injected technology rules");
			FP afterKill = shooter.Hp;
			victim.TakeDamage((FP)10, 0, source: shooter);
			Check(shooter.Hp == afterKill && rules.LifestealReads == 1, "dead targets cannot grant repeated kill effects");

			var quiet = CreateWorld();
			var verbose = CreateWorld();
			int messages = 0;
			verbose.DiagnosticSink = _ => messages++;
			quiet.Units[1000].CommandMove(target);
			verbose.Units[1000].CommandMove(target);
			bool diagnosticsSame = true;
			for (int i = 0; i < 60; i++)
			{
				quiet.Tick();
				verbose.Tick();
				diagnosticsSame &= quiet.GetWorldHash() == verbose.GetWorldHash();
			}
			Check(diagnosticsSame, "diagnostics enabled/disabled preserve tick hashes");
#if DEBUG
			Check(messages > 0, "debug diagnostics reach the injected sink");
#endif
		}

		private static SimWorld CreateRetryWorld()
		{
			var world = new SimWorld();
			for (int x = -8; x <= 8; x++)
				for (int y = -8; y <= 8; y++)
					world.Grid.TerrainCells.Add(new SimVector2I(x, y));
			world.AddUnit(new SimUnit(1000, 1, new FPVector2((FP)(-320), (FP)(-320)))
				{ Hp = (FP)100, MaxHp = (FP)100, Radius = (FP)15 });
			return world;
		}

		private sealed class TestSimulationRules : ISimulationRules
		{
			public int LifestealReads;
			public bool AreTeamsHostile(int a, int b) => a != b;
			public FP GetLifestealFraction(int teamId) { LifestealReads++; return (FP)0.25m; }
			public bool AreTeamsFriendly(int a, int b) => a > 0 && b > 0 && a == b;
		}
	}
}
