using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Sdo.Osu;
using Sdo.Ruleset;

namespace Sdo.Game
{
    // HUD component updates: score/combo digits, HP bar, rank/roster
    public partial class ScreenGameplay
    {
        private void UpdateScoreDigits()
        {
            // (8) commit the real score every 8 beats, then count up old->new + zoom-pop (decompiled BeginAnimate)
            double now = (Time.timeAsDouble - _clockStart) * 1000.0;
            double beatMs = 60000.0 / Math.Max(1.0, _map.Bpm);
            if (_nextScoreCommitMs <= 0) _nextScoreCommitMs = 8 * beatMs;
            if (now >= _nextScoreCommitMs)
            {
                if (_score.Score != _scoreTarget) { _scoreFrom = _shownScore; _scoreTarget = _score.Score; _scoreAnimAt = Time.time; _scoreCommitPop = true; _scoreArmed = true; }
                _nextScoreCommitMs += 8 * beatMs;
                RefreshRanking();   // re-sort + redraw the roster list and rank on the same 8-beat cadence
                // ShowTime: the EnergyScore + EnergyBonus count-up/pop is BATCHED to this settlement (online commits the
                // accumulated per-beat delta once, not per hit). SetTarget here; the rolls Tick every frame in UpdateEnergyBar.
                if (showtimeMode)   // fire the count-up/pop ONLY when the committed value actually changed (no change → no pop)
                {
                    if (_scoreRoll != null && _score.Score != _scoreRollLast) { _scoreRoll.SetTarget(_score.Score, Time.time); _scoreRollLast = _score.Score; }
                    if (_bonusRoll != null && _showtime.Bonus != _bonusRollLast) { _bonusRoll.SetTarget(_showtime.Bonus, Time.time); _bonusRollLast = _showtime.Bonus; }
                }
            }
            // in ShowTime the score is the BIG EnergyScore font (_scoreRoll @300,10) — hide the normal top digits.
            // 旁觀模式也走這條:旁觀者沒有自己的分數,上排的分數位數一律不出(需求 10)。
            if (showtimeMode || spectatorMode)
            {
                if (_scoreDigits != null) foreach (var d in _scoreDigits) if (d) d.enabled = false;
                return;
            }
            // decompiled CtlNumLabel (FUN_0043dac0): NOT a smooth per-frame lerp. It adds a fixed
            // step = delta/20 (0x21c = (target-cur)/0x14) only once every ~50ms (0x31<elapsed, /0x32),
            // then snaps to target at 999ms. => ~20 discrete updates/s, so 個位/十位 不會每幀都在跳(60Hz糊掉).
            double rollMs = (Time.time - _scoreAnimAt) * 1000.0;
            if (rollMs >= 999.0) _shownScore = _scoreTarget;
            else
            {
                long step = (_scoreTarget - _scoreFrom) / 20;   // 0x21c = (target - cur) / 0x14
                long ticks = (long)(rollMs / 50.0);             // one step per ~50ms (0x32) → ~20 ticks over 1s
                _shownScore = _scoreFrom + step * ticks;
            }

            string s = _shownScore.ToString("D8");
            int firstSig = s.Length - 1;               // hidezero: hide leading zeros (keep last)
            for (int k = 0; k < s.Length; k++) if (s[k] != '0') { firstSig = k; break; }
            for (int i = 0; i < _scoreDigits.Length; i++)
            {
                bool show = i >= firstSig && i < s.Length;
                bool newlyVisible = show && !_digitVisible[i];
                // pop a digit only when it FIRST appears (a higher place showing up later in the roll) or on a commit
                // (all visible digits together). NOT on every rolling char change — that would reset it forever.
                if (show && _scoreArmed && (_scoreCommitPop || newlyVisible)) _digitPopAt[i] = Time.time;
                _digitVisible[i] = show;
                var spr = show ? _scoreDigitSprites[s[i] - '0'] : null;
                _scoreDigits[i].enabled = spr != null; _scoreDigits[i].sprite = spr;
                if (spr != null) { PlaceAspect(_scoreDigits[i], _scoreBaseX + i * ScoreDigitPitch + 14, ScorePos.y + 18, 29); _scoreDigits[i].transform.localScale *= DigitBounce(Time.time - _digitPopAt[i]); }
            }
            _scoreCommitPop = false;
        }

        // per-digit pop: slow grow 1.0->1.3 then slow shrink 1.3->1.0, eased, over the WHOLE count-up
        // (~1s, decompiled scale 1.0<->1.3). 必須跟數字滾動同長,否則「還沒跑完就縮小完了」: 放大在中段(0.5s)到頂,
        // 縮回 1.0 剛好落在數字停止滾動(999ms)的同一刻。
        private const float DigitPopDur = 0.999f;             // = roll length (999ms snap), keep in sync
        private static float DigitBounce(float t)
        {
            const float D = DigitPopDur;
            if (t < 0f || t >= D) return 1f;
            float u = t / D;
            float tri = u < 0.5f ? u * 2f : (1f - u) * 2f;    // 0->1->0
            return 1f + 0.3f * Mathf.SmoothStep(0f, 1f, tri);  // ease in/out = 緩慢放大/縮小
        }

        // 判定字 / COMBO 的「打中就彈一下」曲線（decompiled）：命中瞬間放到 PopRest×peak，之後線性收回 PopRest，
        // rate 決定收多快（COMBO 9 → ~111ms，判定字 6 → ~167ms；官方寫死，只有 peak 開放給玩家調）。
        // peak = 峰值 ÷ 靜止大小：官方 2.0，1.0 = 完全不彈跳。純函式（給 EditMode 測）。
        public const float PopRest = 0.8f;   // 兩叢字的靜止尺寸都是原圖 ×0.8 (decompiled)
        public static float PopScale(float age, float rate, float peak)
            => (1f + Mathf.Clamp01(1f - age * rate) * (peak - 1f)) * PopRest;

