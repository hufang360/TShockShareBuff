# TShockShareBuff · 技术细节

面向想改源码 / 排查问题的读者。日常配置见 [README.md](../README.md)。

## 1. 技术前提（源码调研结论，1.4.5.7 / TShock pr-3301）

### 服务器能拿到什么

- 客户端 buff 变化时主动上报 `MessageID 50 (PlayerBuffs)`，携带的是**完整 buffType 列表**；`Main.cs:17884-17897` 逐帧比对本地与镜像，有变化才发，延迟约 1 帧。
- TShock 在 `GetDataHandlers.HandlePlayerBuffList` 中接管该包，写入 `TPlayer.buffType[]` 后 `return true`（GetDataHandlers.cs:3877-3907）。所以 `TShock.Players[i].TPlayer.buffType` 就是服务器视角的最新 buff 集合。
- `Player.maxBuffs = 44`（Player.cs:1491）。

### 剩余时间：协议里没有，但可以拿到（服务器自计时）

- 50 包确实不含时间——`NetMessage.cs:997-1010` 只写 buffType 列表；服务端收到后 `buffTime` 被硬编码成 60（`MessageBuffer.cs:2465`、`GetDataHandlers.cs:3938`）。OTAPI 也没有暴露 `Player.AddBuff` 钩子。
- 但服务器侧 `buffTime[]` 是块**自留地**：原版只在 `whoAmI == Main.myPlayer` 时递减（`Player.cs:9958-9960`），服务端 `Main.myPlayer == 255`，**永远不会自动递减**，50 包也不会覆盖我们写的值。
- 所以做法是**服务器接管时长**：检测到新增 buff → 补发一次 `55 + T` 把时长揽到自己手里 → 服务器每 tick 递减 remain。剩余时间精确到 tick，且全服天然一致。
- 初始时长 `T` 的来源（按优先级）：
  1. **红药水窗口**（仅喝下红药水后 3s 内的新增表内 buff，见第 4 节）
  2. 配置表 `buffs[{id,seconds}]`
  3. `Item.buffTime` 自动建表：`Initialize` 时遍历 `ItemID.Count`，`item.SetDefaults(i)` 后取 `buffType→max(buffTime)`（`Item.cs:279/281`），覆盖绝大多数药水/食物类 buff
  4. 分类兜底（增益 `gainDefaultSeconds` / 减益 `debuffDefaultSeconds`）

### 下发方式

- `NetMessage.SendData(55, -1, -1, null, targetIndex, buffId, ticks)`。客户端 `case 55` 仅当 `number == myPlayer` 才 `AddBuff(type, time, fromNetPvP: true)`（绕过专家/难度时间修正），天然只作用于目标玩家。
- 已持有该 buff 时走 `AddBuff_TryUpdatingExistingBuffTime`，取 `max(旧值, T)`（`Player.cs:5394-5418`）——补发只会延长、不会缩短，可利用这一点"接管"。
- 等价写法 `TSPlayer.SetBuff(id, ticks, bypass: true)`；**必须 bypass**，否则会被 `LastThreat` 5 秒节流丢弃（TSPlayer.cs:2412-2418）。
- 时间单位是 tick（1/60 秒），上限 `int.MaxValue/60 - 1`。
- 移除 buff 没有专用包，且 `55 + time=1` 对已持有 buff 无效——客户端 `AddBuff` 只延长不缩短（见 5. clear 的实现）。

### 倒计时边界（原版规则，服务器需对齐）

- `BuffID.Sets.TimeLeftDoesNotDecrease`：宠物类 8 个 id（28/334/29/159/150/93/348/366，BuffID.cs:22）不递减，服务器也别减。
- `BuffID.Sets.AddBuffTimeAdditivelyToCap`：94 号累加至 600 tick 上限（BuffID.cs:34），非取 max。

### 回环风险

被广播的玩家会带着新 buff 上报 50 包 → 必须防止它再触发一次广播。对策：全局冷却表 `cooldownSeconds` + 回声识别 `_supplied`（服务器下发过、尚未收到客户端回声的 buff 不再当新增重播）。

## 2. 功能需求

