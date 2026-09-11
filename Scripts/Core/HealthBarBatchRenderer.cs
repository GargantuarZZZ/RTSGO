using Godot;
using System.Collections.Generic;

namespace RTS.Core
{
	// 血条批渲染：普通单位血条（黑色背景 + 绿色填充）合入两个 MultiMesh，
	// 400 个单位从 800 个 MeshInstance3D + 800 次绘制降到 2 次绘制。
	// 当前处于停用状态（仅批渲染单位会用到，而 IsBatchableModel 返回 false）。
	public static class HealthBarBatchRenderer
	{
		// 同上：预分配避免动态增长实例数导致闪烁
		private const int MaxInstances = 2048;
	// 隐藏槽位不用零缩放矩阵，避免 GPU 侧退化矩阵产生垃圾帧（同 UnitVatRenderer）
		private static readonly Transform3D HiddenTransform =
			new Transform3D(Basis.Identity, new Vector3(0f, -100000f, 0f));

		private struct BarState
		{
			public Vector3 Pos;
			public float Width;
			public float Ratio;
			public bool Visible;
		}

		private static MultiMeshInstance3D _bgNode;
		private static MultiMeshInstance3D _fillNode;
		private static MultiMesh _bgMesh;
		private static MultiMesh _fillMesh;
		private static Node _parent;
		private static int _count = 0;
		private static readonly Stack<int> _free = new();
		private static readonly Dictionary<int, BarState> _state = new();
		// 实例缓冲（float[]）：主线程写入，每帧一次赋给 MultiMesh.buffer，避免逐实例上传
		private static float[] _bgBuffer;
		private static float[] _fillBuffer;
		private static bool _bufferDirty = false;

		public static void EnsureParent(Node parent)
		{
			if (_parent == null && parent != null)
				_parent = parent;
		}

		public static int Register()
		{
			EnsureNodes();
			int slot = _free.Count > 0 ? _free.Pop() : _count++;
			if (slot >= MaxInstances)
			{
				GD.PrintErr($"[HealthBar] 批渲染实例槽位耗尽 (slot={slot}, max={MaxInstances})");
				return -1;
			}
			_state[slot] = new BarState { Pos = Vector3.Zero, Width = 48f, Ratio = 1f, Visible = true };
			WriteHidden(slot, _bgBuffer);
			WriteHidden(slot, _fillBuffer);
			_bufferDirty = true;
			return slot;
		}

		public static void Unregister(int slot)
		{
			if (slot < 0 || slot >= _count)
				return;

			_free.Push(slot);
			WriteHidden(slot, _bgBuffer);
			WriteHidden(slot, _fillBuffer);
			_bufferDirty = true;
		}

		public static void Update(int slot, Vector3 pos, float width, float ratio, bool visible)
		{
			if (slot < 0 || !_state.TryGetValue(slot, out var st))
				return;

			if (st.Pos == pos && Mathf.IsEqualApprox(st.Width, width) &&
				Mathf.IsEqualApprox(st.Ratio, ratio) && st.Visible == visible)
				return;

			st.Pos = pos;
			st.Width = width;
			st.Ratio = ratio;
			st.Visible = visible;
			_state[slot] = st;

			if (!visible || ratio <= 0f)
			{
				WriteHidden(slot, _bgBuffer);
				WriteHidden(slot, _fillBuffer);
				_bufferDirty = true;
				return;
			}

			WriteTransform(slot, _bgBuffer, new Vector3(width, 8f, 1f), pos);

			float fillW = width * ratio;
			var fillPos = pos + new Vector3((fillW - width) * 0.5f, 0f, 0.1f);
			WriteTransform(slot, _fillBuffer, new Vector3(fillW, 6f, 1f), fillPos);
			_bufferDirty = true;
		}

		/// <summary>主线程每帧调用：一次上传全部血条实例数据。</summary>
		public static void Flush()
		{
			if (!_bufferDirty || _bgMesh == null)
				return;
			_bgMesh.Buffer = _bgBuffer;
			_fillMesh.Buffer = _fillBuffer;
			_bufferDirty = false;
		}

		private static void EnsureNodes()
		{
			if (_bgNode != null)
				return;

			var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
			_bgMesh = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				Mesh = quad,
				InstanceCount = MaxInstances,
	// 固定预分配，避免 InstanceCount 变化导致缓冲区重建闪烁
				VisibleInstanceCount = MaxInstances
			};
			_fillMesh = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				Mesh = quad,
				InstanceCount = MaxInstances,
				VisibleInstanceCount = MaxInstances
			};
			// 血条 MultiMesh 只用 Transform3D（无颜色/自定义数据），每实例 12 个 float
			_bgBuffer = new float[MaxInstances * 12];
			_fillBuffer = new float[MaxInstances * 12];
			for (int i = 0; i < MaxInstances; i++)
			{
				WriteHidden(i, _bgBuffer);
				WriteHidden(i, _fillBuffer);
			}
			_bgMesh.Buffer = _bgBuffer;
			_fillMesh.Buffer = _fillBuffer;

			_bgNode = new MultiMeshInstance3D { Name = "HealthBarBatchBg", Multimesh = _bgMesh };
			_fillNode = new MultiMeshInstance3D { Name = "HealthBarBatchFill", Multimesh = _fillMesh };
			_bgNode.MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0f, 0f, 0f, 1f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};
			_fillNode.MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = Colors.Green,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};

			_parent.AddChild(_bgNode);
			_parent.AddChild(_fillNode);
		}

		private static void WriteHidden(int slot, float[] buf)
		{
			WriteTransform(slot, buf, Vector3.One, HiddenTransform.Origin);
		}

		private static void WriteTransform(int slot, float[] buf, Vector3 scale, Vector3 pos)
		{
			int o = slot * 12;
			buf[o] = scale.X; buf[o + 1] = 0f; buf[o + 2] = 0f; buf[o + 3] = pos.X;
			buf[o + 4] = 0f; buf[o + 5] = scale.Y; buf[o + 6] = 0f; buf[o + 7] = pos.Y;
			buf[o + 8] = 0f; buf[o + 9] = 0f; buf[o + 10] = scale.Z; buf[o + 11] = pos.Z;
		}

		public static string DebugSummary()
		{
			return $"count={_count} free={_free.Count}";
		}
	}
}