        private void UpdateComboDigits()
        {
            int combo = _score.Combo;
            if (combo < 2) { foreach (var d in _comboDigits) d.enabled = false; if (_comboWord) _comboWord.enabled = false; _lastComboShown = combo; return; }
            if (combo != _lastComboShown) { _comboPopAt = Time.time; _lastComboShown = combo; }
            float pop = PopScale(Time.time - _comboPopAt, 9f, comboTextPop);
            string s = combo.ToString();
            // The COMBO word + the number are ONE rigid group: scale every element's position AND size about a single
            // shared pivot (TrackCenterX, comboPivotY = the group's centre) by `grow`. Because the word→number gap and
            // the inter-digit gaps all scale by the same `grow` about the same point, the whole thing grows/shrinks as a
            // unit and the spacing never drifts. (Previously the word popped about its own centre ComboWordY and the
            // digits about ComboDigitY — two separate pivots — so glyphs grew while the vertical gap stayed fixed and the
            // rows fought.) comboTextScale (config.ini) rides the SAME factor, so the player's size setting scales the
            // group as one piece too.
            // 向下模式整組上移 _judgeComboYOffset（支點跟著移，pop 動畫的中心才不會偏；判定字套同一個位移，見該欄位）。
            float wordY = ComboWordY + _judgeComboYOffset, digitY = ComboDigitY + _judgeComboYOffset;
            float comboPivotY = (wordY + digitY) / 2f;
            float grow = pop * comboTextScale;
            // comboTextAlpha (config.ini)：COMBO 字樣＋數字整組同一個不透明度（預設 0.6，不擋住下落中的音符）。
            var comboTint = new Color(1f, 1f, 1f, comboTextAlpha);
            float cxTrack = PX(TrackCenterX);   // track centre X shifted by the 面板位置 (左/中)
            float startX = cxTrack - (s.Length - 1) * ComboDigitStep / 2f;   // centred on the track
            for (int i = 0; i < _comboDigits.Count; i++)
            {
                var d = _comboDigits[i];
                if (i >= s.Length) { d.enabled = false; continue; }
                var spr = _comboDigitSprites[s[i] - '0'];
                d.enabled = spr != null; d.sprite = spr;
                if (spr != null)
                {
                    float dx = cxTrack + (startX + i * ComboDigitStep - cxTrack) * grow;
                    float dy = comboPivotY + (digitY - comboPivotY) * grow;
                    PlaceAspect(d, dx, dy, ComboDigitW, -2); d.transform.localScale *= grow; d.color = comboTint;
                }
            }
            if (_comboWord && _comboWord.sprite != null)
            {
                _comboWord.enabled = true;
                float wy = comboPivotY + (wordY - comboPivotY) * grow;
                PlaceAspect(_comboWord, cxTrack, wy, ComboWordW); _comboWord.transform.localScale *= grow; _comboWord.color = comboTint;
            }
        }

        // ==== ranking UI: head nameplate, centre rank N/M, right-side roster list ====

        private void BuildRankingUi()
        {
            // ≈ all-perfect ceiling: every note in one unbroken run → 68 each, then the ×1.04 display scale.
            _finalEst = Math.Max(20000L, (long)_map.TotalNotes * 68L * 26L / 25L);
            var arrowDir = Path.Combine(SdoExtracted.Root, "UI", "ARROW");
            _arrowFrames = new Sprite[9];
            for (int i = 0; i < 9; i++) _arrowFrames[i] = SdoExtracted.LoadImage(arrowDir, i.ToString("D3") + ".PNG");
            var gpDir = SdoExtracted.GameplayUiDir;
            _slashSprite = SdoExtracted.LoadImage(gpDir, "GAMEPLAY61.PNG");   // the "/" glyph (25×29, matches PKSCORE)
            var pkDir = Path.Combine(gpDir, "PKSCORE");
            for (int i = 0; i < _pkDigits.Length; i++) _pkDigits[i] = SdoExtracted.LoadImage(pkDir, i + ".PNG");

            // centre "N / M": two pink PKSCORE digits + the GAMEPLAY61 slash glyph between them.
            _rankCurD = NewSR("RankCur", null, 26); _rankCurD.enabled = false;
            _rankTotD = NewSR("RankTot", null, 26); _rankTotD.enabled = false;
            _rankSlash = NewSR("RankSlash", _slashSprite, 26); _rankSlash.enabled = false;

            // right-side roster list: RosterRows × (name [left] + score [right]), fixed positions on the HUD layer.
            _rosterName = new Label3D[RosterRows];
            _rosterScore = new Label3D[RosterRows];
            for (int row = 0; row < RosterRows; row++)
            {
                float y = rosterFirstY + row * rosterRowStep;
                _rosterName[row] = TextStyles.NewLabel("RosterName" + row, TextStyles.Style.ListOther, 45, rosterFontWorld, TextAnchor.MiddleLeft);
                _rosterName[row].Position = SdoLayout.ToWorld(rosterNameX, y, -3f);
                // Score column: trackEm 0 → numbers stay at natural spacing (數字照原本的不縮). Only the NAME column
                // above keeps the RosterTrackEm tightening (名字縮緊). Both remain right/left anchored to their column.
                _rosterScore[row] = TextStyles.NewLabel("RosterScore" + row, TextStyles.Style.ListOther, 45, rosterFontWorld, TextAnchor.MiddleRight, trackEmOverride: 0f);
                _rosterScore[row].Position = SdoLayout.ToWorld(rosterScoreX, y, -3f);
            }

            // spectators (旁觀玩家): GAMEPLAY18 title + light-blue names。
            // 離線預設關閉(showSpectators=false) → _lookerTitle/_lookerRows 留 null，後續都有 null 防護。
            // 固定配 MaxLookerRows 列(**不是跟著資料長度**)—— 中途有人進來旁觀只要改文字，不用重建整排 label。
            if (showSpectators)
            {
                _lookerTitle = NewSR("LookerTitle", SdoExtracted.LoadImage(gpDir, "GAMEPLAY18.PNG"), 45);
                SdoLayout.PlaceTopLeft(_lookerTitle, lookerTitleX, lookerTitleY, -3f);
                _lookerRows = new Label3D[MaxLookerRows];
                for (int i = 0; i < MaxLookerRows; i++)
                {
                    _lookerRows[i] = TextStyles.NewLabel("Looker" + i, TextStyles.Style.Looker, 45, lookerFontWorld, TextAnchor.MiddleLeft);
                    _lookerRows[i].Position = SdoLayout.ToWorld(lookerX, lookerFirstY + i * lookerRowStep, -3f);
                }
                ApplySpectatorNames();
            }

            // 旁觀模式的左上提示條(官方 GAMEPLAY19.PNG:「Press Ctrl+Q to quit look on mode」)——
            // Ctrl+Q 本來就通了(FrontendApp 的 Hotkey.SpectatorQuit),只是畫面上沒有一個字告訴玩家,
            // 於是旁觀者不知道怎麼離開。只有旁觀者要看到,參賽者的畫面上不該出現。
            if (spectatorMode)
            {
                _spectateHint = NewSR("SpectateHint", SdoExtracted.LoadImage(gpDir, "GAMEPLAY19.PNG"), 45);
                SdoLayout.PlaceTopLeft(_spectateHint, spectateHintX, spectateHintY, -3f);
            }
        }