| # | 需求 | 说明 |
|---|------|------|
| FR1 | 检测新增 buff | `GameUpdate` 每 tick 遍历在线玩家 `TPlayer.buffType`，与快照 diff 取新增项（开销极小，无需轮询间隔） |
| FR2 | 接管 + 广播 | 对新增且允许共享的 buff：先给**触发者本人**补发 `55 + T`（把时长揽到服务器手里），再给其他在线玩家下发 `55 + T` |
| FR3 | 防回环 | 全局 buff 冷却表：同一 buffId 在 `cooldownSeconds` 内只广播一次 |
| FR4 | 过滤 | **白名单制**：只有配置 `gain` 里的 id 才共享，`shareDebuff=true` 时 `debuff` 里的也共享。两道额外闸门：`blacklist`，以及宠物 / 坐骑 / 矿车的硬排除（代码层，服主清不掉） |
| FR5 | 时长与计时 | 优先级：红药水窗口 > `buffs` 表指定 > 药水时长（`Item.buffTime`）> 分类兜底（增益 5 分钟 / 减益 30 秒）；服务器维护 `remainTicks` 倒计时表，每 tick 递减，跳过 `TimeLeftDoesNotDecrease` |
| FR6 | 快照重置 | `ServerJoin` 时预填该玩家快照（避免进服自带 buff 触发全服）；`ServerLeave` / 死亡重生时清空该玩家受管项 |
| FR7 | 热重载 | `GeneralHooks.ReloadEvent` + `/sharebuff reload` |
| FR8 | 开关 | `/sharebuff on|off|reload`，权限 `sharebuff.admin` |
| FR9 | 查询 | `/sharebuff list`：列出当前全服在管的 buff 及剩余时间，便于验证计时是否准确 |

### 两种共享模式的实现

- **`trigger` 触发式**：某人获得 buff 的那一刻，全服其他人各自获得一段配置时长，之后各自独立倒计时、互不影响。靠 `cooldownSeconds` 防回环。
- **`mirror` 镜像式**：全服 buff 列表始终对齐到并集——只要还有一人持有某 buff，全服缺失的人会被持续补发；全服无人持有后清掉服务器残余计时，客户端侧 buff 到期自然消失（不发撤销包）。此模式下 `cooldownSeconds` 退化为补发节流间隔。

## 3. 剩余时长反推（补发场景）

进服补发 / 镜像补发用的是**剩余时长**而不是全时长，来源优先级：

1. **持有者的服务器计时**（`_timers`）——正常广播路径下每 tick 递减，天然就是剩余值
2. **广播起点折算**（`_origin`：最近一次广播的起始 tick + 总时长）——覆盖持有者没有计时的场景（如 `takeOverSource=false` 时的触发者本人）：剩余 = 总时长 −（当前 tick − 广播 tick）
3. 以上都取不到才退化为 `DurationOf` 全时长

同时所有服务器下发（`Apply` 广播 / `giveOnJoin` / `mirror` 补齐）都会登记 `_supplied`——且只登记**下发时未持有**的目标（已持有者 buffType 不变、不会上报，登记了反而误吞他之后真实的新增）。客户端回的 50 包被识别为回声、不再当"新增"重播——否则那一轮重播会把全服（含触发者本人）的 buff 刷回全时长。

## 4. 重复喝药

客户端刷新时长不会改变 buffType 列表，50 包不发、服务器无感。改由 `GetDataHandlers.PlayerSlot` 监控背包——槽位物品恰好少 1 个、且该物品 `buffType > 0`、且玩家确实持有该 buff，即判定为喝药：同步计时并重新广播一轮（trigger / mirror 均生效，其他玩家该 buff 的时长随之重算）。持有中的误判只多刷一轮时长，"丢弃物品"的误判已被持有校验排除。

### 红药水（678）：特判物品

红药水是唯一不走 `Item.buffTime` 的药水：它的 `buffType` 为 0，客户端在 `Player.ItemCheck`（`if (item.type == 678)` 分支）里特判一次性加一组 buff。因此 `BuffClock` 的物品建表对它完全失效——既检测不到喝药（`GetItemBuff(678)` 返回 `(0,0)`），也查不到时长。机制全靠反编译客户端（1.4.5.7）确认后内置：

| 世界 | 给什么 | 单个时长 |
|------|--------|----------|
| 普通世界 | 11 个固定减益：中毒 20 / 黑暗 22 / 诅咒 23 / 着火 24 / 流血 30 / 困惑 31 / 缓慢 32 / 虚弱 33 / 沉默 35 / 破损盔甲 36 / 窒息 68 | 216000 ticks（3600s） |
| FTW（`Main.getGoodWorld`，含 zenith 种子） | 18 选 3 随机增益：1/2/3/5/6/7/8/9/16/17/104/105/111/113/114/115/116/117 | 108000 ticks（1800s） |

- FTW 的 18 个候选**全都能由其他药水获得**（铁皮 / 迅捷 / 再生等），但药水时长多为 2~10 分钟，红药水给 30 分钟——这正是"共享时长必须以红药水为准"的由来：若走药水表，其他玩家拿到的就是药水时长。
- 普通世界 11 个减益中除窒息（68）外都在客户端 `BuffID.Sets.BuffTimeIsExtendedWithGameDifficulty` 集合里，喝下瞬间经 `AddBuff_DetermineBuffTimeToAdd` 按难度放大：专家 ×2（7200s）、大师 ×2.5（9000s）。

**难度放大的位置是关键**：服务端 `TSPlayer.SetBuff` 只发 55 包原文值（反编译 TShockAPI 确认，不碰 `TPlayer` 数组）；客户端 `case 55 → AddBuff(type, time, fromNetPvP: true)`，而难度放大**仅 `fromNetPvP=false` 时执行**。也就是说服务器下发的值就是客户端最终生效的值——要给其他玩家与喝药者一致的时长，服务端必须直接发放大后的终值（216000 × 难度倍率），不能再指望客户端放大。

