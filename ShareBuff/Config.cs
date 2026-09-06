using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        /// <summary>增益的兜底时长（秒）：药水时长查不到、buffs 表也没指定时用它</summary>
        public int gainDefaultSeconds = 300;

        /// <summary>减益的兜底时长（秒）</summary>
        public int debuffDefaultSeconds = 30;

        /// <summary>同一 buff 的最小广播间隔（秒），用于防回环</summary>
        public int cooldownSeconds = 5;

        /// <summary>是否共享减益。开启后 debuff 白名单里的 id 也参与共享</summary>
        public bool shareDebuff = false;

        /// <summary>检测背包药水消耗，用于处理“重复喝药客户端刷新时长”</summary>
        public bool detectPotionUse = true;

        /// <summary>
        /// 进服补发（仅 trigger 模式）：玩家进服时，把全服仍在生效的共享 buff 按剩余时长补给他。
        /// mirror 模式本身就持续对齐并集，无需此开关。
        /// </summary>
        public bool giveOnJoin = false;

        /// <summary>
        /// 逐条指定 buff 时长（秒），优先级最高，高于药水时长。
        /// 想给某个 buff 固定时长就在这里加一条：{ "id": 5, "seconds": 600 }
        /// </summary>
        public List<BuffData> buffs = new List<BuffData>();

        /// <summary>增益白名单：只有这里列出的 id 才会共享（默认 105 条）</summary>
        [JsonConverter(typeof(IntListCompactConverter))]
        public List<int> gain = new List<int>();

        /// <summary>减益白名单：shareDebuff 为 true 时才生效（默认 87 条）</summary>
        [JsonConverter(typeof(IntListCompactConverter))]
        public List<int> debuff = new List<int>();

        /// <summary>额外的永不共享 id。宠物 / 坐骑 / 矿车已由 BuffCats 硬排除，无需在此列出</summary>
        [JsonConverter(typeof(IntListCompactConverter))]
        public List<int> blacklist = new List<int>();

        /// <summary>不参与共享的玩家名</summary>
        public List<string> exclude = new List<string>();

        [JsonIgnore]
        public bool IsMirror => string.Equals(mode, "mirror", System.StringComparison.OrdinalIgnoreCase);

        public static Config Load(string path)
        {
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) ?? new Config();

            // 首次生成：把 wiki 分类表的 Gain / Debuff 填进去作为白名单初值
            var c = new Config();
            c.gain = BuffCats.Gain.OrderBy(x => x).ToList();
            c.debuff = BuffCats.Debuff.OrderBy(x => x).ToList();
            // TShock 禁用玩家 / 非法装备时会下发 Frozen(47) Webbed(69) Stoned(156)，
            // 默认拉黑，免得开了 shareDebuff 后“一个人被禁用、全服跟着被定身”
            // 冰冻, 灵液, 石化
            c.blacklist = new List<int> { 47, 69, 156 };
            File.WriteAllText(path, JsonConvert.SerializeObject(c, Formatting.Indented));
            return c;
        }
    }

    public class BuffData
    {
        /// <summary>buff id</summary>
        public int id = 0;

        /// <summary>持续时长（秒）</summary>
        public int seconds = 60;
    }

    /// <summary>
    /// 把 List&lt;int&gt; 写成一行，避免几百个 id 每人一行把配置文件撑爆。
    /// </summary>
    public class IntListCompactConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(List<int>);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var list = value as List<int>;
            if (list == null || list.Count == 0)
            {
                writer.WriteRawValue("[]");
                return;
            }

            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(list[i]);
            }
            sb.Append(']');
            writer.WriteRawValue(sb.ToString());
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return new List<int>();

            JArray arr = JArray.Load(reader);
            var list = new List<int>();
            foreach (JToken t in arr)
            {
                if (int.TryParse(t.ToString(), out int v))
                    list.Add(v);
            }
            return list;
        }
    }
}
