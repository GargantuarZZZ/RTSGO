using Godot;
using System;
using System.Collections.Generic;
using RTS.Units;

namespace RTS.Core
{
	/// <summary>
	/// 顶点动画纹理（VAT）实例化渲染：把骨骼动画烘焙成纹理，用 MultiMesh 批量绘制，
	/// 每个单位只占用一个实例槽位，动画在 GPU 上按 TIME 播放，draw calls 与单位数量无关。
	/// 数据由 Scripts/Tools/VatBakeTool.gd 离线烘焙，存放在 res://Data/VAT/&lt;模型名&gt;/。
	/// </summary>
	public static partial class UnitVatRenderer
	{
		private const int MaxInstances = 2048;
		// 每实例 20 个 float：变换 12 + 颜色 4 + custom_data 4（顺序由 BufferLayoutProbe 实测确认）
		private const int FloatsPerInstance = 20;
		private const int TotalFloats = MaxInstances * FloatsPerInstance;

		// 隐藏单位用“远下方合法矩阵”，绝不用零缩放（退化 AABB 会导致整屏闪烁）。
		public static readonly Transform3D HiddenTransform =
			new Transform3D(Basis.Identity, new Vector3(0f, -100000f, 0f));
		// 主线程每帧缓存的 delta，避免每单位重复调用 GetProcessDeltaTime()
		public static float LastFrameDelta = 0f;

		private class ModelVat
		{
			public MultiMeshInstance3D Node;
			public MultiMesh Multimesh;
			public Dictionary<string, int> AnimStart = new();
			public Dictionary<string, int> AnimLength = new();
			public string IdleName = "";
			public string RunName = "";
			public int InstanceCount = 0;
			public int MaxUsedSlot = -1;
			public readonly Stack<int> FreeSlots = new();
			public bool UseColors = true;
			public float[] Buffer;
			public bool BufferDirty = false;
		}

		private static readonly Dictionary<string, ModelVat> _models = new();
		private static readonly Dictionary<string, Mesh> _sourceMeshCache = new();
		// 批量单位视觉统一由 VatFlusher 驱动（关闭各自 _Process，避免 2000 次引擎分发）
		private static readonly List<(UnitVisuals Visual, Unit Unit)> _managedVisuals = new();
		private static Node _parent;
		private static Node _flusher;
		private static bool _visibleDirty = false;

		private partial class VatFlusher : Node
		{
			public override void _Process(double delta)
			{
				foreach (var (visual, unit) in _managedVisuals)
				{
					if (!GodotObject.IsInstanceValid(visual) || !GodotObject.IsInstanceValid(unit))
						continue;
					if (unit.SimUnitData == null || unit.SimUnitData.IsDead)
						continue;
					visual.UpdateBatch();
					// 节点位置只随模拟 tick 吸附（20Hz），模型平滑由 MultiMesh 负责
					int curTick = RTS.Network.LockstepManager.Instance?.CurrentTick ?? -1;
					if (curTick != visual.LastSnapTick)
					{
						visual.LastSnapTick = curTick;
						if (visual.TryGetVisualTarget(out var targetPos))
							unit.GlobalPosition = targetPos;
					}
				}
				UnitVatRenderer.FlushPerFrame((float)delta);
			}
		}

		public static void RegisterManagedVisual(UnitVisuals visual, Unit unit)
		{
			_managedVisuals.Add((visual, unit));
		}

		public static void UnregisterManagedVisual(UnitVisuals visual)
		{
			for (int i = _managedVisuals.Count - 1; i >= 0; i--)
			{
				if (_managedVisuals[i].Visual == visual)
					_managedVisuals.RemoveAt(i);
			}
		}

		public static void EnsureParent(Node parent)
		{
			if (parent == null)
				return;

			if (_parent == null || !GodotObject.IsInstanceValid(_parent) || !_parent.IsInsideTree())
			{
				if (_parent != null)
				{
					_models.Clear();
					_sourceMeshCache.Clear();
				}
				_parent = parent;
				if (_flusher == null || !GodotObject.IsInstanceValid(_flusher))
				{
					// 低优先级：保证在所有单位更新完实例数据后再统一上传
					_flusher = new VatFlusher { Name = "VatFlusher", ProcessPriority = -100000 };
					_parent.AddChild(_flusher);
				}
			}
		}

