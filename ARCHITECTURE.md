# RTS Arcade 架构文档

> 本文档描述 `D:\DEV\RTSarcade` 的整体架构：核心系统、对象实现、接口约定、如何新增内容，以及开发时必须遵守的规范与常见坑。
> 需要“某个类在哪个文件、干什么、有哪些公开接口”时，直接查文末的 **附录 E：全代码库类索引**（覆盖 `Scripts/` 全部 173 个文件）。
> 配套文档：[ROADMAP.md](./ROADMAP.md)（商业化清单）、[ArtRes/models/README.md](./ArtRes/models/README.md)（AI 建模流水线）、`D:\DEV\README.md`（通用流水线）。

## 1. 总览

- 引擎：Godot 4.5.1 Mono（C#），GodotSteam（联机大厅），无 Steam 时可用 `--offline` / `--single` 启动单机。
- 玩法：多人实时对战 RTS，固定 20Hz 锁步逻辑帧，定点数（Fix64）确定性模拟，表现层 3D 渲染。
- 五个种族：联合（Union）、纳米虫（Nano）、恶魔（Demon）、多足机械（Wanderer）、泰伦（Terran）。
- 三个 Autoload 单例：`SimManager`（模拟核心）、`NetworkManager`（网络/大厅）、`LockstepManager`（锁步调度）。

## 2. 目录结构

```
Scripts/
  Core/                  SimManager（模拟主循环）、分族 AI（SimManager.BotAI.Core/Combat.cs：
                         BotBrain 每队状态 + 调度/经济/建造/生产/科技 + 战斗/扩张/种族）、
                         SimManager.Systems、GameEventBus（事件总线）、FogOfWar、
                         EntitySpawner、EntityFactory3D、GameFX3D、DeathVisuals、
                         SimEventQueue、TechEffects、UnitModelLibrary、UnitVatRenderer…
  Data/                  IEntity 接口、EntityExtensions、ResourceCostConfig
  Data/Configs/          UnitConfig / StructureConfig / WeaponConfig / TechConfig /
                         RaceConfig / BuffConfig / EntityConfig / ConfigDatabase
  Data/Enums/            GameEnums、ResourceType
  Menu/                  MainMenuController（大厅/房间/设置）
  Network/               NetworkManager、LockstepManager、NetAction
  Player/                Player、PlayerController、PlayerData
  Race/                  RaceData、ConfiguredRace、各族的轻量派生
  Settings/              GameSettings、Localization
  Simulation/            纯逻辑层：SimWorld(+Tick/Hashes)、SimEntity、SimUnit、
                         SimStructure、SimPathfinder、SimSpatialGrid、SimRandom、
                         SimProjectile(文件名为 SimPojectile.cs，历史拼写)、
                         SimProjectileFactory、ProjectileSpec、SimShrine、SimCreepGrid
  Simulation/Behaviors/  HeroSkillBehavior（英雄技能确定性逻辑）
  Simulation/Combat/     DamageResolver、BuffContainer
  Simulation/Math/       FPVector2（定点向量）
  Test/                  压测场景脚本（StressTest*）
  Tools/                 预制体导出/模型检查/三视图渲染/回放录制校验/VAT 烘焙
  UI/                    ActionPanel、SelectionInfoPanel、ProductionQueueUI、
                         UserUI(聊天/信号/旁观者面板)、NanoPanelUI、OrbitalPanelUI、
                         VoteUI、SettingsMenu、Minimap、HealthBarBatchRenderer…
  Units/                 Unit、Structure、ResourceStructure、SelectionRing3D
  Units/Actions/         UnitAction、UnitActionController、ActionLayer、ActionQueueManager
  Units/Actions/Implementations/  全部指令：Move/Attack/AttackMove/Harvest/Build/
                         Train/Research/Deploy/Garrison/Repair/Stim/OrbitalStrike/…
  Units/Modules/         UnitLife、UnitCombat、UnitVisuals、UnitHarvest、WorkerBuildBehavior、
                         AutoHarvestBehavior、AutoBuildBehavior、HarvesterMiningFX…
  Units/Weapons/         Weapon 基类 + ProjectileWeapon/Rifle + HomingProjectile
  World/                 Game（开局生成）、MapGrid、WorldScanner、EntitySpawner 辅助

Scenes/
  main.tscn / main_1v1.tscn / main_debug.tscn   三种对战地图
  Menu/UI_MainMenu.tscn                          主菜单/房间/大厅
  Races/<Race>/Units|Structures/*.tscn          各族的单位/建筑预制体
  Test/*.tscn                                    压测与 AI 演示场景
  Tools/*.tscn                                   开发工具场景

Data/Configs/*.tres                              全部静态配置（单位/建筑/武器/科技/种族/Buff）
Localization/*.csv                               本地化文案
```

> 注：`Scripts/Content`、`Scripts/Diagnostics`、`Scripts/Lockstep`、`Scripts/Math`、`Scripts/Presentation` 是历史遗留空目录（无文件），可以忽略或删除。

## 3. 核心架构：双层实体

整个游戏围绕“逻辑层 / 表现层”双层实体：

| 层 | 类型 | 职责 |
|----|------|------|
| 逻辑层（确定性） | `SimEntity` / `SimUnit` / `SimStructure` / `SimProjectile` | 位置、血量、资源、路径、冷却、弹药、Buff、碰撞。纯 C# + Fix64，不碰 Godot API |
| 表现层（渲染/输入） | `Unit` / `Structure`（实现 `IEntity`） | 节点树、模型、动画、血条、动作面板数据源；只做镜像与表现 |

- `SimManager.Instance.EntityNodes`（ConcurrentDictionary）负责 `SimUnitID → IEntity 节点` 的双向查找；模拟线程通过 `FindEntityById` 拿节点，但**只允许读取节点上的“缓存字段/模块引用”，禁止调用 Godot API**。
- 逻辑层是唯一真相：表现层 `_Process` 只是“平滑追赶”逻辑坐标；批量渲染模式下按 20Hz 直接吸附。
- 单位死亡时：逻辑层先标记 `IsDead` 并从 `EntityNodes` 注销（模拟线程安全），表现层通过 `SimEventQueue.EnqueueMain` 延迟播放下沉动画，避免 tick 期间释放节点导致 AccessViolation。

### IEntity 接口（`Scripts/Data/IEntity.cs`）

```csharp
public interface IEntity
{
    SimEntity LogicEntity { get; }   // 确定性模拟数据（唯一可用于距离/伤害判定的数据）
    int TeamID { get; set; }
    Vector2 GlobalPosition { get; }  // 仅供 UI/特效/音效读取，严禁用于距离判定！
    bool IsStructure { get; }
    Vector2I GridPosition { get; }
    bool IsDeadOrNull();
    float VisionRange { get; }
    string DisplayName { get; }
    Texture2D Icon { get; }
    List<EntityAction> GetAvailableActions();
    UnitLife LifeModule { get; }
    UnitVisuals VisualsModule { get; }
    UnitCombat CombatModule { get; }
    UnitHarvest HarvestModule { get; }
    UnitActionController Brain { get; }
}
```

## 4. 确定性锁步与线程模型

### 主循环

- 逻辑帧率固定 **20Hz**（`SimManager._fixedDeltaFloat = 0.05`），一个 tick 内按固定顺序执行：结构模块 → 单位模块 → BotAI → 恶魔立场/光环 → 自动生产 → 死亡检查 → `World.Tick()`（移动/碰撞/弹道）→ 研究 → 纳米经济 → 采集 → 泰伦驻扎 → 纳米光环。
- **模拟线程**持有 `SimManager.WorldLock` 执行 `ExecuteOneTick`；完成后通知主线程派发 `SimEventQueue` 的事件与延迟动作，主线程派发完毕才放行下一个 tick（屏障协议）。
- 倍速（演示/回放/离线）：`SimulationSpeed` 1/2/4/8，放大模拟时间流速并同步放大单轮 tick 上限；联机强制 1x。

### 确定性保证

- 定点数：`FixMath.NET.Fix64` + `FPVector2`；禁止在逻辑层使用 `float` 随机、`DateTime`、`GD.Rand*`。
- `SimRandom`：固定种子、确定性序列，供地图生成/特效采样使用。
- 哈希：`SimWorld.Hashes` 输出逐 tick 状态哈希，用于回放校验与双端对拍。
- 回放：`LockstepManager` 支持录制/回放（环境变量 `RECORD_REPLAY` / `REPLAY_FILE` / `REPLAY_DETERMINISTIC`），`Scenes/Tools/ReplayAutoRecord|Verify` 是配套工具。
- 双世界对拍：`Arena` 工程（`D:\DEV\Arena`）的 `SimulationSelfTest` 跑 600 帧确定性 + 全种族指令矩阵；`Tools/run_ci.ps1` 一键 CI。

### 线程安全红线

1. 模拟线程内**禁止**：`GetNode/GetNodeOrNull`、`CreateTween`、`QueueFree`、`GlobalPosition`、发射 Godot 信号。
2. 跨线程视觉/节点操作必须走 `SimEventQueue.EnqueueMain(Action)`。
3. 主线程遍历 `World.Units/Structures` 必须 `lock (SimManager.Instance.WorldLock)`。
4. 表现层拿到 `IEntity` 后，读取其模块/位置前先 `GodotObject.IsInstanceValid(node)` 或 `IsDeadOrNull()`（单位可能在选中/弹道飞行途中死亡释放）。
5. 锁步指令只通过 `LockstepManager.SendAction(NetAction)`，绝不直接改模拟状态。

## 5. 实体生成：EntitySpawner / EntityFactory3D / 预制体

- `EntitySpawner.SpawnEntity(name, teamId, pos)` 是唯一入口：先查 `Scenes/Races/**` 下同名 `.tscn` 预制体（可编辑场景），没有或实例化失败则回退 `EntityFactory3D.TryCreate`（代码工厂）。
- 预制体只需摆“结构/枪口/模块”，数值一律由配置表覆盖：`Unit._Ready` → `ApplyConfigFromTableIfNeeded` → `EntityFactory3D.ApplyUnitStats`（数值）+ 补缺失武器；`Structure` 同理走 `ApplyStructureStats`。
- 代码工厂按 `name` 分发：`CreateConfiguredUnit/CreateConfiguredBuilding/CreateUnitBlueprint/CreateResourceNode/CreateNanoBehemoth/CreateShrine…`。
- 每个实体最终加入 `entities` 组、注册进 `SimManager.EntityNodes`，并把 `SimUnit/SimStructure` 加入 `SimWorld`。

## 6. 动作系统（UnitAction / UnitActionController）

- `UnitActionController`（“大脑”）持有两组队列：`_bodyQueue`（移动/战斗/采集）与 `_productionQueue`（生产/研究）。
- 动作缓存：`_actionCache` 在初始化/重建时一次性填充，模拟线程只查缓存，禁止 `GetNode`。
- 核心入口：
  ```csharp
  Brain.StartAction(actionName);                                  // 无参
  Brain.StartAction(actionName, targetPos, targetObj, isAuto, isQueue);
  Brain.GetAction<T>(actionName);
  Brain.GetProductionQueueSnapshot();  // 生产队列快照（模拟线程安全）
  ```
- `ActionLayer`（位标志）：`Movement`、`Weapon`、`Ability`、`Status`、`Transformation`、`Body`、`Production`… 用于冲突与打断规则。
- 新增指令 = 继承 `UnitAction`，在 `EntityFactory3D.RebuildUnitActions`（单位）或 `RebuildStructureActions`（建筑）里注册；锁定步行为写在 `OnUpdate` 里，视觉副作用一律 `EnqueueMain`。

### 建筑面板技能（地面目标类）标准流程
以“森林蔓延 / 制造虫洞 / 地震波 / 全能叶犬”为例，一条完整的地面目标建筑技能链路：
1. 新动作类继承 `CompletedStructureAbilityAction`，只声明 `ActionId`（如 `"PlantForestSpread"`）与 `DisplayNameText`；基类 `_Ready` 自动设 `ActionName / Layer=Ability / Queueable=false`。
2. 建筑配置 `StructureConfig.PanelSkillMode` 设标志位（1 雷达、2 轨道炮、8 共振波、16 地震波、32 虫洞、64 全能叶犬、128 森林蔓延）→ `EntityFactory3D.AddPanelSkills` 按位挂动作；**新位必须在这里加分支**。
3. `UserController.OnActionClicked` 识别 id + 校验对应位 → 设 `_pendingHeroSkill` / `_pendingHeroSkillGround = true`；鼠标移动预览里可加施法范围圈/落点圈（如森林蔓延显示以建筑为圆心的 12 格圈）。
4. 左键确认 → `SendStructureSkill(id, target, groundPos)` 发出 `NetAction.GroundCommand(id, pid, groundPos, [建筑 SimID])`（施法者 = 选中建筑）。
5. `SimManager.DispatchActions` 路由到 `HandleXxx`：`GetExecutor(netAct)` 取施法建筑 → 校验完工/冷却/资源 → 范围判定**以建筑位置为圆心** → `EnqueueMain` 生成实体/施放。
6. 动作按钮要常驻（冷却中也显示）：`Structure.GetAvailableActions()` 的白名单 `else if` 分支加该类型；CD 显示覆写 `GetCooldownRemaining/GetCooldownMax`（见 §13）。

### Ability 层技能执行闸门（架设/驻扎/兴奋剂等）
- \UnitActionController.StartAction\ 用 \TryPayCost()\ 做执行闸门：返回 false 会让通用派发（按钮/右键 → NetAction → \Brain.StartAction\）直接放弃，表现是**按钮亮着但点了没反应**。
- \AbilityActionBase.TryPayCost\ 必须返回 **true**（技能费用在各自模拟处理器里结算，这里表示无前置扣费、放行）；曾因返回 false 导致架设/驻扎/兴奋剂/切换弹种全部失效。
- 有专属模拟处理器的技能（Burrow/Devour/HeroSkill/OrbitalStrike 等）走 \DispatchActions\ 直接处理，不经过此闸门。

### 技能冷却 / 资源条显示约定
- 技能冷却：按钮上 `CdBar` 进度条（`ActionPanel.UpdateButton`），数据 = `UnitAction.GetCooldownRemaining()/GetCooldownMax()` → `EntityAction.CooldownRemaining/CooldownMax`；面板 10fps 自动刷新。
  - 示例：`CruiserMobilityAction`（战巡机动，`SimUnit.SkillCooldown`）、`PlantForestSpreadAction`（森林蔓延，`SimStructure.ForestSpreadCooldown` / 20s）。
- 冷却中按钮**不能消失**：`Structure.GetAvailableActions()` 对面板技能走“常驻白名单”（冷却/能量不足时置灰而不是移除）。
- 能量/资源数值**不画在按钮上**：按“能量单位”惯例放血条下方（§14 `UnitHealthBar3D` 槽位），例如植物花田读 `SimStructure.FlowerEnergy / FlowerEnergyMax`。

## 7. 战斗与武器

- 武器配置：`WeaponConfig`（伤害、类型、射程、冷却 tick、弹道、AOE、可目标类型、对空/对地/对建筑/中立）。
- 武器节点：`Weapon` 基类 + `Rifle`（即时命中） + `ProjectileWeapon`（生成逻辑弹体 `SimProjectile` + 视觉弹体 `HomingProjectile`）。
- 冷却本体存 `SimUnit`（`WeaponCooldowns`），纳入确定性哈希；索敌目标 = `SimUnit.CombatTargetId`（节点只做 ID→实体解析）。
- 伤害类型：Kinetic / Thermal / Explosive / EM / Beam；按 `ArmorType` 与 `Def*` 减免（`DamageResolver`）。
- 特殊机制：弹药（泰伦）、架设（`DeployAction`，架设后射程/HP/AOE 变化）、解放者范围圈（只能打圈内）、齐射（地狱领主 MultiShot）、英雄技能（`HeroSkillBehavior`）。

## 8. 资源与经济

- `ResourceType`：Metal=0、Wood、Food、Supply、Energy、Biomass=5、NanoBots=6、Money=7、Gas=8、Data=9。
- 采集：`HarvestAction`（工人到矿点装货→回交付点）；`CarryCapacity` 来自 `UnitConfig`；气/野果保底分配由 AI 经济系统处理。
- 纳米自动采集：`AutoHarvestBehavior` 按半径收集并统一转为 NanoBots。
- 玩家资源：`PlayerData`（`GetResource/AddResource/TryConsumeResources`）。
- 机器人难度 = 资源倍率：`GetBotResourceMultiplier`（休闲 0.7x / 标准 1x / 疯狂 1.5x），作用于所有收入点；逐机器人难度用 `SetBotDifficulty(team, diff)`。

### 菌毯（地毯）系统
- 类型：`CreepType` = `None(0) / PlantCreep(1) / DemonCreep(2) / NanoCreep(3) / Any(99)`。**不同种菌毯互相阻挡不能覆盖，同种不同队也不能覆盖**（`SimCreepGrid.AddCreep`）。
- 铺毯来源：建筑挂 `CreepSource` 模块（`StructureConfig.CreepSpreadRadius / CreepSpreadTime / GeneratedCreepType`），完工后每 `CreepSpreadTime` 秒半径 +1 格向外铺，只铺合法地面非墙格。**`CreepSpreadRadius` 默认 0 = 不铺毯**；曾因默认 10 导致所有 `RequiresCreep` 的植物建筑（落叶坑/树苗巢等）意外挂上纳米 CreepSource、铺出蓝色纳米毯——新增建筑必须显式写半径与 `GeneratedCreepType`，或显式写 0。
- **拆掉铺毯源头 = 清毯（不误伤其他源头）**：建筑被摧毁时 `CreepSource.ClearCreep` 按最大半径扫格清掉本来源类型的菌毯（含开局直铺的初始毯），但**被其他我方同型菌毯源头覆盖的格不清**（`IsCoveredByOtherSource` 按对方最大半径判定）——拆生命树/森林节点 → 只有未被别的树覆盖的绿毯消失。挂 CreepSource 的条件是**显式 `CreepSpreadRadius > 0`**（生命树没有 RequiresCreep 也挂，否则初始毯拆了不清）。
  - **生长速度加倍 = `CreepSpreadTime` 减半**（植物当前：生命树 2s、分支树 2s、森林节点 4s）。
- 无工人种族（纳米/植物）的建筑必须 `AutoBuild = true`（挂 `AutoBuildBehavior` 自动施工），否则蓝图永远卡 0%；面板建造**两族指令与入口完全独立**：纳米 `NanoBuild_<结构>` → `HandleNanoBuild`，植物 `PlantBuild_<结构>` → `HandlePlantBuild`（只共用确定性内核 `HandlePanelBuildCore`，并校验种族防跨族指令），生成即 `InitAsBlueprint` + `PromoteFromBlueprint` 进施工。
- 菌毯经济：纳米 = 每 20 格/秒 1 NanoBots（`CarpetIncomeCellsPerResource=20`）；植物 = 每 300 格/秒 1 木（`Plant.tres` = 300），`TickPlantFields` 结算（读 `CreepGrid.PlantCreepCount`）。
- 菌毯视野：`RaceConfig.VisionMode = 1` 的种族（纳米/植物）菌毯即视野，但**只覆盖自己阵营（含同组盟友）的菌毯**（`AddCarpetVisibleCells` 按 Owner 过滤）；敌方菌毯不提供视野。同时地毯视野种族**必须叠加单位/建筑视野**（`UpdateVisibleCells` 对全部视野源调用 `AddSourceVisibleCells`），否则被摧毁的敌方菌毯格会掉出可见集、渲染记忆永远残留（“打了不掉”）。
- 植物森林蔓延链：生命树/分支树/森林节点是“树源”（玩家用建筑技能 `PlantForestSpread`，AI 用 `TrySpawnForestNode`），目标必须在树源**各自范围**内（`ForestSpreadRangeTiles`：生命树 12 / 分支树 8 / 森林节点 8，按设计数据表），消耗 50 木生成森林节点（1x1、BuildTime 1s、铺半径 8、可用一次），源冷却 20s；**新生成的森林节点出生自带 20s 技能 CD**——链条式向外扩张菌毯与收入。
- 铺毯来源（植物）：只有**生命树与森林节点**产生菌毯；分支树等建筑不铺毯（不再挂 CreepSource）。扩散速度统一 `CreepSpreadTime=2s`（每 2 秒半径 +1 格）。
- 中立纳米核心（NanoCore，TeamID -2）是**测试单位，永远铺 NanoCreep**（半径 10，红/蓝纳米样式，不因本局没有纳米玩家而停铺）；植物玩家在它附近拍建筑，脚底是纳米毯而不是植物毯（不同种互不覆盖），属正常规则。

## 9. 科技与 Buff

- `TechConfig`：研究时间/费用/前置科技/效果字段（攻速、射程、移速、生产加速、解锁建筑、追加武器…）。
- 研究入口：`ResearchAction`（“Research_<TechId>”），建筑通过 `ResearchableTechIds` 声明可研究项。
- 效果统一查询：`TechEffects`（如 `GetAttackRangeBonus/GetHarvesterIncomeMultiplier/GetSpreadCostMultiplier`）。
- `BuffConfig` + `BuffContainer`：攻速/移速/伤害/射程修正，挂在 `SimUnit.Buffs` 上并纳入哈希。

## 10. 种族系统（RaceConfig）

每个种族一个 `RaceConfig`（`Data/Configs/<Race>.tres`）+ `Scenes/Races/<Race>/` 预制体 + `Scripts/Race/` 轻量派生（需要代码逻辑时）。

关键字段：
- 展示/难度/攻防经济科技评分、`VisibleResourceTypes`、`StartingResources`、`StartingEntities`（主基地/工人/矿点，TeamMode 控制归属）。
- `AvailableUnitIds / AvailableStructureIds / AvailableTechIds`。
- 特殊机制字段：恶魔 `SupplyCapMode`/立场、多足 `ControlRangeTiles`/蓝图、泰伦 `MaxAmmo/CanDeploy`、纳米 `CarpetSpread*`、联盟 `MaxEnergy` 等。

各族机制实现位置：
| 种族 | 机制 | 主要代码 |
|------|------|----------|
| 联盟 | 标准运营 + 兴奋剂/轨道炮/机动模式 | `Union.tres`、`StimAction/OrbitalStrikeAction/CruiserMobilityAction` |
| 纳米 | 菌毯扩张 + 自动采集 + 光环塔 | `Nano.tres`、`TickBotNanoSpread`、`AutoHarvestBehavior`、`NanoAuras` |
| 恶魔 | 立场（出圈掉血）+ 免费自动产兵 + 英雄 | `TickDemonFields`、`TickAutoProduce`、`HeroSkillBehavior` |
| 多足 | 控制圈（出圈丢控制）+ 蓝图生产 | `SimManager.IsInControlRange`、`CreateUnitBlueprint`、`TickWandererSystems` |
| 泰伦 | 弹药管理 + 架设 + 驻扎产出 | `TryConsumeAmmo`、`DeployAction`、`TickAlgaeGarrison`、`GarrisonAction` |
| 植物 | 菌毯经济 + 森林蔓延链 + 花田能量 + 无工人自动施工 | `Plant.tres`、`TickPlantFields`、`PlantForestSpreadAction`、`CreepSource` |
| 洞穴 | 残骸重建 + 虫洞传送 + 民兵化 + 地震波/裂解器 | `Cave.tres`、`TickCaveRebuild`、`WormholeCreateAction`、`SeismicWaveAction` |

### 人口与视野特殊规则
- `SupplyCapMode`：`0` = 建筑供给之和（默认）；`1` = 供给封顶，超过 `MaxSupplyCap` 截断（恶魔/植物/洞穴，植物默认 200）；`2` = 多足：只由牧羊人数量决定（每个 25，指挥链 +10，封顶 200）。
- `VisionMode`：`1` = 地毯视野（纳米/植物），菌毯即视野（见 §8 菌毯系统）。
- 人口占用：单位按 `UnitConfig.SupplyCost` 求和，另加 `SupplyUsed`；建筑蓝图/施工/完工都按 `StructureConfig.SupplyUsed` 占人口，供应按已完工建筑的 `SupplyProvided` 求和。

## 11. AI 系统（七族独立策略）

重构后的职责、机制与验证范围见 [AI_ARCHITECTURE.md](./AI_ARCHITECTURE.md)。难度统一采用 0.7 / 1.0 / 1.5 倍资源收益，各难度共用策略。

数据驱动：所有 AI 计划都在 `RaceConfig` 的 `Ai*` 字段：
`AiBuildOrderIds`、`AiBuildTargetCounts`、`AiTechOrderIds`、`AiUnitMixIds`、`AiWorkerTarget`、`AiWorkerUnitId`、`AiArmyTarget`、`AiExpansionBaseIds`。

主循环 `TickBotAIAll`（每 30 tick，即 1.5 秒）分派至 `SimManager.BotAI.Strategies.cs` 注册的七族策略。各族 partial 文件决定调用哪些经济、生产、研究、战斗和扩张工具及其顺序。纳米不调用普通生产，恶魔使用自动出兵，多足由编队独立控制；泰伦、植物、洞穴分别优先处理补给、森林经济和残骸重建。

> 队伍错峰：`TickBotAIAll` 按 `(team*7) % 30` 相位错开各队决策，避免 4AI 时所有队伍
> 在同一 tick 跑完整决策（实测单 tick BotAI 峰值 20~35ms，叠一起会让模拟线程超预算）。
> 军事单位判定统一走 `IsMilitaryUnit`：有武器且非工人/非采集/非控制锚点
> （多足 Shepherd 是控制圈锚点、Harvester 是经济单位，不能当兵力派去冲锋）。

关键机制：
- 波次推进 `BotPushWithWave`：集结（9 秒或兵力过半）→ 一起 attack-move；基地被摸直接防守。
- 队伍分组：`SimManager.ConfigureTeamGroups()` 从权威清单同步；`AreTeamsHostile(a,b)` 是唯一的敌对判定（同组=盟友，中立塔 -2 可打，中立资源 -1 不可打）。
- 机器人注册：大厅逐机器人（`BotConfigsOverride`，pid=100+）→ `AddOfflineBots` → 开局 `ConfigureBotsFromOverrides()` 把队伍注册进 `_botTeams`（SimManager 是常驻单例，必须开局重读）。

## 12. 网络与大厅

### NetAction（锁步指令）
字段：`Version/Sequence/PlayerID/TargetTick/ActionId/ActionIdExtra/EntityIDs/TargetX/TargetY/TargetEntityID/IsQueue`。
- 位置编码：`TargetX/Y = RoundToInt(world * 1000)`。
- 系统指令：`LOBBY_UPDATE / GAME_PREPARE / LOBBY_SYSTEM` 等不进锁步缓冲。