        /// <summary>
        /// 中途有人進來/離開旁觀 → 即時改名單(需求 10:要真名)。
        /// 列數不變,只改文字與該列要不要出現。名單還沒建(離線)就只記著。
        /// </summary>
        public void SetSpectatorNames(string[] names)
        {
            spectatorNames = names ?? new string[0];
            ApplySpectatorNames();
        }

        private void ApplySpectatorNames()
        {
            if (_lookerRows == null) return;
            int n = spectatorNames != null ? spectatorNames.Length : 0;

            // 一個旁觀者都沒有 → 連「旁觀玩家」的標題也收起來(官方沒有觀眾時那一區是空的)。
            // 這一行是 showSpectators 改成「連線就一律建」的配套:不然沒人旁觀時會留一個空標題。
            if (_lookerTitle != null)
                _lookerTitle.enabled = _lookersOn && n > 0 && _lookerTitle.sprite != null;
            for (int i = 0; i < _lookerRows.Length; i++)
            {
                if (_lookerRows[i] == null) continue;
                bool has = i < n && !string.IsNullOrEmpty(spectatorNames[i]);
                _lookerRows[i].Text = has ? spectatorNames[i] : "";
                // 沒人的那幾列直接關掉。留一個空字串的 label 雖然看不到字,但它會被 SetRankingVisible
                // 一起打開 —— 名單中間出現一個空行看起來像「掉了一個人」。
                _lookerRows[i].SetActive(has && _lookersOn);
            }
        }

        /// <summary>旁觀名單現在該不該出現(跟著 <see cref="SetRankingVisible"/> 的最後一次決定)。</summary>
        private bool _lookersOn = true;

        // re-apply the (live-tunable) roster font/positions + rank size, then redraw. Hooked to the F4 button.
        private void RelayoutRoster()
        {
            if (_rosterName == null) return;
            for (int row = 0; row < RosterRows; row++)
            {
                float y = rosterFirstY + row * rosterRowStep;
                _rosterName[row].PxSize = rosterFontWorld;
                _rosterName[row].Position = SdoLayout.ToWorld(rosterNameX, y, -3f);
                _rosterScore[row].PxSize = rosterFontWorld;
                _rosterScore[row].Position = SdoLayout.ToWorld(rosterScoreX, y, -3f);
            }
            if (_lookerTitle != null) SdoLayout.PlaceTopLeft(_lookerTitle, lookerTitleX, lookerTitleY, -3f);
            if (_lookerRows != null)
                for (int i = 0; i < _lookerRows.Length; i++)
                {
                    _lookerRows[i].PxSize = lookerFontWorld;
                    _lookerRows[i].Position = SdoLayout.ToWorld(lookerX, lookerFirstY + i * lookerRowStep, -3f);
                }
            if (_roster.Count == 0) RebuildRoster();
            UpdateRosterList();
            UpdateRankDisplay();
        }

        // The local dancer's nameplate (animated arrow + name). On the 3D path it is a constant-pixel-size
        // billboard inside SceneCam, so a dancer standing in front can occlude it through the shared depth buffer.
        // The legacy 2D fallback still projects into the orthographic HUD. Only the local player gets the arrow.
        private void CreateHeadMarker(SdoAvatar avatar)
        {
            int headIdx = avatar.BoneIndex("Bip01_Head");
            if (headIdx < 0) headIdx = avatar.BoneIndex("Bip01_Neck");
            Transform anchor = null;
            if (headIdx >= 0 && _avatarRoot != null)
            {
                var ag = new GameObject("HeadMarkerAnchor");
                if (use3dCamera) ag.layer = SceneLayer;
                ag.transform.SetParent(_avatarRoot, false);
                avatar.AddAnchor(headIdx, ag.transform);
                anchor = ag.transform;
            }
            var go = new GameObject("HeadMarker");
            var hm = go.AddComponent<HeadMarker>();
            // TryLoadAvatar runs before TryLoadScene, so SceneCam does not exist yet. Start in the safe HUD
            // mode; TryLoadScene promotes this marker only after the scene camera is successfully constructed.
            hm.Init(_arrowFrames, localPlayerName);
            hm.SetTeamColor(TeamOf(LocalDancerSlotIndex));   // 組隊局:自己的名字也是自己那一隊的顏色
            Transform a = anchor;
            // MMD 顯示開著、而且模型被放大時,畫面上的頭比 SDO 的頭骨高一截 —— 名字要跟著往上讓,
            // 否則就插在放大後的頭裡(見 MmdHeadroom;縮小時不動)。
            hm.AnchorGetter = () => MmdAvatarSwap.RaiseHeadAnchor(avatar,
                a != null ? a.position
                          : ((_avatarRoot != null ? _avatarRoot.position : _danceSpot) + new Vector3(0f, 59f, 0f)));
            hm.CamGetter = () => _sceneCam != null ? _sceneCam : _cam;
            _headMarker = hm;
        }

        // Build (once) a SEPARATE idle avatar (decompiled: each result row has its own AvtShow avatar playing a wait/
        // idle clip — NOT the background dancer), isolated on its own layer far from the stage, and a camera that
        // renders just its head into a RenderTexture for the local row. Returns the RT, or null if unavailable.
        private Texture BuildLocalHeadPortrait()
        {
            if (!resultHeadPortrait) return null;
            if (_headRt != null) { UpdateHeadPortraitCam(); return _headRt; }
            BuildIdleHeadAvatar();
            if (_headAvatar == null) return null;

            // Aspect matches the result row's overflow quad (slot 48 + overflow ~6 → 48/54 ≈ 0.889) so the head isn't
            // stretched: the head essentially FILLS the slot with only a hair-tip poking above, plus a transparent margin so
            // it's never cut. (If headOverflowTop is retuned far from 6, match this RT aspect to avoid vertical stretch.)
            _headRt = new RenderTexture(192, 216, 16, RenderTextureFormat.ARGB32) { name = "HeadPortraitRT" };
            var camGo = new GameObject("HeadPortraitCam");
            _headCam = camGo.AddComponent<Camera>();
            _headCam.orthographic = false;
            _headCam.fieldOfView = headPortraitFov;
            _headCam.nearClipPlane = 0.5f; _headCam.farClipPlane = 500f;
            _headCam.cullingMask = 1 << headPortraitLayer;   // ONLY the isolated idle avatar
            _headCam.clearFlags = CameraClearFlags.SolidColor;
            _headCam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // TRANSPARENT → no black box; the panel/stage shows through
            _headCam.targetTexture = _headRt;
            _headCam.depth = -10;
            UpdateHeadPortraitCam();
            return _headRt;
        }