		/// <summary>每帧末尾统一上传实例缓冲与绘制范围，避免逐单位跨语言调用。</summary>
		public static void FlushPerFrame(float delta)
		{
			LastFrameDelta = delta;
			foreach (var kv in _models)
			{
				var m = kv.Value;
				if (m.Multimesh == null)
					continue;
				if (_visibleDirty)
				{
					int count = m.MaxUsedSlot + 1;
					if (count != m.Multimesh.VisibleInstanceCount)
						m.Multimesh.VisibleInstanceCount = count;
				}
				if (m.BufferDirty)
				{
					m.Multimesh.Buffer = m.Buffer;
					m.BufferDirty = false;
				}
			}
			_visibleDirty = false;
		}

		/// <summary>模型是否已有烘焙好的 VAT 数据（animation_material.tres 存在）。</summary>
		public static bool HasVat(string modelPath)
		{
			if (string.IsNullOrEmpty(modelPath))
				return false;
			return ResourceLoader.Exists(VatDir(modelPath) + "/animation_material.tres");
		}

		private static string VatDir(string modelPath)
		{
			return "res://Data/VAT/" + modelPath.GetFile().GetBaseName();
		}

		public static int Register(string modelPath)
		{
			if (_parent == null)
				return -1;

			if (!_models.TryGetValue(modelPath, out var model))
			{
				model = BuildModel(modelPath);
				if (model == null)
					return -1;
				_models[modelPath] = model;
			}

			int slot = model.FreeSlots.Count > 0 ? model.FreeSlots.Pop() : model.InstanceCount++;
			if (slot >= MaxInstances)
			{
				GD.PrintErr($"[UnitVat] 实例槽位耗尽: {modelPath} (slot={slot}, max={MaxInstances})");
				return -1;
			}

			if (slot > model.MaxUsedSlot)
				model.MaxUsedSlot = slot;
			_visibleDirty = true;
			return slot;
		}

		public static void Unregister(string modelPath, int slot)
		{
			if (slot < 0 || !_models.TryGetValue(modelPath, out var model) || slot >= model.InstanceCount)
				return;

			model.FreeSlots.Push(slot);
			WriteSlotHidden(model, slot);
			if (slot == model.MaxUsedSlot)
			{
				while (model.MaxUsedSlot >= 0 && model.FreeSlots.Contains(model.MaxUsedSlot))
					model.MaxUsedSlot--;
				_visibleDirty = true;
			}
		}

		public static void SetTransform(string modelPath, int slot, Transform3D xf)
		{
			if (slot < 0 || !_models.TryGetValue(modelPath, out var model) || slot >= model.InstanceCount)
				return;

			if (!IsFinite(xf))
				xf = HiddenTransform;
			WriteTransform(model.Buffer, slot, xf);
			model.BufferDirty = true;
		}

		public static void SetHidden(string modelPath, int slot)
		{
			SetTransform(modelPath, slot, HiddenTransform);
		}

		public static void SetColor(string modelPath, int slot, Color color)
		{
			if (slot < 0 || !_models.TryGetValue(modelPath, out var model) || slot >= model.InstanceCount)
				return;
			if (model.UseColors)
			{
				int o = slot * FloatsPerInstance + 12;
				model.Buffer[o] = color.R;
				model.Buffer[o + 1] = color.G;
				model.Buffer[o + 2] = color.B;
				model.Buffer[o + 3] = color.A;
				model.BufferDirty = true;
			}
		}

		/// <summary>切换实例动画（token 为 UnitModelLibrary 里的 Idle/Move 名称，做模糊匹配）。</summary>
		public static void Play(string modelPath, int slot, string token, float blendDuration = 0.15f)
		{
			if (slot < 0 || !_models.TryGetValue(modelPath, out var model) || slot >= model.InstanceCount)
				return;
			Play(model, slot, ResolveName(model, token), blendDuration);
		}

		public static string DebugSummary()
		{
			var sb = new System.Text.StringBuilder();
			foreach (var kv in _models)
				sb.Append($"[{kv.Key}] count={kv.Value.InstanceCount} free={kv.Value.FreeSlots.Count} ");
			return sb.ToString();
		}