### 权威出生清单
`GAME_PREPARE` 的 `ActionIdExtra` 格式（分号分隔，每项冒号分隔）：
```
pid:team:race:group:obs
```
- `group`：队伍分组（2v2 同组 = 盟友），默认 = team。
- `obs`：1 = 旁观者（不生成玩家实体、全图视野、不可操控、看所有人资源）。
- 两端在 `ApplyAuthoritativeSpawnManifest` 统一覆盖 `PeerTeamMap/RaceMap/GroupMap/ObserverMap`，保证双端一致。

### 大厅（MainMenuController）
- 统一房间列表：人类和 AI 都是“一行”，在自己的行里改种族/位置/队伍；机器人额外有难度与移除。
- 机器人：固定玩家号 100+，位置单独存；位置被占时回退/自动挪位；旁观者行无任何选择控件。
- `ValidateLobbyReady`：旁观者免种族/位置/准备；出生点唯一。

### 主要 API
- `NetworkManager`：`PeerTeamMap/PeerRaceMap/PeerGroupMap/PeerObserverMap/PeerReadyMap`、`OfflineMode`、`BeginOfflineHost()`、`HostStartGame()`、`StartOfflineGameDirect(race, team, map)`、`SendLobbyUpdate(team, race, ready, group, obs)`、`GetPlayerTeam(pid)`、`IsPlayerObserver(pid)`、`FinalizeLocalIdentity()`。
- `LockstepManager`：`PlayerIDs`、`LocalPlayerID`、`SendAction(NetAction)`、`CurrentTick`、回放接口（`SaveReplayToFile/LoadReplayFromFile/VerifyReplayHashes`）。

## 13. UI 与事件总线

### GameEventBus（`Scripts/Core/Events/GameEventBus.cs`）
主线程事件总线，解耦输入层与面板：
```csharp
GameEventBus.CurrentSelection                     // 当前选中快照
GameEventBus.SelectionChanged += (sel, team) => … // 选中变化
GameEventBus.ActionTriggered += id => …           // 动作按钮点击
```
- `UserController` 只发布 `SelectionChanged`；`ActionPanel / SelectionInfoPanel / ProductionQueueUI / GameFX3D` 各自订阅、自行节流刷新。
- 模拟线程 → 表现层仍然走 `SimEventQueue`（保持确定性边界）。

### 主要 UI 对象
- `UserController`：输入/框选/右键指令/快捷键；旁观者时整体禁用。
- `UserUI`：资源/人口、聊天（T/Enter）、中键地图信号、旁观者面板。
- `ActionPanel`：Q~B 15 格动作按钮，从 `GetAvailableActions()` 生成；技能冷却在按钮上画 `CdBar` 进度条（数据来自 `UnitAction.GetCooldownRemaining/GetCooldownMax`，10fps 自动刷新）。
- 技能按钮冷却期间**不能消失**：`Structure.GetAvailableActions()` 对面板技能走“常驻白名单”（`ResonanceWave/SeismicWave/WormholeCreate/OmniLeafDog/PlantForestSpread` 等），冷却中置灰而不是移除；否则 CD 条无从显示。
- `SelectionInfoPanel`：单/多选信息、血条/能量/弹药/Buff。
- `ProductionQueueUI`：生产队列进度。
- `SettingsMenu`：语言/键位/音量/全屏，`GameSettings` 持久化到 `user://settings.cfg`。

### 本地化
- `Localization.Tr(key)` / `Tr(key, args)`（占位符 `{0}`，不是 `%d`）；表在 `Localization/<code>.csv`，回退链 当前语言 → en_US → key。
- 新增文案：同时改 `zh_CN.csv` 与 `en_US.csv`。

## 14. 渲染与表现

- `UnitVisuals`：模型实例化（`UnitModelLibrary` 按配置选择模型/贴图）、选中环、血条挂点、架设/部署表现。
- 批渲染：`UnitVatRenderer` + `HealthBarBatchRenderer`（大批量单位减少节点）；非批渲染走每单位节点。
- VAT：顶点动画纹理烘焙工具 `Scripts/Tools/VatBakeTool`（GDScript，模型动画 → 纹理），替代骨骼动画提高性能。
- 迷雾：`FogOfWar`（1024² 纹理，15fps 刷新）；`RevealAll` 全图（旁观者/压测）；`CurrentViewerTeams` 支持盟友共享视野。
- 特效：`HitFx` 对象池（弹道光束/枪口闪光/命中光球/扩散圈/扇形喷吐）；8 倍速等高倍速下表现层自行降频。
- 头顶条（`UnitHealthBar3D`）：血条在 y=0；**能量条（能量单位英雄能量 / 植物花田 `FlowerEnergy`）与恶魔寿命条共用一个槽位 y=-8**；生产/自动生产进度条在 y=-14。槽位约定：
  - 血条只随 `HealthChanged` 事件刷新；能量/寿命/生产条才开 `_Process`（`SetProcess(_energyFill != null || _prodFill != null || _lifeFill != null)`）。
  - 新增一种条：`Setup` 里加 `hasX` 判定 → 一对 `MakeQuad(背景+填充)`（背景宽 `width`、填充窄 1~2px）→ `_Process` 里按逻辑值刷新 `Scale`（左端固定：`Position.x = width*(ratio-1)*0.5`）。
  - 花田能量直接读 `SimStructure.FlowerEnergy / FlowerEnergyMax`（`TickPlantFields` 每 tick 维护，科技 `PlantTech_Sunflower` 可把上限提到 200）。
- 菌毯 3D 渲染（`CreepVisual3D`，挂在 `MapGround3D` 下）：三个 MultiMesh 批次——友方纳米（青蓝）、敌方纳米（红）、植物（绿色贴图 `PlantCreep.png`，任意归属都画绿）；2D `CreepLayer` 只作逻辑缓存（`CreepManager._Ready` 里 `Visible=false`），不是可见渲染路径。
  - 记忆规则：可见格实时写入 `_memory`（纳米）/ `_plantMemory`（植物）；**同格类型切换必须清掉另一侧记忆**，否则植物毯上会叠纳米方块。
  - `RevealAll`（旁观者/压测）不走记忆，直接按全量 `CreepGrid.GetAllCellData()` 绘制。

## 15. 测试与工具

### 环境变量/调试入口
| 变量 | 作用 |
|------|------|
| `BOT_TEAMS` / `BOT_DIFFICULTY` | 旧版机器人配置（逗号分隔队伍 / 1-3 难度） |
| `SIM_PROFILE=1` | 模拟系统级性能探针 |
| `RECORD_REPLAY` / `REPLAY_TICKS` / `REPLAY_FILE` / `REPLAY_DETERMINISTIC` | 回放录制/校验 |
| `AI_TEST_SPEED/TICKS/RACE1/RACE2` | AI 演示：倍速/时长/双方种族 |
| `AI_TEST_POP` | AI 演示：任一队伍人口达到该值自动退出（0=不启用） |
| `AI_TEST_TEAMS/GROUPS/RACES/BOTS/OBSERVER/PID_TEAM` | AI 演示：多队伍/分组/旁观者/pid≠team |
| `OPEN_SETTINGS=1` | 直接打开设置菜单 |
| `OPEN_TUTORIAL=1` / `=start` / `OPEN_TUTORIAL_INDEX=N` | 打开教程面板（打印条目/资料页数）；`=start` 选中第 N 套并点开始（详见 §19.6） |
| `DUMP_TUTORIALS=1` | 把每套教程的目标与全部资料页 + 每族生产链打到日志（校验图鉴内容与目标 ID） |
| `ART_DEBUG=1` | 打印环境/光照/阴影挂载结果与地面材质（PBR 三件套是否生效） |
| `SETTINGS_DEBUG=1` | 打印设置菜单内容高度（防止加设置项后超出窗口） |
| `FX_DEBUG=1` | 断言程序化网格（指令路径 / 框选边框）真的生成了几何 |
| `HUD_DEBUG=1` | 检查 HUD 面板内容是否被写死的像素尺寸裁掉 |

### 场景
- `Scenes/Test/AIShowcase1v1.tscn`：AI 演示（无头可跑，输出 `[BotAI]` 日志）。
- `Scenes/Test/StressTest*.tscn`：性能压测（2000 单位等）。
- `Scenes/Tools/ReplayAutoRecord|Verify.tscn`：回放工具。
- `Tools/run_ci.ps1`：编译 + Arena 确定性自测 + 双客户端对拍。

## 16. 新增指南

### 新增一个单位
1. 在 `Data/Configs/<Id>.tres` 建 `UnitConfig`（HP/移速/费用/建造时间/人口/标签/武器/技能/采集等）。
2. 可选：`Scenes/Races/<Race>/Units/<Id>.tscn`（模型+枪口），没有则代码工厂自动生成占位模型。
3. 把 `<Id>` 加入该种族 `RaceConfig.AvailableUnitIds`；若要工人能造/生产建筑能训练，分别加进 `BuildableStructureIds` / `TrainableUnitIds`。
4. 若 AI 要出它：加入 `AiUnitMixIds`；若需要武器：建 `WeaponConfig` 并填 `WeaponIds`。

### 新增一个建筑
同上，但用 `StructureConfig`（占地 `GridWidth/Height`、HP、费用、`IsProductionBuilding/IsTechBuilding/IsMainBase/SupplyProvided` 等）；在工人配置的 `BuildableStructureIds` 中登记；AI 建造顺序加进 `AiBuildOrderIds`。

### 新增武器 / 科技 / Buff
- 武器：`WeaponConfig`（伤害/射程/冷却/弹道/AOE/目标类型），实体 `WeaponIds` 引用。
- 科技：`TechConfig` + 在建筑 `ResearchableTechIds` 登记 + 在 `TechEffects` 实现效果。
- Buff：`BuffConfig`，由科技/技能挂到 `SimUnit.Buffs`。

### 新增动作 / 技能
1. 继承 `UnitAction` 写 `OnUpdate`（只碰逻辑数据；视觉走 `EnqueueMain`）。
2. 在 `EntityFactory3D.RebuildUnitActions / RebuildStructureActions` 注册（或用 `AddSkillAction` 按 `Skill1Kind/Skill2Kind` 配置化挂载）。
3. 若需要按钮：`EntityAction` 的 `SlotIndex` 决定动作面板位置；本地化键 `action.<id>`。
4. 技能冷却（按钮上 CD 进度条）：覆写 `GetCooldownRemaining()` / `GetCooldownMax()`（示例：`PlantForestSpreadAction` 读 `SimStructure.ForestSpreadCooldown` / 20s，战巡机动模式读 `SimUnit.SkillCooldown`）。`ActionPanel` 会自动画 `CdBar`。
5. 技能按钮要“冷却中也常驻”：在 `Structure.GetAvailableActions()` 的常驻白名单（`else if` 分支）里加上该动作类型。
6. 能量/资源数值**不画在按钮上**：按“能量单位”惯例放血条下方（见 §14 `UnitHealthBar3D` 槽位约定）；花田示例 = `SimStructure.FlowerEnergy/FlowerEnergyMax` + `Setup` 的 `hasEnergy` 判定。
7. 建筑地面目标技能：走 §6“建筑面板技能标准流程”（`CompletedStructureAbilityAction` + `PanelSkillMode` 位 + `SendStructureSkill` + `HandleXxx` 路由）。

### 新增一种头顶条（血条下方）
1. `UnitHealthBar3D` 加 `_xxxBackground/_xxxFill` 字段与静态材质（`CreateBarMaterial`）。
2. `Setup` 里加 `hasX` 判定（读配置/逻辑字段）→ 一对 `MakeQuad`（背景宽 `width`、填充窄 1~2px），`Position.y` 按槽位约定（能量/寿命 -8，生产 -14；新条避开已有槽位）。
3. `_Process` 里按逻辑值刷新 `Scale` 与 `Position.x = width * (ratio - 1) * 0.5f`（左端固定）。
4. 把 `_xxxFill != null` 加进 `SetProcess(...)` 条件，需要时订阅逻辑变化事件而非每帧轮询。

### 新增一种菌毯 / 铺毯种族
1. `CreepType` 枚举加值；`SimCreepGrid` 加计数缓存（`_plantCreepCount` 同款）与 `AddCreep/RemoveCreep` 分支。
2. 铺毯建筑：`StructureConfig.CreepSpreadRadius / CreepSpreadTime / GeneratedCreepType`（`CreepSource` 自动挂载）；无工人种族必须 `AutoBuild = true`。
3. 经济：`RaceConfig.CarpetIncomeCellsPerResource` + `SimManager` 每 tick 结算（`TickNanoEconomy` / `TickPlantFields` 同款）。
4. 视野：`VisionMode = 1` + `FogOfWar.AddCarpetVisibleCells` 加该类型。
5. 渲染：`CreepVisual3D` 加批次/贴图，并遵守“类型切换清另一侧记忆”规则。
6. AI：`TickBotRaceSpecific` 加铺毯/防御决策（参考 `TickBotNanoSpread` / `TickBotPlantSpread`）。

### 新增种族
1. `RaceConfig` tres（起始资源/实体/可用列表/AI 计划/特殊字段）+ `Data/Configs` 注册。
2. `Scenes/Races/<Race>/` 放预制体；`RacePanelManager` 如需专属面板再扩展。
3. 主菜单种族下拉元数据、`RaceConfig.DisplayName` 本地化键 `race.<id>`。

### 新增 UI 面板
需要实时数据就订阅 `GameEventBus` 或 `SimManager` 快照（`GetChatSnapshot/GetPingSnapshot`）；涉及锁步输入走 `LockstepManager.SendAction`。

## 17. 开发规范与常见坑（务必阅读）

0. **文档同步义务**：任何机制/UI/数值/约定类改动，必须在同一轮改动里同步更新本文档——
   新机制写进对应章节（种族机制→§10/§18.5、UI→§13/§18.2、渲染→§14/§18.3、AI→§11/§18.6、
   数值与放置→§18.4、新增指南→§16），不要再让约定只存在于代码和对话里。

1. **确定性**：逻辑层只用 Fix64；新增逻辑必须纳入哈希（`SimWorld.Hashes`），并在 Arena 对拍/回放里验证。
2. **线程**：模拟线程不碰 Godot 节点；视觉延迟 `EnqueueMain`；主线程遍历 World 加 `WorldLock`。
3. **释放节点**：表现层访问实体前 `IsInstanceValid`；选中列表要自动清理死亡单位（否则 8 倍速下高概率闪退）。
4. **pid ≠ team**：机器人固定 pid 100+、team=位置；任何“用 pid 当队伍”的旧代码都是 bug。
5. **配置**：`ConfigDatabase.ValidateAll` 启动时校验悬空引用/负值/缺字段；新增配置后跑一次无头启动确认不炸。
6. **预制体**：场景里不能挂“空 C# 脚本”子资源（`CSharpScript_xxx`）；导出预制体用 `ExportEntityScenes`（已修复 AutoHarvest 等脚本路径）。
7. **本地化**：占位符用 `{0}`；中英文表同步。
8. **存档/设置**：只写 `user://`，不写工程目录。
9. **跨局状态**：`UnlockSessionForLobby()` 必须全量重置（`ResetSimulation` + `ResetLockstep`），
   否则第二局会带着上一局的单位/指令/波次状态直接闪退。
12. **动作缓存加锁**：`UnitActionController._registeredActions/_actionCache` 会被模拟线程（研究完成加动作）与主线程（`RefreshActionCache`）并发访问——必须用 `_actionLock` 保护，否则 `IndexOutOfRange` 会让缓存刷新失败、`StartAction` 偶发找不到动作（按钮亮着但指令不执行）。
13. **OnDestroyed 视觉副作用回主线程**：`Structure.OnDestroyed` 可能在模拟线程触发（建筑死亡/蓝图取消），`SetSelected/Obstacle/死亡动画/建筑幽灵` 一律 `EnqueueMain`，否则跨线程碰节点触发 “can only be accessed from the main thread” 并连锁问题。
16. **寻路按单位半径膨胀**：`SimPathfinder` 烘焙 0~4 级半径网格，`FindPath`/LOS 按 `radiusTiles = ceil(Radius/64 - 0.5)` 取值——1×1 单位=0 级（不膨胀），2×2/3×3=1 级，4×4+=2 级。此前一律当 1×1 点寻路，大型单位过墙角/窄缝会卡死。`IsWalkableForRadius` 检查中心格 ±r 邻格（ignoreId 感知）。15. **路径耗尽必须重建**：SimUnit.CommandMove 保留旧路径的条件必须包含 CurrentWaypointIndex >= Path.Count——否则路径走完后重复下发的同目标指令会保留已耗尽路径，CurrentWaypointIndex 每 tick 递增、单位永远停住（走一步停住的全种族根源）。

14. **移动中不架设**：AI 的 `HandleTerranDeploy` 对 `HasTarget/PathPending` 的单位直接跳过；`CommandMove` 的架设收起分支幂等（重复指令不再清 `HasTarget`）——否则单位移动中被架设 → 走一步停住。
11. **导航烘培只回主线程**：MapGrid.RequestBake 可能被模拟线程触发（蓝图取消/建筑注册），跨线程 NavRegion.IsBaking()/CallDeferred 会原生闪退（大量蓝图取消时复现）——一律 SimEventQueue.EnqueueMain 后再碰 NavRegion。

10. **信号退订**：任何场景节点订阅 `NetworkManager`/`Localization` 等 autoload 信号，
    必须在 `_ExitTree` 退订（MainMenuController 曾因漏退订 SessionReady，
    第二局开局时回调打到已释放的控制器 → ObjectDisposedException 闪退）。

## 18. 开发约定与模式速查（新增/改动必读）

> 本章把项目里**已经定下来、反复踩坑**的约定集中成速查表。和正文重复的内容以本章为准。

### 18.1 输入与操作（`UserController`）
- 左键：点选 / 框选。**Shift 左键点选 = 加入/移出（toggle）；Shift 框选 = 追加合并；Shift 点空地 = 保留原选择**（星际/帝国风格，`UserController.cs` 输入分支）。
- 右键：对选中单位下指令；**Shift 右键 = 队列指令**（移动/建造/训练集结点等，`NetAction.IsQueue`）。点击反馈颜色：绿=普通指令、黄=Shift 队列、红=敌对目标。
- 小地图：**左键/左键拖拽 = 视角中心对准点击格**（`RTSCamera.SetViewCenter`，不是直接挪相机，否则有 lookAhead 偏移）；**右键 = 对选中单位下达移动指令**；旁观者右键无效。
- 快捷键：**F1 = 选中所有闲置工人**（无战斗/采集/建造/移动目标的本队工人，`SelectIdleWorkers`）；**1/2/3/4 = 全局时间加速**（任何地图/单机/旁观者可用；联机时 `SimulationSpeed` 内部强制 1x，不破坏锁步）；F1~Fn = 顶部种族面板按钮；Q~B = 动作面板 15 槽。
- 旁观者：默认全图、不可操控、可查看所有人资源，可选中实体查看信息。
- 建造模式：左键放置（按住 Shift 连续建造不退出），右键/ESC 退出；树墙（`PlantTreeWall`）按住左键拖拽沿 Bresenham 排成一行。

### 18.2 UI 约定
- **技能冷却**：按钮上 `CdBar` 进度条（`ActionPanel.UpdateButton`），数据 = `UnitAction.GetCooldownRemaining/GetCooldownMax` → `EntityAction.CooldownRemaining/CooldownMax`，面板 10fps 自动刷新。冷却中按钮**置灰常驻，不能消失**（`Structure.GetAvailableActions()` 常驻白名单）。
- **已完成的科技按钮显示绿色**：`Structure.Data` 对 `ResearchAction` 且 `PlayerData.HasTech` 置 `Tint=Green`；`ActionPanel` 的禁用分支不得用灰色覆盖非白 `Tint`（否则已完成科技会变灰）。
- **能量/资源数值不画在按钮上**：按“能量单位”惯例放血条下方（§14 `UnitHealthBar3D` 槽位 y=-8）；花田示例 = `SimStructure.FlowerEnergy/FlowerEnergyMax`。
- 头顶条槽位：血条 y=0；能量/寿命 y=-8；生产/自动生产 y=-14（§14 有新增一条的完整步骤）。
- 本地化：键命名 `race.<id> / unit.<id> / structure.<id> / tech.<id> / action.<id> / skill.<单位>.<n>`；中英 CSV 同步；占位符用 `{0}`；按钮快捷键文本由 `RefreshLocalizedTexts` 自动追加，不要写死在场景里。
- 小地图：单位=小圆点、建筑=方块，图标大小与占地挂钩，边缘最细黑边；包围所有地板/墙的长方形 contain 填满窗口、不溢出；实体层约 10fps 重绘。
- 设置：语言/键位/音量/全屏走 `GameSettings`（持久化 `user://settings.cfg`）；部分设置允许“改完重启生效”。
- 动作按钮图标：配置表驱动（`ActionViewFactory`），不手写按钮树；技能名在 `ActionViewFactory` 的 switch 里按动作类型覆盖 `DisplayName`。

### 18.3 渲染与表现
- 模型：`UnitModelLibrary` 映射 GLB；有 VAT 烘焙的走 `UnitVatRenderer` MultiMesh 批渲染（`IsBatchableModel`）；无模型回退贴图立方体；预制体数值一律被配置表覆盖（`ApplyConfigFromTableIfNeeded`）。
- 菌毯 3D：三批次——友方纳米（青蓝）、敌方纳米（红）、植物（绿色 `PlantCreep.png`）；2D `CreepLayer` 只作逻辑缓存（隐藏）；**可见格类型切换必须清另一侧记忆**（`_memory`/`_plantMemory`），否则植物毯上叠纳米方块。
- 迷雾：`RevealAll` 全图（旁观者/压测）；`CurrentViewerTeams` 同盟共享视野；菌毯即视野的种族走 `AddCarpetVisibleCells`（§8）。
- 建筑障碍：**完工后是方形障碍（等同地形墙，注册进网格）**，对所有队伍阻挡；圆形膨胀方案已废除。
- **蓝图规则（帝国时代 4 式，与完工建筑完全不同）**：
  - **只对"自己人"生效**：同队或同组盟友（`ISimulationRules.AreTeamsFriendly`）看得见、走不过去；**敌方既不阻挡也不可见**（`SimGrid.BlocksForTeam`）。
  - 实现走**蓝图覆盖层**：蓝图**不进**全局阻挡网格 `_blockedCells`，而是单独存进 `_blueprintCells`（`SimPathfinder.SyncStructureBlocking` 里排除蓝图 + `RebuildBlueprintOverlay` 对账）。因为"挡不挡"取决于查询方队伍，而扁平膨胀网格 `_radiusWalkGrids` 没有队伍维度；把蓝图混进全局网格会让敌方也被膨胀层挡住。
    - 寻路 API 带 `teamId`：`IsWalkable / IsWalkableForRadius / HasLineOfSight / IsWalkableFast / GetNearestWalkableNode / FindPath`。**新增调用点必须传 `teamId`**，漏传会退化成"蓝图不挡任何人"。
    - 蓝图查询缓存 `_blueprintQueryCache` 的 key **必须含 `teamId`**——只按格子缓存会把"挡自己人"的命中结论错误复用给敌方（已踩过）。
  - **允许拍在己方单位身上**：放置不做单位占位校验，而是把己方单位**强制插队一个"移出寻路"**（`SimUnit.EvictStructureId` + `SimWorld.ForceEvictUnitFromBlueprint`），不论它当前在执行什么指令；离开后由 `SimWorld.TickBlueprintEvictions`（在 `World.Tick()` 之后每 tick 对账）撤销让路状态。
    - **落点必须可达**，不能空取矩形边中点：先把"该队伍会被蓝图覆盖"的格子收集起来取外接矩形（同一张蓝图由多片 `Blueprint_Building_*` 拼成，单片长宽未必是整张图），向四方向外扩 1 格作候选环，只挑**真正可通行**的格（忽略该蓝图本身，其余地形/建筑/敌方蓝图照常判定），按离单位最近的格排序后交给 `CommandMove`；候选环全堵时退化为 `GetNearestWalkableNode` 邻域搜索（≤15 格）。
    - 四周彻底堵死时**不下无意义的移动目标**（`SimUnit.CancelMoveIntent`），保持让路标记下一 tick 重试；离开蓝图后残留的"移出"目标由 `CancelMoveIntent` 清掉，原指令由动作层在 `OnUpdate` 里重新下达。
  - **施工闸门**：蓝图占地内还有任何单位时**不许开工**（`SimWorld.IsFootprintClearOfUnits`，`BuildAction.TryStartBuilding` 等待），清空后工人自动转施工。
  - 蓝图→施工/完工的状态切换必须让阻挡指纹失效：`_blockingFingerprint` 已含 `CurrentState` 与 `_teamRevision`，否则网格会停留在旧结论（蓝图不挡或完工不挡）。
- 特效：`HitFx` 对象池；**不得按距离/倍率隐藏特效**（曾删除“拉远隐藏特效”的代码）；8 倍速只降刷新频率，不删效果。
- 近战 VFX：刀光扇形从**模型前缘起笔、长度覆盖到目标**（不能从单位中心起笔，大体积单位会把特效埋在身体里）；`SpawnConeSweep` 使用调用方传入的颜色（近战白色刀光/龙息橙色），不得硬编码单色。

### 18.4 数值与放置规则
- 近战武器 `RangeType=0`（与恶魔狗对齐），所有近战统一，不许个别用 Ranged。
- 攻击/索敌一律看**最近敌人**，禁用质心/平均值（AI 与玩家索敌都遵守）。
- 建筑放置：建筑之间留至少 2 格（松散防卡位）；**资源点外 2 格内禁止建造**（`IsAreaNearResourceNode(..., 2)`，玩家与 AI 共用）。
- 采集/建造距离：尽量靠近建筑；有远程建筑采集能力的单位除外。
- 人口：单位按 `SupplyCost` + `SupplyUsed` 占人口；建筑蓝图/施工/完工都占 `SupplyUsed`，供应只算完工建筑的 `SupplyProvided`；`SupplyCapMode` 0/1/2 语义见 §10。
- 射程 >10 的单位选中时显示虚线圈（射程圈）。

