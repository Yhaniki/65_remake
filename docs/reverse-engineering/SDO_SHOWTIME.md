# SDO ShowTime (氣條 / Energy) Mode

Reverse-engineered from `assets/sdox_offline/sdo_stand_alone.exe.c` (Ghidra decompile of the
offline standalone). Every claim below cites the decompiled line(s). This is the reference for
the remake implementation on branch `feat/showtime`.

## What it is

A per-song **ShowTime** mode. During normal play, good hits fill an **energy bar** (氣條). Once the
bar reaches a level the player can press **SPACE** to *release*: the game transitions into a timed
**auto-hit window** — every note is auto-resolved as **PERFECT**, combo never breaks — and a
**score bonus** accumulates on top of the normal score. When the timer expires it reverts to
normal play; the bar must be re-filled to release again. The bonus multiplier **stacks +1 each
release** in a song.

## Config gate

ShowTime mode for a song is the global byte `*(char*)(DAT_00674f04 + 0x87)`. When `== 1`:
- the gameplay screen wires the energy/showtime HUD (`91031`),
- showtime note skins are loaded (`111796`),
- the `Eft_SHOWTIME\Eft_Hit.an` hit burst is paired in (`112252`),
- the result screen uses `Statis\DdrShowTimeStatistic.xml` instead of `DdrStatistic.xml` (`100454-100458`).

Copied per-screen to `param_1 + 0xac1` (`87370`). A pre-song **sub-state** `0/1/2` (note-manager byte
`+0x5c8`, getter `FUN_00422140` @ `32489`, chosen by the `"CurChose"` UI `FUN_0047ff50` @ `94353`)
only picks the *skin/layout variant* (`showtime` vs `showtime1`, plain vs `_down`) and cycles on a
200 ms timer for a pulse animation (`110308-110313`). **It is not the run on/off state.**

## Object model (offsets on the gameplay-screen object `param_1`)

| Offset | Meaning |
|---|---|
| `0xd8` | active note-track ptr; `0xdc + lane*4` = lane tracks |
| `0xb40/0xb44/0xb48` | energy-bar **green/yellow/red pixel widths** (BPM/level scaled, `87925-87979`) |
| `0xb4c` | **ShowTime ACTIVE flag** (1 = running; blocks energy gain + re-trigger). set `84151`, cleared `89028` |
| `0xb50` | ShowTime **start timestamp** (ms). set `85709` |
| `0xb54` | accumulated energy budget == **duration in ms**. `84136`, `/1000`=sec `85710`, end test `89010` |
| `0xb58` (byte) | current armed **level** 0/1/2, `0xff` = not ready. `84131`, reset `88990`/`89031` |
| `0xb5c/0xb60/0xb64` | level thresholds / durations = **8000 / 12000 / 18000** ms (`87962-87964`) |
| `0x408` | ShowTime **activation counter** (−1 init `85156`, `++` per release cap 5 `84145-84146`) → bonus mult `(0x408+1)` |
| `0x400..0x404` | per-activation level history (5 slots, `84148`, saved `83251-83256`) |
| `0x830/0x834/0x838/0x83c` | per-beat PERFECT/COOL/BAD/MISS counters (`82833-82864`) |
| `0xcc4` | main score total (flat formula, `85822`) |
| `0x840` | **ShowTime bonus accumulator** (= the `EnergyBonus` number). `85825`, saved `83252`, merged `85008` |
| `0x370/0x374/0x378` | `myenergy_progress_g/y/r` bar segment widgets |
| `0x388/0x38c` | `WinMyEnergy` window / `"space"` prompt |
| `0x390..0x398` | `EnergyLevel1..3` tier badges; `0x39c..0x3a4` `EnergyEft1..3` glow |
| `0x37c..0x384` | `EnergyProgress1..3` per-tier fill bars (range 0..500) |
| `0x3a8/0x3ac` | on-screen SCORE / `EnergyBonus` number labels |
| `0x3e8`(=1000)/`0x3ec` | `TransShowTime1` / `TransShowTime2` banner transitions |

Note-track object (`*(param_1+0xd8)`): `+8` = current energy scalar (visible bar), `+0xc/0x10/0x14`
= the three level caps (pixel widths, copied from `0xb40/44/48` each frame `88159-88161`).

## Energy accumulation (normal play)

Central per-hit accumulator **`FUN_004a64b0(track, delta)`** (`119648`): `energy += delta`, clamp
`[0, cap3(+0x14)]`. Called from the judgement handler **`FUN_0046cc20`** (`82812`), gated on
**not** being in showtime (`if (param_1+0xb4c == 0)` at `82836/82845/82854/82861`):

| Grade | count field | `delta` | Energy change |
|---|---|---|---|
| 4 PERFECT | `track+0x2c` | `+2` | **+2** (visible bar units) |
| 3 COOL | `track+0x30` | `+1` | **+1** |
| 2 BAD | `track+0x34` | `-1` | band-scaled **−10 / −15 / −20** |
| else MISS | `screen+0x83c` | `-2` | band-scaled **−30 / −40 / −50** |

Negative deltas remap by the band the gauge sits in (`119653-119684`): `-1`→ −10/−15/−20,
`-2`→ −30/−40/−50 for band green/yellow/red. No combo multiplier on energy. Energy does **not**
accumulate during showtime (all adds gated on `0xb4c==0`); it is not passively time-decayed in
normal play.

