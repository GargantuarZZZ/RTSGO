using System;
using System.Collections.Generic;
using System.Text;
using RTS.Simulation.Scripting;
using FP = FixMath.NET.Fix64;

namespace RTS.Tutorial
{
	// =========================================================
	// 教程数据模型 + 运行时
	//
	// 设计取舍：**教程不单独造一套逻辑**。
	//   - 完成条件复用触发器的表达式引擎（SimExpr）与 ITriggerWorld 查询，
	//     所以"教程里能判定的东西"与"触发器里能判定的东西"永远一致；
	//   - 教程进度是**纯确定性状态**（当前目标下标 + 是否完成），
	//     纳入世界哈希 —— 否则双端教程进度分叉不会被发现。
	//
	// 与触发器系统的分工：
	//   触发器 = 地图作者写的"世界事件"（刷兵、改资源、多阶段剧情）
	//   教程   = 面向玩家的**引导流程**（有序目标 + 提示文本 + 阶段过场）
	//   两者共用条件语言，但教程有"必须按顺序完成"的语义，触发器没有。
	// =========================================================

	/// <summary>一个教程目标。</summary>
	public sealed class TutorialObjective
	{
		/// <summary>稳定 ID（用于诊断与本地化 key：tutorial.&lt;tid&gt;.&lt;oid&gt;）。</summary>
		public string Id = "";

		/// <summary>目标标题（一句话说明要做什么）。</summary>
		public string Title = "";

		/// <summary>详细说明（为什么这么做 / 具体怎么操作）。</summary>
		public string Detail = "";

		/// <summary>
		/// 本地化后的显示文案（由教程运行时按 key 查表填入）。
		///
		/// 为什么不在定义里直接写 key：`TutorialDefinitions` 被无头测试项目
		/// 源文件包含编译，**不能引用 Godot 的 Localization**。
		/// 所以定义里保留中文原文当回退，查表在运行时做，结果放这里。
		/// 查不到时这里等于 Title/Detail，行为与以前一致。
		/// </summary>
		public string LocalizedTitle = "";
		public string LocalizedDetail = "";
		public string LocalizedCompleteText = "";

		/// <summary>取最终用于显示的标题（本地化优先）。</summary>
		public string DisplayTitle => string.IsNullOrEmpty(LocalizedTitle) ? Title : LocalizedTitle;
		public string DisplayDetail => string.IsNullOrEmpty(LocalizedDetail) ? Detail : LocalizedDetail;
		public string DisplayCompleteText =>
			string.IsNullOrEmpty(LocalizedCompleteText) ? CompleteText : LocalizedCompleteText;

		/// <summary>完成条件表达式；为空表示"靠触发器/代码手动推进"。</summary>
		public string CompleteWhen = "";

		/// <summary>是否显示进度（形如 "3 / 5"）。配合 ProgressExpression 使用。</summary>
		public string ProgressExpression = "";

		/// <summary>完成时显示的提示（可为空）。</summary>
		public string CompleteText = "";

		/// <summary>目标焦点位置（格）；用于 UI 定位/高亮，-1 表示无焦点。</summary>
		public int FocusGridX = -1;
		public int FocusGridY = -1;

		/// <summary>建议玩家把视角移到这里（新手常常找不到自己的单位在哪）。</summary>
		public bool MoveCameraOnStart;
	}

	/// <summary>
	/// 教程分层：基础（通用操作）→ 进阶（阵营机制/单位/科技）。
	/// 面板按这个字段分组，玩家先学操作再学阵营。
	/// </summary>
	public enum TutorialTier
	{
		/// <summary>基础：移动、采集、建造、生产、科技、攻击等所有阵营通用的操作。</summary>
		Basics,
		/// <summary>进阶：单个阵营的单位、机制与科技树。</summary>
		Advanced,
	}

	/// <summary>
	/// 理论页：**只读讲解**，没有完成条件，不参与教程进度。
	///
	/// 为什么单独建模而不是塞进 Objectives：
	///   目标(Objectives)是"必须按顺序做完的事"，理论页是"可以随便翻的资料"。
	///   两者的推进语义完全不同——理论页不该阻塞目标，也不该计入完成度。
	///
	/// 内容由 TutorialCodex 从 ConfigDatabase 实时生成，
	/// 所以"图鉴里写的数值"永远等于"游戏里真正用的数值"。
	/// </summary>
	public sealed class TutorialPage
	{
		/// <summary>分组标题（如"单位档案""科技树""核心机制"）。</summary>
		public string Section = "";

