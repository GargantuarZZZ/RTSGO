using System.Reflection;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace SimulationDeterminismTest
{
    internal static partial class Program
    {
        private sealed class ShepherdAllianceRules : ISimulationRules
        {
            public bool AreTeamsHostile(int a, int b) => a != b && !(a <= 2 && b <= 2);
            public FP GetLifestealFraction(int team) => FP.Zero;
            // Teams 1 and 2 are allies in this fixture, mirroring AreTeamsHostile above.
            public bool AreTeamsFriendly(int a, int b) => a > 0 && b > 0 && (a == b || (a <= 2 && b <= 2));
        }

        private static void RunShepherdPushTests()
        {
            var resolve = typeof(SimWorld).GetMethod("ResolveEntityCollisions", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (int allyTeam in new[] { 1, 2 })
            foreach (bool shepherdFirst in new[] { true, false })
            {
                var world = new SimWorld(rules: new ShepherdAllianceRules());
                for (int x = -8; x <= 8; x++)
                    for (int y = -8; y <= 8; y++) world.Grid.TerrainCells.Add(new SimVector2I(x, y));
                var shepherd = new SimUnit(shepherdFirst ? 1 : 2, 1, new FPVector2((FP)32, (FP)32))
                    { UnitTypeId = "Shepherd", Radius = (FP)20 };
                var ally = new SimUnit(shepherdFirst ? 2 : 1, allyTeam, new FPVector2((FP)42, (FP)32))
                    { UnitTypeId = "Builder", Radius = (FP)20 };
                world.AddUnit(shepherd);
                world.AddUnit(ally);
                world.SpatialGrid.Rebuild(world.Units.Values);
                new SwarmManager().CalculateVelocity(shepherd, world);
                Check(shepherd.Velocity.MagnitudeSquared() == FP.Zero, "allied separation does not move shepherd");
                resolve.Invoke(world, null);
                Check(shepherd.Position.X == (FP)32 && shepherd.Position.Y == (FP)32 && ally.Position.X == (FP)72,
                    "allied collision moves only the ally in either entity ID order");
                ally.Position = new FPVector2((FP)42, (FP)32);
                ally.UnitTypeId = "Shepherd";
                resolve.Invoke(world, null);
                Check(shepherd.Position.X == (FP)32 && ally.Position.X == (FP)42, "allied shepherds do not displace each other");
                ally.Position = new FPVector2((FP)400, (FP)400);
                shepherd.CommandMove(new FPVector2((FP)300, (FP)32));
                for (int tick = 0; tick < 10; tick++) world.Tick();
                Check(shepherd.Position.X > (FP)32, "protected shepherd can still move under orders");

                shepherd.HasTarget = false;
                shepherd.Position = new FPVector2((FP)32, (FP)32);
                ally.Position = new FPVector2((FP)42, (FP)32);
                ally.TeamID = 3;
                world.SpatialGrid.Rebuild(world.Units.Values);
                new SwarmManager().CalculateVelocity(shepherd, world);
                Check(shepherd.Velocity.X < FP.Zero, "enemy separation still affects shepherd");
                resolve.Invoke(world, null);
                Check(shepherd.Position.X < (FP)32 && ally.Position.X > (FP)42, "enemy collision retains mutual displacement");
            }
        }
    }
}
