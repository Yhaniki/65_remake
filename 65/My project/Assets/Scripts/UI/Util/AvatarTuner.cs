using System.Globalization;
using UnityEngine;
using Sdo.Game;
using Sdo.Settings;
using Sdo.UI.Core;

namespace Sdo.UI.Util
{
    /// <summary>
    /// 「畫面上那尊 3D 角色擺哪裡、多大」的**即時調校面板**(一個熱鍵開關的 IMGUI 小視窗,editor 限定)。
    ///
    /// 為什麼要有它:角色的落點與大小是一組照著官方實機截圖量出來的硬幹數字,每調一次都要改常數、等重編、
    /// 再走回那個畫面看,一輪好幾分鐘,而且眼睛得記住上一版長怎樣才比得出來。這裡把那幾個數字接到滑桿上
    /// 當場改,調到滿意再按「複製 const」把該貼回程式碼的那幾行產生出來。
    ///
    /// 誰在用:大廳左側那尊(<c>LobbyScreen.AvatarDebug.cs</c>)、個人資料視窗那尊
    /// (<c>PlayerInfoModal.AvatarDebug.cs</c>)。**兩邊各自一份**(不同的 LocalPrefs key、不同熱鍵、
    /// 不同視窗),互不影響。
    ///
    /// 🔴 **調過的值會存進 LocalPrefs,而且 build 版也照吃**(<see cref="Load"/> 不受
    ///    <see cref="SdoDebugFeatures"/> 管,只有面板本身是 editor 限定)—— 「自己設好的位置」本來就該一直是
    ///    那個位置,否則調完關掉又變回去。要回到程式碼裡那組原始值,按面板上的「重設」(它會把 key 刪掉)。
    ///
    /// 🔴 **縮放一律等比。** <c>GenderPreview3D.SlotW/SlotH</c> 釘死了相機的 aspect,顯示用的 RawImage
    ///    長寬比一旦不等於那個 aspect,角色立刻被拉扁 —— 所以這裡只給一個「縮放」,不給各自可調的 W / H。
    ///
    /// 兩種「變大」的差別(面板上兩條都給):
    ///   • **縮放** = 把渲染好的那張 RT 整張放大縮小 —— 人與留白一起變,絕對不會被裁,調版位用這條。
    ///   • **取景 fillFrac** = 改相機距離,人在那張 RT 裡佔多滿 —— 拉太大時頭腳會被 RT 的邊界切掉,
    ///     但畫質是原生的(縮放到 1.5× 會糊)。要「又大又清楚」就調這條。
    /// </summary>
    public sealed class AvatarTuner
    {
        // ---- 可調範圍(滑桿兩端;鍵盤/輸入框一樣夾在這裡面)----
        public float MinX = -300f, MaxX = 700f;
        public float MinY = -300f, MaxY = 560f;
        public float MinScale = 0.25f, MaxScale = 2.5f;
        public float MinFill = 0.2f, MaxFill = 1.2f;

        private const float WinW = 360f, WinH = 400f;
        private const float RepeatDelay = 0.4f, RepeatRate = 0.04f;

        private readonly string _prefKey, _title, _codePrefix;
        private readonly KeyCode _hotkey;
        private readonly int _winId;
        private readonly float _defX, _defY, _defScale, _defFill;
        private readonly Vector2 _winHome;

        // 🔴 縮放的基準**永遠是 GenderPreview3D 的槽位 400×600**,不是呼叫端現在用的 W/H:
        //    呼叫端把調好的值貼回程式碼之後,它的 W/H 就已經含著上一輪的縮放(例如大廳現在是 419.21×628.81
        //    = 1.048 倍),若拿那個當基準,存在 LocalPrefs 裡的 1.048 會被再乘一次 → 一貼回去人就自己變大。
        //    以槽位為基準時 scale 的意義是絕對的(1.048 永遠是「比原槽位大 4.8%」),貼回去幾次都一樣。
        private const float BaseW = GenderPreview3D.SlotW, BaseH = GenderPreview3D.SlotH;

        private float _x, _y, _scale = 1f, _fill;
        private bool _loaded, _dirty;
        private bool _open, _winPlaced;
        private Rect _win;
        private bool _anchorFeet = true;   // 縮放時固定底邊中心 → 人原地長大,不會往右下角漂走
        private bool _showBox;
        private string _txX, _txY, _txScale, _txFill, _note = "";
        private float _repeatAt;
        private KeyCode _held = KeyCode.None;

        /// <summary>值變了(載入、滑桿、鍵盤、重設都算)—— 呼叫端在這裡把新版位套到 RawImage / 相機上。</summary>
        public System.Action Applied;
        /// <summary>接了才會畫「顯示熱區」那一列 —— 呼叫端自己決定怎麼把命中區顯示出來(通常是染紅)。</summary>
        public System.Action<bool> ShowHitBox;
        /// <summary>「複製 const」時附在後面的額外行(例如大廳那條跟著角色走的拖曳熱區)。</summary>
        public System.Func<string> ExtraCode;