**实现**（时序依据见下）：

1. `OnPlayerSlot` 按常规"少 1 个"校验识别到 678 消耗 → 登记 `_redPotionAt[plr] = _tick`，直接返回（红药水没有单一 `buffType`，跳过通用喝药流程与 `HasBuff` 校验）。
2. 之后 `ScanBuffs` diff 出该玩家的新 buff，`OnBuffGained` 在常规取值（服务器计时 / `DurationOf`）之后追加一道判定：处于红药水窗口（`RedPotionWindow = 180` tick，即 3s）且 `BuffClock.RedPotionTicks(buffId) > 0`，则时长覆盖为红药水值。优先级：**红药水窗口 > 服务器计时 > buffs 表 > 药水表 > 兜底**。
3. FTW 世界的 3 个随机增益服务端无法预知（客户端 `Main.rand` 掷骰），只能靠 diff 识别——这是选"登记标记 + 窗口判定"而非"喝药时直接广播"的原因；普通世界 11 个减益虽确定，但统一走同一条路径，避免两套逻辑。

**时序依据**：客户端 `Main.TrySyncingMyPlayer` 每帧把本地状态与镜像比对，背包变化发 5 包、buff 变化发 50 包，同一帧内 5 在前；TCP 保序 → 服务器必然先收到 5（喝药）再收到 50（新 buff diff），标记先于 diff 生效。窗口只兜网络抖动与处理乱序。

**边界**：

- 窗口内玩家喝其他药水 / 被敌人上同类 debuff，会被按红药水时长共享（3 秒内连喝两种药水的场景，影响是多给时长，可接受）。
- 红药水重复喝且 buff 未过期时无 diff、不重播（与其他药水"新增才广播"语义一致）。
- 默认黑名单已含窒息（68）与诅咒（23），普通世界 11 个减益实际共享 9 个；要全共享就从 `blacklist` 删掉这两个 id。

## 5. clear 的实现

`clear` / `clearplayer` 走同一条 `ClearOne`：

- 直清服务器端 `buffType`/`buffTime` 数组（不再用 `55 + time=1`：客户端 `AddBuff` 对已有 buff 只延长不缩短，发了等于没发）
- 广播 50 包（PlayerBuff）让客户端覆盖该玩家的 buff 列表——这会刷新头顶图标，但 50 包副作用是客户端把列表内所有 buff 的 `buffTime` 硬写成 60（1 秒）
- **SSC 下保留的增益会立刻过期**，所以清完紧跟 55 包把保留项时长灌回去（时长取服务器计时，反推不到时用分类时长兜底）
- 恒持续类（`NoDecrease`）不递减，跳过恢复
- 清账：计时器 `_timers` 与回声登记 `_supplied` 一并删除，防止残留让后续同 buff 的新增被误判；全服 `clear` 还会清掉 `_origin` 广播起点记录，防反推出已死时长（`clearplayer` 不清，`_origin` 是全服粒度）

debuff 的判定：`debuff` 白名单优先，未分类 id 按"非增益且非宠物/坐骑/矿车"兜底。

## 6. 实现结构

```
ShareBuff/
  ShareBuff.cs   插件主体：hook 注册、GameUpdate 调度、接管与广播
  Config.cs      配置模型 + JSON 读写（参考 TShockGoodLucky/Config.cs）
  BuffClock.cs   时长来源表（Item.buffTime 建表 + 配置覆盖 + 红药水内置表）+ tick 换算
  BuffCats.cs    buff 分类表（增益/减益/宠物/坐骑/矿车，共 400 条无交集），由 wiki 文档生成
```

## 7. 风险与对策

- **时长取值的误差场景**：`Item.buffTime` 建表拿到的是"名义时长"。重叠喝药、专家/大师难度对 debuff 的时长加成、饱食类三档 buff 会与服务器值不符；`takeOverSource=true` 时以服务器值为准，这本身就是"全服共享"想要的一致性。红药水的难度加成已按客户端逻辑内置（见第 4 节），是唯一精确对齐的来源。
- **重复喝药**：见第 4 节。
- **兜底**：即便漏检，计时归零时若客户端仍持有该 buff（`buffType` 里还在），会自动重开一轮，不会提前误清（非共享 buff 不记账，避免无限空转）。
- **包量放大**：O(在线人数 × 新增 buff 数)，靠 `cooldownSeconds` 节流。
- **buff 槽位**：目标玩家 buff 满 44 时，`AddBuff` 会挤掉最前面的非 debuff（Player.cs:5317-5351）。
- **宠物/坐骑类 buff 互斥**：`AddBuff` 内部会移除同类旧 buff，属于原版行为，不额外处理。
- **SSC**：角色数据未加载完成时跳过（进服补发推迟到完全进服 State 10，避免 SSC 恢复 buff 被 diff 成新增触发全服广播）。