using System;
using System.Collections.Generic;
using Godot;
using RTS.Data.Maps;

namespace RTS.Tutorial
{
	// =========================================================
	// 三套内置教程：联盟 / 恶魔 / 多足机械
	//
	// 设计原则（"简单、短流程"）：
	//   1. **4~5 个目标**，每个目标只教一件事，全部能在 1~2 分钟内完成；
	//   2. 教学点只挑该阵营**独有**的机制（资料来自 设计/设计数据表）：
	//        联盟 = 工人采集/建造/修理、兵营出兵、人口
	//        恶魔 = 立场、无限自动爆兵、禁疗与寿命
	//        多足 = 游牧（无建筑）、控制圈、蓝图生产
	//      通用操作（框选、右键移动）不单独设目标，避免拖长流程；
	//   3. 完成条件全部用触发器表达式语言写，与地图触发器同源；
	//   4. 教程自带一张**极简地图**（见 TutorialMap），不依赖 res://Maps 里有没有地图，
	//      保证"点教程一定能开始"。
	//
	// 数值与机制依据：设计/设计数据表 (version 1).xlsb.xlsx 的
	// 「概览 / 联盟 / 恶魔 / 多足」四张表；具体 ID 以 Data/Configs 为准。
	// =========================================================

	public static class TutorialRegistry
	{
		/// <summary>教程自带的极简地图 ID（不写入 res://Maps，运行时现造）。</summary>
		public const string TutorialMapId = "__tutorial";

		private static List<TutorialDefinition> _all;

		/// <summary>
		/// 全部教程。
		///
		/// 依赖说明（重要）：本文件**只依赖 RtsMapData / MapDataOps**，
		/// 刻意不碰 ConfigDatabase —— 因为 TutorialDefinitions.cs 会被无头测试工程
		/// 编译进去（测试要验证教程地图能通过校验器），而测试工程没有 Godot 配置层。
		/// 图鉴（单位/建筑/科技资料页）由 TutorialCodex.HydrateAll 在 UI 侧单独挂上。
		/// </summary>
		public static IReadOnlyList<TutorialDefinition> All => _all ??= Build();

		public static TutorialDefinition Get(string id)
		{
			foreach (var t in All)
				if (t.Id == id) return t;
			return null;
		}

		private static List<TutorialDefinition> Build()
		{
			return new List<TutorialDefinition>
			{
				BuildBasics(),
				BuildUnion(),
				BuildTerran(),
				BuildDemon(),
				BuildNano(),
				BuildPlant(),
				BuildCave(),
				BuildWanderer(),
			};
		}

		/// <summary>基础教程（通用操作）——面板里单独一组，建议新手先做这个。</summary>
		public static TutorialDefinition Basics => All[0];

		/// <summary>进阶教程（各阵营机制/单位/科技）。</summary>
		public static List<TutorialDefinition> Advanced()
		{
			var list = new List<TutorialDefinition>();
			foreach (var t in All)
				if (t.Tier == TutorialTier.Advanced) list.Add(t);
			return list;
		}

		/// <summary>基础教程用的阵营：联盟（标准运营，最适合教通用操作）。</summary>
		public const string BasicsRaceId = "Union";

		// =========================================================
		// 零、基础教程（所有阵营通用的操作）
		//
		// 目的：把"这个游戏怎么玩"讲完——移动、采集、建造、生产、科技、战斗、人口。
		// 阵营专属机制**刻意不在这里讲**，那是进阶教程的事；
		// 这里只用联盟（标准运营）当载体，因为它的操作用法最典型。
		//
		// 每个目标都配一条"玩家真的按了这个操作"的判定
		// （move_orders / build_orders / train_orders / research_orders），
		// 否则"单位本来就在动"会让目标开局就自动完成。
		// =========================================================

		private static TutorialDefinition BuildBasics()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_basics",
				RaceId = BasicsRaceId,
				DisplayName = "基础操作",
				Tier = TutorialTier.Basics,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "从零学会这个游戏怎么玩：选兵、移动、采集、造建筑、出兵、点科技、打仗。",
			};