        public float X { get { Load(); return _x; } }
        public float Y { get { Load(); return _y; } }
        public float Scale { get { Load(); return _scale; } }
        public float Fill { get { Load(); return _fill; } }
        public float W { get { Load(); return BaseW * _scale; } }
        public float H { get { Load(); return BaseH * _scale; } }
        public bool WindowOpen => _open;

        /// <param name="prefKey">LocalPrefs 的前綴(例:<c>lobby.avatar</c>)—— 每個畫面一組,不要共用。</param>
        /// <param name="codePrefix">「複製 const」產出的常數名前綴(例:<c>Avatar</c> → <c>AvatarX/Y/W/H</c>)。</param>
        /// <param name="defW">程式碼裡現在那個 W(含上一輪調過的縮放)—— 會換算成相對 400×600 槽位的倍率。
        ///   <paramref name="defH"/> 只用來檢查比例,實際高度一律由槽位比例算(2:3 不准破,破了角色會被拉扁)。</param>
        public AvatarTuner(string prefKey, string title, string codePrefix, KeyCode hotkey, int winId,
                           float defX, float defY, float defW, float defH, float defFill,
                           float winHomeX = 12f, float winHomeY = 12f)
        {
            _prefKey = prefKey; _title = title; _codePrefix = codePrefix;
            _hotkey = hotkey; _winId = winId;
            _defX = defX; _defY = defY; _defFill = defFill;
            _defScale = defW > 0f ? defW / BaseW : 1f;
            if (defH > 0f && Mathf.Abs(defH / defW - BaseH / BaseW) > 0.01f)
                Debug.LogWarning($"[avatar-tuner] {prefKey} 的 W/H 不是 {BaseW}:{BaseH} 的比例"
                                 + $"({defW}×{defH})—— 角色會被拉扁,請照槽位比例給。");
            _winHome = new Vector2(winHomeX, winHomeY);
        }

        // ================================================================ 存 / 讀

        /// <summary>第一次要用到調校值時從 LocalPrefs 讀進來;沒有紀錄就用建構子給的預設(= 完全照程式碼)。</summary>
        public void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _x = Mathf.Clamp(LocalPrefs.GetFloat(_prefKey + ".x", _defX), MinX, MaxX);
            _y = Mathf.Clamp(LocalPrefs.GetFloat(_prefKey + ".y", _defY), MinY, MaxY);
            _scale = Mathf.Clamp(LocalPrefs.GetFloat(_prefKey + ".scale", _defScale), MinScale, MaxScale);
            _fill = Mathf.Clamp(LocalPrefs.GetFloat(_prefKey + ".fill", _defFill), MinFill, MaxFill);
            SyncTexts();
        }

        public void Set(float x, float y, float scale, float fill)
        {
            Load();
            x = Mathf.Clamp(x, MinX, MaxX);
            y = Mathf.Clamp(y, MinY, MaxY);
            scale = Mathf.Clamp(scale, MinScale, MaxScale);
            fill = Mathf.Clamp(fill, MinFill, MaxFill);

            // 縮放時固定底邊中心:人是「站在原地長高」,而不是左上角釘死、整個人往右下角漂 ——
            // 不這樣的話每拖一格縮放就得再補調 X / Y 兩次,滑桿根本沒法用。
            if (_anchorFeet && !Mathf.Approximately(scale, _scale))
            {
                float dw = BaseW * (_scale - scale), dh = BaseH * (_scale - scale);
                x = Mathf.Clamp(x + dw * 0.5f, MinX, MaxX);   // 水平中線不動
                y = Mathf.Clamp(y + dh, MinY, MaxY);          // 底邊不動(y 是左上角座標、向下為正)
            }

            _x = x; _y = y; _scale = scale; _fill = fill;
            LocalPrefs.SetFloat(_prefKey + ".x", _x);
            LocalPrefs.SetFloat(_prefKey + ".y", _y);
            LocalPrefs.SetFloat(_prefKey + ".scale", _scale);
            LocalPrefs.SetFloat(_prefKey + ".fill", _fill);
            _dirty = true;
            SyncTexts();
            Applied?.Invoke();
        }

        /// <summary>回到程式碼裡那組值,並且**把紀錄刪掉** —— 之後常數改了就跟著改,不會被舊紀錄壓著。</summary>
        public void Reset()
        {
            LocalPrefs.DeleteKey(_prefKey + ".x");
            LocalPrefs.DeleteKey(_prefKey + ".y");
            LocalPrefs.DeleteKey(_prefKey + ".scale");
            LocalPrefs.DeleteKey(_prefKey + ".fill");
            _dirty = true;
            _loaded = true;
            _x = _defX; _y = _defY; _scale = _defScale; _fill = _defFill;
            SyncTexts();
            Applied?.Invoke();
        }