### 18.5 各族机制约定（已实现，改动前先看这里）
- 恶魔：立场沿寻路铺（`TickDemonFields` / `TryBotDemonFieldPush`）；寿命=持续时间（血条下紫条），不掉血，无“出圈扣血加速”；自动产兵建筑；人口 50 封顶（`SupplyCapMode=1`）。
- 多足：编队三逻辑——扩张（1牧羊+2建造→新编队）、补充（补到满编再补战车/战斗单位）、超级（1电浆炮+2建造单开队）；牧羊控制圈、蓝图生产；**工人禁止抽调，编队成员只来自蓝图归属**；编队用数组记录；牧羊接走脱队单位；攻击队补满再上、数量≤占领队且≤3；电炮队无上限、不打塔专打敌人；200 人口（牧羊 25/个，指挥链 +10）。
- 泰伦：架设 AI 在可攻击范围内有敌人时不收架；对地/对空武器分开（解放者别架去对空）；解放者射击圈=目标必须在圈内，AI 以圆心指定单位；**驻扎直接右键进驻**（面板驻扎按钮已删，避免遮住机场按钮）；弹药管理 + 藻类工厂多造。
- 纳米：菌毯只铺外缘不重叠（`TickBotNanoSpread` 选“新毯潜力最多”的外缘格）；防御塔四塔一簇、随机机炮/防空/狙击/烟雾/活性；点科技（要塞化+走路开毯）；无人口限制、无工人、建筑 `AutoBuild=true`。
- 植物：**森林蔓延=建筑技能**（生命树/分支树/森林节点释放，范围以建筑为圆心且按类型不同：12/8/8 格，50 木，20s CD，节点一次性且出生自带 20s CD）；菌毯经济 300 格/木；**只有生命树/森林节点铺毯**，扩散统一 2 秒一格；花田能量=血条下蓝条（10 点生叶犬，上限 100/200），**全能叶犬需先研究“全能性”科技，建造方式 = `Blueprint_PlantLeafDog` 蓝图自动生长（15s，生命树 12 格内建造速度翻倍）**；无工人、建筑 `AutoBuild=true`；菌毯=视野（仅自己阵营）；**面板建造走独立的 `PlantBuild_` 管线，不继承纳米**（`HandlePlantBuild` / `TryBotPanelBuild` 共用内核、指令独立、种族校验）。
- 洞穴：残骸自动重建（`TickBotCaveRebuild`）；虫洞传送；民兵化（驻扎伤害）；地震波/裂解器面板技能；沙虫五节躯体（头/中/尾建模不同，AI 建模用头部复制 5 节）——**每节都能撕咬**（`WeaponIds=CaveSandwormBite`）且整条移动攻击（`AttackMoveFiresWhileMoving`），任意一节死亡整条无法攻击、潜地时整条（含节段）无法攻击；吞噬有 12 格范围圈并做范围校验（`Skill2RangeTiles`）。

### 18.6 AI 约定
- 数据驱动：所有计划都在 `RaceConfig.Ai*` 字段；`TickBotAIAll` 每 30 tick（1.5s），队伍按 `(team*7)%30` 错峰，避免 4AI 同 tick 峰值。
- 打/占分离：拆塔与开矿是两条独立评估线；**开矿是固定流程**（劣势也开）；盟友落后两片矿时让矿。
- 波次推进（WaveMode=2）期间**不逐单位 TryAutoAttack**：逐单位索敌会每 tick 把正在 AttackMove 的单位改成攻击最近敌人（与波次目标打架 → 走一步停住/原地打架），推进交给 AttackMove 自带索敌；基地被摸仍防守。
- `AttackMoveAction` 只有**真正到达目标点（1.5 格内）**才 `Finish`：被 AttackAction 停火清掉 `HasTarget` 时必须继续 `CommandMove` 推进，否则单位停火后 AttackMove 误结束 → 原地打架到波次重发（走一步停住）。
- 波次：集结（9 秒或兵力过半）→ attack-move，不一个一个送；攻击队补满再出门。
- 索敌/攻击只认最近敌人；拆塔评估有军力门槛；塔倒固定拍分矿。
- 工人尽量不闲置；多足工人禁止抽调；生产严格归属编队。
- 调试输出：BotAI 只保留关键行；**用正则删日志时小心误伤控制流**（出过事故）。

- **走一步停住调试测试**：代码内置 `[MoveDebug]` 探针（`MoveAction` 提前结束、SwarmManager 早停、CommandMove 被架设拦截），`Scenes/Test/AIShowcaseDebugMap.tscn` 可在大地图复现（如 `AI_TEST_RACES=Union,Demon` + `AI_TEST_TICKS=30000` + `AI_TEST_SPEED=8`，grep `MoveDebug|main thread|SimEvent`）。
### 18.7 测试与验证
- AI 演示/回归：`AI_TEST_SPEED/TICKS/POP/RACE1/RACE2/TEAMS/GROUPS/RACES/BOTS/OBSERVER/PID_TEAM`（§15）。
- 确定性：`Arena.Stage2/Tools/SimulationSelfTest` 对拍；回放录制/校验；双客户端对拍；CI 见 `Tools/run_ci.ps1`。
- 新增配置后必须跑一次无头启动（`ConfigDatabase.ValidateAll` 会校验悬空引用）。
- 调试作弊键：**F11 = 全种类资源各 +5000**（含木材/食物/人口/能量/生物质/纳米虫/金钱/瓦斯/数据，走锁步指令双端一致）；F9 = 秒建建筑；F10 = 秒点科技（`HandleCheatAddResources` / `CheatToggleBuild` / `CheatToggleResearch`）。

### 18.8 线程与确定性红线（摘要，详见 §17）
- 逻辑层只用 Fix64；模拟线程不碰 Godot 节点；视觉副作用一律 `EnqueueMain`；主线程遍历 World 加 `WorldLock`；新增逻辑必须纳入哈希（`SimWorld.Hashes`）并在对拍/回放验证。

## 19. 地图编辑器与自定义地图

面向地图作者的完整工具链。入口：**主菜单 → 地图编辑器**（`Scenes/Editor/MapEditor.tscn`）。

### 19.1 数据流（唯一真相 = `RtsMapData`）

```
作者编辑 RtsMapData (.tres, res://Maps/)
   │
   ├─ MapValidator.Validate ──→ 报告（错误阻止保存）
   ├─ MapGenerator.Generate ──→ 程序化产出（4 风格 × 4 对称）
   │
   └─ 游戏开局：MapRegistry.Resolve ──→ MapRuntime ──→ SimManager
                                          │
                        MapLoader.ApplyToSimGrid ─┘  （地形进确定性 SimGrid）
```

| 文件 | 职责 |
|------|------|
| `Scripts/Data/Maps/RtsMapData.cs` | 地图数据模型。地形 1 字节/格：**低 4 位 = 可走性，高 4 位 = 外观变体**（换皮不改变寻路结果） |
| `Scripts/Data/Maps/MapSerializer.cs` | **纯文本 `.rtsmap` 序列化**（见 19.1.1，为什么不用 `.tres`） |
| `Scripts/Data/Maps/MapElements.cs` | 出生点 / 实体摆放 / 命名区域（触发器按名引用，挪区域不必改触发器） |
| `Scripts/Data/Maps/MapDataOps.cs` | 纯数据几何：画笔/直线/矩形/圆/洪水填充/重采样/边界墙 |
| `Scripts/MapEditing/MapGenerator.cs` | 程序化生成 + 对称 + 连通性修复 + 出生点 + 资源布置 |
| `Scripts/MapEditing/MapValidator.cs` | 校验器：**连通性与可达性走真实 `SimGrid` + `SimPathfinder`** |
| `Scripts/World/MapLoader.cs` | `RtsMapData ↔ TileMapLayer / SimGrid` 唯一转换点 |
| `Scripts/World/MapRuntime.cs` | 运行时地图（索引化、保序、无 Godot 依赖，可被模拟线程读） |
| `Scripts/World/MapRegistry.cs` | 扫描/保存 `res://Maps/`；旧场景地图出生点导出 |
| `Scripts/Editor/MapEditorController.cs` | 编辑器 UI（自绘画布 + 工具面板） |
| `Scripts/Tools/ExportMaps.cs` | 批量生成预设 + 旧地图迁移（保存前先校验） |

#### 19.1.1 为什么用纯文本 `.rtsmap` 而不是 `.tres`

**实测结论**：`ResourceSaver.Save()` 保存"运行时构造、且含嵌套自定义 `Resource`"的对象时，
会给嵌套项写出一个**空的 `CSharpScript` 子资源**，且不回写脚本路径。
重新加载时报 `Cannot instantiate C# script because the associated class could not be found`
—— 8 张地图**全部**加载失败。

改用自研纯文本格式后问题消失，并且额外得到三个好处：

1. **可 diff**：每个字段一行，git 里能看出作者改了什么；
2. **确定性**：数字一律 `InvariantCulture` 定点输出 + 地形游程编码，
   程序化生成的地图重复导出**逐字节一致**（可直接进 CI 做回归比对）；
3. **体积**：同一张地图 71KB(`.tres`) → 6KB(`.rtsmap`)。

⚠️ **导出必须在 PCK 里带上 `.rtsmap`**：`export_filter="all_resources"` 只包含
Godot 认得的资源类型，自定义扩展名会被丢掉。`export_presets.cfg` 的
`include_filter` 已设为 `*.rtsmap`（**两个 preset 都要设**，漏一个就会打包出没有地图的版本）。

### 19.2 触发器脚本层

- 定义：`Scripts/Data/Triggers/TriggerDefinition.cs`（条件/赋值/动作/相位/冷却）
- 表达式引擎：`Scripts/Simulation/Scripting/SimExpr.cs` —— 自写词法+递归下降语法+**Fix64 求值**。
  不用 Godot `Expression`：它走 float/double 且引擎版本间行为不保证一致，
  而触发器会改资源/刷单位，必须逐位可复现。`0.1 + 0.2` 精确等于 `0.3`。
- 调度器：`MapTriggerSystem.Evaluate`，按地图里触发器的**数组顺序**执行（作者可控的顺序）。
- 宿主：`Scripts/Core/SimManager.Triggers.cs` 实现 `ITriggerWorld`；
  每 tick 在**末尾**求值（此时移动/战斗/死亡已结算，触发器看到稳定的本 tick 结果）。
- **状态纳入哈希**：`GetFullWorldSectionHashes()` 有独立 `Triggers` 区段。
  触发器状态分叉若不被哈希覆盖，就会表现成"某处突然不一致"却定位不到来源。

内置函数（作者可用）：`player(n)` / `resource(t,'Metal')` / `has_tech` / `supply_used` / `supply_max`
/ `units_in_region` / `units_in('region',t)` / `structures_in` / `count_near` / `count` / `defeated`
/ `hostile` / `distance` / `tile_distance` / `min` / `max` / `abs` / `floor` / `ceil` / `round` / `sqrt` / `clamp` / `sign`。
变量：`tick` / `second` / `map_width` / `map_height` / 作者声明的 `var_*`；
触发器自身状态 `trigger_<id>_fired` / `_count` / `_enabled`。

### 19.3 校验器判据（作者必须知道）

| 判据 | 级别 |
|------|------|
| 无出生点 / 出生点在墙里 / 槽位或格子重复 / 出生点互相走不到 | 错误 |
| 地面被墙分割成互不连通的多块 | 错误 |
| 资源点在墙里 / 实体重叠 / 出生点附近无可达资源 | 错误 |
| 出生点周围放不下 3×3 基地（被建造禁区封死） | 错误 |
| 触发器表达式语法错 / 引用不存在的区域、触发器、资源类型 | 错误 |
| 出生点过近 / 资源偏少 / 地图非方形 / 边界未封闭 / 触发器永远不会被启用 | 警告 |

### 19.4 四条容易踩的坑（都实际踩过）

1. **`SimPathfinder.FindPath` 在 A\* 迭代超限时会返回穿墙的截断路径**（回退到"最接近节点"）。
   校验器必须**逐点检查路径可走性**，只看"终点接近目标"会把被墙分割的地图误判为连通。
2. **对称必须放在最后一步地形操作**。`FillIsolatedPockets` 与出生点清障都是一次性且不对称的，
   之前把出生点放在对称之前，最后那次 `ApplySymmetry` 会重新把地图切成互不相通的两半。
   现在顺序是：地形定型 → 放出生点（用真实寻路确认连通）→ 套对称 → **最终连通性兜底**
   （`EnsureFloorConnected`：把每个非主连通块用一条走廊接到主区域）。
3. **`BuildBlocked` 建造禁区掩码必须在游戏里真正生效**。之前只有生成器写、校验器读，
   游戏建造校验完全不看它 —— 作者画的"资源点禁建"从来不起作用。
   现接到 `MapGrid.IsAreaEmpty` 与 `SimWorld.IsAreaNearResourceNode`（两条路径都要设，
   否则玩家被挡住而 AI 照样往禁区里拍建筑）。
4. **`tick` 既是变量也是函数，两者必须同时实现**。`TriggerHost.CallBuiltin` 曾漏了
   `case "tick"`，作者写 `tick() >= 40` 时求值落到 `default` 返回 0 ——
   条件永远为假**且不报任何错**，表现就是"触发器定义了却从不触发"。
   未知内置函数静默返回 0 这个设计放大了问题：拼写错误同样无声。
   现在 `SimValidationTests` 逐个断言作者文档里列出的每个内置函数都有正确返回值。

### 19.5 端到端验证现状（无头）

已实机跑通（`Godot_v4.5.1-stable_mono_win64`）：

```
ExportMaps  --export-maps --gen-presets   → 写出 8 张，失败 0 张
  Gen_Open2 / Gen_Open4 / Gen_Choke2 / Gen_Rooms4 / Gen_Caves2 / Classic / 1v1 / Debug
AIShowcase1v1 --offline AI_TEST_MAP=Gen_Open2  →  地图载入 + 触发器在 tick 40 触发
  [Game] 地图 'Gen_Open2' 地形已从数据载入（120x120）
  [Map]  载入运行时地图：开阔战场 1v1 [Gen_Open2] 120x120 出生点=2 实体=45 触发器=1
  [Trigger] 开阔战场 1v1 · 战斗开始！
```

`AI_TEST_MAP=<MapId>` 可把演示/回归固定到任意地图上（触发器、出生点、中立物都会走真实路径）。

### 19.6 教程系统与教程面板

教程分**两层**（`TutorialTier`），面板按层分组。这个划分是刻意的：
「怎么操作」和「这个阵营怎么玩」是两个不同的问题，混在一起会让新手一上来就面对一堵墙。

| 层 | 内容 | 数量 |
|----|------|------|
| `Basics` | 通用操作：选中/移动/视角、采集、建造、生产、科技、攻击、人口 | 1 套（`tutorial_basics`，8 目标，用联盟当载体） |
| `Advanced` | 每族的**机制详解 + 单位档案 + 建筑档案 + 科技树** | 7 套（Union / Terran / Demon / Nano / Plant / Cave / Wanderer） |

**UI 组织**：主菜单上只有**一个**「教程」按钮，点击后 `SwitchPage(MenuPage.Tutorial)`，
进入一个**独立教程面板**（不是把几套教程平铺在主菜单上——阵营一多会把主菜单挤爆）。

面板由 `MainMenuController.BuildTutorialPage()` **按需用代码构建**（场景 `.tscn` 里没有这一页），
布局是**左列表 + 右详情**：

```
┌ 左：教程列表 ────────────┬ 右：详情 ────────────────────────┐
│ 基础                     │ 标题 + 简介 + 种族/目标数/资料页  │
│   · 基础操作 (8 目标…)   │ [开始这个教程]                    │
│ 进阶（按阵营）           │ 🅐 实操流程：1..N 目标标题+说明    │
│   · 联盟 · 基础运营      │ 🅑 资料：Tree 目录 + 正文         │
│   · 泰伦 · 弹药与架设    │    （阵营总览/经济来源/核心机制/  │
│   · …（7 族）            │     单位档案/建筑档案/科技树）    │
└──────────────────────────┴──────────────────────────────────┘
```

**为什么理论页要独立于目标流程**：目标是"必须按顺序做完的事"，资料是"可以随便翻的图鉴"。
把图鉴塞进目标会让教程变成几十步的填空题；分开之后玩家可以只做实操，
也可以先把阵营看明白再开打。理论页**不参与教程进度**，也不影响哈希。

**图鉴数据是生成的，不是手写的**（这条最重要）：

| 来源 | 内容 |
|------|------|
| `TutorialCodex.BuildRacePage` | 定位五维、人口上限、初始资源与初始单位（读 `RaceConfig`） |
| `TutorialCodex.BuildEconomyPage` | 经济来源（工人采集 / 范围采集建筑 / **菌毯格数换资源**） |
| `TutorialCodex.BuildUnitPages` | 每个单位的生命/移速/人口/花费/武器（伤害·射程·冷却·伤害类型）/建造·采集·治疗能力/专属机制/解锁条件 |
| `TutorialCodex.BuildStructurePages` | 每个建筑的生命/花费/可生产/可研究/前置/驻扎容量/菌毯·立场/自动生产 |
| `TutorialCodex.BuildTechPages` | 每项科技的名称/描述/类别/花费/研究时间/前置/解锁内容/全部效果字段 |
| `TutorialMechanics.BuildPages` | **人工撰写**的核心机制与打法结论（配置里没有"为什么"） |

所有数值都从 `ConfigDatabase` 实时读取并换算成玩家单位（射程 世界单位→格、冷却 tick→秒），
**改一次平衡，教程自动跟着变**，不会出现"教程写着 100 血、游戏里是 110"。
显示名走本地化表 `name.<Id>`（`Localization.Has` 先判存在，再 `Tr`），
所以教程里的名字与游戏 UI 天然一致。

**依赖红线**：`TutorialDefinitions.cs` **只依赖 `RtsMapData` / `MapDataOps`，绝不碰 `ConfigDatabase`**。
因为该文件会被无头测试工程编译（测试要验证教程地图能过校验器），而测试工程没有 Godot 配置层。
图鉴由 `TutorialCodex.HydrateAll(list)` 在 UI 侧单独挂上（幂等）：
**加一套教程只改 `TutorialDefinitions.cs`，UI 与图鉴都不用动。**

**点击「开始」的链路**（刻意绕过大厅）：

```
StartTutorial(def)
  → SimManager.PendingTutorial = def          // 静态待启动项，SimManager 建局时消费
  → NetworkManager.StartOfflineGameDirect(def.RaceId, def.PlayerTeam, TutorialRegistry.TutorialMapId)
  → Game 载入时把地图换成教程自带场地（MapId = "__tutorial"，64x64）
  → SimManager.StartTutorial → TutorialRuntime，HUD 由 TutorialHud 显示
```

不预置机器人：教程是单人引导，让玩家先选机器人/种族/地图只会增加门槛。

#### 玩家指令计数（教程"你真的按了这个操作"的判定基础）

像"移动单位""建造""研究科技"这类目标，**光看世界状态判不出来**——
单位本来就在动、建筑本来就有。所以 `DispatchActions` 里统一把每个锁步动作记进
`PlayerCommandStats`（按 队伍 × 指令大类 计数）：

```csharp
CommandStats.Record(netAct.PlayerID, netAct.ActionId, dispatchTick);
```

- 归类覆盖 `Move/AttackMove/Stop` → Move、`Build_*`/`RebuildWreckage` → Build、
  `Train*` → Train、`Garrison` → Garrison…**未知 ActionId 落到 Misc 而不是丢弃**。
- 表达式侧暴露 `cmd_used(team,'kind')` / `cmd_count(team,'kind')`，
  以及语义化别名 `move_orders / attack_orders / harvest_orders / build_orders /
  train_orders / research_orders / garrison_orders / any_orders`。
- **确定性**：只记录锁步动作（双端顺序相同）、队伍号取自协议字段、
  `GetStateHash()` 按队伍排序只混合"类别位掩码 + 次数"（不混 tick）。
  `CommandStats` 已纳入 `GetFullWorldSectionHashes()` 的 `CommandStats` 段——
  教程目标直接依赖它，不纳入就会出现"教程进度分叉检测不到"。
- 每局在 `LoadRuntimeMap` 里**整体替换新实例**，避免跨局脏状态（否则教程一开局就自动完成）。

#### 无头验证

`OPEN_TUTORIAL`（见 §C.7）：面板是纯 UI，只有真正构建一次才能发现节点/回调问题。
`=1` 打开面板并打印「教程条目数 / 开始按钮数 / 当前资料页目录数」；
`=start` + `OPEN_TUTORIAL_INDEX=N` 选中第 N 套再点「开始」，验证「面板 → 教程」整条链路。
`DUMP_TUTORIALS=1` 把每套教程的目标与全部资料页打到日志，另附
`[Chain]` 段打印每族"谁造谁 / 谁研究什么"（用来核对教程目标里写的具体 ID 是否真的存在）。

实测（8 套全部通过）：目标数 8/5/6/4/5/5/6/5，
资料页数 3/26/31/34/25/34/35/29。

> **踩过的坑**：详情区重建时只 `QueueFree()` 是**延迟**释放，节点要到帧末才离开树，
> 于是树里同时存在"旧的开始按钮"和"新的开始按钮"，`FindChildren` 先撞上旧的那个——
> 表现为"点第 N 个教程却启动了第 1 个"。修法是先 `RemoveChild`（立刻脱离树）再 `QueueFree`。

> **踩过的坑（静默失败，很值得记）**：资料页正文一开始**永远是空的**，但没有报任何错。
> 原因是用 `TreeItem.SetMetadata(0, "标题\0分组")` 编码两个值，而
> **Godot 的 Variant 在 C#↔引擎编组时把 `\0` 当字符串结束符**——
> 取回来只剩标题、分组名被截掉，于是 `PagesIn(section)` 查不到 → 静默 `return`。
> 教训：**不要把结构化数据塞进 metadata 字符串**，用索引（这里是 `si*1000+pi`）做键；
> 而且"UI 建出来了但是空的"这类 bug 必须有断言兜住，否则只有人眼能发现。
> 现在 `OPEN_TUTORIAL=start` 会直接断言正文非空并打印`正文 · 分组 / 页名`。

> **踩过的坑**：正文区和背景同色、且在 260px 高的目录下方，用户截图反馈"点了没反应"。
> 已改成：目录压到 220px、正文加 PanelContainer 边框、上方加一行"正文 · 分组 / 页名"定位。

### 19.7 渲染与美术管线（现状 + 已修 + 待办）

#### 现状：项目此前**没有任何环境与光照配置**

三条独立审计的一致结论，按严重度排：

| # | 问题 | 证据 |
|---|------|------|
| 1 | **全项目没有 `WorldEnvironment`** —— 无环境光/天空/色调映射/泛光 | grep `WorldEnvironment\|Environment\|Sky` 命中 0 |
| 2 | **唯一的光源只写了 transform** —— `shadow_enabled` 默认 false，**零阴影** | `Scenes/main.tscn:95-96`（另 3 个场景同） |
| 3 | **`project.godot` 完全没有 `[rendering]` 段** —— 无 MSAA/TAA/去色带 | 全文 62 行 |
| 4 | **VAT 批渲染是死代码** —— `Data/VAT/` 只有 10 个占位模型（Astronaut/Mech/Enemy\*），而 `UnitModelLibrary` 的 111 条映射**一条都不引用**它们 | `UnitVatRenderer.cs:135-145`、`UnitVisuals.cs:186-192` |
| 5 | **所有 107 个已映射 .glb 都没有 animations/skins** —— 全静态；8 处 `IdleAnimation/MoveAnimation` 是死配置 | GLB JSON 解析 |
| 6 | **60/94 模型违反管线文档**（`octree_resolution=64`，文档要求 32），61 个超 1 万面（`NanoAA` 7.4 万面） | `ArtRes/models/README.md:59-60` |
| 7 | **地表是零高度平面** —— 每格 4 顶点全在 `y=0`、法线全 `Vector3.Up` | `MapGround3D.cs:149-173` |
| 8 | **地形/菌毯贴图 64~128px 且 `mipmaps/generate=false`** —— 缩放时闪烁 | `ArtRes/imgs3d/*.import` 全部 23 个 |
| 9 | **血条没有 billboard** —— 55° 俯角下压扁 0.57×，拉远后低于 1 像素 | `UnitHealthBar3D.cs:126-135` |
| 10 | **`Unshaded` + `Emission` 自相矛盾** —— 引擎在 Unshaded 下忽略 Emission，所有 `EmissionEnergyMultiplier` 无效 | `GlowMaterialFactory.cs`、`HitFx.cs`、`HomingProjectile.cs`、`HarvesterMiningFX.cs` |

#### 已修（本轮）

**1. 新增 `SceneEnvironment` 自动加载单例**（`Scripts/Core/SceneEnvironment.cs`）

集中提供环境与光照，而不是往 4 份 `.tscn` 里各塞一份（"改 main 忘 main_1v1"是本项目的
常态风险）。低频自检（0.5s）挂载，因为存在**不换 `CurrentScene`** 的启动路径
（教程/离线直达是往根节点加子场景），只靠一次 `_Ready` 会漏挂。

```csharp
// 环境：天空 + 环境光 0.35 + Filmic 色调映射 + Glow(阈值 0.9, 强度 0.35) + 调整
// 刻意不开：SSAO / SSIL / SDFGI / 体积雾 —— 固定俯视 RTS 收益极低、掉帧明显
// 太阳：ShadowEnabled = true，max_distance = 2500（= 默认视高），4 级级联
```

配套 `project.godot` 新增 `[rendering]`：`msaa_3d=2`(4×)、`screen_space_aa=1`(FXAA)、
`use_debanding=true`、`anisotropic_filtering_level=2`。

> **注意**：`ScreenSpaceAA` / MSAA 是 **Viewport** 属性，不在 `Environment` 里，
> 只能配在 `project.godot`（`Environment` 上并没有这两个属性，写了会编译报错）。

**2. 修掉 `Unshaded` + `Emission` 的无效组合**

`GlowMaterialFactory.Boost(color, energy)` 把能量**归一化映射**到 `[1.0, 2.2]` 烘进
`AlbedoColor`。为什么不直接乘：原来 4/5/6 是 Emission 语义（可远超 1 倍），
直接当 albedo 倍率会一起过曝成白块、丢掉色相；映射之后
既让 `energy` 参数重新"有效"（不同值看得出区别），又保住颜色，
且超过 1.0 的部分在 Glow 打开后会自然泛光。

**3. 血条加 `Billboard`**（`UnitHealthBar3D.CreateBarMaterial`）——
`BillboardKeepScale = true`，缩远了也能读。

**4. 敌军闪白修复**（`UnitVisuals.cs`）—— `Main.Instance` 未就绪时本地玩家号是 `0`，
而队伍号从 1 开始，`myLocalId=0` 会把**所有单位**（含友军）判成敌人。
现与 `FogOfWar` 统一用 `-1` 作哨兵，未就绪时保持原色、等下次刷新再判定。

**5. 菌毯染色不再超 1.0**（`CreepVisual3D.cs`）—— 染色是**乘**在贴图上的，
任何通道 >1 都会削顶丢掉明暗层次（原来 1.05/1.35/1.45）。改为靠**压其他通道**得到同样色调。

**6. 记忆幽灵改半透明**（`UnitVisuals.CreateGhostCopy`）—— 原来是不透明实心灰，
比看不见还糟（分不清是可交互实体还是影像）。现为偏蓝半透明 `alpha=0.42`。

#### ⚠️ 待办与陷阱（改美术前必读）