> **TWO SEPARATE TABLES — do NOT conflate (confirmed on the ONLINE build).** The gauge has a small
> integer **FILL counter** with **cumulative band caps** (`caps` @`0x181c/0x1820/0x1824`; practice
> mode `[0x14ac]==3` = 50/150/250; online reads them from config, ~60…1120 by song-score tier) AND a
> **separate WINDOW-duration table** `dur[]` = **8000/12000/18000 ms** (`0x1838/0x183c/0x1840`), which
> is the auto-perfect window length for a release at band 0/1/2 — **not** a fill threshold. The visible
> bar is driven by the FILL counter (online `[0x108+0x14]` vs thresholds `[0x108+0x18/1c/20]`), the
> release length by `dur[armed band]`. Per-hit fill = `FUN_00409320(combo) × speed` (chart note-value
> × speed, forward-only; Bad/Miss don't reduce it). **The remake NOW models these as two tables**
> (`ShowtimeMeter.BandCaps` + `ShowtimeMeter.WindowDurationsMs`) — the earlier single-ms-gauge collapse
> was wrong (it made ~combo-130 band-1 and the per-band recolour impossible).

## Levels, thresholds, duration

`0xb5c/0xb60/0xb64 = 8000 / 12000 / 18000` (`87962-87964`) are simultaneously the energy required
to arm level 0/1/2 (green/yellow/red) **and** the ShowTime **duration in ms** granted at that level.
`0xb54` (accumulated energy) is used directly as the window length: end time = `0xb50 + 0xb54`
(`85710`, `89010`). So releasing green ≈ **8 s**, yellow ≈ **12 s**, red ≈ **18 s**. The armed level
`0xb58` is the highest threshold reached (or `0xff`).

**Per-band LAP render (online, item 4).** The fill counter is cumulative, but the visible bar
**re-bases per band and changes colour**, so it reads as "fill green to full → reset to empty → fill
yellow → reset → fill red". Online it is a 15-frame sprite (3 bands × 5 sub-steps, `param_1+0x5a4`):
`score<T0` → frames 1..5 (MyEnergy5 green), `T0≤score<T1` → 6..10 (MyEnergy6 yellow), `T1≤score<T2`
→ 11..15 (MyEnergy7 red), `≥T2` → 15. `EnergyEft1/2/3.an` is a **level-up glow overlay** fired once
per band-up (`PlaySe(0x4f)`), NOT the fill bar. Remake: `ShowtimeMeter.DisplayBand` (colour) +
`DisplayFraction` (0→1 within the band) drive the fill; `ArmedLevel` (= completed bands − 1) drives
the badge/glow. Fill accumulates on good hits only (Bad/Miss add 0); `TryActivate` spends it to 0.

## Release (SPACE)

Handler **`FUN_0046eb70`** (`84075`, registered `91047`); DirectInput scancodes **`0x1c` (Enter)** and
**`0x39` (SPACE)** (`84128-84129`). Gauge-ready guard (`84130-84131`):
```
0xb4c == 0        // not already in showtime
&& 0xd8 != 0      // track exists
&& 0xb58 != 0xff  // a level is armed
```
On success: bump `0x408` (cap 5), record level into `0x400[count]`/`0x844`, set `0xb4c = 1`
(`84151`), hide the `space` prompt + show the achieved level's `EnergyLevel`/`EnergyEft` badge,
snapshot start into `0xca8`/`0xb50`, compute residual `0xca4 = energy − levelCap` (`84185-84187`),
and call `FUN_0049f6f0` → `FUN_0048d330(1)` + `FUN_00499550()` (autoplay on, skin/effect swap).

## Auto-hit window (force PERFECT)

- Autoplay gate `+0x538 & 1` (`FUN_0048d340` @ `103530`), set by `FUN_0049f6f0/0x710`
  (`113840/113853`).
- Per-frame note loop **`FUN_0049ad40`** (`110703`): when autoplay is on and a note sits at the
  receptor tick, it calls the **same** hit/release functions manual play uses —
  `FUN_004978c0(lane,0)` (auto-hit) / `FUN_00497c90(lane)` (auto-release hold) (`110771-110785`).
- Manual input is suppressed: `FUN_0049fd90` only acts when `FUN_0048d340()==0` (`114258`).
- Grade is forced PERFECT: grader **`FUN_0048c4a0`** (`102772`) short-circuits to `return 4`
  (PERFECT) for any in-window delta when the showtime flag **`+0x109b0 == 1`** (`102792`). Auto-hits
  fire at delta≈0 so every one is PERFECT; hold auto-release likewise (`108578-108592`).
- **Same chart** — no alternate note stream. `FUN_00499550`/`FUN_00499640` (`109787/109844`) only
  swap: note-graphics pointers (`+0x107e4`), board geometry (`+0x44a4` ↔ `+0x44dc`/`+0x4514`), and
  the hit-effect list (`+0x3168`). Note timing/data (`+0x548`) is untouched.
- Combo (`+0x10960`) only ever increments during showtime (never reset), driving continuous
  combo-burst FX.

## Score bonus (`EnergyBonus`)

ShowTime-aware beat processor **`FUN_004718a0`** (`85663`; non-showtime twin `FUN_004714f0` @
`85511` lacks the bonus):
```
iVar1  = perfect*5 + (bad + cool*2)*2 - miss;   // this beat's weighted hits (85819)
0xcc4 += iVar1 * 10;                            // base score still accrues (85822)
if (0xb4c != 0)                                 // during ShowTime (85824)
    0x840 += (0x408 + 1) * iVar1 * 10;          // BONUS accumulator (85825-85826)
```
Per-note flat weights (×10): PERFECT +50, COOL +40, BAD +20, MISS −10. The bonus is **additive on
top of base**, multiplier `(0x408 + 1)` = **activation count + 1** → 1×, 2×, 3× … capped 6×
(`0x408` caps at 5). It is **not** ×2 fixed, **not** combo-scaled, and does **not** depend on the
green/yellow/red level (level only sets duration; the released level is merely recorded).

Since showtime auto-hits are all PERFECT, effective per-note during activation N (0-based):
`50 (base) + (N+1)·50 (bonus)`. First release → 100/note, third → 200/note, cap → 350/note.

Displayed live via the `EnergyBonus` label `0x3ac` (`85833`). At showtime/song end **`FUN_004705d0`**
(`85008-85015`) folds the bonus into the score (`score += bonus`), resets the label to 0, and plays
the "bonus tallied" SFX `0x54`. Saved separately to the record (`0x840` @ `83252`) alongside the
5-slot level history.

## End & warnings

Countdown in `88999-89053`: pre-end warning FX at remaining `< 0xbb9` (**3001 ms**) and `< 0x2bd`
(**701 ms**). When `now ≥ 0xb50 + 0xb54` the gauge finishes draining and `89026-89043` ends
showtime: `0xb4c=0`, `0xb54=0`, `0xb58=0xff`, hide all EnergyEft/EnergyLevel/`space`, call
`FUN_0049f710` → `FUN_0048d330(0)` + `FUN_00499640()` (autoplay off, revert skins/effects).
`TransShowTime1/2` (`0x3e8/0x3ec`, `90990-90998`) are the end-of-song banner slide-in/out
(`88712-88748`), not the mid-song release.

## Assets (all present under `assets/Datas/`)

- **Layout** — `UI/GAMEPLAY/PLAYSHOWTIME/GAMEPLAYSHOWTIME.XML` (loaded only in showtime mode; base
  modes have none of the Energy widgets).
- **Energy meter** — `PLAYSHOWTIME/`: `MyEnergy0..8.an` (frame + green/yellow/red fills `5/6/7` +
  tier badges `2/3/4` + empty track `8`), `EnergyEft0.an` (level-up burst, path prefix `GamePlay\`),
  `EnergyEft1/2/3.an` (per-tier glow), `space.an`, `EnergyScore.an`, `EnergyBonus.an`, `GamePlay44.an`
  (bonus icon at 544,23). Lean variant: `energy_y/_b/_r/_lean.an`, `showtime0001-0009.an`, `light*.an`.
- **Banner** — `PLAYSHOWTIME/`: `ShowTime0..5.an` (letter tiles), effects `Cirwin1/2`,
  `TransShowTime1/2` (XML-defined).
- **Hit burst** — `EFFECT/EFT_SHOWTIME/EFT_HIT.AN` + `EFT_HIT0..11.PNG` (2D `.an` sequence, spawned
  on best hits when `0x87==1 && arg==2`).
- **Note skins** — `NOTEIMAGE/NOTE_IMAGE_SHOWTIME{,_DOWN,1,1_DOWN}.DGN` + `NOTEIMAGE_SHOWTIME{,1}/`.
- **Result** — `UI/STATIS/SHOWTIMESTATISTIC/DDRSHOWTIMESTATISTIC.XML`, `LEVEL.AN`,
  `ShowTimeStatis0..14.an` (adds `ExplodePoint{1..5}_{1..3}` per-stage tier echo + `bonus%d` field).
- **(Optional)** camera `CAMERA/SHOWTIME.CDT`.

Key widget positions (GAMEPLAYSHOWTIME.XML): `space` 284,56 · `EnergyLevel*` 311,21 · `EnergyEft*`
304,12 · `EnergyProgress*` 279,15 (w14 h4, range 0..500) · `EnergyBonus` 525,23.

### ✅ Energy-bar FILL — the online truth (2026-07-03, sdo.bin.c)

The moving fill of the top bar is **NOT a 2D ProgressBar and NOT the MyEnergy5/6/7 images**. It is a
dedicated **3D-EFT gauge object** (`FUN_0040dc00` @21398, driven by `FUN_0040e210`/`FUN_0040e0f0`):
an electric particle strip rendered through its own camera into a **scissored viewport over the channel**
(`rect {22,14,510,29}` args @360780; slide span = width×0.625), **translated horizontally** as the fill
counter grows, value eased over **500 ms** (ctor `+0xc8`). Per band it re-bases empty→full and swaps the
strip effect — band indices `{0x2b,0x28,0x2a}` (@360784-360786; offline names kuanghuan/shedeng/shedeng1 —
online renumbered, unresolved). The strip's bright head IS the tip flare of the videos.
- `EnergyProgress1/2/3` (14×4 @279,15, range 0..500) = the **500 ms band-up flash** (value = elapsed ms;
  level-down runs 500→0). ProgressBar rendering is a CLIPPED partial draw (`FUN_00472390` style 0) — no tiling.
- `EnergyEft1/2/3` = steady armed-tier glow FIXED at (304,12), shown after the flash, through the window.
- Badges MyEnergy2/3/4 = **×2/×4/×8 multiplier** chips (not "Lv"), colours **yellow/blue/red** (5/6/7).
- During the window the bar **drains**: `displayedFill = residual + cap[armed]·remaining/duration`,
  residual = overflow above the released cap, carried over at window end (X 361755-361815).
- "15 frames = 3 bands × 5 steps" is REAL but **lean-skin only** (`FUN_0043bb60()==1`, ENERGY_LEAN 16 frames
  + needle 0→180° via `CirShowTimeLean`); the main bar is continuous.
- `showtimeenegy` = SE 0x4d one-shot when the HUD appears; **no slide-in** for WinMyEnergy in the XML; the
  **3-stage intro demo is real**: at HUD creation the gauge tweens 0→cap0→cap1→cap2 at 1200 ms/stage then
  snaps to 0 and live tracking takes over.
- Remake approximation: official `ENERGY_Y/B/R` 11-frame 85×17 electric-plasma capsules as the sliding strip
  (right-anchor crop = head rides the tip), additive, over channel (22,15)-(272,29); HpEft flare on the tip.

#### ✅ 2026-07-03 round 3: the strip effects RESOLVED = POWER_Y/B/R.EFT (+ real head glow)

Byte-walk of the full online table: **0x2b=power_y.eft (band0), 0x28=power_b.eft (band1), 0x2a=power_r.eft
(band2)** (0x29=bubble.eft unrelated; POWER.EFT = un-indexed dancer-scale prototype). Files byte-identical in
the runtime tree. Structure (all three identical, colours differ): root slot0 = carrier looping every 16 ticks
(0.32s) re-firing everything; slot2+3 = the STRIP BODY (RAI_01/04/05 electric ribbon textures, 20u long,
trailing at local z=-10 → behind the head after the gauge's rotY=90); **HEAD GLOW = origin-anchored emitters:
slot1 AEF_4_02 tinted halo (1.5u, scl 0.21→5.1, 0.6s, tint Y=(255,180,64)/B=(127,129,255)/R=(255,66,65)) +
slot5 NAGA00 white 4-point star (1u, scl 0.17→3.6, 0.7s, ~180°/s spin, 0.2s delay) + slot6 RING_L ×3 sparks** —
the user was right: the official head flash is real, and it is these emitters, pulsing at ~3Hz via the 0.32s
loop, cropped by the gauge's 488×15 viewport into a horizontal wash. Gauge camera: PerspectiveLH(488,15,800,
1200), viewport {22,14,488,15}, effect scale 100, 1 EFT unit = 80 design px; headX sweeps [-305..0] wu =
design x22→266.
- Remake: head glow = 2 layers × 2 generations of sprites (AEF_4_02 tinted + NAGA00 white spinning) on the new
  `Sdo/GlowClipRect` shader (additive + world-rect clip = the channel), UpdateTipGlow in ScreenGameplay.Hud.cs.

#### ✅ Window duration + breaking selection (round 3 RE)

- **8000/12000/18000 ms is a THRESHOLD, not the window length**: at release the exe walks the song's dance
  segments (pas) accumulating each segment's chart milliseconds until the tier budget is reached — the window =
  whole-pas-quantised budget (FUN_00643030 @348192-348202 accumulates straight into the duration field
  +0x1830; unitMs = 240000/(BPM×dpsHeader), FUN_00409320 = pas grid length). Medians over all 1796 online song
  DPS: 9.5/13.8/19.6s; Frida's 11.9s (lv0) and 16.7s (lv1) reproduce exactly with 8-beat pas. The window also
  officially STARTS at the next pas boundary (+0x182c written on the pas tick after the press — remake keeps
  press-instant start as a deliberate UX deviation). Residual: release spends fill −= cost(level) and the
  overflow carries over (@348249). Remake: ComputeShowtimeWindowMs = ceil(budget/pasMs)×pasMs (showtimePasBeats
  = 8), ShowtimeMeter.TryActivate(now, windowMsOverride) + residual carry.
- **Breaking selection is NOT per-release random**: FUN_0092d280 rolls each tier's variant ONCE at song load
  (E=rand%6, N=rand&7, H=rand&7) and it stays fixed all song; at release the TIER LETTER = the RELEASED ENERGY
  LEVEL (0→E, 1→N, 2→H — FUN_0092d3f0 @611772), NOT the song difficulty. Break lengths (E≈10s/N≈14s/H≈19s)
  are designed to fill their tier's pas-sized window. When a break ends mid-window the official parks the
  dancer in idle rest (cat 0x15 loop, FUN_00930400 @613781) — the remake instead hands back to the song
  choreography (user-requested deviation).
- **BOOM render bug found**: BOOM slot0 (the pastel radial burst) is an attach child of an invisible carrier
  with baseSize (1,5,3); the remake multiplied that into the billboard's scale → a 900px vertical sheet.
  Engine ground truth (billboard branch @664689-664745): billboards take only the TRANSLATION from the attach
  matrix, never the parent scale. Fixed in EftEffect (non-Persistent billboards use own scale only). BOOM's
  meshIdx=101/ring seg=8 are dead data (mesh needs flag 0x20000, ring needs flag bit3); official burst =
  20ms flash + 0.6s shockwave + 1s pastel radial fan, one-shot ~1.05s total.

### ✅ 2026-07-04 round 4: gauge EFT ENGINE internals (ONLINE `H:\sdo_cn\sdo.bin.c`) — electric-flow & white-flash RESOLVED

Full RE of the online 3D-EFT engine to answer the two open visual questions ("does the electric strip
scroll?" and "where is the flickering white head flash?"). Source = the fresh online Ghidra decompile
`H:\sdo_cn\sdo.bin.c` (base 0x400000, VA==addr). Frida anchor offsets that seeded this (from
`showtime_online_log.txt`): PlaySe 0x965a20, window-start 0x9dc1a0, window-end 0x9dc290, banner 0x6b13a0,
resource-load 0x9e5680, energy-sampler 0x6adf60, energy-add 0x636c30.

**The gauge is the effect `POWER_Y/B/R.EFT` and NOTHING ELSE animates it.** Parsed `POWER_Y.EFT` live slots
(trigger tree `0→1, 0→5, 0→6, 1→4, 4→2, 4→3`; POWER_B/R identical but for ribbon tex + slot-3 cross-angle):

| slot | tex | role | key fields |
|---|---|---|---|
| 0 | — | **carrier** (external) | `life0=-15` ⇒ 16-tick loop; re-fires 1/5/6 each loop |
| 1 | 30 `aef_4_02` | **head halo** (soft round glow) | life30, trig3, vel.y=0.2, baseSize1.5, alpha 0→230→0, tinted **gold/blue/red** per band |
| 2,3 | 207/27/206 `rai_05`/`rai_01`/`rai_04` | **electric ribbon** (horizontal lightning) | life50, `attach=1` (rides slot4's growing Z), 20u long, slot2 scaleY **pinches to 0 at mid-life** |
| 4 | — | ribbon carrier (external) | life20, scaleZ grows 0→1 → ribbon extends from head |
| 5 | 100 `naga00` | **white 4-point star = THE flickering white flash** | life35, trig3, `startDelay=10` (randomised 0..9), **pure white 255/255/255**, scale 0.17→~3.6 |
| 6 | 96 `ring_l` | white X sparks | emit3, life10, trig3, `startDelay=15`, white |

Textures resolve via `3DEFT/GENERIC/LIST.TXT` (0-based line index = texid): **30=AEF_4_02, 96=RING_L,
100=NAGA00, 207=RAI_05, 27=RAI_01, 206=RAI_04** — all present in the remake tree, all confirmed against the
live client. RAI_0x = horizontal lightning-bolt textures; NAGA00 = white-hot 4-point star + blue/red radial.

**(1) DRAW — `FUN_0098d660(instance*, IDirect3DDevice9*)` @664414. There is NO UV scroll, anywhere.**
- Builds a unit quad (-0.5..+0.5) or a bit-8 multi-segment strip, `FVF=0x1C2` (XYZ|DIFFUSE|SPECULAR|TEX1,
  28-byte stride), `DrawPrimitiveUP(D3DPT_TRIANGLEFAN, 2)` @664771-772.
- tu/tv are a **pure discrete-flipbook table lookup** by the integer frame `word[0x46]`: `U=word[frame*4+0x57
  /+0x59]`, `V=word[frame*4+0x5a]/word[(frame+0x16)*4]` (@664661-688). The frame index is only **read** here.
  For a `frameCount=1` emitter (all POWER ribbons/glows) the frame stays 0 ⇒ **UV is 100 % static**.
- `D3DTSS_TEXTURETRANSFORMFLAGS=DISABLE` @664564 ⇒ **no texture-matrix UV pan either.** The strip path
  (@664601-653) writes the **identical** cell UVs to every segment — no per-segment U offset, no age term.
- **Over-bright → white** = three additive mechanisms hard-clipped at 1.0, NO gamma / NO MODULATE2X (COLOROP
  = plain MODULATE(4) @664480-483): (a) additive blend `SRCALPHA,ONE` (mode 0, @664489) accumulating
  overlapping particles; (b) `D3DRS_SPECULARENABLE=1` @664485 adds per-vertex SPECULAR (`word[0x35]`, the
  ch5/6/7 specular colour) on top of the modulated texture; (c) per-frame `TEXTUREFACTOR` @664561.
  Blend modes: 0=`SRCALPHA,ONE` additive, 1=`SRCALPHA,INVSRCALPHA` alpha, 2=`ONE,ZERO` opaque (xmesh).

**(2) UPDATE / LOOP — `FUN_0098fc80` (per-tick) + `FUN_0098f140` (instance init), 50 Hz (20 ms) fixed step.**
- **Carrier loop = negative-life encoding, applied at runtime:** `FUN_0098f140` @665439-442
  `if(life0<0){ life = 1-life0; flags |= 0x80000; }` (slot0: 1-(-15)=16, sets the **loop flag** not present in
  the file). Reset each tick: `FUN_0098fc80` @666729-731 `if((flags&0x80000)&&life==1) life=maxLife` ⇒
  **period = maxLife-1 = 15 ticks ≈ 0.30 s**; life never reaches 0 so the GC (`life==0` @663633) never frees it.
- **Child re-spawn** = the flicker source: @666670-728 switches on the **child's** trigType (`word[0x36]`):
  case0 birth (`life==maxLife-1`), case1 death, case2 AtTime (`word[0x3b]`), case3 repeat. The carrier's 15-tick
  reset re-fires slots 1/5/6 **every loop**; each new slot1 re-spawns slot4→ribbons(2/3). Children outlive one
  loop (30/35/10 ticks) so **2-3 generations overlap** → the crackle/pulse. New gen = `FUN_0098fa00` @665582
  (emit count = `word[0xc]`, so slot6 fires 3 sparks).
- **startDelay is RANDOMISED and that is FAITHFUL:** `FUN_0098f140` @665455-458 sets `startDelay = rand %
  word[0x10]` (0..N-1). Double freeze-gate @665763-770: `if(spawnDelay){--;return} if(startDelay){--;return}`
  — the particle neither ages nor renders until both expire. **⇒ the remake's `Random.Range(0, StartDelay)` is
  correct; do NOT change it to a fixed delay.**
- **Rotation ACCUMULATES, no hard-coded spin:** @666270-272 `rot[0x1a/1b/1c] += sampled ch 0xb/0xc/0xd (deg)`
  each tick → a flat-but-non-zero rot channel = constant spin. Channels 0x8/0x9/0xa are **billboard orientation**
  (fed to `FUN_0098d5c0` @666275), not spin. A flat/zero channel = no spin (NAGA00's rot channels are neutral →
  no self-spin from data; any spin would come only from the `word[0x1f5..7]` random-spin amplitude, also 0 here).
  Channel samplers: `FUN_0098ced0` @664068 (2-pt), `FUN_0098ce20` @664038 (piecewise, online twin of offline
  `FUN_004bcd30`).

**(3) GAUGE OBJECT — `FUN_0040dc00` ctor / `FUN_0040e0f0` set / `FUN_0040e210` per-frame. Translate-only, no RT.**
- Crops with a **D3D SCISSOR RECT** (ctor `param_3` int[4] `{l,t,r,b}` → gauge+0x160 → emitter+0x124 via
  `FUN_0098d240` @664180) — **there is no separate RT camera and no gamma pass.** View = `LookAtLH` eye(0,0,-1000)
  @21485; proj = `PerspectiveLH(w=r-l, h=b-t, near=800, far=1200)` @21496, horizontal mode negates proj m11
  (Y-flip). Visible half-width at the effect plane = `0.625·rectW`.
- Head sweep = **pure horizontal translate**: `FUN_0040e210` @21688 `SetTransform(pos=(curX,0,0), rot=(0,90°,0),
  scale=(S,S,S))`, curX eased over a **500-unit window** (gauge+0xc8) as 3-segment piecewise-linear; parked
  off-screen at x=-10000 pre-fill; start-x = `rectW·-0.625`, end-x = 0. The **3 band effects** live at gauge+0xd4/
  d8/dc; `FUN_0040e210` @21664 re-binds the active one by fill segment + re-applies the scissor.
- **No extra per-frame animation**: `SetTransform` (`FUN_0098cc30`) uses fixed rot + uniform scale; its auto-spin
  path (`emitter+0x70`) is never armed. The draw-time RGBA is the shared global effect colour, not a gauge pulse.
  **⇒ 100 % of the "electric flow" is internal to the POWER_*.EFT emitter channels.**

**Conclusion for the two remake issues:**
- **氣條電流流動 (#1): 官方沒有任何 UV 捲動或貼圖平移。** The "flowing current" is the **overlapping re-spawn**
  (fresh lightning ribbon every 0.30 s, riding slot4's growing Z-scale) + the **scaleY mid-life pinch** (slot2
  0.5→0→0.5 = a travelling thin spot) + additive/specular saturation. To reproduce faithfully the remake must
  re-spawn the ribbon chain (it does, via `Persistent`). A UV-scroll enhancement was implemented then **REMOVED
  per user decision** (官方沒有的就不要加).
- **頭光閃爍白光 (#2): 素材正確** (naga00 pure white). The flash = the white star re-spawned every 0.30 s
  (randomly staggered 0..9 ticks), additive + specular hard-clipped to white — the **same white-hot mechanism as
  the combo burst** ([sdo-combo-burst-whitehot]). If it doesn't "pop", the fix is additive **brightness** on the
  head glow so the white core clips (not a data/material change). No gamma, no MODULATE2X in the original.

### ✅ 2026-07-04 round 5: cone semantics + flag map + gauge viewport/flip (user: 粒子方向不對、白光沒出現)

Second deep-dive after user feedback (head-glow particles officially fly *into* the bar, remake's went straight
up; white flash still missing). Two agents re-read `H:\sdo_cn\sdo.bin.c`. **Findings overturn my velocity
hypothesis and pinpoint the real remake bugs:**

**(1) CONE words `[0x1ef..0x1f3]` = spawn POSITION scatter, NOT velocity** (the remake's interpretation was
already right). `FUN_0098f140` @665514-523 calls `FUN_0098ee50(&(0,0,1), …, ang[0x1ef],[0x1f0],[0x1f1],
mag[0x1f2],[0x1f3])` and ADDS the result to position words 0x17-19. Sampler (@665233-281): orthonormal basis
from constant axis (0,0,1) (`FUN_0098d330`), per-axis **uniform** ±ang/2 in 1 % steps (`rand%0x65*0.01`),
magnitude **uniform** lerp [inner,outer]. For negative-life (looping) emitters the cone re-adds **every loop
reset without resetting position** (@666786-794) = a per-loop random drift — but POWER's cone slots (1/5/6) are
one-shot children, so for them it's a fresh per-generation offset. Velocity words 0x14-16 are only ever rotated
by: (a) posSpread w0x11/w0x12 at birth (triangular, @665498-513), (b) channels 8/9/0xa per tick (deg/tick
velocity-direction rates via `FUN_0098d5c0` — rotates the vector in place about a basis built from itself,
@664382-410 / gate @666274-276), (c) the turbulence block (@666277-321). **POWER slots 1/5/6: all three are
zero ⇒ velocity stays exactly (0, 0.2, 0) — officially the particles DO rise, they never fly sideways.**
Integration @666323-332: `pos += vel × ch0[0x33] × speedJit[500]`; gravity @666347-353.

**(2) Draw flag map (`FUN_0098d660`)** — hierarchical, and bits 1/2/4 are NOT geometry: `0x40000` = draw nothing
(@664477); `0x20000` = xmesh 3D-mesh path (@664508); `(flags&7)==0` = **not drawn at all** (@664583); bits
1/2/4 select the UPDATE interpolator (`FUN_0098fc80` @665778/665988: 1=keyframe `FUN_0098ce20`, 2=2-point
`FUN_0098ced0`, 4=simplified translate-only matrix @665914-951); `0x8` = pre-baked ring/strip (verts built in
init @665356-420, draw only refills colour/UV); otherwise single ±0.5 quad → `0x10000` = camera billboard
(inverse-view basis + roll about the view axis only, isotropic scale from X, @664698-762), **else = WORLD QUAD**
with the particle's full `S·R·T` (@666386-393 `Scaling → YawPitchRoll(rotY,rotX,rotZ) → Translate`) × owner
matrix (@666394-398, live every tick → particles are owner-relative and ride the moving head). No draw branch
stretches along velocity. **POWER head glow (flags 0x2) = world quads**: particle initRot yaw 90° × owner yaw
90° = 180° ⇒ the quad faces the camera *by rotation composition, not billboarding* — drop either 90° and it
renders edge-on/invisible (classic port trap).

**(3) The remake's TWO real bugs (both in the composite, now fixed):**
- **Viewport width**: official scissor = the FULL `{22,14,488,15}` strip (design x22..510; NDC −1..+1 =
  worldX −305..+305; design x = 266 + 0.8·wx). The remake composited only the RT's left half (u 0..0.5 =
  worldX −305..0) onto the 22..272 groove ⇒ everything at worldX > headX (the **+Z-biased cone scatter**, which
  owner-yaw-90 maps to world +X = ahead of the head, up to 0.8×100 = 80 wu) was **cropped away — the missing
  head glow**. Fixed: full-width u[0..1] quad across design 22..510.
- **Vertical flip**: `FUN_0040dc00` L21499 negates gauge proj element +0x134 = float[5] = **D3D `_22`** ⇒ world
  +Y renders DOWNWARD (design +y). (A round-2 agent misread this as `_11` horizontal mirror — disproved by the
  head-sweep constants: −305→0 must land on 22→266.) The remake camera rendered +Y up ⇒ the rising (0,0.2,0)
  particles drifted UP = the user's "平的往上". Fixed: composite V flipped.
- Sanity notes: `EftFile.Load` already applies the engine's `life = 1 − life0` for negative life (slot0 → 16
  ticks ✓); the remake's cone-as-position-offset, ch0 0.5-pivot speed curve, and SRCALPHA/ONE additive all match
  the decompile verbatim.

### ✅ 2026-07-04 round 6: the REAL blue-wash bug = Legacy shader 2× over-bright (visually verified)

Built dance.exe and captured the gauge (added `SDO_SHOWTIME_DEMO=1` = PingPong the fill 0→cap2→0 to cycle all
bands, and `SDO_SHOWTIME_ISO=1/2` = render ribbon-only / head-glow-only). Findings:
- **Yellow/red bands render clean; the BLUE band washed to solid white** — and **ribbon-only isolation still
  washed white**, proving it's the *ribbon* over-brightening, not the head glow.
- **Root cause: the additive material is Unity `Legacy Shaders/Particles/Additive`, whose fragment is
  `2·tex·_TintColor·vertexColor` — a built-in ×2 the official does NOT have** (`FUN_0098d660` @664480 sets
  `COLOROP = D3DTOP_MODULATE` = ×1, NOT MODULATE2X). `EftEffect.SetCol` maps the diffuse straight through
  (`_TintColor = diffuse/255 · _bright`, `_bright=1`), so the gauge rendered **2× too bright**. The pale-blue
  ribbon diffuse `(130,129,255)` → `(1.02,1.02,2.0)` → clips R,G to 1.0 = **white**; saturated yellow `(255,255,128)`
  and red `(255,90,45)` keep a low channel so they survive. Over ~3 overlapping ribbon generations the wash is total.
- **Fix: `energyStripBright = 0.5`** (gauge-only; passed as `_bright`) → `2 × 0.5 = 1×` = the faithful MODULATE.
  Verified: blue band now renders **blue**, red/yellow unchanged.
- **Head white flash**: at the faithful 1× the naga00 star is a small faint spark (the big tinted halo slot1 dim-
  washes the channel and the star has no contrast). Fix: **`PowerHeadGlowBright` (2.5×) on the WHITE layers only
  (tex100 star + tex96 sparks)** — they overlap the ribbon only at the moving tip, so the flash pops white without
  re-washing the blue body; the halo (tex30) stays 1× so its per-band colour reads. Both are F4-tunable
  ("氣條 Gauge" panel). The removed UV-scroll experiment stays removed (官方無捲動).

## 3D effects — dancer aura + board burst (index → filename, resolved)

ShowTime spawns its **3D-EFT** effects by *integer index*, not by name. The index→filename map is a
static table (online `DAT_00b933c4`, offline twin `0x58953c`; **stride `0x44`**, entry = `char name[0x40]`
at `+0`, `int index` at `+0x40`, index also = `*(entry-4)`; offline 111 entries, online 1918).

⚠️ **2026-07-03 correction: the two tables are RENUMBERED — the offline names do NOT hold for the
online client.** The Ghidra image = `assets/閉撰敃氪/sdo.bin`; mapping VA `0xb933c4` through its PE headers
lands at file offset `0x7933c4` = a byte-perfect table start (`yuanpan.eft`, …). Walked entries: 26=kuanghuan,
27=kuanghuan1, 28=kuanghuan2, **39 (0x27)=edge4.eft, 44 (0x2c)=body_star.eft, 45 (0x2d)=boom.eft,
55 (0x37)=kemuri.eft**. (kuanghuan1=44 is the OLD offline/TW numbering — the source of the earlier
mis-mapping; kuanghuan1/2 are carnival-confetti effects that look nothing like the videos.)
All the online files also exist byte-identical in `Extracted/3DEFT/`:

| online index | filename | role | spawn |
|---|---|---|---|
| `0x2c` (44) | `body_star.eft` | **dancer aura** — star twinkles + streak glow on the body | `FUN_0092cec0` (latch `[+0x169c]`) → `FUN_0098cae0(0x2c, layer 1, no private matrices → SCENE camera)`; per-frame `FUN_00930e50`: `SetTransform(rootX, 40, rootZ, rot 0, scale 20)`; killed by `FUN_0092ce00` at window end / cmd 0x17 |
| `0x2d` (45) | `boom.eft` | **board-burst centre** — ~1s ring-of-columns + shockwave flash | `FUN_0098cae0(0x2d, layer 2)` → `FUN_0098cc30(-90,333,0, rot(90°,0,0) pitch-X, 50³)` (sub-state ≠1 only) |
| `0x27` (39) | `edge4.eft` | **board-burst sides ×2 = the full-height lightning columns** (tornado meshes 103/104 + rai_00..03 flipbook, cyan ramp, naga00 sparks rising ~+877wu; root life −45 loops until handle kill) | `FUN_0098cae0(0x27)` ×2 → `(-490,-400,0)` & `(-130,-400,0)`, rot 0, scale 70 |
| `0x37` (55) | `kemuri.eft` | sibling (`FUN_0092cef0`, latch `+0x169d`) | — |

More findings (sdo.bin.c line cites in the session notes):
- The handlers `FUN_00643030`/`FUN_006a4cf0` switch on **DirectInput scancodes** (0x1c Enter / 0x39 Space —
  both fire the release), not message ids.
- Burst camera: `FUN_009ea9a3` = D3DXMatrixLookAtLH (eye 0,0,−1000) + `FUN_009eaaeb` =
  D3DXMatrixPerspectiveLH(**w=800,h=600,zn=800,zf=1200**) ⇒ at the z=0 plane **1 wu = 0.8 px**, +y = screen UP;
  projected: centre (328,34), sides (8,620)/(296,620) — side bases sit 20px BELOW the screen, columns grow UP.
- The burst pass (layer 2) draws in the frame loop AFTER the scene AND both UI passes on a cleared z-buffer
  (FUN_00402d20) — i.e. **over notes and HUD**; the aura (layer 1) draws inside the scene pass (z-tests the dancer).
- Transforms are set ONCE at spawn (nothing re-positions 0x27/0x2d); persistence = edge4's own root loop.
- **Missed pair**: `FUN_0046d220` (643030-class only) spawns 2 MORE edge4 in the note-board's own camera at
  board-space (±25.6, −43.3, 0), rotX = board tilt, scale (5,10,5), viewport-clipped to the board rect.
- **The aura starts on the NEXT BEAT after the keypress** (per-beat handler sets window start + swaps the dancer
  DPS + spawns the aura + broadcasts cmd 0x16; cmd 0x17 = window end, kills the remote aura too).
- Sub-state 1 (tilted "down" layout) uses a different camera (eye −25,0,−1000 → at −25,−300,1700; zn 1056
  zf 3300) and only two 0x27 at (220/−250, −500) scale (100,210,150).
- `0xe2/0xe4` at (−520/−100,−400) in `FUN_0063b550` are **qinmi1/qinmi2 (couple-mode)** — not showtime.

**Frida confirmation** — `assets/閉撰敃氪/hook_showtime_online.js` (hooks 8–11) dumps the static table and
logs `PLAY3D` / `XFORM` / `AURA-FN` lines live (VA-relative, self-sanity-probed). Caveat: the on-disk
`H:/sdo/…/sdo.bin` is a *different, smaller* build than the Ghidra image these VAs came from — attach to the
matching build, and the probe will fall back to live-hook-only if the table VA isn't sane.

## Remake mapping

The remake funnels every judged event through **one** method `ScreenGameplay.ApplyEvent(Judgment,
lane)`; master clock is ms; input is legacy `UnityEngine.Input`; `Space` is free (lanes = A/S/W/D +
numpad). Pure logic lives in `Sdo.Ruleset`.

- **`Sdo.Ruleset/ShowtimeMeter.cs`** — pure, unit-tested: energy accrual, arm level, release,
  activation count, bonus accumulator, timer. (This file.)
- **`ScreenGameplay.ApplyEvent`** — `_showtime.OnJudge(j)`; swap the hit-FX branch during showtime.
- **`Update` key block** — `Input.GetKeyDown(KeyCode.Space)` → `_showtime.TryActivate(now)`.
- **`Update` after clock, before `ScrollNotes`** — `_showtime.Tick(now)`; while active drive
  auto-perfect (reuse `AutoPlay`/`forcedJudge`) instead of `HandleInput`.
- **`BuildHud` / `ScreenGameplay.Hud.cs`** — energy bar (mirror `UpdateHpBar`) + SPACE prompt +
  `BONUS` readout.
- **`ScreenGameplay.Effects.cs`** — showtime hit burst (mirror `SpawnHit3d`, load `EFT_SHOWTIME`),
  note-skin swap via `SetNoteBoardSkin` on enter/exit.
- **Result** — add the folded `Bonus` into the final score / a separate result field.

### ✅ 2026-07-05 round 6: gauge fidelity fixes (user: 藍電流貫穿線 / 電流太少太慢 / 頭光只有一顆且旋轉太固定)

Diagnostic workflow `wf_0c0ba4d2` (3 read-only agents vs `sdo.bin.c` + a ground-truth byte-parse of
`POWER_Y/B/R.EFT` via `EftFile.cs`). Ground truth (all 3 bands byte-identical bar ribbon tex + slot3 cross-angle
+ tint): slot0 carrier life-15→16-tick loop; slot1 tex30 halo life30 cone ang(360,360) mag[0,0.8]; **slot2 tex207
ribbon life50 attach1 base(20,0.4,0.8) initRot(0,90,0) scaleY 3-key pinch**; **slot3 ribbon life50 attach1
base(20,0.8,0.8) initRot(90,90,0)** (B=45° cross), alpha 255→0; slot4 external ribbon-carrier life20 flags 0x40001
(the ONLY full-curve slot) scaleZ 0→1; slot5 tex100 naga00 star life35 startDelay rand0..9 cone ang(180,180)
mag[0,0.8] + small rotZ ~3.6°/tick; slot6 tex96 ring_l emit3 life10 startDelay rand0..14. Fixes in `EftEffect.cs`:

1. **藍電流貫穿整條 (slot3 penetrating line)** — the `!(_isPower && Slot==3)` carrier-scale exclusion was a STALE
   guard from the pre-inverse-rotate direct-multiply path. With the real `initRot=(90,90,0)`,
   `Inverse(Euler)·(1.5,1.5,1.5f) = Abs(1.5f,1.5,1.5)` puts carrier growth on slot3's LOCAL X (**length**), not
   height — so removing the exclusion makes slot3 right-anchor at the head and grow left like slot2 (no tall band).
   Leaving it in stranded slot3 at a constant full 20u **centred** length → a bright line straddling the head into
   the un-filled zone. **Fix: delete the exclusion.**
2. **電流太少/太慢 (density)** — the broken slot3 (line, then dimmed to `PowerCrossDim=0.4`) meant only slot2 read
   as current = half the ribbons. Fixing slot3 makes it the proper second CROSSING ribbon; **`PowerCrossDim`
   0.4→1.0** so it contributes fully (its alpha 255→0 handles the fade). Tick rate (50 Hz) + 16-tick loop +
   50-tick ribbon life (≈3 overlapping generations) were already engine-faithful — no non-official additions.
3. **頭光只有一顆、旋轉太固定 (head glow)** — the INT-position truncation (spawn + per-tick) was truncating **all
   three** axes; the engine (`FUN_0098fc80` @666331-332) int-stores only Y (0x18) + Z (0x19) and keeps **X (0x17 =
   depth) float**. Truncating X collapsed every re-spawned naga00 generation onto depth 0 → the 2-3 overlapping
   stars stacked exactly = one fixed blob. **Fix: truncate only Y/Z, keep X float** → per-generation depth scatter
   returns = many nested, randomly-flashing stars. Kept the small official rotZ (not a bug; do not remove).

### ✅ 2026-07-05 round 7: gauge ribbon count + frequency + tick↔ms RE-verified (user: 只看到一條 / 感覺不只兩條 / 頻率+tick 確認)

Agent re-parse of POWER_Y.EFT bytes + `sdo.bin.c`, cross-checked with a faithful tick sim:

- **Tick = 20 ms (0x14), 50 Hz, FIXED accumulator on a ms master clock — frame-rate INDEPENDENT.** Driver
  `FUN_0098c8d0` @663797-806: `steps = (now_ms − last_ms) / 0x14`, each step = one particle update. The remake's
  `Step=0.02` is correct. (`0.023809524`=1/42 @25629 is a UI sprite-scale constant, NOT the timestep — red herring.)
- **Ribbon re-spawn = every 15 ticks = 300 ms.** Carrier slot0 `life0=-15`→ maxLife 16, loop flag; re-fires children
  when `life==maxLife-1==15`. slot1(trig3)→slot4(trig0)→slot2/3(trig0) ⇒ ribbon pair reborn every 15 ticks.
- **Ribbons = slot2 + slot3 ONLY** (both the file's single ribbon tex: Y=207, B=27, R=206 — three *colour variants*,
  not three tex in one file), `emit=1` each. Slots 7-31 are dead template padding.
- **KEY: ribbons are attach=1 and die when their slot4 carrier dies** (FUN_0098fc80 @666401-406: attach child +
  word0x20f==0 → self-kill on parent death). slot4 life=20, so ribbon EFFECTIVE life ≈ 20 ticks (NOT 50). ⇒
  simultaneous ribbon quads oscillate **2↔4 (peak 4, avg ~2.5)**, never 8. The user is right it's >2; ceiling is 4.
  Remake was rendering the full 50-tick life → ~8 mushed bands. **Fixed: `_isPower && attach && parent.life<=0 →
  life=0`** (EftEffect.StepParticle) = official ≤4 + the sharp 300 ms pop-in/out crackle. Combined with slot3's
  camera-facing cross-angle (round-7 earlier) = two *visible* crossing bands + their overlapping generations.

### Remake defaults vs exe (documented deviations)

- Single ms-unit gauge (max 18000) with **tunable** per-hit gains (exe fill curve not decoded).
- Bonus uses the exe flat per-note weights (50/40/20/−10) × `(activations)`; displayed as a separate
  `BONUS` number folded into the final score (exe behaviour), even though the remake's live score is
  the combo/`ServerScore` formula.

## Open questions

1. Exact per-hit fill of the ms-budget gauge `0xb54` (`FUN_00407610(combo) × seed`) — not decoded;
   remake uses tunable gains.
2. Meaning of sub-state 1 vs 2 (`+0x5c8`): skin/layout variants (`six_notes_board2` vs
   `notes_board_miss1`), gameplay semantics unconfirmed.
3. Whether the always-on `0x87==1 && arg==2` receptor burst and the effect-list swap both fire
   (double effect) or one supersedes.
4. Whether a lower armed level can be released while a higher is charged (exe always releases current
   `0xb58` = highest).
