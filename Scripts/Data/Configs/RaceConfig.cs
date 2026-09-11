using Godot;
using System.Collections.Generic;

namespace RTS.Data.Configs
{
	/// <summary>
	/// 种族配置。
	///
	/// 用途：
	/// - 定义种族基础信息
	/// - 定义六维定位
	/// - 定义初始资源
	/// - 定义开局实体
	/// - 定义该种族可用单位 / 建筑 / 科技
	///
	/// 数据驱动原则：
	/// - 不直接引用 UnitConfig / StructureConfig / TechConfig。
	/// - 使用字符串 ID。
	/// - 运行时通过 ConfigDatabase 查询。
	/// </summary>
	[GlobalClass]
	public partial class RaceConfig : Resource
	{
		// =========================================================
		// 基础识别
		// =========================================================

		[ExportGroup("Identity")]
		[Export] public string RaceId { get; set; } = "";
		[Export] public string DisplayName { get; set; } = "";
		[Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";

		[Export] public Texture2D Icon { get; set; }

		// =========================================================
		// 六维定位
		// =========================================================

		[ExportGroup("Six Dimensions")]

		[Export(PropertyHint.Range, "0,5,1")]
		public int Offense { get; set; } = 3;

		[Export(PropertyHint.Range, "0,5,1")]
		public int Defense { get; set; } = 3;

		[Export(PropertyHint.Range, "0,5,1")]
		public int Economy { get; set; } = 3;

		[Export(PropertyHint.Range, "0,5,1")]
		public int Support { get; set; } = 3;

		[Export(PropertyHint.Range, "0,5,1")]
		public int Technology { get; set; } = 3;

		[Export(PropertyHint.Range, "1,5,1")]
		public int Difficulty { get; set; } = 2;

		// 简短定位，例如：
		// "防守运营"
		// "前期进攻"
		// "团队辅助"
		[Export] public string RoleSummary { get; set; } = "";

		// 明显弱点，用于 UI 提示。
		[Export(PropertyHint.MultilineText)] public string WeaknessDescription { get; set; } = "";

		// =========================================================
		// 初始资源
		// =========================================================

		[ExportGroup("Starting Resources")]
		[Export] public Godot.Collections.Array<ResourceStartConfig> StartingResources { get; set; } = new();

		// =========================================================
		// 开局生成
		// =========================================================

		[ExportGroup("Starting Entities")]
		[Export] public Godot.Collections.Array<StartingEntityConfig> StartingEntities { get; set; } = new();

		// =========================================================
		// 纳米虫地毯技能（面板技能数值全部走配置表）
		// =========================================================

		[ExportGroup("Nano Carpet Skill")]

		[Export(PropertyHint.Range, "0,999999,1")]
		public float CarpetSpreadCost { get; set; } = 20f;

		[Export(PropertyHint.Range, "0,999,0.1")]
		public float CarpetSpreadCooldownSeconds { get; set; } = 10f;

		[Export(PropertyHint.Range, "1,999,1")]
		public float CarpetSpreadRadiusTiles { get; set; } = 10f;

		[Export(PropertyHint.Range, "1,99,1")]
		public int CarpetSpreadMaxCharges { get; set; } = 3;

		[Export(PropertyHint.Range, "0.1,60,0.1")]
		public float CarpetSpreadDurationSeconds { get; set; } = 1f;

		// 视野模式：0=标准（单位/建筑视野），1=地毯视野（纳米：只有地毯与巨兽提供视野）
		[Export(PropertyHint.Range, "0,1,1")]
		public int VisionMode { get; set; } = 0;

		[Export(PropertyHint.Range, "1,9999,1")]
		public int CarpetIncomeCellsPerResource { get; set; } = 20;


		// =========================================================
		// 可用内容
		// =========================================================

		[ExportGroup("Available Content")]

		[Export] public Godot.Collections.Array<string> AvailableUnitIds { get; set; } = new();

		[Export] public Godot.Collections.Array<string> AvailableStructureIds { get; set; } = new();

		[Export] public Godot.Collections.Array<string> AvailableTechIds { get; set; } = new();

		[Export] public Godot.Collections.Array<string> AvailableBuffIds { get; set; } = new();

		// UI 顶部显示哪些资源（例如联盟：矿物/瓦斯/能量）；留空则取初始资源类型
		[Export] public Godot.Collections.Array<ResourceType> VisibleResourceTypes { get; set; } = new();

		// 能量上限（0 = 无上限），联盟轨道控制中心用
		[Export(PropertyHint.Range, "0,999999,1")]
		public float MaxEnergy { get; set; } = 0f;

		// 人口固定上限（0 = 不用固定上限，按建筑供给算）
		[Export(PropertyHint.Range, "0,9999,1")]
		public int MaxSupplyCap { get; set; } = 0;

		// 人口上限模式：0=按供给和（联盟） 1=供给封顶（恶魔） 2=上限+供给-占用（多足）
		[Export(PropertyHint.Range, "0,2,1")]
		public int SupplyCapMode { get; set; } = 0;

		// =========================================================
		// 默认 UI / AI 推荐
		// =========================================================

		[ExportGroup("Recommendation")]

		// 新手推荐建筑顺序，例如：
		// "union_command_center", "union_barracks", "union_generator"
		[Export] public Godot.Collections.Array<string> SuggestedBuildOrderIds { get; set; } = new();

		// 推荐核心单位。
		[Export] public Godot.Collections.Array<string> CoreUnitIds { get; set; } = new();

		// 推荐搭配种族 ID。
		[Export] public Godot.Collections.Array<string> RecommendedAllyRaceIds { get; set; } = new();

		// =========================================================
		// 分族 AI（数据驱动：AI 按这张表执行宏运营，种族机制差异在这里体现）
		// =========================================================

		[ExportGroup("Race AI")]

		// 建筑建造顺序（按序检查，缺什么补什么；纳米为空闲建造路径）
		[Export] public Godot.Collections.Array<string> AiBuildOrderIds { get; set; } = new();

		// 建造目标数量：键=建筑 ID，值=目标座数（缺省 1；多足蓝图按产出单位数量配置）
		[Export] public Godot.Collections.Dictionary<string, int> AiBuildTargetCounts { get; set; } = new();

		// 科技研究顺序（按序检查，满足前置即可研究）
		[Export] public Godot.Collections.Array<string> AiTechOrderIds { get; set; } = new();

		// 兵种混编（训练/生产轮换顺序；蓝图/自动生产种族可留空）
		[Export] public Godot.Collections.Array<string> AiUnitMixIds { get; set; } = new();

		// 经济单位目标数量（工人/工程兵/建造者；0 = 不需要经济单位）
		[Export(PropertyHint.Range, "0,99,1")]
		public int AiWorkerTarget { get; set; } = 6;

		// 经济单位 ID（工人/工程兵/建造者；纳米无工人留空）
		[Export] public string AiWorkerUnitId { get; set; } = "";

		// 分矿基地（中立塔资源点拆塔后建造的建筑 ID，按序各拍一座）
		[Export] public Godot.Collections.Array<string> AiExpansionBaseIds { get; set; } = new();

		// 进攻兵力阈值：己方存活军事单位达到该数量后才主动进攻
		[Export(PropertyHint.Range, "0,999,1")]
		public int AiArmyTarget { get; set; } = 12;

		// =========================================================
		// 工具方法
		// =========================================================

		public bool HasUnit(string unitId)
		{
			return ContainsId(AvailableUnitIds, unitId);
		}

		public bool HasStructure(string structureId)
		{
			return ContainsId(AvailableStructureIds, structureId);
		}

		public bool HasTech(string techId)
		{
			return ContainsId(AvailableTechIds, techId);
		}

		public Dictionary<ResourceType, int> GetStartingResourceDictionary()
		{
			Dictionary<ResourceType, int> result = new();

			foreach (ResourceStartConfig item in StartingResources)
			{
				if (item == null)
					continue;

				if (!result.ContainsKey(item.Type))
					result[item.Type] = 0;

				result[item.Type] += item.Amount;
			}

			return result;
		}

		public bool IsValidConfig()
		{
			if (string.IsNullOrEmpty(RaceId))
				return false;

			if (string.IsNullOrEmpty(DisplayName))
				return false;

			return true;
		}

		private bool ContainsId(Godot.Collections.Array<string> array, string id)
		{
			if (string.IsNullOrEmpty(id))
				return false;

			foreach (string item in array)
			{
				if (item == id)
					return true;
			}

			return false;
		}
	}
}
