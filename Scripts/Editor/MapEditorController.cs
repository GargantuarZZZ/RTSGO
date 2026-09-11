using System;
using System.Collections.Generic;
using Godot;
using RTS.Data.Maps;
using RTS.MapEditing;
using RTS.World;

namespace RTS.Editor
{
	// =========================================================
	// 地图编辑器（面向地图作者）
	//
	// 布局：
	//   左侧 = 地图预览画布（点击/拖拽绘制，右键擦除，滚轮缩放，中键平移）
	//   右侧 = 工具面板（地形调色板 / 程序化生成 / 出生点与实体 / 校验 / 触发器等）
	//
	// 刻意不用 TileMapLayer 做预览，而是自绘 `_Draw`：
	//   - 作者编辑的是 RtsMapData（数据是唯一真相），预览只是它的镜像；
	//   - 用 TileMapLayer 会引入"场景态 vs 数据态"两份状态，容易出现
	//     "看到的和存下去的不一致"；
	//   - 自绘还能把**校验结果直接叠在图上**（红=出生点不可达、黄=警告位置）。
	//
	// 本文件只做 UI 与交互；所有几何/校验/生成逻辑都在 MapDataOps /
	// MapGenerator / MapValidator 里，保证与游戏逻辑同源。
	// =========================================================

	public partial class MapEditorController : Control
	{
		// =========================================================
		// 状态
		// =========================================================

		private RtsMapData _map = new();
		private MapValidationReport _validation;

		private readonly List<string> _availableMapIds = new();

		/// <summary>当前画笔渲染的 source（0=草地 1=墙）。</summary>
		private int _brushSource = RtsMapData.SourceWall;
		private int _brushVariant = 0;
		private int _brushSize = 1;

		/// <summary>true = 画地形；false = 摆内容（出生点/实体）。</summary>
		private EditorTool _tool = EditorTool.Terrain;

		/// <summary>当前要摆放的实体 ID（EditorTool.Entity 时生效）。</summary>
		private string _placementEntityId = "IronOre";

		/// <summary>视图变换。</summary>
		private float _zoom = 4f;
		private Vector2 _pan = Vector2.Zero;
		private bool _panning;
		private Vector2 _panStart;
		private Vector2 _panOrigin;

		private bool _painting;
		private int _lastPaintX = int.MinValue;
		private int _lastPaintY = int.MinValue;

		/// <summary>正在拖拽的矩形起点（Shift+拖 = 画矩形）。</summary>
		private bool _rectDrag;
		private Vector2I _rectStart;
		private Vector2I _rectEnd;

		// UI 引用
		private MapPreview _preview;
		private OptionButton _optMaps;
		private LineEdit _editMapId;
		private LineEdit _editDisplayName;
		private SpinBox _spinWidth;
		private SpinBox _spinHeight;
		private RichTextLabel _log;
		private Label _status;
		private SpinBox _spinBrushSize;

		// 生成参数
		private SpinBox _genWidth, _genHeight, _genSeed, _genSpawns;
		private OptionButton _genStyle, _genSymmetry;
		private Slider _genRichness;

		public override void _Ready()
		{
			// 让本 Control 填满父节点
			SetAnchorsPreset(LayoutPreset.FullRect);

			BuildUi();
			RefreshMapList();

			// 打开编辑器时先给一张可玩的图，而不是空白地图 —— 空白图会让
			// "校验"立刻报一堆错，作者第一眼看到的是红字，体验很差。
			GenerateMap();
		}

		// =========================================================
		// UI 构建
		// =========================================================

