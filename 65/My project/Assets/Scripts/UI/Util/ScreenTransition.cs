using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 全域畫面進出轉場（男女選擇↔房間 / 遊戲→房間 / 進出商城 共用）：
    ///   1) 放 <see cref="UiSfx.ScreenFadeOut"/>(Interfaceout) → 漸黑 <see cref="FadeSec"/> 秒；
    ///   2) 全黑：右下顯示 loading 徽章（<see cref="LoadingArt.RandomBadge"/> 的 LOADINGS_*），並在黑幕下執行 <c>swap</c>
    ///      （切畫面／拆遊戲場景／建 3D 房間、商城）——把切換的閃爍/卡頓藏在黑幕之後；
    ///   3) 放 <see cref="UiSfx.ScreenFadeIn"/>(Interfacein) → 觸發 <c>onReveal</c>（房間用它從四邊滑入 UI）→ 漸亮 <see cref="FadeSec"/> 秒露出。
    ///
    /// 轉場全程蓋在最上層的獨立 ScreenSpaceOverlay canvas（DontDestroyOnLoad）上，所以連 gameplay（前端 canvas 被關閉時）
    /// 也蓋得住。徽章錨定在 4:3 內容區（<see cref="AspectController.ContentRect"/>）右下角，pillarbox 模式也不會被黑邊條蓋掉。
    /// </summary>
    public sealed class ScreenTransition : MonoBehaviour
    {
        public const float FadeSec = 0.2f;        // 漸黑 / 漸亮各 0.2 秒
        public const float HoldBlackSec = 0.25f;  // 全黑期間（顯示徽章 + 在黑幕下 swap）
        private const float BadgeMargin = 8f;     // 徽章距內容右下角的邊距（參考 px）

        private static ScreenTransition _inst;
        private Image _black;
        private Image _badge;
        private bool _busy;

        /// <summary>轉場進行中（黑幕未散）。呼叫端可據此避免重入。</summary>
        public static bool Busy => _inst != null && _inst._busy;

        private static ScreenTransition Instance
        {
            get
            {
                if (_inst != null) return _inst;
                // 蓋在最上層：略低於 AspectController 的黑邊條（short.MaxValue）→ 黑邊條仍框住內容，徽章擺在內容區內不會被蓋。
                var canvas = UIKit.CreateCanvas("ScreenTransition", new Vector2(800f, 600f), short.MaxValue - 1);
                DontDestroyOnLoad(canvas.gameObject);
                _inst = canvas.gameObject.AddComponent<ScreenTransition>();
                _inst.BuildOverlay((RectTransform)canvas.transform);
                return _inst;
            }
        }

        private void BuildOverlay(RectTransform root)
        {
            _black = UIKit.AddImage(root, "Black", new Color(0f, 0f, 0f, 0f), raycast: false);
            UIKit.Stretch(_black.rectTransform);   // 蓋滿整個視窗（含黑邊區；反正都是黑）

            _badge = UIKit.AddImage(root, "LoadingBadge", new Color(1f, 1f, 1f, 0f), raycast: false);
            _badge.preserveAspect = true;
            _badge.rectTransform.sizeDelta = new Vector2(LoadingArt.BadgeW, LoadingArt.BadgeH);
            _badge.enabled = false;
        }

        /// <summary>跑一次轉場：漸黑 → 在全黑時執行 <paramref name="swap"/> → 漸亮露出。
        /// <paramref name="onReveal"/> 在漸亮開始前觸發（房間用來從四邊滑入 UI；其餘畫面傳 null）。轉場進行中的重入呼叫會被忽略。</summary>
        public static void Run(Action swap, Action onReveal = null)
        {
            Instance.StartCoroutine(Instance.RunCo(swap, onReveal));
        }

        private IEnumerator RunCo(Action swap, Action onReveal)
        {
            if (_busy) yield break;   // 轉場中不重入（黑幕已擋住輸入；呼叫端也各自守門）
            _busy = true;
            _black.raycastTarget = true;   // 漸黑/全黑/漸亮期間吃掉所有點擊

            // 1) 漸黑（放 Interfaceout）
            UiSfx.Play(UiSfx.ScreenFadeOut);
            yield return Fade(0f, 1f);

            // 2) 全黑：右下 loading 徽章 + 在黑幕下切換（切畫面 / 拆遊戲 / 建 3D）
            UIKit.ApplySprite(_badge, LoadingArt.RandomBadge());
            PlaceBadge();
            _badge.enabled = _badge.sprite != null;
            try { swap?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
            yield return WaitUnscaled(HoldBlackSec);

            // 3) 露出（房間四邊滑入）+ 漸亮（放 Interfacein）
            UiSfx.Play(UiSfx.ScreenFadeIn);
            try { onReveal?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
            _badge.enabled = false;
            yield return Fade(1f, 0f);

            _black.raycastTarget = false;
            _busy = false;
        }

        // 徽章錨定到 4:3 內容區右下角（normalized 螢幕分數 → canvas 錨點），pillarbox 也不會被黑邊條蓋住。
        private void PlaceBadge()
        {
            var cr = AspectController.ContentRect;   // 內容區（Stretch=整個螢幕；Pillarbox=置中 4:3 子區）
            var brt = _badge.rectTransform;
            brt.anchorMin = brt.anchorMax = new Vector2(cr.xMax, cr.yMin);
            brt.pivot = new Vector2(1f, 0f);
            brt.anchoredPosition = new Vector2(-BadgeMargin, BadgeMargin);
        }

        private IEnumerator Fade(float from, float to)
        {
            float t = 0f;
            while (t < FadeSec)
            {
                t += Time.unscaledDeltaTime;   // unscaled：遊戲時鐘/暫停不影響轉場
                _black.color = new Color(0f, 0f, 0f, Mathf.Lerp(from, to, Mathf.Clamp01(t / FadeSec)));
                yield return null;
            }
            _black.color = new Color(0f, 0f, 0f, to);
        }

        private static IEnumerator WaitUnscaled(float sec)
        {
            float t = 0f;
            while (t < sec) { t += Time.unscaledDeltaTime; yield return null; }
        }
    }
}
