using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Sdo.Localization;
using Sdo.Game;

namespace Sdo.UI.Util
{
    /// <summary>Procedural UGUI builders (the project has no scene-authored prefabs; everything is code).</summary>
    public static class UIKit
    {
        // ---------- canvas / event system ----------

        public static Canvas CreateCanvas(string name, Vector2 referenceResolution, int sortOrder = 0)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            EnsureEventSystem();
            return canvas;
        }

        /// <summary>
        /// A fixed <paramref name="designSize"/> (e.g. 800×600) WORLD-SPACE canvas framed exactly by a dedicated
        /// orthographic camera that <see cref="AspectController"/> drives to a consistent 4:3 (stretched to fill, or
        /// pillarboxed) — the SAME 4:3 frame as the play screen. World-space (not CanvasScaler) so the UI logical
        /// space is a hard <paramref name="designSize"/> regardless of the real window/screen resolution; the camera
        /// alone does the 4:3 fit. Raycasting works because the canvas event camera = <paramref name="uiCam"/>.
        /// 1 design px = 1 world unit, origin centred (so children lay out in 0..W / 0..H via anchors as usual).
        /// </summary>
        public static Canvas CreateWorldCanvas(string name, Vector2 designSize, out Camera uiCam, int sortOrder = 0)
        {
            var camGo = new GameObject(name + "Cam");
            uiCam = camGo.AddComponent<Camera>();
            uiCam.orthographic = true;
            uiCam.orthographicSize = designSize.y / 2f;          // 300 -> vertical = 600 design units
            uiCam.transform.position = new Vector3(0f, 0f, -10f);
            uiCam.nearClipPlane = 0.1f;
            uiCam.farClipPlane = 100f;
            uiCam.clearFlags = CameraClearFlags.SolidColor;
            uiCam.backgroundColor = Color.black;
            uiCam.cullingMask = ~0;                              // only the front-end is alive while this cam is enabled

            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = uiCam;
            canvas.sortingOrder = sortOrder;
            var rt = (RectTransform)canvas.transform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = designSize;                           // hard 800×600 logical rect (centred at origin)
            rt.position = Vector3.zero;
            rt.localScale = Vector3.one;                         // 1 design px = 1 world unit
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.dynamicPixelsPerUnit = 3f;                    // crisp TMP text at world scale
            EnsureEventSystem();
            AspectController.Register(uiCam);
            return canvas;
        }

        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (Object.FindFirstObjectByType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        // ---------- rect helpers ----------

        public static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt, float l = 0, float b = 0, float r = 0, float t = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
            return rt;
        }

        // convenience: stretch a UGUI graphic (e.g. a TextMeshProUGUI label) by its own RectTransform.
        public static RectTransform Stretch(Graphic g, float l = 0, float b = 0, float r = 0, float t = 0)
            => Stretch(g.rectTransform, l, b, r, t);