		private static string ResolveName(ModelVat model, string token)
		{
			if (string.IsNullOrEmpty(token))
				return model.IdleName;
			if (model.AnimStart.ContainsKey(token))
				return token;
			foreach (var key in model.AnimStart.Keys)
			{
				if (key.Contains(token, System.StringComparison.OrdinalIgnoreCase))
					return key;
			}
			return model.IdleName;
		}

		private static void Play(ModelVat model, int slot, string fullName, float blendDuration)
		{
			SetAnimation(model, slot, fullName, blendDuration);
		}

		private static bool IsFinite(Transform3D xf)
		{
			Vector3 o = xf.Origin;
			if (!float.IsFinite(o.X) || !float.IsFinite(o.Y) || !float.IsFinite(o.Z))
				return false;
			Basis b = xf.Basis;
			return IsFinite(b.Row0) && IsFinite(b.Row1) && IsFinite(b.Row2);
		}

		private static bool IsFinite(Vector3 v)
		{
			return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
		}

		private static ModelVat BuildModel(string modelPath)
		{
			var dir = VatDir(modelPath);
			var material = ResourceLoader.Load<Material>(dir + "/animation_material.tres");
			var mesh = LoadSourceMesh(modelPath);
			if (material == null || mesh == null)
			{
				GD.PrintErr($"[UnitVat] 烘焙数据缺失: {modelPath} (material={material != null}, mesh={mesh != null})");
				return null;
			}

			var model = new ModelVat();
			if (!LoadAnimationMeta(model, dir))
				return null;

			var mm = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				UseCustomData = true,
				UseColors = true,
				Mesh = mesh,
				// 固定预分配，运行期不改 InstanceCount，避免缓冲区变化导致整屏闪烁；
				// 空闲槽位用远下方合法矩阵承载。
				InstanceCount = MaxInstances,
				VisibleInstanceCount = MaxInstances
			};

			// 不用 addon 的 GDScript 节点：动画播放数据（custom_data）直接按
			// AnimatedMultiMeshInstance3D 的 Forward+ 编码格式写入，避免运行时依赖全局类缓存。
			var node = new MultiMeshInstance3D();
			node.Name = "VatBatch_" + modelPath.GetFile().GetBaseName();
			node.MaterialOverride = material;
			_parent.AddChild(node);
			node.Multimesh = mm;
			model.Node = node;
			model.Multimesh = mm;

			// 全部槽位预填：隐藏变换 + 白色 + Idle 动画数据，一次性写入实例缓冲。
			model.Buffer = new float[TotalFloats];
			float idleEncoded = model.AnimStart.TryGetValue(model.IdleName, out int idleStart)
				? EncodeTwoU16(idleStart, model.AnimLength[model.IdleName])
				: 0f;
			float idleBlend = EncodeTwoU16(2, 0);
			for (int i = 0; i < MaxInstances; i++)
			{
				WriteTransform(model.Buffer, i, HiddenTransform);
				int o = i * FloatsPerInstance + 12;
				model.Buffer[o] = 1f;
				model.Buffer[o + 1] = 1f;
				model.Buffer[o + 2] = 1f;
				model.Buffer[o + 3] = 1f;
				model.Buffer[o + 4] = idleEncoded;
				model.Buffer[o + 5] = idleEncoded;
				model.Buffer[o + 6] = 0f;
				model.Buffer[o + 7] = idleBlend;
			}
			mm.Buffer = model.Buffer;

			return model;
		}

		/// <summary>直接写入动画 custom_data（与 addon 的 Forward+ 编码一致）。</summary>
		private static void SetAnimation(ModelVat model, int slot, string fullName, float blendDuration)
		{
			if (model.Buffer == null || !model.AnimStart.TryGetValue(fullName, out int start))
				return;
			int length = model.AnimLength[fullName];
			float encoded = EncodeTwoU16(start, length);
			// r=主动画(start,len) g=混合动画(start,len) b=混合时间戳 a=混合入/出时长(1/16 秒单位)
			float timestamp = (float)((Time.GetTicksMsec() % 3_600_000ul) / 1000.0) - 0.5f;
			float blend = EncodeTwoU16((int)(Mathf.Max(0f, blendDuration) * 16f), 0);
			int o = slot * FloatsPerInstance + 16;
			model.Buffer[o] = encoded;
			model.Buffer[o + 1] = encoded;
			model.Buffer[o + 2] = timestamp;
			model.Buffer[o + 3] = blend;
			model.BufferDirty = true;
		}

