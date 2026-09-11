using Godot;
using RTS.Data.Configs;

namespace RTS.Test
{
	public partial class RefactorSmokeTest : Node
	{
		public override void _Ready()
		{
			try
			{
				try
				{
					ConfigDatabase.LoadAll("res://__missing_config_regression__");
					throw new System.Exception("Missing configuration directory was accepted.");
				}
				catch (System.IO.DirectoryNotFoundException) { }
				if (ConfigDatabase.IsLoaded || ConfigDatabase.GetRace("Union") != null)
					throw new System.Exception("Failed load left a published database.");
				ConfigDatabase.LoadAll();
				foreach (string race in new[] { "Union", "Nano", "Demon", "Wanderer", "Terran", "Plant", "Cave" })
					if (ConfigDatabase.GetRace(race) == null)
						throw new System.Exception($"Missing race {race} after retry.");
				var manager = RTS.Core.SimManager.Instance;
				if (!object.ReferenceEquals(manager.World.SyncRoot, manager.WorldLock) ||
					!object.ReferenceEquals(manager.World.Rules, manager))
					throw new System.Exception("Host simulation wiring differs from the expected lock/rules.");
				manager.ResetSimulation();
				if (!object.ReferenceEquals(manager.World.SyncRoot, manager.WorldLock) ||
					!object.ReferenceEquals(manager.World.Rules, manager))
					throw new System.Exception("Reset lost the host lock/rules.");
				GD.Print("REFACTOR_SMOKE_PASS");
				GetTree().Quit();
			}
			catch (System.Exception error)
			{
				GD.PrintErr($"REFACTOR_SMOKE_FAIL: {error}");
				GetTree().Quit(1);
			}
		}
	}
}
