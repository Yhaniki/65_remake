using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sdo.Settings
{
    /// <summary>JSON 落地形狀（<c>DATA/PROFILE/profile.json</c>）。欄位名跟角色自己的 profile.json 一致 ——
    /// 同名欄位角色設過就以角色的為準，這裡放的是**所有角色共用的預設值**。</summary>
    [Serializable]
    public class ProfileDefaultsData
    {
        // 寫進檔案裡的說明（JSON 沒有註解語法，只好用一個欄位帶著）。手改時可以整行留著不管。
        public string _readme = "";
        public string activeId = "";
        public string familyName = "";
        public string familyEmblem = "";
        public int level = 0;
    }

    /// <summary>
    /// **外層的 <c>DATA/PROFILE/profile.json</c>** —— 目前登入哪個角色，以及家族/等級的**共用預設值(Default)**。
    /// 以前這些是 config.ini 的 <c>[Profile]</c> 區，開機時一次性搬過來（見 <see cref="Load"/>），之後 config.ini
    /// 只剩 <c>[Room]</c> / <c>[Option]</c>。
    ///
    /// 跟角色資料夾裡的 <c>DATA/PROFILE/&lt;id&gt;/profile.json</c> **同檔名、同欄位名**，這是刻意的：
    ///   * 外面這份 = default（沒設過的角色都吃它）
    ///   * 角色資料夾裡那份 = 那個角色自己的（有設就以自己的為準，見 <see cref="ProfileManager.FamilyName"/> /
    ///     <see cref="ProfileManager.Level"/>）
    /// 角色那份還多一個經驗值欄位（<see cref="UserProfile.exp"/>），每局打完會加進去並自動升等。
    ///
    /// 跟 <see cref="RoomConfig"/> 同一套寫法：static 欄位＝當下生效的值，解析/夾值是純函式（<see cref="ParseInto"/> /
    /// <see cref="Sanitize"/> 不碰檔案）可單元測試。
    /// </summary>
    public static class ProfileDefaults
    {
        /// <summary>檔名跟角色資料夾裡那份一樣（<c>profile.json</c>）—— 差別只在放在 PROFILE 根還是 &lt;id&gt; 子資料夾。</summary>
        public const string FileName = ProfileManager.ProfileFileName;

        private const string Readme =
            "這份是「所有角色共用的預設值」。角色自己的設定在 DATA/PROFILE/<8位數id>/profile.json，" +
            "那裡有同名欄位就以那裡為準（家族留空 / level=0 ＝沒設過 → 吃這裡）。" +
            "activeId＝目前登入的角色資料夾名（留空＝開遊戲自動挑一個）。" +
            "familyEmblem＝DATA/EMBLEM 底下的檔名(不含副檔名)，留空＝只顯示家族名不放徽章。" +
            "level＝等級起始值，0＝不顯示等級；打歌拿到的經驗值進角色自己的 profile.json，到門檻自動升等。";

        // ---- 當下生效的值 ----
        /// <summary>目前登入的本機角色（8 位數資料夾名）。""＝還沒決定 → <see cref="ProfileManager.Boot"/> 會挑一個並寫回。</summary>
        public static string activeId = "";
        /// <summary>家族名稱的預設值（白字描邊，畫在頭上名字那行的上面）。留空＝預設不顯示家族。</summary>
        public static string familyName = "";
        /// <summary>家族徽章的預設值：DATA/EMBLEM 底下的檔名(不含副檔名，如 SMALL43)。留空＝只顯示名稱不放徽章。</summary>
        public static string familyEmblem = "SMALL43";
        /// <summary>等級的預設起始值；0＝沒設定（不顯示等級，獎勵公式當 LV1 算）。角色升過等就看自己的。</summary>
        public static int level = 0;

        /// <summary>檔案完整路徑：<c>DATA/PROFILE/profile.json</c>（＝<see cref="ProfileManager.Root"/> 那層，
        /// 與 config.ini / favorites.json 同層）。</summary>
        public static string FilePath => Path.Combine(ProfileManager.Root ?? "", FileName);

        /// <summary>讀 <c>DATA/PROFILE/profile.json</c>。檔案不存在 → 把 config.ini 舊 <c>[Profile]</c> 區的值
        /// （<see cref="RoomConfig.Load"/> 已經讀進 RoomConfig 的 legacy 欄位）搬過來並落地一份 —— 一次性搬遷，
        /// 之後 config.ini 不再寫出 <c>[Profile]</c>。要在 <see cref="RoomConfig.Load"/> **之後**、
        /// <see cref="ProfileManager.Boot"/> **之前**呼叫（見 <see cref="SettingsBootstrap"/>）。</summary>
        public static void Load()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    ParseInto(File.ReadAllText(path, Encoding.UTF8));
                    Sanitize();
                    return;
                }

                // 還沒有這個檔 → 從 config.ini 的舊 [Profile] 區搬。舊檔沒有那區（全新安裝、或更舊的 active.txt
                // 年代）就維持內建預設，只把 activeId 撿回來 —— 不然會把預設徽章清成空的。
                familyName = ""; familyEmblem = "SMALL43"; level = 0;
                activeId = RoomConfig.legacyActiveId;
                if (RoomConfig.hasLegacyProfileKeys)
                {
                    familyName = RoomConfig.legacyFamilyName;
                    familyEmblem = RoomConfig.legacyFamilyEmblem;
                    level = PlayerLevel.ParseOptional(RoomConfig.legacyPlayerLevel);
                }
                Sanitize();
                Save();
                if (RoomConfig.hasLegacyProfileKeys)
                    Debug.Log($"[Profile] moved config.ini [Profile] -> {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Profile] defaults load failed, using built-ins: {e.Message}");
                Sanitize();
            }
            finally
            {
                ProfileManager.DeleteLegacyActiveFile();   // 舊 active.txt（內容已經在 activeId 裡）
            }
        }

        /// <summary>把目前的值寫回 <c>DATA/PROFILE/profile.json</c>。</summary>
        public static void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogError($"[Profile] defaults save failed: {e.Message}"); }
        }

        /// <summary>把一份 JSON 文字解析進靜態欄位（純函式：不碰檔案）。壞掉的 JSON → 保留原值。</summary>
        public static void ParseInto(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            ProfileDefaultsData d;
            try { d = JsonUtility.FromJson<ProfileDefaultsData>(json); }
            catch (Exception e) { Debug.LogWarning($"[Profile] defaults parse failed: {e.Message}"); return; }
            if (d == null) return;
            activeId = d.activeId;
            familyName = d.familyName;
            familyEmblem = d.familyEmblem;
            level = d.level;
        }

        /// <summary>輸出 JSON 文字（含檔內說明欄位）。純函式。</summary>
        public static string Serialize()
            => JsonUtility.ToJson(new ProfileDefaultsData
            {
                _readme = Readme,
                activeId = activeId ?? "",
                familyName = familyName ?? "",
                familyEmblem = familyEmblem ?? "",
                level = level,
            }, true) + "\n";

        /// <summary>夾正非法值。純函式（家族/名稱只去頭尾空白，前後空白會讓「留空＝沒設過」判定失真）。</summary>
        public static void Sanitize()
        {
            activeId = SanitizeActiveId(activeId);
            familyName = (familyName ?? "").Trim();
            familyEmblem = (familyEmblem ?? "").Trim();
            level = level <= 0 ? 0 : PlayerLevel.Clamp(level);   // 0 = 沒設定（合法）
        }

        /// <summary>只認 8 位數編號（＝ DATA/PROFILE 下的角色資料夾名）；其它一律當「沒設定」。純函式。</summary>
        public static string SanitizeActiveId(string id)
        {
            id = (id ?? "").Trim();
            if (id.Length != 8) return "";
            foreach (var c in id) if (c < '0' || c > '9') return "";
            return id;
        }
    }
}
