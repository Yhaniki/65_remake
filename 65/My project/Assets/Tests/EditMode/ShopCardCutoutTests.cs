using NUnit.Framework;
using Sdo.UI.Screens;

namespace Sdo.Tests
{
    // 商城卡片縮圖 alpha-clip 門檻 (ShopScreen.CardCutoutFor)。每個部位在透空 RT 上都被強制成 cutout shader,
    // 但門檻要依「原本的 shader」分流,否則髮飾的半透明去背底 (a≈0.07~0.25) 被 0.05 留下 → 縮圖露出方框實底
    // (070028 蝴蝶結髮飾「沒去背」的回歸測試)。
    public class ShopCardCutoutTests
    {
        [Test]
        public void Opaque_Garment_NotClipped()
        {
            // Unlit/Texture (含 alpha 壞掉被強制 opaque 的布料):不裁,alpha 逼 1 = 實心,免透明線框。
            Assert.AreEqual(0f, ShopScreen.CardCutoutFor("Unlit/Texture", 0f));
        }

        [Test]
        public void Hair_Keeps_AuthoredCutoff_ClipsAccessoryBackdrop()
        {
            // 髮 (Sdo/UnlitDoubleSided) 保留 authored 0.3 → 去背底 a≈0.07~0.25 被裁掉 (070028 髮飾方框底消失)。
            Assert.AreEqual(0.3f, ShopScreen.CardCutoutFor("Sdo/UnlitDoubleSided", 0.3f));
        }

        [Test]
        public void Hair_MissingCutoff_FallsBackTo_ShaderDefault()
        {
            // 讀不到 authored 值 (0) → 退回 shader 預設 0.3,絕不會退成 0.05 那個會露底的門檻。
            Assert.AreEqual(0.3f, ShopScreen.CardCutoutFor("Sdo/UnlitDoubleSided", 0f));
        }

        [Test]
        public void BlendAccessory_Keeps_HolesOnly_Cutoff()
        {
            // blend = 去背刺青/紗/眼鏡 (Sdo/UnlitAvatarAlpha):0.05 只裁真洞、留半透布料。
            Assert.AreEqual(0.05f, ShopScreen.CardCutoutFor("Sdo/UnlitAvatarAlpha", 0f));
        }

        [Test]
        public void UnknownShader_DefaultsTo_HolesOnly_Cutoff()
        {
            Assert.AreEqual(0.05f, ShopScreen.CardCutoutFor("Some/OtherShader", 0f));
        }
    }
}