        /// <summary>把 LocalPrefs 真的寫進磁碟(平常只改記憶體裡那份,拖滑桿不該每幀存檔)。</summary>
        public void Flush()
        {
            if (!_dirty) return;
            _dirty = false;
            LocalPrefs.Save();
        }

        private void SyncTexts()
        {
            var ic = CultureInfo.InvariantCulture;
            _txX = _x.ToString("0.##", ic);
            _txY = _y.ToString("0.##", ic);
            _txScale = _scale.ToString("0.###", ic);
            _txFill = _fill.ToString("0.###", ic);
        }

        // ================================================================ 熱鍵

        /// <summary>
        /// 每幀在畫面的 Update 裡呼叫。<paramref name="active"/> 是「這個畫面現在收不收鍵」(例如視窗沒開著就傳 false);
        /// <paramref name="allowArrows"/> 在聊天輸入框有焦點時傳 false —— 不然一邊打字一邊就把角色推到畫面外去了
        /// (熱鍵本身不受這條限制,功能鍵不會在輸入框裡產生字元)。
        /// </summary>
        public void HandleKeys(bool active, bool allowArrows = true)
        {
            if (!SdoDebugFeatures.Enabled || !active) { _held = KeyCode.None; return; }

            if (Input.GetKeyDown(_hotkey))
            {
                _open = !_open;
                if (_open) { Load(); SyncTexts(); ShowHitBox?.Invoke(_showBox); }
                else { ShowHitBox?.Invoke(false); Flush(); }
            }
            if (!_open || !allowArrows) { _held = KeyCode.None; return; }

            // 🔴 放掉鍵才清 held —— **不能**寫成 if/else 鏈末端的 else:連發等待期間 Key() 本來就回 false,
            //    那樣會在等待的第一幀就把 held 清掉,結果永遠等不到連發(按住不動只走得動一格)。
            if (_held != KeyCode.None && !Input.GetKey(_held)) _held = KeyCode.None;

            float step = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 10f : 1f;
            if (Key(KeyCode.LeftArrow)) Set(_x - step, _y, _scale, _fill);
            else if (Key(KeyCode.RightArrow)) Set(_x + step, _y, _scale, _fill);
            else if (Key(KeyCode.UpArrow)) Set(_x, _y - step, _scale, _fill);
            else if (Key(KeyCode.DownArrow)) Set(_x, _y + step, _scale, _fill);
            else if (Key(KeyCode.PageUp)) Set(_x, _y, _scale + 0.01f * step, _fill);
            else if (Key(KeyCode.PageDown)) Set(_x, _y, _scale - 0.01f * step, _fill);
        }

        /// <summary>按下即算一次,按住超過 <see cref="RepeatDelay"/> 之後開始連發 —— 微調位置要能「按著推」。</summary>
        private bool Key(KeyCode k)
        {
            if (Input.GetKeyDown(k))
            {
                _held = k;
                _repeatAt = Time.unscaledTime + RepeatDelay;
                return true;
            }
            if (_held != k || !Input.GetKey(k)) return false;
            if (Time.unscaledTime < _repeatAt) return false;
            _repeatAt = Time.unscaledTime + RepeatRate;
            return true;
        }

        /// <summary>關掉面板(畫面被關閉時呼叫:熱區的紅框不能留在畫面上,調校值也該落地)。</summary>
        public void CloseWindow()
        {
            if (_open) { _open = false; ShowHitBox?.Invoke(false); }
            Flush();
        }

        // ================================================================ 面板

        /// <summary>每幀在畫面的 OnGUI 裡呼叫。<paramref name="active"/> 同 <see cref="HandleKeys"/>。</summary>
        public void Draw(bool active)
        {
            if (!SdoDebugFeatures.Enabled || !active || !_open) return;
            if (!_winPlaced) { _win = new Rect(_winHome.x, _winHome.y, WinW, WinH); _winPlaced = true; }
            // 拖到畫面外就再也抓不回來了 → 每幀夾回可見範圍。
            _win.x = Mathf.Clamp(_win.x, -WinW + 60f, Screen.width - 60f);
            _win.y = Mathf.Clamp(_win.y, 0f, Screen.height - 24f);
            _win = GUI.Window(_winId, _win, DrawWindow, _title + "　[" + _hotkey + " 關閉]");
        }