- **`ExportEntityScenes` 会毁掉手做的 Model 子树**：74 个预制体里只有 **14 个**内嵌
  `Model` 节点（Artillery 的 Hull/Turret/Barrel 骨架、Liberator 的 `Model/Muzzle`），
  而 `EntityFactory3D` **从不创建 `Model` 节点**。按 `README.md:74-78` 无条件重跑导出
  会把这 14 个的手工结构抹平，直接打断 `BarrelPath` / `BottomMuzzlePath`。
  **要重导必须先让工厂能重建这 14 个。**
- **`Round Rover.glb` 不能删**：`MainMenuController.cs:912-913` 拿它当**包完整性哨兵**，
  删掉主菜单会永久显示"包缺失"。
- **`ArtRes/models/**/<Name>_0.png`（94 个，277.9 MB）是纯重复**：已验证每个都字节级
  包含在对应 GLB 内，且无任何代码引用；`export_filter="all_resources"` 会把它们打进包。
- **`Blueprint_PlantLeafDog` / `CaveWreckage` 没有模型映射**，且 `ArtRes/imgs3d/` 里
  也没有同名贴图 → 渲染成灰立方 + 浮动名字标签。
- **Cave(22) / Plant(14) 共 36 个实体没有预制体**：`ExportEntityScenes.cs` 的 `_targets`
  只有 74 条，从未加入这两族（运行时有 `EntityFactory3D` 兜底，但编辑器里不可编辑）。
- **`NanoBehemoth` 渲染约 4 格但逻辑 `FootprintTiles` 默认 1** → 碰撞/选中半径只有半格，
  模型与判定不一致。
- **`Data/VAT`（138.8 MB）当前完全不可达**：要么给真实单位烘焙，要么删掉。
  另注意 `UnitVatRenderer.VatDir` 只用 basename，而各族复用同名文件，**烘焙前必须先改成
  路径相对名**，否则目录会互相覆盖。

#### 诊断

`ART_DEBUG=1` 打印环境挂载结果（天空/环境光/泛光/色调映射/阴影光源数），
并在"有 3D 内容但缺光源"的场景里告警（纯 2D 菜单不报，避免噪音）。
`SETTINGS_DEBUG=1` 打印设置菜单的实际内容高度（防止加设置项后超出窗口把按钮挤没）。
`FX_DEBUG=1` 断言程序化网格（指令路径 / 框选边框）真的生成了几何
—— 这类网格最典型的失败模式是**静默为空**：不报错、节点在、但一个三角形都没有。
`HUD_DEBUG=1` 检查 HUD 底部面板（动作面板 / 生产队列 / 选中信息面板…）的
`GetCombinedMinimumSize()` 是否超过其在 `user_ui.tscn` 里**写死的像素尺寸**
—— 内容是代码长出来的、容器是手写像素，两边不同步就会静默裁掉控件。
`SETTINGS_RT=1` 检查设置的**三跳一致性**：注册表 → `Keybinds` → `InputMap`，
外加磁盘写回读回。`Keybinds` 从"手写 4 条"改成"注册表构建 30 条"后，
这条链任一环偏差都不会报错，只会表现成"改的键重启失效"。

> **`SETTINGS_RT` 实测**：30 个动作全部进 `Keybinds`（可改键 15）、默认冲突 0、
> 改键后 `InputMap` 生效、重置可回默认、`user://settings.cfg` 写回读回一致
> （`game_stop=74(J)` / `game_hold=76(L)`）。

> **`HUD_DEBUG` 的实测结论（2026-09）**：动作面板最小 `206×122` vs 实际 `209×126`，
> 15 个槽位 / 5 列 / 38px = 3 行 114px，容器 126px，**余量仅 12px**。
> 即"当前塞得下，但没有任何余量"——再动槽位尺寸或列数就会被裁。
> 其余面板（生产队列 / 选中信息 / 小地图 / 底部面板）均无裁切。
> 结论：**不需要立刻改**，但这是改动前必须先看的一条。

#### UI / 特效批（已修）

| 问题 | 现象 | 修法 |
|------|------|------|
| **框选是实心半透明绿块** | 把框内单位的颜色压掉，看不清框到了谁 | 改成**空心边框**：四条细长方形（`DrawBoxFrame`），宽度按相机高度自适应 |
| **指令路径用 `PrimitiveType.Lines`** | 1 像素线、锯齿明显、部分平台（Metal）支持差 | 改成 XZ 平面**带状网格**（ribbon），宽度随相机高度 1.2~12 世界单位自适应；并在落点画菱形标记 |
| **血条没有 billboard** | 55° 俯角压扁 0.57×，拉远后低于 1 像素 | `BillboardMode.Enabled` + `BillboardKeepScale` |
| **`Unshaded` + `Emission` 自相矛盾** | 引擎在 Unshaded 下忽略 Emission，`EmissionEnergyMultiplier` 全无效 | `GlowMaterialFactory.Boost()` 把能量归一化映射到 `[1, 2.2]` 烘进 albedo |
| **敌军闪白** | `Main.Instance` 未就绪时本地玩家号是 `0`，而队伍号从 1 起 → 所有单位判成敌人 | 与 `FogOfWar` 统一用 `-1` 作哨兵，未就绪时保持原色 |
| **菌毯染色超 1.0** | 染色是**乘**在贴图上的，>1 削顶丢明暗层次 | 压回 ≤1，靠压其他通道得到同样色调 |
| **记忆幽灵是不透明实心灰** | 比看不见还糟（分不清实体还是影像） | 改偏蓝半透明 `alpha=0.42` |
| **设置菜单底部按钮点不到** | 32 条键位使内容高达 **1601px**，窗口仅 648px，底部被裁掉 | 外层限高 `ScrollContainer`，面板取 `min(内容, 窗口)` |

#### 键位与菜单批（已修）

- **统一输入注册表** `Scripts/Settings/InputActions.cs`：动作、默认键、分组、冲突规则**唯一真相**。
  此前按键散落在 5 处（`GameSettings` 4 条 + `ActionPanel` 15 条 +
  `RaceBuildPanelUI` + `OrbitalPanelUI` + `UserController` 硬编码），玩家看不到也改不了。
- **真实冲突**：`A`=面板槽位6 vs 攻击移动、`T`=聊天 vs 槽位5、`S`=槽位7 vs 停止、
  `F1`=空闲工人 vs 菌毯扩散。重排键位后无冲突，并加了**启动自检**（`ReportKeybindProblems`）。
- **补上此前完全没有的快捷键**：攻击移动(A)、停止(S)、驻守(H)、全选作战单位(F2)、
  全选工人(F3)、**编队 Ctrl+数字 / 数字**。
- **镜头此前只能用鼠标贴边平移**：补方向键 + 中键拖拽。回中用 `Home` 而非空格
  —— 引擎内置 `ui_accept` 默认绑了空格与回车，会顺带触发"UI 接受"。
- **中键冲突**：中键既是地图信号又要拖屏，决定留给拖屏（RTS 标准），ping 改 `Ctrl+中键`。
- **输入消费顺序**：`UserController` 比 `UserUI` 先处理输入，所以全局 F1/F2/F3 会抢走
  面板的键。选择/编队/指令热键全部移入 `_UnhandledInput`（原来在 `_Process` 轮询，
  **绕过 UI 消费**），种族建造面板改用 `F4` 起。
  ⚠️ 已知上限：可用区只有 F4~F8 共 5 个，而植物 10 个建筑、洞穴 14 个
  —— 超出部分没有快捷键（按钮显示 `[-]`），要支持需做分页或第二排键位。

#### 地形贴图（已修）

原贴图基本是纯色块（实测独立颜色数：Grass 14 / Wall 4 / NanoCreep 3，均 64×64）。
用 `Scripts/Tools/GenTerrainTextures.gd` **程序化生成** 512×512 的 PBR 三件套
（albedo + normal + roughness）：

- **必须无缝平铺**：全部噪声用周期性 value noise（晶格坐标对 period 取模），
  法线也用环绕采样。否则四边对不上，铺开出现网格状接缝，在俯视大平面上极其显眼。
- 生成后独立颜色数：Wall 4→53、NanoCreep 3→90。
- 导入参数由 `ConfigureTerrainImports.gd` 设置：法线图必须**线性 + `normal_map=1`**
  （默认按 sRGB 处理会把法线方向掰弯），全部开 mipmap。
- **渐进替换**：`TextureLoader3D.ApplyTerrainPbr` 优先用新贴图，缺失时退回旧图，
  不会出现"地面整个变黑"。
- 顺带修正地形绕序：原来顶点顺序的叉积法线**朝下**，所以当年只能靠
  `CullMode.Disabled` 绕过（代价是填充率翻倍）。现在绕序修正、正常开背面剔除。

#### ★ 地形看不见的根因：三角形是背面朝向的（折腾了 4 轮）

**症状**：地面显示为一片纯蓝灰，完全没有贴图；但网格存在（81288 顶点）、
材质绑着（`MaterialOverride=True`）、贴图绑着（`albedo 512x512`）、
参数全对（`albedoColor=(1,1,1) uv1Scale=(1,1,1)`）。

**根因**：地形每格 quad 的顶点绕序使三角形**从上方看是背面**。
`StandardMaterial3D.CullMode` 一旦设成 `Back`（默认值），整块地面就被剔除掉了——
屏幕上那片区域显示的是**天空/背景**，不是地面。所以怎么看都像"贴图坏了"。

**为什么排查了 4 轮**——这段过程本身就是教训：

1. 我先"修正"了 `BuildGroundCellMesh` 的绕序**并**把 `CullMode` 从历史值
   `Disabled` 改成 `Back`，理由是"叉积法线朝下所以要修正"。**没有截图验证就改了。**
2. 之后每一轮对照实验都建立在这个未验证的改动上，于是出现自相矛盾的结果：
   - 设 `albedoColor=红 + Unshaded + CullMode.Disabled` → **画面变红** ✓
   - 设 `albedoColor=绿 + Unshaded`（沿用 `Back`） → **画面毫无变化** ✗
   同一个节点、同一个材质、只差一个通道，却一个生效一个不生效。
3. 我据此怀疑"存在多个地面实例、我改错了对象"，专门加了实例计数探针去查——
   实测**只有 1 个实例、1 次 `Build()`**，矛盾反而更深。
4. 最后才注意到：**红色那次唯一多设的属性就是 `CullMode.Disabled`**。
   差异一直摆在我眼前，我却盯着贴图、UV、三平面、mipmap 找了好几轮。

**教训**：
- 红色对照之所以"生效"，不是因为它证明了贴图链路正常，而是因为它**顺手关掉了剔除**。
  **对照实验必须只改一个变量**——我那次同时改了 4 个属性（颜色/贴图/着色模式/剔除），
  于是得出了一个错误结论并沿用了 4 轮。
- 遇到"两个几乎相同的实验结论相反"时，应当**逐属性 diff**，
  而不是去构造更复杂的假设（"多个实例"）。

**现状**：`CullMode = Disabled`（回到原始行为）。
`TERRAIN_CULL_BACK=1` 可实验性开启剔除——想真正修掉填充率翻倍，
必须先改对 `BuildGroundCellMesh` 的顶点绕序，**并用截图验证**再切回 `Back`。

#### UI 文案：动态拼出来的 key 最容易漏翻译

**症状**：Nano 的资源条上显示 `resource.nanobots: 163` 这样一个**裸 key**。

**根因**：`Localization.Tr(key)` 找不到条目时**原样返回 key**，调用方无从察觉。
而资源名是**由枚举动态拼**出来的（`"resource." + type.ToString().ToLowerInvariant()`），
`ResourceType` 有 9 个成员，CSV 里只写了 5 个 —— 新增资源类型时不会有任何编译错误，
只会在 HUD 上悄悄多一个裸 key。

**修法（两层）**：

1. 补全条目：`resource.wood / supply / biomass / nanobots`（中英各一份）。
2. 加**自检**：`Localization.ReportMissingKeys()` 在启动时把这类动态 key 全查一遍并报错；
   展示路径改走 `Localization.ResourceName(type)`，缺条目时退化成可读名
   （`NanoBots` → `Nano Bots`）而不是裸 key。

**教训**：凡是**代码拼接**出来的本地化 key（`resource.<枚举>`、`name.<id>`），
都不可能被"扫一遍 CSV 看缺没缺"发现，必须有一条自检。静态 key 靠肉眼，动态 key 靠断言。

### 19.8 视觉层与逻辑层必须同源（重要踩坑）
**症状**：换了地图之后，**屏幕上还是原来那张大地图**，但墙壁/阻挡/判定已经是新地图的
——看起来像"看不见的墙"。

**先搞清楚谁在渲染**（我第一轮修错了地方，就是因为没先确认这一点）：

| 层 | 节点 | 是否可见 |
|----|------|----------|
| `MapGrid.BaseMapLayer`（TileMapLayer） | 场景里的 2D 瓦片层 | **不可见**（`MapGrid._Ready` 里显式 `Visible = false`，注释写明"只作逻辑数据源"） |
| **`MapGround3D`（Node3D）** | 逐格拼出的 **3D 草地网格** + 墙体 MultiMesh | **这才是屏幕上真正看到的地形** |
| `CreepVisual3D` | 菌毯 MultiMesh | 可见 |
| `MapGrid` 的 `TileMapLayer` 之外的 `NavigationRegion2D` | Godot 导航 | 不可见 |

所以"改地图数据"必须让 **`MapGround3D` 重建**，光写 TileMapLayer 是看不见任何效果的。

**根因（两个叠加的时序问题）**：

1. `MapLoader.ApplyToTileMap` **从来没有被调用过** —— 接地图时只写了逻辑层 `SimGrid`。
   （即使调用了也只影响那个隐藏的 TileMapLayer，屏幕上仍然没变化。）
2. **Godot 的 `_ready()` 是子节点先于父节点**：`MapGrid._Ready`（→ `CallDeferred(InitSimGrid)`）
   与 `MapGround3D._Ready`（→ `CallDeferred(Build)`）都**早于** `Game._Ready`
   （→ `CallDeferred(SetupMatch)`）。等 `Game` 拿到地图数据时，
   **地面网格与墙体早就按旧地形建好了，之后没有任何东西触发重建**。
   实测日志：`[MapGrid] TryUseActiveMapData: ActiveMap=null preset=1`。

**修法**：
- `MapGrid.DataRevision` 在每次应用地图数据时 +1；
- `Game.LoadActiveMap` 显式调用 `MapGrid.ApplyDataMap(data)`，
  一次性把同一份 `RtsMapData` 写进可见的 TileMapLayer 与逻辑层 `SimGrid`；
- **`MapGround3D._Process` 监听 `DataRevision`**，变化时只重建"随地形变化的部分"
  （地面网格 + 墙体 MultiMesh），不动迷雾平面与 `CreepVisual3D`（它们与地形形状无关，
  重建只会多出重复节点）；
- **坐标系不做任何偏移**：地图数据格坐标 == 世界格坐标，
  与 `InitSimGrid` 的 `cell.X → SimVector2I(cell.X)`、`WorldToGrid` 的 `world/64`
  以及 `MapGround3D` 的 `cell.X*64+32` 完全一致。
  曾想加"居中偏移"，那会让渲染层与逻辑层差一个常量，只要漏补偿一处就重现本 bug。

**由此派生的第二条修复**：小地图在 `_Ready` 里算过一次地图范围，而那时地形数据还没应用，
算出来的是旧地图大小 → 范围不对、没填满框、没居中。
小地图同样改为监听 `MapGrid.DataRevision` 并重算（contain 缩放 + 居中偏移），
不再依赖"谁先 `_Ready`"。

> 教训：改"地图显示"之前先用日志/代码确认**到底是哪个节点在渲染**。
> 这个项目的地形有两套表示（隐藏的 TileMapLayer 用于反推逻辑，可见的 3D 网格用于显示），
> 只改一套必然出现"视觉与逻辑脱离"。

### 19.9 地图编辑器限制

- **编辑器视角缩放没有任何上下限**：滚轮或 `+`/`−` 可一路放大到单格铺满屏幕，
  也能缩到整张 256×256 一屏看完；`0` 回到 1:1（一格 = 64 像素）。
  打开/生成另一张地图**不会重置视角** —— 作者调好的视角应当保持。
  代码里只保留"数值安全"边界（不出现 0 / 非有限值 / int 溢出），不是设计上的缩放限制。
- 编辑器只在**编辑器/开发构建**里可用：`res://` 在导出后的游戏里是只读的，无法保存地图。
- 导出预设地图：`godot --headless --path . res://Scenes/Tools/ExportMaps.tscn -- --export-maps --gen-presets`
- 诊断：`TRIGGER_DEBUG=1` 每 20 tick 打印触发器状态（触发次数 / 最近错误）。
- **1v1 / Debug 两张图的地形是代码生成的**（`MapGrid.BuildSmallMap1v1` / `BuildDebugMap`，
  由场景的 `MapPresetId` 决定），不从 TileMap 来。导出时会丢弃落在地图外的出生点，
  需要作者在编辑器里重新摆放 —— 这是已知残缺，不是 bug。

## 20. 相关文档

- [ROADMAP.md](./ROADMAP.md)：P0-P3 商业化清单与进度。
- [ArtRes/models/README.md](./ArtRes/models/README.md)：AI 3D 建模流程、风格锚点、踩坑记录。
- `D:\DEV\README.md`：总流水线（建模 → 替换 → 测试）。
- `Tools/run_ci.ps1`：本地 CI 脚本。

---

# 附录 A：核心类公共接口清单（代码级）

> 以下签名以当前源码为准；字段太多时只列关键项，完整列表直接看对应 `.cs`。

## A.1 表现层实体

### Unit（`Scripts/Units/Unit.cs`）
```csharp
public string UnitName;                 // 实体 ID（= 配置表 key）
public int TeamID;
public SimUnit SimUnitData;             // 逻辑数据
public SimEntity LogicEntity => SimUnitData;
public bool IsStructure => false;
public Vector2I GridPosition;
public string DisplayName => UnitName;
public Texture2D Icon => IconProfile;   // 预制体里手动指定
public void SetSelected(bool selected);
public bool IsDeadOrNull();
public List<Vector2> GetWaypointPositions();
public UnitLife LifeModule; public UnitVisuals VisualsModule;
public UnitCombat CombatModule; public UnitHarvest HarvestModule;
public UnitActionController Brain;
```

### Structure（`Scripts/Units/Structure.cs`）
```csharp
public string StructureName;
public StructureState CurrentState;     // Blueprint / Constructing / Active / Destroyed
public bool IsUnderConstruction;
public float ConstructionProgress;
public List<OrderData> RallyQueue;      // 集结点队列
public void AddRallyCommand(OrderData cmd, bool append);
public void AddBuilder(IEntity b); public void RemoveBuilder(IEntity b);
public void InitAsBlueprint(Dictionary<ResourceType,float> costs);
public void CancelConstruction(); public void PromoteFromBlueprint();
public virtual void OnDestroyed();      // 死亡/摧毁入口（主线程）
public SimStructure SimStructureData; public SimEntity LogicEntity => SimStructureData;
```

## A.2 逻辑层（确定性数据，纯 C# + Fix64）

### SimEntity（`Scripts/Simulation/SimEntity.cs`）
```csharp
public int ID; public int TeamID; public FPVector2 Position; public FP Radius;
public bool IsDead; public bool CannotBeHealed; public int LastDamageSourceId;
public FP VisionRange; public FP MaxHp; public FP Hp;
public FP MaxShield; public FP Shield;
public Dictionary<int,FP> Defenses;     // key = (int)DamageType
public int ArmorType; public BuffContainer Buffs;
public FP ResourceAmount; public int ResourceType;   // 资源建筑用
public FP CargoAmount; public FP CargoCapacity; public int CargoType; // 工人用
public abstract void LogicTick(FP fixedDelta);
```

### SimUnit 关键字段（`Scripts/Simulation/SimUnit.cs`）
移动：`Velocity / MaxSpeed / TargetPosition / HasTarget / Path / CurrentWaypointIndex`
身份：`UnitTypeId / TeamID / IsAir / IsGhost / FootprintTiles / BoundStructureId`
战斗：`CombatTargetId / ActiveWeaponCooldown / WeaponRotationCooldown / WeaponCooldowns / ActiveActionName / MaxAmmo / Ammo / BaseMaxAmmo`
架设：`DeployState(0普通/1架设中/2已架设/3收起中) / DeployTimer / DeployMaxHpMultiplier / DeployRangeBonus / DeployAoeRadius / DeployRequiresTargetCircle / DeployTargetCenter / DeployTargetRadius`
技能：`HeroEnergy / HeroMaxEnergy / SkillCooldown / Plasma* / LeapCooldown / Dash*`
种族：`IsDemonUnit / FieldRadius / FieldSpeedMultiplier / SelfCreepRadius / SpeedOnCreepMultiplier / BehemothCanHarvest / CanHitStructuresOverride / BonusDamageVsStructure / AoeRadiusBonus`
便利属性：`IsDeployed => DeployState == 2`；`IsDeployBusy`

### SimStructure 关键字段（`Scripts/Simulation/SimStructure.cs`）
`GridPosition / GridWidth / GridHeight / GridSize / CurrentState / ConstructionProgress / StructureTypeId`
`AutoProduceTimer / AutoProduceMode / AutoProduceIntervalCurrent / AutoProduceBoost*`
`FieldRadius / LifespanTimer / AmmoRangeTiles / AmmoRangeAffectsAir / GarrisonedCount`
`PendingSpawnUnitIds`（建成后待生成单位，防遍历中插入）

### SimWorld 关键字段（`Scripts/Simulation/SimWorld.cs`）
```csharp
public SortedDictionary<int,SimUnit> Units;
public SortedDictionary<int,SimStructure> Structures;
public List<SimProjectile> Projectiles;
public SimGrid Grid; public SimRandom RNG; public SimCreepGrid CreepGrid;
public FP FixedDelta = (FP)0.05m;       // 20Hz
public SimSpatialGrid SpatialGrid;
// 纳米地毯
public int CarpetSpreadCharges/MaxCharges; public FP CarpetSpreadCost/CooldownTime/
    RadiusTiles/Duration; public int CarpetIncomeCellsPerResource;
```
常用方法（`SimWorld.Tick.cs` / `Hashes.cs`）：`Tick()`、`AddUnit/AddStructure/AddProjectile/Remove*`、`FindSimEntity(id)`、`FindNearestUnit/FindNearestStructure`、`GetStateHash()`、`GetSectionHashes()`。

## A.3 模拟核心 SimManager（`Scripts/Core/SimManager.cs`）
```csharp
public static SimManager Instance;
public SimWorld World;
public readonly object WorldLock;       // 模拟线程持有；主线程遍历 World 必须加锁
public float LogicFps; public bool IsPaused;
public int SimulationSpeed { get; set; } // 1/2/4/8，联机强制 1x
public void SetPaused(bool paused);
public bool IsBotTeam(int team);
public void SetBotTeams(string teamsCsv, int difficulty);
public void SetBotDifficulty(int team, int difficulty);   // 逐机器人难度
public void ConfigureBotsFromOverrides();                  // 开局重读大厅配置
public void ConfigureTeamGroups();                         // 从网络映射同步 2v2 分组
public bool AreTeamsHostile(int a, int b);                 // 唯一敌对判定
public int GetTeamGroup(int team);
public FP GetBotResourceMultiplier(int team);              // 难度资源倍率
public IEntity FindEntityById(int id);
public void RegisterEntityNode(IEntity e); public void UnregisterEntityNode(int id);
public ConcurrentDictionary<int,IEntity> EntityNodes;
public bool TryGetUnitVisualState(int id, out float x, out float y, out float vx, out float vy);
public List<string> GetChatSnapshot(); public List<MapPing> GetPingSnapshot(int nowTick);
public bool IsRunning; public void ResetSimulation();
public long GetFullWorldHash();
public List<KeyValuePair<string,long>> GetFullWorldSectionHashes();
public string BuildSectionHashString();                       // "SectionName=HEX;..."
public static Dictionary<string,long> ParseSectionHashString(string value);
public static string CompareSectionStrings(string a, string b); // 返回差异区段名，空=unknown
public string GetDesyncReport(int tick, long localHash, long peerHash, int peerPlayerId, string sectionInfo);
public static bool CheatInstantBuild;       // 作弊：瞬间建造
public static bool CheatInstantResearch;    // 作弊：瞬间研究
public static bool IsInControlRange(SimUnit unit);         // 多足控制圈
public static bool IsTargetVisibleToTeam(int team, IEntity target);
public const string VoteKindRematch = "VoteRematch";
public const string VoteKindSurrenderTeam = "VoteSurrenderTeam";
public static string BotTeamsOverride; public static int BotDifficultyOverride;
public static List<BotLobbyConfig> BotConfigsOverride;     // 大厅逐机器人
```

`BotLobbyConfig` 结构（`SimManager` 内嵌）：`Pid`（固定 100+ 的机器人的网络玩家号）、`Team`（出生点/队伍）、`Race`、`Difficulty`（1-3）、`Group`（2v2 分组）。

## A.4 锁步 LockstepManager（`Scripts/Network/LockstepManager.cs`）
```csharp
public static LockstepManager Instance;
public List<int> PlayerIDs; public int LocalPlayerID;
public int CurrentTick; public bool ReplayMode;
public int TickDelay; public int LateTickTolerance;
public bool DesyncTriggered; public List<long> ReplayTickHashes;
public void SendAction(NetAction action);                  // 锁步指令唯一入口
public bool IsTickReady(int tick);
public List<NetAction> ConsumeActions(int tick);
public void AdvanceTick(); public void ConfirmTick(int tick, int playerId);
public void SaveReplayToFile(string path); public void LoadReplayFromFile(string path);
public string VerifyReplayHashes();
public void ResetLockstep(); public void StartInitialBuffer();
public void FlushOutbox(); public void SendSyncHeartbeat();
public void SendStateHash(int tick, long hash);
```

### NetAction 协议（`Scripts/Network/NetAction.cs`）
```csharp
public struct NetAction
{
    public const int ProtocolVersion = 2;
    public const int MaxEntityIdCount = 512;
    public const int MaxStringLength = 256;
    public int Version; public uint Sequence;
    public int PlayerID;      // 网络玩家号（≠ TeamID）
    public int TargetTick;
    public string ActionId;   // 指令名
    public string ActionIdExtra;
    public int[] EntityIDs;
    public long TargetX; public long TargetY;  // 世界坐标 ×1000
    public int TargetEntityID;
    public bool IsQueue;
    public byte[] Serialize();
    public static bool TryDeserialize(byte[] data, out NetAction action);
    public static NetAction Deserialize(byte[] data);  // 失败时返回 PlayerID=-1 的空指令
    public bool IsValid();                              // ActionId 非空且 PlayerID>0
    public bool IsSystemAction(); public bool IsLockstepAction();
}
```

序列化字节布局见附录 C.1；旧协议 v1 兼容解析见 C.2。

