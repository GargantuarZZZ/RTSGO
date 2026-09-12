using Godot;
using RTS.Network;
using RTS.Core;
using System.Collections.Generic;

public partial class MainMenuController : Control
{
	public static bool ReturningFromMatch;
	public const string GameVersion = "0.2.0";
	// 手动构建标记：每次改代码后由 Codex 递增，用于两台机器对比是否同一份包
	public const string BuildTag = "20260811-200";

	public enum MenuPage { Main, RoomList, Lobby, Tutorial, None }

	[ExportGroup("Pages")]
	[Export] public Control PageMain;
	[Export] public Control PageRoomList;
	[Export] public Control PageLobby;

	[ExportGroup("Main Page")]
	[Export] public Button BtnHost;
	[Export] public Button BtnSinglePlayer;
	[Export] public Button BtnFind;
	[Export] public Button BtnQuit;
	[Export] public Label VersionLabel;

	[ExportGroup("Room List Page")]
	[Export] public Button BtnRefresh;
	[Export] public Button BtnBackToMain;
	[Export] public VBoxContainer RoomContainer;
	[Export] public PackedScene RoomEntryPrefab;

	[ExportGroup("Lobby Page")]
	[Export] public Button BtnReady;
	[Export] public Button BtnStart;
	[Export] public Button BtnBack;
	[Export] public MarginContainer PlayerListContainer;
	// ---- 统一大厅列表（一个可滚动列表里分开"参战者 / 观战者"两段）----
	//
	// 原来是"单个 VBox + 绝对像素偏移（386..1075）"：宽窗口下全部挤在左上角，
	// 而观战席位被我另开一块塞在左列、压住了按钮。
	// 现在合成**一个**列表：外层 ScrollContainer 用**锚点**随视口缩放，
	// 内部按段渲染玩家 / 机器人 / 观战者。
	[Export] public VBoxContainer PlayerList;
	[Export] public VBoxContainer SpectatorList;
	[Export] public Label PlayerSectionHeader;
	[Export] public Label SpectatorSectionHeader;
	[Export] public OptionButton OptRace;
	[Export] public OptionButton OptTeam;
	[Export] public OptionButton OptMap;

	[ExportGroup("Game Configuration")]
	[Export] public PackedScene GameScenePrefab;

	private bool _isLocalReady = false;
	private Node _steamService;

	private Godot.Collections.Array _cachedMembers;
		private MenuPage _currentPage = MenuPage.Main;
		private Button _settingsButton;
		/// <summary>主菜单上的"地图编辑器"入口。</summary>
		private Button _editorButton;
		/// <summary>主菜单上的"教程"入口（点开进入独立教程面板）。</summary>
		private Button _tutorialButton;
		/// <summary>教程面板（代码创建，不在 .tscn 里）。</summary>
		private Panel _tutorialPage;
		// P1-5：统一房间列表——人类/AI 都在自己的行里改种族/位置/队伍
		private CheckBox _obsCheck;
		private Button _btnAddBot;
		// 观战占位标签（真正的观战行渲染进场景里的 SpectatorList）
		private Label _lblSpectatorEmpty;
		private readonly List<BotRowData> _botRows = new();
		private int _nextBotPid = 100;

		private class BotRowData
		{
			public int Pid;
			public string Race = "Union";
			public int Slot;
			public int Difficulty = 2;
			public int Group;
		}

		public override void _Ready()
		{
			RTS.Settings.GameSettings.Load();
			GD.Print($"[Menu] Language={RTS.Settings.Localization.Current}");
			RTS.Settings.Localization.LanguageChanged += RefreshMenuTexts;
			_steamService = GetNodeOrNull("/root/SteamService");

		if (_steamService == null)
		{
			GD.PrintErr("[Menu] 未找到 /root/SteamService。");
		}

		// =========================================================
		// 基础按钮绑定
		// =========================================================

		if (BtnHost != null)
			BtnHost.Pressed += OnHostPressed;

		if (BtnSinglePlayer != null)
			BtnSinglePlayer.Pressed += OnSinglePlayerPressed;

		if (BtnFind != null)
			BtnFind.Pressed += OnFindPressed;

		if (BtnQuit != null)
			BtnQuit.Pressed += () => GetTree().Quit();

		// P2-5 设置按钮：主菜单打开设置菜单
		if (PageMain != null)
		{
			_settingsButton = new Button
			{
				Text = RTS.Settings.Localization.Tr("settings"),
				Position = new Vector2(99f, 321f),
				Size = new Vector2(189f, 44f)
			};
			_settingsButton.Pressed += OpenSettings;
			PageMain.AddChild(_settingsButton);

			// 地图编辑器入口（面向地图作者）。
			// 编辑器是独立场景：作者改完地图不需要重启游戏，
			// 返回主菜单时地图列表会自动重扫 res://Maps/。
			_editorButton = new Button
			{
				Text = RTS.Settings.Localization.Tr("main_menu_map_editor"),
				Position = new Vector2(99f, 371f),
				Size = new Vector2(189f, 44f)
			};
			_editorButton.Pressed += OpenMapEditor;
			PageMain.AddChild(_editorButton);

			// 教程入口：主菜单上只放一个按钮，点开进入**独立教程面板**
			// （面板里再列各阵营教程）。之前是把三个教程直接铺在主菜单上，
			// 阵营一多就会把主菜单挤爆，也不是"面板"该有的组织方式。
			_tutorialButton = new Button
			{
				Text = RTS.Settings.Localization.Tr("main_menu_tutorial"),
				Position = new Vector2(99f, 421f),
				Size = new Vector2(189f, 44f)
			};
			_tutorialButton.Pressed += () => SwitchPage(MenuPage.Tutorial);
			PageMain.AddChild(_tutorialButton);
		}

		// P1-1：单机机器人数量/难度（仅单机大厅显示）
		CreateBotOptions();

		if (BtnBackToMain != null)
			BtnBackToMain.Pressed += () => SwitchPage(MenuPage.Main);

		if (BtnBack != null)
			BtnBack.Pressed += OnLeaveLobbyPressed;

		if (BtnRefresh != null)
			BtnRefresh.Pressed += OnFindPressed;

		if (BtnReady != null)
			BtnReady.Pressed += OnReadyToggled;

		if (BtnStart != null)
			BtnStart.Pressed += OnStartGamePressed;

		if (OptMap != null)
			OptMap.ItemSelected += (idx) => OnMapSelected();

		RefreshMenuTexts();

		// =========================================================
		// SteamService 信号
		// =========================================================

		if (_steamService != null)
		{
			_steamService.Connect(
				"player_list_updated",
				Callable.From<Godot.Collections.Array>(OnSteamPlayerListUpdated)
			);

			_steamService.Connect(
				"lobby_joined",
				Callable.From<long>(OnSteamLobbyJoined)
			);

			_steamService.Connect(
				"lobby_list_received",
				Callable.From<Godot.Collections.Array>(OnSteamLobbyListReceived)
			);

			// 房间内收到任意包就刷新大厅界面
			_steamService.Connect(
				"packet_received",
				Callable.From<ulong, byte[]>(OnSteamPacketReceivedForLobbyRefresh)
			);
		}

		// =========================================================
		// NetworkManager 信号
		// =========================================================

		if (NetworkManager.Instance != null)
		{
			NetworkManager.Instance.LobbyUpdated += OnNetworkLobbyUpdated;
			NetworkManager.Instance.SessionReady += OnGameSessionReady;
		}
		else
		{
			GD.PrintErr("[Menu] NetworkManager.Instance 为空。");
		}

		SetupDropdowns();
		SwitchPage(MenuPage.Main);
		UpdateVersionLabel();
		ApplyMenuDesign();

		// --offline / --single / --no-steam：跳过菜单，直接进入单机对战。
		if (!ReturningFromMatch && NetworkManager.Instance != null && NetworkManager.IsOfflineLaunchRequested())
			CallDeferred(nameof(StartOfflineGameDirect));
		ReturningFromMatch = false;

		// 无头回归：AUTO_BOTS=N 走真实大厅机器人路径（pid 100+）自动加机器人，
		// AUTO_BOT_RACES=Union,Demon,... 按顺序指定种族；AUTO_START=1 自动开局。
		if (int.TryParse(OS.GetEnvironment("AUTO_BOTS"), out int autoBots) && autoBots > 0)
			Callable.From(() => SetupAutoLobby(autoBots)).CallDeferred();

		if (OS.GetEnvironment("OPEN_SETTINGS") == "1")
			CallDeferred(nameof(OpenSettings));

		// 无头回归：SETTINGS_DEBUG=1 打开设置菜单并报告布局尺寸
		// （设置项变多以后，最现实的风险是面板超出窗口高度导致内容点不到）
		if (OS.GetEnvironment("SETTINGS_DEBUG") == "1")
			CallDeferred(nameof(OpenSettingsForTest));

		// 无头回归：FX_DEBUG=1 检查程序化网格（指令路径/框选边框）真的生成了几何
		if (OS.GetEnvironment("FX_DEBUG") == "1")
			CallDeferred(nameof(CheckFxGeometryForTest));

		// 无头/截图用：LOBBY_PAGE=1 直接翻到大厅页。
		// 为什么需要：大厅布局出过多次问题（控件遮挡、内容跑到画面外），
		// 而截默认启动页看不到大厅 —— 没有这个开关就只能靠人肉截图反馈，
		// 我因此改错了好几轮。打开它之后截图工具能直接拍到大厅。
		if (OS.GetEnvironment("LOBBY_PAGE") == "1")
			CallDeferred(nameof(OpenLobbyPageForTest));

		// 无头回归：HUD_DEBUG=1 检查 HUD 面板内容是否被写死的像素尺寸裁掉
		if (OS.GetEnvironment("HUD_DEBUG") == "1")
			CallDeferred(nameof(CheckHudLayoutForTest));

		// 无头回归：LOBBY_DEBUG=1 检查大厅控件有没有互相遮挡
		if (OS.GetEnvironment("LOBBY_DEBUG") == "1")
			CallDeferred(nameof(CheckLobbyLayoutForTest));

		// 无头回归：SETTINGS_RT=1 检查设置/键位的注册表↔InputMap↔重置 往返一致
		if (OS.GetEnvironment("SETTINGS_RT") == "1")
			CallDeferred(nameof(CheckSettingsRoundTripForTest));

		// 无头回归：TEX_DEBUG=1 检查地形贴图**导入后**的实际像素值
		if (OS.GetEnvironment("TEX_DEBUG") == "1")
			CallDeferred(nameof(CheckTerrainTextureImportForTest));

		// 无头回归：OPEN_TUTORIAL=1 打开教程面板；=start 再按一下第 OPEN_TUTORIAL_INDEX 个「开始」。
		// DUMP_TUTORIALS=1 时额外把每套教程的完整结构（目标 + 理论页）打到日志，
		// 用来验证图鉴是从配置里真的读出了内容，而不是生成了一堆空页。
		var openTutorial = OS.GetEnvironment("OPEN_TUTORIAL");
		if (openTutorial == "1" || openTutorial == "start")
			CallDeferred(nameof(OpenTutorialPageForTest));

		if (OS.GetEnvironment("DUMP_TUTORIALS") == "1")
			CallDeferred(nameof(DumpTutorialsForTest));
	}

	/// <summary>无头回归：把全部教程的结构打进日志（验证图鉴内容非空）。</summary>
	private void DumpTutorialsForTest()
	{
		// 图鉴与生产链都靠配置库；这里显式先载入（幂等），
		// 否则会打印出一堆"无配置"的假阴性。
		RTS.Data.Configs.ConfigDatabase.LoadAll();
		RTS.Tutorial.TutorialCodex.HydrateAll(RTS.Tutorial.TutorialRegistry.All);

		// 教程文案本地化：面板构建前先注入翻译函数并应用到每个定义。
		// （运行时的同一件事在 StartTutorial 里做，两处都要，因为玩家可能先看图鉴。）
		RTS.Tutorial.TutorialDefinition.Translator = key =>
			RTS.Settings.Localization.Has(key) ? RTS.Settings.Localization.Tr(key) : null;
		foreach (var td in RTS.Tutorial.TutorialRegistry.All) td.ApplyLocalization();

		DumpProductionChains();

		foreach (var def in RTS.Tutorial.TutorialRegistry.All)
		{
			int units = RTS.Tutorial.TutorialCodex.BuildUnitPages(def.RaceId).Count;
			int structs = RTS.Tutorial.TutorialCodex.BuildStructurePages(def.RaceId).Count;
			int techs = RTS.Tutorial.TutorialCodex.BuildTechPages(def.RaceId).Count;
			GD.Print($"[TutorialDump] codex({def.RaceId}) 单位页={units} 建筑页={structs} 科技页={techs}");
			GD.Print("[TutorialDump] " + RTS.Tutorial.TutorialRuntime.Describe(def));
		}
	}

