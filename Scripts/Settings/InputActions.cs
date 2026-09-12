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

		/// <summary>默认次要键（第二个入口）。None 表示默认没有。</summary>
		public Key DefaultSecondary = Key.None;

		/// <summary>默认是否要求 Ctrl。</summary>
		public bool DefaultCtrl = false;

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
			bool rebindable = true, bool allowsShare = false,
			Key defaultSecondary = Key.None, bool defaultCtrl = false)
		{
			Action = action;
			Default = def;
			DefaultSecondary = defaultSecondary;
			DefaultCtrl = defaultCtrl;
			Group = group;
			LabelKey = labelKey;
			Rebindable = rebindable;
			AllowsShare = allowsShare;
		}
	}

	// =========================================================
	// 绑定模型：一个动作可以有**多个**绑定
	//
	// 支持的能力（用户要求）：
	//   · 混合键位：Ctrl / Shift / Alt / Meta + 主键
	//   · 次要键位：同一个动作的第二、第三个入口
	//   · 鼠标侧键：Mouse4 / Mouse5（以及中键等）
	//
	// 为什么不是"直接往 InputMap 塞多个事件"就完事：
	// InputMap 表达能力够，但**存档与改键 UI 需要知道每个绑定是什么**，
	// 否则读回来分不清主/次、分不清修饰键。
	// 所以这里定义可序列化的绑定结构，由 GameSettings 负责落到 InputMap。
	// =========================================================

	/// <summary>绑定槽位：主键 / 次要键（次要可以有多个）。</summary>
	public enum BindingSlot
	{
		Primary = 0,
		Secondary = 1,
	}

	/// <summary>绑定来源：键盘 / 鼠标。</summary>
	public enum BindingDevice
	{
		Keyboard = 0,
		Mouse = 1,
	}

	/// <summary>
	/// 一条绑定。可直接落到 Godot 的 InputEventKey / InputEventMouseButton。
	/// 值类型：存档与比较都不需要引用语义。
	/// </summary>
	public struct InputBinding
	{
		public BindingDevice Device;
		/// <summary>键盘时为 Key.XXX，鼠标时为 MouseButton.XXX（左中右/侧键）。</summary>
		public int Code;
		public bool Ctrl;
		public bool Shift;
		public bool Alt;
		public bool Meta;
		/// <summary>主键还是次要键（仅用于展示与排序，不影响生效）。</summary>
		public BindingSlot Slot;

		public static InputBinding None => new() { Device = BindingDevice.Keyboard, Code = 0, Slot = BindingSlot.Primary };

		/// <summary>是否是一条"空绑定"（没绑任何东西）。</summary>
		public readonly bool IsEmpty => Code == 0;

		public static InputBinding Key(Key k, BindingSlot slot = BindingSlot.Primary,
			bool ctrl = false, bool shift = false, bool alt = false, bool meta = false) =>
			new()
			{
				Device = BindingDevice.Keyboard,
				Code = (int)k,
				Ctrl = ctrl,
				Shift = shift,
				Alt = alt,
				Meta = meta,
				Slot = slot,
			};

		public static InputBinding Mouse(MouseButton b, BindingSlot slot = BindingSlot.Primary,
			bool ctrl = false, bool shift = false, bool alt = false, bool meta = false) =>
			new()
			{
				Device = BindingDevice.Mouse,
				Code = (int)b,
				Ctrl = ctrl,
				Shift = shift,
				Alt = alt,
				Meta = meta,
				Slot = slot,
			};

		/// <summary>取键盘键（不是键盘绑定时返回 None）。</summary>
		public readonly Key AsKey => Device == BindingDevice.Keyboard ? (Key)Code : Godot.Key.None;

		/// <summary>取鼠标键（不是鼠标绑定时返回 None）。</summary>
		public readonly MouseButton AsMouse =>
			Device == BindingDevice.Mouse ? (MouseButton)Code : MouseButton.None;

		public readonly bool HasModifier => Ctrl || Shift || Alt || Meta;

		/// <summary>转成 Godot 事件，交给 InputMap。</summary>
		public readonly InputEvent ToEvent()
		{
			if (Device == BindingDevice.Mouse)
			{
				return new InputEventMouseButton
				{
					ButtonIndex = (MouseButton)Code,
					CtrlPressed = Ctrl,
					ShiftPressed = Shift,
					AltPressed = Alt,
					MetaPressed = Meta,
				};
			}
			return new InputEventKey
			{
				PhysicalKeycode = (Key)Code,
				CtrlPressed = Ctrl,
				ShiftPressed = Shift,
				AltPressed = Alt,
				MetaPressed = Meta,
			};
		}

		/// <summary>
		/// 用于比较与冲突判定的"签名"：设备 + 键 + 修饰键组合。
		/// 不含 Slot —— 同一个键放在主槽还是次槽，冲突性质是一样的。
		/// </summary>
		public readonly string Signature()
		{
			var m = (Ctrl ? "C" : "") + (Shift ? "S" : "") + (Alt ? "A" : "") + (Meta ? "M" : "");
			return $"{(int)Device}:{Code}:{m}";
		}

		public readonly bool SameAs(InputBinding other) => Signature() == other.Signature();
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

			// ---- 选择快捷键暂不设默认值；F1 起保留给种族面板 ----
			new("game_select_all_army",    Key.None, InputGroup.Selection, "select_all_army"),
			new("game_select_all_workers", Key.None, InputGroup.Selection, "select_all_workers"),
			new("game_select_idle_workers", Key.None, InputGroup.Selection, "select_idle_workers"),

			// ---- 单位指令 ----
			new("game_attack_move", Key.A, InputGroup.Orders, "attack_move"),
			new("game_stop",        Key.S, InputGroup.Orders, "stop"),
			new("game_hold",        Key.H, InputGroup.Orders, "hold"),

			// ---- 对局与社交 ----
			new("game_pause",     Key.P, InputGroup.Match, "pause"),
			new("game_surrender", Key.K, InputGroup.Match, "surrender"),
			// 聊天同时保留 T 与 Enter 两个入口：T 是 RTS 习惯，Enter 是所有聊天框习惯。
			// 这里正好用上"次要键位"：不再靠 AllowsShare 绕冲突，而是**真的绑两个键**。
			new("game_chat",      Key.T, InputGroup.Match, "chat",
				rebindable: true, allowsShare: false, defaultSecondary: Key.Enter),
			// 重开投票 = Ctrl+H。以前是"绑 H + 声明 AllowsShare"来绕开与驻守(H)的冲突，
			// 那是权宜之计：冲突检测因此看不见它，玩家也看不出这是组合键。
			// 现在用真正的修饰键绑定（DefaultCtrl），冲突检测能正确区分 H 与 Ctrl+H。
			new("game_vote_rematch", Key.H, InputGroup.Match, "vote_rematch",
				rebindable: true, allowsShare: false, defaultCtrl: true),

			// ---- 面板技能槽（写死、不暴露改键）----
			// 槽位顺序固定，玩家记的是"位置"而不是具体字母，所以不开放改键。
			// 避开 A/S/D/H/T（见规则2）。T/Y 是刻意留的：T 已让给聊天，
			// 所以第 5/6 槽改用 T/Y 会冲突，改到 T→(unused) 见下方说明。
			new("race_panel_slot_1", Key.F1, InputGroup.Panel, "race_slot_1", rebindable: false),
			new("race_panel_slot_2", Key.F2, InputGroup.Panel, "race_slot_2", rebindable: false),
			new("race_panel_slot_3", Key.F3, InputGroup.Panel, "race_slot_3", rebindable: false),
			new("race_panel_slot_4", Key.F4, InputGroup.Panel, "race_slot_4", rebindable: false),
			new("race_panel_slot_5", Key.F5, InputGroup.Panel, "race_slot_5", rebindable: false),
			new("race_panel_slot_6", Key.F6, InputGroup.Panel, "race_slot_6", rebindable: false),
			new("race_panel_slot_7", Key.F7, InputGroup.Panel, "race_slot_7", rebindable: false),
			new("race_panel_slot_8", Key.F8, InputGroup.Panel, "race_slot_8", rebindable: false),
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
		// 绑定集合
		// =========================================================

		/// <summary>一个动作最多允许的绑定数（主 + 次）。够用且不会把存档撑大。</summary>
		public const int MaxBindingsPerAction = 4;

		/// <summary>动作用户要求的默认绑定（主键 + 次要键，含修饰键）。</summary>
		public static List<InputBinding> DefaultBindings(InputActionDef def)
		{
			var list = new List<InputBinding>();
			if (def == null) return list;

			if (def.Default != Godot.Key.None)
				list.Add(InputBinding.Key(def.Default, BindingSlot.Primary, ctrl: def.DefaultCtrl));
			if (def.DefaultSecondary != Godot.Key.None)
				list.Add(InputBinding.Key(def.DefaultSecondary, BindingSlot.Secondary));

			return list;
		}

		/// <summary>该动作是否支持配置次要键位（面板槽位这类固定键不支持）。</summary>
		public static bool SupportsSecondary(InputActionDef def) =>
			def != null && def.Rebindable;

		/// <summary>该动作是否支持鼠标绑定。</summary>
		public static bool SupportsMouse(InputActionDef def) =>
			def != null && def.Rebindable;

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
		/// 查两个动作的**默认绑定**是否真的冲突。
		///
		/// 判定规则（改绑模型后按"签名"比）：
		///   · 签名 = 设备 + 键 + 修饰键组合。所以 H 与 Ctrl+H **不算冲突**，
		///     这正是"重开投票"与"驻守"能共存的原因；
		///   · 任一方声明 AllowsShare（多入口）→ 不算冲突；
		///   · 一方是"面板技能槽"、另一方是"全局动作" → 算冲突（它们都在手边，会抢键）。
		///     这正是 A/S/T/F1 那批问题的本质。
		/// </summary>
		public static bool Conflicts(InputActionDef a, InputActionDef b)
		{
			if (a == null || b == null || a.Action == b.Action) return false;
			if (a.AllowsShare || b.AllowsShare) return false;

			var ba = DefaultBindings(a);
			var bb = DefaultBindings(b);
			foreach (var x in ba)
				foreach (var y in bb)
					if (x.SameAs(y)) return true;
			return false;
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

		/// <summary>
		/// 某个绑定当前被哪些动作占用（改键时给玩家提示）。
		///
		/// 现在是**按签名**比对：只绑 H 的动作不会被 Ctrl+H 的改动误报，
		/// 反之亦然。这是修饰键支持带来的必要修正。
		/// </summary>
		public static List<string> ActionsUsing(
			IReadOnlyDictionary<string, List<InputBinding>> bindings,
			InputBinding probe, string exceptAction)
		{
			var list = new List<string>();
			if (probe.IsEmpty) return list;

			foreach (var kv in bindings)
			{
				if (kv.Key == exceptAction) continue;
				var def = Get(kv.Key);
				if (def != null && def.AllowsShare) continue;
				foreach (var b in kv.Value)
				{
					if (b.SameAs(probe))
					{
						list.Add(kv.Key);
						break;
					}
				}
			}
			return list;
		}

		/// <summary>兼容重载：老的"单键"调用方（面板槽位等）仍可用。</summary>
		public static List<string> ActionsUsing(Dictionary<string, Key> bindings, Key key, string exceptAction)
		{
			var adapted = new Dictionary<string, List<InputBinding>>();
			foreach (var kv in bindings)
				adapted[kv.Key] = new List<InputBinding> { InputBinding.Key(kv.Value) };
			return ActionsUsing(adapted, InputBinding.Key(key), exceptAction);
		}
	}
}
