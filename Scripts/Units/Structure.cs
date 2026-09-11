// File: res://Scripts/Units/Structure.cs
using Godot;
using System.Collections.Generic;
using Godot.Collections;
using RTS.Data;
using RTS.World;
using RTS.Actions;
using RTS.Actions.Implementation;
using RTS.Core;

// 引入模拟层与定点数结构
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	[GlobalClass]
	// 使用 Node3D，不依赖 Godot 物理
	public partial class Structure : Node3D, IEntity
	{
		public enum StructureState { Blueprint, Construction, Completed }

		[Export] public int TeamID { get; set; } = 0;
		[Export] public string StructureName { get; set; } = "Building";
		[Export] public Texture2D IconProfile;
		[Export] public int GridSize { get; set; } = 2;
		[Export] public bool IsResourceDropOff = false;
		[Export] public Array<ResourceType> AcceptableResources = new();
		[Export] public float VisionRange { get; set; } = 500.0f;
		// 代码工厂已按配置表装好数值/武器时置 true，场景加载的建筑走 _Ready 补配置
		public bool FactoryConfigured = false;

		[ExportGroup("Placement Rules")]
		[Export] public CreepType RequiredCreep { get; set; } = CreepType.Any;

		[ExportGroup("Modules")]
		[Export] public UnitLife LifeModule;
		[Export] public UnitVisuals VisualsModule;
		[Export] public UnitActionController Brain;
		[Export] public UnitCombat CombatModule;
		[Export] public CreepSource CreepModule;
		// 行为模块缓存：模拟线程禁止 GetNodeOrNull，_Ready 一次性缓存
		public AutoBuildBehavior AutoBuildCache;
		public AutoHarvestBehavior AutoHarvestCache;

		[ExportGroup("Construction")]
		[Export] public Godot.Collections.Dictionary<ResourceType, float> OriginalCosts { get; set; } = new();
		[Export] public int MaxBuilders { get; set; } = 2;

		public StructureState CurrentState { get; private set; } = StructureState.Completed;
		public bool IsUnderConstruction => CurrentState == StructureState.Construction;
		public float ConstructionProgress { get; private set; } = 0f;
		public Vector2I GridPosition { get; private set; }
		public List<OrderData> RallyQueue { get; } = new();

		private readonly List<IEntity> _builders = new();
		protected NavigationObstacle2D Obstacle;
		private bool _deathVisualStarted = false;
		private bool _destructionStarted;

		public string DisplayName => StructureName;
		public Texture2D Icon => IconProfile;

		// IEntity 显式实现：接口侧返回逻辑平面 XZ
		Vector2 IEntity.GlobalPosition =>
			SimStructureData != null
				? new Vector2((float)SimStructureData.Position.X, (float)SimStructureData.Position.Y)
				: new Vector2(GlobalPosition.X, GlobalPosition.Z);

		// ==========================================
		// 后台逻辑数据
		// ==========================================
		public SimStructure SimStructureData { get; protected set; }
		public SimEntity LogicEntity => SimStructureData;

		// --- IEntity 接口显式实现 ---
		UnitLife IEntity.LifeModule => LifeModule;
		UnitVisuals IEntity.VisualsModule => VisualsModule;
		UnitActionController IEntity.Brain => Brain;
		UnitCombat IEntity.CombatModule => CombatModule;
		UnitHarvest IEntity.HarvestModule => null;
		bool IEntity.IsStructure => true;

		public override void _Ready()
		{
			AddToGroup("entities");
			AddToGroup("structures");

			LifeModule ??= GetNodeOrNull<UnitLife>("Modules/UnitLife");
			VisualsModule ??= GetNodeOrNull<UnitVisuals>("Modules/UnitVisuals");
			Brain ??= GetNodeOrNull<UnitActionController>("Modules/UnitActionController");
			CombatModule ??= GetNodeOrNull<UnitCombat>("Modules/UnitCombat");
			Obstacle = GetNodeOrNull<NavigationObstacle2D>("NavigationObstacle2D");

			ApplyConfigFromTableIfNeeded();

			// 行为模块补挂：场景加载的建筑也要按配置挂上自动施工/自动采集模块（与工厂一致）
			var structCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
			if (structCfg != null)
			{
				if (structCfg.GlobalVision)
					SetMeta("GlobalVision", true);

				if (structCfg.AutoBuild || structCfg.RequiresSacrifice)
				{
					var autoBuild = GetNodeOrNull<AutoBuildBehavior>("AutoBuild");
					if (autoBuild == null)
						AddChild(new AutoBuildBehavior { Name = "AutoBuild", BuildTime = structCfg.BuildTime });
					else if (autoBuild.BuildRangeTiles <= 0)
						autoBuild.BuildRangeTiles = 1;
				}

				if (structCfg.AutoHarvestRadiusTiles > 0 && structCfg.AutoHarvestPerSecondPerNode > 0f &&
					GetNodeOrNull<AutoHarvestBehavior>("AutoHarvest") == null)
					AddChild(new AutoHarvestBehavior
					{
						Name = "AutoHarvest",
						RadiusTiles = structCfg.AutoHarvestRadiusTiles,
						RatePerSecondPerNode = structCfg.AutoHarvestPerSecondPerNode
					});
			}

			// 动作面板完全由配置表驱动：场景/工厂都不再手写动作树
			EntityFactory3D.RebuildStructureActions(this);
			InitializeModules();

			if (IsUnderConstruction) EnsureCancelAction();

			AutoBuildCache = GetNodeOrNull<AutoBuildBehavior>("AutoBuild");
			AutoHarvestCache = GetNodeOrNull<AutoHarvestBehavior>("AutoHarvest");

			SetProcess(true);

			// 核心修复：绝对禁止使用 CallDeferred 注册逻辑，必须在当前逻辑 Tick 栈内同步完成
			SnapAndRegister();

			if (CurrentState == StructureState.Completed)
			{
				// 直接生成/预置的成品建筑也要发放时代科技（否则调试地图里 T3 永远点不出）。
				GrantProvidedTechs();
				Brain?.StartAction("Idle", true);
			}
		}

		public override void _Process(double delta)
		{
			// 洞穴民兵化：按模拟层驻扎伤害同步远程武器（主线程镜像）
			SyncMilitiaGarrisonWeapon();
		}

		private void SyncMilitiaGarrisonWeapon()
		{
			if (SimStructureData == null || CombatModule == null)
				return;

			int damage = SimStructureData.MilitiaGarrisonDamage;
			var mount = GetNodeOrNull<Node3D>("Visuals/WeaponMount");
			if (mount == null)
				return;

			Weapon existing = null;
			foreach (Node child in mount.GetChildren())
			{
				if (child is Weapon w && w.WeaponName == "CaveMilitiaRanged")
				{
					existing = w;
					break;
				}
			}

			if (damage <= 0)
			{
				if (existing != null)
				{
					mount.RemoveChild(existing);
					CombatModule.RemoveWeapon(existing);
					existing.QueueFree();
				}

				return;
			}

			if (existing == null)
			{
				existing = RTS.Core.EntityFactory3D.CreateWeaponNode("CaveMilitiaRanged");
				mount.AddChild(existing);
				CombatModule.AddWeapon(existing);
			}

			if (existing.Damage != damage)
				existing.Damage = damage;
		}

		// 场景预制体只保留结构，数值/武器全部从配置表读取，保证与代码工厂一致
		private void ApplyConfigFromTableIfNeeded()
		{
			if (FactoryConfigured)
				return;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
			if (cfg == null)
				return;

			// 数值统一走工厂的 ApplyStructureStats（预制体与代码工厂共用一份）
			RTS.Core.EntityFactory3D.ApplyStructureStats(this, StructureName);

			var mount = GetNodeOrNull<Node3D>("Visuals/WeaponMount");
			if (mount == null)
				return;

			RTS.Core.EntityFactory3D.EnsureWeaponsOnMount(mount, cfg.WeaponIds);
		}

		protected virtual void InitializeModules()
		{
			LifeModule?.Initialize(this);
			VisualsModule?.Initialize(this, this);
			Brain?.Initialize(this);
			CombatModule?.Initialize(this);
			CreepModule ??= GetNodeOrNull<CreepSource>("Modules/CreepSource");
			CreepModule?.Initialize(this);

			// Use a managed delegate thunk instead of native method-name dispatch on this node.
			if (LifeModule != null) LifeModule.Died += () => OnDestroyed();

			if (Obstacle != null)
			{
				Obstacle.AvoidanceEnabled = true;
				Obstacle.Vertices = PhysicsUtils.GetCollisionOutline(GetNodeOrNull<CollisionShape2D>("CollisionShape2D"));
			}
		}

		protected virtual SimStructure CreateSimStructure(int id, int teamId, FPVector2 centerPos, SimVector2I gridPos, int gridSize)
		{
			return new SimStructure(id, teamId, centerPos, gridPos, gridSize);
		}

		protected virtual void SnapAndRegister()
		{
			if (MapGrid.Instance == null || !GodotObject.IsInstanceValid(this)) return;

			GridPosition = MapGrid.Instance.GetTopLeftFromCenter(
				MapGrid.Instance.WorldToGrid(new Vector2(GlobalPosition.X, GlobalPosition.Z)),
				GridSize
			);
			Vector2 aligned = MapGrid.Instance.GetAlignedWorldPos(GridPosition, GridSize);
			GlobalPosition = new Vector3(aligned.X, 0f, aligned.Y);

			// 蓝图/工地/完工都必须注册定点数数据，
			// 否则寻路读取 _targetStruct.LogicEntity.Position 会空引用
			if (SimStructureData == null)
			{
				SimStructureData = CreateSimStructure(
					id: RTS.Core.SimManager.Instance.World.GetNextEntityId(),
					teamId: TeamID,
					centerPos: new FPVector2((FixMath.NET.Fix64)GlobalPosition.X, (FixMath.NET.Fix64)GlobalPosition.Z),
					gridPos: new RTS.Simulation.SimVector2I(GridPosition.X, GridPosition.Y),
					gridSize: GridSize
				);
				RTS.Core.SimManager.Instance.World.AddStructure(SimStructureData);
				RTS.Core.SimManager.Instance.RegisterEntityNode(this);
				SimStructureData.StructureTypeId = StructureName;
				SimStructureData.VisionRange = (FP)VisionRange;

				var structCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
				if (structCfg != null)
				{
					SimStructureData.FieldRadius = structCfg.FieldRadius;
					SimStructureData.LifespanTimer = (FP)structCfg.LifespanSeconds;
					SimStructureData.AmmoRangeTiles = structCfg.AmmoRangeTiles;
					SimStructureData.AmmoRangeAffectsAir = structCfg.AmmoRangeAffectsAir;
					SimStructureData.IsAir = structCfg.IsAir;
				}

			}

			// 开局直接生成（默认 Completed）的建筑，逻辑状态也要同步成 Active，
			// 否则研究/自动采集等按 Active 判定的功能会永远失效
			SimStructureData.CurrentState = CurrentState switch
			{
				StructureState.Blueprint => RTS.Simulation.SimStructure.StructureState.Blueprint,
				StructureState.Construction => RTS.Simulation.SimStructure.StructureState.Constructing,
				_ => RTS.Simulation.SimStructure.StructureState.Active
			};

			// 开建后才向网格注册占地阻挡
			if (CurrentState != StructureState.Blueprint)
			{
				if (!MapGrid.Instance.RegisterStructure(GridPosition, GridSize, this))
					GD.PrintErr($"[Structure] 注册失败: {StructureName} @ {GridPosition}");
			}
		}

		public void InitAsBlueprint(Godot.Collections.Dictionary<ResourceType, float> costs)
		{
			if (CurrentState == StructureState.Completed)
			{
				MapGrid.Instance?.UnregisterStructure(GridPosition, GridSize);
			}

			CurrentState = StructureState.Blueprint;
			OriginalCosts = costs;
			ConstructionProgress = 0f;
			if (SimStructureData != null)
			{
				SimStructureData.CurrentState = RTS.Simulation.SimStructure.StructureState.Blueprint;
				SimStructureData.ConstructionProgress = FixMath.NET.Fix64.Zero;
			}

			SnapAndRegister(); // 再次注册，这次它是 Blueprint，不会霸占网格了

			// 再次安全校验：如果是别人同一帧抢了这块地，才触发真取消
			if (MapGrid.Instance?.IsAreaEmpty(GridPosition, GridSize) == false)
			{
				CancelConstruction();
				return;
			}

			VisualsModule?.SetBlueprintVisual(true);
			VisualsModule?.SetConstructionVisual(false);
			Visible = Main.Instance?.LocalPlayerID == TeamID;
			SetPhysicsAndLogic(false);
			EnsureCancelAction();
		}

		public bool PromoteFromBlueprint()
		{
			if (CurrentState != StructureState.Blueprint) return false;

			if (MapGrid.Instance?.IsAreaEmpty(GridPosition, GridSize) == false)
			{
				// 良性竞争（多名工人同时转施工/蓝图被取消重试），已退款取消，不算错误
				string occ = "";
				if (MapGrid.Instance != null)
				{
					for (int gx = GridPosition.X; gx < GridPosition.X + GridSize; gx++)
					{
						for (int gy = GridPosition.Y; gy < GridPosition.Y + GridSize; gy++)
						{
							var cell = new Vector2I(gx, gy);
							if (MapGrid.Instance.IsCellEmpty(cell))
								continue;
							occ += $"{cell}:{MapGrid.Instance.GetOccupantName(cell)} ";
						}
					}
				}
				GD.Print($"[Build] Promote 让位(区域被占) {StructureName} @ {GridPosition} Team {TeamID} 占用={occ.Trim()} 已退款取消");
				CancelConstruction();
				return false;
			}

			// P0-1：数据部分（BuildAction 可能在模拟线程调用 Promote，只能同步改纯数据状态）
			CurrentState = StructureState.Construction;
			if (SimStructureData != null)
			{
				SimStructureData.CurrentState = RTS.Simulation.SimStructure.StructureState.Constructing;
			}

			// 视觉/物理/网格占用副作用必须回主线程（Godot 线程检查会拦截模拟线程的节点调用）
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				SnapAndRegister();
				VisualsModule?.SetBlueprintVisual(false);
				VisualsModule?.SetConstructionVisual(true);
				Visible = true;
				SetPhysicsAndLogic(true);
				LifeModule?.SetHealthRaw(LifeModule.MaxHp * 0.1f);
			});

			return true;
		}

		private void SetPhysicsAndLogic(bool enabled)
		{
			// 不设置 Godot 原生碰撞层，只控制障碍与大脑的 ProcessMode
			if (Obstacle != null)
			{
				Obstacle.ProcessMode = enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
				Obstacle.AvoidanceEnabled = enabled;
			}
			if (Brain != null) Brain.ProcessMode = ProcessModeEnum.Inherit;
		}

		public bool CanAddBuilder() => IsUnderConstruction && _builders.Count < MaxBuilders;
		public void AddBuilder(IEntity b) { if (!_builders.Contains(b)) _builders.Add(b); }
		public void RemoveBuilder(IEntity b) => _builders.Remove(b);
		public bool HasBuilder(IEntity b) => _builders.Contains(b);

		public void AdvanceProgress(float amount)
		{
			if (!IsUnderConstruction) return;
			ConstructionProgress = Mathf.Clamp(ConstructionProgress + amount, 0f, 1f);
			if (SimStructureData != null)
			{
				SimStructureData.ConstructionProgress = (FixMath.NET.Fix64)ConstructionProgress;
			}
			if (ConstructionProgress >= 1f)
			{
				CurrentState = StructureState.Completed;
				if (SimStructureData != null)
				{
					SimStructureData.CurrentState = RTS.Simulation.SimStructure.StructureState.Active;
				}
				GrantProvidedTechs();
				EnqueueCompletionSpawns();

				// 多足单位蓝图：把结果单位挂起，由 SimManager 安全生成后移除蓝图
				if (StructureName.StartsWith("Blueprint_") && SimStructureData != null)
				{
					SimStructureData.PendingSpawnUnitIds.Add(StructureName.Substring("Blueprint_".Length));
					OnDestroyed(false);
					return;
				}

				// P0-1：视觉/节点副作用延迟到主线程（模拟线程禁止碰 Godot 节点与动作缓存）
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					if (!GodotObject.IsInstanceValid(this))
						return;
					VisualsModule?.SetBlueprintVisual(false);
					VisualsModule?.SetConstructionVisual(false);
					Brain?.GetAction<GeneralCancelAction>("Cancel")?.QueueFree();
				});

				Brain?.StartAction("Idle", true);
			}
		}

		// 建筑提供时代解锁：例如 VF 完成 → UnionT2，太空船坞完成 → UnionT3
		private void GrantProvidedTechs()
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
			if (cfg == null || cfg.ProvidesTechIds.Count == 0)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(TeamID);
			if (player?.PlayerData == null)
				return;

			foreach (string techId in cfg.ProvidesTechIds)
				player.PlayerData.GrantTech(techId);
		}

		// 建成时赠送单位（太空船坞送战巡 / 恶魔祭坛堡垒送英雄）：挂起待生成
		private void EnqueueCompletionSpawns()
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(StructureName);
			if (cfg == null || cfg.BonusUnitOnCompleteIds.Count == 0 || SimStructureData == null)
				return;

			foreach (string unitId in cfg.BonusUnitOnCompleteIds)
				SimStructureData.PendingSpawnUnitIds.Add(unitId);
		}

		public void CancelConstruction()
		{
			GD.Print($"[Build] 取消 {StructureName} state={CurrentState} team={TeamID} grid={GridPosition} progress={ConstructionProgress:F2}");
			float refundRate = CurrentState switch {
				StructureState.Blueprint => 1.0f,
				StructureState.Construction => 0.75f,
				_ => 0f
			};

			if (refundRate > 0 && OriginalCosts != null && OriginalCosts.Count > 0)
			{
				var player = Game.GetPlayerByTeam(TeamID);
				if (player?.PlayerData != null)
				{
					foreach (var kvp in OriginalCosts)
					{
						player.PlayerData.AddResource(kvp.Key, kvp.Value * refundRate);
					}
				}
			}
			// 取消蓝图/施工不是死亡：直接消失，不播下沉动画
			OnDestroyed(false);
		}

		public List<EntityAction> GetAvailableActions()
		{
			var list = new List<EntityAction>();
			if (IsDeadOrNull() || Brain == null) return list;

			foreach (Node child in Brain.GetChildren())
			{
				// 隐藏负槽位动作（轨道中心技能已移到顶部面板，不再占建筑卡）
				if (child is UnitAction ua && ua.SlotIndex < 0)
					continue;

				if (child is GeneralCancelAction c) list.Add(Data(c));
				else if (child is RTS.Actions.Implementation.ResearchAction ra)
				{
					// 研究按钮常驻显示（不满足条件时置灰，而不是消失）
					if (CurrentState == StructureState.Completed)
						list.Add(Data(ra));
				}
				else if (child is RTS.Actions.Implementation.TrainUnitAction tu)
				{
					// 训练按钮常驻显示：科技未解锁/资源不足时置灰，而不是消失
					if (CurrentState == StructureState.Completed)
						list.Add(Data(tu));
				}
				else if (child is RTS.Actions.Implementation.RadarAction rad ||
						 child is RTS.Actions.Implementation.OrbitalStrikeAction os ||
						 child is RTS.Actions.Implementation.ResourceExchangeAction ex ||
						 child is RTS.Actions.Implementation.ResonanceWaveAction ||
						 child is RTS.Actions.Implementation.SeismicWaveAction ||
						 child is RTS.Actions.Implementation.WormholeCreateAction ||
						 child is RTS.Actions.Implementation.OmniLeafDogAction ||
						 child is RTS.Actions.Implementation.PlantForestSpreadAction)
				{
					// 面板技能按钮常驻显示：能量不足时置灰，而不是消失
					if (CurrentState == StructureState.Completed)
						list.Add(Data((UnitAction)child));
				}
				else if (CurrentState == StructureState.Completed && child is UnitAction u && u.CanExecute()) list.Add(Data(u));
			}

			// 自杀：摧毁自身（不走普通指令队列，由 SimManager 确定性处理）
			list.Add(new EntityAction
			{
				ActionId = "SelfDestruct",
				DisplayName = RTS.Settings.Localization.Tr("action.SelfDestruct"),
				SlotIndex = 13,
				Tooltip = RTS.Settings.Localization.Tr("action.SelfDestructTip")
			});

			return list;
		}

		private EntityAction Data(UnitAction a)
		{
			var act = RTS.Data.ActionViewFactory.Build(a);

			// 已研究完成的科技：按钮变绿
			if (a is RTS.Actions.Implementation.ResearchAction ra)
			{
				var player = RTS.World.Game.GetPlayerByTeam(TeamID);
				if (player?.PlayerData != null && player.PlayerData.HasTech(ra.TechId))
					act.Tint = Colors.Green;
			}

			return act;
		}

		private void EnsureCancelAction()
		{
			if (Brain != null && !Brain.HasNode("Cancel"))
			{
				var act = new GeneralCancelAction { Name = "Cancel" };
				Brain.AddChild(act);
				act.Initialize(this);
			}
		}

		public void AddRallyCommand(OrderData cmd, bool append) { if (!append) RallyQueue.Clear(); RallyQueue.Add(cmd); }

		public virtual bool IsDeadOrNull() => !GodotObject.IsInstanceValid(this) || (LifeModule?.IsDead ?? false);

		public virtual void OnDestroyed()
		{
			OnDestroyed(true);
		}

		protected virtual void OnDestroyed(bool playDeathVisual)
		{
			if (_destructionStarted) return;
			_destructionStarted = true;
			CreepModule?.ClearCreep();
			Brain?.StopAll();

			// 视觉副作用一律回主线程（OnDestroyed 可能在模拟线程触发：建筑死亡/蓝图取消），
			// 否则跨线程 SetSelected/Obstacle/死亡动画会触发 "can only be accessed from the main thread"
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				if (Obstacle != null) { Obstacle.AvoidanceEnabled = false; Obstacle.SetNavigationMap(new Rid()); }
				if (Main.Instance?.LocalPlayerID == TeamID) VisualsModule?.SetSelected(false);
				// 建筑记忆：脱视野时被摧毁才留灰色幽灵；取消蓝图/施工不算被摧毁，不留幽灵
				if (playDeathVisual && VisualsModule?.IsFogHidden == true && RTS.World.FogOfWar.Instance != null)
					RTS.World.FogOfWar.Instance.RememberStructureGhost(this);
				if (playDeathVisual)
					StartDeathVisual();
			});

			MapGrid.Instance?.UnregisterStructure(GridPosition, GridSize);

			// 标记逻辑死亡，交由 SimWorld 清理
			if (SimStructureData != null)
			{
				// 泰伦：驻扎的工程师在建筑被摧毁时回到战场
				if (SimStructureData.GarrisonedCount > 0 && RTS.Core.EntitySpawner.Instance != null)
				{
					int count = SimStructureData.GarrisonedCount;
					SimStructureData.GarrisonedCount = 0;

					for (int i = 0; i < count; i++)
					{
						RTS.Core.EntitySpawner.Instance.SpawnEntity(
							"Engineer",
							TeamID,
							new RTS.Simulation.FPVector2(
								SimStructureData.Position.X + (FP)((i - 1) * 96),
								SimStructureData.Position.Y));
					}
				}

				SimStructureData.IsDead = true;
				SimStructureData.CurrentState = RTS.Simulation.SimStructure.StructureState.Destroyed;
				RTS.Core.SimManager.Instance.UnregisterEntityNode(SimStructureData.ID);
			}

			if (!playDeathVisual)
				// P0-1：取消蓝图/施工可能在模拟线程触发，释放节点延迟到主线程派发屏障
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					if (GodotObject.IsInstanceValid(this))
						QueueFree();
				});
		}

		// 死亡动画：灰化 + 缓慢沉入地底，3 秒后消失（与单位共用 DeathVisuals）
		private void StartDeathVisual()
		{
			if (_deathVisualStarted)
				return;

			_deathVisualStarted = true;
			RTS.Core.DeathVisuals.PlaySinkAndFree(this, 0f, () =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				VisualsModule?.OnUnitDied();
			});
		}
	}
}