        // The isolated idle avatar (a second skinned instance, parked far from the stage on headPortraitLayer so only
        // the head cam sees it). DanceEnabled=false → it holds the standby idle (RestMot). Simplified material setup
        // (single texture per submesh) — it's only ever seen as a small head portrait.
        private void BuildIdleHeadAvatar()
        {
            if (_headAvatar != null) return;
            var hrc = LoadAsset(skeletonHrc, b => HrcLoader.Load(b));
            if (hrc == null) return;
            var parent = new GameObject("HeadIdleAvatar");
            parent.transform.position = HeadAvatarSpot;   // far from the stage; isolated for the head cam
            var av = parent.AddComponent<SdoAvatar>();
            av.Setup(hrc, LoadAsset(danceMot, b => MotLoader.Load(b)));
            av.SetBodyShape(SdoBodyShape.WeightFromIndex(bodyShapeIndex, maleBody));
            // 🔴 頭貼一律用**地面**待機,不是 restMot —— 穿飛行翅膀時 restMot 已經被 ConfigureAvatarGender 換成
            // flystay(浮空前傾),那是**舞台**待機用的。結算列其他人的頭貼是地面 idle(RoomHeadPortrait.
            // groundClipsOnly),自己這一列跟著飛就變成「同一排頭像裡只有我歪一邊」,而且別台看到的我還是站姿。
            // 也不照 localPlayerMale 挑:整排(含遠端那幾格)共用同一支,見 ResultRowRestMot —— 一男一女
            // 各挑各的就是「頭貼沒有同步擺動」。定了就不再換,整場結算都是這一支。
            av.RestMot = LoadAsset(ResultRowRestMot, b => MotLoader.Load(b));
            av.DanceEnabled = () => false;     // always hold the standby idle clip
            av.DanceTimeSec = () => -1f;
            // Load the WOMAN body parts, opaque portrait style (shared builder; "h_" prefix keeps the isolated names).
            SdoAvatarBuilder.LoadParts(parent, av, avatarParts, SdoAvatarBuilder.SkinStyle.Portrait, "h_");
            av.PoseInitialIdle();
            SetLayerRecursive(parent, headPortraitLayer);
            _headAvatar = av;
            // MMD 顯示模式下結算頭貼也換成 MMD 模型 (framing: TryHeadBoundsRest below)。
            // 布料不建(cloth: false):它是建一具 rig 最貴的一段,而頭貼一律不付這筆錢(使用者指定)。
            // 代價是 MMD 的頭髮在頭貼裡是剛性跟著頭骨走的 —— 見 RoomHeadPortrait.clothSim 的說明。
            MmdAvatarSwap.Register(av, cloth: false);
            // cache the head bone's REST (bind) model-space position — the cam targets this (NOT the live animated bone),
            // so the camera stays FIXED and the idle head-bob plays out inside the frame instead of being chased.
            Vector3 hp = av.BoneModelPos("Bip01_Head");
            if (hp == Vector3.zero) hp = av.BoneModelPos("Bip01_Neck");
            if (hp != Vector3.zero) _headModelPos = hp;
        }

