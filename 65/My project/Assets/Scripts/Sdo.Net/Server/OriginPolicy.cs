using System;
using System.Collections.Generic;

namespace Sdo.Net.Server
{
    /// <summary>
    /// 「這條連線可以進來嗎」—— 來源限制(M10 公網化)。
    ///
    /// 兩件事:
    ///   1. **允許名單**:只讓指定的 IP / 網段連進來。開在公網時這是最有效的一道 ——
    ///      比任何認證都難繞過,因為攻擊者連握手都送不出來。
    ///   2. **每個 IP 的連線數上限**:一個人開一百條連線就能把 <c>maxConnections</c> 佔滿,
    ///      讓其他人完全連不進來(而且不需要通過任何認證就能做到 —— 連線是在 hello 之前就成立的)。
    ///      這一條擋的是那個。
    ///
    /// 純邏輯、不碰 socket → 可以直接單元測試。IP 比對用字串前綴的方式處理網段
    /// (見 <see cref="Allows"/> 的註解:為什麼不做完整的 CIDR)。
    /// </summary>
    public sealed class OriginPolicy
    {
        /// <summary>同一個 IP 最多幾條連線。一份 client 正常會用 2 條(control + file)。</summary>
        public const int DefaultMaxPerIp = 8;

        private readonly List<string> _allow = new List<string>();

        /// <summary>同一個 IP 的連線數上限。&lt;=0 = 不限。</summary>
        public int MaxPerIp = DefaultMaxPerIp;

        /// <summary>有沒有設允許名單。沒設 = 誰都可以連(LAN 的預設行為)。</summary>
        public bool HasAllowList => _allow.Count > 0;

        /// <summary>
        /// 設定允許名單。逗號分隔,每項可以是完整 IP(<c>192.168.0.5</c>)或前綴網段(<c>192.168.0.</c>)。
        /// 空字串 = 清空 = 誰都可以連。
        /// </summary>
        public void SetAllowList(string csv)
        {
            _allow.Clear();
            if (string.IsNullOrWhiteSpace(csv)) return;
            foreach (var raw in csv.Split(','))
            {
                var v = raw.Trim();
                if (v.Length > 0) _allow.Add(v);
            }
        }

        /// <summary>
        /// 這個位址可以連嗎。
        ///
        /// 比對方式:完全相同,或名單項目以 <c>.</c> 結尾時當**前綴**比對(<c>10.0.</c> 收 10.0.*.*)。
        /// 為什麼不做完整的 CIDR(/24 之類):要正確處理 CIDR 就要處理 IPv4/IPv6 的位元遮罩,
        /// 而寫錯的遮罩會安靜地放行不該放行的網段 —— 那比「只支援前綴」危險得多。
        /// 前綴對「只讓我家網段連」這個實際需求已經夠用,而且看得懂、不可能寫錯。
        /// (要更複雜的規則就用 OS 的防火牆 —— 那才是它的工作。)
        /// </summary>
        public bool Allows(string ip)
        {
            if (!HasAllowList) return true;
            if (string.IsNullOrEmpty(ip)) return false;
            for (int i = 0; i < _allow.Count; i++)
            {
                var a = _allow[i];
                if (a.Length == 0) continue;
                if (a[a.Length - 1] == '.')
                {
                    if (ip.StartsWith(a, StringComparison.Ordinal)) return true;
                }
                else if (string.Equals(ip, a, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>這個 IP 現在有 <paramref name="current"/> 條連線,還能再加一條嗎。</summary>
        public bool AllowsAnother(int current) => MaxPerIp <= 0 || current < MaxPerIp;

        /// <summary>
        /// 從 <c>IPEndPoint.ToString()</c> 那種字串取出 IP 部分(去掉 <c>:port</c>)。
        ///
        /// IPv6 的形式是 <c>[::1]:1234</c> —— 直接找最後一個冒號會把 IPv6 位址本身切壞,
        /// 所以要先看有沒有中括號。這種「看起來很簡單所以隨手寫」的解析正是會安靜出錯的地方:
        /// 切錯的話每條 IPv6 連線都會被算成不同的 IP,per-IP 上限就完全失效了。
        /// </summary>
        public static string IpOf(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return "";
            int rb = endpoint.LastIndexOf(']');
            if (endpoint.Length > 0 && endpoint[0] == '[' && rb > 0) return endpoint.Substring(1, rb - 1);
            int c = endpoint.LastIndexOf(':');
            return c > 0 ? endpoint.Substring(0, c) : endpoint;
        }
    }
}
