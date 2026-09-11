using Godot;
using System;

namespace RTS.Data
{
	// =========================================================
	// 地图触发器数据模型（纯数据，Godot Resource）
	//
	// 分层约定：
	//   - 本文件只描述"作者写了什么"，不含执行逻辑，可安全序列化进地图。
	//   - 执行器（MapTriggerRuntime）只读这些字段；表达式求值走 SimExpr（纯 C#、Fix64）。
	//   - 所有可变运行时状态（变量、计时器、已触发标记）由执行器持有，
	//     并**必须纳入确定性哈希**——否则两端触发器状态分叉不会被发现。
	// =========================================================

	/// <summary>条件求值失败时的取值策略。</summary>
	public enum TriggerEvalMode
	{
		/// <summary>未定义变量/未知标识符一律视为 0（宽松，作者友好）。</summary>
		Lenient = 0,
		/// <summary>未定义即报错并跳过本次触发（严格，便于查出拼写错误）。</summary>
		Strict = 1,
	}

	/// <summary>单个触发器的求值时机。</summary>
	public enum TriggerPhase
	{
		/// <summary>每 tick 求值一次（用于持续性条件，如"区域内有敌方单位"）。</summary>
		EveryTick = 0,
		/// <summary>只在指定 tick 求值（定时事件，配 IntervalTicks 做周期）。</summary>
		Scheduled = 1,
		/// <summary>只响应事件（单位死亡/建筑被毁/单位进入区域等），无事件不消耗求值。</summary>
		EventDriven = 2,
	}

	[GlobalClass]
	public partial class TriggerCondition : Resource
	{
		/// <summary>条件表达式，如 "units_in_region(2, 10, 10, 4) &gt;= 3 &amp;&amp; var_gold &lt; 500"。</summary>
		[Export] public string Expression { get; set; } = "";

		/// <summary>作者可读的描述（仅为 UI 显示，不参与求值）。</summary>
		[Export] public string Comment { get; set; } = "";

		public TriggerCondition() { }
		public TriggerCondition(string expression) { Expression = expression; }
	}

	/// <summary>一条"变量 ← 表达式"赋值。</summary>
	[GlobalClass]
	public partial class TriggerAssignment : Resource
	{
		[Export] public string Variable { get; set; } = "";

		/// <summary>值表达式；为空时按 Value 常量赋值。</summary>
		[Export] public string ValueExpression { get; set; } = "";

		/// <summary>常量值（ValueExpression 为空时使用）。</summary>
		[Export] public float Value { get; set; } = 0f;

		public TriggerAssignment() { }
		public TriggerAssignment(string variable, string valueExpression)
		{
			Variable = variable;
			ValueExpression = valueExpression;
		}
	}

	// ---------------------------------------------------------
	// 动作
	// ---------------------------------------------------------

	public enum TriggerActionKind
	{
		/// <summary>在指定格刷单位。Target 为空 = 无条件；否则仅对满足该表达式的玩家生效。</summary>
		SpawnUnit = 0,
		/// <summary>给玩家加/减资源。Resource 为 ResourceType 名，AmountExpression 为数量。</summary>
		AddResource = 1,
		/// <summary>给玩家加/减一种科技。</summary>
		GrantTech = 2,
		/// <summary>向所有玩家广播一条本地化消息（ActionIdExtra 走 Chat 通道）。</summary>
		ShowMessage = 3,
		/// <summary>设置一个地图变量（等价于 TriggerAssignment，保留为独立动作便于作者表达）。</summary>
		SetVariable = 4,
		/// <summary>启停另一个触发器（多阶段剧情的核心）。</summary>
		SetTriggerEnabled = 5,
		/// <summary>改变某个队伍与另一队伍的外交关系（0=敌对 1=中立 2=盟友）。</summary>
		SetDiplomacy = 6,
		/// <summary>播放表现层效果（镜头移动/全屏提示），走 SimEventQueue 回主线程。</summary>
		PlayFx = 7,
		/// <summary>胜利/失败判定。</summary>
		EndMatch = 8,
	}

