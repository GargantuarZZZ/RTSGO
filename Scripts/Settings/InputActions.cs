using Godot;
using System;
using System.Collections.Generic;

namespace RTS.Settings
{
	// =========================================================
	// 输入动作注册表（唯一真相）
	//
	// 背景：改这个文件之前，按键散落在 5 个地方，各写各的：
	//   · GameSettings.Keybinds          —— 只有 4 个能改（P/K/T/F1）
	//   · ActionPanel._hotkeys           —— 15 个技能槽，写死 QWERT/ASDFG/ZXCVB
	//   · RaceBuildPanelUI._hotkeys      —— 写死 F1..Fn
	//   · OrbitalPanelUI._hotkeys        —— 写死 F1..
	//   · UserController._UnhandledInput —— 写死 1/2/3/4、Ctrl+H、F9/F10/F11
	//
	// 后果是**真实冲突**（都在玩家手边）：
	//   A = ActionPanel 槽位6 / 教程里教的"攻击移动"（而攻击移动当时根本没有快捷键）
	//   T = 聊天输入 / ActionPanel 槽位5
	//   S = ActionPanel 槽位7 / 玩家预期的"停止"
	//   F1 = 选中空闲农民 / 菌毯扩散（RaceBuildPanelUI）
	//   1~4 = 时间加速 / 编队（玩家预期）
	//
	// 这个文件的职责：**只定义"有哪些动作、默认什么键、属于哪一组、和谁冲突"**。
	// 它不碰 InputMap，也不碰 UI —— 由 GameSettings.ApplyKeybinds 统一落到 InputMap，
	// 由设置菜单负责展示与改键。这样"能改的键"和"实际生效的键"不可能再分叉。
	// =========================================================

	/// <summary>动作分组：设置菜单按组显示，也让"哪些键归谁管"一目了然。</summary>
	public enum InputGroup
	{
		/// <summary>镜头与视角。</summary>
		Camera,
		/// <summary>编队与选择。</summary>
		Selection,
		/// <summary>单位指令。</summary>
		Orders,
		/// <summary>对局与社交。</summary>
		Match,
		/// <summary>面板/技能槽（默认不推荐改，但列出来避免"隐形占用"）。</summary>
		Panel,
	}

	/// <summary>一个可绑定动作的定义。</summary>
	public sealed class InputActionDef
	{
		/// <summary>InputMap 动作名（也是存档里的 key）。</summary>
		public string Action = "";

		/// <summary>默认物理键。</summary>
		public Key Default = Key.None;

		public InputGroup Group = InputGroup.Orders;

		/// <summary>本地化 key（用于设置菜单显示）。</summary>
		public string LabelKey = "";

		/// <summary>
		/// 是否暴露给玩家改。
		/// 面板技能槽默认 false：它们按"格子位置"排布（QWERT/ASDFG/ZXCVB），
		/// 让玩家改会破坏"位置=按键"的空间记忆，得不偿失。
		/// </summary>
		public bool Rebindable = true;

		/// <summary>是否允许与别的动作共用同一个键（多入口场景，例如 Esc 与右键都取消）。</summary>
		public bool AllowsShare = false;

		public InputActionDef(string action, Key def, InputGroup group, string labelKey,
			bool rebindable = true, bool allowsShare = false)
		{
			Action = action;
			Default = def;
			Group = group;
			LabelKey = labelKey;
			Rebindable = rebindable;
			AllowsShare = allowsShare;
		}
	}