## A.5 网络大厅 NetworkManager（`Scripts/Network/NetworkManager.cs`）
```csharp
public static NetworkManager Instance;
public static bool OfflineMode;
public bool IsHost; public string SelectedMapId = "Classic";
public Dictionary<int,int> PeerTeamMap;      // pid -> 出生点
public Dictionary<int,string> PeerRaceMap;
public Dictionary<int,int> PeerGroupMap;     // pid -> 队伍分组（2v2）
public Dictionary<int,bool> PeerObserverMap;
public bool LocalIsObserver;
public Dictionary<int,bool> PeerReadyMap;
public Dictionary<ulong,int> SteamToPlayerId;
public Dictionary<int,ulong> PlayerIdToSteam;
public int LocalTeamID; public int HostPlayerID; public ulong HostSteamID;
public int LocalNetworkPlayerID;
public void BeginOfflineHost();
public void HostStartGame();                                  // 房主开局（生成清单）
public void StartOfflineGameDirect(string race, int team, string mapId);
public void SendLobbyUpdate(int teamId, string raceName, bool isReady,
                            int group = 0, bool observer = false);
public void SendMapSelection(string mapId);
public int GetPlayerTeam(int networkPlayerId);
public string GetPlayerRace(int networkPlayerId);
public bool IsPlayerObserver(int networkPlayerId);
public bool GetPlayerReadyStatus(int playerId);
public void FinalizeLocalIdentity();
public void LockSession(); public void UnlockSessionForLobby();
public static bool IsOfflineLaunchRequested();
```

## A.6 玩家 Player / PlayerData
### Player（`Scripts/Player/Player.cs`）
`public RaceData Race; public PlayerData PlayerData; public PlayerController Controller; public int TeamId;`

### PlayerData（`Scripts/Player/PlayerData.cs`）
```csharp
public void AddResource(ResourceType type, FP amount);
public void AddResource(ResourceType type, float amount);
public void AddResources(Dictionary<ResourceType,float> resources);
public void ResetResources(Dictionary<ResourceType,float> resources);
public bool TryConsumeResources(Dictionary<ResourceType,float> costs);
public bool HasResources(Dictionary<ResourceType,float> costs);
public float GetResource(ResourceType type);
public int GetMaxSupply(); public int GetUsedSupply();
public bool HasTech(string techId); public int GetTechLevel(string techId);
public void GrantTech(string techId);
public bool StartResearch(string techId, float durationSeconds);
public string AdvanceResearch(FP delta);
public long GetStateHash();
```

## A.7 动作系统
### UnitAction（`Scripts/Units/Actions/UnitAction.cs`，所有指令的基类）
```csharp
public string ActionName; public int SlotIndex; public ActionLayer Layer; public ActionLayer BlockingLayers;
public virtual void Initialize(IEntity unit);
public virtual void Setup(FPVector2 targetPos, IEntity targetObj);
public virtual void OnEnter(); public virtual void OnUpdate(double delta); public virtual void OnExit();
public virtual bool CanExecute(); public virtual bool TryPayCost();
public virtual float GetCooldownMax(); public virtual float GetCooldownRemaining();
public virtual long GetDeterministicExtraHash();
```

### UnitActionController（`Scripts/Units/Actions/UnitActionController.cs`）
```csharp
public void Initialize(IEntity owner);
public void StartAction(string actionName, bool isAuto = false, bool isQueue = false);
public void StartAction(string actionName, FPVector2 targetPos, IEntity targetObj,
                        bool isAuto = false, bool isQueue = false);
public T GetAction<T>(string name);
public UnitAction GetActiveProductionAction();
public UnitAction[] GetProductionQueueSnapshot();
public int GetProductionQueueCount();
public void CancelProduction(int uiIndex); public void CancelLastProduction();
public IEntity GetActiveBuildTarget(); public IEntity GetActiveHarvestSource();
public void StopAction(UnitAction action); public void StopAll();
public void RefreshActionCache();           // 动作树重建后主线程刷新缓存
public void LogicTick(double delta);
```

### 锁步指令字符串（ActionId）速查
| ActionId | 用途 | 额外数据 |
|----------|------|----------|
| `Move` / `AttackMove` / `Attack` / `Harvest` / `Repair` / `Stop` | 基础指令 | TargetX/Y(+1000)、TargetEntityID、IsQueue |
| `Build_<StructureId>` | 建造 | TargetX/Y 蓝图中心、TargetEntityID=蓝图 |
| `Train_<UnitId>`（内部用 `<UnitId>` 入队） | 生产 | 无需目标 |
| `Research_<TechId>` | 研究 | 无需目标 |
| `Rally_Move` / `Rally_Attack` / `Rally_Harvest` | 生产建筑集结点 | TargetX/Y、TargetEntityID、IsQueue |
| `Garrison` / `ReleaseWorkers` | 泰伦驻扎/放出 | TargetEntityID=建筑 |
| `Deploy` | 泰伦架设/收起（解放者带圈） | TargetX/Y=范围圈中心 |
| `Surrender` | 投降 | 无 |
| `Chat` | 聊天 | ActionIdExtra=文本 |
| `Ping` | 地图信号 | TargetX/Y=位置 |
| `Vote` | 投票 | ActionIdExtra=VoteRematch/VoteSurrenderTeam |
| `HeroSkill1` / `HeroSkill2` / `PlasmaStrike` / `CruiserMobility` / `Radar` / `OrbitalStrike` / `Stim` / `ResourceExchange` | 技能/面板 | 依技能 |
| `CheatToggleBuild` / `CheatToggleResearch` | 调试作弊 | 无 |
| `SelfDestruct` | 自毁 | 无 |

## A.8 武器 Weapon（`Scripts/Units/Weapons/Weapon.cs`）
```csharp
public string WeaponName; public int Damage; public DamageType DmgType;
public int AttackRange; public int Cooldown; public bool CanTargetGround/Air/Structure/Neutral;
public bool HasAreaDamage; public int AreaRadius; public int AreaEdgeDamagePercent;
public Marker3D FirePoint;
public void Initialize(IEntity owner); public void ApplyConfig();
public virtual void Fire(IEntity target); public virtual bool FireAtGround(FPVector2 groundPos);
public virtual void FireGroundVolley(FPVector2 groundPos, bool consumeCooldown);
public void FireVolley(IEntity target, bool consumeCooldown);
public bool CanTarget(IEntity target); public bool IsInRange(IEntity target);
public bool CanFire(); public void ForceCooldown();
public Vector3 GetFirePointWorld();       // 已做 IsInstanceValid 守卫
```
派生：`Rifle`（即时命中）、`ProjectileWeapon`（弹道，生成 `SimProjectile` + `HomingProjectile`）。

## A.9 生成入口
### EntitySpawner
`public IEntity SpawnEntity(string name, int teamId, FPVector2 exactPos);`
策略：`Scenes/Races/**` 同名 `.tscn` 优先（失败回退）→ `EntityFactory3D.TryCreate`。

### EntityFactory3D（代码工厂，`Scripts/Core/EntityFactory3D.cs`）
```csharp
public static bool TryCreate(string name, int teamId, FPVector2 pos,
                             out IEntity entity, out Node3D root);
public static Unit/Structure 构建：CreateConfiguredUnit / CreateConfiguredBuilding /
    CreateUnitBlueprint / CreateResourceNode / CreateNanoBehemoth / CreateShrine
public static void RebuildUnitActions(Unit unit);       // 动作树唯一来源
public static void RebuildStructureActions(Structure s);
public static void ApplyUnitStats(Unit u, string id);   // 预制体/工厂共用数值注入
public static void ApplyStructureStats(Structure s, string id);
public static Weapon CreateWeaponNode(string weaponId);
```
`TryCreate` 已知分支：各族单位/建筑 ID、`Blueprint_*`（多足）、`Tower/Shrine`（中立）、`IronOre/GasSpring/MetalMine/WildFruit`、`NanoBehemoth`。

## A.10 事件总线 / 本地化 / 设置
### GameEventBus（`Scripts/Core/Events/GameEventBus.cs`）
```csharp
public static List<IEntity> CurrentSelection;
public static event Action<List<IEntity>,int> SelectionChanged;
public static event Action<string> ActionTriggered;
public static void PublishSelectionChanged(List<IEntity> sel, int localTeamId);
public static void PublishActionTriggered(string actionId);
```

### Localization（`Scripts/Settings/Localization.cs`）
```csharp
public static string Current;
public static string Tr(string key);
public static string Tr(string key, params object[] args); // 占位符 {0}，不是 %d
public static string TrName(string id);
public static bool Has(string key); public static void SetLanguage(string code);
public static string DetectSystemLanguage();
public static void EnsureLoaded(string code); public static void ReloadAll();
public static void ApplySceneTranslations(Node root);
```

### GameSettings（`Scripts/Settings/GameSettings.cs`）
`Load() / Save() / ApplyAll() / ApplyAudio() / ApplyVideo() / ApplyKeybinds()`，持久化到 `user://settings.cfg`。

字段与 cfg 文件格式见附录 C.5。

---

# 附录 B：配置表字段速查（代码级）

> 配置全部是 `Resource` 派生类，放在 `Data/Configs/*.tres`。**ID = 文件名（不含扩展名）**，不是 `WeaponId/TechId` 字段。所有跨表引用一律用字符串 ID，运行时由 `ConfigDatabase` 解析；引用悬空、数值非法或缺失关键字段会在加载期抛异常（目录打不开仅打印错误）。

## B.1 ConfigDatabase（`Scripts/Data/Configs/ConfigDatabase.cs`）

```csharp
public static bool IsLoaded;
public static void LoadAll();                      // 幂等，加载 res://Data/Configs
public static void LoadFromDir(string dir);        // 导出包自动兼容 .remap 后缀
public static UnitConfig GetUnit(string id);
public static StructureConfig GetStructure(string id);
public static WeaponConfig GetWeapon(string id);
public static RaceConfig GetRace(string id);
public static BuffConfig GetBuff(string id);
public static TechConfig GetTech(string id);
public static IEnumerable<KeyValuePair<string,UnitConfig>> GetAllUnits();
public static IEnumerable<KeyValuePair<string,StructureConfig>> GetAllStructures();
```

校验规则（`ValidateAll`，加载期拦截，不满足直接 `InvalidOperationException`）：
- 单位：`MaxHp>0`、`BuildTime>=0`、可移动则 `MoveSpeed>0`、可采集则 `CarryCapacity/HarvestAmountPerCycle>0`、`SupplyCost>=0`、所有成本非负；`WeaponIds/BuildableStructureIds/ResearchableTechIds/RequiredTechIds/LeapTechId/PigeonTechId/Skill2RequiredTechId` 必须可解析。
- 建筑：`MaxHp>0`、`GridWidth/GridHeight>0`、生产建筑有 `TrainableUnitIds` 时 `ProductionQueueSize>0`、`SupplyProvided/SupplyUsed>=0`；`AutoProduceUnitIds/AutoProduceModeUnitIds/BonusUnitOnCompleteIds/TrainableUnitIds/ResearchableTechIds/ProvidesTechIds/RequiredTechIds/WeaponIds/AuraBuffIds` 必须可解析。
- 科技：`ResearchTimeSeconds>0`、成本非负；`RequiredTechIds/TargetUnitIds/UnlockStructureIds/GrantWeaponIds` 可解析。
- 种族：`AiArmyTarget>0`、`AiWorkerTarget>=0`、`AiBuildOrderIds/AiUnitMixIds/AiTechOrderIds/AiExpansionBaseIds/AiBuildTargetCounts` 可解析。
- 等级门标记（`T1~T4`、`TerranT3` 等）是合法字符串，跳过引用校验。

## B.2 EntityConfig（所有单位/建筑共享基类）

| 分组 | 字段 | 类型/默认 | 说明 |
|------|------|-----------|------|
| Vitals | `MaxHp` | float 100 | 最大生命 |
| | `MaxShield` | float 0 | 护盾（先扣盾再扣血） |
| | `VisionRange` | float 350 | 视野（世界单位） |
| | `ArmorType` | enum 0 | Light 等护甲类型 |
| Defenses | `DefKinetic/DefThermal/DefExplosive/DefEM/DefBeam` | float 0 | 五种伤害类型的固定减免值 |
| Cost | `Costs` | `Dictionary<ResourceType,float>` | 造价；`ResourceType` 枚举值 0=Metal,1=Wood,2=Food,3=Supply,4=Energy,5=Biomass,6=NanoBots,7=Money,8=Gas,9=Data |
| | `BuildTime` | float 1 | 建造/训练秒数 |

## B.3 UnitConfig（继承 EntityConfig）

| 分组 | 字段 | 说明 |
|------|------|------|
| Movement | `MoveSpeed` | 移动速度（世界单位/秒） |
| | `FootprintTiles` | 占地格数，1 格 = 64 世界单位，影响碰撞半径与挤位 |
| | `CannotBeHealed` | 恶魔：禁疗 |
| | `Kamikaze` | 熔岩旗手：自动冲向最近敌人自爆 |
| | `NoControlNeeded` | 多足：无需控制 |
| | `ControlRangeTiles` | 多足：控制圈半径（格） |
| | `AutoSubmitHarvest` / `AutoHarvestRadiusTiles` / `AutoHarvestPerSecondPerNode` | 多足范围采矿 |
| | `BuildRangeTiles` / `HarvestRangeTiles` | 多足远程建造/采集距离（格） |
| | `IsTechBuilding` / `ResearchableTechIds` | 单位当科技建筑用（牧羊人） |
| | `CreepDefenseBonus` / `AutoClearCreep` | 菌毯全防加成/自动清毯 |
| | `HasLeap` + `LeapCooldownSeconds/LeapRangeTiles/LeapTechId` | 腾跃 |
| | `HasPigeon` + `PigeonTechId` | 死亡揭示视野 |
| | `FieldRadius` / `FieldAuraEnemyAttackSpeedMultiplier` / `FieldAuraEnemyIncomingDamageMultiplier` / `FieldAuraTickSeconds` | 恶魔立场/英雄光环 |
| | `MultiShotTargets` | 一轮打多个目标（地狱领主 5×3） |
| | `HeroEnergyMax/HeroEnergyRegenPerSecond` | 英雄能量 |
| | `Skill1*` / `Skill2*` | 技能数值：Cost、RangeTiles、DurationSeconds、MoveSpeedBonus、EnemyMovePenalty、Multiplier、Damage、TargetMode（0=自施 1=敌方 2=己方生产建筑 3=地面方向）、Kind（0=无 1=英雄能量 2=电浆炮 3=自动/手动 4=兴奋剂 5=机动模式）、Name、BehaviorId |
| | `Plasma*` | 电浆炮状态机：Windup/Recovery/Cooldown/AutoTargetRange |
| | `IsAir` / `FlyHeight` | 飞行 |
| | `Tags` | 任意字符串标签（Worker/Biological/Mechanical/Air/HeavyArmor…） |
| | `Acceleration` / `TurnSpeed` | 加速度/转向速度 |
| | `CanMove` / `IsGhostByDefault` | 是否可移动/默认无视碰撞 |
| Supply | `SupplyCost` / `SupplyProvided` / `SupplyUsed` | 占人口/供人口/负人口（牧羊人 -5） |
| Combat | `WeaponIds` | 武器 ID 列表 |
| | `AllowAttackMoveWithoutWeapon` | 无武器也可 AttackMove |
| | `WeaponRotationInterval` | 多武器轮换间隔秒 |
| | `DefaultAttackMove` / `AttackMoveFiresWhileMoving` | 右键默认攻移/跑打 |
| Harvest | `CanHarvest` / `HarvestAmountPerCycle` / `HarvestCycleTicks` / `CarryCapacity` / `HarvestableResources` | 采集 |
| Build | `CanBuild` / `BuildableStructureIds` / `BuildPowerPercent` | 建造 |
| Production | `IsWorker` / `IsHero` / `IsSummoned` / `RequiredTechIds` | 身份/解锁条件 |
| Repair | `CanRepair` / `RepairPerSecond` / `RepairCostPerHp` / `RepairRange` | 维修（SCV） |
| Heal | `CanHeal` / `HealRangeTiles` / `HealPerSecond` | 自动治疗 |
| Mobility | `MobilitySpeedBonus/DurationSeconds/CooldownSeconds/SpeedMultiplier/RangeTiles` | 战巡机动模式 |
| Terran | `MaxAmmo` / `CanDeploy` / `DeployTimeSeconds` / `DeployedMaxHpMultiplier` / `DeployedAttackRangeBonusTiles` / `DeployedAoeRadiusTiles` / `DeployOnlyWeapon` / `DeployRequiresTargetCircle` / `DeployTargetCircleTiles` / `CanGarrison` / `CanSwitchAmmoMode` / `EnergyRegenInAmmoRange` / `Skill2RequiredTechId` / `Skill1AttackRangeBonus` | 弹药/架设/驻扎/切弹种 |
| Role | `Role` / `OffenseRating` / `DefenseRating` / `SupportRating` / `EconomyRating` | 定位与评分 |

## B.4 StructureConfig（继承 EntityConfig）

| 分组 | 字段 | 说明 |
|------|------|------|
| Placement | `GridWidth/GridHeight` | 占地格数（1 格 = 64 世界单位） |
| | `RequiresCreep` / `RequiredCreepType` | 需要菌毯才能放 |
| | `CreepSpreadRadius` / `CreepSpreadTime` / `GeneratedCreepType` | 自动铺毯 |
| | `BlocksMovement` / `BlocksBuildingPlacement` / `IsAir` | 阻挡 |
| Auto Harvest | `AutoHarvestRadiusTiles` / `AutoHarvestPerSecondPerNode` / `IsDropOffPoint` / `AcceptableResourceTypes` | 采集器/交付点 |
| Structure Type | `IsMainBase` / `IsProductionBuilding` / `IsTechBuilding` / `IsDefenseBuilding` / `IsResourceBuilding` / `IsInteractiveBuilding` / `IsUnique` | 建筑类型 |
| | `FieldRadius` / `RequiresSacrifice` / `AutoBuild` / `GlobalVision` / `PassiveHpRegenPerSecond` | 恶魔立场/献祭建造/自动施工/全局可见/被动回血 |
| | `AutoProduceUnitIds` / `AutoProduceIntervalSeconds` / `AutoProduceMaxBound` / `AutoProduceModeUnitIds` / `AutoProduceModeName` / `AutoProduceAffectedByRiftTechs` | 免费自动生产（恶魔） |
| | `BonusUnitOnCompleteIds` / `LifespanSeconds` | 建成赠单位/限时建筑 |
| Terran | `AmmoRangeTiles` / `AmmoRangeAffectsAir` / `GarrisonCapacity` / `GarrisonIncomePerSecondPerUnit` | 弹药补给/驻扎产肉 |
| Production | `TrainableUnitIds` / `ProductionQueueSize` / `CanSetRallyPoint` | 生产队列 |
| Tech | `ResearchableTechIds` / `ProvidesTechIds` / `RequiredTechIds` | 科技 |
| Combat | `WeaponIds` | 防御武器 |
| Supply | `SupplyProvided` / `SupplyUsed` | 人口 |
| Resource | `ProvidesResourceIncome` / `IncomeResourceType` / `IncomeAmountPerCycle` / `IncomeCycleTicks` / `ResourceType` / `ResourceAmount` | 被动收入/中立资源点 |
| Aura | `HasAura` / `AuraRange` / `AuraAttackSpeedBonus` / `AuraHpRegenPerSecond` / `AuraEnemyRangeReductionTiles` / `AuraBurnDamagePerSecond` / `AuraBuffIds` | 光环 |
| Panel Skills | `RadarRadiusTiles` / `RadarDurationSeconds` / `RadarEnergyCost` / `StrikeRadiusTiles` / `StrikeDamage` / `StrikeEnergyCost` / `ExchangeMetalAmount` / `ExchangeGasAmount` / `ExchangeEnergyCost` / `PanelSkillMode` | 轨道控制中心面板；`PanelSkillMode` 位标志 1=雷达 2=轨道炮 4=资源交换 |

## B.5 WeaponConfig（`Scripts/Data/Configs/WeaponConfig.cs`）

| 分组 | 字段 | 说明 |
|------|------|------|
| Identity | `WeaponId` / `DisplayName` | 武器 ID（文件名）与显示名 |
| Damage | `Damage` / `DamageType` / `RangeType` | 伤害/类型/射程分类 |
| | `BonusDamageVsArmorType` / `BonusDamage` / `BonusDamageTags` | 针对护甲类型或标签追加伤害 |
| Range & Timing | `AttackRange` / `CooldownTicks` / `WindupTicks` | 射程（世界单位）/冷却（20 tick=1 秒）/前摇 |
| Projectile | `ProjectileBehavior` | 0=Instant 1=Homing 2=Fixed 3=Lob |
| | `ConeAngleDegrees` | 扇形攻击角度（0=不用） |
| | `ProjectileScene` / `ProjectileSpeed` / `HitRadius` | 视觉弹体/速度/命中半径 |
| | `LobDurationSeconds` / `LobHeight` | 抛物线 |
| Impact Override | `FootprintScaledDamage` / `ImpactDamagePerFootprintSq` / `ImpactRadiusTiles` / `ImpactSlowMoveMultiplier` / `ImpactSlowAttackMultiplier` / `ImpactSlowSeconds` | 电浆炮特殊命中 |
| Targeting | `CanTargetGround/Air/Structure/Neutral` / `RequiredTargetTags` / `ForbiddenTargetTags` | 目标限制 |
| Area Damage | `HasAreaDamage` / `AreaRadius` / `AreaEdgeDamagePercent` | AOE（0~100 边缘伤害%） |
| Hit FX | `HitFxRadius` / `HitFxDuration` / `HitFxColor` / `TracerThickness` / `TracerDuration` / `MuzzleFlashRadius` / `MuzzleFlashDuration` | 表现层特效（HitFx 对象池消费） |

工具方法：`IsProjectile`、`ToProjectileSpec()`（映射为纯逻辑 `ProjectileSpec`）、`RequiresTag/ForbidsTag`。

## B.6 TechConfig（`Scripts/Data/Configs/TechConfig.cs`）

| 分组 | 字段 | 说明 |
|------|------|------|
| Identity | `TechId` / `DisplayName` / `Description` / `Icon` | 基础 |
| Research | `Category`（General/Combat/Defense/Mobility/Economy）/ `ExclusiveGroup`（A/B 二选一）/ `RequiredTechIds` / `IsRepeatable` / `RepeatCostMultiplier` / `TargetTag` / `TargetUnitIds` / `GrantNoControlNeeded` / `VisionBonus` / `DefenseType` / `RequiresHarvester` / `ResearchTimeSeconds` / `Cost` | 研究规则 |
| Effects | `UnlockStructureIds` / `UnlockSkillIds` / `GrantWeaponIds` / `GrantWeaponCount` | 解锁 |
| | `AuraRadiusTiles` / `AuraAttackSpeedBonus` / `AuraHpRegenPerSecond` / `AuraEnemyRangeReductionTiles` / `AuraBurnDamagePerSecond` | 光环数值 |
| Active Skill | `SkillWindupSeconds` / `SkillDurationSeconds` / `SkillCooldownSeconds` / `SkillFireIntervalSeconds` / `SkillProjectileSpeedTiles` / `SkillProjectileRangeTiles` / `SkillProjectileRadiusTiles` / `SkillDamage` / `SkillDamageType` | 主动技能（离子吐息等） |
| | `CreepIncomeMultiplier` / `SpreadCostMultiplier` / `SpreadCooldownMultiplier` / `HarvesterIncomeMultiplier` | 纳米经济 |
| | `AutoProduceIntervalMultiplier` / `FireDamageBonus` / `FieldRadiusBonus` / `FieldMoveSpeedBonus` | 恶魔 |
| | `HpBonusPercent` / `KineticResist/ThermalResist/ExplosiveResist/EmResist/BeamResist` / `SelfCreepRadius` / `SpeedOnCreepMultiplier` / `BehemothCanHarvest` | 通用 Buff 型效果 |
| Union Effects | `UnitHpRegenPerSecond` / `MoveSpeedMultiplier` / `AttackRangeBonus` / `ProductionTimeMultiplier` / `StimAttackSpeedMultiplier` / `StimMoveSpeedMultiplier` / `StimDurationSeconds` / `StimSelfDamage` | 联盟 |
| Terran Effects | `GrantKnockbackAttacks` / `KnockbackTiles` / `DeployDamageReduction` / `DeployTimeMultiplier` / `AmmoCapMultiplier` / `AmmoRefundOnKill` / `AoeRadiusBonusTiles` / `AllowStructureTargeting` / `BonusDamageVsStructure` | 泰伦 |

## B.7 BuffConfig / BuffInstance

`BuffConfig`（静态 .tres）：`BuffId` / `DisplayName` / `Duration` / `MaxStacks` / `DamageMultiplier` / `IncomingDamageMultiplier` / `ArmorBonus` / `MoveSpeedMultiplier` / `HpRegenPerSecond` / `ShieldRegenPerSecond` / `OnTickEffectId`（预留）。

运行时 `BuffInstance`（纯逻辑，挂在 `SimEntity.Buffs`）：
```csharp
public string BuffId; public int Stacks; public int MaxStacks;
public FP RemainingTime;      // <=0 永久
public FP TotalDuration;      // UI 用，永久不进哈希
public int SourceEntityId = -1;
public FP DamageMultiplier / IncomingDamageMultiplier / ArmorBonus / MoveSpeedMultiplier;
public FP HpRegenPerSecond / ShieldRegenPerSecond;
public FP KineticResistMultiplier / ThermalResistMultiplier / ExplosiveResistMultiplier /
    EmResistMultiplier / BeamResistMultiplier;
public FP AttackSpeedMultiplier / AttackRangeBonus / FlatDamageBonus;
public long GetStateHash();
```
`BuffContainer`：`Buffs`（只读列表）、`AddBuff(...)`（同 ID 叠层、刷新时长取较长）、`Tick`、各查询方法（`GetAttackRangeBonus/GetMoveSpeedMultiplier/...`）。

## B.8 RaceConfig / StartingEntityConfig / ResourceStartConfig