        public static RectTransform Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.pivot = pivot; return rt;
        }

        public static RectTransform Size(RectTransform rt, float w, float h) { rt.sizeDelta = new Vector2(w, h); return rt; }
        public static RectTransform Pos(RectTransform rt, float x, float y) { rt.anchoredPosition = new Vector2(x, y); return rt; }

        // ---------- graphics ----------

        public static Image AddImage(Transform parent, string name, Color color, bool raycast = false)
        {
            var rt = NewRect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        // ---------- original-art placement (top-left pixel coords) ----------
        // The SDO dialog XMLs (e.g. RoomDlg/MusicSelDlg.xml) lay elements out in 800×600, top-left
        // origin, y-DOWN. Our world canvas is centred & y-UP, so an element placed at XML (x,y) maps to
        // anchorMin=anchorMax=(0,1), pivot=(0,1), anchoredPosition=(x, -y). Sprites carry their native
        // (crop) pixel size at 1px=1unit, so sizeDelta = sprite.rect.size reproduces the original art size.

        /// <summary>Place a sprite at XML top-left pixel (x,y) at its native size. Tolerates a null sprite
        /// (renders nothing). Returns the Image so callers can keep/swap the sprite later.</summary>
        public static Image AddSprite(Transform parent, string name, Sprite s, float x, float y, bool raycast = false)
        {
            var img = AddImage(parent, name, Color.white, raycast);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            ApplySprite(img, s);
            return img;
        }

        /// <summary>Set an Image's sprite and resize to the sprite's native pixel size; hide if null.</summary>
        public static void ApplySprite(Image img, Sprite s)
        {
            if (img == null) return;
            img.sprite = s;
            if (s != null) { img.color = Color.white; img.rectTransform.sizeDelta = s.rect.size; }
            else img.color = new Color(1f, 1f, 1f, 0f);   // missing art -> invisible, no white box
        }

        /// <summary>A three-state (normal/hover/pushed) sprite button at XML top-left pixel (x,y), sized to
        /// the normal sprite. Uses UGUI SpriteSwap so hover/press reproduce the original art states.</summary>
        public static Button AddSpriteButton(Transform parent, string name, Sprite normal, Sprite hover, Sprite pushed, float x, float y)
        {
            var img = AddSprite(parent, name, normal, x, y, raycast: true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.SpriteSwap;
            var st = btn.spriteState;
            st.highlightedSprite = hover != null ? hover : normal;
            st.pressedSprite = pushed != null ? pushed : (hover != null ? hover : normal);
            st.selectedSprite = normal;
            btn.spriteState = st;
            return btn;
        }

        public static TextMeshProUGUI AddText(Transform parent, string name, string text, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Left, bool wrap = false)
        {
            var rt = NewRect(parent, name);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            var f = UIFont.Cjk; if (f != null) t.font = f;
            t.text = text ?? "";
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            return t;
        }

        public static TextMeshProUGUI AddLocText(Transform parent, string name, string key, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Left, object[] args = null)
        {
            var t = AddText(parent, name, LocalizationManager.Get(key), size, color, align);
            var lt = t.gameObject.AddComponent<LocalizedText>();
            lt.key = key;
            if (args != null) lt.SetArgs(args);
            return t;
        }

        // ---------- buttons ----------

        public static Button AddButton(Transform parent, string name, out TextMeshProUGUI label, Color bg, Color fg, float fontSize = 18)
        {
            var rt = NewRect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = bg;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            label = AddText(rt, "Label", "", fontSize, fg, TextAlignmentOptions.Center);
            Stretch(label, 6, 0, 6, 0);
            return btn;
        }

        public static Button AddLocButton(Transform parent, string name, string key, Color bg, Color fg, float fontSize = 18)
        {
            var btn = AddButton(parent, name, out var label, bg, fg, fontSize);
            label.text = LocalizationManager.Get(key);
            var lt = label.gameObject.AddComponent<LocalizedText>();
            lt.key = key;
            return btn;
        }

        public static void SetInteractable(Button b, bool on)
        {
            if (b == null) return;
            b.interactable = on;
            if (b.targetGraphic is Image img)
            {
                var c = img.color;
                img.color = new Color(c.r, c.g, c.b, on ? 1f : 0.4f);
            }
        }

        // ---------- scroll list ----------

        public static ScrollRect AddVerticalScroll(Transform parent, string name, out RectTransform content,
            float spacing = 4f, int pad = 6, Color? bg = null)
        {
            var rootRt = NewRect(parent, name);
            var rootImg = rootRt.gameObject.AddComponent<Image>();
            rootImg.color = bg ?? new Color(0f, 0f, 0f, 0.22f);
            var sr = rootRt.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 26f;

            var vp = NewRect(rootRt, "Viewport");
            Stretch(vp);
            var vpImg = vp.gameObject.AddComponent<Image>();
            vpImg.color = new Color(1f, 1f, 1f, 0.001f);
            vp.gameObject.AddComponent<RectMask2D>();

            content = NewRect(vp, "Content");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(0f, 0f);
            content.offsetMax = new Vector2(0f, 0f);

            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = spacing;
            vlg.padding = new RectOffset(pad, pad, pad, pad);

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            sr.viewport = vp;
            sr.content = content;
            return sr;
        }

        public static LayoutElement Layout(GameObject go, float preferredHeight)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.flexibleWidth = 1f;
            return le;
        }

        public static void Clear(RectTransform content)
        {
            if (content == null) return;
            for (int i = content.childCount - 1; i >= 0; i--)
                Object.Destroy(content.GetChild(i).gameObject);
        }

        // ---------- input field ----------

        public static TMP_InputField AddInputField(Transform parent, string name, string placeholder, float size = 16)
        {
            var rt = NewRect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.12f);
            var field = rt.gameObject.AddComponent<TMP_InputField>();

            var textArea = NewRect(rt, "TextArea");
            Stretch(textArea, 8, 4, 8, 4);
            textArea.gameObject.AddComponent<RectMask2D>();

            var ph = AddText(textArea, "Placeholder", placeholder, size, UITheme.TextDim);
            Stretch(ph);
            var txt = AddText(textArea, "Text", "", size, UITheme.Text);
            Stretch(txt);

            field.textViewport = textArea;
            field.textComponent = txt;
            field.placeholder = ph;
            if (UIFont.Cjk != null) field.fontAsset = UIFont.Cjk;
            field.targetGraphic = img;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.richText = false;
            return field;
        }

        // ---------- cycler ----------

        public static Cycler AddCycler(Transform parent, string name, string[] options, int start, out RectTransform container)
        {
            var rt = NewRect(parent, name);
            container = rt;
            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.06f);
            var cyc = rt.gameObject.AddComponent<Cycler>();

            var prev = AddButton(rt, "Prev", out var pl, UITheme.Secondary, UITheme.Text, 18);
            pl.text = "◀";
            var prt = prev.GetComponent<RectTransform>();
            Anchor(prt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
            prt.sizeDelta = new Vector2(34f, 0f);
            prt.anchoredPosition = Vector2.zero;

            var next = AddButton(rt, "Next", out var nl, UITheme.Secondary, UITheme.Text, 18);
            nl.text = "▶";
            var nrt = next.GetComponent<RectTransform>();
            Anchor(nrt, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f));
            nrt.sizeDelta = new Vector2(34f, 0f);
            nrt.anchoredPosition = Vector2.zero;

            var label = AddText(rt, "Value", "", 17, UITheme.Text, TextAlignmentOptions.Center);
            Stretch(label, 38, 0, 38, 0);

            cyc.Init(label, options, start);
            prev.onClick.AddListener(cyc.Prev);
            next.onClick.AddListener(cyc.Next);
            return cyc;
        }
    }
}
