# TShockShareBuff · Buff 全服共享

## 1. 目标

任意在线玩家获得 buff 时，自动下发给全服其他在线玩家（一瓶药水全服生效）。

## 2. 技术前提（源码调研结论，1.4.5.7 / TShock pr-3301）

**服务器能拿到什么**

- 客户端 buff 变化时主动上报 `MessageID 50 (PlayerBuffs)`，携带的是**完整 buffType 列表**；`Main.cs:17884-17897` 逐帧比对本地与镜像，有变化才发，延迟约 1 帧。
- TShock 在 `GetDataHandlers.HandlePlayerBuffList` 中接管该包，写入 `TPlayer.buffType[]` 后 `return true`（GetDataHandlers.cs:3877-3907）。所以 `TShock.Players[i].TPlayer.buffType` 就是服务器视角的最新 buff 集合。
- `Player.maxBuffs = 44`（Player.cs:1491）。

**剩余时间：协议里没有，但可以拿到（服务器自计时）**

- 50 包确实不含时间——`NetMessage.cs:997-1010` 只写 buffType 列表；服务端收到后 `buffTime` 被硬编码成 60（`MessageBuffer.cs:2465`、`GetDataHandlers.cs:3938`）。OTAPI 也没有暴露 `Player.AddBuff` 钩子。
- 但服务器侧 `buffTime[]` 是块**自留地**：原版只在 `whoAmI == Main.myPlayer` 时递减（`Player.cs:9958-9960`），服务端 `Main.myPlayer == 255`，**永远不会自动递减**，50 包也不会覆盖我们写的值。
- 所以做法是**服务器接管时长**：检测到新增 buff → 补发一次 `55 + T` 把时长揽到自己手里 → 服务器每 tick 递减 remain。剩余时间精确到 tick，且全服天然一致。
- 初始时长 `T` 的来源（按优先级）：
  1. 配置表 `buffs[{id,seconds}]`
  2. `Item.buffTime` 自动建表：`Initialize` 时遍历 `ItemID.Count`，`item.SetDefaults(i)` 后取 `buffType→max(buffTime)`（`Item.cs:279/281`），覆盖绝大多数药水/食物类 buff
  3. `defaultSeconds`

**下发方式**

- `NetMessage.SendData(55, -1, -1, null, targetIndex, buffId, ticks)`。客户端 `case 55` 仅当 `number == myPlayer` 才 `AddBuff(type, time, fromNetPvP: true)`（绕过专家/难度时间修正），天然只作用于目标玩家。
- 已持有该 buff 时走 `AddBuff_TryUpdatingExistingBuffTime`，取 `max(旧值, T)`（`Player.cs:5394-5418`）——补发只会延长、不会缩短，可利用这一点"接管"。
- 等价写法 `TSPlayer.SetBuff(id, ticks, bypass: true)`；**必须 bypass**，否则会被 `LastThreat` 5 秒节流丢弃（TSPlayer.cs:2412-2418）。
- 时间单位是 tick（1/60 秒），上限 `int.MaxValue/60 - 1`。
- 移除 buff 没有专用包，只能发 `55 + time=1` 让其自然消失。

**倒计时边界（原版规则，服务器需对齐）**

- `BuffID.Sets.TimeLeftDoesNotDecrease`：宠物类 8 个 id（28/334/29/159/150/93/348/366，BuffID.cs:22）不递减，服务器也别减。
- `BuffID.Sets.AddBuffTimeAdditivelyToCap`：94 号累加至 600 tick 上限（BuffID.cs:34），非取 max。

**回环风险**

被广播的玩家会带着新 buff 上报 50 包 → 必须防止它再触发一次广播。

## 3. 功能需求

| # | 需求 | 说明 |
|---|------|------|
| FR1 | 检测新增 buff | `GameUpdate` 每 tick 遍历在线玩家 `TPlayer.buffType`，与快照 diff 取新增项（开销极小，无需轮询间隔） |
| FR2 | 接管 + 广播 | 对新增且允许共享的 buff：先给**触发者本人**补发 `55 + T`（把时长揽到服务器手里），再给其他在线玩家下发 `55 + T` |
| FR3 | 防回环 | 全局 buff 冷却表：同一 buffId 在 `cooldownSeconds` 内只广播一次 |
| FR4 | 过滤 | 按 `BuffCats` 分类表过滤：增益默认共享，减益由 `shareDebuff` 控制，**宠物 / 坐骑 / 矿车永不共享**（代码层硬排除，服主清空 blacklist 也不会失效）；另有 blacklist、未登录/SSC 未就绪玩家、exclude 名单玩家 |
| FR5 | 时长与计时 | 时长按 配置表 → `Item.buffTime` 建表 → `defaultSeconds` 优先级取值；服务器维护 `remainTicks` 倒计时表，每 tick 递减，跳过 `TimeLeftDoesNotDecrease` |
| FR6 | 快照重置 | `ServerJoin` 时预填该玩家快照（避免进服自带 buff 触发全服）；`ServerLeave` / 死亡重生时清空该玩家受管项 |
| FR7 | 热重载 | `GeneralHooks.ReloadEvent` + `/sharebuff reload` |
| FR8 | 开关 | `/sharebuff on|off|reload`，权限 `sharebuff.admin` |
| FR9 | 查询 | `/sharebuff list`：列出当前全服在管的 buff 及剩余时间，便于验证计时是否准确 |

