# AGENTS

## 项目
- 名称：死亡与状态追踪（Dalamud 插件）
- 目标：按副本追踪死亡与指定状态获得，并持久化到 JSON

## 待办
- 验证 ActorControl/ActionEffect/EffectResult 钩子的稳定性与去重策略
- 根据需要补充事件列表展示（如技能名/伤害类型/来源 ID）
- 视需求增加状态列表 JSON 热加载反馈

## 已完成
- ActorControl 死亡事件钩子替代轮询
- ActionEffect 记录伤害来源/技能/类型
- EffectResult 记录状态获得来源/层数/持续时间
- 捕获副本/区域信息并写入事件
- 采集范围配置：自己/小队/全部可见玩家/仅副本内
- UI 筛选：副本/玩家/时间 + 日期快捷/最近 X 天
- JSON 事件存储带 schema 包装与损坏备份
- 追踪状态列表单独 JSON 存储与配置同步
