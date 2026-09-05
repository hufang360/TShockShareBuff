using Search.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace ShareBuff
{
    [ApiVersion(2, 1)]
    public class Plugin : TerrariaPlugin
    {

        public override string Description => "";
        public override string Name => "buff全服共享";
        public override string Author => "hufang360";
        public override Version Version => Assembly.GetExecutingAssembly().GetName().Version;


        public Plugin(Main game) : base(game)
        {
        }

        /// <summary>
        /// 初始时
        /// </summary>
        public override void Initialize()
        {
        }

        /// <summary>
        /// 销毁时
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 初始化
        /// </summary>
        void OnInitialize(EventArgs args)
        {
        }

    }
}
