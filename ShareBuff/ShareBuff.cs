using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace ShareBuff
{
    [ApiVersion(2, 1)]
    public class Plugin : TerrariaPlugin
    {
        #region Plugin Info
        public override string Author => "hufang360";
        public override string Description => "Buff全服共享";
        public override string Name => "ShareBuff";
        public override Version Version => Assembly.GetExecutingAssembly().GetName().Version;
        #endregion

        static readonly string SaveDir = Path.Combine(TShock.SavePath, "ShareBuff");
        static Config _config;

        // 白名单（来自配置，Reload 时重建，避免 List.Contains 的线性查找）
        static HashSet<int> _gainSet = [];
        static HashSet<int> _debuffSet = [];

        // 玩家 index -> buff id -> 剩余 tick（服务器自计时）
        readonly Dictionary<int, Dictionary<int, int>> _timers = [];

        // 玩家 index -> 上一帧 buff 集合（用于 diff 出新增项）
        readonly Dictionary<int, HashSet<int>> _snapshots = [];

        // buff id -> 上次广播时的 tick（防回环）
        readonly Dictionary<int, long> _cooldown = [];

        // buff id -> 最近一次全服广播的 (起始 tick, 总时长)。
        // 持有者没有服务器计时（如 takeOverSource=false 时的触发者本人）时，用它反推剩余时长
        readonly Dictionary<int, (long tick, int total)> _origin = [];

        // 玩家 index -> 服务器已下发、尚未收到客户端回声的 buff（回声不算新增，不重播）
        readonly Dictionary<int, HashSet<int>> _supplied = [];

        // 玩家 index -> 背包槽位 -> (物品 id, 数量)（用于检测喝药）
        readonly Dictionary<int, Dictionary<short, (short type, short stack)>> _inv = [];

        // 玩家 index -> 喝下红药水的 tick。窗口内该玩家 diff 出的新 buff 若在红药水表内，
        // 共享时长以红药水为准（客户端特判加 buff，物品表查不到时长）
        readonly Dictionary<int, long> _redPotionAt = [];

        // 红药水判定窗口（tick）：客户端喝药后 5/50 包同帧成对发出且 TCP 保序（5 在前），
        // 窗口只兜网络抖动与处理乱序，取 3 秒
        const int RedPotionWindow = 180;

        long _tick;

        public Plugin(Main game) : base(game)
        {
        }

        public override void Initialize()
        {
            Directory.CreateDirectory(SaveDir);
            Reload();
            // BuffClock 的时长表要等游戏内容就绪，由首次取值时懒加载

            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            ServerApi.Hooks.NetSendData.Register(this, OnSendData);
            GetDataHandlers.PlayerSlot += new EventHandler<GetDataHandlers.PlayerSlotEventArgs>(OnPlayerSlot);
            GeneralHooks.ReloadEvent += OnReload;
            Commands.ChatCommands.Add(new Command("sharebuff.admin", OnCommand, "sharebuff", "sbuff"));
        }

        #region 主循环

        void OnGameUpdate(EventArgs args)
        {
            if (_config == null || !_config.enabled)
                return;

            _tick++;

            TickTimers();
            ScanBuffs();

            // 镜像模式的对齐检查开销较大，降频到 0.5 秒一次
            if (_config.IsMirror && _tick % 30 == 0)
                SyncMirror();
        }

        /// <summary>服务器自计时：每 tick 递减，归零时按客户端是否仍持有决定重开一轮还是移除</summary>
        void TickTimers()
        {
            foreach (var kv in _timers)
            {
                int plr = kv.Key;
                Dictionary<int, int> map = kv.Value;

                // 到期不代表要移除（可能要重开一轮），先收集；遍历中不能改结构
                _expired.Clear();
                foreach (KeyValuePair<int, int> bk in map)
                {
                    if (BuffClock.NoDecrease(bk.Key))
                        continue;

                    if (map[bk.Key]-- > 0)
                        continue;
                    _expired.Add(bk.Key);
                }

                foreach (int buffId in _expired)
                {
                    TSPlayer plrObj = TShock.Players[plr];
                    if (IsValid(plrObj) && CurrentBuffs(plrObj).Contains(buffId))
                    {
                        // 客户端还挂着：说明服务器估短了，或玩家重复喝药刷新过。重开一轮。
                        DebugLog($"{plrObj.Name} 的 {BuffName(buffId)}({buffId}) 到期仍持有，重开一轮 {DurationOf(buffId) / 60}s");
                        map[buffId] = DurationOf(buffId);
                    }
                    else
                    {
                        map.Remove(buffId);
                    }
                }
            }
        }

        // TickTimers 复用的到期清单，避免每玩家每 tick 分配
        readonly List<int> _expired = new List<int>();

        /// <summary>扫描全服 buff，diff 出新增项并广播。快照原地更新，主循环不产生新分配</summary>
        void ScanBuffs()
        {
            foreach (TSPlayer plr in TShock.Players)
            {
                if (!IsValid(plr))
                    continue;

                int idx = plr.Index;
                if (!_snapshots.TryGetValue(idx, out HashSet<int> prev))
                {
                    // 首次见到该玩家：只记快照，不广播（避免进服自带 buff 触发全服）
                    var first = new HashSet<int>();
                    FillBuffs(plr, first);
                    _snapshots[idx] = first;

                    // 建立喝药检测的背包基线（关键）：进服时客户端回传的整包槽位更新
                    // 发生在 State<10，被 IsValid 拦截、_inv 根本没建起来——
                    // 后果是进服后第一次吃/喝会被当作“无旧记录”漏检，共享时长退化成
                    // BuffClock 的最大值兜底（如棉花糖 60s 被当成 1500s）。
                    // State 10 时服务器端 Player.inventory 已是权威值（SSC 恢复 / vanilla 已应用客户端回执），
                    // 网络槽位 0~57 与 Player.inventory 下标恒等（见 PlayerItemSlotID.Inventory0 分配），直接快照。
                    var inv = GetInv(idx);
                    Item[] tInv = plr.TPlayer.inventory;
                    int slots = Math.Min(58, tInv?.Length ?? 0);
                    for (int s = 0; s < slots; s++)
                    {
                        Item it = tInv[s];
                        if (it == null || it.type <= 0 || it.stack <= 0)
                            continue;
                        inv[(short)s] = ((short)it.type, (short)it.stack);
                    }
                    DebugLog($"{plr.Name} 进入跟踪，背包基线 {inv.Count} 槽，自带 {first.Count} 个 buff");

                    // trigger 模式的进服补发；mirror 模式由 SyncMirror 持续对齐，无需处理
                    if (_config.giveOnJoin && !_config.IsMirror)
                        GiveOnJoin(plr);
                    continue;
                }

                // 先 diff 新增：buffType 里 prev 没有的就是新增
                int[] buffType = plr.TPlayer.buffType;
                for (int i = 0; i < Player.maxBuffs; i++)
                {
                    int b = buffType[i];
                    if (b <= 0 || prev.Contains(b))
                        continue;

                    // 服务器自己刚下发的 buff：客户端回声不算新增，不重播
                    if (_supplied.TryGetValue(idx, out HashSet<int> supplied) && supplied.Remove(b))
                    {
                        DebugLog($"{plr.Name} 的 {BuffName(b)}({b}) 是服务器回声，跳过");
                        continue;
                    }

                    OnBuffGained(plr, b);
                }

                // 快照原地更新，避免每玩家每 tick 分配新集合
                prev.Clear();
                FillBuffs(plr, prev);
            }
        }

        /// <summary>镜像模式：全服对齐到并集——缺的补发，全服都失效的清账</summary>
        void SyncMirror()
        {
            var active = new HashSet<int>();
            var players = new List<TSPlayer>();
            // buffId -> 全服当前剩余时长（取持有者里最大的，这样 gbuff 指定的时长能被沿用）
            var remain = new Dictionary<int, int>();

            foreach (TSPlayer plr in TShock.Players)
            {
                if (!IsValid(plr))
                    continue;
                players.Add(plr);
                foreach (int buffId in CurrentBuffs(plr))
                {
                    if (!CanShare(buffId))
                        continue;

                    active.Add(buffId);

                    int t = RemainingOf(buffId, GetTimer(plr.Index, buffId));
                    if (t > 0 && (!remain.TryGetValue(buffId, out int known) || t > known))
                        remain[buffId] = t;
                }
            }

            // 全服已无人持有的 buff：清掉残余计时
            foreach (var kv in _timers)
            {
                foreach (int buffId in kv.Value.Keys.ToList())
                {
                    if (!active.Contains(buffId))
                        kv.Value.Remove(buffId);
                }
            }

            // 还缺 buff 的玩家补发
            foreach (TSPlayer plr in players)
            {
                HashSet<int> cur = CurrentBuffs(plr);
                int missing = 0;
                foreach (int buffId in active)
                {
                    if (cur.Contains(buffId))
                        continue;

                    int ticks = remain.TryGetValue(buffId, out int r) && r > 0 ? r : DurationOf(buffId);
                    SetTimer(plr.Index, buffId, ticks);
                    GetSupplied(plr.Index).Add(buffId);
                    plr.SetBuff(buffId, ticks, true);
                    missing++;
                }

                if (missing > 0)
                    DebugLog($"镜像补发：{plr.Name} 缺 {missing} 项");
            }
        }

        /// <summary>
        /// 进服补发（trigger 模式 + giveOnJoin）：把全服仍在生效的共享 buff 按最大剩余时长补给新玩家。
        /// 剩余时长反推见 RemainingOf；下发的 buff 登记 _supplied，
        /// 客户端回 50 包时被 ScanBuffs 识别为回声，不再当新增重播一轮。
        /// </summary>
        void GiveOnJoin(TSPlayer target)
        {
            // buffId -> 全服最大剩余时长（取持有者里最大的）
            var remain = new Dictionary<int, int>();
            foreach (TSPlayer p in TShock.Players)
            {
                if (!IsValid(p) || p.Index == target.Index)
                    continue;

                foreach (int buffId in CurrentBuffs(p))
                {
                    if (!CanShare(buffId))
                        continue;

                    int t = RemainingOf(buffId, GetTimer(p.Index, buffId));
                    if (!remain.TryGetValue(buffId, out int known) || t > known)
                        remain[buffId] = t;
                }
            }

            // 目标已持有的（如 SSC 进服自带）不登记回声，避免误吞他之后真正的新增
            HashSet<int> has = CurrentBuffs(target);
            foreach (var kv in remain)
            {
                int ticks = kv.Value > 0 ? kv.Value : DurationOf(kv.Key);
                SetTimer(target.Index, kv.Key, ticks);
                if (!has.Contains(kv.Key))
                    GetSupplied(target.Index).Add(kv.Key);
                target.SetBuff(kv.Key, ticks, true);
            }

            if (remain.Count > 0)
                DebugLog($"进服补发给 {target.Name}：{remain.Count} 项");
        }

        #endregion

        #region 事件

        void OnBuffGained(TSPlayer src, int buffId)
        {
            if (!CanShare(buffId))
                return;

            // 防回环：被广播的玩家会带着同样 buff 上报 50 包
            if (_cooldown.TryGetValue(buffId, out long last) &&
                _tick - last < (long)_config.cooldownSeconds * 60)
            {
                DebugLog($"拦截 {BuffName(buffId)}({buffId})：{src.Name} 触发时处于冷却中");
                return;
            }
            _cooldown[buffId] = _tick;

            // 服务器已经下发过（gbuff / 其他插件 / 上一轮广播）就沿用它的时长，不拿估算值覆盖
            int ticks = GetTimer(src.Index, buffId);
            if (ticks <= 0)
                ticks = DurationOf(buffId);

            // 红药水窗口内：其表内的 buff 多数也能由其他药水 / 来源获得（普通世界是 11 个减益、
            // FTW 世界是 18 个可由药水获得的增益），时长一律以红药水实际给予的为准
            if (InRedPotionWindow(src.Index))
            {
                int rp = BuffClock.RedPotionTicks(buffId);
                if (rp > 0)
                    ticks = rp;
            }

            DebugLog($"广播 {BuffName(buffId)}({buffId}) {ticks / 60}s，由 {src.Name} 触发");
            Apply(src, buffId, ticks, _config.takeOverSource);
        }

        /// <summary>
        /// 捕获服务器发出的 55 包（PlayerAddBuff）。任何服务器侧下发的 buff——gbuff 命令、
        /// 其他插件、我们自己的广播——都能在这里拿到确切时长，不必再估算。
        /// 注意：这里只记账，不能发包，否则会递归。
        /// </summary>
        void OnSendData(SendDataEventArgs e)
        {
            if (_config == null || !_config.enabled)
                return;
            if (e.MsgId != PacketTypes.PlayerAddBuff)
                return;

            int plr = e.number;
            if (plr < 0 || plr >= TShock.Players.Length)
                return;

            int buffId = (int)e.number2;
            int ticks = (int)e.number3;
            if (buffId <= 0 || ticks <= 0)
                return;

            // 非共享 buff 不记账：TickTimers 到期后“还持有就重开一轮”会无限空转，list 也全是噪音
            if (!CanShare(buffId))
                return;

            SetTimer(plr, buffId, ticks);
        }

        /// <summary>
        /// 重复喝药：客户端自己把 buff 刷回满时长，但 50 包只有 buffType、看不到刷新，
        /// 服务器靠背包物品数量减少来感知，把计时同步回来，并重新广播一轮
        /// （trigger / mirror 均生效，其他玩家的该 buff 时长随之重算）。
        /// </summary>
        void OnPlayerSlot(object sender, GetDataHandlers.PlayerSlotEventArgs e)
        {
            if (_config == null || !_config.enabled || !_config.detectPotionUse)
                return;
            if (!IsValid(e.Player))
                return;

            short slot = e.Slot;
            if (slot < 0 || slot >= PlayerItemSlotID.Count)
                return;

            Dictionary<short, (short type, short stack)> inv = GetInv(e.Player.Index);
            inv.TryGetValue(slot, out (short type, short stack) old);
            inv[slot] = ((short)e.Type, (short)e.Stack);

            DebugLog($"槽位更新 {e.Player.Name} slot={slot} old=({old.type},{old.stack}) new=({e.Type},{e.Stack})");

            // 只认“该槽位原有物品少了 1 个”
            if (old.type <= 0 || e.Stack >= old.stack || old.stack - e.Stack != 1)
                return;
            if (e.Type != 0 && e.Type != old.type)
                return;

            // 红药水：客户端特判一次加一组 buff（普通世界 11 个减益 / FTW 世界 18 选 3 随机增益），
            // 物品表查不到时长，只登记标记；后续 ScanBuffs diff 出的新 buff 时长由红药水窗口判定
            if (old.type == ItemID.RedPotion)
            {
                _redPotionAt[e.Player.Index] = _tick;
                DebugLog($"{e.Player.Name} 使用红药水，开启 {RedPotionWindow / 60}s 判定窗口");
                return;
            }

            (int buffType, int buffTime) = BuffClock.GetItemBuff(old.type);
            if (buffType <= 0 || !CanShare(buffType))
            {
                DebugLog($"{e.Player.Name} 消耗 {old.type} 无共享 buff（type={buffType}）");
                return;
            }

            int ticks = buffTime > 0 ? buffTime : DurationOf(buffType);

            // 源头持有「炼金强化」(366)（tile 699 炼金台互动获得）时喝经典药水，
            // 客户端实际给的是 buffTime ×1.2（延长集 1-18、104-117、121-124、257、343）；
            // 服务端发 55 包走 fromNetPvP 不再二次修正，这里按客户端实际给终值
            if (BuffClock.IsExtendedByPotionStation(buffType) && HasBuff(e.Player, BuffID.DeadCellsPotionStation))
            {
                int scaled = BuffClock.ScaleByPotionStation(ticks);
                DebugLog($"{e.Player.Name} 持炼金强化(366)，{BuffName(buffType)}({buffType}) 时长 {ticks / 60}s ×1.2 = {scaled / 60}s");
                ticks = scaled;
            }

            // 服务器计时先同步（不凭 HasBuff 过滤：首次喝药时槽位 5 包先于 buff 50 包处理，
            // 服务器端 buffType 还没加上该 buff，拿 HasBuff 当“丢弃 vs 喝药”闸门会误判为丢弃，
            // 导致该 buff 精确时长被吞、退化成 DurationOf 的最大值兜底——棉花糖 60s→1500s 就是这么来的）
            SetTimer(e.Player.Index, buffType, ticks);

            bool holds = HasBuff(e.Player, buffType);
            DebugLog($"{e.Player.Name} 消耗物品 {old.type}，{BuffName(buffType)}({buffType}) {ticks / 60}s（已持有={holds}）");

            if (holds)
            {
                // 重复喝药：客户端已自行刷新，直接按精确时长重新广播一轮，其他玩家随之重算
                // （丢弃误判的代价只是多刷一轮，与旧逻辑一致）
                Apply(e.Player, buffType, ticks, false);
            }
            // 非持有 = 首次喝药：等 50 包回声，由 OnBuffGained 用刚同步的 GetTimer 精确时长广播
        }

        void OnServerLeave(LeaveEventArgs args)
        {
            _timers.Remove(args.Who);
            _snapshots.Remove(args.Who);
            _inv.Remove(args.Who);
            _supplied.Remove(args.Who);
            _redPotionAt.Remove(args.Who);
        }

        void OnReload(ReloadEventArgs args)
        {
            Reload();
            args.Player?.SendSuccessMessage("已重载 ShareBuff 配置文件");
        }

        void Reload()
        {
            _config = Config.Load(ConfigPath);
            _gainSet = [.. _config.gain];
            _debuffSet = [.. _config.debuff];

            // buffs 表建索引（重复 id 时先出现的生效，与旧的线性扫描一致）
            _cfgBuffSeconds = new Dictionary<int, int>();
            foreach (BuffData d in _config.buffs)
            {
                if (d.id > 0 && !_cfgBuffSeconds.ContainsKey(d.id))
                    _cfgBuffSeconds[d.id] = d.seconds;
            }
        }

        // buffs 配置表的 id -> seconds 索引，Reload 时重建
        static Dictionary<int, int> _cfgBuffSeconds = new Dictionary<int, int>();

        #endregion

        #region 命令

        void OnCommand(CommandArgs args)
        {
            TSPlayer p = args.Player;
            string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLower() : "";

            switch (sub)
            {
                case "on":
                    _config.enabled = true;
                    p.SendSuccessMessage("ShareBuff 已开启");
                    break;
                case "off":
                    _config.enabled = false;
                    p.SendSuccessMessage("ShareBuff 已关闭");
                    break;
                case "reload":
                case "r":
                    Reload();
                    p.SendSuccessMessage("已重载 ShareBuff 配置文件");
                    break;
                case "list":
                case "l":
                    ListTracked(p);
                    break;
                case "clear":
                case "c":
                    ClearBuffs(args);
                    break;
                case "clearplayer":
                case "cp":
                    ClearPlayerBuffs(args);
                    break;
                case "config":
                    ConfigCmd(args);
                    break;
                case "blacklist":
                case "bl":
                    BlacklistCmd(args);
                    break;
                case "docs":
                    GenerateReference(p);
                    break;
                default:
                    p.SendInfoMessage("/sharebuff 指令用法（可简写 /sbuff）：");
                    p.SendInfoMessage("  on         - 开启共享 buff");
                    p.SendInfoMessage("  off        - 关闭共享 buff");
                    p.SendInfoMessage("  reload     - 重载配置文件");
                    p.SendInfoMessage("  list       - 查看正在管理的 buff");
                    p.SendInfoMessage("  clear      - 清除所有在线玩家的 buff");
                    p.SendInfoMessage("  clearplayer- 清除指定玩家的 buff，可简写 cp");
                    p.SendInfoMessage("  config     - 查看 / 修改配置项，改动验证通过后写回文件");
                    p.SendInfoMessage("  blacklist  - 查看 / 增删黑名单中的 buff，可简写 bl");
                    p.SendInfoMessage("  docs       - 在配置目录生成 buff 时长参考文档（.md）");
                    break;
            }
        }

        void ListTracked(TSPlayer p)
        {
            var lines = new List<string>();
            foreach (var kv in _timers)
            {
                TSPlayer plr = TShock.Players[kv.Key];
                if (plr == null || kv.Value.Count == 0)
                    continue;

                var buffs = kv.Value
                    .Where(x => x.Value > 0)
                    .OrderBy(x => x.Key)
                    .ToList();
                if (buffs.Count == 0)
                    continue;

                // 每个玩家一块：名字一行，下面每行最多 5 个（名字过长的独占一行）
                lines.Add($"{plr.Name}（{buffs.Count} 个）：");
                lines.AddRange(FormatBuffGrid(buffs.Select(x =>
                {
                    string n = TShock.Utils.GetBuffName(x.Key);
                    return ($"{n}({x.Key}) 剩 {x.Value / 60}s", DisplayWidth(n));
                }), maxWidth: 100));
            }

            if (lines.Count == 0)
                p.SendInfoMessage("当前没有在管的 buff");
            else
                lines.ForEach(p.SendInfoMessage);
        }

        /// <summary>
        /// /sbuff clear：清除所有在线玩家的 buff。见 ClearOne 的实现说明。
        /// </summary>
        void ClearBuffs(CommandArgs args)
        {
            TSPlayer p = args.Player;
            string what = args.Parameters.Count > 1 ? args.Parameters[1].ToLower() : "help";

            if (what == "help")
            {
                p.SendInfoMessage("用法：/sbuff clear <debuff|all|help>");
                p.SendInfoMessage("  debuff - 清除所有在线玩家的减益");
                p.SendInfoMessage("  all    - 清除所有在线玩家的全部 buff");
                p.SendInfoMessage("指定单个玩家请用 /sbuff clearplayer");
                return;
            }

            if (what != "debuff" && what != "all")
            {
                p.SendErrorMessage($"无效参数：{what}。用法：/sbuff clear <debuff|all|help>");
                return;
            }

            bool all = what == "all";
            int players = 0, cleared = 0;
            foreach (TSPlayer plr in TShock.Players)
            {
                if (!IsValid(plr))
                    continue;

                players++;
                cleared += ClearOne(plr, all);
            }

            // 全服已无人持有：广播起点记录一并清账，防反推出已死时长
            foreach (int buffId in _origin.Keys.ToList())
            {
                if (all || IsDebuff(buffId))
                    _origin.Remove(buffId);
            }

            DebugLog($"clear {(all ? "all" : "debuff")}：{players} 名玩家共 {cleared} 项");
            p.SendSuccessMessage($"已为 {players} 名在线玩家清除 {(all ? "全部" : "减益")} buff，共 {cleared} 项");
        }

        /// <summary>
        /// /sbuff clearplayer（cp）：/sbuff cp <玩家名> <debuff|all>，清除指定玩家的 buff
        /// </summary>
        void ClearPlayerBuffs(CommandArgs args)
        {
            TSPlayer p = args.Player;
            if (args.Parameters.Count < 2 || args.Parameters[1].ToLower() == "help")
            {
                p.SendInfoMessage("用法：/sbuff clearplayer <玩家名> <debuff|all>（可简写 /sbuff cp）");
                return;
            }

            // 最后一个参数是 debuff|all，中间的全部拼作玩家名（支持带空格的名字）
            string what = args.Parameters[args.Parameters.Count - 1].ToLower();
            if (what != "debuff" && what != "all")
            {
                p.SendErrorMessage($"无效参数：{what}。用法：/sbuff cp <玩家名> <debuff|all>");
                return;
            }

            string name = string.Join(" ", args.Parameters.Skip(1).Take(args.Parameters.Count - 2));
            if (name.Length == 0)
            {
                p.SendErrorMessage("缺少玩家名。用法：/sbuff cp <玩家名> <debuff|all>");
                return;
            }

            List<TSPlayer> found = TSPlayer.FindByNameOrID(name);
            if (found.Count == 0)
            {
                p.SendErrorMessage($"未找到玩家：{name}");
                return;
            }
            if (found.Count > 1)
            {
                p.SendErrorMessage($"匹配到多个玩家：{string.Join(", ", found.Select(x => x.Name))}");
                return;
            }

            TSPlayer target = found[0];
            if (target.TPlayer == null)
                return;

            // 管理员点名清除不受 exclude / 进服状态限制
            int cleared = ClearOne(target, what == "all");
            DebugLog($"clearplayer {target.Name} {what}：{cleared} 项（by {p.Name}）");
            p.SendSuccessMessage($"已为 {target.Name} 清除 {(what == "all" ? "全部" : "减益")} buff，共 {cleared} 项");
        }

        /// <summary>
        /// 清除单个玩家的目标 buff：清服务器端数组 → 广播 50 包 → 55 恢复保留项时长 → 清账。
        /// 不能用 55 包 time=1：客户端 AddBuff 对已有 buff 只延长不缩短，发了等于没发。
        /// 50 包副作用：客户端会把列表内所有 buff 的 buffTime 硬写成 60（1 秒），
        /// SSC 下保留的增益会立刻过期，所以清完要紧跟 55 包把保留项时长灌回去。
        /// 返回清除的项数。
        /// </summary>
        int ClearOne(TSPlayer plr, bool all)
        {
            Player t = plr.TPlayer;
            bool removed = false;
            int cleared = 0;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                int b = t.buffType[i];
                if (b <= 0)
                    continue;
                if (!all && !IsDebuff(b))
                    continue;

                t.buffType[i] = 0;
                t.buffTime[i] = 0;
                removed = true;
                cleared++;
            }

            if (removed)
            {
                // 广播给所有人（不是只发本人）：其他客户端要靠它刷新头顶 buff 图标。
                TSPlayer.All.SendData(PacketTypes.PlayerBuff, "", plr.Index);

                foreach (int buffId in CurrentBuffs(plr))
                {
                    if (BuffClock.NoDecrease(buffId))
                        continue; // 恒持续类（装备/饰品续命）不递减，无需恢复

                    // 服务器计时 > 广播起点反推 > 分类兜底。宁可刷满也不能让 buff 消失
                    int ticks = RemainingOf(buffId, GetTimer(plr.Index, buffId));
                    if (ticks <= 0)
                        ticks = DurationOf(buffId);

                    // AddBuff 只延长不缩短，客户端实际剩余更长时无副作用
                    plr.SetBuff(buffId, ticks, true);
                }
            }

            // 清账：计时器与未回声登记，残留会让后续同 buff 的新增被误判
            if (_timers.TryGetValue(plr.Index, out Dictionary<int, int> map))
            {
                foreach (int buffId in map.Keys.ToList())
                {
                    if (all || IsDebuff(buffId))
                        map.Remove(buffId);
                }
            }
            if (_supplied.TryGetValue(plr.Index, out HashSet<int> supplied))
            {
                supplied.RemoveWhere(b => all || IsDebuff(b));
            }

            return cleared;
        }

        /// <summary>
        /// /sbuff config：无参列出可配置项及当前值；/sbuff config <key> <value> 修改并验证，
        /// 通过后更新内存并写回 config.json（列表类 gain/debuff/exclude 请直接编辑文件后 reload；
        /// blacklist 可用 /sbuff blacklist 在线增删）
        /// </summary>
        void ConfigCmd(CommandArgs args)
        {
            TSPlayer p = args.Player;
            if (args.Parameters.Count < 3)
            {
                List<string> lines =
                [
                    "用法：/sbuff config <key> <value>（验证通过后写回文件，无参查看当前值）",
                    $"  enabled = {_config.enabled}（true/false）",
                    $"  mode = {_config.mode}（trigger/mirror）",
                    $"  takeOverSource = {_config.takeOverSource}（true/false）",
                    $"  gainDefaultSeconds = {_config.gainDefaultSeconds}（1~86400）",
                    $"  debuffDefaultSeconds = {_config.debuffDefaultSeconds}（1~86400）",
                    $"  cooldownSeconds = {_config.cooldownSeconds}（0~3600）",
                    $"  shareDebuff = {_config.shareDebuff}（true/false）",
                    $"  detectPotionUse = {_config.detectPotionUse}（true/false）",
                    $"  giveOnJoin = {_config.giveOnJoin}（true/false，仅 trigger 模式）",
                    $"  debug = {_config.debug}（true/false，决策日志）",
                    $"  blacklist 管理请使用 /sbuff blacklist 指令",
                ];
                p.SendInfoMessage(string.Join("\n", lines));
                return;
            }

            string key = args.Parameters[1].ToLower();
            string val = args.Parameters[2];
            if (!TrySetConfig(key, val, out string err))
            {
                p.SendErrorMessage($"设置失败：{err}");
                return;
            }

            try
            {
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(_config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ShareBuff] 写入配置文件失败：{ex}");
                p.SendErrorMessage("写入配置文件失败，详见服务器日志（内存值已更新，重启后丢失）");
                return;
            }

            DebugLog($"config {key} = {val}（by {p.Name}）");
            p.SendSuccessMessage($"已设置 {key} = {val}，验证通过并写入配置文件");
            if (key == "mode")
                p.SendInfoMessage(val.Equals("mirror", StringComparison.OrdinalIgnoreCase)
                    ? "已切换到镜像模式：全服 buff 将持续对齐到并集"
                    : "已切换到触发式：各自独立倒计时");
        }

        /// <summary>
        /// /sbuff blacklist（简写 bl）：查看黑名单（带 buff 名称与 id），或 add / del 单个 buff id。
        /// 增删后立即写回 config.json；CanShare 实时读取 blacklist，改完即刻生效（已在管的 buff 不受影响）。
        /// </summary>
        void BlacklistCmd(CommandArgs args)
        {
            TSPlayer p = args.Player;
            string act = args.Parameters.Count > 1 ? args.Parameters[1].ToLower() : "list";

            switch (act)
            {
                case "list":
                case "l":
                    {
                        if (_config.blacklist.Count == 0)
                        {
                            p.SendInfoMessage("blacklist 黑名单为空");
                            return;
                        }

                        var lines = FormatBuffGrid(_config.blacklist
                            .Select(id =>
                            {
                                string name = BuffName(id);
                                return ($"{name}({id})", DisplayWidth(name));
                            }));

                        if (!PaginationTools.TryParsePageNumber(args.Parameters, 2, p, out int pageNumber))
                            return;

                        PaginationTools.SendPage(p, pageNumber, lines, new PaginationTools.Settings
                        {
                            HeaderFormat = $"[c/96FF0A:/sbuff blacklist]（共 {_config.blacklist.Count} 条）({{0}}/{{1}})：",
                            FooterFormat = "输入[c/96FF0A:/sbuff bl list {{0}}]查看更多".SFormat(Commands.Specifier),
                            MaxLinesPerPage = 15,
                        });
                        return;
                    }
                case "add":
                case "a":
                    {
                        if (!TryParseBuffId(args, false, out int id))
                            return;
                        if (_config.blacklist.Contains(id))
                        {
                            p.SendInfoMessage($"blacklist 已包含 {BuffName(id)}({id})，未改动");
                            return;
                        }

                        _config.blacklist.Add(id);
                        _config.blacklist.Sort();
                        SaveConfig(p, $"已加入黑名单：{BuffName(id)}({id})");
                        if (BuffCats.Excluded.Contains(id))
                            p.SendInfoMessage($"提示：{BuffName(id)}({id}) 属于宠物 / 坐骑 / 矿车，代码层已硬排除，无需拉黑");
                        return;
                    }
                case "del":
                case "delete":
                case "remove":
                case "rm":
                case "d":
                    {
                        if (!TryParseBuffId(args, true, out int id))
                            return;
                        if (!_config.blacklist.Remove(id))
                        {
                            p.SendInfoMessage($"blacklist 里没有 {BuffName(id)}({id})，未改动");
                            return;
                        }

                        SaveConfig(p, $"已移出黑名单：{BuffName(id)}({id})");
                        return;
                    }
                default:
                    p.SendInfoMessage("用法：/sbuff blacklist [list [页码] | add <id> | del <id>]（可简写 /sbuff bl）");
                    p.SendInfoMessage("  list - 查看黑名单（带 buff 名称与 id）");
                    p.SendInfoMessage("  add  - 加入单个 buff id（1~" + (BuffID.Count - 1) + "）");
                    p.SendInfoMessage("  del  - 移出单个 buff id");
                    return;
            }
        }

        /// <summary>
        /// 解析第 3 个参数为黑名单 buff id。allowLoose 时只要求正整数（旧配置里的越界 id 也要能删掉），
        /// 否则必须是 1~BuffID.Count-1 的已知 buff id；失败时已回发错误。
        /// </summary>
        bool TryParseBuffId(CommandArgs args, bool allowLoose, out int id)
        {
            id = 0;
            TSPlayer p = args.Player;
            if (args.Parameters.Count < 3)
            {
                p.SendErrorMessage("缺少 buff id");
                return false;
            }

            if (!int.TryParse(args.Parameters[2], out id) || id <= 0 || (!allowLoose && id >= BuffID.Count))
            {
                p.SendErrorMessage(allowLoose
                    ? "buff id 需要正整数"
                    : $"buff id 需要 1~{BuffID.Count - 1} 的整数");
                return false;
            }
            return true;
        }

        /// <summary>把当前配置写回 config.json；失败时内存值已更新（重启后丢失）</summary>
        void SaveConfig(TSPlayer p, string okMsg)
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(_config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ShareBuff] 写入配置文件失败：{ex}");
                p.SendErrorMessage("写入配置文件失败，详见服务器日志（内存值已更新，重启后丢失）");
                return;
            }

            DebugLog($"{okMsg}（by {p.Name}）");
            p.SendSuccessMessage(okMsg + "，已写入配置文件");
        }

        static string ConfigPath => Path.Combine(SaveDir, "config.json");

        /// <summary>验证并写入单个配置项。键名不分大小写；返回 false 时 err 带原因</summary>
        bool TrySetConfig(string key, string val, out string err)
        {
            err = null;
            switch (key)
            {
                case "enabled":
                    if (!TryBool(val, out bool enabled)) { err = "enabled 需要 true/false"; return false; }
                    _config.enabled = enabled;
                    return true;
                case "mode":
                    if (!val.Equals("trigger", StringComparison.OrdinalIgnoreCase) &&
                        !val.Equals("mirror", StringComparison.OrdinalIgnoreCase))
                    { err = "mode 需要 trigger 或 mirror"; return false; }
                    _config.mode = val.ToLower();
                    return true;
                case "takeoversource":
                    if (!TryBool(val, out bool take)) { err = "takeOverSource 需要 true/false"; return false; }
                    _config.takeOverSource = take;
                    return true;
                case "sharedebuff":
                    if (!TryBool(val, out bool share)) { err = "shareDebuff 需要 true/false"; return false; }
                    _config.shareDebuff = share;
                    return true;
                case "detectpotionuse":
                    if (!TryBool(val, out bool detect)) { err = "detectPotionUse 需要 true/false"; return false; }
                    _config.detectPotionUse = detect;
                    return true;
                case "giveonjoin":
                    if (!TryBool(val, out bool give)) { err = "giveOnJoin 需要 true/false"; return false; }
                    _config.giveOnJoin = give;
                    return true;
                case "debug":
                    if (!TryBool(val, out bool dbg)) { err = "debug 需要 true/false"; return false; }
                    _config.debug = dbg;
                    return true;
                case "gaindefaultseconds":
                    if (!TryInt(val, 1, 86400, out int gs)) { err = "gainDefaultSeconds 需要 1~86400 的整数（秒）"; return false; }
                    _config.gainDefaultSeconds = gs;
                    return true;
                case "debuffdefaultseconds":
                    if (!TryInt(val, 1, 86400, out int ds)) { err = "debuffDefaultSeconds 需要 1~86400 的整数（秒）"; return false; }
                    _config.debuffDefaultSeconds = ds;
                    return true;
                case "cooldownseconds":
                    if (!TryInt(val, 0, 3600, out int cs)) { err = "cooldownSeconds 需要 0~3600 的整数（秒）"; return false; }
                    _config.cooldownSeconds = cs;
                    return true;
                default:
                    err = $"未知配置项：{key}（gain/debuff/exclude 等列表请编辑配置文件后 /sbuff reload；blacklist 用 /sbuff blacklist）";
                    return false;
            }
        }

        static bool TryBool(string s, out bool v) => bool.TryParse(s, out v);

        static bool TryInt(string s, int min, int max, out int v)
        {
            return int.TryParse(s, out v) && v >= min && v <= max;
        }

        /// <summary>是否减益：debuff 白名单优先；未分类的 id 按非增益、非宠物/坐骑/矿车兜底</summary>
        bool IsDebuff(int buffId)
        {
            if (_debuffSet.Contains(buffId))
                return true;
            return !_gainSet.Contains(buffId) && !BuffCats.Excluded.Contains(buffId);
        }

        /// <summary>
        /// /sbuff docs：在配置目录生成 buff 时长参考文档（.md）。
        /// 覆盖范围：物品能赋予的 buff ∪ 配置白名单（gain/debuff）∪ 配置 buffs 时长覆盖。
        /// 数据来源：BuffClock 建表（全部物品的 buffType/buffTime）+ 当前配置黑名单 / 白名单 / 时长覆盖。
        /// </summary>
        void GenerateReference(TSPlayer p)
        {
            try
            {
                BuffClock.Build(); // 命令阶段游戏内容已加载，可安全建表
                Dictionary<int, List<(int itemId, int ticks)>> sources = BuffClock.GetBuffSources();

                var ids = new SortedSet<int>(sources.Keys);
                foreach (int id in _config.gain) ids.Add(id);
                foreach (int id in _config.debuff) ids.Add(id);
                foreach (BuffData d in _config.buffs)
                {
                    if (d.id > 0)
                        ids.Add(d.id);
                }

                var rows = new Dictionary<string, List<string>>();
                foreach (int id in ids)
                {
                    string cat = "未分类";
                    if (BuffCats.Gain.Contains(id)) cat = "增益";
                    else if (BuffCats.Debuff.Contains(id)) cat = "减益";
                    else if (BuffCats.Pet.Contains(id)) cat = "宠物";
                    else if (BuffCats.Mount.Contains(id)) cat = "坐骑";
                    else if (BuffCats.Minecart.Contains(id)) cat = "矿车";

                    bool excluded = cat is "宠物" or "坐骑" or "矿车";
                    bool noDecrease = BuffClock.NoDecrease(id);

                    string name = TShock.Utils.GetBuffName(id);
                    if (string.IsNullOrEmpty(name))
                        name = "未知";

                    // 物品来源：id + 名称 + 各自时长
                    string items = "—";
                    if (sources.TryGetValue(id, out var list) && list.Count > 0)
                    {
                        items = string.Join("<br>", list.Select(it =>
                        {
                            string iname = Lang.GetItemName(it.itemId).Value;
                            if (string.IsNullOrEmpty(iname))
                                iname = "?";
                            string tag = it.ticks > 0 && it.ticks <= 2 ? "（持续）" : $"（{FmtTicks(it.ticks)}）";
                            return $"{it.itemId} {iname}{tag}";
                        }));
                    }

                    // 时长：不递减恒为持续；宠物/坐骑/矿车不共享、不做无意义的估算，直接留空
                    string dur = noDecrease ? "持续（不递减）" : excluded ? "—" : FmtTicks(DurationOf(id));

                    // 备注：如非必要不填——异常状态（黑名单/时长覆盖/不参与共享）才写
                    var notes = new List<string>();
                    if (_config.blacklist.Contains(id))
                        notes.Add("在 blacklist 黑名单");
                    if (BuffClock.RedPotionTicks(id) > 0)
                        notes.Add($"红药水可赋予 {FmtTicks(BuffClock.RedPotionTicks(id))}");
                    if (BuffClock.IsExtendedByPotionStation(id))
                        notes.Add($"持炼金强化(366)时实际 {FmtTicks(BuffClock.ScaleByPotionStation(DurationOf(id)))}");
                    if (id == BuffID.DeadCellsPotionStation)
                        notes.Add("点 tile 699（炼金台）互动获得 1800s，无物品来源");
                    if (_cfgBuffSeconds.TryGetValue(id, out int cfgSec))
                        notes.Add($"配置覆盖 {cfgSec}s");
                    switch (cat)
                    {
                        case "增益":
                            if (!_gainSet.Contains(id))
                                notes.Add("不在白名单，不参与共享");
                            else if (!sources.ContainsKey(id))
                                notes.Add("无物品来源");
                            break;
                        case "减益":
                            if (!_debuffSet.Contains(id))
                                notes.Add("不在白名单，不参与共享");
                            else if (!_config.shareDebuff)
                                notes.Add("需开 shareDebuff 才共享");
                            else if (!sources.ContainsKey(id))
                                notes.Add("无物品来源");
                            break;
                        case "未分类":
                            if (!_gainSet.Contains(id) && !_debuffSet.Contains(id))
                                notes.Add("不在白名单，不参与共享");
                            if (!sources.ContainsKey(id))
                                notes.Add("无物品来源");
                            break;
                    }

                    if (!rows.TryGetValue(cat, out List<string> catRows))
                        rows[cat] = catRows = new List<string>();
                    catRows.Add($"| {id} | {name} | {dur} | {items} | {string.Join("；", notes)} |");
                }

                var sb = new StringBuilder();
                sb.AppendLine("# ShareBuff · buff 时长参考");
                sb.AppendLine();
                sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　共 {ids.Count} 条");
                sb.AppendLine();
                sb.AppendLine("说明：");
                sb.AppendLine("- **时长**为服务器实际采用的时长，优先级：buffs 表配置秒数 > 物品自带时长 > 分类兜底（gainDefaultSeconds / debuffDefaultSeconds）。");
                sb.AppendLine("- **物品**列列出该 buff 的物品来源（id + 名称），同一 buff 多个来源用 `<br>` 分隔；`(持续)` 表示装备/手持类每帧续命，不按此计时。");
                sb.AppendLine("- 红药水（678）走客户端特判，不在物品来源列；其表内 buff 在共享时按红药水实际时长计（含难度放大），见备注列。");
                sb.AppendLine("- 源头持有「炼金强化」(366)（点 tile 699 炼金台互动获得 1800s）时喝经典药水，客户端实际时长是物品时长 ×1.2");
                sb.AppendLine("  （1-18、104-117、121-124、257、343），共享按源头实际最终时长计，见备注列。");
                sb.AppendLine("- 宠物 / 坐骑 / 矿车被 BuffCats 硬排除，不参与共享，仅作参考列出。");

                // 按类别分表输出
                string[] order = ["增益", "减益", "宠物", "坐骑", "矿车", "未分类"];
                foreach (string cat in order)
                {
                    if (!rows.TryGetValue(cat, out List<string> catRows) || catRows.Count == 0)
                        continue;

                    sb.AppendLine();
                    sb.AppendLine($"## {cat}（{catRows.Count} 条）");
                    sb.AppendLine();
                    sb.AppendLine("| buffId | 名称 | 时长 | 物品（id + 名称） | 备注 |");
                    sb.AppendLine("|---|---|---|---|---|");
                    foreach (string line in catRows)
                        sb.AppendLine(line);
                }

                string path = Path.Combine(SaveDir, "buff_time_ref.md");
                Directory.CreateDirectory(SaveDir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                p.SendSuccessMessage($"已生成 buff 时长参考文档：{path}（{ids.Count} 条）");
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[ShareBuff] 生成 buff 参考文档失败：{ex}");
                p.SendErrorMessage("生成参考文档失败，详见服务器日志");
            }
        }

        /// <summary>ticks 转 分/秒 展示</summary>
        static string FmtTicks(int ticks)
        {
            if (ticks <= 0)
                return "0s";
            int sec = ticks / 60;
            if (sec >= 60)
                return $"{sec / 60}分{sec % 60}秒";
            return $"{sec}秒";
        }

        #endregion

        #region 工具

        /// <summary>给全服（可含触发者本人）下发 buff 并登记计时</summary>
        void Apply(TSPlayer src, int buffId, int ticks, bool includeSource)
        {
            // 记录广播起点：之后给无计时的持有者（如 takeOverSource=false 的触发者）反推剩余时长用
            _origin[buffId] = (_tick, ticks);

            foreach (TSPlayer p in TShock.Players)
            {
                if (!IsValid(p))
                    continue;
                if (p.Index == src.Index && !includeSource)
                    continue;

                SetTimer(p.Index, buffId, ticks);

                // 下发时未持有才登记回声：客户端收到后上报 50 包，ScanBuffs 据此不重播。
                // 已持有者（如重复喝药刷新）buffType 不变不会上报，登记了反而误吞他之后真实的新增
                if (!HasBuff(p, buffId))
                    GetSupplied(p.Index).Add(buffId);

                p.SetBuff(buffId, ticks, true);
            }
        }

        /// <summary>
        /// 取 buff 时长（tick）：buffs 表指定 > 药水时长 > 分类兜底（增益 gainDefaultSeconds / 减益 debuffDefaultSeconds）
        /// </summary>
        int DurationOf(int buffId)
        {
            int fallback = _gainSet.Contains(buffId)
                ? _config.gainDefaultSeconds
                : _config.debuffDefaultSeconds;

            _cfgBuffSeconds.TryGetValue(buffId, out int cfgSeconds);

            return BuffClock.GetDuration(buffId, cfgSeconds, fallback);
        }

        /// <summary>
        /// 反推 buff 的全服剩余时长（tick）：优先持有者的服务器计时；
        /// 无计时（如 takeOverSource=false 时的触发者本人）时，按最近一次广播的起点 + 总时长折算。
        /// 返回 0 表示无法确定，由调用方兜底。
        /// </summary>
        int RemainingOf(int buffId, int holderTimer)
        {
            if (holderTimer > 0)
                return holderTimer;

            if (_origin.TryGetValue(buffId, out (long tick, int total) o))
            {
                long left = o.total - (_tick - o.tick);
                if (left > 0)
                    return left > int.MaxValue ? int.MaxValue : (int)left;
            }
            return 0;
        }

        HashSet<int> GetSupplied(int plr)
        {
            if (!_supplied.TryGetValue(plr, out HashSet<int> set))
            {
                set = new HashSet<int>();
                _supplied[plr] = set;
            }
            return set;
        }

        /// <summary>该玩家是否处于红药水判定窗口内（刚喝下红药水）</summary>
        bool InRedPotionWindow(int plr)
        {
            return _redPotionAt.TryGetValue(plr, out long at) && _tick - at <= RedPotionWindow;
        }

        int GetTimer(int plr, int buffId)
        {
            return _timers.TryGetValue(plr, out Dictionary<int, int> map) &&
                   map.TryGetValue(buffId, out int t)
                ? t
                : 0;
        }

        void SetTimer(int plr, int buffId, int ticks)
        {
            if (!_timers.TryGetValue(plr, out Dictionary<int, int> map))
            {
                map = new Dictionary<int, int>();
                _timers[plr] = map;
            }
            map[buffId] = ticks;
        }

        Dictionary<short, (short type, short stack)> GetInv(int plr)
        {
            if (!_inv.TryGetValue(plr, out Dictionary<short, (short, short)> inv))
            {
                inv = new Dictionary<short, (short type, short stack)>();
                _inv[plr] = inv;
            }
            return inv;
        }

        static HashSet<int> CurrentBuffs(TSPlayer plr)
        {
            var set = new HashSet<int>();
            FillBuffs(plr, set);
            return set;
        }

        /// <summary>玩家当前是否持有某 buff（直接扫数组，不分配集合）</summary>
        static bool HasBuff(TSPlayer plr, int buffId)
        {
            int[] buffType = plr.TPlayer.buffType;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                if (buffType[i] == buffId)
                    return true;
            }
            return false;
        }

        /// <summary>debug 开启时输出决策日志到服务器日志</summary>
        void DebugLog(string msg)
        {
            if (_config.debug)
                TShock.Log.Info("[ShareBuff] " + msg);
        }

        /// <summary>
        /// 列表紧凑排版：每行最多 perLine 条；名字宽度 ≥ soloWidth 的条目独占一行；
        /// 整行显示宽度超过 maxWidth 时也提前换行。中文按 2 个显示宽度计，贴近游戏内实际占位。
        /// 条目为 (显示文本, 名字宽度)：名字宽度不含「 剩 Ns」之类的附加信息，
        /// 这样 /sbuff list 里真正的长名字才能被识别出来。
        /// </summary>
        static List<string> FormatBuffGrid(IEnumerable<(string Text, int NameWidth)> entries, int perLine = 5, int maxWidth = 68, int soloWidth = 14)
        {
            var lines = new List<string>();
            var sb = new StringBuilder();
            int count = 0;   // 本行已有条数
            int width = 0;   // 本行显示宽度

            foreach ((string e, int nameWidth) in entries)
            {
                int w = DisplayWidth(e);

                if (nameWidth >= soloWidth) // 名字太长：独占一行
                {
                    if (count > 0)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear();
                        count = 0;
                        width = 0;
                    }
                    lines.Add(e);
                    continue;
                }

                if (count > 0 && (count >= perLine || width + 2 + w > maxWidth))
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                    count = 0;
                    width = 0;
                }

                if (count > 0)
                {
                    sb.Append("  ");
                    width += 2;
                }
                sb.Append(e);
                width += w;
                count++;
            }

            if (count > 0)
                lines.Add(sb.ToString());

            return lines;
        }

        /// <summary>字符串显示宽度：CJK / 全角字符按 2 计，其余按 1 计</summary>
        static int DisplayWidth(string s)
        {
            int w = 0;
            foreach (char c in s)
                w += IsWide(c) ? 2 : 1;
            return w;
        }

        /// <summary>是否全角 / CJK 字符（汉字、假名、全角标点与字母数字等）</summary>
        static bool IsWide(char c)
        {
            return (c >= 0x1100 && c <= 0x115F)
                || (c >= 0x2E80 && c <= 0xA4CF)
                || (c >= 0xAC00 && c <= 0xD7A3)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0xFE30 && c <= 0xFE6F)
                || (c >= 0xFF00 && c <= 0xFF60)
                || (c >= 0xFFE0 && c <= 0xFFE6);
        }

        /// <summary>buff id 转名字，查不到给 ?</summary>
        static string BuffName(int buffId)
        {
            string n = TShock.Utils.GetBuffName(buffId);
            return string.IsNullOrEmpty(n) ? "?" : n;
        }

        static void FillBuffs(TSPlayer plr, HashSet<int> set)
        {
            int[] buffType = plr.TPlayer.buffType;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                int b = buffType[i];
                if (b > 0)
                    set.Add(b);
            }
        }

        bool IsValid(TSPlayer plr)
        {
            if (plr == null || plr.Index < 0 || !plr.Active)
                return false;
            if (plr.TPlayer == null)
                return false;
            // 必须完全进服（State 10）：SSC 的 buff 恢复晚于 AwaitingPlayerInfo(3)，
            // 太早记录首见快照会把进服自带 buff 全部 diff 成新增、触发全服广播
            if (plr.State < 10)
                return false;
            if (_config.exclude.Contains(plr.Name))
                return false;
            return true;
        }

        /// <summary>
        /// 白名单制：只在配置的 gain（或开启 shareDebuff 时的 debuff）列表里才共享。
        /// 另外两道闸门：blacklist，以及 BuffCats 的宠物 / 坐骑 / 矿车硬排除。
        /// </summary>
        bool CanShare(int buffId)
        {
            if (buffId <= 0 || buffId >= BuffID.Count)
                return false;
            if (_config.blacklist.Contains(buffId))
                return false;
            if (BuffCats.Excluded.Contains(buffId))
                return false;
            if (_gainSet.Contains(buffId))
                return true;
            return _config.shareDebuff && _debuffSet.Contains(buffId);
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
                ServerApi.Hooks.NetSendData.Deregister(this, OnSendData);
                GetDataHandlers.PlayerSlot -= new EventHandler<GetDataHandlers.PlayerSlotEventArgs>(OnPlayerSlot);
                GeneralHooks.ReloadEvent -= OnReload;
                Commands.ChatCommands.RemoveAll(c => c.Names.Contains("sharebuff") || c.Names.Contains("sbuff"));
            }
            base.Dispose(disposing);
        }
    }
}