		private void BuildUi()
		{
			var root = new HSplitContainer();
			root.SetAnchorsPreset(LayoutPreset.FullRect);
			root.SplitOffset = 760;
			AddChild(root);

			// ---------- 左：预览 ----------
			var leftBox = new VBoxContainer();
			leftBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			root.AddChild(leftBox);

			_preview = new MapPreview { Editor = this };
			_preview.SizeFlagsVertical = SizeFlags.ExpandFill;
			_preview.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_preview.CustomMinimumSize = new Vector2(360, 360);
			leftBox.AddChild(_preview);

			_status = new Label { Text = "就绪" };
			leftBox.AddChild(_status);

			// ---------- 右：面板 ----------
			var scroll = new ScrollContainer();
			scroll.CustomMinimumSize = new Vector2(380, 0);
			scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
			root.AddChild(scroll);

			var panel = new VBoxContainer();
			panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			panel.CustomMinimumSize = new Vector2(370, 0);
			scroll.AddChild(panel);

			BuildFileSection(panel);
			BuildMapSection(panel);
			BuildTerrainSection(panel);
			BuildGenerateSection(panel);
			BuildContentSection(panel);
			BuildValidateSection(panel);
			BuildLogSection(panel);
		}

		private void Section(VBoxContainer parent, string title)
		{
			parent.AddChild(new HSeparator());
			parent.AddChild(new Label { Text = title });
		}

		private void BuildFileSection(VBoxContainer panel)
		{
			Section(panel, "① 地图文件");

			var row = new HBoxContainer();
			panel.AddChild(row);
			row.AddChild(new Label { Text = "打开：" });
			_optMaps = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			_optMaps.ItemSelected += _ => OnMapPicked();
			row.AddChild(_optMaps);

			var row2 = new HBoxContainer();
			panel.AddChild(row2);
			row2.AddChild(new Label { Text = "MapId：" });
			_editMapId = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, Text = "MyMap" };
			row2.AddChild(_editMapId);

			var row3 = new HBoxContainer();
			panel.AddChild(row3);
			row3.AddChild(new Label { Text = "显示名：" });
			_editDisplayName = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, Text = "我的地图" };
			row3.AddChild(_editDisplayName);

