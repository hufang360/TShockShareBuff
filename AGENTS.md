# AGENTS.md

本文件给在本仓库工作的 AI 代理看。保持精简。

## changelog 编写方法（先看这条）

- 文件：根目录 `changelog.md`。
- 结构：
  - 首行 `# 更新日志`；
  - 未发布内容放 `## 未发布（vX.Y.Z）`（版本取自 `ShareBuff/ShareBuff.csproj` 的 `<Version>`）；
  - 已发布用 `## YYYYMMDD（vX.Y.Z）`，新的在上。
- 条目：无序列表，主题一行 `- **主题**`，细节缩进 `  -`，更细 `    -`。
- 风格（重要）：
  - 一行别太长；长了就换行拆成子条目，偏结构化，方便阅读；
  - 通俗易懂，少堆术语（读者包括小白）；
  - 写清「做了什么、影响是什么」，多用命令/例子说明。

## 项目速览

- TShock 服务端插件「Buff 全服共享」（C# / net9.0），指令 `/sharebuff`（别名 `/sbuff`），权限 `sharebuff.admin`。
- 任意在线玩家获得白名单 buff → 下发给全服；`trigger` 触发式 / `mirror` 镜像式两种模式；服务器接管时长、逐 tick 自递减。
- 玩家向说明在 `README.md`，协议机制与实现细节在 `docs/technical.md`。

## 代码地图

- `ShareBuff/ShareBuff.cs` — 插件主体：hook 注册、每 tick 扫描 buffType diff、接管与广播、喝药检测、命令分发。
- `ShareBuff/Config.cs` — 配置模型 + JSON 读写；首次启动写入默认白名单（Gain/Debuff）与黑名单。
- `ShareBuff/BuffClock.cs` — 时长来源表（`Item.buffTime` 建表 + 红药水内置表 + 炼金强化 ×1.2）、tick 换算、`NoDecrease` 判断。
- `ShareBuff/BuffCats.cs` — buff 分类表（Gain 105 / Debuff 87 / Pet 120 / Mount 34 / Minecart 54，共 400 条无交集），由 `../tr-wiki` 生成。
- `docs/technical.md` — 协议调研、两种模式、红药水特判、clear 实现、风险；改行为记得同步。

## 构建

- 需要 .NET SDK；依赖 NuGet 包 `TShock`（6.2.1），不需要本地 `lib/`。
- 构建：`dotnet build TShockShareBuff.sln -c Release`（等价 `dotnet build ShareBuff/ShareBuff.csproj -c Release`）
  - 注意：`.sln` 曾漏配 Release 映射（现已修好），若 Release 编译“空过”多半是这里的问题。
- 产物：`ShareBuff/bin/Release/net9.0/ShareBuff.dll`。

## 参考源码

- Terraria 反编译源码 / TShock 源码常在同级目录：`../tr-code-1458`、`../TShock-pr-3301`（本插件按 1.4.5.7 / pr-3301 调研）。
- 反编译客户端可用 `ilspycmd`。

## 发布

- 更新 `ShareBuff/ShareBuff.csproj` 的 `<Version>`；
- 把 `changelog.md` 的「未发布（vX.Y.Z）」改成 `## YYYYMMDD（vX.Y.Z）`；
- `dotnet build ShareBuff/ShareBuff.csproj -c Release`，把 dll 复制为 `ShareBuff-vX.Y.Z-MMDD.dll`（MMDD = changelog 日期）；
- `gh release create vX.Y.Z <dll> -R hufang360/TShockShareBuff --prerelease --notes-file <说明>`；
- 更新 `README.md` 的下载链接（指向该 release 的资产）。

## 约定与坑

- 配置路径：`<TShock.SavePath>/ShareBuff/config.json`。
- `Initialize` 阶段**不能**调用 `Item.SetDefaults()`（游戏内容未就绪会 NRE，导致服务器启动中止）；`BuffClock` 懒加载 + try/catch 熔断。
- 时长优先级：红药水窗口 > 服务器计时 > `buffs` 表 > `Item.buffTime` > 分类兜底；`NoDecrease` 的 buff（宠物等）不递减。
- 下发必须 `SetBuff(..., bypass:true)`，否则被 `LastThreat` 5 秒节流丢弃。
- 防回环：`cooldownSeconds` + `_supplied` 回声登记；改动广播路径时别破坏。
- 新增/修改命令要同步三处：`ShareBuff.cs` 的命令分发、`/sbuff` 帮助文本、`README.md` 命令表。
- 注释、文档用中文，跟随现有风格。

## 注意

- 除非用户明确要求，否则不要 `git commit` / `git push`。