		/// <summary>页标题（如"步枪兵 RifleMan"）。</summary>
		public string Title = "";

		/// <summary>页正文，一行一条（UI 逐行渲染）。</summary>
		public readonly List<string> Lines = new();
	}

	/// <summary>一套教程（基础一整套流程，或某个阵营的进阶流程）。</summary>
	public sealed class TutorialDefinition
	{
		public string Id = "";
		public string RaceId = "";
		public string DisplayName = "";
		public string Intro = "";

		// ---- 本地化结果（运行时查表填入；空则回退上面的原文）----
		public string LocalizedDisplayName = "";
		public string LocalizedIntro = "";
		public string DisplayNameResolved =>
			string.IsNullOrEmpty(LocalizedDisplayName) ? DisplayName : LocalizedDisplayName;
		public string IntroResolved =>
			string.IsNullOrEmpty(LocalizedIntro) ? Intro : LocalizedIntro;

		/// <summary>
		/// 翻译函数：key → 文案；找不到返回 null（由调用方决定回退策略）。
		///
		/// **必须由游戏侧注入**，不能在这里直接调 Godot 的 Localization：
		/// 本文件被 `Tools/SimulationDeterminismTest` 源文件包含编译，
		/// 那里没有 Godot 程序集，直接引用会编译不过。
		/// </summary>
		public static Func<string, string> Translator;

		/// <summary>把当前语言的文案查表填进各字段（找不到就保持原文）。</summary>
		public void ApplyLocalization()
		{
			if (Translator == null) return;
			string tid = $"{Tier}_{Id}";

			LocalizedDisplayName = Translator($"tutorial.{tid}.name") ?? "";
			LocalizedIntro = Translator($"tutorial.{tid}.intro") ?? "";

			foreach (var o in Objectives)
			{
				if (o == null) continue;
				string b = $"tutorial.{tid}.{o.Id}";
				o.LocalizedTitle = Translator(b + ".title") ?? "";
				o.LocalizedDetail = Translator(b + ".detail") ?? "";
				o.LocalizedCompleteText = Translator(b + ".done") ?? "";
			}
		}

		/// <summary>分层（面板分组用）。默认进阶，保持既有教程的行为不变。</summary>
		public TutorialTier Tier = TutorialTier.Advanced;

		/// <summary>教程使用的地图 ID（res://Maps 里的地图，或内置 __tutorial）。</summary>
		public string MapId = "Classic";

		/// <summary>玩家使用的队伍号。</summary>
		public int PlayerTeam = 1;

		/// <summary>有序目标列表（必须按顺序完成的实操步骤）。</summary>
		public readonly List<TutorialObjective> Objectives = new();

		/// <summary>
		/// 理论页（可随时翻阅，不参与进度）。
		/// 进阶教程靠它承载"单位介绍 / 机制详解 / 科技树"。
		/// </summary>
		public readonly List<TutorialPage> Pages = new();

		/// <summary>教程开始时额外刷出的实体（教学用敌人/资源）。</summary>
		public readonly List<TutorialSpawn> ExtraSpawns = new();

		public int ObjectiveCount => Objectives.Count;
		public int PageCount => Pages.Count;

		/// <summary>理论页按 Section 归组后的顺序（首次出现顺序 = 展示顺序）。</summary>
		public List<string> PageSections()
		{
			var sections = new List<string>();
			foreach (var p in Pages)
			{
				if (p == null || string.IsNullOrEmpty(p.Section)) continue;
				if (!sections.Contains(p.Section)) sections.Add(p.Section);
			}
			return sections;
		}

		/// <summary>某个分组下的全部理论页（保持声明顺序）。</summary>
		public List<TutorialPage> PagesIn(string section)
		{
			var list = new List<TutorialPage>();
			foreach (var p in Pages)
			{
				if (p != null && p.Section == section) list.Add(p);
			}
			return list;
		}
	}

	/// <summary>教程额外刷出的实体（不改地图数据，只影响这一局）。</summary>
	public sealed class TutorialSpawn
	{
		public string EntityId = "";
		/// <summary>归属队伍；-1 = 中立不可攻击，-2 = 中立可攻击。</summary>
		public int TeamId = -1;
		public int GridX;
		public int GridY;
		public int Count = 1;
	}

