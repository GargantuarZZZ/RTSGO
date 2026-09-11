// File: res://Scripts/Units/SpecialUnits/ShrineStructure.cs

using Godot;
using System.Linq;
using RTS.Data;
using RTS.Core;
using RTS.World;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	[GlobalClass]
	public partial class ShrineStructure : Structure
	{
		[ExportGroup("Shrine Settings")]
		[Export] public float CaptureRadius = 250.0f;
		[Export] public float CaptureTime = 5.0f;
		[Export] public ResourceType RewardType = ResourceType.Money;
		[Export] public float RewardAmountPerSecond = 5.0f;

		private MeshInstance3D _ringMesh;
		private StandardMaterial3D _ringMaterial;
		private MeshInstance3D _progressBg;
		private MeshInstance3D _progressFill;
		private StandardMaterial3D _progressMaterial;

		public float CaptureProgressRatio
		{
			get
			{
				if (SimStructureData is SimShrine shrine && CaptureTime > 0f)
					return (float)(shrine.CaptureProgress / (FP)CaptureTime);
				return 0f;
			}
		}

		public override void _Ready()
		{
			// 必须先定阵营再注册逻辑实体，保证 SimShrine.TeamID 一开始就是中立
			TeamID = -1;
			base._Ready();
			SetMeta("GlobalVision", true);
			BuildShrineVisual3D();
		}

		private void BuildShrineVisual3D()
		{
			// 占领光圈
			_ringMesh = new MeshInstance3D
			{
				Name = "CaptureRing",
				Mesh = new CylinderMesh
				{
					TopRadius = CaptureRadius,
					BottomRadius = CaptureRadius,
					Height = 1f,
					RadialSegments = 48
				},
				Position = new Vector3(0f, 1f, 0f)
			};
			_ringMaterial = new StandardMaterial3D
			{
				AlbedoColor = new Color(1f, 1f, 1f, 0.15f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};
			_ringMesh.MaterialOverride = _ringMaterial;
			AddChild(_ringMesh);

			// 占领进度条（头顶）
			float barY = GridSize * 64f + 30f;
			_progressBg = MakeProgressQuad(new Color(0f, 0f, 0f, 0.8f), barY, 0f);
			_progressFill = MakeProgressQuad(Colors.Green, barY, 0.1f);
			_progressMaterial = _progressFill.MaterialOverride as StandardMaterial3D;
		}

		private MeshInstance3D MakeProgressQuad(Color color, float y, float z)
		{
			var mi = new MeshInstance3D
			{
				Mesh = new QuadMesh { Size = new Vector2(160f, 10f) },
				Position = new Vector3(0f, y, z)
			};
			mi.MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = color,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};
			AddChild(mi);
			return mi;
		}

		public override void _Process(double delta)
		{
			if (_ringMaterial == null)
				return;

			int localId = Main.Instance?.LocalPlayerID ?? 0;
			int shrineTeam = SimStructureData?.TeamID ?? -1;
			FP captureProgress = SimStructureData is SimShrine shrine ? shrine.CaptureProgress : FP.Zero;
			int capturingTeam = SimStructureData is SimShrine s2 ? s2.CapturingTeam : -1;

			// 光圈颜色：中立白 / 己方绿 / 敌方红 / 争夺黄
			if (shrineTeam == localId)
				_ringMaterial.AlbedoColor = new Color(0f, 1f, 0f, 0.18f);
			else if (shrineTeam >= 0)
				_ringMaterial.AlbedoColor = new Color(1f, 0f, 0f, 0.18f);
			else if (captureProgress > FP.Zero)
				_ringMaterial.AlbedoColor = new Color(1f, 1f, 0f, 0.25f);
			else
				_ringMaterial.AlbedoColor = new Color(1f, 1f, 1f, 0.15f);

			// 进度条：只显示“正在被占领且尚未归属该队”
			bool showProgress = captureProgress > FP.Zero && shrineTeam != capturingTeam;
			_progressBg.Visible = showProgress;
			_progressFill.Visible = showProgress;

			if (showProgress)
			{
				float ratio = Mathf.Clamp(CaptureProgressRatio, 0f, 1f);
				_progressFill.Scale = new Vector3(ratio, 1f, 1f);
				_progressFill.Position = new Vector3(160f * (ratio - 1f) * 0.5f, _progressFill.Position.Y, 0.1f);
				_progressMaterial.AlbedoColor = capturingTeam == localId ? Colors.Green : Colors.Red;
			}
		}

		protected override SimStructure CreateSimStructure(int id, int teamId, FPVector2 centerPos, SimVector2I gridPos, int gridSize)
		{
			return new SimShrine(id, teamId, centerPos, gridPos, gridSize);
		}

		// 由 SimManager 在每个锁步 Tick 内调用，全部使用逻辑层定点数据，
		// 不再依赖渲染帧 / 场景树 / GlobalPosition，保证多端完全一致。
		public void LogicTick(double delta)
		{
			if (IsDeadOrNull() || CurrentState != StructureState.Completed)
				return;

			if (SimStructureData is not SimShrine shrine)
				return;

			var world = RTS.Core.SimManager.Instance.World;
			FP fixedDelta = world.FixedDelta;
			FP radius = (FP)CaptureRadius;
			FP radiusSq = radius * radius;

			// 1. 占领判定：只依赖逻辑层单位位置（SortedDictionary 顺序确定）
			var teamsPresent = new System.Collections.Generic.HashSet<int>();

			foreach (var simUnit in world.Units.Values)
			{
				if (simUnit == null || simUnit.IsDead)
					continue;

				if (FPVector2.IsWithinRangeSq(simUnit.Position, shrine.Position, radiusSq))
					teamsPresent.Add(simUnit.TeamID);
			}

			if (teamsPresent.Count == 1)
			{
				int presentTeam = teamsPresent.First();

				if (presentTeam != shrine.TeamID)
				{
					if (shrine.CapturingTeam != presentTeam)
					{
						shrine.CapturingTeam = presentTeam;
						shrine.CaptureProgress = FP.Zero;
					}

					shrine.CaptureProgress += fixedDelta;

					if (shrine.CaptureProgress >= (FP)CaptureTime)
					{
						shrine.TeamID = presentTeam;
						shrine.CapturingTeam = presentTeam;
						shrine.CaptureProgress = FP.Zero;

						// 同步表现层归属（仅渲染用途）
						TeamID = presentTeam;
						VisualsModule?.UpdateTeamColor();
						GD.Print($"[Shrine] 阵营 {presentTeam} 占领了圣地！");
					}
				}
				else
				{
					shrine.CaptureProgress = FP.Zero;
				}
			}
			else if (teamsPresent.Count == 0 && shrine.CaptureProgress > FP.Zero)
			{
				shrine.CaptureProgress -= fixedDelta;
				if (shrine.CaptureProgress < FP.Zero)
					shrine.CaptureProgress = FP.Zero;
			}
			// teamsPresent.Count > 1：双方都在圈内，争夺中进度暂停

			// 2. 占领后按秒发放资源
			if (shrine.TeamID >= 0)
			{
				shrine.RewardTimer += fixedDelta;

				if (shrine.RewardTimer >= FP.One)
				{
					shrine.RewardTimer -= FP.One;

					var player = Game.GetPlayerByTeam(shrine.TeamID);
					if (player != null && player.PlayerData != null)
					{
						player.PlayerData.AddResource(RewardType, (FP)RewardAmountPerSecond);
					}
				}
			}

		}
	}
}
