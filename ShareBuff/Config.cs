using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ShareBuff
{
    public class Config
    {
        /// <summary>总开关</summary>
        public bool enabled = true;

        /// <summary>trigger=触发式（各自独立倒计时）；mirror=镜像式（全服持续对齐并集）</summary>
        public string mode = "trigger";

        /// <summary>是否连触发者本人的 buff 时长也统一到服务器值</summary>
        public bool takeOverSource = true;

        /// <summary>时长兜底值（秒）</summary>
        public int defaultSeconds = 3600;

        /// <summary>同一 buff 的最小广播间隔（秒），用于防回环</summary>
        public int cooldownSeconds = 5;

        /// <summary>是否共享减益（Main.debuff）</summary>
        public bool shareDebuff = false;

        /// <summary>检测背包药水消耗，用于处理“重复喝药客户端刷新时长”</summary>
        public bool detectPotionUse = true;

        /// <summary>指定 buff 时长（秒），优先级高于 Item.buffTime</summary>
        public List<BuffData> buffs = new List<BuffData>();

        /// <summary>永不共享的 buff id</summary>
        public List<int> blacklist = new List<int>();

        /// <summary>不参与共享的玩家名</summary>
        public List<string> exclude = new List<string>();

        public bool IsMirror => string.Equals(mode, "mirror", System.StringComparison.OrdinalIgnoreCase);

        public static Config Load(string path)
        {
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) ?? new Config();

            var c = new Config();
            File.WriteAllText(path, JsonConvert.SerializeObject(c, Formatting.Indented));
            return c;
        }
    }

    public class BuffData
    {
        /// <summary>buff id</summary>
        public int id = 0;

        /// <summary>持续时长（秒）</summary>
        public int seconds = 3600;
    }
}
