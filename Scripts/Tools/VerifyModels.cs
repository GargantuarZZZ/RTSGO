using Godot;
using System.Collections.Generic;

namespace RTS.Core
{
	// 开发期自检：确认 UnitModelLibrary 里每条映射的 GLB 在**运行时**真的能加载，
	// 并打印包围盒，方便核对 Scale/OffsetY 有没有配错（模型沉地/离地）。
	//
	// 用法：
	//   godot --headless --path . res://Scenes/Tools/VerifyModels.tscn -- --race=Wizard
	//   godot --headless --path . res://Scenes/Tools/VerifyModels.tscn          （全部）
	//
	// 为什么需要它：AI3D 生成的 GLB 是运行时直接加载的，如果 .import 没生成、
	// 路径写错、或模型本来就是空网格，表现层只会安静地掉回占位方块 —— 肉眼看不出来。
	public partial class VerifyModels : Node
	{
		private static readonly Dictionary<string, string[]> RaceIds = new()
		{
			["Wizard"] = new[]
			{
				"WizPuppet", "WizApprentice", "WizHospitalMage", "WizBattlePuppet",
				"WizBroomRider", "WizElementMage", "WizMusician", "WizStoneGolem", "WizEarthGolem",
				"WizCity", "WizConcertHall", "WizElementForge", "WizForest", "WizHouse",
				"WizMageTower", "WizSchool", "WizSpaceLab", "WizStoneCircle", "WizWaterWall",
			},
			["AICommand"] = new[]
			{
				"AIBaseCar", "AIKamikaze", "AIQuadDrone", "AIAirSuperiority", "AIInterceptor",
				"AIElectronicWarfare", "AIBomber", "AIArsenalBird",
				"AICore", "AIGoldStation", "AIRelay", "AILightFactory", "AIAirFactory",
				"AIHeavyAirfield", "AISkyNetCore",
			},
		};

		public override void _Ready() => CallDeferred(nameof(Run));

		private void Run()
		{
			string only = null;
			foreach (string a in OS.GetCmdlineUserArgs())
				if (a.StartsWith("--race=")) only = a.Substring(7);

			int ok = 0, missing = 0, broken = 0;
			foreach (var pair in RaceIds)
			{
				if (only != null && pair.Key != only)
					continue;
				GD.Print($"[Verify] ===== 种族 {pair.Key} =====");
				foreach (string id in pair.Value)
				{
					var info = UnitModelLibrary.Get(id);
					if (info == null)
					{
						GD.PrintErr($"[Verify] MAP-MISSING {id}：UnitModelLibrary 里没有映射");
						missing++;
						continue;
					}
					if (!ResourceLoader.Exists(info.ModelPath))
					{
						GD.PrintErr($"[Verify] FILE-MISSING {id} -> {info.ModelPath}");
						missing++;
						continue;
					}

					var packed = GD.Load<PackedScene>(info.ModelPath);
					if (packed == null)
					{
						GD.PrintErr($"[Verify] LOAD-FAIL {id} -> {info.ModelPath}");
						broken++;
						continue;
					}
					var inst = packed.Instantiate<Node3D>();
					AddChild(inst);
					var aabb = Measure(inst);
					string bbox = aabb.Size == Vector3.Zero
						? "(无网格)"
						: $"size=({aabb.Size.X:F1},{aabb.Size.Y:F1},{aabb.Size.Z:F1}) y=({aabb.Position.Y:F1}..{aabb.End.Y:F1})";
					GD.Print($"[Verify] OK {id,-22} scale={info.Scale,-8:F2} offsetY={info.OffsetY,-8:F2} {bbox}");
					if (aabb.Size == Vector3.Zero) broken++;
					else ok++;
					RemoveChild(inst);
					inst.QueueFree();
				}
			}
			GD.Print($"[Verify] 汇总：OK={ok} 缺映射/文件={missing} 加载或空网格={broken}");
			GetTree().Quit(missing + broken > 0 ? 1 : 0);
		}

		private static Aabb Measure(Node node)
		{
			Aabb? acc = null;
			if (node is MeshInstance3D mi && mi.Mesh != null)
				acc = mi.GetAabb();
			foreach (Node child in node.GetChildren())
			{
				var sub = Measure(child);
				if (sub.Size == Vector3.Zero)
					continue;
				acc = acc == null ? sub : acc.Value.Merge(sub);
			}
			return acc ?? new Aabb();
		}
	}
}
