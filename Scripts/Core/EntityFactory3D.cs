using Godot;
using System.Collections.Generic;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Units;
using RTS.Actions;
using RTS.Actions.Implementation;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	// 实体工厂：负责创建单位/建筑的 3D 骨架（模块、数值、武器、范围显示）。
	// 动作面板不再在这里逐单位手写，统一由 RebuildUnitActions / RebuildStructureActions
	// 从配置表生成（Unit._Ready / Structure._Ready 与工厂共用同一入口）。
	public static class EntityFactory3D
	{
		// =========================================================
		// 配置表查询（无配置时返回 null，调用方使用默认值兜底）
		// =========================================================

		private static UnitConfig GetUnitCfg(string id) => ConfigDatabase.GetUnit(id);
		private static StructureConfig GetStructCfg(string id) => ConfigDatabase.GetStructure(id);
		private static WeaponConfig GetWeaponCfg(string id) => ConfigDatabase.GetWeapon(id);

		public static void ApplyUnitStats(Unit unit, string id)
		{
			var cfg = GetUnitCfg(id);
			if (cfg == null)
				return;

			var life = unit.GetNode<UnitLife>("Modules/UnitLife");
			life.MaxHp = cfg.MaxHp;
			life.MaxShield = cfg.MaxShield;
			life.ArmorType = cfg.ArmorType;
			life.DefKinetic = cfg.DefKinetic;
			life.DefThermal = cfg.DefThermal;
			life.DefExplosive = cfg.DefExplosive;
			life.DefEM = cfg.DefEM;
			life.DefBeam = cfg.DefBeam;
			unit.VisionRange = cfg.VisionRange;
			unit.FlyHeight = cfg.IsAir ? cfg.FlyHeight : 0f;
			unit.FactoryConfigured = true;
		}

		public static void ApplyStructureStats(Structure structure, string id)
		{
			var cfg = GetStructCfg(id);
			if (cfg == null)
				return;

			structure.GridSize = cfg.GridWidth;
			structure.VisionRange = cfg.VisionRange;

			var life = structure.GetNodeOrNull<UnitLife>("Modules/UnitLife");
			if (life != null)
			{
				life.MaxHp = cfg.MaxHp;
				life.MaxShield = cfg.MaxShield;
				life.ArmorType = cfg.ArmorType;
			}

			structure.IsResourceDropOff = cfg.IsDropOffPoint;

			if (cfg.AcceptableResourceTypes.Count > 0)
				structure.AcceptableResources = cfg.AcceptableResourceTypes;

			if (cfg.RequiresCreep)
				structure.RequiredCreep = cfg.RequiredCreepType;

			structure.FactoryConfigured = true;
		}

		private static BuildAction CreateBuildAction(string structureId, int slot)
		{
			var cfg = GetStructCfg(structureId);

			return new BuildAction
			{
				Name = "Build_" + structureId,
				StructureName = structureId,
				Costs = cfg?.Costs ?? new Godot.Collections.Dictionary<ResourceType, float>(),
				GridSize = cfg?.GridWidth ?? 2,
				BuildTime = cfg?.BuildTime ?? 10f,
				SlotIndex = slot,
				Layer = ActionLayer.Movement | ActionLayer.Weapon,
				BlockingLayers = ActionLayer.Movement | ActionLayer.Weapon
			};
		}

		private static TrainUnitAction CreateTrainAction(string unitId, int slot)
		{
			var cfg = GetUnitCfg(unitId);

			return new TrainUnitAction
			{
				Name = unitId,
				UnitName = unitId,
				Costs = cfg?.Costs ?? new Godot.Collections.Dictionary<ResourceType, float>(),
				BuildTime = cfg?.BuildTime ?? 1f,
				SlotIndex = slot
			};
		}

		public static bool TryCreate(string name, int teamId, FPVector2 pos, out IEntity entity, out Node3D root)
		{
			entity = null;
			root = null;

			// 特殊实体（中立塔/圣地/蓝图/资源/巨兽）单独创建；
			// 普通单位/建筑全部走配置表兜底（原 74 个 case 白名单与 default 重复，已删除）。
			if (name.StartsWith("Blueprint_"))
				root = CreateUnitBlueprint(name, teamId, pos);
			else if (name == "Tower")
				root = CreateConfiguredBuilding(name, -2, pos);
			else if (name == "Shrine")
				root = CreateShrine(name, teamId, pos);
			else if (name == "NanoBehemoth")
				root = CreateNanoBehemoth(name, teamId, pos);
			else if (name == "IronOre" || name == "MetalMine")
				root = CreateResourceNode(name, teamId, pos, ResourceType.Metal, 4500f);
			else if (name == "GasSpring")
				root = CreateResourceNode(name, teamId, pos, ResourceType.Gas, 3000f);
			else if (name == "WildFruit")
				root = CreateResourceNode(name, teamId, pos, ResourceType.Biomass, 2000f);
			else if (GetUnitCfg(name) != null)
				root = CreateConfiguredUnit(name, teamId, pos);
			else if (GetStructCfg(name) != null)
				root = CreateConfiguredBuilding(name, teamId, pos);
			else
				return false;

			if (root == null)
				return false;

			entity = root as IEntity;
			return entity != null;
		}

		// =========================================================
		// 单位（骨架/数值/武器/范围显示走配置表，动作由 RebuildUnitActions 统一生成）
		// =========================================================

		private static Node3D CreateConfiguredUnit(string name, int teamId, FPVector2 pos)
		{
			var cfg = GetUnitCfg(name);
			bool isWorker = cfg?.CanHarvest == true;

			var unit = new Unit
			{
				Name = name,
				TeamID = teamId,
				UnitName = name
			};
			SetupUnitCommon(unit, pos, cfg?.MaxHp ?? 100f, isWorker);
			ApplyUnitStats(unit, name);

			if (cfg != null)
			{
				var mount = unit.GetNode<Node3D>("Visuals/WeaponMount");
				int weaponIndex = 0;

				foreach (string weaponId in cfg.WeaponIds)
				{
					var weapon = CreateWeaponNode(weaponId);

					// 多武器：每个枪口分布在炮身周围，避免多条弹道/光束完全重叠
					if (cfg.WeaponIds.Count > 1)
					{
						float angle = weaponIndex * Mathf.Pi * 2f / cfg.WeaponIds.Count;
						weapon.Position = new Vector3(Mathf.Cos(angle) * 40f, 0f, Mathf.Sin(angle) * 40f);
					}

					mount.AddChild(weapon);
					weaponIndex++;
				}

				if (cfg.FieldRadius > 0)
				{
					unit.AddChild(new AuraRangeVisual
					{
						Name = "FieldRangeVisual",
						RingColor = new Color(0.95f, 0.25f, 0.2f, 0.22f),
						ClipToCircle = true
					});
				}

				if (cfg.ControlRangeTiles > 0)
				{
					unit.AddChild(new AuraRangeVisual
					{
						Name = "ControlRangeVisual",
						RingColor = new Color(0.35f, 0.7f, 1f, 0.2f),
						ClipToCircle = true
					});
				}

				if (cfg.IsWorker)
					unit.AddChild(new HarvesterMiningFX { Name = "HarvesterMiningFX" });
			}

			unit.InjectLogicPosition(pos);
			return unit;
		}

		private static Node3D CreateUnitBlueprint(string name, int teamId, FPVector2 pos)
		{
			var cfg = GetStructCfg(name);
			int grid = cfg?.GridWidth ?? 2;
			float hp = cfg?.MaxHp ?? 200f;

			var structure = CreateStructureBase(name, teamId, pos, grid, hp, false, false);
			structure.MaxBuilders = 3; // 多人施工加速：每个建造型各贡献一份进度
			return structure;
		}

		// 单位动作面板统一由配置表驱动（场景与代码工厂共用同一入口）
		public static void RebuildUnitActions(Unit unit)
		{
			var brain = unit.Brain ?? unit.GetNodeOrNull<UnitActionController>("Modules/UnitActionController");
			if (brain == null)
				return;

			ClearActionNodes(brain);

			var cfg = GetUnitCfg(unit.UnitName);
			bool canHarvest = cfg?.CanHarvest == true;
			bool pureHealer = cfg != null && cfg.CanHeal && !cfg.HasWeapon() && !cfg.CanHarvest && !cfg.CanBuild;

			AddCombatActions(brain, canHarvest, !pureHealer);
			AddMoveActions(brain);

			if (cfg == null)
				return;

			if (cfg.CanBuild)
			{
				int slot = 5;

				foreach (string buildId in cfg.BuildableStructureIds)
				{
					var buildAct = CreateBuildAction(buildId, slot++);
					// 默认近身贴蓝图（0.625 格）；远程建造单位按配置（如多足 Builder 5 格）
					buildAct.BuildRange = cfg.BuildRangeTiles > 0 ? cfg.BuildRangeTiles * 64f : 40f;
					AddAction(brain, buildAct);
				}
			}

			// 远程建造型：采集距离按独立配置设置（与建造距离脱钩）
			if (cfg.HarvestRangeTiles > 0 &&
				brain.GetAction<HarvestAction>("Harvest") is { } harvestAct)
				harvestAct.HarvestRangeTiles = cfg.HarvestRangeTiles;

			if (cfg.CanRepair)
			{
				AddAction(brain, new RepairAction
				{
					Name = "Repair",
					SlotIndex = -1,
					Layer = ActionLayer.Movement | ActionLayer.Weapon,
					BlockingLayers = ActionLayer.Movement | ActionLayer.Weapon
				});
			}

			if (cfg.IsTechBuilding)
				AddResearchActionsFromConfig(brain, unit.UnitName, 9);

			AddSkillAction(brain, cfg.Skill1Kind, cfg.Skill1Name, 1, 5);
			AddSkillAction(brain, cfg.Skill2Kind, cfg.Skill2Name, 2, 6);

			// 泰伦架设：可架设单位给“架设/收起”按钮
			if (cfg.CanDeploy)
			{
				AddAction(brain, new RTS.Actions.Implementation.DeployAction
				{
					Name = "Deploy",
					DisplayNameText = "架设/收起",
					SlotIndex = 7,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability | ActionLayer.Movement
				});
			}

			// 泰伦重装：切换弹种
			if (cfg.CanSwitchAmmoMode)
			{
				AddAction(brain, new RTS.Actions.Implementation.SwitchAmmoAction
				{
					Name = "SwitchAmmo",
					DisplayNameText = "切换弹种",
					SlotIndex = 8,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			// 泰伦工程兵：驻扎
			if (cfg.CanGarrison)
			{
				AddAction(brain, new RTS.Actions.Implementation.GarrisonAction
				{
					Name = "Garrison",
					DisplayNameText = "驻扎",
					// 槽位 -1：命令卡隐藏（避免与建造按钮挤位），右键进驻仍然可用
					SlotIndex = -1,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			// 洞穴工兵：基于地下残骸快速重建（右键残骸触发）
			if (cfg.CanBuild && cfg.BuildableStructureIds.Contains("CaveMainNest"))
			{
				AddAction(brain, new RTS.Actions.Implementation.RebuildWreckageAction
				{
					Name = "RebuildWreckage",
					SlotIndex = -1,
					Layer = ActionLayer.Movement | ActionLayer.Weapon,
					BlockingLayers = ActionLayer.Movement | ActionLayer.Weapon
				});
			}

			// 动作树重建后立即刷新缓存（主线程），避免模拟线程在缓存缺失时丢一次指令
			brain.RefreshActionCache();
		}

		private static void ClearActionNodes(UnitActionController brain)
		{
			foreach (Node child in new List<Node>(brain.GetChildren()))
			{
				if (child is UnitAction)
				{
					brain.RemoveChild(child);
					child.QueueFree();
				}
			}
		}

		// 技能按钮体制化：Kind 0=无 1=英雄能量技能 2=电浆炮 3=自动/手动切换 4=兴奋剂 5=机动模式
		private static void AddSkillAction(UnitActionController brain, int kind, string displayName, int skillIndex, int slot)
		{
			if (kind <= 0)
				return;

			UnitAction action = kind switch
			{
				1 => new HeroSkillAction
				{
					Name = "HeroSkill" + skillIndex,
					SkillIndex = skillIndex,
					DisplayNameText = displayName,
					SlotIndex = slot,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				},
				2 => new PlasmaStrikeAction
				{
					Name = "PlasmaStrike",
					DisplayNameText = displayName,
					SlotIndex = slot,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				},
				3 => new PlasmaModeAction
				{
					Name = "PlasmaMode",
					DisplayNameText = displayName,
					SlotIndex = slot,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				},
				4 => new StimAction
				{
					Name = "Stim",
					DisplayNameText = displayName,
					SlotIndex = slot,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				},
				5 => new CruiserMobilityAction
				{
					Name = "CruiserMobility",
					DisplayNameText = displayName,
					SlotIndex = slot,
					// 机动模式持续 5 秒：占据 Body 层，阻止自动重启 Idle（否则会被自动索敌/清菌毯顶掉）
					Layer = ActionLayer.Body,
					BlockingLayers = ActionLayer.Ability | ActionLayer.Weapon
				},
				6 => new RTS.Actions.Implementation.BurrowAction
				{
					Name = "Burrow",
					DisplayNameText = displayName,
					SlotIndex = slot,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				},
				7 => new RTS.Actions.Implementation.DevourAction
				{
					Name = "Devour",
					DisplayNameText = displayName,
					SlotIndex = slot,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				},
				_ => null
			};

			if (action != null)
				AddAction(brain, action);
		}

		private static void SetupUnitCommon(Unit unit, FPVector2 pos, float maxHp, bool harvest)
		{
			var visuals = new Node3D { Name = "Visuals" };
			var mount = new Node3D { Name = "WeaponMount" };
			visuals.AddChild(mount);
			unit.AddChild(visuals);

			var modules = new Node3D { Name = "Modules" };
			unit.AddChild(modules);

			modules.AddChild(new UnitVisuals { Name = "UnitVisuals" });
			modules.AddChild(new UnitLife { Name = "UnitLife", MaxHp = maxHp });
			modules.AddChild(new UnitCombat { Name = "UnitCombat", WeaponMountPoint = mount });

			if (harvest)
				modules.AddChild(new UnitHarvest { Name = "UnitHarvest" });

			modules.AddChild(new UnitActionController { Name = "UnitActionController" });
		}

		private static void AddCombatActions(UnitActionController brain, bool withHarvest, bool withAttack = true)
		{
			if (withAttack)
				AddAction(brain, new AttackAction { Name = "Attack" });

			if (withHarvest)
				AddAction(brain, new HarvestAction { Name = "Harvest" });

			AddAction(brain, new StopAction
			{
				Name = "Stop",
				SlotIndex = 1,
				Layer = (ActionLayer)23,
				BlockingLayers = (ActionLayer)23
			});
		}

		private static void AddMoveActions(UnitActionController brain)
		{
			AddAction(brain, new MoveAction
			{
				Name = "Move",
				ActionName = "Move",
				SlotIndex = 2,
				Layer = ActionLayer.Movement,
				BlockingLayers = ActionLayer.Movement
			});
			AddAction(brain, new AttackMoveAction
			{
				Name = "AttackMove",
				ActionName = "AttackMove",
				SlotIndex = 3,
				SearchRadius = 400f,
				Layer = ActionLayer.Movement | ActionLayer.Weapon,
				BlockingLayers = ActionLayer.Movement | ActionLayer.Weapon
			});
			AddAction(brain, new IdleAction
			{
				Name = "Idle",
				SlotIndex = 4,
				Layer = (ActionLayer)23,
				BlockingLayers = (ActionLayer)23
			});
		}

		private static void AddAction(UnitActionController brain, UnitAction action)
		{
			brain.AddChild(action);
		}

		private static void AttachRifle(Node3D mount, string weaponId, Vector3? localPos = null, Vector3? muzzleLocalPos = null)
		{
			var rifle = (Rifle)CreateWeaponNode(weaponId, true);
			rifle.Position = localPos ?? Vector3.Zero;
			var muzzle = rifle.GetNodeOrNull<Marker3D>("Muzzle");
			if (muzzle == null)
			{
				muzzle = new Marker3D { Name = "Muzzle" };
				rifle.AddChild(muzzle);
			}
			muzzle.Position = muzzleLocalPos ?? Vector3.Zero;
			mount.AddChild(rifle);
		}

		private static void AttachMagicShooter(Node3D mount, string weaponId)
		{
			mount.AddChild(CreateWeaponNode(weaponId));
		}

		// 预制体里手工摆的武器/炮塔/枪口全部保留（数值仍由 ApplyConfig 覆盖），
		// 只补配置表里有但预制体缺失的武器（Unit / Structure 的 ApplyConfigFromTableIfNeeded 共用）
		public static void EnsureWeaponsOnMount(Node3D mount, System.Collections.Generic.IEnumerable<string> weaponIds)
		{
			if (mount == null || weaponIds == null)
				return;

			foreach (string weaponId in weaponIds)
			{
				bool exists = false;
				foreach (Node child in mount.GetChildren())
				{
					if (child is Weapon w && (w.WeaponName == weaponId || w.Name == weaponId))
					{
						exists = true;
						break;
					}
				}
				if (!exists)
					mount.AddChild(CreateWeaponNode(weaponId));
			}
		}

		// 按武器配置创建武器节点（科技追加武器也走这里）
		public static Weapon CreateWeaponNode(string weaponId, bool forceRifle = false)
		{
			var cfg = GetWeaponCfg(weaponId);
			bool projectile = !forceRifle && cfg != null && cfg.IsProjectile;

			var firePoint = new Marker3D { Name = "FirePoint" };
			float damage = cfg?.Damage ?? (projectile ? 4f : 5f);
			float cooldown = (cfg?.CooldownTicks ?? (projectile ? 60 : 5)) / 20f;
			float range = cfg?.AttackRange ?? (projectile ? 800f : 400f);
			DamageType dmgType = cfg != null
				? (DamageType)cfg.DamageType
				: (projectile ? DamageType.Thermal : DamageType.Kinetic);

			if (projectile)
			{
				var weapon = new ProjectileWeapon
				{
					Name = weaponId,
					WeaponName = weaponId,
					Damage = damage,
					BonusDamage = cfg?.BonusDamage ?? 0f,
					BonusDamageVsArmorType = cfg?.BonusDamageVsArmorType ?? ArmorType.Light,
					Cooldown = cooldown,
					AttackRange = range,
					DmgType = dmgType,
					CanTargetGround = cfg?.CanTargetGround ?? true,
					CanTargetAir = cfg?.CanTargetAir ?? false,
					CanTargetStructure = cfg?.CanTargetStructure ?? true,
					CanTargetNeutral = cfg?.CanTargetNeutral ?? true,
					HasAreaDamage = cfg?.HasAreaDamage ?? false,
					AreaRadius = cfg?.AreaRadius ?? 0f,
					AreaEdgeDamagePercent = cfg?.AreaEdgeDamagePercent ?? 100,
					FirePoint = firePoint,
					HitFxRadius = cfg?.HitFxRadius ?? 36f,
					HitFxDuration = cfg?.HitFxDuration ?? 0.3f,
					HitFxColor = cfg?.HitFxColor ?? new Color(1f, 0.75f, 0.2f, 1f)
				};
				weapon.AddChild(firePoint);
				return weapon;
			}

			var rifle = new Rifle
			{
				Name = weaponId,
				WeaponName = weaponId,
				Damage = damage,
				BonusDamage = cfg?.BonusDamage ?? 0f,
				BonusDamageVsArmorType = cfg?.BonusDamageVsArmorType ?? ArmorType.Light,
				Cooldown = cooldown,
				AttackRange = range,
				DmgType = dmgType,
				CanTargetGround = cfg?.CanTargetGround ?? true,
				CanTargetAir = cfg?.CanTargetAir ?? false,
				CanTargetStructure = cfg?.CanTargetStructure ?? true,
				CanTargetNeutral = cfg?.CanTargetNeutral ?? true,
				HasAreaDamage = cfg?.HasAreaDamage ?? false,
				AreaRadius = cfg?.AreaRadius ?? 0f,
				AreaEdgeDamagePercent = cfg?.AreaEdgeDamagePercent ?? 100,
				FirePoint = firePoint,
				HitFxRadius = cfg?.HitFxRadius ?? 36f,
				HitFxDuration = cfg?.HitFxDuration ?? 0.3f,
				HitFxColor = cfg?.HitFxColor ?? new Color(1f, 0.75f, 0.2f, 1f),
				TracerThickness = cfg?.TracerThickness ?? 3.5f,
				TracerDuration = cfg?.TracerDuration ?? 0.18f,
				MuzzleFlashRadius = cfg?.MuzzleFlashRadius ?? 18f,
				MuzzleFlashDuration = cfg?.MuzzleFlashDuration ?? 0.1f
			};
			rifle.AddChild(firePoint);
			return rifle;
		}

		// =========================================================
		// 建筑（骨架/数值/武器/范围显示走配置表，动作由 RebuildStructureActions 统一生成）
		// =========================================================

		private static Structure CreateStructureBase(string name, int teamId, FPVector2 pos, int gridSize, float maxHp, bool dropOff, bool spreadsCreep)
		{
			var structure = new Structure
			{
				Name = name,
				TeamID = teamId,
				StructureName = name,
				GridSize = gridSize
			};

			if (dropOff)
			{
				structure.IsResourceDropOff = true;
				structure.AcceptableResources = new Godot.Collections.Array<ResourceType>
				{
					ResourceType.Metal,
					ResourceType.Gas
				};
			}

			var visuals = new Node3D { Name = "Visuals" };
			var mount = new Node3D { Name = "WeaponMount" };
			visuals.AddChild(mount);
			structure.AddChild(visuals);

			var modules = new Node3D { Name = "Modules" };
			structure.AddChild(modules);

			modules.AddChild(new UnitVisuals { Name = "UnitVisuals" });
			modules.AddChild(new UnitLife { Name = "UnitLife", MaxHp = maxHp });
			modules.AddChild(new UnitCombat { Name = "UnitCombat", WeaponMountPoint = mount });
			modules.AddChild(new UnitActionController { Name = "UnitActionController" });

			if (spreadsCreep)
			{
				var creepCfg = GetStructCfg(name);
				structure.RequiredCreep = creepCfg?.RequiredCreepType ?? CreepType.NanoCreep;

				// 只有配置了铺毯半径的建筑才挂 CreepSource（纳米主基地），
				// 其余“需要菌毯”的建筑只参与放置判定，不自己铺毯
				if (creepCfg?.CreepSpreadRadius > 0f)
				{
					modules.AddChild(new CreepSource
					{
						Name = "CreepSource",
						MaxRadius = creepCfg.CreepSpreadRadius,
						SpreadTime = creepCfg.CreepSpreadTime,
						GeneratedCreep = creepCfg.GeneratedCreepType
					});
				}
			}

			structure.Position = new Vector3((float)pos.X, 0f, (float)pos.Y);
			ApplyStructureStats(structure, name);
			return structure;
		}

		private static Node3D CreateConfiguredBuilding(string name, int teamId, FPVector2 pos)
		{
			var cfg = GetStructCfg(name);

			var structure = CreateStructureBase(
				name,
				teamId,
				pos,
				cfg?.GridWidth ?? 2,
				cfg?.MaxHp ?? 500f,
				cfg?.IsDropOffPoint ?? false,
				// 只要显式配置了铺毯半径就挂 CreepSource（生命树没有 RequiresCreep 也要能铺/清毯）
				cfg?.CreepSpreadRadius > 0f);

			if (cfg != null)
			{
				// 自动施工（纳米建筑 / 恶魔献祭建筑）：挂上施工推进模块
				if (cfg.AutoBuild || cfg.RequiresSacrifice)
				{
					var autoBuild = new AutoBuildBehavior
					{
						Name = "AutoBuild",
						BuildTime = cfg.BuildTime
					};

					// 类名与文件名一致后脚本资源自带 resource_path；保留防御性补路径，避免打包成空 CSharpScript。
					var autoScript = autoBuild.GetScript().As<CSharpScript>();
					if (autoScript != null && string.IsNullOrEmpty(autoScript.ResourcePath))
						autoScript.ResourcePath = "res://Scripts/Units/Modules/Behaviors/AutoBuildBehavior.cs";

					structure.AddChild(autoBuild);
				}

				if (cfg.FieldRadius > 0)
				{
					structure.AddChild(new AuraRangeVisual
					{
						Name = "FieldRangeVisual",
						RingColor = new Color(0.95f, 0.25f, 0.2f, 0.22f),
						ClipToCircle = true
					});
				}
				else if (cfg.HasAura)
				{
					structure.AddChild(new AuraRangeVisual { Name = "AuraRangeVisual" });
				}

				if (cfg.AutoHarvestRadiusTiles > 0 && cfg.AutoHarvestPerSecondPerNode > 0f)
				{
					structure.AddChild(new HarvesterMiningFX { Name = "HarvesterMiningFX" });
					structure.AddChild(new AutoHarvestBehavior
					{
						Name = "AutoHarvest",
						RadiusTiles = cfg.AutoHarvestRadiusTiles,
						RatePerSecondPerNode = cfg.AutoHarvestPerSecondPerNode
					});
				}

				var mount = structure.GetNode<Node3D>("Visuals/WeaponMount");

				foreach (string weaponId in cfg.WeaponIds)
					AttachMagicShooter(mount, weaponId);
			}

			return structure;
		}

		// 建筑动作面板统一由配置表驱动（场景与代码工厂共用同一入口）
		public static void RebuildStructureActions(Structure structure)
		{
			var brain = structure.Brain ?? structure.GetNodeOrNull<UnitActionController>("Modules/UnitActionController");
			if (brain == null)
				return;

			ClearActionNodes(brain);
			AddStructureActions(brain);

			var cfg = GetStructCfg(structure.StructureName);
			if (cfg == null)
				return;

			int slot = 5;

			foreach (string unitId in cfg.TrainableUnitIds)
				AddAction(brain, CreateTrainAction(unitId, slot++));

			// 泰伦藻类工厂：放出驻扎的工人
			if (cfg.GarrisonCapacity > 0)
			{
				AddAction(brain, new RTS.Actions.Implementation.ReleaseWorkersAction
				{
					Name = "ReleaseWorkers",
					DisplayNameText = "放出工人",
					SlotIndex = 1,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			if (cfg.AutoProduceModeUnitIds.Count > 0)
			{
				AddAction(brain, new AutoProduceModeAction
				{
					Name = "AutoProduceMode",
					DisplayNameText = string.IsNullOrEmpty(cfg.AutoProduceModeName) ? "切换模式" : cfg.AutoProduceModeName,
					SlotIndex = 1,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			AddResearchActionsFromConfig(brain, structure.StructureName);
			AddPanelSkills(brain, cfg);
		}

		// 面板技能（轨道控制中心等交互建筑）：按配置位标记生成，1=雷达 2=轨道炮 4=资源交换
		private static void AddPanelSkills(UnitActionController brain, StructureConfig cfg)
		{
			if ((cfg.PanelSkillMode & 1) != 0)
			{
				AddAction(brain, new RadarAction
				{
					Name = "Radar",
					SlotIndex = -1,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			if ((cfg.PanelSkillMode & 2) != 0)
			{
				AddAction(brain, new OrbitalStrikeAction
				{
					Name = "OrbitalStrike",
					SlotIndex = -1,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			if ((cfg.PanelSkillMode & 4) != 0)
			{
				AddAction(brain, new ResourceExchangeAction
				{
					Name = "ExchangeMetalToGas",
					DisplayNameText = "金属→瓦斯",
					Costs = new Godot.Collections.Dictionary<ResourceType, float>
					{
						{ ResourceType.Energy, cfg.ExchangeEnergyCost },
						{ ResourceType.Metal, cfg.ExchangeMetalAmount }
					},
					Reward = new Godot.Collections.Dictionary<ResourceType, float>
					{
						{ ResourceType.Gas, cfg.ExchangeGasAmount }
					},
					SlotIndex = -1,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
				AddAction(brain, new ResourceExchangeAction
				{
					Name = "ExchangeGasToMetal",
					DisplayNameText = "瓦斯→金属",
					Costs = new Godot.Collections.Dictionary<ResourceType, float>
					{
						{ ResourceType.Energy, cfg.ExchangeEnergyCost },
						{ ResourceType.Gas, cfg.ExchangeGasAmount }
					},
					Reward = new Godot.Collections.Dictionary<ResourceType, float>
					{
						{ ResourceType.Metal, cfg.ExchangeMetalAmount }
					},
					SlotIndex = -1,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			if ((cfg.PanelSkillMode & 8) != 0)
			{
				AddAction(brain, new ResonanceWaveAction
				{
					Name = "ResonanceWave",
					SlotIndex = 10,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			if ((cfg.PanelSkillMode & 16) != 0)
			{
				AddAction(brain, new SeismicWaveAction
				{
					Name = "SeismicWave",
					SlotIndex = 11,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			if ((cfg.PanelSkillMode & 32) != 0)
			{
				AddAction(brain, new WormholeCreateAction
				{
					Name = "CaveWormhole",
					SlotIndex = 12,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			if ((cfg.PanelSkillMode & 64) != 0)
			{
				AddAction(brain, new OmniLeafDogAction
				{
					Name = "PlantOmni",
					SlotIndex = 10,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}

			// 植物：森林蔓延（建筑技能，以建筑为圆心定范围）
			if ((cfg.PanelSkillMode & 128) != 0)
			{
				AddAction(brain, new PlantForestSpreadAction
				{
					Name = "PlantForestSpread",
					SlotIndex = 11,
					Layer = ActionLayer.Ability,
					BlockingLayers = ActionLayer.Ability
				});
			}
		}

		private static void AddStructureActions(UnitActionController brain)
		{
			AddAction(brain, new AttackAction { Name = "Attack" });
			AddAction(brain, new IdleAction { Name = "Idle" });
			AddAction(brain, new GeneralCancelAction { Name = "Cancel", SlotIndex = 14 });
		}

		// 研究院/牧羊人：按配置表 ResearchableTechIds 生成研究按钮
		private static void AddResearchActionsFromConfig(UnitActionController brain, string structureId, int startSlot = 1)
		{
			var cfg = GetStructCfg(structureId);
			var unitCfg = cfg == null ? GetUnitCfg(structureId) : null;

			var techIds = cfg != null
				? cfg.ResearchableTechIds
				: (unitCfg != null ? unitCfg.ResearchableTechIds : null);

			if (techIds == null)
				return;

			int slot = startSlot;

			foreach (string techId in techIds)
			{
				var techCfg = ConfigDatabase.GetTech(techId);
				if (techCfg == null)
					continue;

				AddAction(brain, new ResearchAction
				{
					Name = "Research_" + techId,
					TechId = techId,
					DisplayNameText = techCfg.DisplayName,
					SlotIndex = slot++,
					Layer = ActionLayer.None,
					BlockingLayers = ActionLayer.None
				});
			}
		}

		private static Node3D CreateShrine(string name, int teamId, FPVector2 pos)
		{
			var shrine = new ShrineStructure
			{
				Name = name,
				TeamID = -1,
				StructureName = name,
				GridSize = 2,
				VisionRange = 250f,
				CaptureRadius = 250f,
				CaptureTime = 5f,
				RewardType = ResourceType.Money,
				RewardAmountPerSecond = 5f
			};

			var visuals = new Node3D { Name = "Visuals" };
			var mount = new Node3D { Name = "WeaponMount" };
			visuals.AddChild(mount);
			shrine.AddChild(visuals);

			var modules = new Node3D { Name = "Modules" };
			shrine.AddChild(modules);

			modules.AddChild(new UnitVisuals { Name = "UnitVisuals" });
			modules.AddChild(new UnitLife { Name = "UnitLife", MaxHp = 1000f });
			modules.AddChild(new UnitCombat { Name = "UnitCombat", WeaponMountPoint = mount });
			modules.AddChild(new UnitActionController { Name = "UnitActionController" });

			shrine.Position = new Vector3((float)pos.X, 0f, (float)pos.Y);
			ApplyStructureStats(shrine, name);
			return shrine;
		}

		// =========================================================
		// 纳米虫种族
		// =========================================================

		private static Node3D CreateNanoBehemoth(string name, int teamId, FPVector2 pos)
		{
			var unit = new Unit
			{
				Name = name,
				TeamID = teamId,
				UnitName = name,
				RequiresCarpet = true,
				VisionRange = 350f
			};
			SetupUnitCommon(unit, pos, 2000f, false);
			ApplyUnitStats(unit, name);

			unit.AddChild(new AuraRangeVisual
			{
				Name = "AuraRangeActive",
				TechId = "NanoTech_GeneralA",
				RingColor = new Color(0.3f, 1f, 0.4f, 0.28f)
			});
			unit.AddChild(new AuraRangeVisual
			{
				Name = "AuraRangeSmoke",
				TechId = "NanoTech_GeneralB",
				RingColor = new Color(0.45f, 0.7f, 1f, 0.28f)
			});
			unit.AddChild(new HarvesterMiningFX { Name = "HarvesterMiningFX" });

			// 先进采集器科技：巨兽可自动采集（模块常挂，科技未授予时 Tick 内直接跳过）
			var harvesterCfg = GetStructCfg("NanoHarvester");
			unit.AddChild(new AutoHarvestBehavior
			{
				Name = "AutoHarvest",
				RadiusTiles = harvesterCfg?.AutoHarvestRadiusTiles ?? 8,
				RatePerSecondPerNode = harvesterCfg?.AutoHarvestPerSecondPerNode ?? 5f
			});

			var mount = unit.GetNode<Node3D>("Visuals/WeaponMount");

			// 四台随身机炮：分布在 200³ 立方体的四个角，枪口朝外且互不相同
			float halfX = 110f;
			float halfZ = 150f;
			float gunY = 95f;
			Vector3[] corners =
			{
				new Vector3(-halfX, gunY, -halfZ),
				new Vector3(halfX, gunY, -halfZ),
				new Vector3(-halfX, gunY, halfZ),
				new Vector3(halfX, gunY, halfZ)
			};

			foreach (var corner in corners)
			{
				Vector3 dir = new Vector3(Mathf.Sign(corner.X), 0f, Mathf.Sign(corner.Z));
				AttachRifle(mount, "NanoBehemothGun", corner, dir * 26f + new Vector3(0f, 14f, 0f));
			}

			unit.InjectLogicPosition(pos);
			return unit;
		}

		private static Node3D CreateResourceNode(string name, int teamId, FPVector2 pos, ResourceType resType, float amount)
		{
			var cfg = GetStructCfg(name);
			resType = cfg != null ? cfg.ResourceType : resType;
			amount = cfg != null ? cfg.ResourceAmount : amount;

			var resource = new ResourceStructure
			{
				Name = name,
				TeamID = -1,
				StructureName = name,
				GridSize = 1
			};

			var visuals = new Node3D { Name = "Visuals" };
			resource.AddChild(visuals);

			var modules = new Node3D { Name = "Modules" };
			resource.AddChild(modules);

			modules.AddChild(new UnitVisuals { Name = "UnitVisuals" });
			modules.AddChild(new ResourceModule
			{
				Name = "ResourceModule",
				ResType = resType,
				MaxAmount = amount
			});

			resource.ResModule = resource.GetNode<ResourceModule>("Modules/ResourceModule");
			resource.Position = new Vector3((float)pos.X, 0f, (float)pos.Y);
			return resource;
		}
	}
}