	/// <summary>
	/// 打印每族的"谁造谁 / 谁研究什么"。
	/// 教程目标写的是具体建筑 ID（built('BB')、has_tech('UnionTech_Stim')），
	/// 必须确认这些 ID 与配置里的生产链一致，否则教程会给出做不到的指引。
	/// </summary>
	private void DumpProductionChains()
	{
		string[] races = { "Union", "Terran", "Demon", "Nano", "Plant", "Cave", "Wanderer" };
		foreach (string raceId in races)
		{
			var rc = RTS.Data.Configs.ConfigDatabase.GetRace(raceId);
			if (rc == null) { GD.Print($"[Chain] {raceId}: 无配置"); continue; }

			var sb = new System.Text.StringBuilder();
			sb.Append($"[Chain] {raceId}");

			// 单位 → 能造什么
			foreach (string uid in rc.AvailableUnitIds)
			{
				var u = RTS.Data.Configs.ConfigDatabase.GetUnit(uid);
				if (u == null || !u.CanBuild || u.BuildableStructureIds.Count == 0) continue;
				sb.Append($" | {uid}可造=[{string.Join(",", u.BuildableStructureIds)}]");
			}
			GD.Print(sb.ToString());

			// 建筑 → 能出什么兵 / 研究什么科技
			foreach (string sid in rc.AvailableStructureIds)
			{
				var s = RTS.Data.Configs.ConfigDatabase.GetStructure(sid);
				if (s == null) continue;
				var bits = new System.Collections.Generic.List<string>();
				if (s.TrainableUnitIds.Count > 0) bits.Add("造=" + string.Join(",", s.TrainableUnitIds));
				if (s.ResearchableTechIds.Count > 0) bits.Add("研=" + string.Join(",", s.ResearchableTechIds));
				if (s.RequiredTechIds.Count > 0) bits.Add("需=" + string.Join(",", s.RequiredTechIds));
				if (s.AutoProduceUnitIds.Count > 0) bits.Add("自动=" + string.Join(",", s.AutoProduceUnitIds));
				// 经济与机制：教程要写"经济从哪来"，必须看到这些字段
				if (s.ProvidesResourceIncome) bits.Add($"产={s.IncomeResourceType}x{s.IncomeAmountPerCycle}/{s.IncomeCycleTicks}t");
				if (s.AutoHarvestRadiusTiles > 0) bits.Add($"范围采集={s.AutoHarvestRadiusTiles}格{s.AutoHarvestPerSecondPerNode}/s");
				if (s.CreepSpreadRadius > 0) bits.Add($"菌毯={s.CreepSpreadRadius}");
				if (s.IsDropOffPoint) bits.Add("交付点");
				if (s.SupplyProvided > 0) bits.Add($"人口+{s.SupplyProvided}");
				if (s.AmmoRangeTiles > 0) bits.Add($"弹药范围={s.AmmoRangeTiles}");
				if (s.GarrisonCapacity > 0) bits.Add($"驻扎={s.GarrisonCapacity}");
				if (s.IsUnique) bits.Add("唯一");
				if (s.RequiresCreep) bits.Add("需菌毯");
				if (s.ProvidesTechIds.Count > 0) bits.Add("给等级=" + string.Join(",", s.ProvidesTechIds));
				if (bits.Count > 0) GD.Print($"[Chain]   {sid}: {string.Join(" ", bits)}");
			}
		}
	}

	/// <summary>
	/// 无头回归：检查指令路径/框选网格真的生成了几何。
	///
	/// 这类"程序化网格"最典型的失败模式就是**静默为空**：
	/// 没报错、节点也在，但一个三角形都没有，玩家看到的就是"什么都没画"。
	/// 所以这里直接断言顶点数，而不是只看有没有报错。
	/// </summary>
	private void CheckFxGeometryForTest()
	{
		var host = new Node3D { Name = "FxGeomProbe" };
		AddChild(host);

		// 注意：GameFX3D 声明在全局命名空间（文件里没有 namespace 语句），
		// 所以不能写 RTS.Core.GameFX3D —— 写错会编译失败。
		var fx = new GameFX3D { Name = "GameFX3D" };
		host.AddChild(fx);

		Callable.From(() =>
		{
			// 框选：给一个 200x150 的世界矩形（模拟拖拽中）
			fx.UpdateBox(new Vector2(0f, 0f), new Vector2(200f, 150f), true);
			var box = fx.FindChild("SelectionBox", true, false) as MeshInstance3D;
			int boxSurfaces = box?.Mesh?.GetSurfaceCount() ?? 0;

			// ImmediateMesh 取不到顶点数组，所以用 AABB 验证几何。
			// 注意：Mesh.GetAabb() 是**局部空间**的，不含节点的 -90° 旋转，
			// 所以"地面上的高度"在这里是 Y 轴而不是 Z（一开始写成 Z 导致误报）。
			// 四条边框画在矩形**外侧**，所以包围盒必然略大于 200x150。
			Aabb boxAabb = box?.Mesh?.GetAabb() ?? new Aabb();
			bool boxOk = boxSurfaces == 1
				&& boxAabb.Size.X > 200f && boxAabb.Size.Y > 150f
				&& boxAabb.Size.X < 240f && boxAabb.Size.Y < 190f;

			// 空选中时不该建路径网格、也不该报错
			var path = fx.FindChild("PathLines", true, false) as MeshInstance3D;
			bool pathEmptyOk = path == null || (path.Mesh?.GetSurfaceCount() ?? 0) == 0;

			GD.Print($"[FxDbg] 框选 surface={boxSurfaces} AABB={boxAabb.Size} 判定={(boxOk ? "OK" : "不对")} " +
				$"| 空选中路径={(pathEmptyOk ? "无几何(OK)" : "不该有几何")}");

			if (!boxOk)
				GD.PrintErr($"[FxDbg] 框选空心边框几何不对：surface={boxSurfaces} AABB={boxAabb.Size} " +
					$"（期望 1 个 surface，且 X∈(200,240) Y∈(150,190)）—— 边框会显示不出来或画错。");
			if (!pathEmptyOk)
				GD.PrintErr("[FxDbg] 没有选中单位时路径网格不该有几何。");
		}).CallDeferred();
	}

	/// <summary>
	/// 数某个 MeshInstance3D 的三角形数（按名称在所有子节点里找）。
	///
	/// 注意：只对 `ArrayMesh` 有效。`ImmediateMesh` 走自己的内部存储，
	/// `SurfaceGetArrays` 取不到顶点（调用方应改用 GetAabb/GetSurfaceCount 验证）。
	/// </summary>
	private static int CountTriangles(Node root, string nodeName)
	{
		var mi = root.FindChild(nodeName, true, false) as MeshInstance3D;
		if (mi?.Mesh is not ArrayMesh am || am.GetSurfaceCount() == 0)
			return 0;
		var arrays = am.SurfaceGetArrays(0);
		var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
		return verts.Length / 3;
	}

	/// <summary>
	/// 无头回归：检查 HUD 底部各面板的**内容是否被容器裁掉**。
	///
	/// 为什么需要：`user_ui.tscn` 里这些面板的位置与尺寸是**写死的像素值**
	/// （offset_left/top/right/bottom），而内容是按代码长出来的
	/// （动作面板 15 个槽位、生产队列、选中信息面板的武器/增益行…）。
	/// 两边一旦不同步，控件就会被静默裁掉 —— 不报错、就是点不到。
	/// </summary>
	/// <summary>
	/// 无头回归：LOBBY_DEBUG=1 检查大厅里有没有控件互相遮挡。
	///
	/// 为什么需要它：观战席位我第一版把坐标写死在 (94,476) 高 96，
	/// 正好压住地图下拉（y 477..497）和"开始"键（y 510..554）——
	/// 这种错**肉眼要打开对应页面才看得见**，纯代码审查和单元测试都发现不了。
	/// 与其靠人眼，不如让布局自己报出来：两两求交，有重叠就报错并给出双方名字。
	/// </summary>
	private void CheckLobbyLayoutForTest()
	{
		if (PageLobby == null)
		{
			GD.PrintErr("[LobbyDbg] PageLobby 为空");
			return;
		}

		Callable.From(() =>
		{
			// 强制跑一次容器排布：headless 下从未渲染，VBoxContainer 不会主动排布子节点，
			// 不刷的话同级子节点会重叠在同一个原点，报出假遮挡。
			PageLobby.ResetSize();
			foreach (var c in PageLobby.GetChildren())
				if (c is Control cc) cc.ResetSize();

			var items = new System.Collections.Generic.List<(string Name, Rect2 Rect)>();
			CollectVisibleControls(PageLobby, items);

			// 布局未生效时不要报遮挡：headless 下页面从未真正排布，
			// 控件会拿到负坐标（实测 -63,-158）——那是废值，不是真遮挡。
			// 这种情况直接说"跳过"，而不是输出假阳性把真正的问题淹掉。
			int invalid = 0;
			foreach (var it in items)
				if (it.Rect.Position.X < 0f || it.Rect.Position.Y < 0f) invalid++;

			if (invalid > 0)
			{
				GD.Print($"[LobbyDbg] 跳过：{invalid}/{items.Count} 个控件拿到负坐标，" +
					"说明布局尚未生效（headless 常见）。请用截图确认：\n" +
					"  LOBBY_PAGE=1 godot --path . res://Scenes/Tools/ScreenshotTool.tscn " +
					"-- --shot=res://Scenes/Menu/UI_MainMenu.tscn --wait=200 --out=x.png");
				return;
			}

			GD.Print($"[LobbyDbg] 大厅可见控件 {items.Count} 个");

			int overlaps = 0;
			for (int i = 0; i < items.Count; i++)
			{
				for (int j = i + 1; j < items.Count; j++)
				{
					var a = items[i];
					var b = items[j];
					// 父子关系不算遮挡（子控件本来就落在父容器里）
					if (a.Name.StartsWith(b.Name + "/") || b.Name.StartsWith(a.Name + "/"))
						continue;

					var inter = a.Rect.Intersection(b.Rect);
					// 1~2px 的边缘相接不算遮挡（控件边框/描边常见），
					// 只报真正盖住内容的。
					if (inter.Size.X <= 2f || inter.Size.Y <= 2f) continue;

					overlaps++;
					GD.PrintErr($"[LobbyDbg] ✗ 遮挡：'{a.Name}' {a.Rect} 与 '{b.Name}' {b.Rect} " +
						$"重叠 {inter.Size}");
				}
			}

			if (overlaps == 0)
				GD.Print("[LobbyDbg] 布局自检通过：无控件互相遮挡");
			else
				GD.PrintErr($"[LobbyDbg] 发现 {overlaps} 处遮挡");
		}).CallDeferred();
	}

	/// <summary>截图/无头用：直接翻到大厅页（LOBBY_PAGE=1）。</summary>
	private void OpenLobbyPageForTest()
	{
		// 先摆好离线大厅（真人 + 几个机器人），否则列表是空的，
		// 截图看不出真实排版 —— 我之前就是拿空列表的图去判断，白改了几轮。
		SetupAutoLobby(3);
		SwitchPage(MenuPage.Lobby);
		EnsureLobbyMembersAndRefresh();

		// LOBBY_RECTS=1 把控件实际矩形打出来。改布局前先量，别再猜坐标。
		if (OS.GetEnvironment("LOBBY_RECTS") == "1")
			Callable.From(DumpLobbyRects).CallDeferred();
	}

	/// <summary>打印 Page_Lobby 下所有控件的实际全局矩形（排查布局用）。</summary>
	private void DumpLobbyRects()
	{
		if (PageLobby == null) return;
		PageLobby.ResetSize();
		foreach (var c in PageLobby.GetChildren())
			if (c is Control cc) cc.ResetSize();

		GD.Print($"[RectDump] === Page_Lobby 控件实际矩形 ===");
		GD.Print($"[RectDump] 视口={GetViewport().GetVisibleRect().Size} 本节点 Scale={Scale} " +
			$"PageLobby.Scale={PageLobby.Scale} PageLobby.Rect={PageLobby.GetRect()}");
		DumpRect(PageLobby, 0);
	}

	private static void DumpRect(Node n, int depth)
	{
		if (n is Control c)
		{
			var r = c.GetGlobalRect();
			GD.Print($"[RectDump] {new string(' ', depth * 2)}{n.Name} [{n.GetClass()}] " +
				$"全局=({r.Position.X:0},{r.Position.Y:0}) 尺寸=({r.Size.X:0},{r.Size.Y:0})" +
				(c.Visible ? "" : " (隐藏)"));
		}
		foreach (var ch in n.GetChildren())
			DumpRect(ch, depth + 1);
	}

	/// <summary>
	/// 分段标题样式。尺寸与字号一律取自 <see cref="RTS.UI.UiLayout"/>，不再写字面量。
	///
	/// 必须显式设字号：主题默认字号很大，一个"参战者"标题曾占 689×75，
	/// 把列表容器撑到 985 高（视口只有 648），把"观战者"段顶到屏幕外。
	/// </summary>
	private static void StyleSectionHeader(Label header)
	{
		header.AddThemeFontSizeOverride("font_size", RTS.UI.UiLayout.FontSection);
		header.AddThemeColorOverride("font_color", new Color(0.78f, 0.82f, 0.88f));
		header.CustomMinimumSize = new Vector2(0f, RTS.UI.UiLayout.HeaderHeight);
	}

	/// <summary>收集可见控件及其在 PageLobby 坐标系下的矩形（递归）。</summary>
	private static void CollectVisibleControls(Node parent, System.Collections.Generic.List<(string, Rect2)> outList)
	{
		foreach (var child in parent.GetChildren())
		{
			if (child is not Control c || !c.Visible) continue;

			// 只统计"叶子/有实际内容"的控件：纯容器（VBox/HBox）本身不画东西，
			// 但它的子控件会单独被收集，所以跳过容器避免把父框也算成一次遮挡。
			bool isContainer = c is BoxContainer or Container;
			if (!isContainer)
			{
				var r = c.GetGlobalRect();
				// 跳过布局尚未生效的控件：headless 下从未渲染，容器不会去排布子节点，
				// 未排布的同级会在同一坐标上报出假重叠（实测两段标题都在 y=5.2）。
				bool degenerate = r.Size.X < 1f || r.Size.Y < 1f
					|| (Mathf.IsEqualApprox(r.Position.X, 0f) && Mathf.IsEqualApprox(r.Position.Y, 0f));
				if (!degenerate)
					outList.Add((c.GetPath().ToString(), r));
			}

			CollectVisibleControls(c, outList);
		}
	}