			// 开局：指挥中心 + 6 个 SCV（比标准多 2 个，方便同时教"采集"和"建造"）
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "CommandCenter", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			for (int i = 0; i < 6; i++)
				t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "SCV", TeamId = 1, GridX = PlayerBaseX - 4 + i, GridY = PlayerBaseY + 3 });

			// 资源给足：基础教程不该卡在"等钱"上
			// （资源由 Game 通过 StartingResources 给不了，这里用触发器式额外资源不可行，
			//   改为多给矿 + 教程目标不要求攒钱，见各目标的阈值设计）

			t.Objectives.Add(new TutorialObjective
			{
				Id = "select_move",
				Title = "第 1 步：选中单位并移动",
				Detail = "左键框选（按住拖出一个框）几个 SCV，然后在地面上右键——它们会走过去。\n" +
						 "右键点空地 = 移动；右键点敌人 = 攻击；右键点矿 = 采集。",
				CompleteWhen = "move_orders(player(1)) >= 1",
				ProgressExpression = "move_orders(player(1))",
				CompleteText = "这就是最基本的操作：左键选、右键指挥。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "camera",
				Title = "第 2 步：学会看全局",
				Detail = "滚轮缩放视角。移动视角有四种方式：方向键、鼠标推到屏幕边缘、按住中键拖拽、" +
						 "或点小地图跳转。\n" +
						 "★ 这一条不需要额外操作——看完点一下『下一步』继续即可。",
				// 镜头操作在主线程，模拟层看不到，无法直接判定。
				// 刻意把条件写成"已经下过任意指令"（第 1 步必然满足），
				// 让这一步纯粹当讲解页，**不会**卡住玩家。
				CompleteWhen = "any_orders(player(1)) >= 1",
				CompleteText = "视角看全了。接下来开始搞经济。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "harvest",
				Title = "第 3 步：采集资源",
				Detail = "选中 SCV，右键点金色的铁矿。SCV 会自动挖矿并送回指挥中心。\n" +
						 "金属是主资源，瓦斯（绿色气泉）用来出高级单位。",
				CompleteWhen = "resource(player(1), 'Metal') >= 300",
				ProgressExpression = "resource(player(1), 'Metal')",
				CompleteText = "资源会自动入库，不用手动收。",
				FocusGridX = 21,
				FocusGridY = 27,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "build_supply",
				Title = "第 4 步：造补给站（人口）",
				Detail = "人口满了就不能再出兵。选中一个 SCV，点『建造』选补给站，放在基地旁边空地上。\n" +
						 "建筑放下后是半透明的『蓝图』，SCV 会自动过去施工，血条满格才算造好。",
				CompleteWhen = "built('SupplyDepot', player(1)) >= 1",
				CompleteText = "补给站 +10 人口。人口不够就再补一个。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "build_barracks",
				Title = "第 5 步：造兵营",
				Detail = "同样的方法放下兵营（BB）。兵营是出兵的前提。",
				CompleteWhen = "built('BB', player(1)) >= 1",
				CompleteText = "兵营完工，可以出兵了。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "train",
				Title = "第 6 步：生产部队",
				Detail = "选中兵营，在右侧动作面板点『训练 步枪兵』。生产的兵会出现在兵营旁边的集结点。\n" +
						 "选中兵营后右键点地面，可以设置集结点（新兵自动往那儿走）。",
				CompleteWhen = "units('RifleMan', player(1)) >= 3",
				ProgressExpression = "units('RifleMan', player(1))",
				CompleteText = "出兵了。人口不够就先补补给站。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "tech",
				Title = "第 7 步：研究科技",
				Detail = "先造一个学院（Academy，需要兵营作为前置），然后选中学院研究一项科技。\n" +
						 "科技会永久强化你的部队——这是拉开差距的关键。",
				CompleteWhen = "has_tech(player(1), 'UnionTech_Stim')",
				CompleteText = "科技点起来了。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "attack",
				Title = "第 8 步：攻击敌人",
				Detail = "选中全部部队（框选，或按 F2 全选作战单位），右键东边那座中立塔。\n" +
						 "想边走边打就用攻击移动：按 A，再左键点目标位置。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "目标摧毁！基础操作你已经全会了。",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			// ---- 理论页：把"面板上没有但必须知道"的规则写下来 ----
			t.Pages.Add(new TutorialPage
			{
				Section = "基本规则",
				Title = "资源与人口",
				Lines =
				{
					"金属：主资源，SCV 采集铁矿获得。造建筑、出大部分兵都要它。",
					"瓦斯：高级资源，采集绿色气泉获得。高级单位与科技需要。",
					"人口：每个作战单位占人口。人口满了就不能再出兵。",
					"  指挥中心提供一部分人口，补给站每个 +10。",
					"  人口不看会直接卡死生产——补兵前先看人口条。",
				},
			});
			t.Pages.Add(new TutorialPage
			{
				Section = "基本规则",
				Title = "操作速查",
				// 注意：这里的按键必须与 `Scripts/Settings/InputActions.cs` 的默认键位一致。
				// 改键位表时**必须同步改这里** —— 曾经教程写着"按 A 全选作战单位"，
				// 而 A 实际是攻击移动，玩家照着按只会进攻击移动待命。
				// （设置菜单里改过的键位不会反映到教程文本，教程教的是默认键。）
				Lines =
				{
					"左键拖框：选中多个单位。",
					"右键空地：移动；右键敌人：攻击；右键矿：采集。",
					"A 然后左键点地：攻击移动（边走边自动打）。",
					"S：停止（原地待命，不再自动索敌）。",
					"H：驻守（停在原地但会还击）。",
					"Shift + 右键：排入指令队列（依次执行）。",
					"Ctrl + 数字：把当前选择存成编队；数字键：选回该编队。",
					"F2：全选作战单位；F3：全选工人；F1：选中空闲工人。",
					"方向键 / 鼠标贴边 / 按住中键拖拽：移动视角。",
					"滚轮：缩放视角；Home：视角回中。",
					"Ctrl + 中键：在地图上打信号（联机时队友能看到）。",
				},
			});
			t.Pages.Add(new TutorialPage
			{
				Section = "基本规则",
				Title = "建造与施工",
				Lines =
				{
					"选工人 → 点『建造』→ 选建筑 → 点地面放下。",
					"放下后是『蓝图』：半透明、不阻挡、自己人踩上去会让路。",
					"工人会自动过去施工；血条涨满才变成真正的建筑。",
					"只有完工的建筑能出兵/研究，蓝图不算。",
					"建筑能挡住敌方单位，也能被拆掉。",
				},
			});

			return t;
		}

		// =========================================================
		// 极简教程地图
		//
		// 刻意不用 MapGenerator：程序生成的地形每张都不一样，
		// 而教程的教学点（"往那边走""打掉那座塔"）依赖固定布局。
		// 这里直接手写一张 64×64 的空地 + 四周围墙 + 几个坐标点。
		// =========================================================

		public const int MapSize = 64;
		public const int PlayerBaseX = 14;
		public const int PlayerBaseY = 32;
		public const int EnemyTowerX = 46;
		public const int EnemyTowerY = 32;

		/// <summary>造教程用的地图（确定性：同一份代码永远给出同一张图）。</summary>
		public static RtsMapData BuildTutorialMap(string mapId = TutorialMapId)
		{
			var map = new RtsMapData
			{
				MapId = mapId,
				DisplayName = "教程场地",
				Author = "built-in",
				Description = "教程专用极简地图：空地 + 围墙 + 一个敌方目标。",
			};

			map.Resize(MapSize, MapSize);
			map.Fill(RtsMapData.SourceGrass);
			MapDataOps.StampBorder(map, 2);

			// 出生点：只给玩家一个（教程不需要对手出生点）
			map.SpawnPoints.Add(new MapSpawnPoint
			{
				TeamSlot = 1,
				GridX = PlayerBaseX,
				GridY = PlayerBaseY,
				Comment = "教程：玩家出生点",
			});

			// 玩家基地周围清出建造空间并解除禁建
			for (int x = PlayerBaseX - 6; x <= PlayerBaseX + 6; x++)
				for (int y = PlayerBaseY - 6; y <= PlayerBaseY + 6; y++)
					map.SetBuildBlocked(x, y, false);

			// 中期可选目标：一座中立可攻击的塔（拆塔练习）
			map.Entities.Add(new MapEntityPlacement
			{
				EntityId = "Tower",
				Owner = MapEntityOwner.HostileNeutral,
				GridX = EnemyTowerX,
				GridY = EnemyTowerY,
				Comment = "教程：拆塔目标",
			});

			// 玩家基地附近的资源（够教程用，不用跑很远）
			int[,] ores = { { 21, 27 }, { 21, 31 }, { 21, 35 }, { 8, 28 }, { 8, 36 } };
			for (int i = 0; i < ores.GetLength(0); i++)
			{
				map.Entities.Add(new MapEntityPlacement
				{
					EntityId = "IronOre",
					Owner = MapEntityOwner.Neutral,
					GridX = ores[i, 0],
					GridY = ores[i, 1],
					Comment = "教程：铁矿",
				});
			}

			map.Entities.Add(new MapEntityPlacement
			{
				EntityId = "GasSpring",
				Owner = MapEntityOwner.Neutral,
				GridX = 21,
				GridY = 39,
				Comment = "教程：气泉",
			});

			map.MaxPlayers = 2;
			map.RecommendedPlayers = 1;
			return map;
		}

		// =========================================================
		// 一、联盟（新手友好 · 标准运营）
		//
		// 教学点：SCV 采集 → 补工人 → 造兵营 → 出兵 → 拆塔
		// 依据：「联盟」表：初始 指挥中心*1 + SCV*4 + 金属200；
		//       金属/瓦斯由 SCV 采集后交付指挥中心；SCV 可建造与修理。
		// =========================================================

		private static TutorialDefinition BuildUnion()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_union",
				RaceId = "Union",
				DisplayName = "联盟 · 基础运营",
				Tier = TutorialTier.Advanced,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "你是联盟指挥官。先用 SCV 建立经济，再出兵拆掉敌方塔楼。",
			};


			// 初始：指挥中心 + 4 个 SCV（与设计表「联盟 · 初始」一致）
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "CommandCenter", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			for (int i = 0; i < 4; i++)
				t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "SCV", TeamId = 1, GridX = PlayerBaseX - 3 + i, GridY = PlayerBaseY + 2 });

			t.Objectives.Add(new TutorialObjective
			{
				Id = "gather",
				Title = "采集金属",
				Detail = "选中 SCV，右键点金矿让它去采集。金属会自动送回指挥中心。",
				CompleteWhen = "resource(player(1), 'Metal') >= 300",
				ProgressExpression = "resource(player(1), 'Metal')",
				CompleteText = "很好！金属是联盟的主要资源。",
				FocusGridX = 21,
				FocusGridY = 27,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "more_scv",
				Title = "训练 SCV",
				Detail = "选中指挥中心，在动作面板点『训练 SCV』，把工人补到 6 个。",
				CompleteWhen = "units('SCV', player(1)) >= 6",
				ProgressExpression = "units('SCV', player(1))",
				CompleteText = "工人越多，经济越快。注意人口上限——不够就造补给站。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "barracks",
				Title = "建造兵营",
				Detail = "选中 SCV，点建造按钮放下兵营（BB）。兵营是出兵的前提。",
				CompleteWhen = "built('BB', player(1)) >= 1",
				CompleteText = "兵营完工！现在可以出兵了。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "army",
				Title = "训练步枪兵",
				Detail = "选中兵营，训练 4 个步枪兵。",
				CompleteWhen = "units('RifleMan', player(1)) >= 4",
				ProgressExpression = "units('RifleMan', player(1))",
				CompleteText = "部队成形。步枪兵对地对空都能打。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "destroy",
				Title = "摧毁敌方塔楼",
				Detail = "选中全部部队，右键攻击东边的中立塔。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "目标摧毁！联盟教程完成。",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			return t;
		}

		// =========================================================
		// 二、恶魔（立场 · 无限爆兵 · 禁疗）
		//
		// 教学点：免资源自动爆兵 → 认识立场 → 体会禁疗 → 用自动兵拆塔
		// 依据：「恶魔」表：传送门免费自动生产；单位离开立场后逐渐失去生命
		//       （立场外时间消耗 ×3）；恶魔单位无法被任何方式治疗；人口上限 50。
		// =========================================================

		private static TutorialDefinition BuildDemon()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_demon",
				RaceId = "Demon",
				DisplayName = "恶魔 · 立场与爆兵",
				Tier = TutorialTier.Advanced,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "恶魔的主城每 15 秒免费送一个怨灵。但恶魔单位无法被治疗，离开立场还会加速衰亡。",
			};


			// 初始：地狱城 + 4 个恶魔工人
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "HellCity", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			for (int i = 0; i < 4; i++)
				t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "DemonWorker", TeamId = 1, GridX = PlayerBaseX - 3 + i, GridY = PlayerBaseY + 2 });

			// 大裂隙：免费自动爆兵的核心建筑
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "GreatRift", TeamId = 1, GridX = PlayerBaseX + 5, GridY = PlayerBaseY - 4 });

			t.Objectives.Add(new TutorialObjective
			{
				Id = "gather",
				Title = "用恶魔工人采集",
				Detail = "选中恶魔工人，右键金矿。恶魔一样要金属和瓦斯来盖建筑。",
				CompleteWhen = "resource(player(1), 'Metal') >= 300",
				ProgressExpression = "resource(player(1), 'Metal')",
				CompleteText = "资源到位。现在看看恶魔真正的优势——免费爆兵。",
				FocusGridX = 21,
				FocusGridY = 27,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "auto_army",
				Title = "等待自动爆兵",
				Detail = "地狱城与大裂隙会自动免费生产单位，不需要花资源和时间。等兵力涨到 6 个。",
				CompleteWhen = "units('DemonDog', player(1)) + units('DemonFlyer', player(1)) + units('HeavyTank', player(1)) >= 6",
				ProgressExpression = "units('DemonDog', player(1)) + units('DemonFlyer', player(1)) + units('HeavyTank', player(1))",
				CompleteText = "这就是恶魔的『无限爆兵』：不花资源，只管往前推。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "field",
				Title = "把部队带回立场内",
				Detail = "你所有恶魔建筑周围的地面就是『立场』（紫色范围）。单位在立场外寿命消耗 ×3，" +
						 "而且恶魔单位无法被治疗——远离基地就是慢性死亡。把部队拉回基地附近。",
				CompleteWhen = $"units_in_region(player(1), {PlayerBaseX}, {PlayerBaseY}, 14) >= 6",
				ProgressExpression = $"units_in_region(player(1), {PlayerBaseX}, {PlayerBaseY}, 14)",
				CompleteText = "记住这条：恶魔靠立场作战，不靠治疗。",
				FocusGridX = PlayerBaseX,
				FocusGridY = PlayerBaseY,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "destroy",
				Title = "拆掉敌方塔楼",
				Detail = "带着部队一路打到东边的中立塔，把它拆掉。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "恶魔教程完成！",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			return t;
		}

		// =========================================================
		// 二、泰伦（弹药 · 架设 · 阵地战）
		//
		// 教学点：工程兵建造 → 野战营出大兵 → 架设变强 → 军械所点科技 → 拆塔
		// 依据「泰伦」表与配置：所有作战单位带 MaxAmmo，必须在弹药范围（指挥堡垒 10 格 /
		// 前线营地 5 格 / 机场 5 格）内回弹；大兵带 CanDeploy（停下 1 秒架设，
		// 生命 ×2 或射程/AOE 提升）。上手难度 3/5。
		// =========================================================

		private static TutorialDefinition BuildTerran()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_terran",
				RaceId = "Terran",
				DisplayName = "泰伦 · 弹药与架设",
				Tier = TutorialTier.Advanced,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "泰伦是阵地战阵营：单位有弹药，离开补给范围就打不动；停下来架设才会变强。",
			};


			// 初始：指挥堡垒 + 4 个工程兵（与设计表「泰伦 · 初始」一致）
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "FortressCore", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			for (int i = 0; i < 4; i++)
				t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "Engineer", TeamId = 1, GridX = PlayerBaseX - 3 + i, GridY = PlayerBaseY + 3 });

			t.Objectives.Add(new TutorialObjective
			{
				Id = "gather",
				Title = "工程兵采集",
				Detail = "选中工程兵，右键点铁矿。泰伦的工程兵既能采集也能建造。",
				CompleteWhen = "resource(player(1), 'Metal') >= 300",
				ProgressExpression = "resource(player(1), 'Metal')",
				CompleteText = "资源到位。泰伦的资源和别的族一样，但花法不同——要预留补弹药的建筑。",
				FocusGridX = 21,
				FocusGridY = 27,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "fieldcamp",
				Title = "造野战营",
				Detail = "选中工程兵，点『建造』放下野战营（FieldCamp）。它是泰伦的基础兵营，出大兵与重装步兵。",
				CompleteWhen = "built('FieldCamp', player(1)) >= 1",
				CompleteText = "野战营完工。泰伦的兵都从这里出来。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "train",
				Title = "训练大兵",
				Detail = "选中野战营，训练 3 个大兵。",
				CompleteWhen = "units('Marine', player(1)) >= 3",
				ProgressExpression = "units('Marine', player(1))",
				CompleteText = "大兵是泰伦的主力，能架设。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "deploy",
				Title = "架设（泰伦招牌）",
				Detail = "选大兵后点『架设』（或让它停下 1 秒自动架设）。架设后不能移动，但活得更久、打得更远。\n" +
						 "★ 记住这条：泰伦架设后不能动，被贴脸或要撤退时先点『收起』。",
				CompleteWhen = "units('Marine', player(1)) >= 3 && move_orders(player(1)) >= 1",
				CompleteText = "架设/收起是这个阵营最核心的操作。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "armory",
				Title = "造军械所并点科技",
				Detail = "继续用工程兵造军械所（Armory，需要先有野战营提供 T1），然后研究『深埋工事』或『弹药回收』。\n" +
						 "★ 弹药回收能让击杀返还弹药——这是泰伦敢往前的关键科技。",
				CompleteWhen = "has_tech(player(1), 'TerranTech_Entrench') || has_tech(player(1), 'TerranTech_AmmoRecovery')",
				CompleteText = "科技点起来了。泰伦的科技基本都在后勤与防御线上。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "destroy",
				Title = "拆掉敌方塔楼",
				Detail = "带着大兵打到东边那座中立塔。记得**慢慢推**：一路把前线营地拍过去，\n" +
						 "让弹药范围跟着部队走，而不是一口气冲到底。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "泰伦教程完成！你已经会打阵地战了。",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			return t;
		}

		// =========================================================
		// 三、纳米虫（菌毯经济 · 面板建造 · 塔防）
		//
		// 教学点：菌毯就是经济 → 面板凭空造塔（不需要工人）→ 唯一的机动单位纳米巨兽 → 拆塔
		// 依据：Nano 全部建筑 RequiresCreep + AutoBuild（拍下自己长）；
		//       菌毯格数换纳米机器人（CarpetIncomeCellsPerResource，Nano.tres = 500）；
		//       只有 NanoBehemoth 一个可动单位，右键默认攻击移动。
		// =========================================================

		private static TutorialDefinition BuildNano()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_nano",
				RaceId = "Nano",
				DisplayName = "纳米虫 · 菌毯与塔防",
				Tier = TutorialTier.Advanced,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "纳米虫只有一种机动单位。它的经济和建造都长在菌毯上——铺地毯就是搞经济和造基地。",
			};


			// 初始：纳米巨兽 + 采集器（与设计表一致）
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "NanoBehemoth", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "NanoHarvester", TeamId = 1, GridX = PlayerBaseX + 4, GridY = PlayerBaseY });

			t.Objectives.Add(new TutorialObjective
			{
				Id = "spread",
				Title = "铺开菌毯",
				Detail = "纳米虫没有工人。选中纳米巨兽，用『扩散菌毯』技能往空地上铺菌毯。\n" +
						 "★ 菌毯铺到的格数就是你的收入与建造范围，铺得越广越强。",
				CompleteWhen = "resource(player(1), 'NanoBots') >= 150",
				ProgressExpression = "resource(player(1), 'NanoBots')",
				CompleteText = "地毯经济启动了：菌毯格数自动换纳米机器人。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "turret",
				Title = "在菌毯上造炮塔",
				Detail = "直接点建造面板放一座机炮（NanoTurret）——**纳米建筑不需要工人施工**，\n" +
						 "拍下后自己会长好。注意必须放在菌毯上，否则放不下去。",
				CompleteWhen = "built('NanoTurret', player(1)) >= 1",
				CompleteText = "自动建造是纳米虫的核心：一个单位也能铺出整片防线。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "behemoth",
				Title = "操纵纳米巨兽",
				Detail = "纳米巨兽是你唯一的机动单位。右键点地面——它是**攻击移动**，会边走边自动打敌人。\n" +
						 "带着它往东边清一圈，把路上的敌人清掉。",
				CompleteWhen = "move_orders(player(1)) >= 1",
				CompleteText = "记住：纳米只有这一个能动的单位，别把它当消耗品。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "tech",
				Title = "研究一项科技（注意二选一）",
				Detail = "选中采集器研究科技。★ 纳米科技是**成对 A/B 二选一**的：\n" +
						 "选了就换不了，必须按对手阵容提前判断。这里随便点一个试试。",
				CompleteWhen = "research_orders(player(1)) >= 1",
				ProgressExpression = "research_orders(player(1))",
				CompleteText = "科技开始研究了。记住 A/B 只能选一个。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "destroy",
				Title = "拆掉敌方塔楼",
				Detail = "用纳米巨兽去打东边那座中立塔。路上注意别把巨兽送掉——它是你唯一的机动力量。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "纳米虫教程完成！",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			return t;
		}

		// =========================================================
		// 四、植物（菌毯扩散 · 自动建造 · 建筑血量）
		//
		// 教学点：菌毯铺出去 → 在菌毯上自动长建筑 → 造落叶坑出兵 → 点菌毯增殖 → 拆塔
		// 依据：Plant 全部建筑 AutoBuild + RequiresCreep（生命树除外）；
		//       菌毯格数换木材（Plant.tres CarpetIncomeCellsPerResource = 300）；
		//       落叶坑出叶犬、树苗巢出蜘蛛、巢堡出大树守卫。
		// =========================================================

		private static TutorialDefinition BuildPlant()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_plant",
				RaceId = "Plant",
				DisplayName = "植物 · 菌毯扩张",
				Tier = TutorialTier.Advanced,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "植物没有工人：靠菌毯往外铺，建筑在菌毯上自己长出来。菌毯就是你的经济和领土。",
			};


			// 初始：生命树 + 2 只叶犬（与设计表一致）
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "PlantLifeTree", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "PlantLeafDog", TeamId = 1, GridX = PlayerBaseX - 3, GridY = PlayerBaseY + 2 });
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "PlantLeafDog", TeamId = 1, GridX = PlayerBaseX - 4, GridY = PlayerBaseY + 3 });

			t.Objectives.Add(new TutorialObjective
			{
				Id = "wood",
				Title = "等菌毯产出木材",
				Detail = "植物没有工人采集。生命树周围的菌毯每 300 格每秒产 1 木材——\n" +
						 "等木材涨到 150，你就已经懂了植物的经济逻辑。",
				CompleteWhen = "resource(player(1), 'Wood') >= 150",
				ProgressExpression = "resource(player(1), 'Wood')",
				CompleteText = "★ 结论：植物的收入 = 菌毯面积。铺得越广，钱越多。",
				FocusGridX = PlayerBaseX,
				FocusGridY = PlayerBaseY,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "forestnode",
				Title = "用森林节点扩大菌毯",
				Detail = "点建造面板，在菌毯边缘放一个森林节点（PlantForestNode）。它会自动长好，并把菌毯再往外推 8 格。\n" +
						 "★ 植物的推进节奏就是「节点接力」：一个节点推 8 格，再用下一个接上去。",
				CompleteWhen = "built('PlantForestNode', player(1)) >= 1",
				CompleteText = "菌毯扩大了，收入也跟着涨。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "leafpit",
				Title = "造落叶坑出叶犬",
				Detail = "在菌毯上放一个落叶坑（PlantLeafPit），它会自动建成并开始生产叶犬。把叶犬补到 4 只。",
				CompleteWhen = "units('PlantLeafDog', player(1)) >= 4",
				ProgressExpression = "units('PlantLeafDog', player(1))",
				CompleteText = "有兵了。叶犬便宜，适合当消耗品和侦查。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "tech",
				Title = "研究菌毯增殖",
				Detail = "选中生命树研究『菌毯增殖』（PlantTech_Spread）。\n" +
						 "★ 这是植物最重要的运营科技：菌毯铺得又快又远，等于经济和领土一起加速。",
				CompleteWhen = "has_tech(player(1), 'PlantTech_Spread')",
				CompleteText = "菌毯加速了。植物越到后期越强，前提是菌毯没被清掉。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "destroy",
				Title = "拆掉敌方塔楼",
				Detail = "带着叶犬去打东边那座中立塔。★ 尽量在**自己的菌毯上**接战——\n" +
						 "离开菌毯，植物就失去了回复和防御加成。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "植物教程完成！",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			return t;
		}

		// =========================================================
		// 五、洞穴族（远程建造 · 驻扎 · 残骸重建）—— 上手难度最高
		//
		// 教学点：工兵 5 格远程采集/建造 → 训练场出兵 → 驻扎保命 → 学院升 T2 → 拆塔
		// 依据：CaveWorker BuildRangeTiles=5 / HarvestRangeTiles=5 / AutoSubmitHarvest；
		//       几乎所有建筑带 GarrisonCapacity（主巢 8）；学院提供 CaveT2；
		//       碎壳者 / 虫洞核心提供 CaveT3。
		// =========================================================

		private static TutorialDefinition BuildCave()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_cave",
				RaceId = "Cave",
				DisplayName = "洞穴族 · 驻扎与远程作业",
				Tier = TutorialTier.Advanced,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "洞穴族是上手最难的阵营：工兵能在 5 格之外干活，建筑全是掩体，几乎所有建筑都能驻扎部队。",
			};


			// 初始：主巢 + 3 个工兵（与设计表一致）
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "CaveMainNest", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			for (int i = 0; i < 3; i++)
				t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "CaveWorker", TeamId = 1, GridX = PlayerBaseX - 3 + i, GridY = PlayerBaseY + 3 });

			t.Objectives.Add(new TutorialObjective
			{
				Id = "gather",
				Title = "远距离采集",
				Detail = "选中工兵，右键点金矿。★ 洞穴工兵能在 **5 格之外**采集，不用贴到矿旁边，\n" +
						 "而且采集会自动提交，不用来回跑。把金属攒到 300。",
				CompleteWhen = "resource(player(1), 'Metal') >= 300",
				ProgressExpression = "resource(player(1), 'Metal')",
				CompleteText = "记住这个特性：工兵站远一点更安全，不用挤在矿边被范围伤害打死。",
				FocusGridX = 21,
				FocusGridY = 27,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "remote_build",
				Title = "远距离建造训练场",
				Detail = "选中工兵，点『建造』放下训练场（CaveTrainingGround）。\n" +
						 "★ 只要目标点在 5 格内，工兵**不需要走过去**就能施工。",
				CompleteWhen = "built('CaveTrainingGround', player(1)) >= 1",
				CompleteText = "远程建造是洞穴族最实用的机制。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "train",
				Title = "训练巢穴射手",
				Detail = "选中训练场，训练 3 个巢穴射手。",
				CompleteWhen = "units('CaveNestShooter', player(1)) >= 3",
				ProgressExpression = "units('CaveNestShooter', player(1))",
				CompleteText = "巢穴射手是洞穴的基础远程。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "garrison",
				Title = "驻扎（洞穴的保命核心）",
				Detail = "选中全部部队，右键点主巢把它们塞进去（容量 8）。\n" +
						 "★ 驻扎后单位几乎无法被击杀，主巢还能继续输出。被打的时候这是最有效的保命手段。",
				CompleteWhen = "garrison_orders(player(1)) >= 1",
				ProgressExpression = "garrison_orders(player(1))",
				CompleteText = "会驻扎，洞穴族就活得下来。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "tech",
				Title = "造学院，升 T2",
				Detail = "用工兵造学院（CaveAcademy）。★ 学院不只是研究科技，它本身**提供 T2 等级**——\n" +
						 "没有学院就出不了牧场、前哨、碎壳者这些二阶建筑。",
				CompleteWhen = "built('CaveAcademy', player(1)) >= 1",
				CompleteText = "二本解锁了。洞穴的等级门由建筑提供，这是它和别的族最大的不同。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "destroy",
				Title = "拆掉敌方塔楼",
				Detail = "带着射手去打东边那座中立塔。路上如果被打，把残血单位塞进最近的建筑驻扎。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "洞穴族教程完成！这是最难的一族，你已经会了。",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			return t;
		}

		// =========================================================
		// 六、多足机械（游牧 · 控制圈 · 蓝图生产）
		//
		// 教学点：没有建筑（全是蓝图）→ 牧羊人的控制圈 → 造游猎型 → 拆塔
		// 依据：「多足」表：无建筑，所有单位以蓝图形式拍下；大部分单位无视野，
		//       且必须在控制范围内才能被选中操作；建造型是长手工人，可建造与采集；
		//       初始 牧羊人*1 + 建造型*2；人口上限 200。
		//       实现细节：蓝图完工后由 Structure.cs:410 把 Blueprint_<X> 变成单位 <X>。
		// =========================================================

		private static TutorialDefinition BuildWanderer()
		{
			var t = new TutorialDefinition
			{
				Id = "tutorial_wanderer",
				RaceId = "Wanderer",
				DisplayName = "多足 · 游牧与控制圈",
				Tier = TutorialTier.Advanced,
				MapId = TutorialMapId,
				PlayerTeam = 1,
				Intro = "多足没有传统建筑：所有单位都先拍成蓝图，完工后变成可移动的单位。",
			};


			// 初始：牧羊人 + 2 个建造型（与设计表「多足 · 初始」一致）
			// 注意：这些是 **蓝图**，会在开局自动完工并变成真正的单位。
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "Blueprint_Shepherd", TeamId = 1, GridX = PlayerBaseX, GridY = PlayerBaseY });
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "Blueprint_Builder", TeamId = 1, GridX = PlayerBaseX - 4, GridY = PlayerBaseY - 3 });
			t.ExtraSpawns.Add(new TutorialSpawn { EntityId = "Blueprint_Builder", TeamId = 1, GridX = PlayerBaseX - 4, GridY = PlayerBaseY + 3 });

			t.Objectives.Add(new TutorialObjective
			{
				Id = "gather",
				Title = "建造型采集",
				Detail = "选中建造型（长手工人），右键金矿。多足的工人能采集也能建造，效率是 2 倍。",
				CompleteWhen = "resource(player(1), 'Metal') >= 300",
				ProgressExpression = "resource(player(1), 'Metal')",
				CompleteText = "多足靠金属和瓦斯。数据（d）只能靠击杀获得。",
				FocusGridX = 21,
				FocusGridY = 27,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "shepherd",
				Title = "找到牧羊人",
				Detail = "牧羊人是你的指挥中心：它提供 15 格控制范围与视野，" +
						 "范围外的单位无法被选中操作。把视角移到牧羊人身上。",
				CompleteWhen = "units('Shepherd', player(1)) >= 1",
				CompleteText = "牧羊人在哪，你的控制权就在哪。",
				FocusGridX = PlayerBaseX,
				FocusGridY = PlayerBaseY,
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "blueprint",
				Title = "拍下第一张蓝图",
				Detail = "用建造型拍下一个『游猎型』蓝图。蓝图完工后会自动变成游猎型单位。",
				CompleteWhen = "units('Hunter', player(1)) >= 1",
				CompleteText = "蓝图 → 单位，这就是多足的生产方式。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "army",
				Title = "组建小队",
				Detail = "再补到 3 个游猎型。它们便宜、跑得快，还能移动射击。",
				CompleteWhen = "units('Hunter', player(1)) >= 3",
				ProgressExpression = "units('Hunter', player(1))",
				CompleteText = "小队成形。",
			});

			t.Objectives.Add(new TutorialObjective
			{
				Id = "destroy",
				Title = "拆掉敌方塔楼",
				Detail = "带着游猎型去打东边那座中立塔。记住别走太远——离开牧羊人控制圈就指挥不动了。",
				CompleteWhen = "count('Tower', -2) == 0",
				CompleteText = "多足教程完成！",
				FocusGridX = EnemyTowerX,
				FocusGridY = EnemyTowerY,
			});

			return t;
		}
	}
}
