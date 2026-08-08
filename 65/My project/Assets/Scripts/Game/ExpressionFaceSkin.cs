using UnityEngine;

namespace Sdo.Game
{
    /// <summary>
    /// 表情 (<c>*_FACE_HUAN.MSH</c>) 的「臉底膚色」正規化 —— 商城、儲物櫃…凡是把表情畫出來的畫面都要套同一份。
    ///
    /// 官方每個表情 mesh 的臉底材質各自綁不同深淺的膚色貼圖 <c>{id}_{g}_face_huan[0-4].dds</c>,不少綁最深的 huan4
    /// (亮度~51,看起來像黑人),另有一批綁到打錯字/根本不存在的檔名 (haun4 / huan_1 / …_face_new_huan0) → 貼圖
    /// resolve 不到,退回平塗色 = 一張沒有五官的素臉。強制改用該 model 的 <c>*_FACE_HUAN0.DDS</c>(同一表情最白的
    /// 膚色變體,亮度~190) → 深膚色與檔名回退兩種一次修好。
    ///
    /// 這段原本只寫在 <c>ShopScreen</c> 裡,所以「商城看都是好的、放到儲物櫃就壞掉」(使用者回報 Devil Neko F
    /// 002012 / 深邃的眸(女) 008354 臉黑的、032585 沒表情)。搬到 Sdo.Game 讓兩個畫面共用同一份修正。
    /// </summary>
    public static class ExpressionFaceSkin
    {
        /// <summary>表情 mesh 的膚色臉底材質貼圖名 = <c>…_face_huan[0-4].dds</c>(含打錯字 haun[0-4]、分隔底線
        /// huan_0)。裝飾疊層(面具/口罩/腮紅)貼圖名 = base <c>…_face_huan.dds</c>(無數字尾) → 回傳 false 讓
        /// <see cref="ForceLightSkin"/> 略過、保留疊層自己的貼圖。非 huan/haun 材質 (W_Basic_ 頸/耳膚色…) 也回傳
        /// false (本來就是膚色,不需再壓白)。Pure → 單元測試。</summary>
        public static bool IsFaceSkinVariant(string matName)
        {
            if (string.IsNullOrEmpty(matName)) return false;
            string n = matName.ToLowerInvariant();
            int i = n.IndexOf("huan", System.StringComparison.Ordinal);
            if (i < 0) i = n.IndexOf("haun", System.StringComparison.Ordinal);   // 打錯字檔名 haun
            if (i < 0) return false;
            int j = i + 4;
            if (j < n.Length && n[j] == '_') j++;   // huan_0 / haun_4 的分隔底線
            return j < n.Length && n[j] >= '0' && n[j] <= '9';   // 有數字尾 = 膚色底;base huan = 裝飾疊層 → 略過
        }

        /// <summary>表情 mesh 的檔名 token(<c>{id6}_{MAN|WOMAN}_FACE_HUAN</c>)。</summary>
        private const string FaceHuanToken = "_FACE_HUAN";

        /// <summary>從一組部位 mesh 路徑裡挑出表情那件並套 <see cref="ForceLightSkin"/> —— 房間/遊戲/頭貼這些
        /// 「只拿得到 mesh 路徑、拿不到 ShopItem」的路徑用這支。沒穿表情就什麼都不做。</summary>
        public static void ApplyToParts(GameObject root, System.Collections.Generic.IEnumerable<string> parts)
        {
            if (root == null || parts == null) return;
            foreach (var rel in parts)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                string stem = System.IO.Path.GetFileNameWithoutExtension(rel);
                if (stem == null || stem.IndexOf(FaceHuanToken, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                ForceLightSkinByStem(root, rel, TrimAfterToken(stem));
            }
        }

        /// <summary>把 <c>032585_WOMAN_FACE_HUAN_</c> / <c>…_FACE_HUAN2</c> 這種尾綴切掉,只留到 token 結束 ——
        /// 貼圖候選是 <c>{stem}0.DDS</c> / <c>{stem}_0.DDS</c> / <c>{stem}.DDS</c>。Pure。</summary>
        public static string TrimAfterToken(string meshStem)
        {
            if (string.IsNullOrEmpty(meshStem)) return meshStem;
            int i = meshStem.IndexOf(FaceHuanToken, System.StringComparison.OrdinalIgnoreCase);
            return i < 0 ? meshStem : meshStem.Substring(0, i + FaceHuanToken.Length);
        }

        /// <summary>把 <paramref name="root"/> 底下表情 mesh 的臉底材質統一換成該 model 最白的膚色 (huan0)。
        /// <paramref name="mshRelPath"/> = 該表情的 MSH 相對路徑(只用來定位它所在的 AVATAR 目錄)。找不到任何
        /// huan 貼圖(231 件中僅 1 件)就原樣不動。</summary>
        public static void ForceLightSkin(GameObject root, string mshRelPath, int modelId, string genderFolder)
            => ForceLightSkinByStem(root, mshRelPath, modelId.ToString("D6") + "_" + genderFolder + FaceHuanToken);

        private static void ForceLightSkinByStem(GameObject root, string mshRelPath, string stem)
        {
            if (root == null || string.IsNullOrEmpty(mshRelPath) || string.IsNullOrEmpty(stem)) return;
            string dir = System.IO.Path.GetDirectoryName(SdoAvatarBuilder.ResolveAvatarFile(mshRelPath));
            Texture2D tex = null;
            foreach (var cand in new[] { stem + "0.DDS", stem + "_0.DDS", stem + ".DDS" })
                if ((tex = SdoAvatarBuilder.ResolveDds(dir, cand)) != null) break;
            if (tex == null) return;
            // 25 個表情 mesh 的 material[0] 貼圖名打錯字 (haun/huan_1…) → LoadParts resolve 失敗、退回 Unlit/Color
            // (無 _MainTex 取樣器) → 我們設的 mainTexture 被忽略 → 臉變平塗「空白」。連 shader 一起改成會取樣貼圖的
            // cutout,臉才顯示出來。
            var faceShader = Shader.Find("Sdo/UnlitDoubleSided") ?? Shader.Find("Unlit/Texture");
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                if (mr.name.IndexOf("FACE_HUAN", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var mats = mr.sharedMaterials;   // 預覽/卡片的 material 都是新建實例 → 直接改安全
                    for (int s = 0; s < mats.Length; s++)
                        // 把「膚色臉底」統一成最白 huan0,但**保留裝飾疊層**(面具/口罩/腮紅 = base …_face_huan)自己的貼圖。
                        // 要壓白的兩種:①膚色底 = 材質名有數字尾 huan[0-4](含深膚 huan4 與打錯字 haun4/huan_1);②貼圖
                        // **解不到**的破損材質(mainTexture==null → LoadParts 退回平塗色 = 白頭) —— 如 030252,mesh 兩個
                        // submesh 都引到磁碟不存在的 base huan、但 huan0-4 都在 → 必須回退到 huan0。要保留的:有解到貼圖
                        // 的裝飾疊層(面具/口罩,base huan DDS 存在)與有效 base 膚色(012882)。否則疊層被膚色蓋掉,疊層幾何
                        // (眼周/口鼻)帶臉底 UV → 帶鬼影五官的素臉(017675 化妝舞會眼罩 / 015353 貓咪口罩)。
                        if (mats[s] != null && (IsFaceSkinVariant(mats[s].name) || mats[s].mainTexture == null))
                        { if (faceShader != null) mats[s].shader = faceShader; mats[s].mainTexture = tex; }
                }
        }
    }
}
