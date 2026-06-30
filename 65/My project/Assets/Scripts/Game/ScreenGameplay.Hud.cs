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
                if (spr != null) { PlaceAspect(_scoreDigits[i], ScorePos.x + i * ScoreDigitPitch + 14, ScorePos.y + 18, 29); _scoreDigits[i].transform.localScale *= DigitBounce(Time.time - _digitPopAt[i]); }
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

        private void UpdateComboDigits()
        {
            int combo = _score.Combo;
            if (combo < 2) { foreach (var d in _comboDigits) d.enabled = false; if (_comboWord) _comboWord.enabled = false; _lastComboShown = combo; return; }
            if (combo != _lastComboShown) { _comboPopAt = Time.time; _lastComboShown = combo; }
            float pop = (1f + Mathf.Clamp01(1f - (Time.time - _comboPopAt) * 9f) * 1.0f) * 0.8f;
            string s = combo.ToString();
            float startX = TrackCenterX - (s.Length - 1) * ComboDigitStep / 2f;   // centred on the track
            for (int i = 0; i < _comboDigits.Count; i++)
            {
                var d = _comboDigits[i];
                if (i >= s.Length) { d.enabled = false; continue; }
                var spr = _comboDigitSprites[s[i] - '0'];
                d.enabled = spr != null; d.sprite = spr;
                // pop scales the WHOLE number as a single group about its centre (TrackCenterX): grow each digit's
                // offset-from-centre by `pop` too, so the inter-digit gaps expand in step with the digit size and the
                // places never collide/overlap. (Scaling each digit about its own centre left the gaps fixed -> fight.)
                if (spr != null) { float dx = TrackCenterX + (startX + i * ComboDigitStep - TrackCenterX) * pop; PlaceAspect(d, dx, ComboDigitY, ComboDigitW, -2); d.transform.localScale *= pop; }
            }
            if (_comboWord && _comboWord.sprite != null) { _comboWord.enabled = true; PlaceAspect(_comboWord, TrackCenterX, ComboWordY, ComboWordW); _comboWord.transform.localScale *= pop; }
        }

        // ==== ranking UI: head nameplate, centre rank N/M, right-side roster list ====

        private void BuildRankingUi()
        {
            _finalEst = Math.Max(20000L, (long)_map.TotalNotes * 68L);   // ≈ all-perfect ServerScore ceiling
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
                _rosterScore[row] = TextStyles.NewLabel("RosterScore" + row, TextStyles.Style.ListOther, 45, rosterFontWorld, TextAnchor.MiddleRight);
                _rosterScore[row].Position = SdoLayout.ToWorld(rosterScoreX, y, -3f);
            }

            // spectators (旁觀玩家): GAMEPLAY18 title + fake light-blue names (static; never re-sorted).
            // 預設關閉(showSpectators=false) — 全是測試假名;不建 → _lookerTitle/_lookerRows 留 null，後續都有 null 防護。
            if (showSpectators)
            {
                _lookerTitle = NewSR("LookerTitle", SdoExtracted.LoadImage(gpDir, "GAMEPLAY18.PNG"), 45);
                SdoLayout.PlaceTopLeft(_lookerTitle, lookerTitleX, lookerTitleY, -3f);
                _lookerRows = new Label3D[SpectatorNames.Length];
                for (int i = 0; i < SpectatorNames.Length; i++)
                {
                    _lookerRows[i] = TextStyles.NewLabel("Looker" + i, TextStyles.Style.Looker, 45, lookerFontWorld, TextAnchor.MiddleLeft);
                    _lookerRows[i].Position = SdoLayout.ToWorld(lookerX, lookerFirstY + i * lookerRowStep, -3f);
                    _lookerRows[i].Text = SpectatorNames[i];
                }
            }
        }

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

        // the local dancer's nameplate (animated arrow + name). It is a SCREEN-SPACE label (on the HUD
        // layer, not the scene layer): HeadMarker projects the head bone through the scene cam each frame
        // and draws a fixed pixel distance above it — so it floats over the head from any angle and never
        // occludes it. Only the local player is rendered, so there is exactly one.
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
            var go = new GameObject("HeadMarker");   // HUD layer (default) — children draw in the main ortho cam
            var hm = go.AddComponent<HeadMarker>();
            hm.Init(_arrowFrames, localPlayerName);
            Transform a = anchor;
            hm.AnchorGetter = () => a != null ? a.position
                : ((_avatarRoot != null ? _avatarRoot.position : _danceSpot) + new Vector3(0f, 59f, 0f));
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
            av.RestMot = LoadAsset(restMot, b => MotLoader.Load(b));
            av.DanceEnabled = () => false;     // always hold the standby idle clip
            av.DanceTimeSec = () => -1f;
            foreach (var rel in avatarParts)
            {
                var path = Path.Combine(SdoExtracted.Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) continue;
                var r = MshLoader.Load(File.ReadAllBytes(path));
                if (r == null || r.Submeshes.Count == 0) continue;
                var dir = Path.GetDirectoryName(path);
                // PortraitOpaque: forces drawn pixels fully opaque (no semi-transparent hair) + cutout gaps + two-sided.
                var sh = Shader.Find("Sdo/PortraitOpaque") ?? Shader.Find("Unlit/Texture");
                int si = 0;
                foreach (var sub in r.Submeshes)
                {
                    var go = new GameObject("h_" + Path.GetFileNameWithoutExtension(rel) + "_" + si++);
                    go.transform.SetParent(parent.transform, false);
                    go.AddComponent<MeshFilter>().mesh = sub.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    var tex = ResolveDds(dir, sub.Dds);
                    mr.sharedMaterial = tex != null ? new Material(sh) { mainTexture = tex }
                                                    : new Material(Shader.Find("Unlit/Color")) { color = PartColor(rel) };
                    if (sub.BindVerts != null && sub.BoneHrc != null)
                        av.AddPart(sub.Mesh, sub.BindVerts, sub.BoneHrc, sub.BoneWt, sub.MshInvBindByHrc);
                }
            }
            av.PoseInitialIdle();
            SetLayerRecursive(parent, headPortraitLayer);
            _headAvatar = av;
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
            Vector3 restHead = _headAvatar.transform.TransformPoint(_headModelPos);   // head bone world pos (rest)
            // Auto-frame from the MEASURED head (the official frames each costume's head to fill the row via a per-costume
            // scale table — no single value — so we measure THIS head instead of porting an arbitrary number). We capture
            // from ~chest up to ~0.15·(hair height) ABOVE the hair top, so the hair always lands inside the RT (never cut),
            // and target the capture centre. headZoom nudges the framing; auto OFF → manual dist/aimOffset.
            EnsureHairOffset();
            float h = _hairOffsetModel * Mathf.Max(0.01f, headAvatarScale);    // hair-top height above the head bone (world)
            Vector3 target;
            if (headAutoFrame && h > 0.001f)
            {
                // TIGHT head close-up (official look): capture ≈ chin→hair-top + ~10% margin (head fills the frame, only a
                // sliver of shoulder, hair spills above). dist 1.9·h, aim centred a bit above the bone so the face sits low.
                headPortraitDist = 1.9f * h * Mathf.Max(0.05f, headZoom);
                target = restHead + new Vector3(headAimOffset.x, 0.35f * h, 0f);
            }
            else target = restHead + headAimOffset;
            _headCam.fieldOfView = headPortraitFov;
            // Frontal (+Z) view tilted DOWN by headPitchDeg, matching the official cam (eye slightly above the head, looking
            // down ~2.3°). Place the cam back along that tilted forward axis and look at the head target.
            Vector3 dir = Quaternion.Euler(headPitchDeg, 0f, 0f) * Vector3.forward;   // +Z, pitched down
            _headCam.transform.position = target - dir * headPortraitDist;
            _headCam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        // Measure (once) how far the hair top sits above the head bone, in MODEL units (scale-independent), from the posed
        // avatar's renderer bounds — bounds are valid after CPU skinning (SdoAvatar recalculates them). Used by the auto-frame
        // so the cam captures the whole head + hair + a top margin (never cut) regardless of the model's unit scale.
        private float _hairOffsetModel = -1f;
        private void EnsureHairOffset()
        {
            if (_hairOffsetModel > 0f || _headAvatar == null) return;
            var rends = _headAvatar.GetComponentsInChildren<Renderer>();
            if (rends == null || rends.Length == 0) return;
            float top = float.NegativeInfinity; bool any = false;
            foreach (var r in rends)
            {
                if (r == null) continue;
                var b = r.bounds;
                if (b.size.sqrMagnitude < 1e-6f) continue;     // not posed yet
                top = Mathf.Max(top, b.max.y); any = true;
            }
            if (!any) return;
            float headBoneY = _headAvatar.transform.TransformPoint(_headModelPos).y;
            float offW = top - headBoneY;                       // world hair height above the bone
            if (offW <= 0.001f) return;                         // bounds not ready — retry next frame
            _hairOffsetModel = offW / Mathf.Max(0.01f, headAvatarScale);   // back to model units
        }

        // rebuild + redraw the roster (called at each 8-beat score commit and once at startup).
        private void RefreshRanking()
        {
            if (_rosterName == null || !_trackVisible) return;   // not built / hidden during the opening hold
            if (freeMode) { SetRankingVisible(false); return; }  // 自由模式: no ranking display during play
            RebuildRoster();
            UpdateRosterList();
            UpdateRankDisplay();
        }

        private void RebuildRoster()
        {
            _roster.Clear();
            _roster.Add(new PlayerEntry(localPlayerName, _score != null ? _score.Score : 0L, true));
            if (mockOpponents && !freeMode)   // 自由模式 = solo (no opponents)
            {
                double now = _clockStart >= 0 ? (Time.timeAsDouble - _clockStart) * 1000.0 : 0.0;
                double progress = _totalMs > 1.0 ? Math.Min(1.0, Math.Max(0.0, now / _totalMs)) : 0.0;
                int n = Math.Min(OpponentNames.Length, RosterRows - 1);
                for (int i = 0; i < n; i++)
                    _roster.Add(new PlayerEntry(OpponentNames[i], SimOpponentScore(i, progress), false));
            }
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
            var (rank, total) = RankingBoard.LocalRank(_roster);
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
            if (freeMode) on = false;   // 自由模式: ranking (rank N/M + roster list) never shows during play
            if (_rosterName != null)
                for (int i = 0; i < RosterRows; i++)
                {
                    if (_rosterName[i] != null) _rosterName[i].SetActive(on);
                    if (_rosterScore[i] != null) _rosterScore[i].SetActive(on);
                }
            if (_rankCurD) _rankCurD.enabled = on && _rankCurD.sprite != null;
            if (_rankTotD) _rankTotD.enabled = on && _rankTotD.sprite != null;
            if (_rankSlash) _rankSlash.enabled = on;
            if (_lookerTitle) _lookerTitle.enabled = on && _lookerTitle.sprite != null;
            if (_lookerRows != null)
                for (int i = 0; i < _lookerRows.Length; i++)
                    if (_lookerRows[i] != null) _lookerRows[i].SetActive(on);
        }

        private void UpdateHpBar()
        {
            if (!_trackVisible) return;   // hidden during the opening intro; SetTrackVisible(true) re-shows it
            double hp = _health?.Health ?? HealthProcessor.MaxHealth;
            float frac = Mathf.Clamp01((float)((hp - HealthProcessor.FloorHealth) / (HealthProcessor.MaxHealth - HealthProcessor.FloorHealth)));
            ShowEmoji(_emojiState.OnHp(frac));   // low-HP emoji (GTH): <30% bar fires once, re-arms above 40%
            // official MyHp fill clipped to (HP+150)/1150 (no overlay -> uniform red, no banding).
            if (_hpTex) SdoLayout.PlaceBarFill(_hpTex, HpPos.x, HpPos.y, HpSize.x, HpSize.y, frac, -0.1f);
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
                        _hpGlowMat.SetFloat("_ClipMinX", SdoLayout.WorldX(HpPos.x));
                        _hpGlowMat.SetFloat("_ClipMaxX", 100000f);   // no right clip — let the rightmost flash bleed out
                    }
                    _hpGlow.sharedMaterial = _hpGlowMat;
                }
                // HpEft sits at the HP fill's LEADING EDGE (decompiled HpEft.x = (HP+150)/1150 * barW + base), native
                // 64×32 (no width-squash). Clamp so the glow's right edge never juts PAST the bar's right end.
                // HpEft.png's bright/widest core sits at ~0.78 of its width; hpGlowOffsetX (default -20) lands that core
                // flush ON the fill edge (the old -16 left it ~2px right of the edge -> read as "too far right").
                float edgeX = Mathf.Min(HpPos.x + HpSize.x * frac, HpPos.x + HpSize.x);   // fill edge, capped at bar end
                float cx = edgeX + hpGlowOffsetX;
                PlaceAspect(_hpGlow, cx, HpPos.y + HpSize.y / 2f, HpEftSize.x, -0.2f);
                _hpGlow.enabled = hp > HealthProcessor.FloorHealth + 1;
            }
        }

    }
}