	private void CheckHudLayoutForTest()
	{
		var scene = GD.Load<PackedScene>("res://Scenes/UI/user_ui.tscn");
		if (scene == null)
		{
			GD.PrintErr("[HudDbg] 载入 user_ui.tscn 失败");
			return;
		}

		var ui = scene.Instantiate();
		AddChild(ui);

		Callable.From(() =>
		{
			string[] targets =
			{
				"ActionPanel", "ProductionQueue", "SelectionInfoPanel",
				"MiniMap", "InfoPanel", "BottomPanel",
			};

			foreach (string name in targets)
			{
				var node = ui.FindChild(name, true, false) as Control;
				if (node == null)
				{
					GD.Print($"[HudDbg] {name}: <未找到>");
					continue;
				}

				Vector2 size = node.Size;
				Vector2 min = node.GetCombinedMinimumSize();
				bool clipped = min.X > size.X + 1f || min.Y > size.Y + 1f;

				GD.Print($"[HudDbg] {name,-20} 尺寸={size} 最小={min} " +
					$"{(clipped ? "★ 内容被裁掉" : "OK")}");

				if (clipped)
					GD.PrintErr($"[HudDbg] {name} 的内容比容器大 " +
						$"(最小 {min} > 实际 {size})，会有控件被裁掉、点不到。");
			}

			// 动作面板的槽位是**运行时**在 _Ready 里创建的，
			// 而此刻它还只是场景实例（没进树渲染），所以上面的静态测量看不到内容高度。
			// 这里直接按它的构造参数推算，并留出余量判定。
			const int slots = 15, columns = 5, btnPx = 38;
			int rows = (slots + columns - 1) / columns;
			int gridH = rows * btnPx;
			var actionPanel = ui.FindChild("ActionPanel", true, false) as Control;
			float panelH = actionPanel?.Size.Y ?? 0f;
			GD.Print($"[HudDbg] 动作面板推算：{slots} 槽位 / {columns} 列 = {rows} 行 × {btnPx}px " +
				$"= {gridH}px，容器高 {panelH:0}px，余量 {panelH - gridH:0}px");
			if (panelH - gridH < 8f)
				GD.PrintErr($"[HudDbg] 动作面板余量不足 {panelH - gridH:0}px —— " +
					"槽位尺寸/列数一改就会被裁掉，应改为内容自适应而不是写死像素。");
		}).CallDeferred();
	}

	/// <summary>
	/// 无头回归：设置持久化往返。
	///
	/// 为什么值得单独测：`Keybinds` 从"手写 4 条"改成"由 InputActions 注册表构建"后，
	/// 存档读取路径也变了（多了 13 个新动作、多了面板技能槽）。
	/// 一旦读取/回写有偏差，表现是"玩家改的键重启后失效"或
	/// "新动作拿到脏值"，两者都不会报错。
	/// </summary>
	private void CheckSettingsRoundTripForTest()
	{
		int total = RTS.Settings.InputActions.All.Length;
		int rebindable = RTS.Settings.InputActions.Rebindable().Count;

		// 1. 注册表 → Keybinds 必须一一对应（缺一个就会静默用不上那个动作）
		int missing = 0;
		foreach (var def in RTS.Settings.InputActions.All)
			if (!RTS.Settings.GameSettings.Keybinds.ContainsKey(def.Action)) missing++;

		// 2. 默认冲突自检
		var conflicts = RTS.Settings.InputActions.FindDefaultConflicts();

		GD.Print($"[SettingsRT] 动作总数={total}（可改键 {rebindable}）" +
			$" Keybinds 缺项={missing} 默认冲突={conflicts.Count}");

		if (missing > 0)
			GD.PrintErr($"[SettingsRT] 有 {missing} 个动作没进 Keybinds —— 它们在设置里看不到、也改不了。");
		foreach (string c in conflicts)
			GD.PrintErr($"[SettingsRT] 默认键位冲突：{c}");

		// 3. 改键 → 重置 → 必须回到默认，且 ApplyKeybinds 不能抛
		string probe = "game_stop";
		try
		{
			// 走新的"绑定"接口（不再直接改 Keybinds 投影）
			RTS.Settings.GameSettings.SetBinding(probe, RTS.Settings.BindingSlot.Primary, 0,
				RTS.Settings.InputBinding.Key(Key.J));
			RTS.Settings.GameSettings.ApplyKeybinds();
			bool applied = InputMap.HasAction(probe) && InputMap.ActionGetEvents(probe).Count > 0;

			int changed = RTS.Settings.GameSettings.ResetAllKeybinds();
			var restored = RTS.Settings.GameSettings.GetBinding(probe, RTS.Settings.BindingSlot.Primary, 0);
			bool backToDefault = restored.AsKey == RTS.Settings.InputActions.Get(probe).Default;

			GD.Print($"[SettingsRT] 改键后 InputMap 生效={applied} 恢复默认={backToDefault} " +
				$"（重置了 {changed} 项）");

			if (!applied) GD.PrintErr("[SettingsRT] 改键后 InputMap 里没有事件，按键不会生效。");
			if (!backToDefault) GD.PrintErr($"[SettingsRT] 恢复默认失败：{restored.AsKey} ≠ 默认值。");
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[SettingsRT] 改键/重置抛异常：{e.Message}");
		}

		// 3b. 修饰键组合：Ctrl+H 必须与裸 H 区分，且不与"驻守(H)"冲突
		try
		{
			var ctrlH = RTS.Settings.InputBinding.Key(Key.H, RTS.Settings.BindingSlot.Primary, ctrl: true);
			var plainH = RTS.Settings.InputBinding.Key(Key.H);
			bool distinct = !ctrlH.SameAs(plainH);
			bool ctrlHConflict = RTS.Settings.InputActions.Conflicts(
				RTS.Settings.InputActions.Get("game_vote_rematch"),
				RTS.Settings.InputActions.Get("game_hold"));
			string text = RTS.Settings.GameSettings.BindingToText(ctrlH);

			GD.Print($"[SettingsRT] 修饰键：签名区分={distinct} 与裸键冲突={ctrlHConflict} 文本='{text}'");
			if (!distinct) GD.PrintErr("[SettingsRT] Ctrl+H 与 H 签名相同，修饰键没生效。");
			if (ctrlHConflict) GD.PrintErr("[SettingsRT] Ctrl+H 被误判为与 H 冲突。");
			if (!text.Contains("Ctrl+")) GD.PrintErr($"[SettingsRT] 绑定文本缺修饰键前缀：'{text}'");
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[SettingsRT] 修饰键检查抛异常：{e.Message}");
		}

		// 3c. 鼠标绑定（含侧键）：必须落到 InputMap 的 InputEventMouseButton
		try
		{
			string mAction = "game_attack_move";
			var savedM = new System.Collections.Generic.List<RTS.Settings.InputBinding>(
				RTS.Settings.GameSettings.BindingsOf(mAction));

			RTS.Settings.GameSettings.SetBinding(mAction, RTS.Settings.BindingSlot.Secondary, 0,
				RTS.Settings.InputBinding.Mouse(MouseButton.Xbutton1, RTS.Settings.BindingSlot.Secondary));
			RTS.Settings.GameSettings.ApplyKeybinds();

			bool hasMouse = false;
			foreach (var ev in InputMap.ActionGetEvents(mAction))
				if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Xbutton1)
					hasMouse = true;

			string mt = RTS.Settings.GameSettings.BindingToText(
				RTS.Settings.GameSettings.GetBinding(mAction, RTS.Settings.BindingSlot.Secondary, 0));

			GD.Print($"[SettingsRT] 鼠标侧键：InputMap 生效={hasMouse} 文本='{mt}'");
			if (!hasMouse) GD.PrintErr("[SettingsRT] 鼠标侧键没落到 InputMap，绑定不会生效。");

			RTS.Settings.GameSettings.SetBindings(mAction, savedM);
			RTS.Settings.GameSettings.ApplyKeybinds();
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[SettingsRT] 鼠标绑定检查抛异常：{e.Message}");
		}

		// 3d. 次要键位：聊天默认应有 T 与 Enter 两条
		try
		{
			int kb = 0;
			foreach (var x in RTS.Settings.GameSettings.BindingsOf("game_chat"))
				if (!x.IsEmpty) kb++;
			GD.Print($"[SettingsRT] 次要键位：game_chat 绑定数={kb}（默认应为 2：T + Enter）");
			if (kb < 2) GD.PrintErr("[SettingsRT] 次要键位没生效，chat 只绑了一条。");
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[SettingsRT] 次要键位检查抛异常：{e.Message}");
		}