	/// <summary>教程运行状态快照（UI 只读这个，不直接碰运行时内部）。</summary>
	public readonly struct TutorialSnapshot
	{
		public readonly bool Active;
		public readonly string TutorialId;
		public readonly string TutorialName;
		public readonly int ObjectiveIndex;
		public readonly int ObjectiveCount;
		public readonly string Title;
		public readonly string Detail;
		public readonly string ProgressText;
		public readonly string JustCompletedText;
		public readonly int FocusGridX;
		public readonly int FocusGridY;
		public readonly bool HideHud;

		public TutorialSnapshot(bool active, string tutorialId, string tutorialName,
			int index, int count, string title, string detail, string progress,
			string justCompleted, int fx, int fy, bool hideHud)
		{
			Active = active;
			TutorialId = tutorialId;
			TutorialName = tutorialName;
			ObjectiveIndex = index;
			ObjectiveCount = count;
			Title = title;
			Detail = detail;
			ProgressText = progress;
			JustCompletedText = justCompleted;
			FocusGridX = fx;
			FocusGridY = fy;
			HideHud = hideHud;
		}

		public static readonly TutorialSnapshot Inactive =
			new(false, "", "", 0, 0, "", "", "", "", -1, -1, false);
	}

	/// <summary>
	/// 教程运行时：持有当前目标下标与完成状态，每个模拟 tick 检查一次完成条件。
	///
	/// 线程约定：Evaluate 在**模拟线程**被调用，因此只读 SimWorld / PlayerData，
	/// 不碰任何 Godot 节点。UI 通过 GetSnapshot() 读快照（主线程）。
	/// </summary>
	public sealed class TutorialRuntime
	{
		private TutorialDefinition _def;

		/// <summary>完成提示保留的 tick 数（20Hz，100 tick = 5 秒）。</summary>
		private const int CompleteBannerTicks = 100;

		public bool Active { get; private set; }
		public int ObjectiveIndex { get; private set; }
		public int CompletedCount { get; private set; }

		/// <summary>最近一次完成提示 + 它到期的 tick。</summary>
		private string _banner = "";
		private int _bannerUntilTick;

		/// <summary>上一个已完成目标的 ID（用于日志去重）。</summary>
		private string _lastLoggedObjective = "";

		public TutorialDefinition Definition => _def;

		public void Start(TutorialDefinition def)
		{
			_def = def;
			Active = def != null && def.ObjectiveCount > 0;
			ObjectiveIndex = 0;
			CompletedCount = 0;
			_banner = "";
			_bannerUntilTick = 0;
			_lastLoggedObjective = "";
		}

		public void Stop()
		{
			Active = false;
			_def = null;
			ObjectiveIndex = 0;
			CompletedCount = 0;
			_banner = "";
			_bannerUntilTick = 0;
			_lastLoggedObjective = "";
		}

		/// <summary>手动推进（条件为空的目标，或触发器/代码驱动的阶段）。</summary>
		public void AdvanceObjective(int tick)
		{
			if (!Active || _def == null) return;

			var obj = CurrentObjective;
			if (obj != null && !string.IsNullOrEmpty(obj.CompleteText))
			{
				_banner = obj.CompleteText;
				_bannerUntilTick = tick + CompleteBannerTicks;
			}

			ObjectiveIndex++;
			CompletedCount++;

			if (ObjectiveIndex >= _def.ObjectiveCount)
			{
				Active = false;
				_banner = "教程完成！";
				_bannerUntilTick = tick + CompleteBannerTicks * 2;
			}
		}

		public TutorialObjective CurrentObjective =>
			_def != null && ObjectiveIndex >= 0 && ObjectiveIndex < _def.ObjectiveCount
				? _def.Objectives[ObjectiveIndex]
				: null;