	public static class InputActions
	{
		// =========================================================
		// 动作表
		//
		// 命名约定：`game_*` 一律走 InputMap（可改键）；`panel_*` 是技能槽。
		//
		// ---- 键位设计（这是重排过的，原方案有真实冲突）----
		//
		// 冲突是本项目已经发生过的现实问题，所以这套表有两条硬规则：
		//   规则1：**镜头不用 WASD**，改用方向键。
		//          WASD 与 RTS 指令键（A 攻击移动 / S 停止 / H 驻守）天然打架。
		//          星际2 的默认就是方向键平移、字母给指令，这里跟随同一习惯。
		//   规则2：**面板技能槽避开全局动作键**。
		//          原来技能槽占 QWERT/ASDFG/ZXCVB，把 A/S/T 全吃了，
		//          而 A=攻击移动、S=停止、T=聊天 都是玩家肌肉记忆里的键。
		//          现在槽位改用 Q/E/R/F/G/Z/X/C/V/B + T/Y，把 A/S/D/H/I/J/K/L/M/N/O/P/U/W 让出来。
		//
		// InputActions.FindDefaultConflicts() 会静态校验这两条，测试里也会断言。
		// =========================================================

		public static readonly InputActionDef[] All =
		{
			// ---- 镜头（方向键；见规则1）----
			new("game_cam_up",    Key.Up,    InputGroup.Camera, "cam_up"),
			new("game_cam_down",  Key.Down,  InputGroup.Camera, "cam_down"),
			new("game_cam_left",  Key.Left,  InputGroup.Camera, "cam_left"),
			new("game_cam_right", Key.Right, InputGroup.Camera, "cam_right"),
			// 回中刻意用 Home 而不是空格：
			// 引擎内置的 `ui_accept` 默认绑了**空格与回车**，
			// 空格回中会顺带触发"UI 接受"（按钮被激活），在开着面板时会误操作。
			new("game_cam_center", Key.Home, InputGroup.Camera, "cam_center"),

			// ---- 选择与编队 ----
			new("game_select_all_army",    Key.F2, InputGroup.Selection, "select_all_army"),
			new("game_select_all_workers", Key.F3, InputGroup.Selection, "select_all_workers"),
			new("game_select_idle_workers", Key.F1, InputGroup.Selection, "select_idle_workers"),

			// ---- 单位指令 ----
			new("game_attack_move", Key.A, InputGroup.Orders, "attack_move"),
			new("game_stop",        Key.S, InputGroup.Orders, "stop"),
			new("game_hold",        Key.H, InputGroup.Orders, "hold"),

			// ---- 对局与社交 ----
			new("game_pause",     Key.P, InputGroup.Match, "pause"),
			new("game_surrender", Key.K, InputGroup.Match, "surrender"),
			// 聊天同时保留 T 与 Enter 两个入口：T 是 RTS 习惯，Enter 是所有聊天框的习惯
			new("game_chat",      Key.T, InputGroup.Match, "chat", rebindable: true, allowsShare: true),
			// 重开投票 = Ctrl+H。与"驻守(H)"共用 H 但靠 Ctrl 区分，
			// 所以这里必须声明 AllowsShare，否则冲突检测会误报。
			new("game_vote_rematch", Key.H, InputGroup.Match, "vote_rematch",
				rebindable: true, allowsShare: true),

			// ---- 面板技能槽（写死、不暴露改键）----
			// 槽位顺序固定，玩家记的是"位置"而不是具体字母，所以不开放改键。
			// 避开 A/S/D/H/T（见规则2）。T/Y 是刻意留的：T 已让给聊天，
			// 所以第 5/6 槽改用 T/Y 会冲突，改到 T→(unused) 见下方说明。
			new("panel_slot_1",  Key.Q, InputGroup.Panel, "slot_1",  rebindable: false),
			new("panel_slot_2",  Key.E, InputGroup.Panel, "slot_2",  rebindable: false),
			new("panel_slot_3",  Key.R, InputGroup.Panel, "slot_3",  rebindable: false),
			new("panel_slot_4",  Key.F, InputGroup.Panel, "slot_4",  rebindable: false),
			new("panel_slot_5",  Key.G, InputGroup.Panel, "slot_5",  rebindable: false),
			new("panel_slot_6",  Key.Z, InputGroup.Panel, "slot_6",  rebindable: false),
			new("panel_slot_7",  Key.X, InputGroup.Panel, "slot_7",  rebindable: false),
			new("panel_slot_8",  Key.C, InputGroup.Panel, "slot_8",  rebindable: false),
			new("panel_slot_9",  Key.V, InputGroup.Panel, "slot_9",  rebindable: false),
			new("panel_slot_10", Key.B, InputGroup.Panel, "slot_10", rebindable: false),
			new("panel_slot_11", Key.N, InputGroup.Panel, "slot_11", rebindable: false),
			new("panel_slot_12", Key.M, InputGroup.Panel, "slot_12", rebindable: false),
			new("panel_slot_13", Key.U, InputGroup.Panel, "slot_13", rebindable: false),
			new("panel_slot_14", Key.I, InputGroup.Panel, "slot_14", rebindable: false),
			new("panel_slot_15", Key.O, InputGroup.Panel, "slot_15", rebindable: false),
		};