### 两种共享模式（`mode` 可配）

- **`trigger` 触发式**：某人获得 buff 的那一刻，全服其他人各自获得一段配置时长，之后各自独立倒计时、互不影响。靠 `cooldownSeconds` 防回环。
- **`mirror` 镜像式**：全服 buff 列表始终对齐到并集——只要还有一人持有某 buff，全服缺失的人会被持续补发；所有人都失效后才撤销（撤销靠发 `55 + time=1`，有 1~2 秒延迟）。此模式下 `cooldownSeconds` 退化为补发节流间隔。

## 3.1 buff 分类（`BuffCats.cs`）

数据来源：`tr-wiki/buff_categories.md` 第六节的 C# 数组定义，共 400 条、彼此无交集。

| 类别 | 条数 | 是否共享 |
|------|------|----------|
| 增益 Gain | 105 | ✅ 默认共享 |
| 减益 Debuff | 87 | 由 `shareDebuff` 控制（默认否） |
| 宠物 Pet | 120 | ❌ 永不共享 |
| 坐骑 Mount | 34 | ❌ 永不共享 |
| 矿车 Minecart | 54 | ❌ 永不共享 |

不在任何表里的 id（如未来版本新增）一律不共享，宁可漏也不要错共享宠物/坐骑。

**注意**：增益表里含 28（狼人）、29（灵视）、93（弹药箱）、366 等 `TimeLeftDoesNotDecrease` 的 buff——它们照常共享，只是服务器不递减其计时（永久生效）。所以"是否共享"与"是否递减"是两件事。

## 4. 配置 `tshock/ShareBuff/config.json`

```json
{
  "enabled": true,
  "mode": "trigger",
  "takeOverSource": true,
  "defaultSeconds": 3600,
  "cooldownSeconds": 5,
  "shareDebuff": false,
  "detectPotionUse": true,
  "buffs": [ { "id": 3, "seconds": 3600 } ],
  "blacklist": [],
  "exclude": []
}
```

- `mode`：`trigger` | `mirror`
- `takeOverSource`：`true` 时连触发者本人的 buff 时长也统一到服务器值（保证全服完全一致）；`false` 时不动触发者
- `shareDebuff`：`true` 时减益也全服共享（默认 `false`）
- `detectPotionUse`：检测背包药水消耗以感知"重复喝药"，关闭后退化为靠计时归零自动重开一轮
- `blacklist`：额外的永不共享 id。宠物 / 坐骑 / 矿车已由 `BuffCats` 硬排除，无需在此列出
- `buffs[].seconds` 与 `defaultSeconds` 单位是**秒**；`Item.buffTime` 建表拿到的是 tick，无需配置

## 5. 命令

| 命令 | 权限 | 说明 |
|------|------|------|
| `/sharebuff on` / `off` | `sharebuff.admin` | 运行时开关（不写回配置文件） |
| `/sharebuff reload` | `sharebuff.admin` | 重载 `config.json` |
| `/sharebuff list` | `sharebuff.admin` | 列出每个玩家当前在管的 buff 及服务器计时的剩余秒数，用于核对计时是否准确 |

别名：`/sb`

## 5. 实现结构

```
ShareBuff/
  ShareBuff.cs   插件主体：hook 注册、GameUpdate 调度、接管与广播
  Config.cs      配置模型 + JSON 读写（参考 TShockGoodLucky/Config.cs）
  BuffClock.cs   时长来源表（Item.buffTime 建表 + 配置覆盖）+ tick 换算
  BuffCats.cs    buff 分类表（增益/减益/宠物/坐骑/矿车），由 wiki 文档生成
```

## 6. 风险与对策

- **时长取值的误差场景**：`Item.buffTime` 建表拿到的是"名义时长"。重叠喝药、专家/大师难度对 debuff 的时长加成、饱食类三档 buff 会与服务器值不符；`takeOverSource=true` 时以服务器值为准，这本身就是"全服共享"想要的一致性。
- **重复喝药（已处理）**：客户端刷新时长不会改变 buffType 列表，50 包不发、服务器无感。改由 `GetDataHandlers.PlayerSlot` 监控背包——某槽位原有物品恰好少 1 个且该物品 `buffType > 0`，即判定为喝药，用该物品自身的 `buffTime` 重置该玩家计时（`trigger` 只同步自己，`mirror` 全服一起刷新）。误判（如扔掉一瓶）的代价只是一次计时重置。
- **兜底**：即便漏检，计时归零时若客户端仍持有该 buff（`buffType` 里还在），会自动重开一轮，不会提前误清。
- **包量放大**：O(在线人数 × 新增 buff 数)，靠 `cooldownSeconds` 节流。
- **buff 槽位**：目标玩家 buff 满 44 时，`AddBuff` 会挤掉最前面的非 debuff（Player.cs:5317-5351）。
- **宠物/坐骑类 buff 互斥**：`AddBuff` 内部会移除同类旧 buff，属于原版行为，不额外处理。
- **SSC**：角色数据未加载完成时跳过，避免污染。
