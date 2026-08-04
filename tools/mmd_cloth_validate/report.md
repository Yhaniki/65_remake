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
| turnPeakAmp | 0.1760 | 0.1793 | +2% | 0.0616 | PASS |
| turnPeakAmpNorm | 1.7194 | 1.7514 | +2% | 0.6018 | PASS |
| turnLagMs | 150.0 | 66.7 | -56% | 150.0 | PASS |
| oscillations | 1 | 2 | +100% | 2 | PASS |
| walkStreamDeg | 53.67 | 59.87 | +12% | 18.78 | PASS |
| walkRecoverySec | 0.00 | 0.00 | inf | 0.15 | PASS |
| spinFlingAmp | 0.0075 | 0.0161 | +115% | 0.0200 | PASS |

### Dress_5

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 0.3084 | 0.3085 | +0% | 0.1079 | PASS |
| restDroopDeg | 53.30 | 32.63 | -39% | 18.65 | **FAIL** |
| settleTime | 3.52 | 1.07 | -70% | 1.23 | **FAIL** |
| turnPeakAmp | 0.0539 | 0.0005 | -99% | 0.0200 | **FAIL** |
| turnPeakAmpNorm | 0.1746 | 0.0015 | -99% | 0.0649 | **FAIL** |
| turnLagMs | 483.3 | -83.3 | -117% | 169.2 | **FAIL** |
| oscillations | 6 | 0 | -100% | 2 | **FAIL** |
| walkStreamDeg | 58.64 | 36.28 | -38% | 20.52 | **FAIL** |
| walkRecoverySec | 0.00 | 0.20 | inf | 0.15 | **FAIL** |
| spinFlingAmp | 0.0667 | 0.0401 | -40% | 0.0233 | **FAIL** |

### RightTwicHairA

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 1.3990 | 1.3993 | +0% | 0.4896 | PASS |
| restDroopDeg | 31.80 | 8.16 | -74% | 11.13 | **FAIL** |
| settleTime | 1.93 | never | - | - (one side never settled/recovered) | **FAIL** |
| turnPeakAmp | 1.1927 | 0.6553 | -45% | 0.4174 | **FAIL** |
| turnPeakAmpNorm | 0.8525 | 0.4683 | -45% | 0.2984 | **FAIL** |
| turnLagMs | 483.3 | 1083.3 | +124% | 169.2 | **FAIL** |
| oscillations | 5 | 5 | +0% | 2 | PASS |
| walkStreamDeg | 32.09 | 15.22 | -53% | 11.23 | **FAIL** |
| walkRecoverySec | 0.18 | 0.00 | -100% | 0.15 | **FAIL** |
| spinFlingAmp | 0.2224 | 0.0000 | -100% | 0.0778 | **FAIL** |

### Tie

| metric | ref | magica | rel.err | tol | verdict |
|---|---|---|---|---|---|
| chainLenM | 0.4299 | 0.4299 | +0% | 0.1505 | PASS |
| restDroopDeg | 19.90 | 20.50 | +3% | 6.96 | PASS |
| settleTime | 1.70 | 3.30 | +94% | 0.59 | **FAIL** |
| turnPeakAmp | 0.0215 | 0.0827 | +285% | 0.0200 | **FAIL** |
| turnPeakAmpNorm | 0.0499 | 0.1924 | +286% | 0.0465 | **FAIL** |
| turnLagMs | -133.3 | 16.7 | - | - (noise: chain not driven by this motion) | SKIP |
| oscillations | 0 | 3 | - | - (noise: chain not driven by this motion) | SKIP |
| walkStreamDeg | 59.26 | 45.25 | -24% | 20.74 | PASS |
| walkRecoverySec | 0.00 | 0.92 | inf | 0.15 | **FAIL** |
| spinFlingAmp | 0.2418 | 0.2144 | -11% | 0.0846 | PASS |

**Summary: 16 PASS / 22 FAIL / 2 SKIP(noise) / 0 NOT-RUN**

