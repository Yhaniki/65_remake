using System.IO;
using NUnit.Framework;
using Sdo.Game;
using UnityEngine;

namespace Sdo.Tests
{
    /// <summary>
    /// REAL-DATA guard for「同一根骨被寫了兩份 node」的 MOT。使用者回報:「motion white gumiho F —— 這個翅膀的
    /// motion 方向前後相反了,它原本尾巴的道具,可是它的 motion 變成畫到臉的前面了」。
    ///
    /// 白色九尾狐 = 商城翅膀槽 (cat 8/108) 的 021089 男 / 021090 女 (iteminfo「MOTION White Gumiho M/F 1」),外觀是
    /// 五條狐狸尾巴,走動畫 _G rig (見 <see cref="AvatarWingRig"/>)。它的 <c>021090_WOMAN_CHIBANG.MOT</c> 在檔尾多寫了
    /// 一份 root (Bip01) node:pos (0,37.01,0)、繞 Y 比正確值差 180°,而 <c>_G.HRC</c> 的 root rest 是 (0,33.92,3.97)。
    /// MotLoader 舊行為是「後面的 node 覆寫前面的」→ 整組尾巴轉到身體前面蓋住臉,正是使用者看到的畫面。
    ///
    /// 官方 7,389 支 MOT 掃過只有下面 16 支有重複 node,全是掛件 (翅膀/尾巴/手臂) 的 _G rig。其中 14 支「最後那份」
    /// 剛好等於 rest,所以舊行為看不出問題;判準統一成 <see cref="MotLoader.ResolveDuplicateNodes"/> 的「選對得上
    /// HRC rest 的那份」後,16 支全部都選到正確的一份 —— 這張表就是拿真檔把它釘住。純選擇邏輯測在 MotLoaderTests。
    /// </summary>
    public class WingRigMotDuplicateRealDataTests
    {
        /// <summary>一支有重複 node 的官方 clip:motion 檔、配對的骨架、被寫了兩份的那根骨。</summary>
        public struct Row
        {
            public string Mot, Hrc; public int Bone; public string Why;
            /// <summary>這支的 _G.HRC 跟 .MOT 根本不成對(官方就出錯了),沒有 rest 可對 —— 只要求「選擇不變」。</summary>
            public bool HrcMismatched;
            public override string ToString() => Mot + "#" + Bone;   // NUnit test-case name
        }

        private static readonly Row[] Rows =
        {
            new Row { Mot = "007428_WOMAN_UPARM_G.MOT", Hrc = "007428_WOMAN_UPARM_G.HRC", Bone = 0, Why = "手臂掛件,root 兩份" },
            new Row { Mot = "007429_MAN_UPARM_G.MOT",   Hrc = "007429_MAN_UPARM_G.HRC",   Bone = 0, Why = "手臂掛件,root 兩份" },
            new Row { Mot = "017285_MAN_CHIBANG.MOT",   Hrc = "017285_MAN_CHIBANG_G.HRC", Bone = 1, Why = "chibang 骨兩份" },
            new Row { Mot = "017287_WOMAN_CHIBANG.MOT", Hrc = "017287_WOMAN_CHIBANG_G.HRC", Bone = 1, Why = "chibang 骨兩份(第一份是別處掛點)" },
            new Row { Mot = "017288_WOMAN_CHIBANG.MOT", Hrc = "017288_WOMAN_CHIBANG_G.HRC", Bone = 1, Why = "chibang 骨兩份" },
            new Row { Mot = "017369_WOMAN_CHIBANG.MOT", Hrc = "017369_WOMAN_CHIBANG_G.HRC", Bone = 1, Why = "chibang 骨兩份" },
            new Row { Mot = "017371_WOMAN_CHIBANG.MOT", Hrc = "017371_WOMAN_CHIBANG_G.HRC", Bone = 1, Why = "chibang 骨兩份" },
            new Row { Mot = "017373_MAN_CHIBANG.MOT",   Hrc = "017373_MAN_CHIBANG_G.HRC", Bone = 1, Why = "chibang 骨兩份" },
            new Row { Mot = "017374_MAN_CHIBANG.MOT",   Hrc = "017374_MAN_CHIBANG_G.HRC", Bone = 1, Why = "chibang 骨兩份" },
            new Row { Mot = "021089_MAN_CHIBANG.MOT",   Hrc = "021089_MAN_CHIBANG_G.HRC", Bone = 0, Why = "白色九尾狐 男 —— 最後那份 root 是殘留 (0,37.01,0) 且反 180°" },
            new Row { Mot = "021090_WOMAN_CHIBANG.MOT", Hrc = "021090_WOMAN_CHIBANG_G.HRC", Bone = 0, Why = "白色九尾狐 女 —— 使用者回報的那一件" },
            new Row { Mot = "024422_MAN_CHIBANG.MOT",   Hrc = "024422_MAN_CHIBANG_G.HRC", Bone = 0, Why = "root 兩份(最後那份就是 rest)" },
            // 025924 男版官方就出錯:_G.HRC 只有 7 根、骨名全是 "coat"、位置離譜,跟 20-node 的 .mot 不成對。
            // 沒有 rest 可對(兩份候選離 rest 一樣遠)→ 平手,維持原本的選擇。
            new Row { Mot = "025924_MAN_CHIBANG.MOT",   Hrc = "025924_MAN_CHIBANG_G.HRC", Bone = 0, HrcMismatched = true, Why = "整份 node 複製兩次;骨架與 mot 不成對" },
            new Row { Mot = "025925_WOMAN_CHIBANG.MOT", Hrc = "025925_WOMAN_CHIBANG_G.HRC", Bone = 0, Why = "整份 node 複製兩次(平手)" },
            new Row { Mot = "033238_WOMAN_CHIBANG_G.MOT", Hrc = "033238_WOMAN_CHIBANG_G.HRC", Bone = 2, Why = "Bone01 兩份" },
            new Row { Mot = "033239_MAN_CHIBANG_G.MOT",   Hrc = "033239_MAN_CHIBANG_G.HRC", Bone = 2, Why = "Bone01 兩份" },
        };

