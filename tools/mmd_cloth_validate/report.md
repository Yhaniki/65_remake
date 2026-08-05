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
| turnPeakAmpNorm | 1.7194 | 1.6617 | -3% | 0.6018 | PASS |
| turnLagMs | 150.0 | 83.3 | -44% | 150.0 | PASS |
| oscillations | 1 | 1 | +0% | 2 | PASS |
| walkStreamDeg | 53.67 | 52.15 | -3% | 18.78 | PASS |
| walkRecoverySec | 0.00 | 0.00 | inf | 0.15 | PASS |
| spinFlingAmp | 0.0075 | 0.0071 | -5% | 0.0200 | PASS |

### Dress_5

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 0.3084 | 0.3085 | +0% | 0.1079 | PASS |
| restDroopDeg | 53.30 | 31.92 | -40% | 18.65 | **FAIL** |
| settleTime | 3.52 | 0.98 | -72% | 1.23 | **FAIL** |
| turnPeakAmp | 0.0539 | 0.0016 | -97% | 0.0200 | **FAIL** |
| turnPeakAmpNorm | 0.1746 | 0.0051 | -97% | 0.0649 | **FAIL** |
| turnLagMs | 483.3 | 83.3 | -83% | 169.2 | **FAIL** |
| oscillations | 6 | 0 | -100% | 2 | **FAIL** |
| walkStreamDeg | 58.64 | 32.88 | -44% | 20.52 | **FAIL** |
| walkRecoverySec | 0.00 | 0.00 | inf | 0.15 | PASS |
| spinFlingAmp | 0.0667 | 0.0384 | -42% | 0.0233 | **FAIL** |

### RightTwicHairA

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 1.3990 | 1.3991 | +0% | 0.4896 | PASS |
| restDroopDeg | 31.80 | 15.87 | -50% | 11.13 | **FAIL** |
| settleTime | 1.93 | 3.88 | +101% | 0.68 | **FAIL** |
| turnPeakAmp | 1.1927 | 0.9173 | -23% | 0.4174 | PASS |
| turnPeakAmpNorm | 0.8525 | 0.6557 | -23% | 0.2984 | PASS |
| turnLagMs | 483.3 | 300.0 | -38% | 169.2 | **FAIL** |
| oscillations | 5 | 4 | -20% | 2 | PASS |
| walkStreamDeg | 32.09 | 20.15 | -37% | 11.23 | **FAIL** |
| walkRecoverySec | 0.18 | 0.00 | -100% | 0.15 | **FAIL** |
| spinFlingAmp | 0.2224 | 0.4957 | +123% | 0.0778 | **FAIL** |

### Tie

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 0.4299 | 0.4299 | +0% | 0.1505 | PASS |
| restDroopDeg | 19.90 | 8.59 | -57% | 6.96 | **FAIL** |
| settleTime | 1.70 | 1.75 | +3% | 0.59 | PASS |
| turnPeakAmp | 0.0215 | 0.0098 | -54% | 0.0200 | PASS |
| turnPeakAmpNorm | 0.0499 | 0.0229 | -54% | 0.0465 | PASS |
| turnLagMs | -133.3 | -66.7 | - | - (noise: chain not driven by this motion) | SKIP |
| oscillations | 0 | 0 | - | - (noise: chain not driven by this motion) | SKIP |
| walkStreamDeg | 59.26 | 28.55 | -52% | 20.74 | **FAIL** |
| walkRecoverySec | 0.00 | 0.00 | inf | 0.15 | PASS |
| spinFlingAmp | 0.2418 | 0.0941 | -61% | 0.0846 | **FAIL** |

**Summary: 20 PASS / 18 FAIL / 2 SKIP(noise) / 0 NOT-RUN**

| group | metrics | PASS |
|---|---|---|
| shape at rest | chainLenM, restDroopDeg, settleTime | 6 / 12 |
| behaviour in motion | the turn / walk / spin metrics | 14 / 26 |

## Suspect knobs

For each failing metric: the conversion parameter in `MmdMagicaCloth.cs` (<repo>/65/My project/Assets/Scripts/Game/MmdMagicaCloth.cs) that most directly controls it, and the direction to move it.

