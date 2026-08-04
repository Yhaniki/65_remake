# MMD -> Magica cloth conversion validation report

Comparator: `compare.py` over `ref_<scenario>.json` (pybullet ground truth) vs `magica_<scenario>.json` (Unity Magica Cloth 2 probe). All values meters/seconds/degrees; each file self-normalizes by its own `unitsPerMeter`.

**Pass rule:** `|magica - ref| <= max(0.35*|ref|, floor)` with floors 5 deg (angles), 0.15 s (times, 150 ms for turnLagMs), 0.02 m (amplitudes), 2 (oscillation count), 0.02/chainLen (normalized amplitude).

All 4 scenarios (rest / turn / walk / spin) recorded on both sides.

## Chain alignment notes

- chain Tie aligned to common 18-bone segment (Tie_2_1..Tie_19_1); ref: dropped Tie_0_1, Tie_1_1

## Anchor drive checks

| scenario | quantity | ref | magica | ok |
|---|---|---|---|---|
| rest | max anchor drift (m) | 0.0000 | 0.0000 | OK |
| turn | final |head yaw| (deg) | 90.0 | 90.0 | OK |
| walk | anchor travel (m, expect 2.4) | 2.40 | 2.38 | OK |
| spin | swept |yaw| (deg, expect 360) | 360 | 354 | OK |

## Per-chain metrics

### BangHairA

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 0.1024 | 0.1024 | +0% | 0.0358 | PASS |
| restDroopDeg | 38.63 | 42.89 | +11% | 13.52 | PASS |
| settleTime | 0.20 | 0.00 | -100% | 0.15 | **FAIL** |
| turnPeakAmp | 0.1760 | 0.1701 | -3% | 0.0616 | PASS |
| turnPeakAmpNorm | 1.7194 | 1.6618 | -3% | 0.6018 | PASS |
| turnLagMs | 150.0 | 83.3 | -44% | 150.0 | PASS |
| oscillations | 1 | 1 | +0% | 2 | PASS |
| walkStreamDeg | 53.67 | 52.16 | -3% | 18.78 | PASS |
| walkRecoverySec | 0.00 | 0.00 | inf | 0.15 | PASS |
| spinFlingAmp | 0.0075 | 0.0071 | -5% | 0.0200 | PASS |

### Dress_5

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 0.3084 | 0.3085 | +0% | 0.1079 | PASS |
| restDroopDeg | 53.30 | 33.23 | -38% | 18.65 | **FAIL** |
| settleTime | 3.52 | 1.00 | -72% | 1.23 | **FAIL** |
| turnPeakAmp | 0.0539 | 0.0003 | -99% | 0.0200 | **FAIL** |
| turnPeakAmpNorm | 0.1746 | 0.0010 | -99% | 0.0649 | **FAIL** |
| turnLagMs | 483.3 | -100.0 | -121% | 169.2 | **FAIL** |
| oscillations | 6 | 0 | -100% | 2 | **FAIL** |
| walkStreamDeg | 58.64 | 35.20 | -40% | 20.52 | **FAIL** |
| walkRecoverySec | 0.00 | 0.00 | inf | 0.15 | PASS |
| spinFlingAmp | 0.0667 | 0.0237 | -64% | 0.0233 | **FAIL** |

### RightTwicHairA

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 1.3990 | 1.3991 | +0% | 0.4896 | PASS |
| restDroopDeg | 31.80 | 31.30 | -2% | 11.13 | PASS |
| settleTime | 1.93 | never | - | - (one side never settled/recovered) | **FAIL** |
| turnPeakAmp | 1.1927 | 1.4925 | +25% | 0.4174 | PASS |
| turnPeakAmpNorm | 0.8525 | 1.0668 | +25% | 0.2984 | PASS |
| turnLagMs | 483.3 | 233.3 | -52% | 169.2 | **FAIL** |
| oscillations | 5 | 5 | +0% | 2 | PASS |
| walkStreamDeg | 32.09 | 39.73 | +24% | 11.23 | PASS |
| walkRecoverySec | 0.18 | 0.00 | -100% | 0.15 | **FAIL** |
| spinFlingAmp | 0.2224 | 0.5202 | +134% | 0.0778 | **FAIL** |