        private static System.Collections.Generic.IEnumerable<Row> AllRows() => Rows;

        private static byte[] Read(string rel)
        {
            var path = SdoAvatarBuilder.ResolveAvatarFile(rel);
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : File.ReadAllBytes(path);
        }

        private static void LoadPair(Row row, out HrcLoader hrc, out MotLoader mot)
        {
            var hb = Read("AVATAR/" + row.Hrc);
            var mb = Read("MOTION/" + row.Mot);
            if (hb == null || mb == null) Assert.Ignore("game data not found (data_root.txt) — real-data row skipped");
            hrc = HrcLoader.Load(hb);
            mot = MotLoader.Load(mb);
            Assert.IsNotNull(hrc, row.Hrc + ": parse fail");
            Assert.IsNotNull(mot, row.Mot + ": parse fail");
        }

        private static Vector3 RestPos(HrcLoader hrc, int bone) => hrc.LocalRest[bone].GetColumn(3);

        [Test, TestCaseSource(nameof(AllRows))]
        public void DuplicateNode_ResolvesToTheRestAlignedTrack(Row row)
        {
            LoadPair(row, out var hrc, out var mot);
            Assert.IsTrue(mot.HasDuplicateNodes, row.Mot + ": 這支 clip 應該有重複 node(" + row.Why + ")");
            var before = mot.Bones[row.Bone];
            mot.ResolveDuplicateNodes(hrc);
            Assert.IsTrue(mot.Bones.TryGetValue(row.Bone, out var node), row.Mot + ": bone " + row.Bone + " 沒有軌");
            if (row.HrcMismatched)
            {
                Assert.AreSame(before, node, row.Mot + ": 沒有 rest 可對(骨架不成對)就不該亂換 node(" + row.Why + ")");
                return;
            }
            var picked = MotLoader.SamplePos(node, 0f);
            Assert.AreEqual(0f, Vector3.Distance(picked, RestPos(hrc, row.Bone)), 0.01f,
                $"{row.Mot} bone {row.Bone}: 選到的 node 起點 {picked} 應該對得上骨架 rest {RestPos(hrc, row.Bone)} ({row.Why})");
        }

        /// <summary>白色九尾狐 (使用者回報的那件) —— 釘住「舊行為真的會錯」與「新行為挑對」的差別,免得判準被改回去。</summary>
        [TestCase("021090_WOMAN_CHIBANG.MOT", "021090_WOMAN_CHIBANG_G.HRC")]
        [TestCase("021089_MAN_CHIBANG.MOT",   "021089_MAN_CHIBANG_G.HRC")]
        public void WhiteGumiho_StrayRootNode_IsNotTheOneWeUse(string motRel, string hrcRel)
        {
            var row = new Row { Mot = motRel, Hrc = hrcRel, Bone = 0, Why = "白色九尾狐" };
            LoadPair(row, out var hrc, out var mot);

            // 解析前 = 舊行為(檔案裡最後那份覆寫):殘留的掛點,離 rest 超過 3 個單位、而且繞 Y 差 180°。
            var stray = MotLoader.SamplePos(mot.Bones[0], 0f);
            var rest = RestPos(hrc, 0);
            Assert.Greater(Vector3.Distance(stray, rest), 3f, "殘留 node 應該明顯偏離 rest(它就是尾巴跑到臉前面的原因)");
            MotLoader.SampleRot(mot.Bones[0], 0f, out float sx, out float sy, out float sz, out float sw);

            mot.ResolveDuplicateNodes(hrc);
            var picked = MotLoader.SamplePos(mot.Bones[0], 0f);
            Assert.AreEqual(0f, Vector3.Distance(picked, rest), 0.01f, "選到的應該是對得上 rest 的那份");
            MotLoader.SampleRot(mot.Bones[0], 0f, out float px, out float py, out float pz, out float pw);

            // 兩份 root 的朝向相差 ~180°(q 與 -q 同旋轉,所以用 |dot| 判:同向 →1,差 180° →0)。
            float dot = Mathf.Abs(sx * px + sy * py + sz * pz + sw * pw);
            Assert.Less(dot, 0.1f, "殘留 node 與正確 node 的朝向應該差 180°(使用者:「方向前後相反」)");
        }
    }
}
