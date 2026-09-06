# Meals at Work 0.4.3 兼容性补丁验收

日期：2026-09-06。通过本机 GABS 启动 RimWorld 1.6，加载 Harmony、Core、Biotech、HugsLib、Common Sense、Pick Up And Haul、Schedule Everything、Meals at Work 和仅测试用的桥接/断言模组。

## 实现

- [Pick Up And Haul](https://steamcommunity.com/sharedfiles/filedetails/?id=1279012058)：读取 `CompHauledToInventory.takenToInventory` 标记，排除正在搬运的背包食物；不改变其他模组的货物标记。
- [Common Sense](https://steamcommunity.com/sharedfiles/filedetails/?id=1561769193)：同样排除 `CompUnloadChecker.ShouldUnload` 食物。在它的 `ReserveChewSpot` toil 初始化时接入空工作台选座，保留后续清洁、寻路、进食流程。没有合适工作台时仍执行原选座逻辑。不增加 toil 数量。
- 配送交接禁止合并到已有堆栈，避免送来的饭继承待搬运堆栈标记。
- [Schedule Everything!](https://steamcommunity.com/sharedfiles/filedetails/?id=3486341728)：允许 `Mazo_Cook/Smith/Tailor/Art/Craft/Research`。卧床休息、就医、搬运等专用排班及未知排班不允许。切换时复用既有的订单、等待、午休返回取消规则。
- 其他排班作者可以向自己的 TimeAssignmentDef 添加 `modExtensions/li Class="DeskLunch.WorkMealScheduleExtension"`，并设 `allowWorkMeals` 为 `true`。应以 Meals at Work 存在为条件应用该扩展。
- 这些均为可选适配，不新增强制依赖；加载顺序声明置于三个模组之后。未安装它们时自动跳过反射识别与可选 Harmony 补丁。

## 实机通过项

1. 读取已加载的六种真实 Schedule Everything Def，全部允许工作餐。在裁缝排班中真实制作产生订单，切到卧床休息后订单验证立即失败。
2. 背包放入五份带 PUAH 标记的简单饭：不计为随身餐；改用 Common Sense 待卸货标记得到相同结果；清除标记后可正常识别。
3. 生产代码交接一份同类餐食：形成独立堆栈，原货物仍五份。真实工作 toil 开始进餐，中途存档 `MealsCompat_Cargo` 并重新加载；吃完后营养恢复，五份货物原样保留，只消耗配送的一份。
4. 开启 Common Sense 的 `adv_cleaning_ingest`，启动原生 Ingest 任务：自动选中空工作台，恰好预留一台和一个用餐格。随后添加裤子配方，既有用餐仍有效。
5. 上述用餐中存档 `MealsCompat_Idle` 并重新加载；吃完得到工作台用餐心情，记录与工作台预留全部释放。
6. 本轮游戏错误日志查询为空。测试结果原始文本随本文保存为 `compatibility-results.txt`。

测试前曾遇到未订阅的下载项目没有进入游戏模组列表，导致测试找不到排班 Def；建立本地测试目录入口并确认 activeModCount=10 后重新验证通过。这是测试装载问题。

## 范围

自动检查另外验证无第三方模组时的补丁安装和排班边界。测试辅助 DLL 不进入发布包。

本补丁约束 Meals at Work 的背包食物识别，不替换原版或其他模组所有主动找饭行为。反射接口依据本机当前 1.6 版本源码/程序集，第三方更新后仍需回归。自定义制作驱动和多人同步未在此轮验证。

Common Sense 会改写制作任务的 toil 布局：在已经开始制作的旧存档中途新增/移除它，可能遇到原有任务序号不匹配。应在相关工作结束后保存再调整模组列表；本轮存读档测试保持同一模组组合。
