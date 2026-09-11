# 卡墙定位：多足指令与公共寻路均有问题

以下为修复前的定位记录。后续快速修复已完成：多足下达完整终点，成员跟随牧羊人的当前位置；公共直线判定及 A* 同时检查对角两侧阻挡。18 组完整终点绕墙场景已纳入默认回归；旧 3 格指令仅保留在诊断模式中作为失败对照。

## 已复现的两类故障

| 场景 | 完整终点指令 | 多足式直线 3 格指令 |
| --- | --- | --- |
| 一格厚长墙 | 三种尺寸均能绕过 | 能绕过，但小目标不断变化，终点附近可能来回移动 |
| 六格厚长墙 | 三种尺寸均能绕过，最终距目标 8～9 单位 | 三种尺寸均卡在墙前，最终距目标 841～864 单位 |
| 两块对角接触的墙，半径 16／32 | 卡在墙角，距目标 248／271 单位 | 同样卡住 |
| 同一对角墙，半径 64 | 能绕行，最终距目标 8 单位 | 能绕行 |

测试每组运行 1800 tick（90 秒模拟时间），每 30 tick 下令一次。Shepherd 与 RifleMan 在完全相同的速度、半径和地图条件下逐项结果一致；名称对照用于隔离种族身份，并非比较两者默认配置。共 36 组，Debug/Release 输出完全一致。多足实际牧羊人半径为 64，小型作战单位也会使用半径 32。

### 多足决策错误

`Scripts/Core/SimManager.BotAI.Wanderer.Orders.cs:121` 将实际目标改成“当前位置 + 朝目标的方向 × 192”。厚墙附近，这个临时目标持续落在墙内；寻路修正临时目标后仍停在近侧。公共 A* 根本没有收到真正的远端目的地，无法规划完整绕行。这个问题由上一轮多足 AI 重构中的短距离直线指令引入。

修复应保留完整目的地，或者先求完整路线，再沿该路线选短距离落点；控制圈等待和牧羊人停止距离需一起适配。

### 公共寻路与碰撞不一致

`Scripts/Simulation/SimPathfinder.cs:242` 的直线判定采样中心点所在格。小单位的膨胀级别为 0；对角穿过两块墙的共同角点时，采样格可以一直可走，但圆形实体实际过不去。公共 A* 邻居扩展也未检查对角移动两侧是否阻挡，因此仅关闭直线路径快捷返回不足以完整修复。

`Scripts/Simulation/SimUnit.cs` 对未走完且同目标的路径继续复用；当前重试主要处理“路径已经走完但没到目的地”。因此碰撞持续阻挡某个活动路径点时，可能长时间顶墙。此为源码确认的放大因素，本轮没有单独量化它的发生率。

修复需要统一直线判定、A* 对角通行与实体碰撞的净空规则，再补充活动路径无进展恢复；不能单靠放宽碰撞或忽略体积。

## 复现

```powershell
dotnet run --project Tools/SimulationDeterminismTest/SimulationDeterminismTest.csproj -c Debug --no-restore -- --wall-probe
dotnet run --project Tools/SimulationDeterminismTest/SimulationDeterminismTest.csproj -c Release --no-restore -- --wall-probe
```

源代码：`Tools/SimulationDeterminismTest/WallProbe.cs`。结果：`tmp/wall_probe_debug.log`、`tmp/wall_probe_release.log`。这是输出当前位置和距离的诊断模式，退出码 0 表示诊断执行完毕，不表示所有场景可通行。