		// 4. 磁盘往返：写出去再用 ConfigFile 读回来。
		//    这一步不能只用 GameSettings.Load()（它有 _loaded 短路），
		//    所以直接读文件，验证"写进去的绑定能读回来"。
		//
		//    注意要连**次要键与修饰键**一起验：只验主键的话，
		//    "次要键位重启后丢失"这类问题查不出来。
		try
		{
			RTS.Settings.GameSettings.SetBinding("game_stop", RTS.Settings.BindingSlot.Primary, 0,
				RTS.Settings.InputBinding.Key(Key.J));
			RTS.Settings.GameSettings.SetBinding("game_hold", RTS.Settings.BindingSlot.Secondary, 0,
				RTS.Settings.InputBinding.Mouse(MouseButton.Xbutton1, RTS.Settings.BindingSlot.Secondary));
			RTS.Settings.GameSettings.Save();

			var cfg = new ConfigFile();
			if (cfg.Load("user://settings.cfg") != Error.Ok)
			{
				GD.PrintErr("[SettingsRT] 读不回 settings.cfg —— 设置无法持久化。");
			}
			else
			{
				var stopRaw = cfg.GetValue("keys", "game_stop", new string[0]).AsStringArray();
				var holdRaw = cfg.GetValue("keys", "game_hold", new string[0]).AsStringArray();

				// 反序列化后比对，确认"写出去的结构"能还原成等价绑定
				var stopB = stopRaw.Length > 0
					? RTS.Settings.GameSettings.ParseBinding(stopRaw[0]) : RTS.Settings.InputBinding.None;
				RTS.Settings.InputBinding holdB = RTS.Settings.InputBinding.None;
				foreach (string s in holdRaw)
				{
					var b = RTS.Settings.GameSettings.ParseBinding(s);
					if (b.Device == RTS.Settings.BindingDevice.Mouse) { holdB = b; break; }
				}

				bool ok = stopB.AsKey == Key.J && holdB.AsMouse == MouseButton.Xbutton1;
				GD.Print($"[SettingsRT] 磁盘往返：game_stop={RTS.Settings.GameSettings.BindingToText(stopB)} " +
					$"game_hold(次)={RTS.Settings.GameSettings.BindingToText(holdB)} {(ok ? "OK" : "★ 不一致")}");
				if (!ok)
					GD.PrintErr("[SettingsRT] 绑定写盘后读回不一致 —— 次要键/鼠标绑定重启会失效。");
			}
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[SettingsRT] 磁盘往返抛异常：{e.Message}");
		}
		finally
		{
			// 恢复原状，避免污染后续手测
			RTS.Settings.GameSettings.ResetAllKeybinds();
			RTS.Settings.GameSettings.Save();
		}
	}

	/// <summary>
	/// 无头回归：检查地形贴图**导入后**的实际像素值。
	///
	/// 为什么必须查这一层：PNG 源文件看着正常，不代表引擎加载到的纹理正常。
	/// sRGB 被当成线性（或反之）会让颜色明显偏移，而这类错误**不会报任何错**。
	/// 尤其是我把 `process/hdr_as_srgb` 也一并写进 import 参数时——
	/// 那是给 HDR 用的开关，对普通 albedo 关掉它会改变解释方式。
	/// </summary>
	private void CheckTerrainTextureImportForTest()
	{
		string[] names = { "Grass_Albedo", "Grass_Normal", "Grass_Roughness", "Wall_Albedo" };
		foreach (string n in names)
		{
			var tex = RTS.Core.TextureLoader3D.LoadPng($"res://ArtRes/imgs3d/terrain/{n}.png");
			if (tex == null)
			{
				GD.PrintErr($"[TexDbg] {n}: 加载失败（贴图拿不到，会退回旧图或纯色）");
				continue;
			}

			var img = tex.GetImage();
			if (img == null)
			{
				GD.PrintErr($"[TexDbg] {n}: 纹理没有 Image 数据");
				continue;
			}

			// 采样中心区域的平均色，避开边缘
			int w = img.GetWidth(), h = img.GetHeight();
			double r = 0, g = 0, b = 0;
			int count = 0;
			for (int y = h / 4; y < h * 3 / 4; y += 4)
			{
				for (int x = w / 4; x < w * 3 / 4; x += 4)
				{
					var c = img.GetPixel(x, y);
					r += c.R; g += c.G; b += c.B; count++;
				}
			}
			if (count == 0) continue;
			r /= count; g /= count; b /= count;

			// 法线图的判据：单位法线应集中在 (0.5,0.5,1) 附近，所以 B 明显高于 RG
			bool normalOk = n.Contains("Normal") ? (b > 0.7 && b > r && b > g) : true;

			GD.Print($"[TexDbg] {n,-18} {w}x{h} 格式={img.GetFormat()} " +
				$"平均RGB=({r:0.000},{g:0.000},{b:0.000}) " +
				$"{(n.Contains("Normal") ? (normalOk ? "法线OK" : "★ 法线异常") : "")}");

			if (n.Contains("Normal") && !normalOk)
				GD.PrintErr($"[TexDbg] {n} 的法线方向异常（B={b:0.000}，应 >0.7 且大于 R/G）" +
					"—— 光照会明显发暗或错乱，检查导入是否为 normal_map=1 / 线性。");
		}
	}

	/// <summary>无头回归：打印设置菜单的布局尺寸（检查是否超出窗口）。</summary>
	private void OpenSettingsForTest()
	{
		RTS.UI.SettingsMenu.Open(this);
		var menu = RTS.UI.SettingsMenu.Instance;

		// SETTINGS_PAGE=keybinds 时直接翻到键位页：主/次键位与鼠标绑定的
		// 显示只有在这一页才看得到，不然截图拍到的是语言页。
		string wantPage = OS.GetEnvironment("SETTINGS_PAGE");
		if (!string.IsNullOrEmpty(wantPage))
			Callable.From(() => menu.ShowPageForTest(wantPage)).CallDeferred();

		// 等一帧布局算完再量
		Callable.From(() =>
		{
			var vp = GetViewport().GetVisibleRect().Size;
			int buttons = 0;
			foreach (var n in menu.FindChildren("*", "Button", true, false)) buttons++;

			// 找到内层面板量它的实际高度：菜单根是全屏锚点，
			// GetCombinedMinimumSize() 在它上面恒为 0，量不出内容高度。
			var panel = menu.FindChild("*", true, false) as PanelContainer;
			float panelH = panel?.Size.Y ?? -1f;
			var scroll = menu.GetChildCount() > 1 ? menu.GetChild(1) as ScrollContainer : null;
			float contentH = (scroll?.GetChild(0) as Control)?.GetCombinedMinimumSize().Y ?? -1f;

			GD.Print($"[SettingsDbg] 视口={vp} 面板高度={panelH:0} 内容高度={contentH:0} " +
				$"按钮数={buttons} 需要滚动={contentH > vp.Y}");
			if (panelH > vp.Y + 1f)
				GD.PrintErr($"[SettingsDbg] 设置面板比窗口还高（{panelH:0} > {vp.Y:0}），" +
					"底部按钮会被截掉、点不到 —— 外层滚动没有生效。");
		}).CallDeferred();
	}

	/// <summary>
	/// 无头回归入口：打开教程面板并打印结构。
	/// 面板是纯 UI，只有真正构建一次才能发现节点/回调问题。
	/// OPEN_TUTORIAL=start 时再按一个「开始这个教程」，验证从面板点进教程的完整链路。
	/// </summary>
	private void OpenTutorialPageForTest()
	{
		GD.Print($"[Menu][TEST] 进入教程面板测试，OPEN_TUTORIAL={OS.GetEnvironment("OPEN_TUTORIAL")}");
		SwitchPage(MenuPage.Tutorial);

		// 面板改成"左列表 + 右详情"后，按钮文案也随之变了：
		// 左侧是教程条目（文案含"目标 · 资料页"），右侧是「开始这个教程」。
		var starts = new List<Button>();
		var entries = new List<Button>();
		int buttons = 0;
		if (_tutorialPage != null)
		{
			foreach (var node in _tutorialPage.FindChildren("*", "Button", true, false))
			{
				if (node is not Button btn) continue;
				buttons++;
				if (btn.Text == "开始这个教程") starts.Add(btn);
				else if (btn.Text.Contains("目标 ·")) entries.Add(btn);
			}
		}

		int treePages = 0;
		if (_tutorialPageTree != null)
		{
			// 统计叶子节点数（= 资料页数），分组节点不算
			var root = _tutorialPageTree.GetRoot();
			if (root != null)
			{
				foreach (var section in root.GetChildren())
					foreach (var page in section.GetChildren())
						treePages++;
			}
		}

		GD.Print($"[Menu][TEST] 教程面板可见={_tutorialPage != null && _tutorialPage.Visible} " +
			$"按钮数={buttons} 教程条目={entries.Count} 开始按钮={starts.Count} " +
			$"当前资料页目录={treePages} 教程数={RTS.Tutorial.TutorialRegistry.All.Count}");

		if (_tutorialPageBody == null)
		{
			GD.PrintErr("[Menu][TEST] _tutorialPageBody 为 null（正文区没建出来）。");
		}
		else if (_tutorialPageBody.Text.Length == 0)
		{
			GD.PrintErr("[Menu][TEST] 初始正文区是空的：资料页没有被渲染。");
		}
		else
		{
			GD.Print($"[Menu][TEST] 初始正文：字符数={_tutorialPageBody.Text.Length} " +
				$"标题='{(_currentPageLabel?.Text ?? "")}'");
		}

		if (OS.GetEnvironment("OPEN_TUTORIAL") != "start")
			return;

		if (starts.Count == 0)
		{
			GD.PrintErr("[Menu][TEST] 详情区没有找到「开始这个教程」按钮。");
			return;
		}

		// OPEN_TUTORIAL_INDEX=N 选择左列表第 N 个教程（默认 0），再点开始。
		int index = 0;
		if (int.TryParse(OS.GetEnvironment("OPEN_TUTORIAL_INDEX"), out int parsed) && parsed >= 0)
			index = parsed;

		if (index >= entries.Count)
		{
			GD.PrintErr($"[Menu][TEST] 教程下标 {index} 越界（共 {entries.Count} 个）。");
			return;
		}

		GD.Print($"[Menu][TEST] 选中第 {index} 个教程并点开始…");
		entries[index].EmitSignal(BaseButton.SignalName.Pressed);
		GD.Print($"[Menu][TEST]   当前教程={_currentTutorialDef?.Id ?? "(null)"}");

		// 选中后详情区重建，正文必须跟着换（这是这次修的核心 bug：
		// 之前正文永远是空的，因为 metadata 里的分组名被 Variant 的 '\0' 截掉了）
		if (_tutorialPageBody == null || _tutorialPageBody.Text.Length == 0)
			GD.PrintErr($"[Menu][TEST] 教程[{_currentTutorialDef?.Id}] 选中后正文是空的！");
		else
			GD.Print($"[Menu][TEST]   正文 OK：字符数={_tutorialPageBody.Text.Length} " +
				$"标题='{(_currentPageLabel?.Text ?? "")}'");

		// 再验证"切换资料页"的路径（用户点目录里另一条）。
		// 程序化 Select() 不发 ItemSelected，所以要手动触发一次回调。
		var secondPage = FindPageItem(1);
		if (secondPage != null && _tutorialPageBody != null)
		{
			secondPage.Select(0);
			OnTutorialPageSelected();
			GD.Print($"[Menu][TEST]   切到『{secondPage.GetText(0)}』→ 字符数=" +
				$"{_tutorialPageBody.Text.Length} 标题='{(_currentPageLabel?.Text ?? "")}'");
		}

		// 选中后详情区会重建，重新取开始按钮
		Button startAgain = null;
		if (_tutorialPage != null)
		{
			foreach (var node in _tutorialPage.FindChildren("*", "Button", true, false))
			{
				if (node is Button b && b.Text == "开始这个教程") { startAgain = b; break; }
			}
		}
		if (startAgain == null)
		{
			GD.PrintErr("[Menu][TEST] 重建详情区后找不到开始按钮。");
			return;
		}
		startAgain.EmitSignal(BaseButton.SignalName.Pressed);
	}

	// =========================================================
	// 教程面板（MenuPage.Tutorial）
	//
	// 布局：左右两栏
	//   左 = 教程列表，分「基础 / 进阶」两组（Tier 决定分组）
	//   右 = 选中教程的详情：标题 + 简介 + 目标清单 + 「开始」按钮 +
	//        **理论页浏览**（阵营总览 / 核心机制 / 单位档案 / 建筑档案 / 科技树）
	//
	// 为什么理论页要有独立浏览区，而不是塞进目标流程：
	//   目标是"必须按顺序做完的事"，理论是"可以随便翻的资料"。
	//   把图鉴塞进目标会让教程变成几十步的填空题；
	//   分开之后，玩家可以只做实操、也可以先把图鉴看一遍再打。
	//
	// 全部内容按需用代码构建（场景 .tscn 里没有这一页）。
	// =========================================================

	private VBoxContainer _tutorialDetail;
	private Tree _tutorialPageTree;
	private RichTextLabel _tutorialPageBody;
	private Label _currentPageLabel;

	private void BuildTutorialPage()
	{
		if (_tutorialPage != null) return;

		_tutorialPage = new Panel
		{
			Name = "Page_Tutorial",
			Visible = false,
		};
		// 全屏铺满父节点（与 Page_Main 同级覆盖）
		_tutorialPage.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_tutorialPage);

		var margin = new MarginContainer();
		margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 40);
		margin.AddThemeConstantOverride("margin_right", 40);
		margin.AddThemeConstantOverride("margin_top", 28);
		margin.AddThemeConstantOverride("margin_bottom", 28);
		_tutorialPage.AddChild(margin);

		var root = new VBoxContainer();
		root.AddThemeConstantOverride("separation", 10);
		margin.AddChild(root);

		var title = new Label { Text = RTS.Settings.Localization.Tr("tutorial_panel.title") };
		title.AddThemeFontSizeOverride("font_size", 26);
		root.AddChild(title);

		var hint = new Label
		{
			Text = RTS.Settings.Localization.Tr("tutorial_panel.hint"),
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
		root.AddChild(hint);
		root.AddChild(new HSeparator());

		// ---- 两栏 ----
		var split = new HBoxContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		split.AddThemeConstantOverride("separation", 20);
		root.AddChild(split);

		// 左：教程列表
		var leftScroll = new ScrollContainer
		{
			CustomMinimumSize = new Vector2(400, 0),
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		split.AddChild(leftScroll);

		var list = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		list.AddThemeConstantOverride("separation", 8);
		leftScroll.AddChild(list);

		// 先把图鉴挂上：ConfigDatabase 是图鉴的数据源，未就绪时挂出来的是空页。
		// HydrateAll 幂等，重复进面板不会重复挂。
		RTS.Tutorial.TutorialCodex.HydrateAll(RTS.Tutorial.TutorialRegistry.All);

		// 教程文案本地化：面板构建前先注入翻译函数并应用到每个定义。
		// （运行时的同一件事在 StartTutorial 里做，两处都要，因为玩家可能先看图鉴。）
		RTS.Tutorial.TutorialDefinition.Translator = key =>
			RTS.Settings.Localization.Has(key) ? RTS.Settings.Localization.Tr(key) : null;
		foreach (var td in RTS.Tutorial.TutorialRegistry.All) td.ApplyLocalization();

		// Hydrated = 已挂上图鉴的教程（图鉴要等 ConfigDatabase 就绪）
		AppendTutorialGroup(list, "基础", BasicsOnly());
		AppendTutorialGroup(list, "进阶（按阵营）", RTS.Tutorial.TutorialRegistry.Advanced());

		// 右：详情 + 理论页
		var rightScroll = new ScrollContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
		};
		split.AddChild(rightScroll);

		_tutorialDetail = new VBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_tutorialDetail.AddThemeConstantOverride("separation", 8);
		rightScroll.AddChild(_tutorialDetail);

		// ---- 底部返回 ----
		root.AddChild(new HSeparator());
		var backBtn = new Button
		{
			Text = RTS.Settings.Localization.Tr("tutorial_panel.back"),
			CustomMinimumSize = new Vector2(200, 0),
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
		};
		backBtn.Pressed += () => SwitchPage(MenuPage.Main);
		root.AddChild(backBtn);

		// 默认选中第一套（基础操作），玩家一进来就有内容可看
		var all = RTS.Tutorial.TutorialRegistry.All;
		if (all.Count > 0) ShowTutorialDetail(all[0]);

		GD.Print($"[Menu] 教程面板已建立（{all.Count} 个教程：" +
			$"基础 {BasicsOnly().Count} / 进阶 {RTS.Tutorial.TutorialRegistry.Advanced().Count}）");
	}

	/// <summary>基础教程列表（面板左栏第一组）。</summary>
	private static List<RTS.Tutorial.TutorialDefinition> BasicsOnly()
	{
		var list = new List<RTS.Tutorial.TutorialDefinition>();
		foreach (var t in RTS.Tutorial.TutorialRegistry.All)
			if (t.Tier == RTS.Tutorial.TutorialTier.Basics) list.Add(t);
		return list;
	}

	/// <summary>往列表里追加一组教程（带分组标题）。</summary>
	private void AppendTutorialGroup(VBoxContainer list, string groupTitle, List<RTS.Tutorial.TutorialDefinition> defs)
	{
		if (defs == null || defs.Count == 0) return;

		var header = new Label { Text = groupTitle };
		header.AddThemeFontSizeOverride("font_size", 18);
		header.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 1f));
		list.AddChild(header);

		foreach (var def in defs)
		{
			var btn = new Button
			{
				Text = $"{def.DisplayNameResolved}　({def.ObjectiveCount} 目标 · {def.PageCount} 资料页)",
				Alignment = HorizontalAlignment.Left,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};
			var captured = def;
			btn.Pressed += () => ShowTutorialDetail(captured);
			list.AddChild(btn);
		}
	}

	/// <summary>把选中教程的详情渲染到右栏（每次重建，避免残留上一套的内容）。</summary>
	private void ShowTutorialDetail(RTS.Tutorial.TutorialDefinition def)
	{
		if (_tutorialDetail == null || def == null) return;

		// QueueFree() 是**延迟**释放：节点要到帧末才真正离开树。
		// 只 QueueFree 的话，重建详情区后树里会同时存在"旧的开始按钮"和"新的开始按钮"，
		// FindChildren 会先撞上旧的那个 —— 表现就是"点第 N 个教程，却启动了第 1 个"。
		// 所以这里先 RemoveChild（立刻脱离树），再 QueueFree 回收。
		foreach (var child in _tutorialDetail.GetChildren())
		{
			_tutorialDetail.RemoveChild(child);
			child.QueueFree();
		}

		_currentTutorialDef = def;
		_tutorialPageTree = null;
		_tutorialPageBody = null;

		var title = new Label { Text = def.DisplayNameResolved };
		title.AddThemeFontSizeOverride("font_size", 22);
		_tutorialDetail.AddChild(title);

		if (!string.IsNullOrWhiteSpace(def.Intro))
		{
			var intro = new Label
			{
				Text = def.Intro,
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
			};
			intro.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
			_tutorialDetail.AddChild(intro);
		}

		var meta = new Label
		{
			Text = $"阵营 {def.RaceId} · {def.ObjectiveCount} 个实操目标 · {def.PageCount} 页资料",
		};
		meta.AddThemeFontSizeOverride("font_size", 12);
		meta.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 1f));
		_tutorialDetail.AddChild(meta);

		var startBtn = new Button
		{
			Text = RTS.Settings.Localization.Tr("tutorial_panel.start"),
			CustomMinimumSize = new Vector2(200, 36),
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
		};
		var captured = def;
		startBtn.Pressed += () => StartTutorial(captured);
		_tutorialDetail.AddChild(startBtn);

		// ---- 实操目标预览 ----
		_tutorialDetail.AddChild(new HSeparator());
		var objHeader = new Label { Text = RTS.Settings.Localization.Tr("tutorial_panel.objectives") };
		objHeader.AddThemeFontSizeOverride("font_size", 16);
		_tutorialDetail.AddChild(objHeader);

		for (int i = 0; i < def.Objectives.Count; i++)
		{
			var o = def.Objectives[i];
			var line = new Label
			{
				Text = $"{i + 1}. {o.Title}",
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
			};
			line.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.75f));
			_tutorialDetail.AddChild(line);

			if (!string.IsNullOrWhiteSpace(o.Detail))
			{
				var detail = new Label
				{
					Text = "    " + o.Detail,
					AutowrapMode = TextServer.AutowrapMode.WordSmart,
				};
				detail.AddThemeFontSizeOverride("font_size", 12);
				detail.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
				_tutorialDetail.AddChild(detail);
			}
		}

		if (def.PageCount == 0) return;

		// ---- 理论资料（可浏览的图鉴）----
		_tutorialDetail.AddChild(new HSeparator());
		var pageHeader = new Label { Text = RTS.Settings.Localization.Tr("tutorial_panel.codex") };
		pageHeader.AddThemeFontSizeOverride("font_size", 16);
		_tutorialDetail.AddChild(pageHeader);

		var pageHint = new Label
		{
			Text = RTS.Settings.Localization.Tr("tutorial_panel.codex_hint"),
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		pageHint.AddThemeFontSizeOverride("font_size", 12);
		pageHint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
		_tutorialDetail.AddChild(pageHint);

		// 目录用 Tree：天然支持"分组可折叠 + 自带滚动"，比手搓滚动列表省事。
		// 高度给 220：再高会把下面的正文区挤出可视范围，玩家会以为"点了没反应"。
		_tutorialPageTree = new Tree
		{
			HideRoot = true,
			CustomMinimumSize = new Vector2(0, 220),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_tutorialDetail.AddChild(_tutorialPageTree);

		var rootItem = _tutorialPageTree.CreateItem();
		var firstPageItem = (TreeItem)null;

		// metadata 里**必须用索引而不是字符串**：
		// Godot 的 Variant 在 C#↔引擎编组时把 '\0' 当字符串结束符，
		// 之前用 "标题\0分组" 编码，结果只留下标题、分组被截掉，
		// 于是 PagesIn(section) 查不到 → 正文永远是空的（这个 bug 就是这么来的）。
		var sections = def.PageSections();
		for (int si = 0; si < sections.Count; si++)
		{
			string section = sections[si];
			var pages = def.PagesIn(section);

			var sectionItem = _tutorialPageTree.CreateItem(rootItem);
			sectionItem.SetText(0, $"{section}（{pages.Count}）");
			sectionItem.Collapsed = false;

			for (int pi = 0; pi < pages.Count; pi++)
			{
				var pageItem = _tutorialPageTree.CreateItem(sectionItem);
				pageItem.SetText(0, pages[pi].Title);
				pageItem.SetMetadata(0, si * 1000 + pi);
				firstPageItem ??= pageItem;
			}
		}

		var currentLabel = new Label { Text = RTS.Settings.Localization.Tr("tutorial_panel.body") };
		currentLabel.AddThemeFontSizeOverride("font_size", 13);
		currentLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.85f, 1f));
		_tutorialDetail.AddChild(currentLabel);
		_currentPageLabel = currentLabel;

		// 正文用 PanelContainer 包一层：一是和第 1 页的"当前页标题"合成一块，
		// 二是给它一个可见边框 —— 之前正文和背景同色，玩家以为"点了没反应"。
		var bodyPanel = new PanelContainer();
		_tutorialDetail.AddChild(bodyPanel);

		_tutorialPageBody = new RichTextLabel
		{
			BbcodeEnabled = false,
			FitContent = true,
			CustomMinimumSize = new Vector2(0, 120),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			ScrollActive = false,
		};
		bodyPanel.AddChild(_tutorialPageBody);

		_tutorialPageTree.ItemSelected += OnTutorialPageSelected;

		// 默认展开第一页，避免右栏一片空白
		if (firstPageItem != null)
		{
			firstPageItem.Select(0);
			RenderTutorialPage(def, firstPageItem.GetMetadata(0).AsInt32());
		}
	}

	private void OnTutorialPageSelected()
	{
		if (_tutorialPageTree == null || _currentTutorialDef == null) return;

		var item = _tutorialPageTree.GetSelected();
		if (item == null) return;

		// 分组节点没有 metadata（默认 0 会被误当成第 0 页），
		// 所以用"文本 + 子节点数"判断：有子节点的就是分组，选中它不改正文。
		if (item.GetChildCount() > 0) return;

		RenderTutorialPage(_currentTutorialDef, item.GetMetadata(0).AsInt32());
	}

	/// <summary>
	/// 按索引渲染一页资料到正文区。
	/// 索引编码为 sectionIndex * 1000 + pageIndex（见 ShowTutorialDetail 的说明）。
	/// </summary>
	private void RenderTutorialPage(RTS.Tutorial.TutorialDefinition def, int index)
	{
		if (_tutorialPageBody == null || def == null) return;
		if (index < 0) return;

		var sections = def.PageSections();
		int si = index / 1000;
		int pi = index % 1000;
		if (si >= sections.Count) return;

		var pages = def.PagesIn(sections[si]);
		if (pi >= pages.Count) return;

		var found = pages[pi];
		var sb = new System.Text.StringBuilder();
		foreach (string line in found.Lines)
			sb.AppendLine(line);
		_tutorialPageBody.Text = sb.ToString();

		// 显示"当前正在看哪一页"，否则正文和目录之间没有视觉联系
		if (_currentPageLabel != null)
			_currentPageLabel.Text = $"正文 · {sections[si]} / {found.Title}";
	}

	/// <summary>按目录顺序取第 n 个资料页节点（0 起）。测试用。</summary>
	private TreeItem FindPageItem(int nth)
	{
		if (_tutorialPageTree == null) return null;
		var root = _tutorialPageTree.GetRoot();
		if (root == null) return null;

		int seen = 0;
		foreach (var section in root.GetChildren())
		{
			foreach (var page in section.GetChildren())
			{
				if (seen == nth) return page;
				seen++;
			}
		}
		return null;
	}

	/// <summary>取一段文本的第一行（诊断用）。</summary>
	private static string FirstLine(string text)
	{
		if (string.IsNullOrEmpty(text)) return "";
		int nl = text.IndexOf('\n');
		return nl < 0 ? text : text.Substring(0, nl);
	}

	/// <summary>当前右栏显示的教程（理论页回调需要知道是哪一套）。</summary>
	private RTS.Tutorial.TutorialDefinition _currentTutorialDef;

	private void SetupAutoLobby(int botCount)
	{
		OnSinglePlayerPressed();

		// 先占好本地玩家身份（位置 1 / Union），机器人再自动挑剩余空位
		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		if (network != null && lockstep != null)
		{
			int myPid = lockstep.LocalPlayerID;
			network.PeerTeamMap[myPid] = 1;
			network.PeerRaceMap[myPid] = "Union";
			network.PeerGroupMap[myPid] = 1;
			network.PeerObserverMap[myPid] = false;
			network.SendLobbyUpdate(1, "Union", true, 1, false);
		}

		var racesCsv = OS.GetEnvironment("AUTO_BOT_RACES") ?? "";
		var races = racesCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < System.Math.Min(botCount, 6); i++)
		{
			AddBotRow();
			if (_botRows.Count > 0)
			{
				var row = _botRows[_botRows.Count - 1];
				if (i < races.Length && races[i].Trim().Length > 0)
					row.Race = races[i].Trim();
				ApplyBotSettings();
			}
		}
		EnsureLobbyMembersAndRefresh();
		// 模拟玩家点击“准备”（无头回归）
		if (!_isLocalReady)
			OnReadyToggled();
		if (OS.GetEnvironment("AUTO_START") == "1")
			Callable.From(OnStartGamePressed).CallDeferred();
	}

	private void OpenSettings()
	{
		RTS.UI.SettingsMenu.Open(this);
	}

		private void RefreshMenuTexts()
		{
			// 场景节点（Label/Button 等）按 metadata/tr_key 刷新
			RTS.Settings.Localization.ApplySceneTranslations(this);
			RefreshDropdownTexts();
			if (_settingsButton != null)
				_settingsButton.Text = RTS.Settings.Localization.Tr("settings");
			if (BtnSinglePlayer != null)
				BtnSinglePlayer.Text = RTS.Settings.Localization.Tr("main_menu_single");
			if (BtnHost != null)
				BtnHost.Text = RTS.Settings.Localization.Tr("main_menu_host");
			if (BtnFind != null)
				BtnFind.Text = RTS.Settings.Localization.Tr("main_menu_find");
			if (BtnQuit != null)
				BtnQuit.Text = RTS.Settings.Localization.Tr("main_menu_quit");
			if (BtnRefresh != null)
				BtnRefresh.Text = RTS.Settings.Localization.Tr("menu.refresh");
			if (BtnBackToMain != null)
				BtnBackToMain.Text = RTS.Settings.Localization.Tr("back");
			if (BtnStart != null)
				BtnStart.Text = RTS.Settings.Localization.Tr("lobby.start");
			if (BtnBack != null)
				BtnBack.Text = RTS.Settings.Localization.Tr("back");
			if (BtnReady != null)
				BtnReady.Text = RTS.Settings.Localization.Tr(_isLocalReady ? "lobby.unready" : "lobby.ready");
			if (_menuCaption != null)
			{
				_menuCaption.Text = RTS.Settings.Localization.Tr("menu.tagline");
				LayoutMenuDesign();
			}
		}

		// 下拉框文案刷新（不重置选中项）。
		// 地图列表现在是动态的，所以不再按下标硬套本地化 key，
		// 而是整体重建（PopulateMapDropdown 结尾会恢复当前选中项）。
		private void RefreshDropdownTexts()
		{
			PopulateMapDropdown();
		}

		public override void _ExitTree()
		{
			RTS.Settings.Localization.LanguageChanged -= RefreshMenuTexts;
			// 必须退订 NetworkManager 信号：菜单场景在第一局开局时被释放，
			// 若仍订阅 SessionReady/LobbyUpdated，第二局开局触发 GAME_PREPARE
			// 时回调会打到已释放的控制器 → ObjectDisposedException → 直接闪退。
			if (NetworkManager.Instance != null)
			{
				NetworkManager.Instance.LobbyUpdated -= OnNetworkLobbyUpdated;
				NetworkManager.Instance.SessionReady -= OnGameSessionReady;
			}
		}

	private void UpdateVersionLabel()
	{
		if (VersionLabel == null)
			return;

		// 包完整性校验：配置表与模型都在 PCK 里才算“包完整”，
		// 避免“DLL 是新版但 PCK 是旧的”导致版号一致却缺文件。
		bool configOk = Godot.FileAccess.FileExists("res://Data/Configs/Union.tres")
			|| ResourceLoader.Exists("res://Data/Configs/Union.tres");
		bool modelOk = Godot.FileAccess.FileExists("res://ArtRes/models/Round Rover.glb")
			|| ResourceLoader.Exists("res://ArtRes/models/Round Rover.glb");

		string status = configOk && modelOk
			? RTS.Settings.Localization.Tr("menu.pkg_ok")
			: RTS.Settings.Localization.Tr("menu.pkg_missing");

		VersionLabel.Text = RTS.Settings.Localization.Tr("menu.version", GameVersion, BuildTag, status);
	}

	// =============================================================
	// Steam 玩家列表更新
	// =============================================================

	private void OnSteamPlayerListUpdated(Godot.Collections.Array members)
	{
		_cachedMembers = members;

		// 注意：
		// 玩家列表映射交给 NetworkManager 自己的 player_list_updated 连接处理，
		// 这里不再重复调用，避免信号顺序导致 _cachedMembers 尚未写入就触发刷新。
		if (_currentPage == MenuPage.Lobby)
			RefreshLobbyUI(members);
	}

	private void OnNetworkLobbyUpdated()
	{
		if (_currentPage != MenuPage.Lobby)
			return;

		EnsureLobbyMembersAndRefresh();
	}

	private void OnSteamPacketReceivedForLobbyRefresh(ulong senderId, byte[] data)
	{
		if (_currentPage != MenuPage.Lobby)
			return;

		EnsureLobbyMembersAndRefresh();
	}

	private void EnsureLobbyMembersAndRefresh()
	{
		if (_cachedMembers != null)
			RefreshLobbyUI(_cachedMembers);
	}

	// =============================================================
	// 进入游戏
	// =============================================================

	private void OnGameSessionReady()
	{
		GD.Print("[Menu] 会话已准备，正在切换游戏场景。");

		// 按房主在房间选择的地图开局
		string mapId = NetworkManager.Instance?.SelectedMapId ?? "Classic";
		bool smallMap = mapId == "1v1";

		if (smallMap && Godot.ResourceLoader.Exists("res://Scenes/main_1v1.tscn"))
		{
			GD.Print("[Menu] 房主选择 1v1 小地图，正在切换。");
			GetTree().ChangeSceneToFile("res://Scenes/main_1v1.tscn");
		}
		else if (GameScenePrefab != null)
		{
			GetTree().ChangeSceneToPacked(GameScenePrefab);
		}
		else
		{
			GD.PrintErr("[Menu] 严重错误：未绑定 GameScenePrefab！");
		}
	}

	// =============================================================
	// 下拉菜单
	// =============================================================

		private void SetupDropdowns()
	{
		PopulateMapDropdown();
	}

	/// <summary>
	/// 用地图注册表动态填充地图下拉框。
	///
	/// 之前这里是写死的三行 AddItem("Classic"/"1v1"/"Debug")，加一张地图必须改 C#。
	/// 现在改为：
	///   1. 先保留内置场景地图（它们还没有 .tres，不能丢）；
	///   2. 再追加 res://Maps/ 下所有 .tres 地图（按 MapId 排序，顺序确定）；
	///   3. item metadata 一律存 **MapId 字符串**，不再用"下标 → if/else 猜地图"。
	/// </summary>
		private void PopulateMapDropdown()
		{
			if (OptMap == null) return;

			OptMap.Clear();

			// 1) 内置场景地图
			AddMapItem("Classic", RTS.Settings.Localization.Tr("lobby.map_classic"));
			AddMapItem("1v1", RTS.Settings.Localization.Tr("lobby.map_1v1"));
			AddMapItem("Debug", RTS.Settings.Localization.Tr("lobby.map_debug"));

			// 2) res://Maps/ 下的自定义地图
			RTS.World.MapRegistry.LoadAll();
			foreach (string mapId in RTS.World.MapRegistry.SortedIds())
			{
				var data = RTS.World.MapRegistry.Get(mapId);
				string label = data != null && !string.IsNullOrWhiteSpace(data.DisplayName)
					? data.DisplayName
					: mapId;

				// 标注人数，作者一眼能看出这图适合几人
				if (data != null && data.RecommendedPlayers > 0)
					label = $"{label} ({data.RecommendedPlayers}P)";

				AddMapItem(mapId, label);
			}

			SelectMapInDropdown(NetworkManager.Instance?.SelectedMapId ?? "Classic");
		}

		private void AddMapItem(string mapId, string label)
		{
			int index = OptMap.ItemCount;
			OptMap.AddItem(label, index);
			OptMap.SetItemMetadata(index, mapId);
		}

		/// <summary>按 MapId 选中下拉项；找不到则回退到第一项（而不是硬编码下标）。</summary>
		private void SelectMapInDropdown(string mapId)
		{
			if (OptMap == null) return;

			for (int i = 0; i < OptMap.ItemCount; i++)
			{
				var meta = OptMap.GetItemMetadata(i);
				if (meta.VariantType == Variant.Type.String && meta.AsString() == mapId)
				{
					OptMap.Selected = i;
					return;
				}
			}

			if (OptMap.ItemCount > 0) OptMap.Selected = 0;
		}

	// P1-1/P1-5：统一房间界面——人类和 AI 都在玩家列表里用自己的行改种族/位置/队伍
	private void CreateBotOptions()
	{
		if (PageLobby == null)
			return;

		// 旁观者开关：全图视野、不可操控、可见所有人资源
		//
		// 宽度按内容给：原来是 460px 横跨整个面板，实测压住了左侧的"返回"
		// （Btn_Back y 173..217 vs 本控件 y 196..227，重叠 21px）——
		// 这类"控件横着长出去压到别人"正是本页反复出问题的地方。
		_obsCheck = new CheckBox
		{
			Text = RTS.Settings.Localization.Tr("observer.check"),
			Position = new Vector2(94f, 340f),   // 让开 Btn_Back 底部（274）与地图下拉（477）
			CustomMinimumSize = new Vector2(280f, 30f),
		};
		_obsCheck.Toggled += OnObserverToggled;
		PageLobby.AddChild(_obsCheck);

		_btnAddBot = new Button
		{
			Text = RTS.Settings.Localization.Tr("bot.add"),
			Position = new Vector2(94f, 432f),
			Size = new Vector2(180f, 36f)
		};
		_btnAddBot.Pressed += AddBotRow;
		PageLobby.AddChild(_btnAddBot);

		// ---- 观战席位 ----
		//
		// 观战行现在渲染进场景里的 SpectatorList（统一列表的"观战者"段），
		// 不再另开面板 —— 之前我把它塞在左列 (94,476)，
		// 压住地图下拉与"开始"键；改成右侧固定坐标后，宽窗口下又会挤出画面。
		// 这里只准备一个占位节点，由 RefreshSpectatorSeats 填内容。
		_lblSpectatorEmpty = new Label
		{
			Text = RTS.Settings.Localization.Tr("spectator.none"),
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};

		// 顶部旧下拉隐藏：统一改成“在自己的信息行里改”
		if (OptRace != null)
			OptRace.Visible = false;
		if (OptTeam != null)
			OptTeam.Visible = false;

		ApplyBotSettings();
		RefreshBotCapacity();
		RefreshSpectatorSeats();
	}

	/// <summary>当前选中地图的数据（拿不到返回 null）。</summary>
	private RTS.Data.Maps.RtsMapData GetSelectedMapData()
	{
		if (OptMap == null) return null;
		string id = null;
		if (OptMap.Selected >= 0)
		{
			var meta = OptMap.GetItemMetadata(OptMap.Selected);
			if (meta.VariantType == Variant.Type.String)
				id = meta.AsString();
		}
		if (string.IsNullOrEmpty(id) && OptMap.ItemCount > 0)
			id = OptMap.GetItemText(OptMap.Selected);
		if (string.IsNullOrEmpty(id)) return null;

		RTS.World.MapRegistry.LoadAll();
		return RTS.World.MapRegistry.Get(id);
	}

	/// <summary>
	/// 参战方上限 = min(地图 MaxPlayers, 出生点数量)。
	///
	/// 为什么要取出生点数量：地图只有 N 个出生点时，第 N+1 个参战方会没地方落，
	/// 只能和别人挤在同一个出生点（实测会出现叠在一起）。
	/// 观战者**不占**这个上限，他们单独有席位。
	/// </summary>
	private int GetCombatantCapacity()
	{
		var data = GetSelectedMapData();
		int byMap = data != null && data.MaxPlayers > 0 ? data.MaxPlayers : 8;
		int bySpawn = data?.SpawnPoints?.Count ?? 0;
		return bySpawn > 0 ? System.Math.Min(byMap, bySpawn) : byMap;
	}

	/// <summary>当前参战方数量 = 人类（自己）+ 人机。</summary>
	private int GetCombatantCount()
	{
		int humans = 0;
		var lockstep = LockstepManager.Instance;
		if (lockstep != null && lockstep.LocalPlayerID > 0)
		{
			var obs = NetworkManager.Instance?.PeerObserverMap;
			bool localIsObserver = obs != null && obs.GetValueOrDefault(lockstep.LocalPlayerID, false);
			if (!localIsObserver) humans = 1;
		}
		return humans + _botRows.Count;
	}

	/// <summary>
	/// 还能不能再加入人机。返回 false 时给出原因（用于按钮提示）。
	/// </summary>
	private bool CanAddBot(out string reason)
	{
		int cap = GetCombatantCapacity();
		int cur = GetCombatantCount();

		if (cur >= cap)
		{
			reason = RTS.Settings.Localization.Tr("bot.full", cap);
			return false;
		}
		if (_botRows.Count >= 6)
		{
			reason = RTS.Settings.Localization.Tr("bot.limit");
			return false;
		}
		reason = null;
		return true;
	}

	/// <summary>
	/// 重建观战席位列表。
	///
	/// 观战者与参战方**席位分开**：他们不占出生点、不计入 MaxPlayers、
	/// 也不需要准备确认。这里按 PeerObserverMap 列出所有观战者，
	/// 自己那条标出来，方便确认"我确实在观战席上"。
	/// </summary>
	/// <summary>往"观战者"段加一行。</summary>
	private void AddSpectatorRow(int pid, string name, bool isLocal)
	{
		if (!Alive(SpectatorList)) return;

		var row = new Label
		{
			Text = isLocal
				? RTS.Settings.Localization.Tr("spectator.seat_me", name)
				: RTS.Settings.Localization.Tr("spectator.seat", name)
		};
		if (isLocal)
			row.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 0.72f));
		SpectatorList.AddChild(row);
	}

	/// <summary>
	/// 刷新"观战者"段：更新段标题计数；无人时显示占位文案。
	/// 观战者的**行**由 RefreshLobbyUI 在遍历成员时插入
	/// （这样顺序与成员列表一致，不会两次遍历导致顺序不一致）。
	/// </summary>
	private void RefreshSpectatorSeats()
	{
		if (!Alive(SpectatorList)) return;

		int count = 0;
		foreach (var c in SpectatorList.GetChildren())
			if (GodotObject.IsInstanceValid(c) && c is Label) count++;

		// 占位行：没有人观战时显示一行说明。
		//
		// **必须用 IsInstanceValid 判空，不能用 `!= null`**：
		// Godot 对象被 QueueFree() 之后 C# 引用仍然非 null，
		// 但访问 GetParent() 会抛 ObjectDisposedException
		// （实测栈：RefreshSpectatorSeats → Node.GetParent → ObjectDisposedException）。
		bool placeholderValid = Alive(_lblSpectatorEmpty);
		var placeholderParent = placeholderValid ? _lblSpectatorEmpty.GetParent() : null;
		bool placeholderAttached = placeholderValid
			&& placeholderParent != null
			&& GodotObject.IsInstanceValid(placeholderParent);

		if (count == 0)
		{
			if (placeholderValid && !placeholderAttached)
				SpectatorList.AddChild(_lblSpectatorEmpty);
		}
		else if (placeholderAttached)
		{
			placeholderParent.RemoveChild(_lblSpectatorEmpty);
		}

		if (Alive(SpectatorSectionHeader))
		{
			SpectatorSectionHeader.Text = RTS.Settings.Localization.Tr("spectator.header_count", count);
			StyleSectionHeader(SpectatorSectionHeader);
		}
		if (Alive(PlayerSectionHeader))
		{
			PlayerSectionHeader.Text = RTS.Settings.Localization.Tr("spectator.combatants");
			StyleSectionHeader(PlayerSectionHeader);
		}
	}
	/// <summary>
	/// 安全的"节点是否还能用"判定。
	///
	/// 为什么不直接用 `!= null`：Godot 对象被 QueueFree() 之后，
	/// C# 侧的引用**仍然非 null**，但访问任何成员都会抛 ObjectDisposedException。
	/// 这个项目已经踩过两次（列表重建、观战占位行），所以统一走这里。
	/// </summary>
	private static bool Alive(GodotObject o) => GodotObject.IsInstanceValid(o);

	/// <summary>按当前容量刷新"加入人机"按钮的可用状态与提示。</summary>
	private void RefreshBotCapacity()
	{
		if (!Alive(_btnAddBot)) return;

		bool can = CanAddBot(out string reason);
		int cap = GetCombatantCapacity();
		int cur = GetCombatantCount();

		_btnAddBot.Disabled = !can;
		_btnAddBot.Text = can
			? RTS.Settings.Localization.Tr("bot.add")
			: RTS.Settings.Localization.Tr("bot.add_full");
		// 悬停提示：说清为什么不能加了（或当前 3/4 这类计数）
		_btnAddBot.TooltipText = can
			? $"{cur}/{cap}"
			: reason ?? $"{cur}/{cap}";
	}

	private void AddBotRow()
	{
		if (!CanAddBot(out string why))
		{
			GD.Print($"[Lobby] 不加入人机：{why}");
			RefreshBotCapacity();
			return;
		}

		int pid = _nextBotPid++;
		int freeSlot = 0;
		var taken = GetTakenSlots(pid);
		for (int t = 1; t <= 8; t++)
			if (freeSlot == 0 && !taken.Contains(t))
				freeSlot = t;
		_botRows.Add(new BotRowData
		{
			Pid = pid,
			Race = "Union",
			Slot = freeSlot,
			Difficulty = 2,
			Group = freeSlot > 0 ? freeSlot : 0
		});
		ApplyBotSettings();
		RefreshBotCapacity();
		Callable.From(EnsureLobbyMembersAndRefresh).CallDeferred();
	}

	private void RemoveBotRow(BotRowData row)
	{
		_botRows.Remove(row);

		// 从房间里移除（和人类退出房间一样：释放出生点）
		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		if (network != null && lockstep != null && row.Pid > 0)
		{
			lockstep.PlayerIDs.Remove(row.Pid);
			network.PeerTeamMap.Remove(row.Pid);
			network.PeerRaceMap.Remove(row.Pid);
			network.PeerGroupMap.Remove(row.Pid);
			network.PeerObserverMap.Remove(row.Pid);
			network.PeerReadyMap.Remove(row.Pid);
			network.SteamToPlayerId.Remove((ulong)row.Pid);
			network.PlayerIdToSteam.Remove(row.Pid);
		}
		ApplyBotSettings();
		RefreshBotCapacity();
		Callable.From(EnsureLobbyMembersAndRefresh).CallDeferred();
	}

	private void OnObserverToggled(bool on)
	{
		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		if (network == null || lockstep == null)
			return;

		int myPid = lockstep.LocalPlayerID;
		network.PeerObserverMap[myPid] = on;

		if (on)
		{
			// 旁观者不需要种族/出生点，直接视为已准备
			network.PeerTeamMap[myPid] = 0;
			network.PeerRaceMap[myPid] = "";
			network.PeerGroupMap[myPid] = 0;
			network.PeerReadyMap[myPid] = true;
			_isLocalReady = true;
			if (BtnReady != null)
				BtnReady.Text = RTS.Settings.Localization.Tr("lobby.unready");
		}
		else
		{
			network.PeerReadyMap[myPid] = false;
			_isLocalReady = false;
			if (BtnReady != null)
				BtnReady.Text = RTS.Settings.Localization.Tr("lobby.ready");
		}

		network.SendLobbyUpdate(
			network.PeerTeamMap.GetValueOrDefault(myPid, 0),
			network.PeerRaceMap.GetValueOrDefault(myPid, ""),
			network.PeerReadyMap.GetValueOrDefault(myPid, false),
			network.PeerGroupMap.GetValueOrDefault(myPid, 0),
			on);
		ApplyBotSettings();
		// 立即重建玩家列表，旁观者行马上变成纯状态显示
		Callable.From(EnsureLobbyMembersAndRefresh).CallDeferred();
	}

	private void ApplyBotSettings()
	{
		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		if (network == null || lockstep == null)
			return;

		// 双重保险：给机器人刷新配置时，若旁观者复选框仍勾着，重新锁回本地旁观者状态
		int myPid = lockstep.LocalPlayerID;
		if (Alive(_obsCheck) && _obsCheck.ButtonPressed)
		{
			network.PeerObserverMap[myPid] = true;
			network.PeerTeamMap[myPid] = 0;
			network.PeerRaceMap[myPid] = "";
			network.PeerGroupMap[myPid] = 0;
			network.PeerReadyMap[myPid] = true;
		}

		// 逐机器人配置（数据驱动；行 UI 由 RefreshLobbyUI 统一重建）
		var configs = new List<RTS.Core.SimManager.BotLobbyConfig>();
		var teamsCsv = new List<string>();
		foreach (var row in _botRows)
		{
			if (row.Pid <= 0 || row.Slot <= 0)
				continue;

			// 位置冲突：被其他玩家/机器人占用 → 清空本次选择（和人类选位规则一致）
			if (GetTakenSlots(row.Pid).Contains(row.Slot))
			{
				// 自动换到第一个空位，而不是清空整行（避免“改个种族其他信息全重置”）
				var taken = GetTakenSlots(row.Pid);
				int free = 0;
				for (int t = 1; t <= 8; t++)
				{
					if (!taken.Contains(t))
					{
						free = t;
						break;
					}
				}
				if (free <= 0)
					continue; // 没空位：本 tick 不注册
				row.Slot = free;
				if (row.Group <= 0)
					row.Group = free;
			}

			if (string.IsNullOrEmpty(row.Race))
				row.Race = "Union";
			if (row.Group <= 0)
				row.Group = row.Slot;
			configs.Add(new RTS.Core.SimManager.BotLobbyConfig
			{
				Pid = row.Pid,
				Team = row.Slot,
				Race = row.Race,
				Difficulty = row.Difficulty,
				Group = row.Group
			});
			teamsCsv.Add(row.Slot.ToString());

			// 即时注册为房间玩家：和人类一样出现在玩家列表、占用出生点、参与就绪判定
			if (!lockstep.PlayerIDs.Contains(row.Pid))
				lockstep.PlayerIDs.Add(row.Pid);
			network.PeerTeamMap[row.Pid] = row.Slot;
			network.PeerRaceMap[row.Pid] = row.Race;
			network.PeerGroupMap[row.Pid] = row.Group;
			network.PeerReadyMap[row.Pid] = true;
			network.PeerObserverMap[row.Pid] = false;
			network.SteamToPlayerId[(ulong)row.Pid] = row.Pid;
			network.PlayerIdToSteam[row.Pid] = (ulong)row.Pid;
		}
		lockstep.PlayerIDs.Sort();
		RTS.Core.SimManager.BotConfigsOverride = configs;
		RTS.Core.SimManager.BotTeamsOverride = teamsCsv.Count > 0 ? string.Join(",", teamsCsv) : null;
		RTS.Core.SimManager.BotDifficultyOverride = 2;
	}

	// 当前被占用的出生点（排除指定 pid 自己，用于人类/机器人共同的选位置灰）
	private HashSet<int> GetTakenSlots(int exceptPid)
	{
		var taken = new HashSet<int>();
		var network = NetworkManager.Instance;
		if (network == null)
			return taken;
		foreach (var kv in network.PeerTeamMap)
		{
			if (kv.Key != exceptPid && kv.Value > 0)
				taken.Add(kv.Value);
		}
		return taken;
	}

	private BotRowData FindBotRow(int pid)
	{
		foreach (var row in _botRows)
			if (row.Pid == pid)
				return row;
		return null;
	}

	// 统一的玩家行：人类和 AI 都在这里构建（自己的行里改信息）
	private Control BuildPlayerRow(int pid, string displayName, bool isBot, bool isLocal)
	{
		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 6);

		bool observer = network.PeerObserverMap.GetValueOrDefault(pid, false);
		// 双重保险：复选框是权威来源——只要还勾着，本地行永远按旁观者渲染，
		// 即使地图状态被任何路径重置也不会“变回可选”
		if (pid == lockstep?.LocalPlayerID && Alive(_obsCheck) && _obsCheck.ButtonPressed)
			observer = true;

		// 旁观者：只显示状态，不给任何选择控件（选不了就不会“脱离观察者”）
		if (observer)
		{
			var obsRow = new HBoxContainer();
			obsRow.AddThemeConstantOverride("separation", 6);
			var obsName = new Label
			{
				Text = displayName,
				CustomMinimumSize = new Vector2(110f, 0f),
				VerticalAlignment = VerticalAlignment.Center,
				TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
			};
			obsRow.AddChild(obsName);
			var obsLabel = new Label
			{
				Text = RTS.Settings.Localization.Tr("observer.short"),
				CustomMinimumSize = new Vector2(320f, 0f),
				VerticalAlignment = VerticalAlignment.Center
			};
			obsRow.AddChild(obsLabel);
			return obsRow;
		}

		bool editable = (isLocal && !observer) || isBot;

		var name = new Label
		{
			Text = displayName,
			CustomMinimumSize = new Vector2(110f, 0f),
			VerticalAlignment = VerticalAlignment.Center,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
		};
		row.AddChild(name);

		int curTeam = network.PeerTeamMap.GetValueOrDefault(pid, 0);
		string curRace = network.PeerRaceMap.GetValueOrDefault(pid, "");
		int curGroup = network.PeerGroupMap.GetValueOrDefault(pid, curTeam);
		// 机器人以 _botRows 数据为唯一来源，避免地图状态与数据不同步造成“重置”假象
		if (isBot)
		{
			var botData = FindBotRow(pid);
			if (botData != null)
			{
				curTeam = botData.Slot;
				curRace = botData.Race;
				curGroup = botData.Group;
			}
		}

		var race = CreateRaceDropdown(curRace);
		race.Disabled = !editable;
		row.AddChild(race);

		var slot = CreateSlotDropdown(pid, curTeam);
		slot.Disabled = !editable;
		row.AddChild(slot);

		var group = CreateGroupDropdown(curGroup);
		group.Disabled = !editable;
		row.AddChild(group);

		if (isBot)
		{
			AppendBotControls(row, pid, race, slot, group);
		}
		else
		{
			var ready = new Label
			{
				Text = RTS.Settings.Localization.Tr(
					network.PeerReadyMap.GetValueOrDefault(pid, false) ? "lobby.ready_yes" : "lobby.ready_no"),
				CustomMinimumSize = new Vector2(70f, 0f),
				VerticalAlignment = VerticalAlignment.Center
			};
			row.AddChild(ready);

			// 本地人类：改自己的信息并广播；远程玩家只读
			if (isLocal && !observer)
			{
				race.ItemSelected += _ => ApplyLocalRowChange(pid, race, slot, group);
				slot.ItemSelected += _ => ApplyLocalRowChange(pid, race, slot, group);
				group.ItemSelected += _ => ApplyLocalRowChange(pid, race, slot, group);
			}
		}

		return row;
	}

	private OptionButton CreateRaceDropdown(string selectedRace)
	{
		var race = new OptionButton { CustomMinimumSize = new Vector2(120f, 0f) };
		AddRaceChoices(race);
		int idx = 0;
		for (int i = 0; i < race.ItemCount; i++)
		{
			if (race.GetItemMetadata(i).AsString() == selectedRace)
				idx = i;
		}
		race.Selected = idx;
		return race;
	}

	// 种族下拉选项唯一来源（大厅顶部旧下拉与玩家行共用）
	private static void AddRaceChoices(OptionButton btn)
	{
		AddRaceChoice(btn, "Union");
		AddRaceChoice(btn, "Nano");
		AddRaceChoice(btn, "Demon");
		AddRaceChoice(btn, "Wanderer");
		AddRaceChoice(btn, "Terran");
		AddRaceChoice(btn, "Plant");
		AddRaceChoice(btn, "Cave");
	}

	private static void AddRaceChoice(OptionButton btn, string race)
	{
		btn.AddItem(RTS.Settings.Localization.Tr("race." + race.ToLowerInvariant()));
		btn.SetItemMetadata(btn.ItemCount - 1, race);
	}

	private OptionButton CreateSlotDropdown(int pid, int selectedTeam)
	{
		var slot = new OptionButton { CustomMinimumSize = new Vector2(110f, 0f) };
		slot.AddItem(RTS.Settings.Localization.Tr("lobby.team_prompt"), 0);
		var taken = GetTakenSlots(pid);
		for (int t = 1; t <= 8; t++)
		{
			slot.AddItem(RTS.Settings.Localization.Tr("lobby.team_slot", t), t);
			slot.SetItemDisabled(t, taken.Contains(t));
		}
		slot.Selected = selectedTeam;
		return slot;
	}

	private OptionButton CreateGroupDropdown(int selectedGroup)
	{
		var group = new OptionButton { CustomMinimumSize = new Vector2(100f, 0f) };
		group.AddItem(RTS.Settings.Localization.Tr("lobby.group_auto"), 0);
		for (int g = 1; g <= 4; g++)
			group.AddItem(RTS.Settings.Localization.Tr("lobby.group", g), g);
		group.Selected = selectedGroup;
		return group;
	}

	// 机器人专属控件：难度 + 移除 + 数据更新事件（唯一来源 = _botRows）
	private void AppendBotControls(HBoxContainer row, int pid, OptionButton race, OptionButton slot, OptionButton group)
	{
		var diff = new OptionButton { CustomMinimumSize = new Vector2(90f, 0f) };
		diff.AddItem(RTS.Settings.Localization.Tr("bot.easy"), 1);
		diff.AddItem(RTS.Settings.Localization.Tr("bot.normal"), 2);
		diff.AddItem(RTS.Settings.Localization.Tr("bot.hard"), 3);
		var botData = FindBotRow(pid);
		diff.Selected = Mathf.Clamp((botData?.Difficulty ?? 2) - 1, 0, 2);
		diff.ItemSelected += _ =>
		{
			var d = FindBotRow(pid);
			if (d != null)
			{
				d.Difficulty = diff.Selected + 1;
				ApplyBotSettings();
			}
		};
		row.AddChild(diff);

		var remove = new Button { Text = "X", CustomMinimumSize = new Vector2(28f, 0f) };
		remove.Pressed += () =>
		{
			var d = FindBotRow(pid);
			if (d != null)
				RemoveBotRow(d);
		};
		row.AddChild(remove);

		race.ItemSelected += _ =>
		{
			var d = FindBotRow(pid);
			if (d != null)
			{
				d.Race = race.GetItemMetadata(race.Selected).AsString();
				ApplyBotSettings();
				Callable.From(EnsureLobbyMembersAndRefresh).CallDeferred();
			}
		};
		slot.ItemSelected += _ =>
		{
			var d = FindBotRow(pid);
			if (d != null)
			{
				// 位置被其他玩家/机器人占用：回退到原位置，不打断其他信息
				if (slot.Selected > 0 && GetTakenSlots(pid).Contains(slot.Selected))
				{
					slot.SetBlockSignals(true);
					slot.Selected = d.Slot;
					slot.SetBlockSignals(false);
					return;
				}
				d.Slot = slot.Selected;
				if (d.Group <= 0)
					d.Group = d.Slot;
				ApplyBotSettings();
				Callable.From(EnsureLobbyMembersAndRefresh).CallDeferred();
			}
		};
		group.ItemSelected += _ =>
		{
			var d = FindBotRow(pid);
			if (d != null)
			{
				d.Group = group.Selected;
				ApplyBotSettings();
				Callable.From(EnsureLobbyMembersAndRefresh).CallDeferred();
			}
		};
	}

	private void ApplyLocalRowChange(int pid, OptionButton race, OptionButton slot, OptionButton group)
	{
		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		if (network == null || lockstep == null)
			return;
		// 防御：旁观者不能被写入队伍/位置
		if (network.PeerObserverMap.GetValueOrDefault(pid, false))
			return;

		int team = slot.Selected;
		if (team > 0 && IsTeamTakenByOther(team))
		{
			GD.Print("[Menu] 该出生点已被其他玩家选择，请换一个位置。");
			int prev = network.PeerTeamMap.GetValueOrDefault(pid, 0);
			slot.SetBlockSignals(true);
			slot.Selected = prev;
			slot.SetBlockSignals(false);
			team = prev;
		}

		string raceName = race.Selected >= 0 ? race.GetItemMetadata(race.Selected).AsString() : "";
		int grp = group.Selected > 0 ? group.Selected : team;
		bool observer = network.PeerObserverMap.GetValueOrDefault(pid, false);

		network.PeerTeamMap[pid] = team;
		network.PeerRaceMap[pid] = raceName;
		network.PeerGroupMap[pid] = grp;
		network.SendLobbyUpdate(
			team,
			raceName,
			network.PeerReadyMap.GetValueOrDefault(pid, false),
			grp,
			observer);
		// 延迟刷新：避免在信号执行中重建/释放当前控件
		Callable.From(EnsureLobbyMembersAndRefresh).CallDeferred();
	}

	private void OnLobbyOptionChanged()
	{
		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		if (network == null || lockstep == null)
			return;

		int myPid = lockstep.LocalPlayerID;
		if (network.PeerObserverMap.GetValueOrDefault(myPid, false))
			return; // 旁观者不选种族/出生点

		// 信息现在在玩家自己的行里改（BuildPlayerRow），这里只负责把当前状态广播一次
		int team = network.PeerTeamMap.GetValueOrDefault(myPid, 0);
		string race = network.PeerRaceMap.GetValueOrDefault(myPid, "");
		int group = network.PeerGroupMap.GetValueOrDefault(myPid, team);
		network.SendLobbyUpdate(team, race, _isLocalReady, group, false);
	}

	// 地图选择：只有房主能改，改动广播给全房间
	private void OnMapSelected()
	{
		if (NetworkManager.Instance == null || OptMap == null)
			return;

		if (!IsLocalHost())
			return;

		string mapId = OptMap.Selected >= 0
			? OptMap.GetItemMetadata(OptMap.Selected).AsString()
			: "Classic";

		NetworkManager.Instance.SendMapSelection(mapId);

		// 换图会改变容量（MaxPlayers / 出生点数量），
		// 必须重算"加入人机"按钮状态，否则会出现"新图已满但仍能加人机"。
		RefreshBotCapacity();
	}

	private bool IsLocalHost()
	{
		if (NetworkManager.OfflineMode)
			return true;

		if (_steamService == null)
			return false;

		try
		{
			return (bool)_steamService.Get("is_host");
		}
		catch
		{
			return false;
		}
	}

	private bool IsTeamTakenByOther(int teamId)
	{
		if (NetworkManager.Instance == null || LockstepManager.Instance == null)
			return false;

		int myPid = LockstepManager.Instance.LocalPlayerID;

		foreach (var kvp in NetworkManager.Instance.PeerTeamMap)
		{
			if (kvp.Key != myPid && kvp.Value == teamId)
				return true;
		}

		return false;
	}

	private void OnReadyToggled()
	{
		if (BtnReady == null)
			return;

		var network = NetworkManager.Instance;
		var lockstep = LockstepManager.Instance;
		if (network == null || lockstep == null)
			return;

		int myPid = lockstep.LocalPlayerID;
		bool observer = network.PeerObserverMap.GetValueOrDefault(myPid, false);
		int team = network.PeerTeamMap.GetValueOrDefault(myPid, 0);
		string race = network.PeerRaceMap.GetValueOrDefault(myPid, "");

		if (!observer && (team <= 0 || string.IsNullOrEmpty(race)))
		{
			GD.Print("[Menu] 请先选择种族和位置！");
			return;
		}

		if (!observer && IsTeamTakenByOther(team))
		{
			GD.Print("[Menu] 出生点已被占用，请重新选择！");
			return;
		}

		_isLocalReady = !_isLocalReady;

		BtnReady.Text = RTS.Settings.Localization.Tr(_isLocalReady ? "lobby.unready" : "lobby.ready");

		OnLobbyOptionChanged();
	}

	// =============================================================
	// Lobby UI 刷新
	// =============================================================

	private void RefreshLobbyUI(Godot.Collections.Array members)
	{
		// 场景切换/重建瞬间，OptionButton 可能已被释放：C# 的 != null 对已释放 GodotObject 仍为 true，
		// 必须用 IsInstanceValid 判定，否则访问 SetBlockSignals 会抛 ObjectDisposedException
		if (!GodotObject.IsInstanceValid(this) ||
			!GodotObject.IsInstanceValid(OptMap))
			return;
		// 列表容器：优先用新的统一列表；拿不到就退回旧的单容器（兼容旧场景）
		if (!GodotObject.IsInstanceValid(PlayerList))
			PlayerList = PlayerListContainer?.GetNodeOrNull<VBoxContainer>("LobbyListColumn/PlayerListScroll/PlayerList");
		if (!GodotObject.IsInstanceValid(PlayerList))
			return;

		if (NetworkManager.Instance == null || LockstepManager.Instance == null)
			return;

		OptMap.SetBlockSignals(true);

		// 两段各自清空重建（观战者进观战段，其余进参战者段）。
		//
		// **必须先 RemoveChild 再 QueueFree**：QueueFree 是延迟的，
		// 节点要到帧末才真正离开树；只 QueueFree 的话，紧随其后的
		// RefreshSpectatorSeats 会把这些"已排队但还在树里"的行也算进观战人数。
		foreach (Node child in PlayerList.GetChildren())
		{
			PlayerList.RemoveChild(child);
			child.QueueFree();
		}
		if (GodotObject.IsInstanceValid(SpectatorList))
		{
			foreach (Node child in SpectatorList.GetChildren())
			{
				SpectatorList.RemoveChild(child);
				child.QueueFree();
			}
		}

		bool allReady = true;
		bool allSelected = true;
		int playerCount = members.Count;
		bool renderedLocal = false;

		int myPid = LockstepManager.Instance.LocalPlayerID;

		foreach (Godot.Collections.Dictionary member in members)
		{
			ulong sid = 0;
			string sname = RTS.Settings.Localization.Tr("lobby.unknown_player");

			if (member.ContainsKey("id"))
				sid = (ulong)member["id"];

			if (member.ContainsKey("name"))
				sname = (string)member["name"];

			int pid = NetworkManager.Instance.SteamToPlayerId.GetValueOrDefault(sid, 0);

			int team = NetworkManager.Instance.PeerTeamMap.GetValueOrDefault(pid, 0);
			string race = NetworkManager.Instance.PeerRaceMap.GetValueOrDefault(pid, "");
			bool isReady = NetworkManager.Instance.PeerReadyMap.GetValueOrDefault(pid, false);
			bool isObserver = NetworkManager.Instance.PeerObserverMap.GetValueOrDefault(pid, false);
			bool isLocal = pid == myPid;
			if (isLocal)
				renderedLocal = true;

			if (!isObserver && (team <= 0 || race == ""))
				allSelected = false;

			if (!isObserver && !isReady)
				allReady = false;

			// 观战者不进"参战者"段：他们不占出生点、不选种族、不需要准备，
			// 放进同一个参战列表会让"他还差什么没选"的判断看着很怪。
			if (isObserver)
			{
				AddSpectatorRow(pid, sname, isLocal);
				continue;
			}

			// 统一房间界面：每个人（含自己）都在自己的行里显示/修改种族/位置/队伍
			PlayerList.AddChild(BuildPlayerRow(pid, sname, isBot: false, isLocal: isLocal));
		}

		// 机器人也作为玩家行显示（单机大厅），和人类同一套控件
		bool offlineLobby = NetworkManager.Instance != null && NetworkManager.OfflineMode;
		if (offlineLobby)
		{
			int botIndex = 1;
			foreach (var bot in _botRows)
			{
				PlayerList.AddChild(BuildPlayerRow(
					bot.Pid,
					RTS.Settings.Localization.Tr("bot.label", botIndex),
					isBot: true,
					isLocal: false));
				botIndex++;
				playerCount++;
			}

			// 单机没有 Steam 成员列表：本地玩家行必须兜底渲染（自己改自己的信息）
			if (!renderedLocal)
			{
				int team = NetworkManager.Instance.PeerTeamMap.GetValueOrDefault(myPid, 0);
				string race = NetworkManager.Instance.PeerRaceMap.GetValueOrDefault(myPid, "");
				bool ready = NetworkManager.Instance.PeerReadyMap.GetValueOrDefault(myPid, false);
				bool obs = NetworkManager.Instance.PeerObserverMap.GetValueOrDefault(myPid, false);
				if (!obs && (team <= 0 || race == ""))
					allSelected = false;
				if (!obs && !ready)
					allReady = false;
				// 本地是观战者时不能塞进参战段（否则他自己会同时出现在两段里）
				if (obs)
					AddSpectatorRow(myPid, RTS.Settings.Localization.Tr("lobby.you"), true);
				else
				{
					PlayerList.AddChild(BuildPlayerRow(
						myPid,
						RTS.Settings.Localization.Tr("lobby.you"),
						isBot: false,
						isLocal: true));
					playerCount++;
				}
			}
		}

		if (BtnStart != null)
		{
			bool isHost = IsLocalHost();

			BtnStart.Visible = isHost;

			if (isHost)
			{
				bool canStart = playerCount > 0 && allReady && allSelected;
				BtnStart.Disabled = !canStart;
			}
		}

		// 地图选择跟随房主广播：房主可改，客机只读。
		// 按 MapId 查下标（不再用"1v1→1 / Debug→2"这种下标猜测，
		// 否则一旦地图列表变成动态的，选中的就会是另一张图）。
		if (OptMap != null)
		{
			SelectMapInDropdown(NetworkManager.Instance?.SelectedMapId ?? "Classic");
			OptMap.Disabled = !IsLocalHost();
		}

		OptMap.SetBlockSignals(false);

		// P1-1/P1-5：旁观者开关/添加机器人只在单机大厅显示
		if (Alive(_obsCheck))
			_obsCheck.Visible = offlineLobby;
		if (Alive(_btnAddBot))
			_btnAddBot.Visible = offlineLobby;

		// 观战段标题/占位必须在**所有玩家行都插完之后**再刷，
		// 否则计数会少算（这里是本函数的末尾）。
		RefreshSpectatorSeats();
	}

	// =============================================================
	// 主菜单按钮
	// =============================================================

	private void OnSinglePlayerPressed()
	{
		if (NetworkManager.Instance == null)
		{
			GD.PrintErr("[Menu] NetworkManager.Instance 为空，无法进入单机模式。");
			return;
		}

		NetworkManager.Instance.BeginOfflineHost();

		SwitchPage(MenuPage.Lobby);
		ResetLocalLobbyState();

		if (BtnStart != null)
			BtnStart.Visible = true;

		// 本地假玩家列表：让大厅 UI 显示本机玩家
		var fakeMembers = new Godot.Collections.Array
		{
			new Godot.Collections.Dictionary
			{
				{ "id", 1L },
				{ "name", RTS.Settings.Localization.Tr("lobby.local_player", 1) },
				{ "is_host", true }
			}
		};
		_cachedMembers = fakeMembers;
		RefreshLobbyUI(fakeMembers);
	}

	/// <summary>
	/// 打开地图编辑器（独立场景）。
	/// 返回主菜单时会重新执行 _Ready → PopulateMapDropdown，
	/// 因此新保存的地图立刻出现在大厅的地图下拉里。
	/// </summary>
	private void OpenMapEditor()
	{
		const string editorScene = "res://Scenes/Editor/MapEditor.tscn";

		if (!Godot.ResourceLoader.Exists(editorScene))
		{
			GD.PrintErr($"[Menu] 找不到地图编辑器场景：{editorScene}");
			return;
		}

		GD.Print("[Menu] 打开地图编辑器。");
		GetTree().ChangeSceneToFile(editorScene);
	}

	/// <summary>
	/// 启动一套教程：把教程写进 SimManager.PendingTutorial，然后直接开一局单机。
	///
	/// 刻意**不走大厅**：教程是单人引导，让玩家先选机器人/种族/地图只会增加门槛。
	/// 地图由 Game 在载入时强制换成教程自带场地（见 Game.LoadActiveMap）。
	/// </summary>
	private void StartTutorial(RTS.Tutorial.TutorialDefinition def)
	{
		if (def == null || NetworkManager.Instance == null)
		{
			GD.PrintErr("[Menu] 无法开始教程：NetworkManager 为空。");
			return;
		}

		RTS.Core.SimManager.PendingTutorial = def;

		// 教程文案本地化：把翻译函数注入运行时。
		//
		// 为什么用注入而不是让 TutorialRuntime 直接调 Localization：
		// TutorialRuntime.cs 被无头测试项目源文件包含编译，那里没有 Godot 程序集，
		// 直接引用会编译不过。所以由游戏侧把函数塞进去。
		// 找不到 key 时返回 null，运行时保持中文字面量作为回退。
		RTS.Tutorial.TutorialDefinition.Translator = key =>
			RTS.Settings.Localization.Has(key) ? RTS.Settings.Localization.Tr(key) : null;
		def.ApplyLocalization();

		// 种族跟随教程定义，保证起始单位与教程文本一致
		NetworkManager.Instance.StartOfflineGameDirect(def.RaceId, def.PlayerTeam,
			RTS.Tutorial.TutorialRegistry.TutorialMapId);

		GD.Print($"[Menu] 开始教程：{def.DisplayName}（种族 {def.RaceId}）");
	}

	private void StartOfflineGameDirect()
	{
		if (NetworkManager.Instance == null)
			return;

		GD.Print("[Menu] 检测到 --offline，直接进入单机对战（Union / 位置1 / Classic）。");
		NetworkManager.Instance.StartOfflineGameDirect("Union", 1, "Classic");
	}

	private void OnHostPressed()
	{
		if (_steamService == null)
			return;

		_steamService.Call("create_lobby");

		SwitchPage(MenuPage.Lobby);
		ResetLocalLobbyState();

		if (BtnStart != null)
			BtnStart.Visible = true;
	}

	private void OnSteamLobbyJoined(long id)
	{
		SwitchPage(MenuPage.Lobby);
		ResetLocalLobbyState();

		bool isHost = false;

		if (_steamService != null)
		{
			try
			{
				isHost = (bool)_steamService.Get("is_host");
			}
			catch
			{
				isHost = false;
			}
		}

		if (BtnStart != null)
			BtnStart.Visible = isHost;
	}

	private void OnFindPressed()
	{
		if (_steamService == null)
			return;

		SwitchPage(MenuPage.RoomList);
		_steamService.Call("search_lobbies");
	}

	private void OnSteamLobbyListReceived(Godot.Collections.Array lobbies)
	{
		if (RoomContainer == null)
			return;

		foreach (Node child in RoomContainer.GetChildren())
		{
			child.QueueFree();
		}

		foreach (Godot.Collections.Dictionary lobby in lobbies)
		{
			GD.Print("[Menu] 收到大厅数据: ", lobby);

			if (RoomEntryPrefab == null)
			{
				GD.PrintErr("[Menu] RoomEntryPrefab 未绑定。");
				return;
			}

			if (RoomEntryPrefab.Instantiate() is not RoomEntry entry)
				continue;

			RoomContainer.AddChild(entry);

			ulong id = 0;

			if (lobby.ContainsKey("id"))
			{
				id = (ulong)lobby["id"];
			}
			else if (lobby.ContainsKey("lobby_id"))
			{
				id = (ulong)lobby["lobby_id"];
			}
			else
			{
				GD.PrintErr("[Menu] 大厅字典中没有 id 字段，请检查 SteamService.gd。");
				continue;
			}

			string roomName = lobby.ContainsKey("name")
				? (string)lobby["name"]
				: RTS.Settings.Localization.Tr("lobby.room_fallback", id);

			entry.Setup(id.ToString(), roomName, 0, 8);
			entry.OnJoinRequested = (idStr) =>
			{
				if (_steamService != null)
					_steamService.Call("join_lobby", ulong.Parse(idStr));
			};
		}
	}

	private void OnStartGamePressed()
	{
		if (NetworkManager.Instance == null)
		{
			GD.PrintErr("[Menu] NetworkManager.Instance 为空，无法开始游戏。");
			return;
		}

		NetworkManager.Instance.HostStartGame();
	}

	private void OnLeaveLobbyPressed()
	{
		if (_steamService != null)
			_steamService.Call("leave_lobby");

		if (NetworkManager.Instance != null)
			NetworkManager.Instance.UnlockSessionForLobby();

		SwitchPage(MenuPage.Main);
	}

	private void ResetLocalLobbyState()
	{
		_isLocalReady = false;

		if (BtnReady != null)
			BtnReady.Text = RTS.Settings.Localization.Tr("lobby.ready");

		SetupDropdowns();

		if (OptMap != null)
			OptMap.Disabled = false;
	}

	public void SwitchPage(MenuPage page)
	{
		_currentPage = page;

		if (PageMain != null)
			PageMain.Visible = page == MenuPage.Main;

		if (PageRoomList != null)
			PageRoomList.Visible = page == MenuPage.RoomList;

		if (PageLobby != null)
			PageLobby.Visible = page == MenuPage.Lobby;

		// 教程面板是按需创建的（第一次进入才建），避免主菜单 _Ready 时多铺一堆节点
		if (page == MenuPage.Tutorial)
			BuildTutorialPage();

		if (_tutorialPage != null)
			_tutorialPage.Visible = page == MenuPage.Tutorial;
	}
}
