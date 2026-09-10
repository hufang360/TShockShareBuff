using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace ShareBuff
{
    /// <summary>
    /// buff 名义时长来源，以及服务器侧倒计时用的工具方法。
    ///
    /// 协议层拿不到客户端 buff 的剩余时间（50 包只有 buffType 列表），
    /// 所以由服务器接管：检测到新增 buff 时补发一次 55 包把时长揽过来，之后自己递减。
    /// </summary>
    public class BuffClock
    {
        // buffId -> ticks（由 Item.buffTime 汇总而来）
        static readonly Dictionary<int, int> _buffTime = [];

        // itemId -> (buffType, buffTime)
        static readonly Dictionary<int, (int type, int time)> _itemBuff = [];

        static bool _built;
        static bool _failed;

        /// <summary>秒数上限，Terraria 侧 buff 时间上限（415 天）</summary>
        const int TickLimit = int.MaxValue / 60 - 1;

        /// <summary>
        /// 遍历所有物品建立 buff 时长表。
        /// 必须在游戏内容初始化完成之后调用（Initialize 阶段太早，SetDefaults 会抛 NRE），
        /// 所以由 GetDuration / GetItemBuff 懒加载触发，并带失败降级。
        /// </summary>
        public static void Build()
        {
            if (_built || _failed)
                return;

            try
            {
                var probe = new Item();
                for (int i = 0; i < ItemID.Count; i++)
                {
                    probe.SetDefaults(i);
                    if (probe.buffType <= 0)
                        continue;

                    _itemBuff[i] = (probe.buffType, probe.buffTime);

                    if (probe.buffTime <= 0)
                        continue;
                    if (!_buffTime.TryGetValue(probe.buffType, out int known) || probe.buffTime > known)
                        _buffTime[probe.buffType] = probe.buffTime;
                }
                _built = true;
            }
            catch (Exception ex)
            {
                // 建表失败不影响主流程，退化为配置指定值 + 分类兜底值；熔断避免每次取值都重试刷日志
                _failed = true;
                TShockAPI.TShock.Log.Error($"[ShareBuff] 建立 buff 时长表失败，将退化为配置值：{ex.Message}");
            }
        }

        /// <summary>
        /// 时长优先级：配置指定的秒数 > 药水时长（Item.buffTime 建表）> 分类兜底秒数。
        /// 药水时长最贴近玩家实际体验，所以兜底之前优先用它。
        /// </summary>
        public static int GetDuration(int buffId, int cfgSeconds, int fallbackSeconds)
        {
            Build();

            if (cfgSeconds > 0)
                return SecondsToTicks(cfgSeconds);

            if (_buffTime.TryGetValue(buffId, out int ticks) && ticks > 0)
                return ticks;

            return SecondsToTicks(fallbackSeconds);
        }

        /// <summary>物品自身携带的 buff 与时长，用于“重复喝药”时精确刷新</summary>
        public static (int buffType, int time) GetItemBuff(int itemId)
        {
            Build();
            return _itemBuff.TryGetValue(itemId, out var v) ? v : (0, 0);
        }

        /// <summary>
        /// 反向索引：buffType -> 赋予它的物品清单（物品 id + 物品自带的名义时长 ticks）。
        /// 供 /sbuff docs 生成参考文档用；建表失败返回空表。
        /// </summary>
        public static Dictionary<int, List<(int itemId, int ticks)>> GetBuffSources()
        {
            Build();
            var map = new Dictionary<int, List<(int itemId, int ticks)>>();
            if (_failed)
                return map;

            foreach (var kv in _itemBuff)
            {
                (int type, int time) = kv.Value;
                if (!map.TryGetValue(type, out List<(int itemId, int ticks)> list))
                    map[type] = list = new List<(int itemId, int ticks)>();
                list.Add((kv.Key, time));
            }

            foreach (List<(int itemId, int ticks)> list in map.Values)
                list.Sort((a, b) => a.itemId.CompareTo(b.itemId));
            return map;
        }

        #region 红药水（ItemID 678）

        // 红药水的 buffType 为 0，客户端在 Player.ItemCheck 里特判一次性加一组 buff，
        // 物品表查不到时长，只能按客户端逻辑内置（含 buffId 硬编码）：
        // - 普通世界：11 个减益各 216000 ticks（3600s）；其中除窒息(68)外的 10 个，
        //   客户端 AddBuff 会按难度放大（专家 ×2 / 大师 ×2.5）
        // - FTW(getGoodWorld) 世界（含 zenith）：18 选 3 随机增益，各 108000 ticks（1800s）
        //   ——这批增益全都能由其他药水获得（如铁皮/迅捷/再生药水），但时长远短于红药水
        static readonly int[] RedDebuffs = [20, 22, 23, 24, 30, 31, 32, 33, 35, 36, 68];

        static readonly HashSet<int> RedGoodPool =
        [
            1, 2, 3, 5, 6, 7, 8, 9, 16, 17,
            104, 105, 111, 113, 114, 115, 116, 117,
        ];

        /// <summary>
        /// 该 buff 若来自红药水时的时长（ticks），已按难度放大——服务端发 55 包后
        /// 客户端走 AddBuff(fromNetPvP:true) 不会再放大，这里直接给终值。
        /// buffId 不在红药水的 buff 表内返回 0。
        /// </summary>
        public static int RedPotionTicks(int buffId)
        {
            if (Main.getGoodWorld)
                return RedGoodPool.Contains(buffId) ? 108000 : 0;

            if (Array.IndexOf(RedDebuffs, buffId) < 0)
                return 0;

            if (buffId == 68) // 窒息不在客户端的难度放大集合内
                return 216000;

            float mult = Main.masterMode ? 2.5f : Main.expertMode ? 2f : 1f;
            return (int)(216000f * mult);
        }

        #endregion

        /// <summary>
        /// 该 buff 是否属于「炼金强化」(366) 的 1.2 倍延长集（1-18、104-117、121-124、257、343）。
        /// 客户端持有 366（tile 699 炼金台互动获得 1800s）时喝下经典药水，会把时长 ×1.2 再入账；
        /// 服务端发 55 包是终值（fromNetPvP:true 不二次修正），所以源头喝药检测到 366 时必须给放大后的时长。
        /// </summary>
        public static bool IsExtendedByPotionStation(int buffId)
        {
            if (buffId <= 0 || buffId >= BuffID.Count)
                return false;
            bool[] set = BuffID.Sets.BuffTimeIsExtendedByDeadCellsPotionStationBuff;
            return set != null && set[buffId];
        }

        /// <summary>该 buff 的时间不递减（宠物类），服务器也不该递减它</summary>
        public static bool NoDecrease(int buffId)
        {
            return buffId > 0 && buffId < BuffID.Count && BuffID.Sets.TimeLeftDoesNotDecrease[buffId];
        }

        /// <summary>炼金强化 1.2 倍放大后的时长（ticks 已按 6/5 放大，防溢出用 long 算）</summary>
        public static int ScaleByPotionStation(int ticks)
        {
            if (ticks <= 0)
                return ticks;
            long scaled = (long)ticks * 6L / 5L;
            return scaled > TickLimit ? TickLimit : (int)scaled;
        }

        public static int SecondsToTicks(int seconds)
        {
            // 负数视为无效返回 0（SetBuff 会静默丢弃），不能落进上限分支变成 415 天
            if (seconds <= 0)
                return 0;

            long ticks = (long)seconds * 60;
            if (ticks > TickLimit)
                return TickLimit;
            return (int)ticks;
        }
    }
}
