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
        static readonly Dictionary<int, int> _buffTime = new Dictionary<int, int>();

        // itemId -> (buffType, buffTime)
        static readonly Dictionary<int, (int type, int time)> _itemBuff = new Dictionary<int, (int, int)>();

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
                // 建表失败不影响主流程，退化为配置表 + defaultSeconds；熔断避免每次取值都重试刷日志
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

        /// <summary>取 buff 的名义时长（tick）：配置表 > Item.buffTime > defaultSeconds</summary>
        public static int GetDuration(int buffId, Config cfg)
        {
            Build();

            foreach (BuffData d in cfg.buffs)
            {
                if (d.id == buffId)
                    return SecondsToTicks(d.seconds);
            }

            if (_buffTime.TryGetValue(buffId, out int ticks) && ticks > 0)
                return ticks;

            return SecondsToTicks(cfg.defaultSeconds);
        }

        /// <summary>物品自身携带的 buff 与时长，用于“重复喝药”时精确刷新</summary>
        public static (int buffType, int time) GetItemBuff(int itemId)
        {
            Build();
            return _itemBuff.TryGetValue(itemId, out var v) ? v : (0, 0);
        }

        /// <summary>该 buff 的时间不递减（宠物类），服务器也不该递减它</summary>
        public static bool NoDecrease(int buffId)
        {
            return buffId > 0 && buffId < BuffID.Count && BuffID.Sets.TimeLeftDoesNotDecrease[buffId];
        }

        public static int SecondsToTicks(int seconds)
        {
            long ticks = (long)seconds * 60;
            if (seconds < 0 || ticks > TickLimit)
                return TickLimit;
            return (int)ticks;
        }
    }
}