			var btns = new HBoxContainer();
			panel.AddChild(btns);
			AddButton(btns, "新建空地图", OnNewMap);
			AddButton(btns, "保存到 res://Maps/", OnSave);
		}

		private void BuildMapSection(VBoxContainer panel)
		{
			Section(panel, "② 尺寸");

			var row = new HBoxContainer();
			panel.AddChild(row);
			row.AddChild(new Label { Text = "宽：" });
			_spinWidth = new SpinBox { MinValue = 16, MaxValue = RtsMapData.MaxSize, Step = 1, Value = 96, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			row.AddChild(_spinWidth);
			row.AddChild(new Label { Text = "高：" });
			_spinHeight = new SpinBox { MinValue = 16, MaxValue = RtsMapData.MaxSize, Step = 1, Value = 96, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			row.AddChild(_spinHeight);

			var btns = new HBoxContainer();
			panel.AddChild(btns);
			AddButton(btns, "应用尺寸", OnResize);
			AddButton(btns, "四周加墙", OnStampBorder);
		}

		private void BuildTerrainSection(VBoxContainer panel)
		{
			Section(panel, "③ 地形绘制");

			var tools = new HBoxContainer();
			panel.AddChild(tools);
			AddButton(tools, "画笔（草地）", () => SetTerrainBrush(RtsMapData.SourceGrass));
			AddButton(tools, "画笔（墙）", () => SetTerrainBrush(RtsMapData.SourceWall));
			AddButton(tools, "填充同色区", OnFloodFill);

			var row = new HBoxContainer();
			panel.AddChild(row);
			row.AddChild(new Label { Text = "笔刷大小：" });
			_spinBrushSize = new SpinBox { MinValue = 1, MaxValue = 16, Step = 1, Value = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			_spinBrushSize.ValueChanged += v => { _brushSize = (int)v; _preview.QueueRedraw(); };
			row.AddChild(_spinBrushSize);

			var row2 = new HBoxContainer();
			panel.AddChild(row2);
			row2.AddChild(new Label { Text = "贴图变体（仅外观）：" });
			var optVariant = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			for (int i = 0; i < 4; i++) optVariant.AddItem($"变体 {i}", i);
			optVariant.ItemSelected += i => { _brushVariant = (int)optVariant.GetItemId((int)i); _preview.QueueRedraw(); };
			row2.AddChild(optVariant);

			panel.AddChild(new Label
			{
				Text = "左键绘制 / 右键擦成草地 / Shift+拖 = 画矩形\n" +
					   "滚轮或 +/− 缩放（无上下限）/ 0 = 1:1 / 中键平移",
				AutowrapMode = TextServer.AutowrapMode.WordSmart
			});
		}

		private void BuildGenerateSection(VBoxContainer panel)
		{
			Section(panel, "④ 程序化生成");

			AddLabeledSpin(panel, "宽", 16, RtsMapData.MaxSize, 120, out _genWidth);
			AddLabeledSpin(panel, "高", 16, RtsMapData.MaxSize, 120, out _genHeight);
			AddLabeledSpin(panel, "随机种子", 0, int.MaxValue, 2024, out _genSeed);
			AddLabeledSpin(panel, "出生点数量", 2, 8, 2, out _genSpawns);

			var row = new HBoxContainer();
			panel.AddChild(row);
			row.AddChild(new Label { Text = "风格：" });
			_genStyle = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			foreach (MapStyle s in Enum.GetValues(typeof(MapStyle))) _genStyle.AddItem(s.ToString(), (int)s);
			row.AddChild(_genStyle);

			var row2 = new HBoxContainer();
			panel.AddChild(row2);
			row2.AddChild(new Label { Text = "对称：" });
			_genSymmetry = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			foreach (MapSymmetry s in Enum.GetValues(typeof(MapSymmetry))) _genSymmetry.AddItem(s.ToString(), (int)s);
			_genSymmetry.Selected = (int)MapSymmetry.Rotational180;
			row2.AddChild(_genSymmetry);

			var row3 = new HBoxContainer();
			panel.AddChild(row3);
			row3.AddChild(new Label { Text = "资源贫富度：" });
			_genRichness = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = 0.5, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			row3.AddChild(_genRichness);

			var btns = new HBoxContainer();
			panel.AddChild(btns);
			AddButton(btns, "生成（覆盖当前）", GenerateMap);
			AddButton(btns, "换个种子生成", () =>
			{
				_genSeed.Value += 1;
				GenerateMap();
			});
		}

		private void BuildContentSection(VBoxContainer panel)
		{
			Section(panel, "⑤ 出生点与中立物");

			var btns = new HBoxContainer();
			panel.AddChild(btns);
			AddButton(btns, "摆出生点", () => SetTool(EditorTool.Spawn));
			AddButton(btns, "摆实体", () => SetTool(EditorTool.Entity));
			AddButton(btns, "清除模式（右键点删）", () => SetTool(EditorTool.Erase));

			var row = new HBoxContainer();
			panel.AddChild(row);
			row.AddChild(new Label { Text = "实体 ID：" });
			var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, Text = _placementEntityId };
			edit.TextChanged += t => _placementEntityId = t;
			row.AddChild(edit);

			var quick = new HBoxContainer();
			panel.AddChild(quick);
			foreach (string id in new[] { "IronOre", "GasSpring", "Tower", "Shrine" })
			{
				string captured = id;
				AddButton(quick, id, () =>
				{
					_placementEntityId = captured;
					SetTool(EditorTool.Entity);
				});
			}

			panel.AddChild(new Label { Text = "提示：出生点会自动按顺序分配槽位号。" });
		}

		private void BuildValidateSection(VBoxContainer panel)
		{
			Section(panel, "⑥ 校验");
			var btns = new HBoxContainer();
			panel.AddChild(btns);
			AddButton(btns, "校验地图", RunValidation);
			AddButton(btns, "清除标记", () => { _validation = null; _preview.QueueRedraw(); });
		}

		private void BuildLogSection(VBoxContainer panel)
		{
			Section(panel, "⑦ 输出");
			_log = new RichTextLabel
			{
				CustomMinimumSize = new Vector2(0, 240),
				ScrollActive = true,
				BbcodeEnabled = false,
			};
			panel.AddChild(_log);
		}

		private void AddButton(Container parent, string text, Action onClick)
		{
			var b = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			b.Pressed += onClick;
			parent.AddChild(b);
		}

		private void AddLabeledSpin(VBoxContainer panel, string label, double min, double max, double value, out SpinBox spin)
		{
			var row = new HBoxContainer();
			panel.AddChild(row);
			row.AddChild(new Label { Text = label + "：" });
			spin = new SpinBox { MinValue = min, MaxValue = max, Step = 1, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			row.AddChild(spin);
		}

		private void Print(string message)
		{
			_log?.AddText(message + "\n");
			_status.Text = message;
		}

		// =========================================================
		// 文件操作
		// =========================================================

		private void RefreshMapList()
		{
			if (_optMaps == null) return;

			_optMaps.Clear();
			_availableMapIds.Clear();

			MapRegistry.LoadAll(forceReload: true);
			foreach (string id in MapRegistry.SortedIds())
			{
				var data = MapRegistry.Get(id);
				_availableMapIds.Add(id);
				_optMaps.AddItem(data?.DisplayName is { Length: > 0 } n ? $"{n} ({id})" : id, _availableMapIds.Count - 1);
			}

			if (_availableMapIds.Count == 0)
				Print("res://Maps/ 下还没有地图。先点『生成』做一张，再『保存』。");
		}

		private void OnMapPicked()
		{
			int idx = _optMaps.Selected;
			if (idx < 0 || idx >= _availableMapIds.Count) return;

			string id = _availableMapIds[idx];
			var data = MapRegistry.Get(id);
			if (data == null) { Print($"打开失败：{id}"); return; }

			_map = data;
			SyncUiFromMap();
			RunValidation();
			_preview.QueueRedraw();
			Print($"已打开地图：{id}");
		}

		private void SyncUiFromMap()
		{
			_editMapId.Text = _map.MapId;
			_editDisplayName.Text = _map.DisplayName;
			_spinWidth.Value = _map.Width;
			_spinHeight.Value = _map.Height;
			// 刻意**不**重置缩放：作者调整好的视角不应该因为"打开/生成另一张地图"被改掉。
			// 需要看全图时用滚轮缩出去，需要看细节时放大，视角完全由作者掌握。
			_preview.QueueRedraw();
		}

		private void OnNewMap()
		{
			int w = (int)_spinWidth.Value;
			int h = (int)_spinHeight.Value;
			_map = new RtsMapData { MapId = _editMapId.Text, DisplayName = _editDisplayName.Text };
			_map.Resize(w, h);
			_map.Fill(RtsMapData.SourceGrass);
			MapDataOps.StampBorder(_map, 2);
			_validation = null;
			SyncUiFromMap();
			Print($"新建地图 {w}x{h}");
		}

		private void OnSave()
		{
			_map.MapId = _editMapId.Text?.Trim() ?? "";
			_map.DisplayName = _editDisplayName.Text?.Trim() ?? "";

			// 保存前先校验：坏图不应该进仓库。有错误就拒绝保存并说明原因。
			var report = MapValidator.Validate(_map);
			_validation = report;
			_preview.QueueRedraw();

			if (!report.IsValid)
			{
				Print("校验未通过，已阻止保存：");
				Print(report.ToText());
				return;
			}

			_map.LastValidationReport = report.ToText();

			if (MapRegistry.SaveToMapsDirectory(_map, out string error))
			{
				Print($"已保存：res://Maps/{_map.MapId}.tres");
				RefreshMapList();
			}
			else
			{
				Print($"保存失败：{error}");
			}
		}

		// =========================================================
		// 地图操作
		// =========================================================

		private void OnResize()
		{
			_map.Resize((int)_spinWidth.Value, (int)_spinHeight.Value);
			Print($"尺寸改为 {_map.Width}x{_map.Height}（保留重叠区域）");
			_preview.QueueRedraw();
		}

		private void OnStampBorder()
		{
			MapDataOps.StampBorder(_map, 2);
			Print("已在四周加 2 格墙");
			_preview.QueueRedraw();
		}

		private void SetTerrainBrush(int source)
		{
			_brushSource = source;
			_tool = EditorTool.Terrain;
			Print($"画笔：{(source == RtsMapData.SourceWall ? "墙" : "草地")}，大小 {_brushSize}");
			_preview.QueueRedraw();
		}

		private void SetTool(EditorTool tool)
		{
			_tool = tool;
			Print($"工具切换：{tool}");
			_preview.QueueRedraw();
		}

		private void OnFloodFill()
		{
			// 从地图中心开始洪水填充（作者最常想"把外面这片草地全变墙"）
			int cx = _map.Width / 2;
			int cy = _map.Height / 2;
			int target = RtsMapData.DecodeSource(_map.Terrain[_map.Index(cx, cy)]);
			int changed = MapDataOps.FloodFill(_map, cx, cy, target, _brushSource, _brushVariant);
			Print($"洪水填充：从中心 ({cx},{cy}) 改了 {changed} 格");
			_preview.QueueRedraw();
		}

		private void GenerateMap()
		{
			var settings = new MapGenSettings
			{
				Width = _genWidth != null ? (int)_genWidth.Value : 120,
				Height = _genHeight != null ? (int)_genHeight.Value : 120,
				Seed = _genSeed != null ? (int)_genSeed.Value : 2024,
				Style = _genStyle != null ? (MapStyle)(int)_genStyle.GetItemId(_genStyle.Selected) : MapStyle.Open,
				Symmetry = _genSymmetry != null ? (MapSymmetry)(int)_genSymmetry.GetItemId(_genSymmetry.Selected) : MapSymmetry.Rotational180,
				SpawnPointCount = _genSpawns != null ? (int)_genSpawns.Value : 2,
				Richness = _genRichness != null ? (float)_genRichness.Value : 0.5f,
				NeutralTowerCount = 4,
				ShrineCount = 2,
			};

			_map = MapGenerator.Generate(settings, out var report);
			_map.MapId = _editMapId.Text?.Trim() is { Length: > 0 } id ? id : "Generated";
			_map.DisplayName = _editDisplayName.Text?.Trim() is { Length: > 0 } n ? n : "生成的地图";

			SyncUiFromMap();
			RunValidation();
			_preview.QueueRedraw();

			Print($"生成完成：{report.WallCells} 墙 / {report.FloorCells} 地面 / " +
				  $"{report.SpawnPointsPlaced} 出生点 / {report.ResourcesPlaced} 资源 / " +
				  $"{report.NeutralStructuresPlaced} 中立物（种子 {settings.Seed}）");
		}

		private void RunValidation()
		{
			_validation = MapValidator.Validate(_map);
			_preview.QueueRedraw();

			Print(_validation.IsValid
				? $"校验通过（{_validation.WarningCount} 条警告）"
				: $"校验失败：{_validation.ErrorCount} 个错误，{_validation.WarningCount} 条警告");

			foreach (var issue in _validation.Issues)
				Print("  " + issue);
		}

		// =========================================================
		// 预览交互
		// =========================================================

		internal RtsMapData Map => _map;
		internal MapValidationReport Validation => _validation;
		internal EditorTool Tool => _tool;
		internal float Zoom => _zoom;
		internal Vector2 Pan => _pan;

		/// <summary>资源类实体用不同颜色画（与中立建筑区分）。</summary>
		internal static bool IsResourceId(string id) => MapGenerator.IsResourceEntity(id);

		internal Vector2I ScreenToCell(Vector2 screen)
		{
			var local = (screen - _pan) / _zoom;
			float cx = local.X / RtsMapData.TileSize;
			float cy = local.Y / RtsMapData.TileSize;

			// 缩到极小时（无上限缩放）格坐标会越出 int 范围 —— Godot 的
			// float→int 转换在溢出时行为未定义，这里夹到一个足够大的安全范围
			// 让它"落在很远的地图外"，后续 InBounds 判定自然会拒绝。
			// 这是数值安全，不是缩放限制。
			const float SafeBound = 1e7f;
			return new Vector2I(
				(int)Mathf.Clamp(cx, -SafeBound, SafeBound),
				(int)Mathf.Clamp(cy, -SafeBound, SafeBound));
		}

		internal void OnPreviewGuiInput(InputEvent e)
		{
			switch (e)
			{
				case InputEventKey key when key.Pressed && !key.Echo:
					HandleKey(key);
					break;
				case InputEventMouseButton mb:
					HandleMouseButton(mb);
					break;
				case InputEventMouseMotion mm when _panning:
					_pan = _panOrigin + (mm.Position - _panStart);
					_preview.QueueRedraw();
					break;
				case InputEventMouseMotion mm when _painting:
					PaintLineScreen(mm.Position);
					break;
			}
		}

		/// <summary>
		/// 键盘缩放（+ / -）：滚轮每格倍率固定，想精确定到某个大小用键盘更可控。
		/// 同样**没有上下限**，只受数值安全约束。
		/// </summary>
		private void HandleKey(InputEventKey key)
		{
			switch (key.Keycode)
			{
				case Key.Plus:
				case Key.Equal:
				case Key.KpAdd:
					ZoomAt(_preview.Size * 0.5f, 1.25f);
					break;

				case Key.Minus:
				case Key.KpSubtract:
					ZoomAt(_preview.Size * 0.5f, 1f / 1.25f);
					break;

				// 1:1（一个格子 = 64 像素），画像素级细节时方便对齐
				case Key.Key0:
					_zoom = 1f;
					_preview.QueueRedraw();
					break;
			}
		}

		private void HandleMouseButton(InputEventMouseButton mb)
		{
			var cell = ScreenToCell(mb.Position);

			switch (mb.ButtonIndex)
			{
				case MouseButton.WheelUp when mb.Pressed:
					ZoomAt(mb.Position, 1.15f);
					break;

				case MouseButton.WheelDown when mb.Pressed:
					ZoomAt(mb.Position, 1f / 1.15f);
					break;

				case MouseButton.Middle:
					_panning = mb.Pressed;
					if (mb.Pressed) { _panStart = mb.Position; _panOrigin = _pan; }
					break;

				case MouseButton.Left:
					if (mb.Pressed)
					{
						if (Input.IsKeyPressed(Key.Shift))
						{
							_rectDrag = true;
							_rectStart = cell;
							_rectEnd = cell;
						}
						else if (_tool == EditorTool.Terrain)
						{
							_painting = true;
							_lastPaintX = int.MinValue;
							PaintLineScreen(mb.Position);
						}
						else
						{
							ApplyContentTool(cell);
						}
					}
					else
					{
						if (_rectDrag) { CommitRect(); }
						_painting = false;
					}
					break;

				case MouseButton.Right when mb.Pressed:
					if (_tool == EditorTool.Erase) EraseAt(cell);
					else PaintBrush(cell, RtsMapData.SourceGrass);
					break;
			}

			_preview.QueueRedraw();
		}

		/// <summary>
		/// 以鼠标位置为锚点缩放。
		///
		/// **不做任何倍率上下限**：作者需要能一路放大到单格铺满屏幕（画细节），
		/// 也要能缩到整张 256×256 地图一屏看完（看整体布局与对称性）。
		/// 固定区间（曾经是 0.5~32）在两端都会挡住这两种用法。
		///
		/// 唯一的边界是"不能变成 0 或无穷"：0 会让 ScreenToCell 除零，
		/// 无穷会让 tile 尺寸溢出。所以只挡非有限值与下溢。
		/// </summary>
		private void ZoomAt(Vector2 screenPos, float factor)
		{
			float old = _zoom;
			float target = _zoom * factor;

			// 只做"数值安全"下限，不是设计上的缩放限制
			if (!float.IsFinite(target) || target <= 1e-4f)
				return;

			_zoom = target;

			// 以鼠标位置为锚点缩放，手感才自然
			_pan = screenPos - (screenPos - _pan) * (_zoom / old);
			_preview.QueueRedraw();
		}

		private void PaintLineScreen(Vector2 screenPos)
		{
			var cell = ScreenToCell(screenPos);
			if (cell.X == _lastPaintX && cell.Y == _lastPaintY) return;

			if (_lastPaintX != int.MinValue)
				MapDataOps.StampLine(_map, _lastPaintX, _lastPaintY, cell.X, cell.Y, _brushSource, _brushVariant, _brushSize);
			else
				PaintBrush(cell, _brushSource);

			_lastPaintX = cell.X;
			_lastPaintY = cell.Y;
			_preview.QueueRedraw();
		}

		private void PaintBrush(Vector2I cell, int source)
		{
			MapDataOps.StampBrush(_map, cell.X, cell.Y, source, _brushVariant, _brushSize);
		}

		private void CommitRect()
		{
			_rectDrag = false;
			MapDataOps.StampRect(_map, _rectStart.X, _rectStart.Y, _rectEnd.X, _rectEnd.Y, _brushSource, _brushVariant, filled: true);
			Print($"矩形填充 ({_rectStart.X},{_rectStart.Y}) → ({_rectEnd.X},{_rectEnd.Y})");
		}

		private void ApplyContentTool(Vector2I cell)
		{
			if (!_map.InBounds(cell.X, cell.Y)) { Print("超出地图范围"); return; }

			if (_tool == EditorTool.Spawn)
			{
				if (!_map.IsWalkableCell(cell.X, cell.Y)) { Print("出生点不能在墙里"); return; }

				int nextSlot = 1;
				foreach (var s in _map.SpawnPoints)
					if (s.TeamSlot >= nextSlot) nextSlot = s.TeamSlot + 1;

				_map.SpawnPoints.Add(new MapSpawnPoint { TeamSlot = nextSlot, GridX = cell.X, GridY = cell.Y });
				Print($"新增出生点 {nextSlot} @({cell.X},{cell.Y})");
			}
			else if (_tool == EditorTool.Entity)
			{
				_map.Entities.Add(new MapEntityPlacement
				{
					EntityId = _placementEntityId,
					Owner = _placementEntityId == "Tower" ? MapEntityOwner.HostileNeutral : MapEntityOwner.Neutral,
					GridX = cell.X,
					GridY = cell.Y,
					ScatterResources = _placementEntityId == "Tower",
				});
				Print($"摆放 {_placementEntityId} @({cell.X},{cell.Y})");
			}

			// 内容变了，旧校验结论作废
			_validation = null;
		}

		private void EraseAt(Vector2I cell)
		{
			// 优先删内容；没有内容就把地形擦成草地
			for (int i = _map.Entities.Count - 1; i >= 0; i--)
			{
				if (_map.Entities[i].GridX == cell.X && _map.Entities[i].GridY == cell.Y)
				{
					Print($"删除实体 {_map.Entities[i].EntityId} @({cell.X},{cell.Y})");
					_map.Entities.RemoveAt(i);
					_validation = null;
					return;
				}
			}

			for (int i = _map.SpawnPoints.Count - 1; i >= 0; i--)
			{
				if (_map.SpawnPoints[i].GridX == cell.X && _map.SpawnPoints[i].GridY == cell.Y)
				{
					Print($"删除出生点 {_map.SpawnPoints[i].TeamSlot} @({cell.X},{cell.Y})");
					_map.SpawnPoints.RemoveAt(i);
					_validation = null;
					return;
				}
			}

			PaintBrush(cell, RtsMapData.SourceGrass);
		}
	}

	public enum EditorTool
	{
		Terrain = 0,
		Spawn = 1,
		Entity = 2,
		Erase = 3,
	}

	// =========================================================
	// 预览画布：把 MapData 直接画出来，并叠加校验标记
	// =========================================================

	public partial class MapPreview : Control
	{
		internal MapEditorController Editor;

		private static readonly Color GrassColor = new(0.28f, 0.42f, 0.22f);
		private static readonly Color WallColor = new(0.42f, 0.40f, 0.38f);
		private static readonly Color GridColor = new(0f, 0f, 0f, 0.12f);
		private static readonly Color SpawnColor = new(0.2f, 0.7f, 1f);
		private static readonly Color ResourceColor = new(0.9f, 0.8f, 0.2f);
		private static readonly Color NeutralColor = new(0.9f, 0.4f, 0.2f);
		private static readonly Color ErrorColor = new(1f, 0.2f, 0.2f, 0.85f);
		private static readonly Color WarnColor = new(1f, 0.85f, 0.2f, 0.75f);

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Stop;
			FocusMode = FocusModeEnum.All;
		}

		public override void _GuiInput(InputEvent @event)
		{
			Editor?.OnPreviewGuiInput(@event);
		}

		public override void _Draw()
		{
			if (Editor?.Map == null || !Editor.Map.HasTerrain) return;

			var map = Editor.Map;
			float zoom = Editor.Zoom;
			Vector2 pan = Editor.Pan;
			float tile = RtsMapData.TileSize * zoom;

			// 1) 地形
			for (int y = 0; y < map.Height; y++)
			{
				for (int x = 0; x < map.Width; x++)
				{
					bool wall = map.IsWallCell(x, y);
					var rect = new Rect2(pan + new Vector2(x * tile, y * tile), new Vector2(tile, tile));
					DrawRect(rect, wall ? WallColor : GrassColor, filled: true);
				}
			}

			// 2) 网格（缩得太小时不画，否则一片糊）
			if (tile >= 6f)
			{
				for (int x = 0; x <= map.Width; x++)
					DrawLine(pan + new Vector2(x * tile, 0), pan + new Vector2(x * tile, map.Height * tile), GridColor, 1f);
				for (int y = 0; y <= map.Height; y++)
					DrawLine(pan + new Vector2(0, y * tile), pan + new Vector2(map.Width * tile, y * tile), GridColor, 1f);
			}

			// 3) 建造禁区（半透明黑）
			if (map.BuildBlocked != null && map.BuildBlocked.Length == map.Width * map.Height)
			{
				var banColor = new Color(0f, 0f, 0f, 0.25f);
				for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width; x++)
						if (map.BuildBlocked[map.Index(x, y)] != 0)
							DrawRect(new Rect2(pan + new Vector2(x * tile, y * tile), new Vector2(tile, tile)), banColor, true);
			}

			// 4) 实体
			foreach (var e in map.Entities)
			{
				var c = pan + new Vector2(e.GridX * tile + tile * 0.5f, e.GridY * tile + tile * 0.5f);
				var color = MapEditorController.IsResourceId(e.EntityId) ? ResourceColor : NeutralColor;
				DrawCircle(c, Mathf.Max(2f, tile * 0.32f), color);
			}

			// 5) 出生点
			foreach (var s in map.SpawnPoints)
			{
				var c = pan + new Vector2(s.GridX * tile + tile * 0.5f, s.GridY * tile + tile * 0.5f);
				DrawCircle(c, Mathf.Max(3f, tile * 0.45f), SpawnColor);
				DrawArc(c, Mathf.Max(3f, tile * 0.45f), 0, Mathf.Tau, 24, Colors.Black, 1.5f);
			}

			// 6) 校验标记（叠在最上层，作者能直接看到问题在哪一格）
			var report = Editor.Validation;
			if (report != null)
			{
				foreach (var issue in report.Issues)
				{
					if (issue.GridX < 0) continue;
					var rect = new Rect2(pan + new Vector2(issue.GridX * tile, issue.GridY * tile), new Vector2(tile, tile));
					DrawRect(rect, issue.Severity == MapIssueSeverity.Error ? ErrorColor : WarnColor, false, 2f);
				}
			}
		}
	}
}
