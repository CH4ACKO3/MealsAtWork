# 工作者、工作台与选座兼容性审查

更新：0.5.4 已支持有普通 Food 需求的玩家机械体，详见 `2026-09-07-food-mechs.md`。下表对吃饭机械体的“不支持”描述是 0.5.2 审查时的状态；HardworkingKz 和 Hospitality 尚未接入。

更新：0.5.3 已用距离加权取代下文 0.5.2 的绝对餐桌优先。正式餐桌成本为实际直线距离，空工作台成本为实际直线距离 × 可配置倍率（默认 2，范围 1～5），成本相等优先正式餐桌。沿用原搜索半径和可达性，不计算实际绕行路径长度。加权边界、远处餐桌让位及倍率 1 已通过自动检查；下文实机记录对应 0.5.2。

本轮是兼容性审查，并修复正式餐桌优先级；没有将尚未适配的动物或访客直接开放为收餐者。版本 0.5.2。未加入额外派工唤醒逻辑，默认订单有效期仍为两小时。

| 对象 | 当前支持程度 | 依据与限制 |
| --- | --- | --- |
| 原版殖民者、人类型种族殖民者 | 支持既有工作餐流程 | 识别游戏中的 Humanlike 和受控殖民者身份，不限定 Human defName；必须有食物需求、库存及合适的餐食 |
| 原版机械体 | 可作为有相应搬运能力的配送员；不作为收餐者 | WorkGiver 声明 canBeDoneByMechs，仍受工作类型和 Manipulation 等条件约束。原版机械体没有普通食物需求；本轮没有新增机械体实机测试 |
| 模组中吃饭的机械体 | 未完整支持 | 若仍归类 Mechanoid 或不是受控殖民者，当前 Eligible/AtWork 会排除；不能仅按外观判断 |
| kemomimihouse HardworkingKz / Hardworking Extension | 不兼容收餐与工作餐；特殊配送员仅条件性可用 | 本机 1.6 包为 Moo.Hardworking.Kz，程序集 HardworkingExtension 1.6.dll，类型位于 Kz 命名空间。工作动物被 Humanlike/殖民者限制排除，且存在独立找饭、工作、进餐接口 |
| 原版任务借住者、临时工作者 | 取决于是否为 IsColonistPlayerControlled | 不是只根据访客称谓判定；满足现有条件可进入工作餐 |
| Hospitality (Continued) 访客 | 当前不支持工作餐、收餐或借用空台 | 访客通常不是受控殖民者；必须接入该模组的食物权限、来访/离开状态与工作许可后才能安全支持 |
| 其他模组新增的普通制作工作台 | 条件性支持 | 不按工作台 defName 白名单识别；Building_WorkTable 子类、目标 A、原版 DoBill 制作阶段是关键 |
| 独立制作驱动／无人自动生产设备 | 未通用支持 | 没有调用原版工作 toil 的驱动不会自动获得进度减速、工作餐和完成驻留；无人设备不存在收餐 pawn |

## HardworkingKz 源码发现

本机已安装 [kemomimihouse HardworkingKz](https://steamcommunity.com/sharedfiles/filedetails/?id=2574995438)。About 中的框架描述和程序集名称对应 Hardworking Extension；不能将它与旧 [Hardworking KEMOMIMIHOUSE](https://steamcommunity.com/sharedfiles/filedetails/?id=2142587186) 视作同一接口版本。

- `Kz.CompHardworking` 管理工作许可、训练、卸货等状态。
- `Kz.JobGiver_HardworkingFood` 覆盖 GetPriority 和 TryGiveJob；当前给原版基类方法安装的补丁不自动覆盖这些重写。该节点还处理聚会进食、随机吃饭、暴食，不能统一压制所有分支。
- `Kz.JobGiver_HardworkingWork` 直接继承 ThinkNode，当前只查 JobGiver_Work 的下一工作查询找不到它。
- `Kz.JobDriver_IngestLikeHumans` 直接继承 JobDriver，而不是 JobDriver_Ingest；即使部分调用 Toils_Ingest，当前空台准入、餐盘绘制与午休返回仍不能直接复用。
- Tiny Mode 通过 AllowableWorkGiverNames 白名单建立工作列表；新增送餐 WorkGiver 不会自动进入该白名单。普通模式则读取原版工作列表，是否能配送还取决于该动物的工作许可。

专用适配需要覆盖上述入口，并验证训练许可、食物偏好、真实进食后果、卸货、存读档和完成驻留；只删除 Humanlike 检查并不足够。

## Hospitality 源码发现

核对本机 [Hospitality (Continued)](https://steamcommunity.com/sharedfiles/filedetails/?id=3509486825) 的 1.6/Hospitality.dll。

- GuestUtility.IsGuest 使用 PresentGuests 集合；IsArrivedGuest 另检查 CompGuest.arrived，CompGuest 还有 sentAway 等状态。
- 工作权限取决于 Hospitality 的技能、好感和设置；工艺/艺术可被设置禁用。不能为饥饿访客直接绕过这些条件分配制作任务。
- 食物选择有 GuestCanUseFoodSourceInternal/Exceptions 等规则，并涉及购买；当前配送直接扫描现成食物，不能仅凭可食用和殖民者搬运权限就给访客送饭。
- 比较稳妥的第一步是适配访客自有背包餐食，再单独定义配送的免费食物/付费来源规则。本轮没有新增这些行为。

## 工作逻辑改动模组

- Common Sense：之前已实测制作、送餐、午休恢复及高级进餐清洁。它仍调用 Toils_Recipe.DoRecipeWork，因此能复用工作阶段包装；空台选座有专用兼容入口。本轮正式餐桌优先规则也由该入口共同调用。
- Pick Up And Haul：已有货物标记排除和配送独立堆栈，之前实测存读档后不吃掉搬运货物。
- Work Tab：本轮前的用户现场可以通过实际 JobGiver_Work 创建配送任务；下一工作查询沿用实际工作节点，不自行重排工种。尚未穷举其逐小时、逐 WorkGiver 设置组合。
- Schedule Everything：六种工作台相关排班允许准入；时间表变化不直接删除已有订单。配送范围仍仅制作，研究排班获准不等于研究自动下单。
- 完全替换制作 toil 的模组：必须使用公开 LunchUtility.EnableFor(Toil) 接入；当前要求 tickIntervalAction，不能宣称所有修改工作速度/流程的模组通用兼容。
- Lunch Break 的自动返回更严格，只接原版 JobDriver_DoBill 与本模组 ResumeWork。不能把其他自定义驱动替换成原版驱动后声称保留了其内部状态。

## 用餐优先级修复与验证

既有工作餐与下一制作工作优先逻辑保留，仍尊重“优先外出吃饭”、紧急饥饿及时间表等条件。开始普通找用餐位置时，原版找到的可用正式用餐表面优先，空台只作后备。此处沿用餐食的搜索半径和原版可达、预留与位置条件，不表示跨地图搜寻任何餐桌。

在真实 RimWorld 1.6 + Common Sense/PUAH/Schedule Everything 测试存档中，放置比空工作台更远的正式餐桌和餐椅，确认原版能选中该位置，空台入口拒绝抢占且不留下用餐记录；移除正式餐桌/餐椅后，确认空台正常接管。测试最初仅禁止餐椅时，角色仍可在桌边站着吃，这属于仍有正式用餐表面，因此最终使用移除餐桌的场景验证后备行为。

非人类及 Hospitality 的结论来自上述本机程序集审查，未在本轮伪装成人类进行“兼容通过”测试。正式餐桌优先级修复有实机结果；其他历史实测详见同目录 0.4.3、0.5.0 验收记录。
