using Godot;
using System.Collections.Generic;
using System.Text;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;
using RTS.World;
using RTS.Core.Events;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	public partial class SelectionInfoPanel : Control
	{
		public static SelectionInfoPanel Instance { get; private set; }

		[ExportGroup("Single View")]
		[Export] public Control SingleContainer;
		[Export] public Label NameLabel;
		[Export] public TextureRect PortraitIcon;
		[Export] public ProgressBar HpBar;
		[Export] public Label HpText;
		[Export] public ProgressBar ShieldBar;
		[Export] public ProgressBar SecondaryBar;
		[Export] public Label SecondaryLabel;
		[Export] public HBoxContainer WeaponRow;
		[Export] public VBoxContainer BuffBox;
		[Export] public RichTextLabel StatsLabel;

		// 单选头像的大小限制（信息栏紧凑布局）
		[Export] public Vector2 PortraitSize = new Vector2(44, 44);

		[ExportGroup("Multi View")]
		[Export] public Control MultiContainer;
		[Export] public Container GridContainer;
		[Export] public PackedScene IconPrefab;

		// 多选图标的大小限制 (例如 40x40)
		[Export] public Vector2 IconSize = new Vector2(40, 40);

		private IEntity _currentSingleTarget;
		private string _buffFingerprint = "";
		private readonly List<(Label Name, ProgressBar Bar, BuffInstance Buff)> _buffRows = new();

		public override void _Ready()
		{
			Instance = this;
			GameEventBus.SelectionChanged += OnBusSelectionChanged;
			if (SingleContainer != null) SingleContainer.Visible = false;
			if (MultiContainer != null) MultiContainer.Visible = false;

			PaintBarsGreen(HpBar);
			PaintBarsGreen(ShieldBar);
			PaintBarsGreen(SecondaryBar);
		}

		private static void PaintBarsGreen(ProgressBar bar)
		{
			if (bar == null)
				return;
			bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
			{
				BgColor = new Color(0.25f, 0.9f, 0.3f),
				CornerRadiusTopLeft = 2,
				CornerRadiusTopRight = 2,
				CornerRadiusBottomLeft = 2,
				CornerRadiusBottomRight = 2
			});
		}

		public override void _Process(double delta)
		{
			if (_currentSingleTarget != null && SingleContainer.Visible)
			{
				// 选中单位死亡/释放后必须立刻停更：否则每帧读它的
				// LifeModule 会踩已释放节点 → 原生 AccessViolation 闪退
				// （8 倍速下选中单位死得快，必现）
				if (!GodotObject.IsInstanceValid(_currentSingleTarget as GodotObject) ||
					_currentSingleTarget.IsDeadOrNull())
				{
					SingleContainer.Visible = false;
					MultiContainer.Visible = false;
					_currentSingleTarget = null;
					return;
				}
				UpdateVitalStats(_currentSingleTarget);
				UpdateBuffBarValues();
			}
		}

		public override void _ExitTree()
		{
			GameEventBus.SelectionChanged -= OnBusSelectionChanged;
			base._ExitTree();
		}

		private void OnBusSelectionChanged(System.Collections.Generic.List<IEntity> selection, int team)
		{
			UpdateInfo(selection);
		}

		public void UpdateInfo(List<IEntity> selected)
		{
			// 过滤已死亡/已释放实体：选中单位死后节点释放，再读它的
			// LifeModule/CombatModule 会原生崩溃（8 倍速下高发）
			var valid = new List<IEntity>();
			if (selected != null)
			{
				foreach (var e in selected)
				{
					if (e != null &&
						GodotObject.IsInstanceValid(e as GodotObject) &&
						!e.IsDeadOrNull())
						valid.Add(e);
				}
			}

			if (valid.Count == 0)
			{
				SingleContainer.Visible = false;
				MultiContainer.Visible = false;
				_currentSingleTarget = null;
				return;
			}

			if (valid.Count == 1)
			{
				ShowSingleView(valid[0]);
			}
			else
			{
				ShowMultiView(valid);
			}
		}

		// =========================================================
		// 单体显示
		// =========================================================
		private void ShowSingleView(IEntity entity)
		{
			MultiContainer.Visible = false;
			SingleContainer.Visible = true;
			_currentSingleTarget = entity;

			if (NameLabel != null)
			{
				NameLabel.Text = RTS.Settings.Localization.TrName(entity.DisplayName);
				NameLabel.ClipText = true;
				NameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
				NameLabel.TooltipText = RTS.Settings.Localization.TrName(entity.DisplayName);
			}

			if (PortraitIcon != null)
			{
				PortraitIcon.Texture = entity.Icon;
				PortraitIcon.CustomMinimumSize = PortraitSize;
				PortraitIcon.Size = PortraitSize;
				PortraitIcon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
				PortraitIcon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
				PortraitIcon.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
				PortraitIcon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			}

			RebuildBuffBars(entity);
			RebuildWeaponIcons(entity);
			RefreshDetails(entity);
			UpdateVitalStats(entity);
		}

		private void RefreshDetails(IEntity entity)
		{
			if (StatsLabel != null)
				StatsLabel.Text = BuildDetailText(entity);
		}

		// =========================================================
		// 详细文本：属性 / 武器 / 特殊
		// =========================================================
		private string BuildDetailText(IEntity entity)
		{
			var sb = new StringBuilder();
			var logic = entity?.LogicEntity;
			if (logic == null)
				return "";

			// 矿点：只显示剩余储量
			if (entity is ResourceStructure res)
			{
				float max = res.ResModule != null ? res.ResModule.MaxAmount : (float)logic.ResourceAmount;
				float cur = (float)logic.ResourceAmount;
				sb.AppendLine(RTS.Settings.Localization.Tr("info.resource_remaining", Mathf.Ceil(cur), max));
				return sb.ToString();
			}

			var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(entity.DisplayName);
			var structCfg = unitCfg == null ? RTS.Data.Configs.ConfigDatabase.GetStructure(entity.DisplayName) : null;

			sb.Append(RTS.Settings.Localization.Tr("info.attrs", ArmorName(logic.ArmorType)));

			if (unitCfg != null && unitCfg.Tags.Count > 0)
				sb.Append(RTS.Settings.Localization.Tr("info.tags", string.Join("/", unitCfg.Tags)));

			float move = unitCfg?.MoveSpeed ?? 0f;
			if (move > 0f)
				sb.Append(RTS.Settings.Localization.Tr("info.move_speed", $"{move:F1}"));

			float vision = (unitCfg?.VisionRange ?? structCfg?.VisionRange ?? 0f) / 64f;
			if (vision > 0f)
				sb.Append(RTS.Settings.Localization.Tr("info.vision", $"{vision:F1}"));

			if (logic is SimUnit heroUnit && heroUnit.HeroMaxEnergy > FP.Zero)
				sb.Append(RTS.Settings.Localization.Tr("info.energy", (float)heroUnit.HeroEnergy, (float)heroUnit.HeroMaxEnergy));

			// 战斗力评分（综合 DPS/生命/射程/移速/技能，见 CombatRating）
			var ratingCfg = unitCfg ?? RTS.Data.Configs.ConfigDatabase.GetUnit(entity.DisplayName);
			if (ratingCfg != null)
			{
				float rating = RTS.Data.CombatRating.ComputeUnitRating(ratingCfg);
				if (rating > 0f)
					sb.Append(RTS.Settings.Localization.Tr("info.combat_rating", rating));
			}

			sb.AppendLine();

			return sb.ToString();
		}

		private static string BuildWeaponTooltip(Weapon w)
		{
			string id = !string.IsNullOrEmpty(w.WeaponName) && w.WeaponName != "Standard Weapon"
				? w.WeaponName
				: w.Name;
			var cfg = RTS.Data.Configs.ConfigDatabase.GetWeapon(id);

			string displayName = RTS.Settings.Localization.TrName(id);
			string dmg = $"{w.Damage:F0}";
			if (w.BonusDamage > 0f)
				dmg += $"+{w.BonusDamage:F0}({ArmorName((int)w.BonusDamageVsArmorType)})";

			string targets = "";
			if (w.CanTargetGround)
				targets += RTS.Settings.Localization.Tr("target.ground");
			if (w.CanTargetAir)
				targets += (targets.Length > 0 ? "/" : "") + RTS.Settings.Localization.Tr("target.air");
			if (w.CanTargetStructure)
				targets += (targets.Length > 0 ? "/" : "") + RTS.Settings.Localization.Tr("target.structure");

			string proj = cfg?.ProjectileBehavior switch
			{
				ProjectileBehavior.Instant => RTS.Settings.Localization.Tr("proj.instant"),
				ProjectileBehavior.Homing => RTS.Settings.Localization.Tr("proj.homing"),
				ProjectileBehavior.Fixed => RTS.Settings.Localization.Tr("proj.fixed"),
				ProjectileBehavior.Lob => RTS.Settings.Localization.Tr("proj.lob"),
				_ => ""
			};

			// 近战/远程标签（设计表口径）：紧跟伤害类型，如“近战物理40”
			string rangeLabel = w.RangeType == WeaponRangeType.Melee
				? RTS.Settings.Localization.Tr("weapon.melee")
				: RTS.Settings.Localization.Tr("weapon.ranged");
			string dmgTypeName = DamageTypeName(w.DmgType);
			string dmgTypeWithRange = rangeLabel.Length > 0
				? rangeLabel + dmgTypeName
				: dmgTypeName;

			string extra = "";
			if (w.HasAreaDamage && w.AreaRadius > 0f)
				extra += RTS.Settings.Localization.Tr("weapon.splash", $"{w.AreaRadius / 64f:F1}");
			if (w.ConeAngleDegrees > 0f)
				extra += RTS.Settings.Localization.Tr("weapon.cone", $"{w.ConeAngleDegrees:F0}");
			if (cfg != null && cfg.FootprintScaledDamage)
				extra += RTS.Settings.Localization.Tr("weapon.footprint");

			string cd = w.CanFire() ? "" : RTS.Settings.Localization.Tr("weapon.cooldown", $"{w.CooldownRemaining:F1}");

			string stats = RTS.Settings.Localization.Tr("weapon.stats",
				dmgTypeWithRange, dmg, targets,
				$"{w.AttackRange / 64f:F1}", $"{w.Cooldown:F1}",
				proj, extra, cd);
			return $"{displayName}\n{stats}";
		}

		// =========================================================
		// 武器图标行：小图标 + 悬浮详情
		// =========================================================
		private void RebuildWeaponIcons(IEntity entity)
		{
			if (WeaponRow == null)
				return;

			foreach (Node child in WeaponRow.GetChildren())
				child.QueueFree();

			if (entity?.CombatModule == null || entity.CombatModule.Weapons.Count == 0)
			{
				if (!entity.IsStructure)
				{
					var none = new Label
					{
						Text = RTS.Settings.Localization.Tr("info.no_weapon"),
						CustomMinimumSize = new Vector2(40, 24),
						VerticalAlignment = VerticalAlignment.Center
					};
					none.AddThemeFontSizeOverride("font_size", 10);
					WeaponRow.AddChild(none);
				}
				return;
			}

			foreach (var w in entity.CombatModule.Weapons)
			{
				var btn = new Button
				{
					CustomMinimumSize = new Vector2(28, 28),
					SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
					Text = DamageTypeShort(w.DmgType),
					TooltipText = BuildWeaponTooltip(w),
					FocusMode = FocusModeEnum.None
				};
				btn.AddThemeFontSizeOverride("font_size", 11);
				btn.AddThemeColorOverride("font_color", Colors.White);
				btn.AddThemeColorOverride("font_hover_color", Colors.White);
				btn.AddThemeColorOverride("font_pressed_color", Colors.White);

				Color c = DamageTypeColor(w.DmgType);
				btn.AddThemeStyleboxOverride("normal", MakeWeaponStyle(c.Darkened(0.5f)));
				btn.AddThemeStyleboxOverride("hover", MakeWeaponStyle(c.Darkened(0.3f)));
				btn.AddThemeStyleboxOverride("pressed", MakeWeaponStyle(c));

				WeaponRow.AddChild(btn);
			}
		}

		private static StyleBoxFlat MakeWeaponStyle(Color bg)
		{
			return new StyleBoxFlat
			{
				BgColor = bg,
				CornerRadiusTopLeft = 4,
				CornerRadiusTopRight = 4,
				CornerRadiusBottomLeft = 4,
				CornerRadiusBottomRight = 4,
				ContentMarginLeft = 2f,
				ContentMarginRight = 2f,
				ContentMarginTop = 2f,
				ContentMarginBottom = 2f
			};
		}

		private static string DamageTypeShort(DamageType t)
		{
			return t switch
			{
				DamageType.Kinetic => RTS.Settings.Localization.Tr("dmg_short.kinetic"),
				DamageType.Thermal => RTS.Settings.Localization.Tr("dmg_short.thermal"),
				DamageType.Explosive => RTS.Settings.Localization.Tr("dmg_short.explosive"),
				DamageType.EM => RTS.Settings.Localization.Tr("dmg_short.em"),
				DamageType.Beam => RTS.Settings.Localization.Tr("dmg_short.beam"),
				_ => "?"
			};
		}

		private static Color DamageTypeColor(DamageType t)
		{
			return t switch
			{
				DamageType.Kinetic => new Color(0.55f, 0.55f, 0.6f),
				DamageType.Thermal => new Color(0.9f, 0.45f, 0.15f),
				DamageType.Explosive => new Color(0.85f, 0.2f, 0.2f),
				DamageType.EM => new Color(0.3f, 0.55f, 1f),
				DamageType.Beam => new Color(0.2f, 0.9f, 0.9f),
				_ => new Color(0.5f, 0.5f, 0.5f)
			};
		}

		private static string DamageTypeName(DamageType t)
		{
			return t switch
			{
				DamageType.Kinetic => RTS.Settings.Localization.Tr("dmg.kinetic"),
				DamageType.Thermal => RTS.Settings.Localization.Tr("dmg.thermal"),
				DamageType.Explosive => RTS.Settings.Localization.Tr("dmg.explosive"),
				DamageType.EM => RTS.Settings.Localization.Tr("dmg.em"),
				DamageType.Beam => RTS.Settings.Localization.Tr("dmg.beam"),
				_ => ""
			};
		}

		private static string ArmorName(int armorType)
		{
			return armorType switch
			{
				(int)ArmorType.Light => RTS.Settings.Localization.Tr("armor.light"),
				(int)ArmorType.Armored => RTS.Settings.Localization.Tr("armor.armored"),
				(int)ArmorType.Biological => RTS.Settings.Localization.Tr("armor.biological"),
				(int)ArmorType.Mechanical => RTS.Settings.Localization.Tr("armor.mechanical"),
				(int)ArmorType.Structure => RTS.Settings.Localization.Tr("armor.structure"),
				_ => RTS.Settings.Localization.Tr("armor.other", armorType)
			};
		}

		// =========================================================
		// 血条 / 护盾 / 能量 / Buff 条
		// =========================================================
		private void UpdateVitalStats(IEntity entity)
		{
			// 矿点没有生命：显示剩余储量
			if (entity is ResourceStructure res && res.LogicEntity != null)
			{
				float max = res.ResModule != null ? res.ResModule.MaxAmount : (float)res.LogicEntity.ResourceAmount;
				float current = (float)res.LogicEntity.ResourceAmount;

				if (HpBar != null)
				{
					HpBar.MaxValue = max;
					HpBar.Value = current;
				}
				if (HpText != null)
					HpText.Text = $"{Mathf.Ceil(current)} / {max}";
				if (ShieldBar != null)
					ShieldBar.Visible = false;
				if (SecondaryBar != null)
					SecondaryBar.Visible = false;
				if (SecondaryLabel != null)
					SecondaryLabel.Visible = false;
				return;
			}

			var logic = entity?.LogicEntity;
			if (entity.LifeModule == null || logic == null) return;

			if (HpBar != null)
			{
				HpBar.MaxValue = entity.LifeModule.MaxHp;
				HpBar.Value = entity.LifeModule.CurrentHp;
			}
			if (HpText != null)
			{
				HpText.Text = $"{Mathf.Ceil(entity.LifeModule.CurrentHp)} / {entity.LifeModule.MaxHp}";
			}

			if (ShieldBar != null)
			{
				ShieldBar.Visible = entity.LifeModule.MaxShield > 0;
				if (ShieldBar.Visible)
				{
					ShieldBar.MaxValue = entity.LifeModule.MaxShield;
					ShieldBar.Value = entity.LifeModule.CurrentShield;
				}
			}

			// 第二资源条：英雄能量 / 泰伦弹药
			bool hasSecondary = logic is SimUnit heroUnit2 && heroUnit2.HeroMaxEnergy > FP.Zero;
			bool hasAmmo = logic is SimUnit ammoUnit && ammoUnit.MaxAmmo > FP.Zero;
			bool hasLifespan = logic is SimUnit lifeUnit && lifeUnit.LifespanTimer > FP.Zero;
			bool showAmmo = hasAmmo && !hasSecondary;
			bool showLifespan = hasLifespan && !hasSecondary && !showAmmo;
			if (SecondaryBar != null)
			{
				SecondaryBar.Visible = hasSecondary || showAmmo || showLifespan;
				if (hasSecondary && logic is SimUnit heroUnit)
				{
					SecondaryBar.MaxValue = (float)heroUnit.HeroMaxEnergy;
					SecondaryBar.Value = (float)heroUnit.HeroEnergy;
				}
				else if (showAmmo && logic is SimUnit ammoUnit2)
				{
					SecondaryBar.MaxValue = (float)ammoUnit2.MaxAmmo;
					SecondaryBar.Value = (float)ammoUnit2.Ammo;
				}
				else if (showLifespan && logic is SimUnit lifeUnit2)
				{
					SecondaryBar.MaxValue = lifeUnit2.LifespanMax > FP.Zero ? (float)lifeUnit2.LifespanMax : 1f;
					SecondaryBar.Value = (float)lifeUnit2.LifespanTimer;
				}
			}
			if (SecondaryLabel != null)
			{
				SecondaryLabel.Visible = hasSecondary || showAmmo || showLifespan;
				if (hasSecondary && logic is SimUnit heroUnit)
					SecondaryLabel.Text = RTS.Settings.Localization.Tr("info.energy_value", (float)heroUnit.HeroEnergy);
				else if (showAmmo && logic is SimUnit ammoUnit3)
					SecondaryLabel.Text = RTS.Settings.Localization.Tr("info.ammo_value", (float)ammoUnit3.Ammo);
				else if (showLifespan && logic is SimUnit lifeUnit3)
					SecondaryLabel.Text = $"寿命 {Mathf.Ceil((float)lifeUnit3.LifespanTimer)}s";
			}
		}

		// Buff 条只在“Buff 集合变化”时重建，剩余时间每帧直接改进度条值
		private void RebuildBuffBars(IEntity entity)
		{
			if (BuffBox == null)
				return;

			var buffs = entity?.LogicEntity?.Buffs;
			string fp = ComputeBuffSetFingerprint(buffs);
			if (fp == _buffFingerprint)
				return;

			_buffFingerprint = fp;

			foreach (Node child in BuffBox.GetChildren())
				child.QueueFree();
			_buffRows.Clear();

			if (buffs == null)
				return;

			foreach (var buff in buffs.Buffs)
			{
				// 右侧窄列：Buff 名在上、细进度条在下
				var row = new VBoxContainer
				{
					CustomMinimumSize = new Vector2(0, 16),
					SizeFlagsHorizontal = SizeFlags.Fill
				};

				var name = new Label
				{
					Text = RTS.Settings.Localization.TrName(buff.BuffId),
					CustomMinimumSize = new Vector2(0, 10),
					ClipText = true,
					TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
					MouseFilter = MouseFilterEnum.Ignore
				};
				name.AddThemeFontSizeOverride("font_size", 9);
				name.TooltipText = BuildBuffTooltip(buff);

				var bar = new ProgressBar
				{
					CustomMinimumSize = new Vector2(0, 6),
					SizeFlagsHorizontal = SizeFlags.ExpandFill,
					ShowPercentage = false,
					MouseFilter = MouseFilterEnum.Ignore
				};
				bar.TooltipText = BuildBuffTooltip(buff);
				bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
				{
					BgColor = new Color(0.25f, 0.9f, 0.3f),
					CornerRadiusTopLeft = 2,
					CornerRadiusTopRight = 2,
					CornerRadiusBottomLeft = 2,
					CornerRadiusBottomRight = 2
				});

				row.AddChild(name);
				row.AddChild(bar);
				BuffBox.AddChild(row);
				_buffRows.Add((name, bar, buff));
			}
		}

		private void UpdateBuffBarValues()
		{
			foreach (var (_, bar, buff) in _buffRows)
			{
				if (!GodotObject.IsInstanceValid(bar))
					continue;

				float ratio = buff.TotalDuration > FP.Zero
					? (float)(buff.RemainingTime / buff.TotalDuration)
					: 1f;
				bar.Value = Mathf.Clamp(ratio * 100f, 0f, 100f);
				bar.TooltipText = BuildBuffTooltip(buff);
			}
		}

		private static string ComputeBuffSetFingerprint(BuffContainer buffs)
		{
			if (buffs == null || buffs.Buffs.Count == 0)
				return "";

			var sb = new StringBuilder();
			foreach (var b in buffs.Buffs)
			{
				sb.Append(b.BuffId).Append('|').Append(b.Stacks)
					.Append('|').Append((long)(b.TotalDuration * (FP)20m)).Append(';');
			}
			return sb.ToString();
		}

		private static string BuildBuffTooltip(BuffInstance b)
		{
			string name = RTS.Settings.Localization.TrName(b.BuffId);
			if (b.TotalDuration > FP.Zero)
				return RTS.Settings.Localization.Tr("buff.tooltip", name, b.Stacks, (float)b.RemainingTime, (float)b.TotalDuration);
			return RTS.Settings.Localization.Tr("buff.permanent", name, b.Stacks);
		}

		// =========================================================
		// 多选
		// =========================================================
		private void ShowMultiView(List<IEntity> entities)
		{
			SingleContainer.Visible = false;
			MultiContainer.Visible = true;
			_currentSingleTarget = null;

			foreach (Node child in GridContainer.GetChildren())
				child.QueueFree();

			if (IconPrefab == null) return;

			foreach (var entity in entities)
			{
				var btn = IconPrefab.Instantiate<Button>();

				btn.Icon = entity.Icon;
				btn.CustomMinimumSize = IconSize;
				btn.Size = IconSize;
				btn.ExpandIcon = true;
				btn.IconAlignment = HorizontalAlignment.Center;
				btn.VerticalIconAlignment = VerticalAlignment.Center;
				btn.TooltipText = RTS.Settings.Localization.TrName(entity.DisplayName);
				btn.FocusMode = FocusModeEnum.None;

				float hpPercent = entity.LifeModule != null ? (entity.LifeModule.CurrentHp / entity.LifeModule.MaxHp) : 1.0f;
				btn.Modulate = hpPercent < 0.3f ? new Color(1, 0.5f, 0.5f) : Colors.White;

				btn.Pressed += () => SelectSingleFromGroup(entity);

				GridContainer.AddChild(btn);
			}
		}

		private void SelectSingleFromGroup(IEntity target)
		{
			var player = Game.GetPlayerByTeam(Main.Instance.LocalPlayerID);
			if (player != null && player.Controller != null)
			{
				var userController = Main.Instance.GetNodeOrNull<UserController>("User/UserController");
				if (userController != null)
				{
					userController.ForceSelect(target);
				}
			}
		}
	}
}
