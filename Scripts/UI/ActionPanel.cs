using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Core.Events;

namespace RTS.Core
{
	public partial class ActionPanel : Control
	{
		public static ActionPanel Instance { get; private set; }

		[Export] public GridContainer Grid;
		[Export] public PackedScene ButtonPrefab;

		private readonly Button[] _slots = new Button[15];
		private ulong _lastAutoRefreshMs;

		/// <summary>
		/// 技能槽快捷键。
		///
		/// **不要在这里再写死一份键位**：从 InputActions 注册表按 `panel_slot_N` 读。
		/// 之前这里硬编码 QWERT/ASDFG/ZXCVB，把 A/S/T 全占了 ——
		/// 而 A=攻击移动、S=停止、T=聊天 是玩家肌肉记忆里的键，
		/// 结果"按 A 攻击移动"根本做不到（还会触发第 6 槽技能）。
		/// 现在键位表与面板槽位是同一个来源，不可能再分叉。
		/// </summary>
		private static readonly Key[] _hotkeys = BuildHotkeys();

		private static Key[] BuildHotkeys()
		{
			var keys = new Key[15];
			for (int i = 0; i < keys.Length; i++)
			{
				var def = RTS.Settings.InputActions.Get($"panel_slot_{i + 1}");
				keys[i] = def?.Default ?? Key.None;
			}
			return keys;
		}

		public override void _Ready()
		{
			Instance = this;
			GameEventBus.SelectionChanged += OnBusSelectionChanged;

			// 1. 这里的检查被我加回来了，万一 Godot 丢了引用，你会看到报错而不是一脸懵
			if (Grid == null || ButtonPrefab == null)
			{
				GD.PrintErr("[ActionPanel] ❌ 严重错误：Grid 或 Prefab 未绑定！请检查 Inspector。");
				return;
			}

			Grid.Columns = 5;
			foreach (Node n in Grid.GetChildren()) n.QueueFree();

			for (int i = 0; i < 15; i++)
			{
				var btn = ButtonPrefab.Instantiate<Button>();
				btn.Name = $"Slot_{i}";
				btn.FocusMode = FocusModeEnum.None;
				btn.CustomMinimumSize = new Vector2(38, 38);
				btn.ExpandIcon = true;

				// 2. 防止 Prefab 默认隐藏导致按钮不可见
				btn.Visible = true;

				Grid.AddChild(btn);
				_slots[i] = btn;
			}
		}

		public override void _ExitTree()
		{
			GameEventBus.SelectionChanged -= OnBusSelectionChanged;
			base._ExitTree();
		}

		// 自动刷新：研究完成/资源变化/科技解锁不用重新选中也能更新（10fps 节流）
		public override void _Process(double delta)
		{
			ulong now = Time.GetTicksMsec();
			if (now - _lastAutoRefreshMs < 100)
				return;
			_lastAutoRefreshMs = now;
			if (GameEventBus.CurrentSelection.Count > 0)
				RefreshFromBus();
		}

		private void OnBusSelectionChanged(System.Collections.Generic.List<IEntity> selection, int team)
		{
			RefreshFromBus();
		}

		// 从总线缓存取第一个有效实体刷新动作按钮；按钮点击回总线，由 UserController 路由
		private void RefreshFromBus()
		{
			var first = GameEventBus.CurrentSelection.FirstOrDefault(e =>
				e != null &&
				GodotObject.IsInstanceValid(e as GodotObject) &&
				!e.IsDeadOrNull());
			Refresh(
				first?.GetAvailableActions() ?? new List<EntityAction>(),
				id => GameEventBus.PublishActionTriggered(id));
		}

		public void Refresh(List<EntityAction> actions, Action<string> onTrigger)
		{
			// 如果没初始化成功，直接跳过
			if (_slots[0] == null) return;

			// 3. 改回手动循环，比 LINQ 更稳健，不怕 Key 重复报错
			var map = new Dictionary<int, EntityAction>();
			if (actions != null)
			{
				foreach (var a in actions)
				{
					if (a.SlotIndex >= 0 && a.SlotIndex < 15)
						map[a.SlotIndex] = a;
				}
			}

			for (int i = 0; i < 15; i++)
			{
				UpdateButton(_slots[i], map.GetValueOrDefault(i), i, onTrigger);
			}
		}