`RaceConfig` 字段：
- Identity：`RaceId` / `DisplayName` / `Description` / `Icon`。
- Six Dimensions：`Offense/Defense/Economy/Support/Technology`（0-5）、`Difficulty`（1-5）、`RoleSummary`、`WeaknessDescription`。
- Starting Resources：`StartingResources`（`ResourceStartConfig[]`：`Type` / `Amount` / `MaxCapacity`）。
- Starting Entities：`StartingEntities`（`StartingEntityConfig[]`：`Kind`=Unit/Structure/Neutral/ResourceNode，`EntityId`，`TeamMode`=OwnerTeam/Neutral/EnemyTeam，`Offset`，`RotationDegrees`，`Count`，`Spacing`）。
- Nano Carpet：`CarpetSpreadCost/CooldownSeconds/RadiusTiles/MaxCharges/DurationSeconds`、`VisionMode`（0=标准 1=地毯视野）、`CarpetIncomeCellsPerResource`。
- Available Content：`AvailableUnitIds` / `AvailableStructureIds` / `AvailableTechIds` / `AvailableBuffIds` / `VisibleResourceTypes` / `MaxEnergy` / `MaxSupplyCap` / `SupplyCapMode`（0=按供给和 1=供给封顶 2=上限+供给-占用）。
- Recommendation：`SuggestedBuildOrderIds` / `CoreUnitIds` / `RecommendedAllyRaceIds`。
- Race AI：`AiBuildOrderIds` / `AiBuildTargetCounts` / `AiTechOrderIds` / `AiUnitMixIds` / `AiWorkerTarget` / `AiWorkerUnitId` / `AiExpansionBaseIds` / `AiArmyTarget`。

工具方法：`HasUnit/HasStructure/HasTech`、`GetStartingResourceDictionary()`。

---

# 附录 C：协议与文件格式

## C.1 NetAction v2 字节布局（`Serialize()`）

用 .NET `BinaryWriter` 顺序写入（字符串 = 7-bit 长度前缀 + UTF-8，截断到 256 字符）：

| # | 字段 | 类型 | 字节 | 说明 |
|---|------|------|------|------|
| 1 | `Version` | Int32 | 4 | 恒等于 `ProtocolVersion = 2`，兼作协议头识别 |
| 2 | `Sequence` | UInt32 | 4 | 发送者本地递增，配合 `PlayerID` 强去重 |
| 3 | `PlayerID` | Int32 | 4 | 网络玩家号（**≠ TeamID**） |
| 4 | `TargetTick` | Int32 | 4 | 执行目标逻辑帧 |
| 5 | `ActionId` | string | 变长 | 指令名 |
| 6 | `ActionIdExtra` | string | 变长 | 附加字符串 |
| 7 | EntityIDs 数量 | Int32 | 4 | 0~512，超长截断 |
| 8 | `EntityIDs[]` | Int32×N | 4N | 选中实体 ID |
| 9 | `TargetX` | Int64 | 8 | 世界坐标 ×1000 |
| 10 | `TargetY` | Int64 | 8 | 世界坐标 ×1000 |
| 11 | `TargetEntityID` | Int32 | 4 | 目标实体 ID（-1 = 无） |
| 12 | `IsQueue` | Boolean | 1 | 是否入队 |

反序列化安全规则：空包拒绝；`EntityIDs` 长度 <0 或 >512 拒绝；`ActionId` 为空拒绝；任何读取异常返回 false。

## C.2 NetAction v1（旧协议兼容）

旧包**第一个 Int32 不是 2**，按 v1 解析：`PlayerID(4)` → `TargetTick(4)` → `ActionId` → `ActionIdExtra` → 数量 → `EntityIDs` → `TargetX(8)` → `TargetY(8)` → `TargetEntityID(4)` → `IsQueue(1)`。解析后 `Version=1`、`Sequence=0`，走弱去重（`PlayerID|ActionId|...` 字符串 key）。

## C.3 系统指令编码（不走锁步缓冲）

| ActionId | 编码 | 说明 |
|----------|------|------|
| `LOBBY_UPDATE` | `TargetEntityID=team`（出生点），`ActionIdExtra="race\|group\|obs"`，`IsQueue=ready` | 旧客户端无 `\|` 时按纯 race 解析，group=team |
| `LOBBY_MAP` | `ActionIdExtra=mapId`（Classic / 1v1 / Debug） | 房主广播地图 |
| `LOBBY_PLAYER_MAP` | `EntityIDs=pid[]`，`ActionIdExtra=steamId` 逗号串（顺序对应） | Steam 映射 |
| `GAME_PREPARE` | `PlayerID=1`（仅房主），`ActionIdExtra=出生清单` | 见下 |
| `GAME_TICK_CONFIRM` | `TargetTick=tick`，`EntityIDs=[]` | tick 确认 |
| `GAME_RESEND_REQUEST` | `TargetTick=tick`，`EntityIDs=[]` | 请求补发 |
| `Sync` | `TargetTick=CurrentTick+TickDelay`（补发时可指定），`EntityIDs=[]` | 锁步心跳/占位指令 |
| `HashCheck` | `TargetTick=tick`，`TargetX=hash`，`ActionIdExtra=区段哈希串` | 状态哈希校验 |
| `DESYNC_TRIGGER` | `TargetTick=tick`，`TargetX=本地hash`，`TargetY=对端hash`，`TargetEntityID=对端pid`，`ActionIdExtra=差异区段` | 脱步广播 |

`IsSystemAction()` 判定：`ActionId` 以 `LOBBY_` 或 `GAME_` 开头。`Sync/HashCheck/DESYNC_TRIGGER` 不以该系统前缀开头，但它们由 `LockstepManager.ReceiveExternalAction` 内部消化（`Sync` 入占位缓冲、`HashCheck/DESYNC_TRIGGER` 处理完直接 return），不会投递给模拟逻辑。

**GAME_PREPARE 权威出生清单**（`ActionIdExtra`，分号分隔，每项冒号分隔）：
```
pid:team:race:group:obs;pid:team:race:group:obs;...
```
例：`1:1:Union:1:0;101:2:Demon:1:0;202:0::0:1`
- `pid`：网络玩家号（>0）；`team`：出生点/队伍；`race`：空或“未选择”时回退 `Union`；`group`：2v2 分组，缺省 = team；`obs=1`：旁观者（清空 team/race/group）。
- 两端 `ApplyAuthoritativeSpawnManifest` 无条件覆盖本地大厅映射，保证双端一致。

## C.4 回放文件格式（`SaveReplayToFile/LoadReplayFromFile`）

Godot `FileAccess` 的 `Store32/Store64` 顺序（按引擎默认字节序写入；同机/同版本回放无需关心字节序）：

```
UInt32 tickCount
重复 tickCount 次:
  UInt32 tick
  UInt32 actionCount
  重复 actionCount 次:
    UInt32 payloadLength
    byte[]  NetAction.Serialize()（C.1 布局）
UInt32 hashCount
重复 hashCount 次:
  UInt64 tickHash          // RecordedTickHashes
```

回放验证：重放时逐 tick 记录 `ReplayTickHashes`，结束后与文件内哈希逐项比对；`VerifyReplayHashes()` 返回空串 = 一致，否则返回 `mismatch at hash#N: replay=... recorded=...`。

## C.5 settings.cfg（`user://settings.cfg`）

Godot `ConfigFile` INI 格式：
```ini
[general]
language="zh_CN"
[audio]
master=1.0
music=1.0
sfx=1.0
[video]
fullscreen=false
[keys]
game_pause=80
game_surrender=75
game_chat=84
```
- `keys` 的值是 Godot `Key` 枚举的 int（`PhysicalKeycode`）。
- 默认键位：`game_pause=P`、`game_surrender=K`、`game_chat=T`；`LANGUAGE` 环境变量优先级最高。
- 只写 `user://`，禁止写工程目录。

## C.6 本地化 CSV（`Localization/<code>.csv`）

```csv
key,text
settings,设置
name.union_rifleman,步枪兵
action.move,移动
race.union,联合
```
- 第一行固定 `key,text`；UTF-8。
- 回退链：当前语言 → `en_US` → 原 key。
- 命名约定：`name.<entityId>` 内容名、`action.<actionId>` 动作按钮、`race.<raceId>` 种族名。
- 场景节点翻译：`Control` 挂 `metadata/tr_key`，`ApplySceneTranslations(root)` 递归刷 `Label/Button/RichTextLabel`。
- 可用语言：zh_CN、en_US、ja_JP、ko_KR、de_DE、fr_FR、es_ES、pt_BR、ru_RU、it_IT。

## C.7 命令行与环境变量

启动参数：
| 参数 | 作用 |
|------|------|
| `--offline` / `--single` / `--no-steam` | 无 Steam 单机模式（`NetworkManager.OfflineMode=true`，本机=玩家 1/房主） |

环境变量：
| 变量 | 作用 |
|------|------|
| `LANGUAGE` | 覆盖语言（设置档之上） |
| `BOT_TEAMS` / `BOT_DIFFICULTY` | 旧版机器人：逗号分隔队伍 / 1-3 难度 |
| `BOT_RACES=pid:Race,pid:Race` | 无 Steam 多族 AI 回归测试：按玩家号覆盖机器人种族 |
| `SIM_SPEED=1..8` | 无头/离线测试倍速（等价 AIShowcase 的 AI_TEST_SPEED） |
| `AUTO_BOTS=N` / `AUTO_BOT_RACES=...` / `AUTO_START=1` | 无头回归：走真实大厅机器人路径（pid 100+）自动加机器人并开局 |
| `AUTO_REMATCH=1` | 无头回归：45 秒后模拟“回大厅再开一局”（验证跨局状态重置） |
| `SIM_PROFILE=1` | 模拟系统级性能探针 |
| `RECORD_REPLAY=<user://路径>` / `REPLAY_TICKS=<n>` / `AUTO_SURRENDER_TICK=<n>` | 录制回放 |
| `REPLAY_FILE=<路径>` / `REPLAY_DETERMINISTIC=1` | 回放校验 |
| `AI_TEST_SPEED` / `AI_TEST_TICKS` / `AI_TEST_RACE1/RACE2` | AI 演示倍速/时长/种族 |
| `AI_TEST_TEAMS` / `AI_TEST_GROUPS` / `AI_TEST_RACES` / `AI_TEST_BOTS` / `AI_TEST_OBSERVER=1` / `AI_TEST_PID_TEAM=pid:team` | 多队伍/分组/旁观者/pid≠team |
| `STRESS_UNITS` / `STRESS_NO_UI=1` / `STRESS_HIDE` / `STRESS_WALLS=1` / `STRESS_NO_FIGHT=1` | 压测场景 |
| `OPEN_SETTINGS=1` | 直接打开设置菜单 |
| `OPEN_TUTORIAL=1` / `OPEN_TUTORIAL=start` / `OPEN_TUTORIAL_INDEX=N` | 无头回归：打开教程面板（打印条目/资料页数），`=start` 选中第 N 套并点开始，验证「面板 → 教程」完整链路 |
| `DUMP_TUTORIALS=1` | 打印全部教程结构（目标 + 资料页）与每族生产链（详见 §19.6） |
| `ART_DEBUG=1` / `SETTINGS_DEBUG=1` / `FX_DEBUG=1` / `HUD_DEBUG=1` / `SETTINGS_RT=1` | 美术与 UI 自检（环境光照 / 面板布局 / 网格几何 / HUD 裁切 / 键位往返，详见 §19.7） |

---

# 附录 D：事件、哈希与确定性边界

## D.1 SimEventQueue（模拟线程 → 主线程唯一出口）

```csharp
public enum SimEventType { HealthChanged, Died, ResourceAmountChanged, ResourceDepleted }
public readonly struct SimEvent
{
    public readonly SimEventType Type;
    public readonly int EntityId;
    public readonly float A;   // 例如 HealthChanged: 当前 HP
    public readonly float B;   // 例如 HealthChanged: 最大 HP
}
public static class SimEventQueue
{
    public static void EnqueueMain(Action action);   // 视觉/节点操作延迟到主线程
    public static bool TryDequeueMain(out Action action);
    public static void Enqueue(in SimEvent e);
    public static bool TryDequeue(out SimEvent e);
}
```

红线：模拟线程里禁止 `GetNode/CreateTween/QueueFree/GlobalPosition/发信号`；必须 `EnqueueMain` 且闭包只捕获值类型。主线程在 `SimManager._Process` 末尾统一派发（屏障协议，派发完才放行下一 tick）。

## D.2 GameEventBus（主线程 UI 总线）

```csharp
public static class GameEventBus
{
    public static List<IEntity> CurrentSelection { get; private set; }
    public static event Action<List<IEntity>, int> SelectionChanged;
    public static event Action<string> ActionTriggered;
    public static void PublishSelectionChanged(List<IEntity> selection, int localTeamId);
    public static void PublishActionTriggered(string actionId);
}
```

用途：`UserController` 发布选择/动作；`ActionPanel`、`SelectionInfoPanel`、`ProductionQueueUI`、`GameFX3D` 订阅并自行节流。**模拟线程禁止发这里的信号**。

## D.3 SimWorld 哈希管线

`SimWorld.GetWorldHash()` 顺序：计数器 → 单位 → 建筑 → 弹体 → 菌毯 → 地毯 → RNG 状态。基础算法为 FNV-1a 风格：
```
seed = 1469598103934665603L
Mix: hash ^= value; hash *= 1099511628211L
```
- 坐标编码：`ToRaw1000 = (long)(value * 1000)`（Fix64 → 千分之一精度整数）。
- 实体级（`SimEntity.GetStateHash`）：ID、TeamID、ArmorType、Position×1000（用两个黄金常数分别混合 X/Y，防止对称抵消）、Hp×100、Shield×100、ResourceAmount×10、CargoAmount×10、CargoType、Buff 哈希。
- `SimUnit/SimStructure` 各自叠加移动/武器冷却/弹药/架设/自动生产/驻扎等状态；`SimStructure` 还逐个字符混入 `StructureTypeId/PendingSpawnUnitIds`。
- 区段哈希：`GetFullWorldSectionHashes()` 返回 `(区段名, hash)`；`BuildSectionHashString()` 格式 `SectionName=HEX;SectionName=HEX`；`ParseSectionHashString/CompareSectionStrings` 用于双端定位首个差异区段。
- 脱步报告：`GetDesyncReport(...)` 输出 tick、双方全量哈希、差异区段、世界 dump、指令历史，保存为报告文件供 diff。

## D.4 确定性哈希单位约定

| 数据 | 编码 | 示例 |
|------|------|------|
| 世界坐标 | ×1000 取整 | `(FP)123.456m → 123456` |
| HP/Shield | ×100 | 血量精度 0.01 |
| 资源/货舱 | ×10 | 精度 0.1 |
| 冷却/倍率 | ×1000 | 攻击速度等 |
| 字符串 | 逐字符混入 FNV-1a | BuffId、UnitTypeId 等 |

新增逻辑字段后必须加入 `GetStateHash()`，否则脱步无法被发现。

## D.5 DamageResolver / DamageContext

```csharp
public struct DamageContext
{
    public int DamageType;      // DamageType 枚举
    public FP BaseDamage;       // 面板基础伤害
    public int BonusDamageType; // 针对的护甲类型
    public FP BonusDamage;      // 命中该护甲时的追加伤害
    public FP Pierce;           // 护甲穿透（预留）
    public int SourceId; public int TargetId;
    public bool CanCrit; public FP CritChance; public FP CritMultiplier; // 预留
}
public static class DamageResolver
{
    public static void RegisterTypeModifier(int damageType, int armorType, FP multiplier);
    public static FP GetTypeModifier(int damageType, int armorType);
    public static FP Resolve(DamageContext ctx, SimEntity target, SimEntity source);
}
```

结算顺序（`SimEntity.TakeDamage`）：堡垒守卫替伤/减伤 → `DamageResolver.Resolve`（类型修正/护甲/Buff 倍率）→ 先扣护盾 → 再扣血 → `Hp<=0` 标记死亡 → 击杀回弹（泰伦科技）。所有数值均为 Fix64，纳入实体哈希。

# 附录 E：全代码库类索引

> 覆盖 `Scripts/` 下全部 173 个 C# 文件。类型/成员由脚本从源码提取（public/protected），
> 注释取自源码中的 XML/行注释；无注释条目补了一行“作用”说明。完整签名以对应 `.cs` 为准。

## E.1 Core（核心系统）

### Scripts\Core\CreepManager.cs
  作用: 菌毯格子管理（2D 逻辑层）：记录每格菌毯类型与归属，向表现层与 AI 提供查询。
- **public partial class CreepManager : Node2D**
  成员: Instance, IsCellVisibleToPlayer, _EnterTree(), _Ready(), _ExitTree(), RevealCells(), AddCreep(), RemoveCreep(), GetActiveCreep()

### Scripts\Core\CreepVisual3D.cs
- **public partial class CreepVisual3D : Node3D**
  注释: 3D 菌毯视觉：只显示“当前被点亮格子”上的菌毯（与 2D 逻辑一致）， 用 MultiMesh 合批渲染扁方块；纳米菌毯按归属分成“我方/敌方”两种颜色。
  成员: _Process()

### Scripts\Core\CubeMeshBuilder.cs
- **public static class CubeMeshBuilder**
  注释: 六面体网格生成器：每个面显式 UV(0,0)~(1,1)， 保证任意尺寸的方块每个面都显示完整贴图（配合 CullMode.Disabled 双面可见）
  成员: Build()

### Scripts\Core\DeathVisuals.cs
- **public static class DeathVisuals**
  注释: 死亡视觉唯一实现：单位与建筑共用（灰化/下沉/延迟释放）。
  成员: PlaySinkAndFree()

### Scripts\Core\EntityFactory3D.cs
- **public static class EntityFactory3D**
  注释: 实体工厂：负责创建单位/建筑的 3D 骨架（模块、数值、武器、范围显示）。 动作面板不再在这里逐单位手写，统一由 RebuildUnitActions / RebuildStructureActions 从配置表生成（Unit._Ready / Structure._Ready 与工厂共用同一入口）。
  成员: ApplyUnitStats(), ApplyStructureStats(), TryCreate(), RebuildUnitActions(), CreateWeaponNode(), RebuildStructureActions()

### Scripts\Core\Events\GameEventBus.cs
- **public static class GameEventBus**
  注释: 游戏内事件总线：解耦输入层（UserController）与表现层（面板/特效/队列）。 主线程专用；模拟线程 → 主线程仍走 SimEventQueue（确定性锁步边界不变）。 面板各自订阅自己关心的事件，自行节流刷新，不再被控制器直接调用。
  成员: CurrentSelection, SelectionChanged, ActionTriggered, PublishSelectionChanged(), PublishActionTriggered()

### Scripts\Core\FogOfWar.cs
- **public partial class FogOfWar : Node2D**
  成员: RememberStructureGhost(), Instance, CurrentViewerTeam, new(), CurrentVisibleCells, VisionTexture, ExplorationTexture, _Ready(), _Process(), WorldToFogPos(), WorldToFogRadius(), AddTemporaryReveal(), GetVisionSources()

### Scripts\Core\FogPainter.cs
  作用: 迷雾 2D 绘制：把 FogOfWar 的可视/探索贴图画到地图遮罩上。
- **public partial class FogPainter : Node2D**
  成员: _Draw()

### Scripts\Core\FormationSolver.cs
  作用: 阵型解算：给定数量/目标中心/间距，输出一列定点数落点（单位集结用）。
- **public static class FormationSolver**
  成员: SolveFP()

### Scripts\Core\GameFX3D.cs
- **public partial class GameFX3D : Node3D**
  注释: 3D 指令特效：选中单位路径线、框选矩形、点击标记
  成员: _Ready(), _ExitTree(), SetupPathDrawing(), UpdateBox(), SpawnClickMarker(), _Process()

### Scripts\Core\HealthBarBatchRenderer.cs
- **public static class HealthBarBatchRenderer**
  注释: 血条批渲染：普通单位血条（黑色背景 + 绿色填充）合入两个 MultiMesh， 400 个单位从 800 个 MeshInstance3D + 800 次绘制降到 2 次绘制。 当前处于停用状态（仅批渲染单位会用到，而 IsBatchableModel 返回 false）。
  成员: Pos, Width, Ratio, Visible, EnsureParent(), Register(), Unregister(), Update(), Flush(), DebugSummary()

### Scripts\Core\HitFx.cs
- **public static class HitFx**
  注释: 命中特效：亮色光球 + 地面扩散光圈 半径 / 时长 / 颜色全部由武器配置表传入
  成员: SpawnTracer(), SpawnMuzzleFlash(), Spawn(), SpawnConeSweep()

### Scripts\Core\Main.cs
- **public partial class Main : Node2D**
  成员: Instance, LocalNetworkPlayerID, LocalTeamID, LocalPlayerID, _EnterTree(), _Ready(), StartSimulation()

### Scripts\Core\RTSCamera.cs
- **public partial class RTSCamera : Camera3D**
  注释: 伪 3D RTS 相机：倾斜上帝视角（透视投影，近大远小） 移动方式：鼠标贴边 / 小地图；滚轮缩放（FOV）
  成员: _Ready(), _Process(), SetViewCenter(), _UnhandledInput()

### Scripts\Core\SimEventQueue.cs
- **public enum SimEventType**
- **public readonly struct SimEvent**
- **public static class SimEventQueue**
  注释: 模拟线程 → 主线程的事件队列。 锁步模拟不允许碰 Godot 对象（信号/节点），所有跨层事件先入队， 由主线程在 SimManager._Process 末尾统一派发。
  成员: Type, EntityId, A, B, SimEvent(), EnqueueMain(), TryDequeueMain(), Enqueue(), TryDequeue()

### Scripts\Core\SimManager.BotAI.Combat.cs
- **public partial class SimManager**
  注释: ========================================================= 分族对战 AI —— 战斗 / 波次 / 扩张 / 种族特化 =========================================================

### Scripts\Core\SimManager.BotAI.Core.cs
- **public partial class SimManager**
  注释: ========================================================= 分族对战 AI —— 核心调度 + 每队状态（重构版） 结构：
  成员: Team, RaceCfg, WaveMode, WaveTarget, WaveTick, WaveRallyFront, TowerRaidPos, PendingExpansion, ArmyPower, EnemyPower, new(), ExpansionPos, HomeAnchor

### Scripts\Core\SimManager.BotAI.Wanderer.cs
- **public partial class SimManager**
  成员: Kind, Home, Mission, HasMission, WaitingRecruits, WaitTicks, Index, new(), IsWandererAttackingUnit()

### Scripts\Core\SimManager.cs
- **public partial class SimManager : Node**
- **public struct BotLobbyConfig**
- **public sealed class ActiveVoteState**
- **public readonly struct MapPing**
  注释: 大厅机器人列表（逐机器人种族/难度/组别，优先于 BotTeamsOverride）。 地图信号：锁步数据，渲染层按 ExpireTick 过期消失。
  成员: Instance, World, LogicFps, SimulationSpeed, IsPaused, SetPaused(), Pid, Team, Race, Difficulty, Group, new(), GetBotResourceMultiplier(), ConfigureBotsFromOverrides(), SetBotTeams(), SetBotDifficulty(), ConfigureTeamGroups(), AreTeamsHostile(), GetTeamGroup(), IsBotTeam(), MatchOver, Kind, ProposerPid, TickDeadline, CurrentVote, Position, PlayerID, ExpireTick, MapPing(), GetChatSnapshot(), GetPingSnapshot(), IsRunning, TryGetUnitVisualState(), QueueGarrison(), RegisterEntityNode(), UnregisterEntityNode(), FindEntityById(), _EnterTree(), _ExitTree(), _Process(), StartSimThread(), StopSimThread(), IsInControlRange(), IsTargetVisibleToTeam(), CountAliveSegments(), GetFullWorldHash(), GetFullWorldSectionHashes(), BuildSectionHashString(), ParseSectionHashString(), CompareSectionStrings(), GetDesyncReport(), ResetSimulation()

### Scripts\Core\SimManager.Systems.cs
- **public partial class SimManager**
  注释: SimManager 领域拆分：主文件保留核心编排（Tick 顺序/指令分发/哈希）， 各领域系统按文件组织，后续方法按 Skills / Economy / RaceSystems 迁入。

### Scripts\Core\TechEffects.cs
- **public static class TechEffects**
  注释: 科技效果统一入口：效果全部按 TechConfig 字段驱动， 新增科技只需填配置表（目标 tag + 效果数值），无需再写分支代码。
  成员: ApplyToPlayer(), ApplyToEntity(), GetAutoProduceIntervalMultiplier(), GetCreepIncomeMultiplier(), GetSpreadCostMultiplier(), GetSpreadCooldownMultiplier(), GetHarvesterIncomeMultiplier(), GetProductionTimeMultiplier(), GetResearchSpeedMultiplier(), GetProductionCostMultiplier(), GetLifestealPercent()

### Scripts\Core\TextureLoader3D.cs
- **public static class TextureLoader3D**
  注释: 伪 3D 贴图加载：直接读 PNG 生成 ImageTexture， 不依赖 Godot 编辑器导入缓存（新增图片后无需手动导入）
  成员: LoadPng()