### Tie

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 0.4299 | 0.4299 | +0% | 0.1505 | PASS |
| restDroopDeg | 19.90 | 28.43 | +43% | 6.96 | **FAIL** |
| settleTime | 1.70 | 2.48 | +46% | 0.59 | **FAIL** |
| turnPeakAmp | 0.0215 | 0.0296 | +38% | 0.0200 | PASS |
| turnPeakAmpNorm | 0.0499 | 0.0689 | +38% | 0.0465 | PASS |
| turnLagMs | -133.3 | -233.3 | - | - (noise: chain not driven by this motion) | SKIP |
| oscillations | 0 | 2 | - | - (noise: chain not driven by this motion) | SKIP |
| walkStreamDeg | 59.26 | 36.80 | -38% | 20.74 | **FAIL** |
| walkRecoverySec | 0.00 | 0.23 | inf | 0.15 | **FAIL** |
| spinFlingAmp | 0.2418 | 0.1312 | -46% | 0.0846 | **FAIL** |

**Summary: 20 PASS / 18 FAIL / 2 SKIP(noise) / 0 NOT-RUN**

| group | metrics | PASS |
|---|---|---|
| shape at rest | chainLenM, restDroopDeg, settleTime | 6 / 12 |
| behaviour in motion | the turn / walk / spin metrics | 14 / 26 |

## Suspect knobs

For each failing metric: the conversion parameter in `MmdMagicaCloth.cs` (<repo>/65/My project/Assets/Scripts/Game/MmdMagicaCloth.cs) that most directly controls it, and the direction to move it.