		private static void WriteTransform(float[] buf, int slot, Transform3D xf)
		{
			int o = slot * FloatsPerInstance;
			Basis b = xf.Basis;
			Vector3 origin = xf.Origin;
			buf[o] = b.Row0.X; buf[o + 1] = b.Row0.Y; buf[o + 2] = b.Row0.Z; buf[o + 3] = origin.X;
			buf[o + 4] = b.Row1.X; buf[o + 5] = b.Row1.Y; buf[o + 6] = b.Row1.Z; buf[o + 7] = origin.Y;
			buf[o + 8] = b.Row2.X; buf[o + 9] = b.Row2.Y; buf[o + 10] = b.Row2.Z; buf[o + 11] = origin.Z;
		}

		private static void WriteSlotHidden(ModelVat model, int slot)
		{
			WriteTransform(model.Buffer, slot, HiddenTransform);
			int o = slot * FloatsPerInstance + 12;
			model.Buffer[o] = 1f;
			model.Buffer[o + 1] = 1f;
			model.Buffer[o + 2] = 1f;
			model.Buffer[o + 3] = 1f;
			model.BufferDirty = true;
		}

		private static float EncodeTwoU16(int a, int b)
		{
			uint bits = ((uint)a & 0xFFFFu) | (((uint)b & 0xFFFFu) << 16);
			return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
		}

		private static bool LoadAnimationMeta(ModelVat model, string dir)
		{
			var jsonPath = ProjectSettings.GlobalizePath(dir + "/animations.json");
			if (!System.IO.File.Exists(jsonPath))
			{
				GD.PrintErr($"[UnitVat] 缺少动画元数据: {jsonPath}");
				return false;
			}

			var text = System.IO.File.ReadAllText(jsonPath);
			var parsed = Json.ParseString(text).AsGodotDictionary();
			foreach (var kv in parsed)
			{
				var name = kv.Key.ToString();
				var data = kv.Value.AsGodotDictionary();
				var start = (int)data["start"].AsInt64();
				var length = (int)data["length"].AsInt64();
				model.AnimStart[name] = start;
				model.AnimLength[name] = length;
				if (model.IdleName == "" && name.Contains("Idle", System.StringComparison.OrdinalIgnoreCase))
					model.IdleName = name;
				if (model.RunName == "" && (name.Contains("Run", System.StringComparison.OrdinalIgnoreCase) ||
					name.Contains("Walk", System.StringComparison.OrdinalIgnoreCase)))
					model.RunName = name;
			}

			if (model.IdleName == "" && model.AnimStart.Count > 0)
				model.IdleName = FirstKey(model.AnimStart);
			if (model.RunName == "")
				model.RunName = model.IdleName;
			return model.AnimStart.Count > 0;
		}

		private static string FirstKey(Dictionary<string, int> dict)
		{
			foreach (var key in dict.Keys)
				return key;
			return "";
		}

		private static Mesh LoadSourceMesh(string modelPath)
		{
			if (_sourceMeshCache.TryGetValue(modelPath, out var cached))
				return cached;

			var packed = ResourceLoader.Load<PackedScene>(modelPath);
			if (packed == null)
				return null;

			var root = packed.Instantiate() as Node3D;
			Mesh found = null;
			if (root != null)
			{
				found = FindSkinnedMesh(root);
				root.Free();
			}
			if (found == null)
				return null;

			_sourceMeshCache[modelPath] = found;
			return found;
		}

		private static Mesh FindSkinnedMesh(Node node)
		{
			if (node is MeshInstance3D mi && mi.Skin != null && mi.Mesh != null)
				return mi.Mesh;
			foreach (Node child in node.GetChildren())
			{
				var found = FindSkinnedMesh(child);
				if (found != null)
					return found;
			}
			return null;
		}
	}
}