		private void UpdateButton(Button btn, EntityAction act, int idx, Action<string> onTrigger)
		{
			// 清理旧信号
			foreach (var c in btn.GetSignalConnectionList(Button.SignalName.Pressed))
				btn.Disconnect(Button.SignalName.Pressed, c["callable"].AsCallable());

			// 更新快捷键显示
			var label = btn.GetNodeOrNull<Label>("KeyLabel");
			var status = btn.GetNodeOrNull<Label>("StatusLabel");
			if (status != null) status.Visible = false;

			// === 空槽位逻辑 ===
			if (act == null)
			{
				btn.Disabled = true;
				btn.Icon = null;
				btn.Text = "";
				btn.TooltipText = "";
				btn.Modulate = Colors.White;
				var emptyBar = btn.GetNodeOrNull<ProgressBar>("CdBar");
				if (emptyBar != null) emptyBar.Visible = false;
				if (label != null) label.Visible = false;
				return;
			}

			// === 有技能逻辑 ===
			if (label != null)
			{
				label.Visible = true;
				label.Text = _hotkeys[idx].ToString();
			}

			btn.Disabled = !act.IsEnabled;
			// 有前置条件未满足的按钮：整体置灰
			// 已研究完成的科技：即使按钮禁用（不可再点）也要保持绿色，不能被灰色覆盖
			btn.Modulate = act.IsEnabled
				? act.Tint
				: (act.Tint != Colors.White ? act.Tint : new Color(0.5f, 0.5f, 0.5f, 1f));
			btn.Icon = act.Icon;
			// 4. 这里加了个非空判断，防止 act.DisplayName 为 null 时崩溃
			string safeName = act.DisplayName ?? "";
			btn.Text = act.Icon == null ? safeName[..Mathf.Min(2, safeName.Length)] : "";
			string tooltip = $"{safeName} [{_hotkeys[idx]}]";
			if (!string.IsNullOrEmpty(act.StatusText))
			{
				if (status == null)
				{
					status = new Label { Name = "StatusLabel", MouseFilter = MouseFilterEnum.Ignore,
						HorizontalAlignment = HorizontalAlignment.Center, ClipText = true, ZIndex = 2 };
					btn.AddChild(status);
					status.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
					status.OffsetTop = -15;
					status.OffsetBottom = 0;
					status.AddThemeColorOverride("font_color", new Color(0.65f, 1f, 0.8f));
					status.AddThemeColorOverride("font_shadow_color", Colors.Black);
					status.AddThemeConstantOverride("shadow_outline_size", 2);
				}
				status.Text = act.StatusText;
				status.AddThemeFontSizeOverride("font_size", Mathf.Clamp((int)(Mathf.Max(38, btn.Size.X) / act.StatusText.Length), 8, 11));
				status.Visible = true;
				tooltip += "\n" + act.StatusText;
			}
			if (!string.IsNullOrEmpty(act.Tooltip))
				tooltip = act.Tooltip + "\n" + tooltip;
			btn.TooltipText = tooltip;

			// 技能冷却进度条（仿生产队列）：冷却中显示在按钮上
			var cdBar = btn.GetNodeOrNull<ProgressBar>("CdBar");
			if (act.CooldownMax > 0f)
			{
				if (cdBar == null)
				{
					cdBar = new ProgressBar { Name = "CdBar" };
					cdBar.SetAnchorsPreset(Control.LayoutPreset.FullRect);
					cdBar.MouseFilter = Control.MouseFilterEnum.Ignore;
					cdBar.ShowPercentage = false;
					btn.AddChild(cdBar);
				}

				cdBar.MaxValue = act.CooldownMax;
				cdBar.Value = Mathf.Clamp(act.CooldownRemaining, 0f, act.CooldownMax);
				cdBar.Visible = act.CooldownRemaining > 0f;
			}
			else if (cdBar != null)
			{
				cdBar.Visible = false;
			}

			btn.Pressed += () =>
			{
				GameAudio.Instance?.Play("click");
				onTrigger?.Invoke(act.ActionId);
			};
		}

		public override void _UnhandledInput(InputEvent @event)
		{
            if (RTS.UI.MatchMenu.BlocksGameInput) return;
			if (@event is InputEventKey k && k.Pressed && !k.Echo)
			{
				int idx = Array.IndexOf(_hotkeys, k.Keycode);
				// 确保索引有效 且 按钮已初始化 且 按钮可用
				if (idx >= 0 && idx < 15 && _slots[idx] != null && !_slots[idx].Disabled)
				{
					_slots[idx].EmitSignal(Button.SignalName.Pressed);
					GetViewport().SetInputAsHandled();
				}
			}
		}
	}
}