		/// <summary>
		/// 每个 tick 检查当前目标是否达成。返回本 tick 是否推进了目标。
		/// </summary>
		public bool Evaluate(ITriggerWorld world, ISimExprHost host, int tick)
		{
			if (!Active || _def == null || world == null || host == null) return false;

			var obj = CurrentObjective;
			if (obj == null) { Active = false; return false; }

			// 条件为空的目标不会被自动推进（由 AdvanceObjective 手动控制）
			if (string.IsNullOrWhiteSpace(obj.CompleteWhen)) return false;

			bool done;
			try
			{
				done = SimExpr.Evaluate(obj.CompleteWhen, host).AsBool();
			}
			catch (SimExprException)
			{
				// 教程条件写错不该把整局游戏搞崩：当作未完成，并只在首次记一次
				if (_lastLoggedObjective != obj.Id)
				{
					_lastLoggedObjective = obj.Id;
					world.Log($"[Tutorial] 目标 '{obj.Id}' 条件求值失败：{obj.CompleteWhen}");
				}
				return false;
			}

			if (!done) return false;

			AdvanceObjective(tick);
			return true;
		}

		/// <summary>取"进度"文本（如 "3 / 5"）；没有进度表达式时返回空串。</summary>
		public string BuildProgressText(ISimExprHost host)
		{
			var obj = CurrentObjective;
			if (obj == null || string.IsNullOrWhiteSpace(obj.ProgressExpression) || host == null)
				return "";

			try
			{
				var v = SimExpr.Evaluate(obj.ProgressExpression, host);
				return SimExprValue.FormatNumber(v.AsNumber());
			}
			catch (SimExprException)
			{
				return "";
			}
		}

		public TutorialSnapshot GetSnapshot(ISimExprHost host, int tick)
		{
			if (_def == null) return TutorialSnapshot.Inactive;

			var obj = CurrentObjective;
			string banner = tick < _bannerUntilTick ? _banner : "";

			return new TutorialSnapshot(
				Active,
				_def.Id,
				_def.DisplayNameResolved,
				ObjectiveIndex,
				_def.ObjectiveCount,
				obj?.DisplayTitle ?? (Active ? "" : "教程完成"),
				obj?.DisplayDetail ?? "",
				BuildProgressText(host),
				banner,
				obj?.FocusGridX ?? -1,
				obj?.FocusGridY ?? -1,
				false);
		}

		/// <summary>
		/// 确定性哈希：教程进度必须两端一致，否则"一个客户端已经进入下一目标、
		/// 另一个还在上一目标"这种分叉不会被脱步检测发现。
		/// </summary>
		public long GetStateHash()
		{
			long hash = 1469598103934665603L;
			Mix(ref hash, Active ? 1 : 0);
			Mix(ref hash, ObjectiveIndex);
			Mix(ref hash, CompletedCount);
			if (_def != null)
			{
				foreach (char c in _def.Id) Mix(ref hash, c);
			}
			return hash;
		}

		private static void Mix(ref long hash, long value)
		{
			unchecked
			{
				hash ^= value;
				hash *= 1099511628211L;
			}
		}

		/// <summary>诊断用：把整套教程渲染成可读文本（编辑器/控制台预览）。</summary>
		public static string Describe(TutorialDefinition def)
		{
			if (def == null) return "(null)";

			var sb = new StringBuilder();
			sb.Append($"{def.DisplayName} [{def.Id}]  层级={def.Tier}  种族={def.RaceId}  地图={def.MapId}  " +
				$"目标数={def.ObjectiveCount}  理论页={def.PageCount}\n");
			if (!string.IsNullOrEmpty(def.Intro)) sb.Append($"  简介：{def.Intro}\n");

			for (int i = 0; i < def.Objectives.Count; i++)
			{
				var o = def.Objectives[i];
				sb.Append($"  {i + 1}. {o.Title}\n");
				if (!string.IsNullOrEmpty(o.Detail)) sb.Append($"     {o.Detail}\n");
				if (!string.IsNullOrEmpty(o.CompleteWhen)) sb.Append($"     完成条件：{o.CompleteWhen}\n");
			}

			foreach (string section in def.PageSections())
			{
				sb.Append($"  【{section}】\n");
				foreach (var p in def.PagesIn(section))
				{
					sb.Append($"     · {p.Title}\n");
					foreach (string line in p.Lines)
						sb.Append($"         {line}\n");
				}
			}

			if (def.ExtraSpawns.Count > 0)
			{
				sb.Append($"  额外实体：{def.ExtraSpawns.Count} 项\n");
				foreach (var s in def.ExtraSpawns)
					sb.Append($"     {s.EntityId} x{s.Count} @({s.GridX},{s.GridY}) team={s.TeamId}\n");
			}

			return sb.ToString();
		}
	}
}