- **BangHairA.settleTime** (magica 0.0 vs ref 0.2, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **Dress_5.restDroopDeg** (magica 31.92 vs ref 53.3, low): Magica hangs MORE vertical than Bullet. The reference keeps part of the authored pose because MMD 0/0-LOCKED joint LIMITS are load-bearing (e.g. twintails: authored 47.9 deg -> Bullet only relaxes to ~32 deg). MmdMagicaCloth derives stiffness from joint rotation SPRINGS only and ignores locked limits (`useAngle = springMean > 0`), so spring-less chains become free pendulums and hang straight down. Fix: derive `angleRestorationConstraint` stiffness (or a nonzero `gravityFalloff`) from joint-limit tightness (acc.LimSum/LimN is already accumulated but unused for stiffness), not just from springs.
- **Dress_5.settleTime** (magica 0.983 vs ref 3.517, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **Dress_5.turnPeakAmp** (magica 0.0016 vs ref 0.0539, low): Chain barely swings out on the head turn. Primary knob: `worldInertia` too low (line 191: Lerp(0.7, 0.25, springNorm); low = tracks the anchor rigidly). Also check the speed clamps: `particleSpeedLimit = 8 m/s * unitsPerMeter` (~300 u/s) requires the MC2 MaxParticleSpeedLimit patch -- unpatched it clamps back to 10 u/s = 0.26 m/s and the chain physically cannot whip. Secondary: damping too high.
- **Dress_5.turnPeakAmpNorm** (magica 0.0051 vs ref 0.1746, low): no knob mapping
- **Dress_5.turnLagMs** (magica 83.3 vs ref 483.3, low): Tip peaks too early = chain stiffer/faster than Bullet. Angle restoration too strong (`angleStiff`), `worldInertia` too low (tip yanked with the head instead of lagging), or `movementInertiaSmoothing` (0.3, line 346) smoothing away the anchor acceleration spike.
- **Dress_5.oscillations** (magica 0 vs ref 6, low): Over-damped (or pinned): lower `sd.damping`, and check angle restoration / `gravityFalloff` are not killing the free swing.
- **Dress_5.walkStreamDeg** (magica 32.88 vs ref 58.64, low): Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag.
- **Dress_5.spinFlingAmp** (magica 0.0384 vs ref 0.0667, low): No centrifugal fling on the 360 spin. `worldInertia` too low (anchor rotation not converted into cloth inertia -- also verify the cloth GO is parented to the ANCHOR bone, line 317, not the static root), `rotationSpeedLimit` accidentally enabled (line 352 disables it), or the `particleSpeedLimit` MC2 clamp (unpatched = 0.26 m/s cap kills the fling).
- **RightTwicHairA.restDroopDeg** (magica 15.87 vs ref 31.8, low): Magica hangs MORE vertical than Bullet. The reference keeps part of the authored pose because MMD 0/0-LOCKED joint LIMITS are load-bearing (e.g. twintails: authored 47.9 deg -> Bullet only relaxes to ~32 deg). MmdMagicaCloth derives stiffness from joint rotation SPRINGS only and ignores locked limits (`useAngle = springMean > 0`), so spring-less chains become free pendulums and hang straight down. Fix: derive `angleRestorationConstraint` stiffness (or a nonzero `gravityFalloff`) from joint-limit tightness (acc.LimSum/LimN is already accumulated but unused for stiffness), not just from springs.
- **RightTwicHairA.settleTime** (magica 3.883 vs ref 1.933, high): Magica keeps ringing longer. `sd.damping` too low: `BulletToMagicaDamping` (cap 0.2, solver power 0.6, line ~71) may underestimate -- raise the cap or map from the authored ANGULAR damping too (hair is authored angular-damp ~2.0, capped to 1.0 in Bullet = very dead).
- **RightTwicHairA.turnLagMs** (magica 300.0 vs ref 483.3, low): Tip peaks too early = chain stiffer/faster than Bullet. Angle restoration too strong (`angleStiff`), `worldInertia` too low (tip yanked with the head instead of lagging), or `movementInertiaSmoothing` (0.3, line 346) smoothing away the anchor acceleration spike.
- **RightTwicHairA.walkStreamDeg** (magica 20.15 vs ref 32.09, low): Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag.
- **RightTwicHairA.walkRecoverySec** (magica 0.0 vs ref 0.183, low): Snaps back instantly = over-damped or pinned (angle restoration / gravityFalloff). Usually benign if ref is also ~0.
- **RightTwicHairA.spinFlingAmp** (magica 0.4957 vs ref 0.2224, high): Over-fling: `worldInertia` too high or `depthInertia` too low (line 194: 0.5*clamp(massGrad/5.7); raising it carries the root with the body and reins in the tip), or damping too low.
- **Tie.restDroopDeg** (magica 8.59 vs ref 19.9, low): Magica hangs MORE vertical than Bullet. The reference keeps part of the authored pose because MMD 0/0-LOCKED joint LIMITS are load-bearing (e.g. twintails: authored 47.9 deg -> Bullet only relaxes to ~32 deg). MmdMagicaCloth derives stiffness from joint rotation SPRINGS only and ignores locked limits (`useAngle = springMean > 0`), so spring-less chains become free pendulums and hang straight down. Fix: derive `angleRestorationConstraint` stiffness (or a nonzero `gravityFalloff`) from joint-limit tightness (acc.LimSum/LimN is already accumulated but unused for stiffness), not just from springs.
- **Tie.walkStreamDeg** (magica 28.55 vs ref 59.26, low): Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag.
- **Tie.spinFlingAmp** (magica 0.0941 vs ref 0.2418, low): No centrifugal fling on the 360 spin. `worldInertia` too low (anchor rotation not converted into cloth inertia -- also verify the cloth GO is parented to the ANCHOR bone, line 317, not the static root), `rotationSpeedLimit` accidentally enabled (line 352 disables it), or the `particleSpeedLimit` MC2 clamp (unpatched = 0.26 m/s cap kills the fling).

## Clipping (magica only)

How deep a cloth bone ends up INSIDE one of the body's own kinematic rigid bodies. There is no reference column: Bullet does not let bodies interpenetrate, so the target is 0. The `dance` scenario (swinging legs/spine/head + a bounce) exists for this metric -- the other four never move a limb, so they cannot reproduce the failure the cloth is judged on in game.

| scenario | max depth (cm) | mean depth (cm) | frames with contact |
|---|---|---|---|
| rest | 0.00 | 0.00 | 0/240 |
| turn | 0.00 | 0.00 | 0/234 |
| walk | 0.00 | 0.00 | 0/330 |
| spin | 0.00 | 0.00 | 0/270 |
| dance | 0.60 | 0.01 | 30/360 |

## Rerun

```
python <repo>/tools/mmd_cloth_validate/compare.py
```
(Regenerate the Unity side first: `powershell -File <repo>/tools/mmd_cloth_validate/run_magica_probe.ps1` with the editor closed -- it builds a player and runs `dance.exe -mmdprobe`. Do NOT record via the PlayMode test: MC2 does not step under the test framework and every chain comes out perfectly rigid, which the Data Validity check above exists to catch.)
