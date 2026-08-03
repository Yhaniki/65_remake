using System;
using System.Collections.Generic;

namespace Sdo.Net.Server
{
    /// <summary>一個 token 對應的身分。</summary>
    public struct AuthIdentity
    {
        /// <summary>這個 token 綁定的 playerId。空 = 不綁(沿用 client 自稱的)。</summary>
        public string PlayerId;

        /// <summary>這個 token 綁定的顯示名稱。空 = 不綁。</summary>
        public string Name;

        /// <summary>是不是管理者(保留給之後的管理指令用;目前只影響日誌)。</summary>
        public bool Admin;

        public bool HasPlayerId => !string.IsNullOrEmpty(PlayerId);
        public bool HasName => !string.IsNullOrEmpty(Name);
    }

    /// <summary>
    /// token 認證(M10 公網化的第一步)。
    ///
    /// 🔴 **這個東西存在的唯一理由:身分要由 server 決定,不能由 client 自稱。**
    /// MVP 階段 <c>hello.playerId</c> 與名字完全是 client 說了算 —— 在 LAN/朋友之間沒差,
    /// 但一開公網就等於「任何人都能冒用任何人」。token 認證把那件事反過來:
    /// **連線帶一個 token,server 查出它是誰**,client 自稱的 playerId 只在 token 沒綁時才採用。
    ///
    /// 為什麼是「一個檔案列出允許的 token」而不是資料庫/OAuth:
    /// 這是重製一個已關服的遊戲,server 是「自己開一台給幾個朋友連」。
    /// 一行一個 token 的檔案可以用任何方式產生與分發(私訊給朋友),而且 server 零依賴、
    /// 重啟即生效。接 OAuth 反而會讓「自己開一台」變不可能(見 docs/architecture/online-services.md)。
    ///
    /// 檔案格式(<c>tokens.txt</c>,UTF-8):
    /// <code>
    ///   # 井號開頭是註解,空行忽略
    ///   3f9a…            ← 只給 token:認證通過,身分沿用 client 自稱的
    ///   3f9a… = 00000001 ← 綁 playerId
    ///   3f9a… = 00000001, 小明          ← 綁 playerId 與顯示名稱
    ///   3f9a… = 00000001, 小明, admin   ← 再加 admin
    /// </code>
    ///
    /// 這個類別是**純的**(吃字串、吐結果),所以可以直接單元測試,不用碰檔案系統或開 socket。
    /// </summary>
    public sealed class AuthTokens
    {
        /// <summary>token 至少要這麼長 —— 太短的等於沒有(暴力猜得出來)。</summary>
        public const int MinTokenLength = 16;

        private readonly Dictionary<string, AuthIdentity> _byToken =
            new Dictionary<string, AuthIdentity>(StringComparer.Ordinal);

        /// <summary>有幾個可用的 token。0 = 沒有啟用 token 認證(見 <see cref="Enabled"/>)。</summary>
        public int Count => _byToken.Count;

        /// <summary>
        /// 有沒有啟用 token 認證。
        ///
        /// 沒有 token 檔(或檔案是空的)→ 停用,行為完全回到 MVP(密碼門檻 + client 自稱身分)。
        /// 這是刻意的:LAN 用的人不該被迫先產生 token 才能玩。
        /// </summary>
        public bool Enabled => _byToken.Count > 0;

        /// <summary>
        /// 解析 token 檔的內容。回傳解析到幾筆;<paramref name="problems"/> 收壞行的說明
        /// (**不含 token 本身** —— 那些訊息會進日誌,而日誌常被貼到 issue 或截圖)。
        /// </summary>
        public int Load(string fileText, List<string> problems = null)
        {
            _byToken.Clear();
            if (string.IsNullOrEmpty(fileText)) return 0;

            var lines = fileText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].Trim();
                if (raw.Length == 0 || raw[0] == '#') continue;

                string token = raw, rest = "";
                int eq = raw.IndexOf('=');
                if (eq >= 0) { token = raw.Substring(0, eq).Trim(); rest = raw.Substring(eq + 1).Trim(); }

                if (token.Length < MinTokenLength)
                {
                    problems?.Add("第 " + (i + 1) + " 行的 token 太短(至少 " + MinTokenLength + " 個字),已忽略");
                    continue;
                }
                if (_byToken.ContainsKey(token))
                {
                    problems?.Add("第 " + (i + 1) + " 行的 token 重複,已忽略");
                    continue;
                }

                var id = new AuthIdentity();
                if (rest.Length > 0)
                {
                    var parts = rest.Split(',');
                    if (parts.Length > 0) id.PlayerId = parts[0].Trim();
                    if (parts.Length > 1) id.Name = parts[1].Trim();
                    for (int k = 2; k < parts.Length; k++)
                        if (string.Equals(parts[k].Trim(), "admin", StringComparison.OrdinalIgnoreCase)) id.Admin = true;
                }
                _byToken[token] = id;
            }
            return _byToken.Count;
        }

        /// <summary>
        /// 驗一個 token。停用時一律回 true(身分留空 = 沿用 client 自稱的)。
        ///
        /// ⚠️ 比對用 <see cref="StringComparer.Ordinal"/> 的字典查找。這裡**不做**常數時間比對:
        /// 字典查找的時間差理論上會洩漏一點資訊,但攻擊者要靠那個時間差猜出一個 16 字以上的 token
        /// 完全不現實,而寫一個自製的常數時間比對反而更容易寫錯。真正的防線是 token 夠長。
        /// </summary>
        public bool TryAuth(string token, out AuthIdentity identity)
        {
            identity = default(AuthIdentity);
            if (!Enabled) return true;                                  // 沒啟用 → 放行
            if (string.IsNullOrEmpty(token)) return false;
            return _byToken.TryGetValue(token, out identity);
        }
    }
}