- **BangHairA.settleTime** (magica 0.0 vs ref 0.2, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **Dress_5.restDroopDeg** (magica 33.23 vs ref 53.3, low): Magica hangs MORE vertical than Bullet. The reference keeps part of the authored pose because MMD 0/0-LOCKED joint LIMITS are load-bearing (e.g. twintails: authored 47.9 deg -> Bullet only relaxes to ~32 deg). MmdMagicaCloth derives stiffness from joint rotation SPRINGS only and ignores locked limits (`useAngle = springMean > 0`), so spring-less chains become free pendulums and hang straight down. Fix: derive `angleRestorationConstraint` stiffness (or a nonzero `gravityFalloff`) from joint-limit tightness (acc.LimSum/LimN is already accumulated but unused for stiffness), not just from springs.
- **Dress_5.settleTime** (magica 1.0 vs ref 3.517, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **Dress_5.turnPeakAmp** (magica 0.0003 vs ref 0.0539, low): Chain barely swings out on the head turn. Primary knob: `worldInertia` too low (line 191: Lerp(0.7, 0.25, springNorm); low = tracks the anchor rigidly). Also check the speed clamps: `particleSpeedLimit = 8 m/s * unitsPerMeter` (~300 u/s) requires the MC2 MaxParticleSpeedLimit patch -- unpatched it clamps back to 10 u/s = 0.26 m/s and the chain physically cannot whip. Secondary: damping too high.
- **Dress_5.turnPeakAmpNorm** (magica 0.001 vs ref 0.1746, low): no knob mapping
- **Dress_5.turnLagMs** (magica -100.0 vs ref 483.3, low): Tip peaks too early = chain stiffer/faster than Bullet. Angle restoration too strong (`angleStiff`), `worldInertia` too low (tip yanked with the head instead of lagging), or `movementInertiaSmoothing` (0.3, line 346) smoothing away the anchor acceleration spike.
- **Dress_5.oscillations** (magica 0 vs ref 6, low): Over-damped (or pinned): lower `sd.damping`, and check angle restoration / `gravityFalloff` are not killing the free swing.
- **Dress_5.walkStreamDeg** (magica 35.2 vs ref 58.64, low): Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag.
- **Dress_5.spinFlingAmp** (magica 0.0237 vs ref 0.0667, low): No centrifugal fling on the 360 spin. `worldInertia` too low (anchor rotation not converted into cloth inertia -- also verify the cloth GO is parented to the ANCHOR bone, line 317, not the static root), `rotationSpeedLimit` accidentally enabled (line 352 disables it), or the `particleSpeedLimit` MC2 clamp (unpatched = 0.26 m/s cap kills the fling).
- **RightTwicHairA.settleTime** (magica None vs ref 1.933, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **RightTwicHairA.turnLagMs** (magica 233.3 vs ref 483.3, low): Tip peaks too early = chain stiffer/faster than Bullet. Angle restoration too strong (`angleStiff`), `worldInertia` too low (tip yanked with the head instead of lagging), or `movementInertiaSmoothing` (0.3, line 346) smoothing away the anchor acceleration spike.
- **RightTwicHairA.walkRecoverySec** (magica 0.0 vs ref 0.183, low): Snaps back instantly = over-damped or pinned (angle restoration / gravityFalloff). Usually benign if ref is also ~0.
- **RightTwicHairA.spinFlingAmp** (magica 0.5202 vs ref 0.2224, high): Over-fling: `worldInertia` too high or `depthInertia` too low (line 194: 0.5*clamp(massGrad/5.7); raising it carries the root with the body and reins in the tip), or damping too low.
- **Tie.restDroopDeg** (magica 28.43 vs ref 19.9, high): Magica hangs LESS vertical than Bullet at rest. Either angle restoration is wrongly ON (`useAngle = springMean > 0` around line 176 misclassifying this part -> pins it to the styled authored pose), `gravityFalloff` is too high (holds rest shape against gravity; it is set = springNorm, line 180), or gravity is effectively too weak: `sd.gravity = 9.8*unitsPerMeter (~375 u/s^2)` needs the LOCAL MC2 gravity<=20 clamp patch -- if the patch is missing the sim runs at ~5% gravity and chains keep their styled outward pose. Move: falloff/stiffness down, verify the clamp patch.
- **Tie.settleTime** (magica 2.483 vs ref 1.7, high): Magica keeps ringing longer. `sd.damping` too low: `BulletToMagicaDamping` (cap 0.2, solver power 0.6, line ~71) may underestimate -- raise the cap or map from the authored ANGULAR damping too (hair is authored angular-damp ~2.0, capped to 1.0 in Bullet = very dead).
- **Tie.walkStreamDeg** (magica 36.8 vs ref 59.26, low): Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag.
- **Tie.walkRecoverySec** (magica 0.233 vs ref 0.0, high): Slow recovery after the stop: damping too low (keeps swinging outside the 10-deg band) or gravity too weak (MC2 clamp patch). If it oscillates through the band repeatedly, raise `sd.damping`; if it creeps back slowly, check gravity.
- **Tie.spinFlingAmp** (magica 0.1312 vs ref 0.2418, low): No centrifugal fling on the 360 spin. `worldInertia` too low (anchor rotation not converted into cloth inertia -- also verify the cloth GO is parented to the ANCHOR bone, line 317, not the static root), `rotationSpeedLimit` accidentally enabled (line 352 disables it), or the `particleSpeedLimit` MC2 clamp (unpatched = 0.26 m/s cap kills the fling).

## Clipping (magica only)

How deep a cloth bone ends up INSIDE one of the body's own kinematic rigid bodies. There is no reference column: Bullet does not let bodies interpenetrate, so the target is 0. The `dance` scenario (swinging legs/spine/head + a bounce) exists for this metric -- the other four never move a limb, so they cannot reproduce the failure the cloth is judged on in game.

| scenario | max depth (cm) | mean depth (cm) | frames with contact |
|---|---|---|---|
| rest | 0.00 | 0.00 | 0/240 |
| turn | 0.00 | 0.00 | 0/234 |
| walk | 0.00 | 0.00 | 0/330 |
| spin | 0.00 | 0.00 | 0/270 |
| dance | 0.16 | 0.01 | 28/360 |

## Rerun

```
python <repo>/tools/mmd_cloth_validate/compare.py
```
(Regenerate the Unity side first: `powershell -File <repo>/tools/mmd_cloth_validate/run_magica_probe.ps1` with the editor closed -- it builds a player and runs `dance.exe -mmdprobe`. Do NOT record via the PlayMode test: MC2 does not step under the test framework and every chain comes out perfectly rigid, which the Data Validity check above exists to catch.)