### Scripts\Core\UnitModelLibrary.cs
- **public sealed class UnitModelInfo**
- **public static class UnitModelLibrary**
  注释: 实体 -> 3D 模型映射表。 数据来源：Quaternius Ultimate Space Kit (CC0, https://quaternius.com/packs/ultimatespacekit.html)。 模型原始单位为 Blender 单位（GLB 内已自带 scale=100），这里按 1 格 = 64 世界单位换算。
  成员: VisualHeight, TopY

### Scripts\Core\UnitVatRenderer.cs
- **public static partial class UnitVatRenderer**
  注释: 顶点动画纹理（VAT）实例化渲染：把骨骼动画烘焙成纹理，用 MultiMesh 批量绘制， 每个单位只占用一个实例槽位，动画在 GPU 上按 TIME 播放，draw calls 与单位数量无关。 数据由 Scripts/Tools/VatBakeTool.gd 离线烘焙，存放在 res://Data/VAT/&lt;模型名&gt;/。
  成员: Node, Multimesh, new(), Buffer, _Process(), RegisterManagedVisual(), UnregisterManagedVisual(), EnsureParent(), FlushPerFrame(), HasVat(), Register(), Unregister(), SetTransform(), SetHidden(), SetColor(), Play(), DebugSummary()

### Scripts\Core\WorldScanner.cs
- **public static class WorldScanner**
  成员: IsOperable(), Raycast(), BoxSelect(), BoxSelectAll(), ScreenSelect(), ScreenSelectAll()

## E.2 Data（数据 / 配置 / 枚举）

### Scripts\Data\ActionViewFactory.cs
- **public static class ActionViewFactory**
  注释: 动作按钮视图统一工厂：单位/建筑命令卡共用同一套“动作 → EntityAction”转换
  成员: Build()

### Scripts\Data\CombatRating.cs
- **public static class CombatRating**
  注释: 单位战斗力评分：综合 DPS、有效生命、射程、移速、技能与特殊机制， 全部从配置表读取（数据驱动），用于单位卡展示、AI 选兵/估价。
  成员: ComputeUnitRating(), ComputeStructureRating(), BuildRatingTable(), ComputeRawDps()

### Scripts\Data\Configs\BuffConfig.cs
- **public partial class BuffConfig : Resource**
  成员: Duration, MaxStacks, ArmorBonus, HpRegenPerSecond, ShieldRegenPerSecond, IsValidConfig()

### Scripts\Data\Configs\ConfigDatabase.cs
- **public static class ConfigDatabase**
  注释: 配置数据库：运行时加载 res://Data/Configs/ 下的 .tres 配置表， 以文件名（不含扩展名）作为 ID，供实体工厂 / 对战初始化查询。
  成员: IsLoaded, LoadAll(), LoadFromDir(), GetUnit(), GetStructure(), GetWeapon(), GetRace(), GetBuff(), GetTech(), GetAllUnits(), GetAllStructures()

### Scripts\Data\Configs\EntityConfig.cs
- **public partial class EntityConfig : Resource**
  成员: MaxHp, MaxShield, VisionRange, DefKinetic, DefThermal, DefExplosive, DefEM, DefBeam, Costs, BuildTime, IsValidConfig()

### Scripts\Data\Configs\RaceConfig.cs
- **public partial class RaceConfig : Resource**
  成员: Offense, Defense, Economy, Support, Technology, Difficulty, CarpetSpreadCost, CarpetSpreadCooldownSeconds, CarpetSpreadRadiusTiles, CarpetSpreadMaxCharges, CarpetSpreadDurationSeconds, VisionMode, CarpetIncomeCellsPerResource, MaxEnergy, MaxSupplyCap, SupplyCapMode, AiWorkerTarget, AiArmyTarget, HasUnit(), HasStructure(), HasTech(), GetStartingResourceDictionary(), IsValidConfig()

### Scripts\Data\Configs\ResourceCostConfig.cs
- **public partial class ResourceCostConfig : Resource**
  成员: Amount, IsValid(), CloneCost(), ToString()

### Scripts\Data\Configs\StartingEntityConfig.cs
- **public partial class StartingEntityConfig : Resource**
- **public enum StartingEntityKind**
- **public enum StartingEntityTeamMode**
  成员: RotationDegrees, Count, Spacing, IsValidConfig()

### Scripts\Data\Configs\StructureConfig.cs
- **public partial class StructureConfig : EntityConfig**
  成员: GridWidth, GridHeight, CreepSpreadRadius, CreepSpreadTime, GeneratedCreepType, AutoHarvestRadiusTiles, AutoHarvestPerSecondPerNode, AcceptableResourceTypes, FieldRadius, PassiveHpRegenPerSecond, AutoProduceIntervalSeconds, AutoProduceMaxBound, LifespanSeconds, AmmoRangeTiles, GarrisonCapacity, GarrisonIncomePerSecondPerUnit, ProductionQueueSize, SupplyProvided, SupplyUsed, IncomeAmountPerCycle, IncomeCycleTicks, ResourceAmount, AuraRange, AuraAttackSpeedBonus, AuraHpRegenPerSecond, AuraEnemyRangeReductionTiles, AuraBurnDamagePerSecond, RadarRadiusTiles, RadarDurationSeconds, RadarEnergyCost, StrikeRadiusTiles, StrikeDamage, StrikeEnergyCost, ExchangeMetalAmount, ExchangeGasAmount, ExchangeEnergyCost, PanelSkillMode, WaveRadiusTiles, WaveDurationSeconds, WaveMultiplier, SeismicMaxCharges, SeismicChargeSeconds, WormholeCreateCooldownSeconds, HasWeapon(), HasWeaponId(), CanTrainUnit(), CanResearchTech(), ProvidesTech(), RequiresTech(), IsValidConfig()

### Scripts\Data\Configs\TechConfig.cs
- **public enum TechCategory**
- **public partial class TechConfig : Resource**
  成员: RepeatCostMultiplier, VisionBonus, DefenseType, ResearchTimeSeconds, Cost, GrantWeaponCount, AuraRadiusTiles, AuraAttackSpeedBonus, AuraHpRegenPerSecond, AuraEnemyRangeReductionTiles, AuraBurnDamagePerSecond, SkillWindupSeconds, SkillDurationSeconds, SkillCooldownSeconds, SkillFireIntervalSeconds, SkillProjectileSpeedTiles, SkillProjectileRangeTiles, SkillProjectileRadiusTiles, SkillDamage, CreepIncomeMultiplier, SpreadCostMultiplier, SpreadCooldownMultiplier, HarvesterIncomeMultiplier, AutoProduceIntervalMultiplier, FireDamageBonus, AttackSpeedMultiplier, FlatHpBonus, FlatDamageBonus, StructureHpBonusPercent, CreepSpreadRadiusBonus, CreepSpreadTimeMultiplier, LifestealPercent, HitSlowMoveMultiplier, FieldRadiusBonus, FieldMoveSpeedBonus, HpBonusPercent, KineticResist, ThermalResist, ExplosiveResist, EmResist, BeamResist, SelfCreepRadius, SpeedOnCreepMultiplier, KnockbackTiles, DeployDamageReduction, DeployTimeMultiplier, AmmoCapMultiplier, AoeRadiusBonusTiles, BonusDamageVsStructure, IsValidConfig()

### Scripts\Data\Configs\UnitConfig.cs
- **public partial class UnitConfig : EntityConfig**
- **public enum UnitRoleType**
  成员: MoveSpeed, FootprintTiles, LifespanSeconds, ControlRangeTiles, AutoHarvestRadiusTiles, AutoHarvestPerSecondPerNode, BuildRangeTiles, HarvestRangeTiles, CreepDefenseBonus, LeapCooldownSeconds, LeapRangeTiles, FieldRadius, FieldAuraEnemyAttackSpeedMultiplier, FieldAuraEnemyIncomingDamageMultiplier, FieldAuraTickSeconds, MultiShotTargets, HeroEnergyMax, HeroEnergyRegenPerSecond, Skill1Cost, Skill1RangeTiles, Skill1DurationSeconds, Skill1MoveSpeedBonus, Skill1EnemyMovePenalty, Skill2Cost, Skill2RangeTiles, Skill2DurationSeconds, Skill2Multiplier, Skill2Damage, Skill1TargetMode, Skill2TargetMode, Skill1Kind, Skill2Kind, PlasmaWindupSeconds, PlasmaRecoverySeconds, PlasmaCooldownSeconds, PlasmaAutoTargetRangeTiles, FlyHeight, Acceleration, TurnSpeed, SupplyCost, SupplyProvided, SupplyUsed, WeaponRotationInterval, HarvestAmountPerCycle, HarvestCycleTicks, CarryCapacity, BuildPowerPercent, RepairPerSecond, RepairCostPerHp, RepairRange, HealRangeTiles, HealPerSecond, MobilitySpeedBonus, MobilityDurationSeconds, MobilityCooldownSeconds, MobilitySpeedMultiplier, MobilityRangeTiles, MaxAmmo, DeployTimeSeconds, DeployedMaxHpMultiplier, DeployedAttackRangeBonusTiles, DeployedAoeRadiusTiles, DeployTargetCircleTiles, Skill1AttackRangeBonus, OffenseRating, DefenseRating, SupportRating, EconomyRating, HasWeapon(), HasWeaponId(), CanHarvestResource(), CanBuildStructure(), RequiresTech(), IsValidConfig()

### Scripts\Data\Configs\WeaponConfig.cs
- **public enum ProjectileBehavior**
- **public partial class WeaponConfig : Resource**
  注释: 弹道行为：决定开火后“伤害如何到达目标”
  成员: Damage, BonusDamage, AttackRange, CooldownTicks, WindupTicks, ConeAngleDegrees, ProjectileSpeed, HitRadius, LobDurationSeconds, LobHeight, ImpactDamagePerFootprintSq, ImpactRadiusTiles, ImpactSlowMoveMultiplier, ImpactSlowAttackMultiplier, ImpactSlowSeconds, AreaRadius, AreaEdgeDamagePercent, HitFxRadius, HitFxDuration, HitFxColor, TracerThickness, TracerDuration, MuzzleFlashRadius, MuzzleFlashDuration, IsProjectile, ToProjectileSpec(), IsValidConfig(), RequiresTag(), ForbidsTag()

### Scripts\Data\EntityAction.cs
- **public class EntityAction**
  成员: ActionId, SlotIndex, DisplayName, Icon, EntityAction(), ActionName

### Scripts\Data\EntityExtensions.cs
- **public static class EntityExtensions**
  成员: GetTree(), FindClosestEnemy(), FindAutoAttackTarget(), TryAutoAttack(), CommandMoveTo(), CommandStop(), CommandAttack(), CommandAttackMove(), CommandHarvest()

### Scripts\Data\Enums\GameEnums.cs
- **public enum DamageType**
- **public enum ArmorType**
- **public enum WeaponRangeType**
- **public enum CreepType**
  注释: 伤害类型 护甲类型 武器射程类型 (注意：这里名字是 WeaponRangeType)

### Scripts\Data\Enums\ResourceType.cs
- **public enum ResourceType**
  注释: 定义游戏里所有可能存在的资源类型

### Scripts\Data\FPVector2Extensions.cs
- **public static class FPVector2GodotExtensions**
  成员: ToGodotVector2()

### Scripts\Data\IEntity.cs
  作用: 表现层实体统一接口（文档 §3 已详述）：逻辑数据、模块、动作面板、视野/图标。
- **public interface IEntity**

### Scripts\Data\ResourceStartConfig.cs
  作用: 种族初始资源条目：资源类型 + 数量。
- **public partial class ResourceStartConfig : Resource**

## E.3 平台层（菜单 / 网络 / 玩家 / 种族 / 设置）

### Scripts\Menu\MainMenuController.cs
  作用: 主菜单/房间列表/大厅控制器：切页、机器人行、出生点校验、开局、教程面板。
- **public partial class MainMenuController : Control**
- **public enum MenuPage { Main, RoomList, Lobby, Tutorial, None }**
  成员: Pid, Slot, Group, _Ready(), _ExitTree(), SwitchPage()
- `BuildTutorialPage()` / `ShowTutorialDetail()`：左列表 + 右详情的教程面板，分基础/进阶两组，
  右栏含实操目标预览与可浏览的图鉴（Tree 目录 + 正文），详见 §19.6。
- `StartTutorial(def)`：把教程写进 `SimManager.PendingTutorial` 后直接开一局单机（走 `StartOfflineGameDirect`，不经过大厅）。
- `OpenMapEditor()` / `OpenTutorialPageForTest()`（`OPEN_TUTORIAL` 无头回归入口）/ `DumpTutorialsForTest()`（`DUMP_TUTORIALS`）。

### Scripts\Menu\RoomEntry.cs
  作用: 房间列表中的一行：显示主机 IP，点击发起加入。
- **public partial class RoomEntry : HBoxContainer**
  成员: HostIp, OnJoinRequested, Setup()

### Scripts\Network\LockstepManager.cs
  作用: 锁步调度核心：缓冲、心跳、补包重发、tick 推进、回放录制/校验（附录 A.4 已详述）。
- **public partial class LockstepManager : Node**
  成员: Instance, Payload, TargetSteamId, OutPacket(), new(), CurrentTick, ReplayMode, TickDelay, LateTickTolerance, LocalPlayerID, _EnterTree(), _Ready(), IsTickReady(), GetMissingPlayers(), SendAction(), SendSyncHeartbeat(), SendCurrentTickConfirm(), ResendSyncForTickTo(), StartInitialBuffer(), SendStateHash(), FlushOutbox(), ReceiveExternalAction(), ConsumeActions(), ConfirmTick(), RebroadcastHistoricalActionsTo(), AdvanceTick(), ResetLockstep(), SaveReplayToFile(), LoadReplayFromFile(), VerifyReplayHashes()

### Scripts\Network\NetAction.cs
- **public struct NetAction**
  注释: 网络指令协议结构。 当前仍兼容你的旧结构： - Move / Attack / Build_xxx 等战斗锁步指令
  成员: Version, Sequence, PlayerID, TargetTick, ActionId, ActionIdExtra, EntityIDs, TargetX, TargetY, TargetEntityID, IsQueue, Serialize(), TryDeserialize(), Deserialize(), IsValid(), IsSystemAction(), IsLockstepAction(), BuildDebugString()

### Scripts\Network\NetworkManager.cs
  作用: 网络与会话管理：Steam/离线大厅、队伍/种族/旁观者映射、系统指令（附录 A.5 已详述）。
- **public partial class NetworkManager : Node**
  成员: Instance, OfflineMode, IsHost, PeerTeamMap, PeerRaceMap, PeerGroupMap, PeerObserverMap, LocalIsObserver, PeerReadyMap, new(), LocalNetworkPlayerID, LocalTeamID, HostPlayerID, HostSteamID, _EnterTree(), _Ready(), RefreshPlayerList(), FinalizeLocalIdentity(), LockSession(), UnlockSessionForLobby(), SendLobbyUpdate(), SendMapSelection(), HandleSystemAction(), SendResendRequest(), SendTickConfirm(), SendTickConfirmTo(), IsOfflineLaunchRequested(), BeginOfflineHost(), StartOfflineGameDirect(), HostStartGame(), GetPlayerReadyStatus(), GetPlayerTeam(), IsPlayerObserver(), GetPlayerRace(), IsSessionLocked()

### Scripts\Player\Player.cs
  作用: 玩家节点：绑定种族配置、PlayerData 与本地控制器。
- **public partial class Player : Node2D**
  成员: Race, PlayerData, Controller, _Ready()

### Scripts\Player\PlayerController.cs
  作用: 本机玩家输入：选择、框选、指令下发。
- **public partial class PlayerController : Node**
  成员: SelectedEntities, _Ready(), _Process(), SelectEntities(), DeselectAll(), IssueUnitOrder(), GetMySelectedUnits()

### Scripts\Player\PlayerData.cs
  作用: 玩家确定性数据：资源、科技、人口、研究（附录 A.6 已详述）。
- **public partial class PlayerData : Node**
  成员: ResearchedTechs, TechLevels, ResearchingTechId, ResearchProgress, ResearchDuration, HasTech(), GetTechLevel(), IsResearching, GrantTech(), StartResearch(), AdvanceResearch(), Initialize(), AddResource(), GetResource(), HasResources(), TryConsumeResources(), AddResources(), ResetResources(), GetUsedSupply(), GetMaxSupply(), GetStateHash(), GetResourcesDebugString()

### Scripts\Race\ConfiguredRace.cs
  作用: 通用配置化种族：全部数据从配置表读取。
- **public partial class ConfiguredRace : RaceData**
  成员: RaceName, ConfiguredRace(), GetVisibleResources(), GetStartingWallet(), GetStartingUnits(), GetStartingStructures(), GetStartingResources()

### Scripts\Race\RaceData.cs
  作用: 种族抽象基类：初始资源/单位/建筑清单。
- **public abstract partial class RaceData : Node**
  成员: RaceName, GetVisibleResources(), GetStartingWallet(), GetStartingUnits(), GetStartingStructures(), GetStartingResources(), Initialize()

### Scripts\Race\Union.cs
  作用: 联合（Union）种族定义：初始 4 SCV + 主基地 + 金/气。
- **public partial class Union : RaceData**
  成员: RaceName, GetVisibleResources(), GetStartingWallet(), GetStartingUnits(), GetStartingStructures(), GetStartingResources()

### Scripts\Settings\GameSettings.cs
- **public static class GameSettings**
  注释: P2-5/P2-6：游戏设置（语言/键位/声音/画面），持久化到 user://settings.cfg
  成员: new(), Load(), Save(), ApplyAll(), ApplyAudio(), ApplyVideo(), ApplyKeybinds()

### Scripts\Settings\Localization.cs
- **public static class Localization**
- **public readonly record struct LanguageInfo(string Code, string NativeName);**
  注释: P2-6 本地化框架：CSV 配置（res://Localization/<code>.csv），回退链 当前语言 -> en_US -> key。 可用语言列表为现代游戏常见语言；未提供翻译的语言自动回退英文。 支持：
  成员: LanguageChanged, SetLanguage(), IsKnown(), Tr(), Has(), TrName(), ApplySceneTranslations(), DetectSystemLanguage(), EnsureLoaded(), ReloadAll()

## E.4 Simulation（纯逻辑层）

### Scripts\Simulation\Behaviors\HeroSkillBehavior.cs
- **public struct HeroSkillArgs**
- **public abstract class HeroSkillBehavior**
- **public sealed class CharmSkillBehavior : HeroSkillBehavior**
- **public sealed class RoarSkillBehavior : HeroSkillBehavior**
- **public sealed class TerranSmokeBehavior : HeroSkillBehavior**
- **public sealed class TerranDisruptBehavior : HeroSkillBehavior**
- **public sealed class BoostSkillBehavior : HeroSkillBehavior**
- **public sealed class DashSkillBehavior : HeroSkillBehavior**
- **public static class HeroSkillBehaviors**
  注释: 英雄技能效果参数（由 SimManager 从 UnitConfig 读取后传入，行为类不依赖 Godot 配置资源） 英雄技能行为基类：确定性效果实现，可被不同单位复用/子类化 勾魂（地狱领主）：魅惑敌方单位，使其不受控制地走向施法者
  成员: Cost, DurationSeconds, RangeTiles, MoveSpeedBonus, EnemyMovePenalty, Multiplier, Damage, AttackRangeBonus, Execute()

### Scripts\Simulation\Combat\BuffContainer.cs
- **public class BuffInstance**
- **public class BuffContainer**
  注释: 运行时 Buff 实例（纯逻辑、确定性数据） Buff 容器：挂在 SimEntity 上，负责叠加 / 到期 / 持续效果 / 修改器查询
  成员: BuffId, Stacks, RemainingTime, TotalDuration, ArmorBonus, HpRegenPerSecond, ShieldRegenPerSecond, GetStateHash(), Buffs, BuffContainer(), AddBuff(), RemoveBuff(), AddResistBuff(), AddStatBuff(), AddFlatDamageBuff(), GetFlatDamageBonus(), RemoveAll(), HasBuff(), GetBuff(), GetStacks(), Tick(), GetDamageMultiplier(), GetIncomingDamageMultiplier(), GetMoveSpeedMultiplier(), GetArmorBonus(), GetDamageResistMultiplier(), GetAttackSpeedMultiplier(), GetAttackRangeBonus()

### Scripts\Simulation\Combat\DamageResolver.cs
- **public struct DamageContext**
- **public static class DamageResolver**
  注释: 伤害判定上下文：把“打谁、用什么打、附加参数”封装成可扩展结构 攻防判定管线： 基础伤害 -> 伤害类型 x 护甲类型修正 -> 护甲值(扣穿透) -> Buff 倍率 -> 下限 1
  成员: DamageType, BaseDamage, BonusDamageType, BonusDamage, Pierce, SourceId, TargetId, CanCrit, CritChance, CritMultiplier, RegisterTypeModifier(), GetTypeModifier(), Resolve()

### Scripts\Simulation\Math\FPVector2.cs
  作用: 定点数二维向量（Fix64），逻辑层唯一坐标类型。
- **public struct FPVector2**
  成员: X, Y, FPVector2(), Zero, MagnitudeSquared(), Magnitude(), Normalized(), DistanceSquared(), IsWithinRange(), IsWithinRangeSq()

### Scripts\Simulation\ProjectileSpec.cs
- **public struct ProjectileSpec**
  注释: 纯逻辑弹体规格：由 WeaponConfig 映射而来。 模拟层不依赖 Godot 配置资源，确定性测试项目可直接编译。
  成员: Motion, Speed, HitRadius, MaxTravel, LobDuration, LobHeight, FootprintScaledDamage, ImpactRadius, ImpactDamagePerFootprintSq, ImpactSlowMoveMultiplier, ImpactSlowAttackMultiplier, ImpactSlowSeconds

### Scripts\Simulation\SimCreepGrid.cs
- **public class SimCreepGrid**
- **public struct CreepCellData**
  成员: Type, OwnerTeam, NanoCreepCount, PlantCreepCount, OnCreepChanged, AddCreep(), RemoveCreep(), DestroyNanoCreep(), DestroyNanoCreepRadius(), GetActiveCreep(), GetOwner(), IsValidIndex(), GetAllCells(), GetAllCellData(), GetStateHash()

### Scripts\Simulation\SimEntity.cs
- **public abstract class SimEntity**
  成员: ID, TeamID, Position, Radius, IsDead, World, CannotBeHealed, MaxHp, Hp, MaxShield, Shield, new(), ResourceAmount, ResourceType, ArmorType, Buffs, CargoAmount, CargoCapacity, CargoType, GetDebugState(), SimEntity(), TakeDamage(), LogicTick(), GetStateHash(), MixCoord()

### Scripts\Simulation\SimPathfinder.cs
- **public struct SimVector2I : IEquatable<SimVector2I>**
- **public class SimGrid**
- **public static class SimPathfinder**
- **internal class Node**
  成员: Y, SimVector2I(), Equals(), GetHashCode(), new(), SyncStructureBlocking(), SyncStaticBlocking(), IsWalkableFast(), RefreshTerrainBounds(), WorldToGrid(), IsAreaPlaceable(), GridToWorldCentered(), IsWalkable(), IsWalkableForRadius(), HasLineOfSight(), GetNearestWalkableNode(), Pos, H, F, Parent, InOpen, Closed, HeapIndex, Stamp, Count, Clear(), Add(), Pop(), BubbleUp(), FindPath()

### Scripts\Simulation\SimPojectile.cs
- **public enum ProjectileMotion**
- **public class SimProjectile**
  注释: 弹体运动方式（由 WeaponConfig.ProjectileBehavior 映射而来）
  成员: Position, Speed, HitRadius, Target, TargetPoint, Damage, DamageType, BonusDamage, BonusDamageType, Source, LobStartX, LobStartY, IsDead, HasHit, ParabolicLob, SimProjectile(), LogicTick()

### Scripts\Simulation\SimProjectileFactory.cs
- **public static class SimProjectileFactory**
  注释: 弹体统一工厂：武器开火与技能弹体都从这里按 WeaponConfig 生成逻辑弹体。 新增弹道/命中行为只需改配置表，不再需要每个发射点手写 SimProjectile。
  成员: Create()

### Scripts\Simulation\SimRandom.cs
- **public class SimRandom**
  成员: State, SimRandom(), Next(), NextFP()

### Scripts\Simulation\SimShrine.cs
- **public class SimShrine : SimStructure**
  成员: CaptureProgress, RewardTimer, SimShrine(), GetStateHash()

### Scripts\Simulation\SimSpatialGrid.cs
- **public sealed class SimSpatialGrid**
  注释: 单位空间索引：把每 Tick 的 O(n²) 邻近查询降为 O(候选数)。 只做查询加速，不改变任何受力/碰撞公式与遍历确定性（候选统一按 ID 排序）。
  成员: MaxUnitRadius, MaxVisionRange, Rebuild(), QueryNeighbors()

### Scripts\Simulation\SimStructure.cs
- **public class SimStructure : SimEntity**
- **public enum StructureState**
  成员: GridPosition, GridWidth, GridHeight, GridSize, new(), CurrentState, ConstructionProgress, SimStructure(), LogicTick(), GetStateHash()

### Scripts\Simulation\SimUnit.cs
- **public class SimUnit : SimEntity**
  成员: Velocity, TargetPosition, FinalTargetPosition, HasTarget, IsSandwormBody, DashTarget, new(), PlasmaTarget, GetWeaponCooldown(), SetWeaponCooldown(), DeployTargetCenter, PendingMoveTarget, IsDeployed, IsDeployBusy, SimUnit(), GetDebugState(), CommandMove(), CompletePendingPath(), LogicTick(), GetStateHash(), GetEffectiveMaxSpeed()

### Scripts\Simulation\SimWorld.cs
- **public class CarpetSpreadState**
- **public partial class SimWorld**
  成员: CenterX, CenterY, OwnerTeam, MaxRadius, TicksElapsed, DurationTicks, new(), SpatialGrid, SimGrid(), SimRandom(), CreepGrid, ApplyNanoConfig(), GetNextEntityId(), AddUnit(), AddStructure(), IsAreaOccupiedByStructure(), IsAreaNearResourceNode(), AddProjectile(), FindEntityById()

### Scripts\Simulation\SimWorld.Hashes.cs
- **public partial class SimWorld**
  成员: GetWorldHash(), GetCountersHash(), GetUnitsHash(), GetStructuresHash(), GetProjectilesHash(), GetCreepHash(), GetCarpetSpreadHash(), GetRngHash(), GetWorldDebugReport()

### Scripts\Simulation\SimWorld.Tick.cs
- **public partial class SimWorld**
  成员: ProfileEnabled, new(), ProfileTickCount, Tick(), TryReservePathBudget(), FindSimEntity(), ApplyAreaDamage(), StartCarpetSpread(), IsOnField(), IsInAnyField(), IsInTeamVision(), FindNearestUnit(), FindNearestStructure()

### Scripts\Simulation\SwarmManager.cs
- **public class SwarmManager**
  成员: CalculateVelocity()

## E.5 测试与工具

### Scripts\Test\StressTest2000Mixed.cs
- **public partial class StressTest2000Mixed : Node**
  注释: 联盟 vs 多足机械 混编 2000 人口对战场景（每边 1000 人口）。 配比（供参考，可直接改 UnionArmy / WandererArmy）： 联盟：枪兵400 + 医疗80 + 火箭80 + 双足60 + 直升机40 + 战巡14 = 1000 人口 / 674 单位
  成员: UnitId, Count, Supply, Spacing, UnitGroup(), _Ready(), _Process()

### Scripts\Test\StressTest200Rifle.cs
- **public partial class StressTest200Rifle : Node**
  注释: 压力测试场景：实例化主战斗场景（单机正常开局）， 再追加 200 个枪兵（100 vs 100 相向冲锋）和一批随机墙体。
  成员: _Ready(), _Process()

### Scripts\Test\StressTestMixed.cs
- **public partial class StressTestMixed : Node**
  注释: P1-2 混合种族压测：2000 单位（多族混编）确定性生成 + 回放可验证。 用 REPLAY_DETERMINISTIC=1 同步生成；RECORD_REPLAY/REPLAY_FILE 支持回放录制与校验。
  成员: _Ready(), _Process()

### Scripts\Tools\AIShowcase1v1.cs
- **public partial class AIShowcase1v1 : Node**
  注释: 1v1 AI 演示：双方机器人对战，视野按各自队伍正常计算， 观看者（OB）为正交俯视全图视角（FogOfWar.RevealAll 仅影响渲染，不影响机器人视野/行为）。 运行：godot --path . res://Scenes/Test/AIShowcase1v1.tscn
  成员: _Process(), _EnterTree(), ApplyMatchConfig(), _Ready()

### Scripts\Tools\ExportEntityScenes.cs
- **public partial class ExportEntityScenes : Node3D**
  注释: 预制体导出器：把 EntityFactory3D 生成的实体打包成 .tscn， 供编辑器编辑；游戏加载时优先使用场景文件。 运行方式: godot --headless --path . res://Scenes/Tools/ExportEntityScenes.tscn
  成员: _Ready()

### Scripts\Tools\FlattenPrefabModels.cs
- **public partial class FlattenPrefabModels : Node3D**
  注释: 临时工具：把所有预制体里的 Model 实例“拍平”成普通本地节点， 消除实例 + 可编辑子节点导致的节点重名警告。用完即删。
  成员: _Ready()

### Scripts\Tools\InspectModelTree.cs
- **public partial class InspectModelTree : Node3D**
  注释: 临时工具：打印常用 GLB 模型的节点树（含重复名检测）。用完即删。
  成员: _Ready()

### Scripts\Tools\ReplayAutoRecord.cs
- **public partial class ReplayAutoRecord : Node**
  注释: P0-2 回放自动录制：设置 RECORD_REPLAY（user:// 路径）与 REPLAY_TICKS 后挂到对局场景， 跑到目标 tick 后停止模拟并保存回放文件（含逐 tick 哈希）。
  成员: _Ready(), _Process()

### Scripts\Tools\ReplayAutoVerify.cs
- **public partial class ReplayAutoVerify : Node**
  注释: P0-2 回放自动验证：设置 REPLAY_FILE（user:// 路径）后挂到对局场景， 回放跑到录制 tick 数后停止模拟，逐 tick 对比哈希并打印结果。
  成员: _Ready(), _Process()

## E.6 UI（界面）

### Scripts\UI\ActionPanel.cs
  作用: 底部命令卡：动作按钮、快捷键、按选中实体刷新。
- **public partial class ActionPanel : Control**
  成员: Instance, _Ready(), _ExitTree(), _Process(), Refresh(), _UnhandledInput()

### Scripts\UI\BuildPreview.cs
- **public partial class BuildPreview : Node3D**
  注释: 3D 建造预览：半透明立方体贴在鼠标射线指向的地面格子上
  成员: ExtraBlocked, AlignedWorldPos, Setup(), SetTargetPosition(), CanPlace

### Scripts\UI\FpsOverlay.cs
- **public partial class FpsOverlay : CanvasLayer**
  注释: 右下角帧率小字：画面 FPS + 逻辑 TPS（锁步实际执行率）
  成员: _Ready(), _Process()

### Scripts\UI\Minimap.cs
  作用: 小地图：绘制实体点/迷雾/相机框，点击与右键移动。
- **public partial class Minimap : Control**
  成员: _Ready(), _Process(), _Draw(), DrawEntityLayer(), _GuiInput()

### Scripts\UI\MinimapEntityLayer.cs
- **public partial class MinimapEntityLayer : Control**
  注释: 小地图实体层：位于迷雾层之后绘制，保证单位点/建筑点/相机框不被迷雾盖住
  成员: Setup(), _Draw()

### Scripts\UI\NanoPanelUI.cs
- **public partial class NanoPanelUI : PanelContainer**
  注释: 纳米虫顶部面板：UI 结构完全在 user_ui.tscn 中声明（与其它面板平级）， 本脚本只负责绑定按钮、刷新技能 CD/充能、两段式扩散交互。
  成员: IsSpreadPending, _Ready(), _ExitTree(), _Process(), _UnhandledInput(), ToggleSpreadPending(), CancelSpreadPending(), ConfirmSpreadAt()

### Scripts\UI\NetworkNotice.cs
- **public partial class NetworkNotice : CanvasLayer**
  注释: 网络状态提示：等待补包 / 玩家掉线 / 脱步暂停。 只在主线程轮询 LockstepManager 状态，避免跨线程碰 Godot UI。
  成员: Instance, _Ready(), _Process(), ShowMessage()

### Scripts\UI\OrbitalPanelUI.cs
- **public partial class OrbitalPanelUI : PanelContainer**
  注释: 联盟顶部面板：轨道控制中心的技能（雷达/轨道炮/资源交换）+ 能量显示 结构上与纳米顶部面板平级，由 RacePanelManager 按种族显示
  成员: _Ready(), _ExitTree(), _Process(), _UnhandledInput()

### Scripts\UI\PlantPanelUI.cs
- **public partial class PlantPanelUI : PanelContainer**
  注释: 植物顶部面板：与纳米面板同构，按钮绑定植物建筑 + 森林蔓延技能
  成员: IsSpreadPending, _Ready(), _ExitTree(), _UnhandledInput(), ToggleSpreadPending(), ConfirmSpreadAt()

### Scripts\UI\ProductionQueueUI.cs
  作用: 生产队列 UI：显示正在训练/队列中的单位。
- **public partial class ProductionQueueUI : PanelContainer**
  成员: _Ready(), _ExitTree(), Setup(), _Process()

### Scripts\UI\RacePanelManager.cs
- **public partial class RacePanelManager : Node**
  注释: 种族面板管理器：遍历 user_ui.tscn 中 MainControl 下所有 RacePanel_<种族ID> 节点， 按本地玩家种族显示对应面板、隐藏其他。新种族只需在场景里加面板节点。
  成员: _Process()

### Scripts\UI\SelectionInfoPanel.cs
  作用: 选中信息面板：单位/建筑属性、资源、技能说明。
- **public partial class SelectionInfoPanel : Control**
  成员: Instance, _Ready(), _Process(), _ExitTree(), UpdateInfo()

### Scripts\UI\SettingsMenu.cs
- **public partial class SettingsMenu : Control**
  注释: P2-5 设置菜单：语言 / 键位 / 声音 / 画面（代码构建 UI，避免手写 .tscn）
  成员: _Ready(), _UnhandledInput()

### Scripts\UI\SkillRangeIndicator.cs
- **public partial class SkillRangeIndicator : Node3D**
  注释: 技能范围指示圈：施法射程圈（以施法者为圆心）+ 落点效果圈（跟随鼠标）。 由 UserController / NanoPanelUI 在技能待确认阶段驱动，纯本地视觉。
  成员: GetOrCreate(), _Ready(), ShowCastRange(), ShowEffectRange(), HideRanges()

### Scripts\UI\UnitHealthBar3D.cs
- **public partial class UnitHealthBar3D : Node3D**
  注释: 3D 血条：两个 QuadMesh（背景 + 填充），填充从左端收缩，面向 -Z 相机
  成员: Setup(), SetFogHidden(), _Process(), _ExitTree()

### Scripts\UI\VoteUI.cs
- **public partial class VoteUI : CanvasLayer**
  注释: P1-4 协商/投票 HUD：显示进行中的锁步投票，N/M 表决，结果提示 3 秒。 发起入口：Ctrl+H（重开投票，UserController 处理）。
  成员: _Ready(), _Process(), _UnhandledInput(), SendVote()

## E.7 Units（单位 / 动作 / 模块 / 武器）

### Scripts\Units\Actions\ActionLayer.cs
  作用: 指令层枚举（立即/排队等），决定指令进入方式。
- **public enum ActionLayer**

### Scripts\Units\Actions\ActionQueueManager.cs
  作用: 指令队列（纯逻辑）：先进先出、快照、按位移除。
- **public class ActionQueueManager**
  注释: 纯逻辑类，不继承 Node
  成员: Count, Enqueue(), Dequeue(), Peek(), Clear(), GetSnapshot(), TryRemoveAt()

### Scripts\Units\Actions\CreepClearHelper.cs
- **public static class CreepClearHelper**
  注释: 统一的“清敌方纳米菌毯”入口：与普通攻击共用武器冷却/射程/特效/溅射
  成员: TryFireAtCreep()

### Scripts\Units\Actions\Implementations\AttackAction.cs
  作用: 攻击指定目标。
- **public partial class AttackAction : UnitAction**
  成员: Target, _Ready(), SetTarget(), Setup(), OnEnter(), OnUpdate(), OnExit(), GetDeterministicExtraHash()

### Scripts\Units\Actions\Implementations\AttackMoveAction.cs
  作用: 攻击移动：沿途自动索敌。
- **public partial class AttackMoveAction : UnitAction**
  成员: TargetPosFP, _Ready(), Setup(), OnUpdate(), GetDeterministicExtraHash()

### Scripts\Units\Actions\Implementations\AutoProduceModeAction.cs
- **public partial class AutoProduceModeAction : UnitAction**
  成员: _Ready(), CanExecute(), OnEnter()

### Scripts\Units\Actions\Implementations\BuildAction.cs
- **public partial class BuildAction : UnitAction**
  成员: TargetStructure, Initialize(), _Ready(), CanExecute(), GetRequirementTooltip(), Setup(), OnEnter(), OnUpdate(), OnExit(), GetTargetPos(), GetDeterministicExtraHash()

### Scripts\Units\Actions\Implementations\BurrowAction.cs
- **public partial class BurrowAction : UnitAction**
  成员: _Ready(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\Implementations\CruiserMobilityAction.cs
- **public partial class CruiserMobilityAction : UnitAction**
  成员: CanBeInterrupted, _Ready(), Setup(), CanExecute(), GetCooldownRemaining(), GetCooldownMax(), GetDeterministicExtraHash(), OnEnter(), OnUpdate(), OnExit()

### Scripts\Units\Actions\Implementations\DeployAction.cs
- **public partial class DeployAction : UnitAction**
  成员: _Ready(), CanExecute(), Setup(), OnEnter()

### Scripts\Units\Actions\Implementations\DevourAction.cs
- **public partial class DevourAction : UnitAction**
  成员: _Ready(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\Implementations\GarrisonAction.cs
- **public partial class GarrisonAction : UnitAction**
  成员: Target, _Ready(), Setup(), OnEnter(), OnUpdate()

### Scripts\Units\Actions\Implementations\GeneralCancelAction.cs
- **public partial class GeneralCancelAction : UnitAction**
  成员: _Ready(), OnEnter()

### Scripts\Units\Actions\Implementations\HarvestAction.cs
- **public partial class HarvestAction : UnitAction**
  成员: IsHarvesting, HarvestedThisTick, Source, SourceEntity, _Ready(), Initialize(), Setup(), SetSource(), OnEnter(), OnExit(), OnUpdate(), GetDeterministicExtraHash()

### Scripts\Units\Actions\Implementations\HeroSkillAction.cs
- **public partial class HeroSkillAction : UnitAction**
  成员: _Ready(), CanExecute()

### Scripts\Units\Actions\Implementations\IdleAction.cs
  作用: 待机：原地不动，默认指令。
- **public partial class IdleAction : UnitAction**
  成员: _Ready(), OnUpdate()

### Scripts\Units\Actions\Implementations\MoveAction.cs
  作用: 移动到目标点。
- **public partial class MoveAction : UnitAction**
  成员: TargetPosFP, Setup(), OnEnter(), OnUpdate()

### Scripts\Units\Actions\Implementations\OmniLeafDogAction.cs
- **public partial class OmniLeafDogAction : UnitAction**
  成员: _Ready(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\Implementations\OrbitalStrikeAction.cs
- **public partial class OrbitalStrikeAction : UnitAction**
  成员: _Ready(), Initialize(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\Implementations\PlasmaModeAction.cs
- **public partial class PlasmaModeAction : UnitAction**
  成员: _Ready(), CanExecute()

### Scripts\Units\Actions\Implementations\PlasmaStrikeAction.cs
- **public partial class PlasmaStrikeAction : UnitAction**
  成员: _Ready(), CanExecute()

### Scripts\Units\Actions\Implementations\RadarAction.cs
- **public partial class RadarAction : UnitAction**
  成员: _Ready(), Initialize(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\Implementations\RebuildWreckageAction.cs
- **public partial class RebuildWreckageAction : UnitAction**
  成员: _Ready(), Setup(), OnUpdate()

### Scripts\Units\Actions\Implementations\ReleaseWorkersAction.cs
- **public partial class ReleaseWorkersAction : UnitAction**
  成员: _Ready(), CanExecute(), OnEnter()

### Scripts\Units\Actions\Implementations\RepairAction.cs
- **public partial class RepairAction : UnitAction**
  成员: _Ready(), Initialize(), CanExecute(), Setup(), OnEnter(), OnUpdate(), OnExit(), GetDeterministicExtraHash()

### Scripts\Units\Actions\Implementations\ResearchAction.cs
- **public partial class ResearchAction : UnitAction**
  成员: _Ready(), CanExecute(), GetScaledCost()

### Scripts\Units\Actions\Implementations\ResonanceWaveAction.cs
- **public partial class ResonanceWaveAction : UnitAction**
  成员: _Ready(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\Implementations\ResourceExchangeAction.cs
- **public partial class ResourceExchangeAction : UnitAction**
  成员: _Ready(), CanExecute(), TryPayCost(), OnEnter()

### Scripts\Units\Actions\Implementations\SeismicWaveAction.cs
- **public partial class SeismicWaveAction : UnitAction**
  成员: _Ready(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\Implementations\StimAction.cs
- **public partial class StimAction : UnitAction**
  成员: _Ready(), CanExecute(), GetCooldownRemaining(), GetCooldownMax(), OnEnter()

### Scripts\Units\Actions\Implementations\StopAction.cs
  作用: 停止当前行为。
- **public partial class StopAction : UnitAction**
  成员: _Ready(), OnEnter(), OnUpdate()

### Scripts\Units\Actions\Implementations\SwitchAmmoAction.cs
- **public partial class SwitchAmmoAction : UnitAction**
  成员: _Ready(), CanExecute(), OnEnter()

### Scripts\Units\Actions\Implementations\TrainUnitAction.cs
  作用: 训练单位（生产建筑）。
- **public partial class TrainUnitAction : UnitAction**
  成员: GetProgress(), GetTimerRaw1000(), _Ready(), TryPayCost(), CanExecute(), OnEnter(), OnUpdate()

### Scripts\Units\Actions\Implementations\WormholeCreateAction.cs
- **public partial class WormholeCreateAction : UnitAction**
  成员: _Ready(), CanExecute(), TryPayCost()

### Scripts\Units\Actions\UnitAction.cs
  作用: 所有指令的抽象基类：状态机（OnEnter/OnUpdate/OnExit）+ 确定性哈希（附录 A.7 已详述）。
- **public abstract partial class UnitAction : Node**
  成员: _unit, Unit, IsActive, OnFinished, CanBeInterrupted, Initialize(), CanExecute(), OnEnter(), OnUpdate(), OnExit(), Setup(), TryPayCost(), GetCooldownRemaining(), GetCooldownMax(), GetDeterministicExtraHash(), Finish()

### Scripts\Units\Actions\UnitActionController.cs
  作用: 单位指令控制器：缓存动作、队列管理、生产/建造/采集查询（附录 A.7 已详述）。
- **public struct OrderData**
- **public partial class UnitActionController : Node**
  注释: --- [核心升级 1] 定义指令数据包 ---
  成员: ActionName, TargetPos, TargetObj, EntityOwner, Initialize(), RefreshActionCache(), LogicTick(), GetActiveProductionAction(), GetProductionQueueCount(), GetActiveBuildTarget(), GetActiveHarvestSource(), GetProductionQueueSnapshot(), StartAction(), CancelProduction(), CancelLastProduction(), StopAction(), StopAll(), UnitAction, HasCachedAction(), GetPreviewPathPoints(), GetDeterministicStateHash(), GetDebugSnapshot()

### Scripts\Units\Effects\BeamFX.cs
- **public static class BeamFX**
  注释: 光束类武器的专用直线光束特效（纯表现层）：从枪口到目标拉一根粗亮光束。 点名特效（如“光束线”的曲线光束）不走这里。
  成员: Spawn()

### Scripts\Units\Effects\CurvedBeamFX.cs
- **public static class CurvedBeamFX**
  注释: 曲线光束特效（纯表现层）：每条线独立随机曲率/倾斜/起终点散布
  成员: Spawn()

### Scripts\Units\Modules\AuraRangeVisual.cs
- **public partial class AuraRangeVisual : Node3D**
  注释: 光环范围显示（战争迷雾式方形格子）： - 所有光环实例把各自的格子规格上报给全局绘制器 - 合并绘制，重叠格子只画一次，颜色不会叠加变深
  成员: CenterCell, RadiusTiles, Color, ClipToCircle, RebuildMerged(), _Ready(), _ExitTree(), _Process()

### Scripts\Units\Modules\Behaviors\AutoBuildBehavior.cs
- **public partial class AutoBuildBehavior : BuildBehavior**
  成员: Tick()

### Scripts\Units\Modules\Behaviors\AutoHarvestBehavior.cs
- **public partial class AutoHarvestBehavior : HarvestBehavior**
  注释: 自动范围采集：从周围资源点按“每秒每点速率”收集（纳米采集器 / 先进采集器巨兽）。 独立文件：场景预制体按文件路径挂脚本时必须能命中具体类（不能挂在抽象基类文件上）。
  成员: Tick()

### Scripts\Units\Modules\Behaviors\BuildBehavior.cs
- **public abstract partial class BuildBehavior : Node**
  注释: 建造行为基类：施工推进策略由 SimManager 每帧驱动。 新建造方式（工人施工 / 自动施工 / 献祭 / 远程蓝图）都继承它，实体通过工厂挂载。
  成员: Tick()

### Scripts\Units\Modules\Behaviors\HarvestBehavior.cs
- **public abstract partial class HarvestBehavior : Node**
  注释: 采集行为基类：确定性逻辑由 SimManager 每帧驱动。 新采集方式（工人搬运 / 远程光束 / 范围自动）都继承它，实体通过工厂/科技挂载。
  成员: Tick()

### Scripts\Units\Modules\Behaviors\WorkerBuildBehavior.cs
- **public partial class WorkerBuildBehavior : BuildBehavior**
  成员: Tick()

### Scripts\Units\Modules\BoundCountLabel3D.cs
- **public partial class BoundCountLabel3D : Node3D**
  注释: 地狱城：显示当前绑定的怨灵数量（alive / 上限）
  成员: _Ready(), _Process()

### Scripts\Units\Modules\CreepSource.cs
  作用: 菌毯源：周期性铺毯/清除/升级加成。
- **public partial class CreepSource : Node**
  成员: Initialize(), Tick(), ClearCreep(), ApplyUpgrade()

### Scripts\Units\Modules\DeployCircleVisual.cs
- **public partial class DeployCircleVisual : Node3D**
  注释: 架设射击圈：地面虚线圆环（挂在场景根节点，全员可见、不受迷雾影响）
  成员: UpdateCircle()

### Scripts\Units\Modules\GarrisonCountLabel3D.cs
- **public partial class GarrisonCountLabel3D : Node3D**
  注释: 泰伦藻类工厂：显示当前驻扎工人数量（alive / 上限），样式与怨灵计数一致
  成员: _Ready(), _Process()

### Scripts\Units\Modules\HarvesterMiningFX.cs
- **public partial class HarvesterMiningFX : Node3D**
  注释: 采集器采矿特效：向周围资源点拉出脉动的能量光束（纯表现层）
  成员: _Ready(), _Process()

### Scripts\Units\Modules\ResourceAmountLabel.cs
- **public partial class ResourceAmountLabel : Node3D**
  注释: 矿点剩余量悬浮文字：实时显示资源剩余，采空后随节点一起消失
  成员: _Ready(), _Process()

### Scripts\Units\Modules\ResourceModule.cs
  作用: 资源点储量模块：扣除、枯竭事件。
- **public partial class ResourceModule : Node**
  成员: CurrentAmount, Initialize(), Tick(), NotifyAmountChanged(), NotifyDepleted()

### Scripts\Units\Modules\UnitCombat.cs
  作用: 战斗模块：武器管理、索敌、开火、齐射、切换武器。
- **public partial class UnitCombat : Node**
  成员: ActiveWeapon, LastGroundWeapon, CurrentTarget, GetAttackRange(), Initialize(), AddWeapon(), RemoveWeapon(), ClearWeapons(), Tick(), ConsumeAllWeaponCooldowns(), SetTarget(), CanAnyWeaponTarget(), CanAnyWeaponInRange(), TryVolley(), FireAtGround(), FireGroundVolley(), TryAttack(), CycleActiveWeapon()

### Scripts\Units\Modules\UnitHarvest.cs
  作用: 采集模块：携带量、当前目标、满载判定。
- **public partial class UnitHarvest : Node**
  成员: CurrentAmount, CurrentType, CurrentTargetSource, Initialize(), Tick(), IsFull(), GetFillRatio()

### Scripts\Units\Modules\UnitLife.cs
  作用: 生命模块：HP/护盾、伤害入口、死亡事件。
- **public partial class UnitLife : Node**
  成员: CurrentHp, CurrentShield, IsDead, Initialize(), ForceInjectStats(), Tick(), CheckDeath(), NotifyHealthChanged(), NotifyDied(), Heal(), SetHealthRaw(), TakeDamage()

### Scripts\Units\Modules\UnitVisuals.cs
  作用: 表现模块：模型/动画/血条/迷雾隐藏/选中圈/蓝图施工视觉。
- **public partial class UnitVisuals : Node**
  成员: Initialize(), TryGetVisualTarget(), UpdateBatch(), Tick(), TryGetModelBounds(), _Process(), UpdateTeamColor(), SetBlueprintVisual(), SetConstructionVisual(), SetFogHidden(), IsFogHidden, IsBatched, CreateGhostCopy(), SetSelected(), AimAt(), ResetAim(), OnUnitDied(), _ExitTree()

### Scripts\Units\PhysicsUtils.cs
- **public static class PhysicsUtils**
  成员: GetCollisionOutline()

### Scripts\Units\ResourceStructure.cs
- **public partial class ResourceStructure : Structure**
  成员: _Ready(), IsDeadOrNull(), InitializeModules()

### Scripts\Units\SelectionRing3D.cs
- **public partial class SelectionRing3D : Node3D**
  注释: 3D 选择圈：地面上的半透明圆盘，选中时显示
  成员: Setup(), SetSelected(), SetDeployed()

### Scripts\Units\SpecialUnits\ShrineStructure.cs
- **public partial class ShrineStructure : Structure**
  成员: CaptureProgressRatio, _Ready(), _Process(), CreateSimStructure(), LogicTick()

### Scripts\Units\Structure.cs
- **public partial class Structure : Node3D, IEntity**
- **public enum StructureState { Blueprint, Construction, Completed }**
  注释: 使用 Node3D，不依赖 Godot 物理
  成员: AutoBuildCache, AutoHarvestCache, CurrentState, IsUnderConstruction, ConstructionProgress, GridPosition, RallyQueue, Obstacle, DisplayName, Icon, SimStructureData, LogicEntity, _Ready(), _Process(), InitializeModules(), CreateSimStructure(), SnapAndRegister(), InitAsBlueprint(), PromoteFromBlueprint(), CanAddBuilder(), AddBuilder(), RemoveBuilder(), HasBuilder(), AdvanceProgress(), CancelConstruction(), GetAvailableActions(), AddRallyCommand(), IsDeadOrNull(), OnDestroyed()

### Scripts\Units\Unit.cs
- **public partial class Unit : Node3D, IEntity**
  注释: 使用 Node3D，不依赖 Godot 物理引擎
  成员: AutoHarvestCache, SimUnitData, LogicEntity, IsStructure, GridPosition, DisplayName, Icon, InjectLogicPosition(), _Ready(), _Process(), GetAvailableActions(), FindClosestEnemies(), PerformMultiShotVolley(), SetSelected(), IsDeadOrNull(), GetWaypointPositions(), StartDeathVisual()

### Scripts\Units\UnitCommandCard.cs
  作用: 命令卡资源：动作槽位数据（ActionSlotData）。
- **public partial class ActionSlotData : Resource**
- **public partial class UnitCommandCard : Resource**

### Scripts\Units\Weapons\Projectile\HomingProjectile.cs
  作用: 追踪弹表现节点：跟随目标、命中销毁。
- **public partial class HomingProjectile : Node3D**
  成员: Setup(), _Ready(), _Process()

### Scripts\Units\Weapons\Weapon.cs
  作用: 武器抽象基类：冷却、弹药、射程、伤害结算、特效入口。
- **public abstract partial class Weapon : Node3D**
  成员: GetFirePointWorld(), _owner, CurrentCooldown, Initialize(), ApplyConfig(), Tick(), CanFire(), CooldownRemaining, CooldownRemainingFP, TryConsumeAmmo(), CanFireWithDeployAndAmmo(), ForceCooldown(), CanTarget(), IsInRange(), CanHit(), Fire(), FireVolley(), ApplyConeDamage(), FireAtGround(), FireGroundVolley(), OnFireGroundVisuals(), ApplyDamage(), HasTagBonus(), ApplyAreaDamageAt(), OnFireVisuals()

### Scripts\Units\Weapons\Weapons\ProjectileWeapon.cs
  作用: 弹道武器：发射逻辑弹体（SimProjectile）。
- **public partial class ProjectileWeapon : Weapon**
  成员: Fire(), FireAtGround(), FireGroundVolley(), OnFireVisuals(), OnFireGroundVisuals()

### Scripts\Units\Weapons\Weapons\Rifle.cs
  作用: 步枪武器：仅覆盖开火特效（曳光/枪口焰）。
- **public partial class Rifle : Weapon**
  成员: OnFireVisuals(), OnFireGroundVisuals()

## E.8 其他

### Scripts\User\UserController.cs
- **public partial class UserController : Node**
  成员: _Ready(), _ExitTree(), _Process(), _UnhandledInput(), ForceSelect(), HandleMinimapRightClick(), EnterBuildMode(), EnterNanoBuildMode(), TriggerNanoSpread(), SpawnSpreadClickFeedback(), EnterRadarPending(), EnterOrbitalStrikePending(), GetMouseWorldPos()

### Scripts\User\UserUI.cs
- **public partial class UserUI : CanvasLayer**
  成员: ChatOpen, _Process(), _Ready(), _UnhandledInput(), ToggleChatInput()

### Scripts\World\EntitySpawner.cs
  作用: 实体生成单例：主线程创建逻辑+表现实体（附录 A.9 已详述）。
- **public partial class EntitySpawner : Node**
  成员: Instance, _Ready(), SpawnEntity()

### Scripts\World\Game.cs
  作用: 对局初始化：玩家/种族/中立建筑/资源簇生成。
- **public partial class Game : Node**
  成员: GetPlayerByTeam(), GetAllPlayers(), _Ready()

### Scripts\World\MapGrid.cs
  作用: 网格与地形：坐标换算、占位查询、蓝图合法性。
- **public partial class MapGrid : Node2D**
  成员: Instance, WorldBounds, _EnterTree(), _Ready(), WorldToGrid(), GridToWorldCentered(), GetTopLeftFromCenter(), GetAlignedWorldPos(), IsCellEmpty(), GetOccupantName(), IsAreaEmpty(), RegisterStructure(), UnregisterStructure(), IsValidTerrainForCreep(), IsPositionAvailableForBlueprint(), _ExitTree()

### Scripts\World\MapGround3D.cs
- **public partial class MapGround3D : Node3D**
  注释: 伪 3D 地面：草地平面 + 战争迷雾遮罩（从 FogOfWar 的 SubViewport 采样）
  成员: _Ready(), RebuildWalls(), _Process()