        private void DrawWindow(int id)
        {
            Load();
            GUILayout.Space(2);
            GUILayout.Label($"框:X={_x:0.#}  Y={_y:0.#}  {W:0}×{H:0}(比例鎖住)　取景 {_fill:0.###}");

            float x = _x, y = _y, s = _scale, f = _fill;
            bool changed = false;
            changed |= Row("X 左右", ref _txX, ref x, MinX, MaxX, 1f, 10f, "0.##");
            changed |= Row("Y 上下", ref _txY, ref y, MinY, MaxY, 1f, 10f, "0.##");
            changed |= Row("縮放", ref _txScale, ref s, MinScale, MaxScale, 0.01f, 0.1f, "0.###");
            changed |= Row("取景", ref _txFill, ref f, MinFill, MaxFill, 0.005f, 0.05f, "0.###");
            if (changed) Set(x, y, s, f);

            GUILayout.Space(2);
            _anchorFeet = GUILayout.Toggle(_anchorFeet, " 縮放時固定腳底中心(人原地長大)");
            if (ShowHitBox != null)
            {
                bool box = GUILayout.Toggle(_showBox, " 顯示轉身熱區(紅框,會跟著角色走)");
                if (box != _showBox) { _showBox = box; ShowHitBox(box); }
            }
            GUILayout.Label("縮放 = 整張放大(不會被裁,放很大會糊)　取景 = 改相機距離(清楚,拉太大頭腳會被切)");
            GUILayout.Label("方向鍵移動(Shift ×10)、PageUp/Down 縮放。調好的值會自己記住,build 也吃。");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("重設為程式碼預設")) { Reset(); _note = "已回到程式碼裡那組值"; }
            if (GUILayout.Button("複製 const 到剪貼簿"))
            {
                string code = CodeLines();
                GUIUtility.systemCopyBuffer = code;
                Debug.Log("[avatar-tuner] " + _prefKey + " 目前調校值:\n" + code);
                _note = "已複製(Console 也印了一份)";
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_note)) GUILayout.Label(_note);

            GUI.DragWindow(new Rect(0f, 0f, WinW, 20f));   // 只有標題列可拖(不然滑桿會被拖窗吃掉)
        }

        /// <summary>把現在的值排成可以直接貼回程式碼的那幾行 const。</summary>
        public string CodeLines()
        {
            var ic = CultureInfo.InvariantCulture;
            string s = $"private const float {_codePrefix}X = {_x.ToString("0.##", ic)}f, "
                     + $"{_codePrefix}Y = {_y.ToString("0.##", ic)}f, "
                     + $"{_codePrefix}W = {W.ToString("0.##", ic)}f, "
                     + $"{_codePrefix}H = {H.ToString("0.##", ic)}f;\n"
                     + $"private const float {_codePrefix}FillFrac = {_fill.ToString("0.###", ic)}f;";
            string extra = ExtraCode != null ? ExtraCode() : null;
            return string.IsNullOrEmpty(extra) ? s : s + "\n" + extra;
        }

        /// <summary>
        /// 一行調校控制項:輸入框 + 四顆增減鈕 + 滑桿。回傳「這一幀有沒有被改動」。
        ///
        /// 🔴 輸入框的字**只在滑桿/按鈕改值時**才回寫(<paramref name="text"/> 是使用者正在打的那串)——
        ///    每幀都用格式化值蓋回去的話,打到一半的 "-" 或 "12." 會當場被吃掉,那個輸入框就等於不能用。
        /// </summary>
        private static bool Row(string label, ref string text, ref float value,
                                float min, float max, float step, float big, string fmt)
        {
            var ic = CultureInfo.InvariantCulture;
            float v = value;
            bool widget = false;

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(52f));
            string t = GUILayout.TextField(text ?? "", GUILayout.Width(56f));
            if (!string.Equals(t, text))
            {
                text = t;
                if (float.TryParse(t, NumberStyles.Float, ic, out float typed)) v = typed;
            }
            if (GUILayout.Button("−" + big.ToString(fmt, ic), GUILayout.Width(38f))) { v = value - big; widget = true; }
            if (GUILayout.Button("−" + step.ToString(fmt, ic), GUILayout.Width(38f))) { v = value - step; widget = true; }
            if (GUILayout.Button("+" + step.ToString(fmt, ic), GUILayout.Width(38f))) { v = value + step; widget = true; }
            if (GUILayout.Button("+" + big.ToString(fmt, ic), GUILayout.Width(38f))) { v = value + big; widget = true; }
            GUILayout.EndHorizontal();

            float slid = GUILayout.HorizontalSlider(v, min, max);
            if (Mathf.Abs(slid - v) > 1e-4f) { v = slid; widget = true; }

            v = Mathf.Clamp(v, min, max);
            if (widget) text = v.ToString(fmt, ic);
            if (Mathf.Abs(v - value) <= 1e-4f) return false;
            value = v;
            return true;
        }
    }
}
