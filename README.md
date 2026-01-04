# Death Buff Tracker / 死亡与状态追踪

这是一个用于 FFXIV 的 Dalamud 插件，记录死亡与指定状态获得，并持久化到 JSON。

## 功能
- 追踪死亡与指定状态获得（可记录来源/技能/伤害类型）
- 捕获副本/区域信息并写入事件
- 采集范围可配置：自己/小队/全部可见玩家（需勾选）/仅副本内
- 筛选：副本、玩家、时间范围；提供日期快捷与最近 X 天按钮
- JSON 事件存储（`death-buff-tracker.json`）与状态列表 JSON（`tracked-statuses.json`）

## 使用
- 主窗口命令：`/dbt`
- 在设置窗口配置采集范围与追踪状态 ID（格式：`id` 或 `id:名称`）

## 构建
- 命令：`dotnet build .\DeathBuffTracker.sln -c Release`
