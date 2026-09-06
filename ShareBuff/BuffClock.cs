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

        /// <summary>重载配置时重置，允许重新建表</summary>
        public static void Reset()
        {
            _built = false;
            _failed = false;
            _buffTime.Clear();
            _itemBuff.Clear();
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
        /// 供 /sbuff reference 生成参考文档用；建表失败返回空表。
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

        /// <summary>该 buff 的时间不递减（宠物类），服务器也不该递减它</summary>
        public static bool NoDecrease(int buffId)
        {
            return buffId > 0 && buffId < BuffID.Count && BuffID.Sets.TimeLeftDoesNotDecrease[buffId];
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