## Suspect knobs

For each failing metric: the conversion parameter in `MmdMagicaCloth.cs` (H:/65_remake-mmd/65/My project/Assets/Scripts/Game/MmdMagicaCloth.cs) that most directly controls it, and the direction to move it.

- **BangHairA.settleTime** (magica 0.0 vs ref 0.2, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **Dress_5.restDroopDeg** (magica 32.63 vs ref 53.3, low): Magica hangs MORE vertical than Bullet. The reference keeps part of the authored pose because MMD 0/0-LOCKED joint LIMITS are load-bearing (e.g. twintails: authored 47.9 deg -> Bullet only relaxes to ~32 deg). MmdMagicaCloth derives stiffness from joint rotation SPRINGS only and ignores locked limits (`useAngle = springMean > 0`), so spring-less chains become free pendulums and hang straight down. Fix: derive `angleRestorationConstraint` stiffness (or a nonzero `gravityFalloff`) from joint-limit tightness (acc.LimSum/LimN is already accumulated but unused for stiffness), not just from springs.
- **Dress_5.settleTime** (magica 1.067 vs ref 3.517, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **Dress_5.turnPeakAmp** (magica 0.0005 vs ref 0.0539, low): Chain barely swings out on the head turn. Primary knob: `worldInertia` too low (line 191: Lerp(0.7, 0.25, springNorm); low = tracks the anchor rigidly). Also check the speed clamps: `particleSpeedLimit = 8 m/s * unitsPerMeter` (~300 u/s) requires the MC2 MaxParticleSpeedLimit patch -- unpatched it clamps back to 10 u/s = 0.26 m/s and the chain physically cannot whip. Secondary: damping too high.
- **Dress_5.turnPeakAmpNorm** (magica 0.0015 vs ref 0.1746, low): no knob mapping
- **Dress_5.turnLagMs** (magica -83.3 vs ref 483.3, low): Tip peaks too early = chain stiffer/faster than Bullet. Angle restoration too strong (`angleStiff`), `worldInertia` too low (tip yanked with the head instead of lagging), or `movementInertiaSmoothing` (0.3, line 346) smoothing away the anchor acceleration spike.
- **Dress_5.oscillations** (magica 0 vs ref 6, low): Over-damped (or pinned): lower `sd.damping`, and check angle restoration / `gravityFalloff` are not killing the free swing.
- **Dress_5.walkStreamDeg** (magica 36.28 vs ref 58.64, low): Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag.
- **Dress_5.walkRecoverySec** (magica 0.2 vs ref 0.0, high): Slow recovery after the stop: damping too low (keeps swinging outside the 10-deg band) or gravity too weak (MC2 clamp patch). If it oscillates through the band repeatedly, raise `sd.damping`; if it creeps back slowly, check gravity.
- **Dress_5.spinFlingAmp** (magica 0.0401 vs ref 0.0667, low): No centrifugal fling on the 360 spin. `worldInertia` too low (anchor rotation not converted into cloth inertia -- also verify the cloth GO is parented to the ANCHOR bone, line 317, not the static root), `rotationSpeedLimit` accidentally enabled (line 352 disables it), or the `particleSpeedLimit` MC2 clamp (unpatched = 0.26 m/s cap kills the fling).
- **RightTwicHairA.restDroopDeg** (magica 8.16 vs ref 31.8, low): Magica hangs MORE vertical than Bullet. The reference keeps part of the authored pose because MMD 0/0-LOCKED joint LIMITS are load-bearing (e.g. twintails: authored 47.9 deg -> Bullet only relaxes to ~32 deg). MmdMagicaCloth derives stiffness from joint rotation SPRINGS only and ignores locked limits (`useAngle = springMean > 0`), so spring-less chains become free pendulums and hang straight down. Fix: derive `angleRestorationConstraint` stiffness (or a nonzero `gravityFalloff`) from joint-limit tightness (acc.LimSum/LimN is already accumulated but unused for stiffness), not just from springs.
- **RightTwicHairA.settleTime** (magica None vs ref 1.933, low): Magica settles too fast = over-damped. `sd.damping` too high (per-substep damping at 150 Hz compounds hard -- check BulletToMagicaDamping and the dampTip curve, line ~331), or angle restoration/`gravityFalloff` pinning the chain so it never swings at all.
- **RightTwicHairA.turnPeakAmp** (magica 0.6553 vs ref 1.1927, low): Chain barely swings out on the head turn. Primary knob: `worldInertia` too low (line 191: Lerp(0.7, 0.25, springNorm); low = tracks the anchor rigidly). Also check the speed clamps: `particleSpeedLimit = 8 m/s * unitsPerMeter` (~300 u/s) requires the MC2 MaxParticleSpeedLimit patch -- unpatched it clamps back to 10 u/s = 0.26 m/s and the chain physically cannot whip. Secondary: damping too high.
- **RightTwicHairA.turnPeakAmpNorm** (magica 0.4683 vs ref 0.8525, low): no knob mapping
- **RightTwicHairA.turnLagMs** (magica 1083.3 vs ref 483.3, high): Tip peaks too late = pendulum too slow. Pendulum period ~ sqrt(L/g): gravity effectively too weak -- verify the MC2 gravity<=20 clamp patch is live (`sd.gravity = 9.8*upm` ~375 u/s^2); or damping too high (peak dragged later).
- **RightTwicHairA.walkStreamDeg** (magica 15.22 vs ref 32.09, low): Chain does not stream backward during the walk. `worldInertia` too low (reference frame drags the cloth along = no apparent wind), or `movementSpeedLimit` re-enabled / `particleSpeedLimit` still clamped to the stock 10 u/s (MC2 patch missing) so particles cannot keep the lag.
- **RightTwicHairA.walkRecoverySec** (magica 0.0 vs ref 0.183, low): Snaps back instantly = over-damped or pinned (angle restoration / gravityFalloff). Usually benign if ref is also ~0.
- **RightTwicHairA.spinFlingAmp** (magica 0.0 vs ref 0.2224, low): No centrifugal fling on the 360 spin. `worldInertia` too low (anchor rotation not converted into cloth inertia -- also verify the cloth GO is parented to the ANCHOR bone, line 317, not the static root), `rotationSpeedLimit` accidentally enabled (line 352 disables it), or the `particleSpeedLimit` MC2 clamp (unpatched = 0.26 m/s cap kills the fling).
- **Tie.settleTime** (magica 3.3 vs ref 1.7, high): Magica keeps ringing longer. `sd.damping` too low: `BulletToMagicaDamping` (cap 0.2, solver power 0.6, line ~71) may underestimate -- raise the cap or map from the authored ANGULAR damping too (hair is authored angular-damp ~2.0, capped to 1.0 in Bullet = very dead).
- **Tie.turnPeakAmp** (magica 0.0827 vs ref 0.0215, high): Chain overswings vs Bullet. `worldInertia` too high (lower the 0.7 pendulum end, line 191), damping too low, or missing limit-derived angle stiffness (Bullet's locked joint limits resist the swing; Magica has restoration OFF for spring-less chains).
- **Tie.turnPeakAmpNorm** (magica 0.1924 vs ref 0.0499, high): no knob mapping
- **Tie.walkRecoverySec** (magica 0.917 vs ref 0.0, high): Slow recovery after the stop: damping too low (keeps swinging outside the 10-deg band) or gravity too weak (MC2 clamp patch). If it oscillates through the band repeatedly, raise `sd.damping`; if it creeps back slowly, check gravity.

## Rerun

```
python H:/65_remake-mmd/tools/mmd_cloth_validate/compare.py
```
(Regenerate the Unity side first if needed: `powershell -File H:/65_remake-mmd/tools/mmd_cloth_validate/run_magica_probe.ps1` with the editor closed, or Test Runner > PlayMode > Sdo.Tests.MmdClothProbe with it open.)
