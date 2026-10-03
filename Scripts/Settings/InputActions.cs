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
		// ---- 键位设计：命令卡就是键盘上那个矩形 ----
		//
		// 命令卡是 5 列 × 3 行，15 个格子固定排布。键位取键盘上**同一个矩形**：
		//
		//        Q W E R T        ← 面板第 1 行（槽 1~5）
		//        A S D F G        ← 面板第 2 行（槽 6~10）
		//        Z X C V B        ← 面板第 3 行（槽 11~15）
		//
		// 这样"格子在哪、键就在哪"，玩家不用背这 15 个字母。
		//
		// 这里曾经反着做过一轮：为了给全局动作让路，把槽位改成
		// Q/E/R/F/G + Z/X/C/V/B + N/M/U/I/O —— 于是面板第二行从 Z 开始、
		// 第三行是 N M U I O，键位和格子位置完全对不上，玩家只能说"快捷键全是错的"。
		// 结论：**面板占住 QWERT/ASDFG/ZXCVB，其它动作绕道**，不回退到"让面板躲"。
		//
		// 被挤走的全局键各自平移到同一物理行的右侧邻居（矩形右边正好多出 Y/H/J/N 四格）：
		//     攻击移动  A → H       停止  S → J       聊天  T → Y
		//     驻守      H → N
		//     重开投票  Ctrl+H 不变（修饰键签名不同，与裸 H 不构成冲突）
		//
		// 两条硬规则：
		//   规则1：**镜头不用 WASD**，改用方向键。
		//          WASD 与 RTS 指令键（A 攻击移动 / S 停止 / H 驻守）天然打架。
		//          星际2 的默认就是方向键平移、字母给指令，这里跟随同一习惯。
		//   规则2：**面板矩形与全局动作键不得重叠**（重合的后果是按键既触发技能又触发指令）。
		//
		// FindDefaultConflicts() 静态校验规则2；PanelGridProblems() 校验
		// "槽位默认值 == PanelGridKeys 里声明的矩形"；测试里两条都会断言。
		// =========================================================

		/// <summary>
		/// 面板 15 个格子在键盘上对应的矩形，顺序与槽位一致：从左到右、从上到下。
		///
		/// **这就是"面板键位"的唯一真相**：面板按钮上的角标、ActionPanel 的按键处理、
		/// 设置菜单里 Panel 分组显示的键，全部由它派生。
		/// 改这里等于改整块面板的键位，改完必须同步 LayoutVersion 让老存档迁移。
		/// </summary>
		public static readonly Key[] PanelGridKeys =
		{
			Key.Q, Key.W, Key.E, Key.R, Key.T,
			Key.A, Key.S, Key.D, Key.F, Key.G,
			Key.Z, Key.X, Key.C, Key.V, Key.B,
		};

		/// <summary>面板格子数（UI 与键位表共用，避免一边改一边忘）。</summary>
		public const int PanelSlotCount = 15;

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
			// 攻击移动/停止原本是 A/S，被面板矩形（ASDFG）征用，平移到同一物理行的右侧：
			// H 是矩形右边第一格，J 是第二格；驻守原本就是 H，让给攻击移动后挪到底行 B 右边的 N。
			new("game_attack_move", Key.H, InputGroup.Orders, "attack_move"),
			new("game_stop",        Key.J, InputGroup.Orders, "stop"),
			new("game_hold",        Key.N, InputGroup.Orders, "hold"),

			// ---- 对局与社交 ----
			new("game_pause",     Key.P, InputGroup.Match, "pause"),
			new("game_surrender", Key.K, InputGroup.Match, "surrender"),
			// 聊天同时保留字母键与 Enter 两个入口：字母键是 RTS 习惯（原本是 T，
			// 被面板第 5 槽征用后挪到它右侧的 Y），Enter 是所有聊天框习惯。
			// 这里正好用上"次要键位"：不再靠 AllowsShare 绕冲突，而是**真的绑两个键**。
			new("game_chat",      Key.Y, InputGroup.Match, "chat",
				rebindable: true, allowsShare: false, defaultSecondary: Key.Enter),
			// 重开投票 = Ctrl+H。以前是"绑 H + 声明 AllowsShare"来绕开与驻守(H)的冲突，
			// 那是权宜之计：冲突检测因此看不见它，玩家也看不出这是组合键。
			// 现在用真正的修饰键绑定（DefaultCtrl），冲突检测能正确区分 H 与 Ctrl+H
			// （裸 H 现在是攻击移动，同样是"Ctrl+H 与裸 H 各自成立"的例子）。
			new("game_vote_rematch", Key.H, InputGroup.Match, "vote_rematch",
				rebindable: true, allowsShare: false, defaultCtrl: true),
			// 投票的同意/反对。原来这两个键**写死在 VoteUI 里**（N/M），不在注册表里 ——
			// 设置菜单看不见、冲突检测查不到，而 N 正好是驻守：按下同意会顺带让全军原地驻守。
			// 收进注册表后：可改键、可检测冲突、界面显示的是真实绑定。
			// 同意用 O（OK），反对保留 M。
			new("game_vote_yes", Key.O, InputGroup.Match, "vote_yes"),
			new("game_vote_no",  Key.M, InputGroup.Match, "vote_no"),

			// ---- 面板技能槽（写死、不暴露改键）----
			// 槽位顺序固定，玩家记的是"位置"而不是具体字母，所以不开放改键。
			// 字母必须与 PanelGridKeys 逐项一致（面板就是 QWERT/ASDFG/ZXCVB 这块矩形）。
			// 为什么这里仍然手写一遍字母、而不是直接 PanelGridKeys[i]：
			// 手写让"哪个槽是哪个键"在表里一眼可读，代价是靠 PanelGridProblems() 防漂移。
			new("race_panel_slot_1", Key.F1, InputGroup.Panel, "race_slot_1", rebindable: false),
			new("race_panel_slot_2", Key.F2, InputGroup.Panel, "race_slot_2", rebindable: false),
			new("race_panel_slot_3", Key.F3, InputGroup.Panel, "race_slot_3", rebindable: false),
			new("race_panel_slot_4", Key.F4, InputGroup.Panel, "race_slot_4", rebindable: false),
			new("race_panel_slot_5", Key.F5, InputGroup.Panel, "race_slot_5", rebindable: false),
			new("race_panel_slot_6", Key.F6, InputGroup.Panel, "race_slot_6", rebindable: false),
			new("race_panel_slot_7", Key.F7, InputGroup.Panel, "race_slot_7", rebindable: false),
			new("race_panel_slot_8", Key.F8, InputGroup.Panel, "race_slot_8", rebindable: false),
			// 第 1 行
			new("panel_slot_1",  Key.Q, InputGroup.Panel, "slot_1",  rebindable: false),
			new("panel_slot_2",  Key.W, InputGroup.Panel, "slot_2",  rebindable: false),
			new("panel_slot_3",  Key.E, InputGroup.Panel, "slot_3",  rebindable: false),
			new("panel_slot_4",  Key.R, InputGroup.Panel, "slot_4",  rebindable: false),
			new("panel_slot_5",  Key.T, InputGroup.Panel, "slot_5",  rebindable: false),
			// 第 2 行
			new("panel_slot_6",  Key.A, InputGroup.Panel, "slot_6",  rebindable: false),
			new("panel_slot_7",  Key.S, InputGroup.Panel, "slot_7",  rebindable: false),
			new("panel_slot_8",  Key.D, InputGroup.Panel, "slot_8",  rebindable: false),
			new("panel_slot_9",  Key.F, InputGroup.Panel, "slot_9",  rebindable: false),
			new("panel_slot_10", Key.G, InputGroup.Panel, "slot_10", rebindable: false),
			// 第 3 行
			new("panel_slot_11", Key.Z, InputGroup.Panel, "slot_11", rebindable: false),
			new("panel_slot_12", Key.X, InputGroup.Panel, "slot_12", rebindable: false),
			new("panel_slot_13", Key.C, InputGroup.Panel, "slot_13", rebindable: false),
			new("panel_slot_14", Key.V, InputGroup.Panel, "slot_14", rebindable: false),
			new("panel_slot_15", Key.B, InputGroup.Panel, "slot_15", rebindable: false),
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
		/// 面板槽位表与 <see cref="PanelGridKeys"/> 是否还一致（诊断/自检用）。
		///
		/// 面板的键位是"格子 = 键位"的空间记忆，一旦某个槽的字母和矩形对不上，
		/// 玩家看到的就是"快捷键全是错的"。这类错字不会让任何东西报错，
		/// 所以必须有一条专门的断言。
		/// </summary>
		public static List<string> PanelGridProblems()
		{
			var issues = new List<string>();
			for (int i = 0; i < PanelSlotCount; i++)
			{
				var def = Get($"panel_slot_{i + 1}");
				if (def == null)
				{
					issues.Add($"panel_slot_{i + 1} 不在动作表里");
					continue;
				}
				Key want = i < PanelGridKeys.Length ? PanelGridKeys[i] : Key.None;
				if (def.Default != want)
					issues.Add($"panel_slot_{i + 1} 默认是 {def.Default}，矩形里应为 {want}");
			}
			return issues;
		}

		// =========================================================
		// 老存档迁移
		//
		// 面板槽位虽然 `rebindable: false`，但它们同样会被写进 settings.cfg
		// （GameSettings.Save 遍历的是全部 Bindings）。所以只改默认值不够：
		// 老玩家的存档里存着旧默认值，Load 会把它们读回来，新布局永远不生效。
		//
		// 迁移规则只有一条：**存档值仍等于旧默认值的，才升级成新默认值**。
		// 玩家自己改过的键（哪怕改成了别的旧默认值）一律不动。
		// =========================================================

		/// <summary>
		/// 默认键位布局版本。键位表动了布局（换字母、挪矩形）就 +1，
		/// 老存档会在 GameSettings.Load 里按 <see cref="LegacyV1"/> 迁移。
		/// </summary>
		public const int LayoutVersion = 2;

		/// <summary>
		/// 版本 1 的默认键位中**与当前不同**的那些（动作、旧主键、旧次要键）。
		/// 只列变过的：没变的动作即使存档里是旧值，迁移也没意义。
		/// </summary>
		public static readonly (string Action, Key Primary, Key Secondary)[] LegacyV1 =
		{
			// 全局动作：给面板矩形让路
			("game_attack_move", Key.A, Key.None),
			("game_stop",        Key.S, Key.None),
			("game_hold",        Key.H, Key.None),
			("game_chat",        Key.T, Key.Enter),
			// 面板槽位：从"避让式"键位改回 QWERT/ASDFG/ZXCVB
			("panel_slot_2",  Key.E, Key.None),
			("panel_slot_3",  Key.R, Key.None),
			("panel_slot_4",  Key.F, Key.None),
			("panel_slot_5",  Key.G, Key.None),
			("panel_slot_6",  Key.Z, Key.None),
			("panel_slot_7",  Key.X, Key.None),
			("panel_slot_8",  Key.C, Key.None),
			("panel_slot_9",  Key.V, Key.None),
			("panel_slot_10", Key.B, Key.None),
			("panel_slot_11", Key.N, Key.None),
			("panel_slot_12", Key.M, Key.None),
			("panel_slot_13", Key.U, Key.None),
			("panel_slot_14", Key.I, Key.None),
			("panel_slot_15", Key.O, Key.None),
			// panel_slot_1 旧新都是 Q，不需要迁移
		};

		/// <summary>
		/// 把"仍是旧默认值"的绑定升级成当前默认值，返回迁移条数。
		/// <paramref name="bindings"/> 会被原地修改。
		/// </summary>
		public static int MigrateLegacyDefaults(Dictionary<string, List<InputBinding>> bindings)
		{
			if (bindings == null) return 0;

			int changed = 0;
			foreach (var (action, oldPrimary, oldSecondary) in LegacyV1)
			{
				var def = Get(action);
				if (def == null) continue;
				if (!bindings.TryGetValue(action, out var cur)) continue;
				if (!MatchesLegacy(cur, oldPrimary, oldSecondary)) continue;

				bindings[action] = DefaultBindings(def);
				changed++;
			}
			return changed;
		}

		/// <summary>恰好只绑了"旧主键（+旧次要键）"、没有修饰键 —— 说明玩家没改过它。</summary>
		private static bool MatchesLegacy(List<InputBinding> list, Key primary, Key secondary)
		{
			var want = new List<Key> { primary };
			if (secondary != Key.None) want.Add(secondary);

			if (list == null || list.Count != want.Count) return false;
			for (int i = 0; i < want.Count; i++)
			{
				var b = list[i];
				if (b.Device != BindingDevice.Keyboard || b.HasModifier || b.AsKey != want[i])
					return false;
			}
			return true;
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
