using Godot;

namespace RTS.Core
{
	// 临时工具：打印常用 GLB 模型的节点树（含重复名检测）。用完即删。
	public partial class InspectModelTree : Node3D
	{
		private static readonly string[] Files =
		{
			"res://ArtRes/models/Round Rover.glb",
			"res://ArtRes/models/Rover.glb",
			"res://ArtRes/models/Astronaut.glb",
			"res://ArtRes/models/Mech-D5wW2jDO42.glb"
		};

		public override void _Ready()
		{
			CallDeferred(nameof(DoInspect));
		}

		private void DoInspect()
		{
			foreach (string file in Files)
			{
				GD.Print($"[Tree] ===== {file} =====");
				var packed = GD.Load<PackedScene>(file);
				if (packed == null)
				{
					GD.Print("[Tree] 加载失败");
					continue;
				}

				var root = packed.Instantiate<Node3D>();
				AddChild(root);
				PrintNode(root, 0);
				root.QueueFree();
			}

			GetTree().Quit();
		}

		private static void PrintNode(Node node, int depth)
		{
			string pad = new string(' ', depth * 2);
			string extra = node is MeshInstance3D ? " [mesh]" : "";
			GD.Print($"[Tree] {pad}{node.Name} ({node.GetType().Name}){extra}");

			foreach (Node child in node.GetChildren())
				PrintNode(child, depth + 1);
		}
	}
}
