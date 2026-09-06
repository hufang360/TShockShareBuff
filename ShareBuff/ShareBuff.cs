using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        static HashSet<int> _gainSet = new HashSet<int>();
        static HashSet<int> _debuffSet = new HashSet<int>();

        // 玩家 index -> buff id -> 剩余 tick（服务器自计时）
        readonly Dictionary<int, Dictionary<int, int>> _timers = new Dictionary<int, Dictionary<int, int>>();

        // 玩家 index -> 上一帧 buff 集合（用于 diff 出新增项）
        readonly Dictionary<int, HashSet<int>> _snapshots = new Dictionary<int, HashSet<int>>();

        // buff id -> 上次广播时的 tick（防回环）
        readonly Dictionary<int, long> _cooldown = new Dictionary<int, long>();

        // 玩家 index -> 背包槽位 -> (物品 id, 数量)（用于检测喝药）
        readonly Dictionary<int, Dictionary<short, (short type, short stack)>> _inv =
            new Dictionary<int, Dictionary<short, (short type, short stack)>>();

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
                foreach (int buffId in kv.Value.Keys.ToList())
                {
                    if (BuffClock.NoDecrease(buffId))
                        continue;

                    if (kv.Value[buffId]-- > 0)
                        continue;

                    TSPlayer plrObj = TShock.Players[plr];
                    if (IsValid(plrObj) && CurrentBuffs(plrObj).Contains(buffId))
                    {
                        // 客户端还挂着：说明服务器估短了，或玩家重复喝药刷新过。重开一轮。
                        kv.Value[buffId] = DurationOf(buffId);
                    }
                    else
                    {
                        kv.Value.Remove(buffId);
                    }
                }
            }
        }

        /// <summary>扫描全服 buff，diff 出新增项并广播</summary>
        void ScanBuffs()
        {
            foreach (TSPlayer plr in TShock.Players)
            {
                if (!IsValid(plr))
                    continue;

                HashSet<int> cur = CurrentBuffs(plr);

                if (!_snapshots.TryGetValue(plr.Index, out HashSet<int> prev))
                {
                    // 首次见到该玩家：只记快照，不广播（避免进服自带 buff 触发全服）
                    _snapshots[plr.Index] = cur;

                    // trigger 模式的进服补发；mirror 模式由 SyncMirror 持续对齐，无需处理
                    if (_config.giveOnJoin && !_config.IsMirror)
                        GiveOnJoin(plr);
                    continue;
                }

                foreach (int buffId in cur)
                {
                    if (prev.Contains(buffId))
                        continue;
                    OnBuffGained(plr, buffId);
                }

                _snapshots[plr.Index] = cur;
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

                    int t = GetTimer(plr.Index, buffId);
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
                foreach (int buffId in active)
                {
                    if (cur.Contains(buffId))
                        continue;

                    int ticks = remain.TryGetValue(buffId, out int r) && r > 0 ? r : DurationOf(buffId);
                    SetTimer(plr.Index, buffId, ticks);
                    plr.SetBuff(buffId, ticks, true);
                }
            }
        }

        /// <summary>
        /// 进服补发（trigger 模式 + giveOnJoin）：把全服仍在生效的共享 buff 按最大剩余时长补给新玩家。
        /// 同时登记冷却——补发出去的 buff 会让客户端回 50 包，diff 出"新增"，
        /// 不压一下会在冷却过期后引发一轮多余的全服重播。
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

                    int t = GetTimer(p.Index, buffId);
                    if (!remain.TryGetValue(buffId, out int known) || t > known)
                        remain[buffId] = t;
                }
            }

            foreach (var kv in remain)
            {
                int ticks = kv.Value > 0 ? kv.Value : DurationOf(kv.Key);
                SetTimer(target.Index, kv.Key, ticks);
                _cooldown[kv.Key] = _tick;
                target.SetBuff(kv.Key, ticks, true);
            }
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
                return;
            _cooldown[buffId] = _tick;

            // 服务器已经下发过（gbuff / 其他插件 / 上一轮广播）就沿用它的时长，不拿估算值覆盖
            int ticks = GetTimer(src.Index, buffId);
            if (ticks <= 0)
                ticks = DurationOf(buffId);

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

            SetTimer(plr, buffId, ticks);
        }

        /// <summary>
        /// 重复喝药：客户端自己把 buff 刷回满时长，但 50 包只有 buffType、看不到刷新，
        /// 服务器靠背包物品数量减少来感知，把计时同步回来。
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

            // 只认“该槽位原有物品少了 1 个”
            if (old.type <= 0 || e.Stack >= old.stack || old.stack - e.Stack != 1)
                return;
            if (e.Type != 0 && e.Type != old.type)
                return;

            (int buffType, int buffTime) = BuffClock.GetItemBuff(old.type);
            if (buffType <= 0 || !CanShare(buffType))
                return;

            int ticks = buffTime > 0 ? buffTime : DurationOf(buffType);

            // 客户端已自行刷新，服务器只需同步自己的计时
            SetTimer(e.Player.Index, buffType, ticks);

            // 镜像模式下全服一起刷新
            if (_config.IsMirror)
                Apply(e.Player, buffType, ticks, false);
        }

        void OnServerLeave(LeaveEventArgs args)
        {
            _timers.Remove(args.Who);
            _snapshots.Remove(args.Who);
            _inv.Remove(args.Who);
        }

        void OnReload(ReloadEventArgs args)
        {
            Reload();
            args.Player?.SendSuccessMessage("已重载 ShareBuff 配置文件");
        }

        void Reload()
        {
            _config = Config.Load(Path.Combine(SaveDir, "config.json"));
            _gainSet = new HashSet<int>(_config.gain);
            _debuffSet = new HashSet<int>(_config.debuff);
            BuffClock.Reset();
        }

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
                default:
                    p.SendInfoMessage("用法：/sharebuff <on|off|reload|list|clear>（可简写 /sbuff）");
                    p.SendInfoMessage("  on     - 开启共享 buff");
                    p.SendInfoMessage("  off    - 关闭共享 buff");
                    p.SendInfoMessage("  reload - 重载配置文件");
                    p.SendInfoMessage("  list   - 查看正在管理的 buff");
                    p.SendInfoMessage("  clear  - 清除玩家 buff");
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

                string desc = string.Join(", ", kv.Value
                    .Where(x => x.Value > 0)
                    .OrderBy(x => x.Key)
                    .Select(x => $"{TShock.Utils.GetBuffName(x.Key)}({x.Key}) 剩 {x.Value / 60}s"));

                if (desc.Length > 0)
                    lines.Add($"{plr.Name}：{desc}");
            }

            if (lines.Count == 0)
                p.SendInfoMessage("当前没有在管的 buff");
            else
                lines.ForEach(p.SendInfoMessage);
        }

        /// <summary>
        /// /sbuff clear：清除在线玩家的 buff。协议没有移除包，统一发 55 + time=1 让其自然过期，
        /// 快照 diff 只看新增项，不会因此触发重新广播。
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
                foreach (int buffId in CurrentBuffs(plr))
                {
                    if (!all && !IsDebuff(buffId))
                        continue;

                    plr.SetBuff(buffId, 1, true);
                    cleared++;
                }
            }

            p.SendSuccessMessage($"已为 {players} 名在线玩家清除 {(all ? "全部" : "减益")} buff，共 {cleared} 项");
        }

        /// <summary>是否减益：debuff 白名单优先；未分类的 id 按非增益、非宠物/坐骑/矿车兜底</summary>
        bool IsDebuff(int buffId)
        {
            if (_debuffSet.Contains(buffId))
                return true;
            return !_gainSet.Contains(buffId) && !BuffCats.Excluded.Contains(buffId);
        }

        #endregion

        #region 工具

        /// <summary>给全服（可含触发者本人）下发 buff 并登记计时</summary>
        void Apply(TSPlayer src, int buffId, int ticks, bool includeSource)
        {
            foreach (TSPlayer p in TShock.Players)
            {
                if (!IsValid(p))
                    continue;
                if (p.Index == src.Index && !includeSource)
                    continue;

                SetTimer(p.Index, buffId, ticks);
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

            int cfgSeconds = 0;
            foreach (BuffData d in _config.buffs)
            {
                if (d.id == buffId)
                {
                    cfgSeconds = d.seconds;
                    break;
                }
            }

            return BuffClock.GetDuration(buffId, cfgSeconds, fallback);
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
            int[] buffType = plr.TPlayer.buffType;
            for (int i = 0; i < Player.maxBuffs; i++)
            {
                int b = buffType[i];
                if (b > 0)
                    set.Add(b);
            }
            return set;
        }

        bool IsValid(TSPlayer plr)
        {
            if (plr == null || plr.Index < 0 || !plr.Active)
                return false;
            if (plr.TPlayer == null)
                return false;
            if (plr.State < (int)ConnectionState.AwaitingPlayerInfo)
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
