using Godot;

namespace RTS.Core
{
	// 临时工具：把所有预制体里的 Model 实例“拍平”成普通本地节点，
	// 消除实例 + 可编辑子节点导致的节点重名警告。用完即删。
	public partial class FlattenPrefabModels : Node3D
	{
		public override void _Ready()
		{
			CallDeferred(nameof(DoFlatten));
		}

		private void DoFlatten()
		{
			int flattened = 0, skipped = 0, failed = 0;

			foreach (string path in FindTscn("res://Scenes/Races"))
			{
				var packed = GD.Load<PackedScene>(path);
				if (packed == null)
				{
					GD.PrintErr($"[Flatten] 无法加载 {path}");
					failed++;
					continue;
				}

				var root = packed.Instantiate<Node3D>();
				var model = root.GetNodeOrNull<Node3D>("Model");

				if (model == null)
				{
					skipped++;
					root.Free();
					continue;
				}

				var flat = (Node3D)model.Duplicate(15);
				root.RemoveChild(model);
				model.Free();
				root.AddChild(flat);

				SetOwnerRecursive(root, root);

				var outPacked = new PackedScene();
				outPacked.Pack(root);
				Error err = ResourceSaver.Save(outPacked, path);

				if (err == Error.Ok)
					flattened++;
				else
				{
					GD.PrintErr($"[Flatten] 保存失败 {path} ({err})");
					failed++;
				}

				root.Free();
			}

			GD.Print($"[Flatten] 完成: flattened={flattened} skipped={skipped} failed={failed}");
			GetTree().Quit();
		}

		private static System.Collections.Generic.List<string> FindTscn(string dir)
		{
			var result = new System.Collections.Generic.List<string>();
			using var d = DirAccess.Open(dir);
			if (d == null)
				return result;

			d.ListDirBegin();
			string file = d.GetNext();

			while (file != "")
			{
				if (d.CurrentIsDir())
				{
					if (!file.StartsWith("."))
						result.AddRange(FindTscn($"{dir}/{file}"));
				}
				else if (file.EndsWith(".tscn"))
				{
					result.Add($"{dir}/{file}");
				}

				file = d.GetNext();
			}

			return result;
		}

		private static void SetOwnerRecursive(Node node, Node owner)
		{
			foreach (Node child in node.GetChildren())
			{
				child.Owner = owner;
				SetOwnerRecursive(child, owner);
			}
		}
	}
}