        // FIXED head cam: targets the head bone's REST position (stable; only moves when the F4 sliders change), sitting a
        // fixed distance in front (world -Z). The avatar is scaled/yawed for the 3/4 angle; its idle bob plays in-frame.
        private void UpdateHeadPortraitCam()
        {
            if (_headAvatar != null)
            {
                var t = _headAvatar.transform;
                t.position = HeadAvatarSpot;
                t.localScale = Vector3.one * Mathf.Max(0.01f, headAvatarScale);
                t.localRotation = Quaternion.Euler(0f, headAvatarYaw, 0f);
            }
            if (_headCam == null || _headAvatar == null) return;

            // MMD display mode: the SDO parts this normally measures are hidden, and the MMD model is one skinned mesh —
            // frame the head the MMD rig sized for itself off the 頭 bone's tail (表示先) — see MmdHeadBounds:
            // 綁在頭骨上的不只有頭(髮皮/角/帽子/髮飾),量幾何的話這一格的頭就比旁邊 SDO 那幾格小一圈。
            // Rest bounds, not live: the cam stays FIXED and the idle head-bob plays in-frame, exactly as the SDO path.
            var mmd = MmdAvatarSwap.ActiveFor(_headAvatar);
            if (mmd != null && mmd.TryHeadBoundsRest(out var mb))
            {
                // MmdAvatar's constants, not the SDO ones: its box is the bare head, the SDO box is head+hair (see there).
                // 🔴 算出來的距離只准存在**區域變數**裡,絕不可寫回 headPortraitDist —— 那個欄位是「模型單位、相對
                //    頭骨」的共用取景值,SyncResultHeadPortraitTuning 每幀把它推給結算列**其他人**那幾格的
                //    boneDistModel(那裡還會再 ×avatarScale)。這裡的值是**世界單位**(框已含 scale),兩種量混用 =
                //    只要自己換上 MMD,旁邊那格 SDO 的頭就大一圈(2026-08-05 使用者回報:結算畫面男生的頭特別大;
                //    實測 MMD 的 20.8 被當成模型單位推過去 → 21.8 世界距離,正確值是 25.17 → 近 15%)。
                MmdAvatar.FramePortrait(mb, headZoom, headAimOffset.x, out Vector3 aim, out float mmdDist);
                Vector3 fwd = Quaternion.Euler(headPitchDeg, 0f, 0f) * Vector3.forward;
                _headCam.fieldOfView = headPortraitFov;
                _headCam.transform.position = aim - fwd * mmdDist;
                _headCam.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                return;
            }

            Vector3 restHead = _headAvatar.transform.TransformPoint(_headModelPos);   // head bone world pos (rest)
            // 取景只看頭骨：距離與瞄準偏移是相對頭骨的固定值（模型單位 × avatar scale），完全不碰任何 renderer 的 bounds。
            // 骨架每套裝扮都同一副 → 髮型/帽子/翅膀/法杖都影響不到取景（穿 Ribbon Star M 翅膀把相機甩飛的那個 bug）。
            float s = Mathf.Max(0.01f, headAvatarScale);
            Vector3 target = restHead + headAimOffset * s;
            float dist = headPortraitDist * s * Mathf.Max(0.05f, headZoom);   // headPortraitDist 預設 = HeadBoneFraming.DistModel（F4 可調）
            _headCam.fieldOfView = headPortraitFov;
            // Frontal (+Z) view tilted DOWN by headPitchDeg, matching the official cam (eye slightly above the head, looking
            // down ~2.3°). Place the cam back along that tilted forward axis and look at the head target.
            Vector3 dir = Quaternion.Euler(headPitchDeg, 0f, 0f) * Vector3.forward;   // +Z, pitched down
            _headCam.transform.position = target - dir * dist;
            _headCam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        // rebuild + redraw the roster (called at each 8-beat score commit and once at startup).
        private void RefreshRanking()
        {
            // 旁觀模式的 _trackVisible 恆 false(音符板不出)—— 但名單一定要更新,那是旁觀者唯一看得到的資訊。
            if (_rosterName == null || (!_trackVisible && !spectatorMode)) return;   // not built / hidden during the opening hold
            // 🔴 自由模式**照樣**出遊戲中的名次(N/M)與右側名單(使用者指定)—— freeMode 只藏「結算列最左的
            //    名次數字」與 YOU WIN/LOSE 旗(見 ShowResult 的 showRank/showBanner)。以前這裡整組關掉,
            //    離線預設就是自由模式(config defaultGameMode=0)→ 一般玩家永遠看不到那兩塊。
            RebuildRoster();
            UpdateRosterList();
            UpdateRankDisplay();
        }

        private void RebuildRoster()
        {
            _roster.Clear();
            // 連線:用 server 推來的真分數,而且**優先於 mockOpponents** —— 真連線時混進假對手的話
            // 名次是假的,結算列還會多出不存在的人。(先取,本機那一列要照它決定畫哪一刻的分數。)
            var netOpp = NetOpponents != null ? NetOpponents() : null;
            // 同分時排第一的是**台上站最前面**那位(領隊格),不是座位序最小的 —— 見 RankingBoard 的
            // tie-break 說明。leader 由 server 推(frames 的 leaderUserId),所以兩台排出同一個人。
            int leaderUid = NetLeaderUserId != null ? NetLeaderUserId() : 0;
            // 🔴 連線時名單的底稿是**這一場的座位表**(netDancers,matchStarting 就到齊了),不是「已經收到
            //    分數的人」。照 frame 建的話別人的名字要等他那台真的開始送分數才出現 —— 使用者回報的
            //    「進去只看得到自己,開場 5~6 秒別人的名字才一個個冒出來」就是它:名單只在每 8 拍結算時
            //    重建(見 RefreshRanking 的呼叫點),慢歌一個 8 拍就是好幾秒。
            //    旁觀者不是參賽者 → 名單裡不能有自己(localSeat < 0)。加了的話會多出一列 0 分的自己,
            //    而且它會跟著參與名次排序(「第 3 名」裡有一個根本沒下場的人)。
            if (netDancers != null && netDancers.Length > 0)
            {
                var seats = new RosterSeat[netDancers.Length];
                for (int i = 0; i < netDancers.Length; i++)
                    seats[i] = new RosterSeat(netDancers[i].UserId, netDancers[i].Name);
                var live = new RosterScore[netOpp != null ? netOpp.Length : 0];
                for (int i = 0; i < live.Length; i++)
                    live[i] = new RosterScore(netOpp[i].UserId, netOpp[i].Name, netOpp[i].Score);
                GameplayRoster.Build(_roster, seats, spectatorMode ? -1 : LocalSeatOrder,
                                     localPlayerName, RosterLocalScore(netOpp), live, leaderUid, RosterRows);
                return;
            }
            if (!spectatorMode)
                _roster.Add(new PlayerEntry(localPlayerName, RosterLocalScore(netOpp), true, LocalSeatOrder,
                                            leaderUid != 0 && leaderUid == LocalNetUserId));
            if (netOpp != null)
            {
                int cap = Math.Min(netOpp.Length, RosterRows - _roster.Count);
                for (int i = 0; i < cap; i++)
                    _roster.Add(new PlayerEntry(netOpp[i].Name ?? "", netOpp[i].Score, false, SeatOrderOf(netOpp[i].UserId),
                                                leaderUid != 0 && leaderUid == netOpp[i].UserId));
                return;
            }
            if (mockOpponents && !freeMode)   // 自由模式 = solo (no opponents)
            {
                double now = _clockStart >= 0 ? (Time.timeAsDouble - _clockStart) * 1000.0 : 0.0;
                double progress = _totalMs > 1.0 ? Math.Min(1.0, Math.Max(0.0, now / _totalMs)) : 0.0;
                int n = Math.Min(OpponentNames.Length, RosterRows - 1);
                for (int i = 0; i < n; i++)
                    _roster.Add(new PlayerEntry(OpponentNames[i], SimOpponentScore(i, progress), false));
            }
        }

        /// <summary>本機的 server userId(離線 / 還沒有對戰名單 = 0)。名單裡要認出「本機是不是領隊」
        /// 只能靠它 —— leaderUserId 是 server 的編號,本機那一列沒有別的地方帶著它。</summary>
        private int LocalNetUserId
        {
            get
            {
                int i = LocalDancerSlotIndex;
                return netDancers != null && i >= 0 && i < netDancers.Length ? netDancers[i].UserId : 0;
            }
        }

        // ---- 名單的「同一時刻」與平手序 ---------------------------------------------------------------------

        /// <summary>本機在座位序裡的位置(離線 = 0)。平手時比這個 —— 與 server 的 (seat, userId) 同序
        /// (<c>netDancers</c> 就是照座位序排好的,見 FrontendApp.FillNetDancers)。</summary>
        private int LocalSeatOrder => LocalDancerSlotIndex;

        /// <summary>某位遠端玩家的座位序;名單裡找不到(中途離開/資料還沒到)= 沒有座位資料。</summary>
        private int SeatOrderOf(int userId)
        {
            if (userId != 0 && netDancers != null)
                for (int i = 0; i < netDancers.Length; i++)
                    if (netDancers[i].UserId == userId) return i;
            return PlayerEntry.NoSeat;
        }

        // 右側名單要畫的是「同一個歌曲時刻」的分數。遠端那幾筆是 server 5Hz 彙整推來的,天生落後約一個
        // 往返;本機若照 TotalScore 直接畫,自己那一列就永遠比別人快一步(使用者回報「自己的分數總是比較快」)。
        // 所以把本機的分數也倒帶到**遠端那幾筆的譜面時間**再畫 —— 整張名單於是都是同一刻的分數。
        // (上方那排大分數仍然是即時的;倒帶只影響右側名單與「第幾名」。)
        private const double RosterSyncCapMs = 2000.0;   // 有人卡住不再送 frame → 名單最多只跟著等這麼久
        private const double ScoreTrailKeepMs = 5000.0;
        private readonly List<(double tMs, long score)> _scoreTrail = new List<(double, long)>();

        private long RosterLocalScore(NetPlayerScore[] netOpp)
        {
            // 曲末定名次(EnterResult)用的是**真**最終分,不能倒帶 —— 那一刻要的是自己打完的成績。
            if (_ended || netOpp == null || netOpp.Length == 0) return TotalScore;
            double asOf = double.MaxValue;
            for (int i = 0; i < netOpp.Length; i++)
                if (netOpp[i].TimeMs > 0.0 && netOpp[i].TimeMs < asOf) asOf = netOpp[i].TimeMs;
            if (asOf == double.MaxValue) return TotalScore;   // 一筆 frame 都還沒收到
            // 🔴 用 NetClockMs(與遠端 tMs 同一把尺,見它的註解),不是 _nowMs(那把尺含本機的音訊偏移設定)。
            return LocalScoreAt(Math.Max(asOf, NetClockMs - RosterSyncCapMs));
        }

        /// <summary>本機分數的短期歷程 —— 只在分數變動時記一筆(整首歌約幾百筆,而且只留最近幾秒)。</summary>
        private void RecordLocalScoreSample(double nowMs)
        {
            long s = TotalScore;
            int n = _scoreTrail.Count;
            if (n > 0 && _scoreTrail[n - 1].score == s) return;
            _scoreTrail.Add((nowMs, s));
            int drop = 0;   // 視窗外只留最後一筆 —— 查詢要靠它回答「那個時刻是多少分」
            while (drop + 1 < _scoreTrail.Count && _scoreTrail[drop + 1].tMs < nowMs - ScoreTrailKeepMs) drop++;
            if (drop > 0) _scoreTrail.RemoveRange(0, drop);
        }

        /// <summary>本機在譜面時間 <paramref name="tMs"/> 當下的分數(比最舊的樣本還早 → 取最舊那筆)。</summary>
        private long LocalScoreAt(double tMs)
        {
            for (int i = _scoreTrail.Count - 1; i >= 0; i--)
                if (_scoreTrail[i].tMs <= tMs) return _scoreTrail[i].score;
            return _scoreTrail.Count > 0 ? _scoreTrail[0].score : TotalScore;
        }

        // deterministic mock score: skill × smoothstep(progress) × (1 ± small oscillation). The oscillation
        // lets opponents trade places over the song so the rank moves; result is clamped ≥ 0.
        private long SimOpponentScore(int i, double progress)
        {
            float skill = 0.72f + 0.11f * ((i * 7 + 3) % 5);                 // ≈ 0.72..1.16 spread
            double curve = progress * progress * (3.0 - 2.0 * progress);     // smoothstep, monotonic 0→1
            double jitter = 0.05 * Math.Sin(i * 1.7 + progress * (6.0 + i)); // ±5% lead changes
            double v = _finalEst * skill * curve * (1.0 + jitter);
            return v < 0 ? 0 : (long)v;
        }

        private void UpdateRosterList()
        {
            var order = RankingBoard.SortedIndices(_roster);
            for (int row = 0; row < RosterRows; row++)
            {
                if (row < order.Length)
                {
                    var p = _roster[order[row]];
                    var (face, edge) = TextStyles.Colors(p.IsLocal ? TextStyles.Style.ListLocal : TextStyles.Style.ListOther);
                    _rosterName[row].SetColors(face, edge); _rosterName[row].SetActive(true); _rosterName[row].Text = p.Name;
                    _rosterScore[row].SetColors(face, edge); _rosterScore[row].SetActive(true); _rosterScore[row].Text = p.Score.ToString();
                }
                else { _rosterName[row].SetActive(false); _rosterScore[row].SetActive(false); }
            }
        }

        private void UpdateRankDisplay()
        {
            // 🔴 旁觀者沒有「我排第幾」。這裡是每 8 拍(每次計分)都會跑的,而它直接寫
            // _rankCurD.enabled —— 所以只在 SetRankingVisible 那邊把旁觀夾掉是不夠的:
            // 下一次計分就把數字重新打開了。而旁觀者不在名單裡 → LocalRank 回 rank 0
            // → 畫面上出現「0 / N」。
            if (spectatorMode) return;

            // 畫面上的名次:同分並列、不跳號(1,1,2)—— 與結算面板的名次牌同一條規則(使用者指定)。
            // 輸贏定格用的是另一條(LocalRank,嚴格順序),兩者刻意不同。
            var (rank, total) = RankingBoard.LocalDisplayRank(_roster);
            rank = Mathf.Clamp(rank, 0, 6);    // PKSCORE digits only go 0..6
            total = Mathf.Clamp(total, 0, 6);
            var cur = _pkDigits[rank]; var tot = _pkDigits[total];
            _rankCurD.sprite = cur; _rankCurD.enabled = cur != null;
            _rankTotD.sprite = tot; _rankTotD.enabled = tot != null;
            // N (current) — slash — M (total), spaced on the score's column pitch (M lands under the tens digit).
            if (cur != null) PlaceAspect(_rankCurD, rankCenterX - rankPitch, rankY, rankDigitW, -2f);
            _rankSlash.enabled = _rankSlash.sprite != null;
            if (_rankSlash.sprite != null) PlaceAspect(_rankSlash, rankCenterX, rankY, rankDigitW, -2f);  // GAMEPLAY61 "/"
            if (tot != null) PlaceAspect(_rankTotD, rankCenterX + rankPitch, rankY, rankDigitW, -2f);
        }

        private void SetRankingVisible(bool on)
        {
            // 自由模式不再在這裡整組關掉 —— 遊戲中的 N/M 與右側名單照出(見 RefreshRanking 的說明)。
            if (_rosterName != null)
                for (int i = 0; i < RosterRows; i++)
                {
                    if (_rosterName[i] != null) _rosterName[i].SetActive(on);
                    if (_rosterScore[i] != null) _rosterScore[i].SetActive(on);
                }
            // 名次「N / M」= **我**排第幾。旁觀者沒有「我」→ 不出(但上面的名單照出,那才是旁觀要看的)。
            bool rankOn = on && !spectatorMode;
            if (_rankCurD) _rankCurD.enabled = rankOn && _rankCurD.sprite != null;
            if (_rankTotD) _rankTotD.enabled = rankOn && _rankTotD.sprite != null;
            if (_rankSlash) _rankSlash.enabled = rankOn;
            _lookersOn = on;
            ApplySpectatorNames();   // 標題與空的那幾列都由它決定(見 ApplySpectatorNames)
        }

        private void UpdateHpBar()
        {
            if (showtimeMode) return;     // ShowTime has no HP bar (only the 集氣 energy gauge) — nothing to drive
            if (!_trackVisible) return;   // hidden during the opening intro; SetTrackVisible(true) re-shows it
            double hp = _health?.Health ?? HealthProcessor.MaxHealth;
            float frac = Mathf.Clamp01((float)((hp - HealthProcessor.FloorHealth) / (HealthProcessor.MaxHealth - HealthProcessor.FloorHealth)));
            if (_emojiState.OnHp(frac)) PlaySe("VOICE_0012");   // 血剩30% → 警告語音 (低血只播語音;GTH emoji 改成累計100miss,見 OnJudge)
            // official MyHp fill clipped to (HP+150)/1150 (no overlay -> uniform red, no banding).
            if (_hpTex) SdoLayout.PlaceBarFill(_hpTex, PX(HpPos.x), HpPos.y + _hpYOffset, HpSize.x, HpSize.y, frac, -0.1f);
            if (_hpGlow && _hpGlowFrames != null && _hpGlowFrames.Length > 0)
            {
                _hpGlowT += Time.deltaTime * 24f;   // HpEft flash (6 frames) — was too slow at 12fps
                _hpGlow.sprite = _hpGlowFrames[((int)_hpGlowT) % _hpGlowFrames.Length];
                // glow is opaque-on-black -> additive. Drive its OWN material's _TintColor by hpGlowBright so it reads
                // as bright as the official (the shared _addMat's stock (.5,.5,.5,.5) tint was halving it -> too dim).
                if (_hpGlowMat != null)
                {
                    float t = 0.5f * hpGlowBright;   // 0.5 = old stock; rgb keeps brightening past 1 (additive, unclamped)
                    if (_hpGlowMat.HasProperty("_TintColor")) _hpGlowMat.SetColor("_TintColor", new Color(t, t, t, Mathf.Clamp01(t)));
                    // Scissor only the LEFT end (world X): the glow must never spill before the bar's left start, but
                    // the RIGHT end stays UNCLIPPED so the full-HP leading-edge flash (which pokes a few px past the
                    // bar's right end) shows bright instead of being chopped off.
                    if (_hpGlowMat.HasProperty("_ClipMinX"))
                    {
                        _hpGlowMat.SetFloat("_ClipMinX", SdoLayout.WorldX(PX(HpPos.x)));
                        _hpGlowMat.SetFloat("_ClipMaxX", 100000f);   // no right clip — let the rightmost flash bleed out
                    }
                    _hpGlow.sharedMaterial = _hpGlowMat;
                }
                // HpEft sits at the HP fill's LEADING EDGE (decompiled HpEft.x = (HP+150)/1150 * barW + base), native
                // 64×32 (no width-squash). Clamp so the glow's right edge never juts PAST the bar's right end.
                // HpEft.png's bright/widest core sits at ~0.78 of its width; hpGlowOffsetX (default -20) lands that core
                // flush ON the fill edge (the old -16 left it ~2px right of the edge -> read as "too far right").
                float edgeX = Mathf.Min(PX(HpPos.x) + HpSize.x * frac, PX(HpPos.x) + HpSize.x);   // fill edge, capped at bar end
                float cx = edgeX + hpGlowOffsetX;
                PlaceAspect(_hpGlow, cx, HpPos.y + HpSize.y / 2f + _hpYOffset, HpEftSize.x, -0.2f);
                _hpGlow.enabled = hp > HealthProcessor.FloorHealth + 1;
            }
        }

        // ShowTime energy meter tick: fill while charging (or drain over the remaining window when active), colour by
        // level, blink the SPACE prompt when releasable, and surface the live BONUS number. Called from UpdateHud only
        // in showtimeMode. See docs/reverse-engineering/SDO_SHOWTIME.md + Sdo.Ruleset.ShowtimeMeter.
        //
        // FILL (official online model, sdo.bin gauge object): an electric strip whose bright HEAD rides the fill tip
        // and whose tail is clipped at the channel start — reproduced by right-anchor-CROPPING the official ENERGY_Y/
        // B/R 85×17 plasma capsule to the fill fraction (head always at the tip, like the official strip slide),
        // stretched ~2.9× onto the 250px channel, frames cycling for the crackle, additive. Never X-squash the whole
        // capsule (smears the streaks) and never stretch the 14×4 MyEnergy chips (the old flat look).
        private void UpdateEnergyBar()
        {
            if (_energyFill == null) return;   // NOTE: not gated on _trackVisible — the bar animates in BEFORE the board reveal
            var m = _showtime;
            float ox = _energyIntroOffX;   // intro slide-in offset (design px); 0 once settled
            if (_energyFrameL) SdoLayout.PlaceTopLeft(_energyFrameL, energyFramePos.x + ox, energyFramePos.y, -0.05f);   // re-placed each frame for the slide
            if (_energyFrameR) SdoLayout.PlaceTopLeft(_energyFrameR, energyFramePos.x + 256f + ox, energyFramePos.y, -0.05f);
            // OFFICIAL fill drive (sdo.bin.c FUN_0040e0f0 value-map + FUN_0040e210 ease + band select). Feed the RAW
            // energy value; ease three per-band POSITIONS; pick the active band by HYSTERESIS (only re-select when the
            // active band's eased position leaves (-305,0]). NO smoothed counter, NO per-frame re-bucket → no 前後跳.
            var caps = m.BandCaps;
            float cap0 = caps[0], cap1 = caps[1], cap2 = caps[Mathf.Min(2, caps.Length - 1)];
            bool intro = _energyIntroFill >= 0f;
            float rawV;
            if (intro) rawV = _energyIntroFill * cap2;                              // demo sweeps 0→cap2 (all 3 bands)
            else if (m.Active)                                                       // window: drain the WHOLE fill through every band
            {
                // OFFICIAL (sdo.bin.c X361755-361815): displayedFill = residual + cap[armed]·remaining/duration. The fill
                // drains from the full pre-release value DOWN THROUGH EVERY BAND to the carried-over residual (red→blue→
                // yellow), NOT just within the released band. FillCount already holds the residual (TryActivate spent
                // cap[L]); adding cap[L]·fraction rebuilds the pre-release total at the window start and lands EXACTLY on
                // FillCount at the window end — so the head reaches empty (or the residual) precisely as the window closes
                // and the m.Active→charging hand-off is continuous (no snap). The old Lerp(caps[L-1],caps[L]) swept only the
                // released band's colour once (looked too slow) and ended at caps[L]≠FillCount (the sudden end-snap).
                int L = Mathf.Clamp(m.ReleasedLevel, 0, 2);
                rawV = m.FillCount + caps[L] * m.WindowRemainingFraction(_nowMs);
            }
            else rawV = m.FillCount;                                                 // charging: the raw counter, straight in
            if (DebugGaugeSweep) rawV = Mathf.PingPong(Time.time * (cap2 / 4f), cap2);   // diagnostic: cycle all 3 bands ~8s
            rawV = Mathf.Clamp(rawV, 0f, cap2);
            float kk = Mathf.Clamp01(Time.deltaTime / 0.5f);                         // ~500ms exponential ease (official +0xc8)
            float range = GaugeFullP - GaugeBaseP;                                   // 305 + gaugeEmptyHideP (empty parked left of the visible edge)
            float[] tgt =
            {
                GaugeBaseP + range * (rawV - 0f) / Mathf.Max(1f, cap0 - 0f),         // band0 target (UNCLAMPED — overshoot drives selection)
                GaugeBaseP + range * (rawV - cap0) / Mathf.Max(1f, cap1 - cap0),     // band1 target
                GaugeBaseP + range * (rawV - cap1) / Mathf.Max(1f, cap2 - cap1),     // band2 target
            };
            for (int i = 0; i < 3; i++) _gaugeCur[i] += (tgt[i] - _gaugeCur[i]) * kk;
            if (m.Active)
                // Window DRAIN: the fill falls monotonically through the bands (red→blue→yellow), so pick the band holding
                // it DIRECTLY (no jitter risk without hysteresis). The scan-lowest-first hysteresis below is built for the
                // CHARGING climb and would skip a colour on the way down (band0 also sits at full, so it wins the scan).
                // On each band swap the head jumps empty→full = the official per-band re-base (FUN_0040e210 strip swap).
                _gaugeActive = rawV >= cap1 ? 2 : rawV >= cap0 ? 1 : 0;
            else if (_gaugeCur[_gaugeActive] < GaugeBaseP || _gaugeCur[_gaugeActive] > GaugeFullP)   // charging: active band left the window → re-select
                for (int i = 0; i < 3; i++)
                    if (GaugeBaseP < _gaugeCur[i] && _gaugeCur[i] <= GaugeFullP) { _gaugeActive = i; break; }
            int level = _gaugeActive;
            float headWorldX = _gaugeCur[_gaugeActive];
            // Once the song is playing (after the 3-stage intro) the head glow stays lit even at 0 fill — user wants it
            // glowing from song start regardless of key presses (_gaugeGlowFromStart). Before that (intro sweep / pre-start)
            // it only draws when there's actual fill so nothing shows parked off-screen during the empty pre-roll.
            bool drawHead = rawV > 0f || _gaugeGlowFromStart;

            // Move the ACTIVE POWER effect to headX inside the dedicated RT camera's isolated world; park the others
            // (and ALL bands while empty) off-frustum. The effects run continuously (never re-init), so the electric
            // ribbon + head glow keep aging.
            if (_gaugeCam != null)
                for (int b = 0; b < 3; b++)
                    if (_gaugeAnchor[b] != null)
                        _gaugeAnchor[b].position = (_energyHudOn && b == level && drawHead)
                            ? GaugeOrigin + new Vector3(headWorldX, 0f, 0f)
                            : GaugeOrigin + new Vector3(-10000f, 0f, 0f);

            int animFrame = (int)(Time.time * showtimeAnimFps);   // ~10fps UI-sprite tick (space prompt blink)
            int glowFrame = (int)(Time.time * energyGlowFps);     // FAST tick for the electric glow (official crackle)
            // band-up 500ms mini flash (official EnergyProgress widget @279,15: value = elapsed ms 0..500). During the
            // flash the badge cluster hides; when it completes the armed tier's badge + glow (+ space) show steady.
            bool flashing = _energyMiniT0 >= 0f;
            if (flashing)
            {
                float el = (Time.time - _energyMiniT0) * 1000f;
                if (el >= energyMiniFlashMs) { flashing = false; _energyMiniT0 = -1f; if (_energyMini) _energyMini.enabled = false; }
                else if (_energyMini)
                {
                    var chip = _energyFillSpr != null ? _energyFillSpr[Mathf.Clamp(m.ArmedLevel, 0, 2)] : null;
                    _energyMini.enabled = _energyHudOn && chip != null;
                    if (chip != null)
                    {
                        _energyMini.sprite = chip;
                        SdoLayout.PlaceBarFill(_energyMini, energyMiniPos.x + ox, energyMiniPos.y, 14f, 4f, el / energyMiniFlashMs, -0.2f);
                    }
                }
            }
            // level badge (MyEnergy2/3/4 = ×2/×4/×8) + EnergyEft glow — FIXED at their XML panel spots, shown for the
            // armed (or released) tier once the flash finished; hidden while nothing is armed.
            int badge = m.Active ? m.ReleasedLevel : m.ArmedLevel;
            // Keep the ×2/×4/×8 badge + glow VISIBLE through the band-up flash (was hidden on `!flashing`, which left a
            // black gap during the x2→x4 switch = user "變成黑色"). It just updates to the new tier's sprite.
            bool showBadge = badge >= 0 && _energyHudOn;
            if (_energyBadge)
            {
                _energyBadge.enabled = showBadge && _energyBadgeSpr != null;
                if (_energyBadge.enabled)
                {
                    _energyBadge.sprite = _energyBadgeSpr[Mathf.Clamp(badge, 0, 2)];
                    if (_energyBadge.sprite) SdoLayout.PlaceTopLeft(_energyBadge, energyBadgePos.x + ox, energyBadgePos.y, -0.2f);
                }
            }
            if (_energyEftSpr)
            {
                var fr = (_energyEftFrames != null && badge >= 0) ? _energyEftFrames[Mathf.Clamp(badge, 0, 2)] : null;
                bool showEft = showBadge && fr != null && fr.Length > 0;
                _energyEftSpr.enabled = showEft;
                if (showEft)
                {
                    _energyEftSpr.sprite = fr[glowFrame % fr.Length];                  // 10-frame loop, fast crackle tick
                    if (_energyEftSpr.sprite) SdoLayout.PlaceTopLeft(_energyEftSpr, energyEftPos.x + ox, energyEftPos.y, -0.15f);
                }
            }

            // SPACE press-prompt: the space.an 2-image pulse (hand → fist+flash), shown only when releasable
            if (_spaceSpr)
            {
                bool show = _energyHudOn && m.Ready && _spaceFrames != null && _spaceFrames.Length > 0;
                if (_spaceSpr.enabled != show) _spaceSpr.enabled = show;
                if (show) _spaceSpr.sprite = _spaceFrames[animFrame % _spaceFrames.Length];
            }

            // ShowTime EnergyScore (big) + EnergyBonus (small): SetTarget is BATCHED to the 8-beat settlement in
            // UpdateScoreDigits; here we just tick the count-up/pop each frame. Bonus stays hidden until it scores.
            _scoreRoll?.Tick(Time.time);   // pop fires only on a value change (SetTarget); hidden until the HUD reveals
            _bonusRoll?.Tick(Time.time);
            if (_bonusIcon) _bonusIcon.enabled = _energyHudOn;   // the "+" (GamePlay44) tracks the WinMyEnergy cluster
        }

    }
}