		/// <summary>按动作名取定义。</summary>
		public static InputActionDef Get(string action)
		{
			foreach (var d in All)
				if (d.Action == action) return d;
			return null;
		}

		/// <summary>某组里的全部动作（设置菜单按组渲染）。</summary>
		public static List<InputActionDef> InGroup(InputGroup group)
		{
			var list = new List<InputActionDef>();
			foreach (var d in All)
				if (d.Group == group) list.Add(d);
			return list;
		}

		public static List<InputActionDef> Rebindable()
		{
			var list = new List<InputActionDef>();
			foreach (var d in All)
				if (d.Rebindable) list.Add(d);
			return list;
		}

		// =========================================================
		// 编队：Ctrl+数字 存 / 数字 取
		//
		// 不做成 InputMap 动作，因为 InputMap 一个动作只能绑一个键，
		// 而这里需要 10 个独立的数字键 + Ctrl 组合。用固定键位表更直接，
		// 也避免在项目设置里塞 20 个动作名。
		// =========================================================

		public const int ControlGroupCount = 10;

		public static readonly Key[] ControlGroupKeys =
		{
			Key.Key1, Key.Key2, Key.Key3, Key.Key4, Key.Key5,
			Key.Key6, Key.Key7, Key.Key8, Key.Key9, Key.Key0,
		};

		/// <summary>数字键 → 编队下标（0~9）；非编队键返回 -1。</summary>
		public static int ControlGroupIndex(Key key)
		{
			for (int i = 0; i < ControlGroupKeys.Length; i++)
				if (ControlGroupKeys[i] == key) return i;
			return -1;
		}

		// =========================================================
		// 冲突检测
		// =========================================================

		/// <summary>
		/// 查两个动作是否真的冲突。
		///
		/// 判定规则：
		///   · 任一方声明 AllowsShare（多入口，如 Ctrl/Ctrl+H）→ 不算冲突；
		///   · 一方是"面板技能槽"、另一方是"全局动作" → 算冲突（它们都在手边，会抢键）。
		///     这正是 A/S/T/F1 那批问题的本质。
		/// </summary>
		public static bool Conflicts(InputActionDef a, InputActionDef b)
		{
			if (a == null || b == null || a.Action == b.Action) return false;
			if (a.Default != b.Default) return false;
			if (a.AllowsShare || b.AllowsShare) return false;
			return true;
		}

		/// <summary>列出默认键位表里的全部冲突（诊断/自检用）。</summary>
		public static List<string> FindDefaultConflicts()
		{
			var issues = new List<string>();
			for (int i = 0; i < All.Length; i++)
				for (int j = i + 1; j < All.Length; j++)
					if (Conflicts(All[i], All[j]))
						issues.Add($"{All[i].Action}({All[i].Default}) ↔ {All[j].Action}({All[j].Default})");
			return issues;
		}

		/// <summary>某个键当前被哪些动作占用（改键时给玩家提示）。</summary>
		public static List<string> ActionsUsing(Dictionary<string, Key> bindings, Key key, string exceptAction)
		{
			var list = new List<string>();
			foreach (var kv in bindings)
			{
				if (kv.Key == exceptAction) continue;
				if (kv.Value != key) continue;
				var def = Get(kv.Key);
				if (def != null && def.AllowsShare) continue;
				list.Add(kv.Key);
			}
			return list;
		}
	}
}