	[GlobalClass]
	public partial class TriggerAction : Resource
	{
		[Export] public TriggerActionKind Kind { get; set; } = TriggerActionKind.ShowMessage;

		// --- 通用参数（按 Kind 解释，未用的留空）---
		/// <summary>动作主体/接收方表达式，如 "player(1)" 或 "owner(trigger_source)"。</summary>
		[Export] public string TargetExpression { get; set; } = "";

		/// <summary>数值表达式，如 "50" / "var_reward * 2"。</summary>
		[Export] public string AmountExpression { get; set; } = "";

		/// <summary>网格坐标 X（生成/区域类动作用）。</summary>
		[Export] public int GridX { get; set; }

		/// <summary>网格坐标 Y。</summary>
		[Export] public int GridY { get; set; }

		/// <summary>区域半径（格）；0 = 单格。</summary>
		[Export] public int RadiusTiles { get; set; }

		/// <summary>单位/建筑/科技/资源 ID，或本地化 key、触发器 ID，按 Kind 解释。</summary>
		[Export] public string Id { get; set; } = "";

		/// <summary>数量（生成个数、外交值等）。</summary>
		[Export] public int Count { get; set; } = 1;

		/// <summary>动作延迟（秒）；0 = 立即。用于编排演出节奏。</summary>
		[Export] public float DelaySeconds { get; set; }
	}

	// ---------------------------------------------------------
	// 触发器
	// ---------------------------------------------------------

	[GlobalClass]
	public partial class TriggerDefinition : Resource
	{
		/// <summary>唯一 ID，供 SetTriggerEnabled 与其他触发器引用。</summary>
		[Export] public string TriggerId { get; set; } = "";

		[Export] public string DisplayName { get; set; } = "";

		/// <summary>作者备注，编辑器显示用。</summary>
		[Export] public string Comment { get; set; } = "";

		/// <summary>初始是否启用（可用 SetTriggerEnabled 在运行期改变）。</summary>
		[Export] public bool EnabledAtStart { get; set; } = true;

		/// <summary>求值时机。</summary>
		[Export] public TriggerPhase Phase { get; set; } = TriggerPhase.EveryTick;

		/// <summary>Phase=Scheduled 时的首次触发 tick（20 tick = 1 秒）。</summary>
		[Export] public int StartTick { get; set; }

		/// <summary>Phase=Scheduled 时的重复间隔（tick）；0 = 只触发一次。</summary>
		[Export] public int IntervalTicks { get; set; }

		/// <summary>Expressions 全部为真才触发（AND 语义）。</summary>
		[Export] public Godot.Collections.Array<TriggerCondition> Conditions { get; set; } = new();

		/// <summary>触发时按顺序执行的赋值。</summary>
		[Export] public Godot.Collections.Array<TriggerAssignment> Assignments { get; set; } = new();

		/// <summary>触发时按顺序执行的动作。</summary>
		[Export] public Godot.Collections.Array<TriggerAction> Actions { get; set; } = new();

		/// <summary>是否可重复触发（false = 触发一次后自动禁用）。</summary>
		[Export] public bool Repeatable { get; set; }

		/// <summary>最小触发间隔（tick），防止 EveryTick 条件下同一瞬间连发。</summary>
		[Export] public int CooldownTicks { get; set; } = 20;

		/// <summary>条件严格模式：未定义变量报错而非当 0。</summary>
		[Export] public TriggerEvalMode EvalMode { get; set; } = TriggerEvalMode.Lenient;
	}

	/// <summary>地图初始变量（触发器共享的全局脚本变量）。</summary>
	[GlobalClass]
	public partial class TriggerVariable : Resource
	{
		[Export] public string Name { get; set; } = "";
		[Export] public float Value { get; set; }
	}
}
