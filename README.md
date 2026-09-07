# TShockShareBuff · Buff 全服共享

一瓶药水，全服生效：任意在线玩家获得 buff 时，自动下发给全服其他在线玩家。

## 特性

- **两种共享模式**：触发式 `trigger`（各自独立倒计时）/ 镜像式 `mirror`（全服持续对齐并集）
- **白名单制**：只共享配置在 `gain` / `debuff` 里的 buff，`blacklist` 一键排除
- **宠物 / 坐骑 / 矿车代码层硬排除**，永远不参与共享
- **服务器接管时长**：全服剩余时间天然一致，重复喝药也会自动同步刷新
- **进服补发**：新玩家进服自动补上全服仍在生效的共享 buff（按剩余时长）
- **在线改配置** `/sbuff config`、**生成时长参考文档** `/sbuff docs`

## 安装

1. 把 `ShareBuff.dll` 放进 TShock 的 `ServerPlugins/` 目录
2. 重启服务器，或 `/tshock reload` 热加载
3. 首次启动自动生成配置 `tshock/ShareBuff/config.json`——白名单已预填全部增益 / 减益，**开箱即用**

> 基于 1.4.5.7 / TShock pr-3301 开发。

## 命令速查（别名 `/sbuff`）

全部需要权限 `sharebuff.admin`。

| 命令 | 说明 |
|------|------|
| `/sbuff on` / `off` | 运行时开关（不写回配置文件） |
| `/sbuff reload` | 重载 `config.json`（`/tshock reload` 也会触发） |
| `/sbuff list` | 列出全服当前在管的 buff 及剩余秒数，核对计时用 |
| `/sbuff clear debuff` | 清除所有在线玩家的减益 |
| `/sbuff clear all` | 清除所有在线玩家的全部 buff |
| `/sbuff clearplayer <玩家名> <debuff\|all>` | 清除**指定**玩家的减益或全部 buff（简写 `/sbuff cp`，支持带空格的玩家名） |
| `/sbuff config` | 查看全部配置项当前值；`/sbuff config <key> <value>` 在线修改，校验通过即写回文件 |
| `/sbuff docs` | 生成 buff 时长参考文档 `buff_time_ref.md`（含物品→buff 时长、白名单、黑名单），查 id / 调时长时用 |

## 配置（基础）

### 1. 核心开关

| 键 | 默认 | 说明 |
|----|------|------|
| `enabled` | `true` | 总开关 |
| `mode` | `trigger` | `trigger` 触发式：某刻获得 buff 时全服各来一段配置时长，之后各自独立倒计时；`mirror` 镜像式：只要还有人持有就持续补齐，全服列表始终对齐 |
| `shareDebuff` | `true` | 减益是否也全服共享（`debuff` 白名单生效的前提） |
| `giveOnJoin` | `true` | 进服补发（仅 `trigger` 模式）：进服按剩余时长补上全服生效中的共享 buff |
| `detectPotionUse` | `true` | 检测背包药水消耗感知"重复喝药"，自动刷新全服该 buff 时长 |
| `takeOverSource` | `true` | 连触发者本人的 buff 时长也统一到服务器值；`false` 时不动触发者 |
| `cooldownSeconds` | `5` | 同一 buff 的最小广播间隔（秒），防回环 |
| `exclude` | `[]` | 不参与共享的玩家名列表 |
| `debug` | `false` | 调试日志：输出广播 / 喝药 / 清账等决策细节到服务器日志 |

标量键可直接 `/sbuff config <key> <value>` 在线修改并写回文件；列表类（`gain` / `debuff` / `blacklist` / `exclude`）请编辑文件后 `/sbuff reload`。

### 2. 时长

| 键 | 默认 | 说明 |
|----|------|------|
| `gainDefaultSeconds` | `300` | 增益兜底时长（秒） |
| `debuffDefaultSeconds` | `30` | 减益兜底时长（秒） |
| `buffs` | `[]` | 逐条指定时长，优先级最高：`{ "id": 5, "seconds": 600 }` |

实际时长按此优先级取值：**`buffs` 表指定 → 药水原本时长（`Item.buffTime` 自动建表）→ 分类兜底**。想固定某个 buff 的时长就在 `buffs` 里加一条，单位都是秒。

> 恒持续类 buff（28 狼人 / 29 灵视 / 93 弹药箱等）照常共享，只是服务器不递减其计时（永久生效）——"是否共享"与"是否递减"是两件事。

## 名单管理

共享与否完全由三张名单决定，判断顺序：

```
blacklist（黑名单）→ 宠物/坐骑/矿车（代码硬排除）→ gain（增益白名单）→ debuff（减益白名单，需 shareDebuff=true）
```

不在任何白名单里的 id 一律不共享。

### 默认名单（首次启动自动写入）

| 名单 | 默认内容 |
|------|----------|
| `gain` | **全部增益 105 条**（共享） |
| `debuff` | **全部减益 87 条**（`shareDebuff=true` 时共享） |
| `blacklist` | 禁用类 15 条 + 鞭子类 24 条（307~369 段）：冰冻、石化、耐药性、窒息、旗帜、创意震撼、魔力涌动、控脑术、被网住、野性咬噬、强风、诅咒、微光闪烁、叶状水晶、钛金屏障，及全部鞭子 buff |
| 硬排除 | 宠物 120 / 坐骑 34 / 矿车 54，代码层排除，加进 `gain` 也不会共享 |

### 怎么调

- **不想共享某个增益**：从 `gain` 里删掉，或加进 `blacklist`（二选一即可；模板推荐黑名单，方便一眼看清排除了什么）
- **想共享某个不在白名单的 buff**：把 id 加进 `gain`（或 `debuff`）
- **id 从哪查**：`/sbuff docs` 生成的 `buff_time_ref.md` 有完整物品→buff 对照，或查 terraria.wiki.gg 增益 ID
- 改完执行 `/sbuff reload` 生效（`/sbuff config` 只能改标量键）

> 提示：开着 `shareDebuff` 时，被禁用 / 穿非法装备的玩家会触发 冰冻、石化 等控制类减益并传染全服——这类已经默认拉黑，服主对玩家下手也放心。

## 更多

- 技术细节（协议机制、实现结构、风险与对策）：[docs/technical.md](docs/technical.md)
- 更新日志：[changelog.md](changelog.md)