// Faithful C# port of the 4K orchestrator: UlbuBase.h (Bazoinkazoink) + Ulbu.h (TheGreatBazoinkazoinkInTheSky),
// flattened into one concrete class (we only ever run keycount == 4). Owns all pattern-mod objects, runs the
// hand-agnostic then hand-dependent pmod loops, and produces pmod_vals + init_base_diff_vals into the Calc.
using System;
using System.Collections.Generic;
using static Mina.col_type;
using static Mina.base_type;

namespace Mina
{
    public sealed class Ulbu4K : IUlbu
    {
        private readonly Calc _calc;
        private int hand = 0;

        // ---- agnostic meta (base Bazoinkazoink members) ----
        private readonly metaItvInfo _mitvi = new metaItvInfo();
        private metaRowInfo _mri;
        private metaRowInfo _last_mri;

        // ---- dependent meta (4K members) ----
        private readonly metaItvHandInfo _mitvhi = new metaItvHandInfo();
        private metaHandInfo _mhi;
        private metaHandInfo _last_mhi;
        private readonly SequencerGeneral _seq = new SequencerGeneral();

        // ---- pattern mods ----
        private readonly StreamMod _s = new StreamMod();
        private readonly JSMod _js = new JSMod();
        private readonly HSMod _hs = new HSMod();
        private readonly CJMod _cj = new CJMod();
        private readonly CJDensityMod _cjd = new CJDensityMod();
        private readonly HSDensityMod _hsd = new HSDensityMod();
        private readonly OHJumpModGuyThing _ohj = new OHJumpModGuyThing();
        private readonly CJOHJumpMod _cjohj = new CJOHJumpMod();
        private readonly RollMod _roll = new RollMod();
        private readonly RollJSMod _rolljs = new RollJSMod();
        private readonly BalanceMod _bal = new BalanceMod();
        private readonly OHTrillMod _oht = new OHTrillMod();
        private readonly VOHTrillMod _voht = new VOHTrillMod();
        private readonly ChaosMod _ch = new ChaosMod();
        private readonly CJOHAnchorMod _chain = new CJOHAnchorMod();
        private readonly RunningManMod _rm = new RunningManMod();
        private readonly MinijackMod _mj = new MinijackMod();
        private readonly WideRangeBalanceMod _wrb = new WideRangeBalanceMod();
        private readonly WideRangeRollMod _wrr = new WideRangeRollMod();
        private readonly WideRangeJumptrillMod _wrjt = new WideRangeJumptrillMod();
        private readonly WideRangeJJMod _wrjj = new WideRangeJJMod();
        private readonly WideRangeAnchorMod _wra = new WideRangeAnchorMod();
        private readonly FlamJamMod _fj = new FlamJamMod();
        private readonly TheThingLookerFinderThing _tt = new TheThingLookerFinderThing();
        private readonly TheThingLookerFinderThing2 _tt2 = new TheThingLookerFinderThing2();

        private readonly diffz _diffz = new diffz();

        // 4K pmods-per-skillset table (Ulbu.h)
        private readonly IReadOnlyList<int>[] _pmods_table;
        // 4K base scalers
        private readonly float[] _basescalers = { 0f, 0.91f, 0.75f, 0.77f, 0.93f, 1.01f, 1.06f, 1.06f };

        public Ulbu4K(Calc calc)
        {
            _calc = calc;
            _mri = new metaRowInfo(calc);
            _last_mri = new metaRowInfo(calc);
            _mhi = new metaHandInfo(calc);
            _last_mhi = new metaHandInfo(calc);

            static int P(CalcPatternMod m) => (int)m;
            _pmods_table = new IReadOnlyList<int>[(int)Skillset.NUM_Skillset];
            _pmods_table[(int)Skillset.Skill_Overall] = new int[0];
            _pmods_table[(int)Skillset.Skill_Stream] = new[] {
                P(CalcPatternMod.Stream), P(CalcPatternMod.OHTrill), P(CalcPatternMod.VOHTrill), P(CalcPatternMod.Roll),
                P(CalcPatternMod.WideRangeRoll), P(CalcPatternMod.WideRangeJumptrill), P(CalcPatternMod.WideRangeJJ), P(CalcPatternMod.FlamJam),
            };
            _pmods_table[(int)Skillset.Skill_Jumpstream] = new[] {
                P(CalcPatternMod.JS), P(CalcPatternMod.WideRangeBalance), P(CalcPatternMod.WideRangeJumptrill), P(CalcPatternMod.WideRangeJJ),
                P(CalcPatternMod.VOHTrill), P(CalcPatternMod.RollJS), P(CalcPatternMod.FlamJam),
            };
            _pmods_table[(int)Skillset.Skill_Handstream] = new[] {
                P(CalcPatternMod.HS), P(CalcPatternMod.OHJumpMod), P(CalcPatternMod.TheThing), P(CalcPatternMod.WideRangeRoll), P(CalcPatternMod.WideRangeJumptrill),
                P(CalcPatternMod.WideRangeJJ), P(CalcPatternMod.OHTrill), P(CalcPatternMod.VOHTrill), P(CalcPatternMod.FlamJam), P(CalcPatternMod.HSDensity),
            };
            _pmods_table[(int)Skillset.Skill_Stamina] = new int[0];
            _pmods_table[(int)Skillset.Skill_JackSpeed] = new int[0];
            _pmods_table[(int)Skillset.Skill_Chordjack] = new[] {
                P(CalcPatternMod.CJ), P(CalcPatternMod.WideRangeJumptrill), P(CalcPatternMod.VOHTrill), P(CalcPatternMod.FlamJam),
            };
            _pmods_table[(int)Skillset.Skill_Technical] = new[] {
                P(CalcPatternMod.OHTrill), P(CalcPatternMod.VOHTrill), P(CalcPatternMod.Balance), P(CalcPatternMod.Roll), P(CalcPatternMod.Chaos),
                P(CalcPatternMod.WideRangeJumptrill), P(CalcPatternMod.WideRangeJJ), P(CalcPatternMod.WideRangeBalance), P(CalcPatternMod.WideRangeRoll),
                P(CalcPatternMod.FlamJam), P(CalcPatternMod.Minijack), P(CalcPatternMod.TheThing), P(CalcPatternMod.TheThing2),
            };
        }

        public IReadOnlyList<int>[] GetPmods() => _pmods_table;
        public float[] GetBasescalers() => _basescalers;

        // ---- adj_diff_func (4K override) ----
        public void AdjDiffFunc(int itv, int hand, int ss, float adj_npsbase, float[] pmod_product_cur_interval)
        {
            int PMOD_HS = (int)Mina.CalcPatternMod.HS;
            int PMOD_OHJ = (int)Mina.CalcPatternMod.OHJumpMod;
            int PMOD_CJ = (int)Mina.CalcPatternMod.CJ;
            int NPS = (int)CalcDiffValue.NPSBase, CJB = (int)CalcDiffValue.CJBase, TB = (int)CalcDiffValue.TechBase;

            switch ((Skillset)ss)
            {
                case Skillset.Skill_Stream:
                    break;
                case Skillset.Skill_Jumpstream:
                {
                    float adj_diff = _calc.base_adj_diff[hand][ss][itv];
                    adj_diff /= Math.Max(_calc.pmod_vals[hand][PMOD_HS][itv], 1f);
                    adj_diff /= M.fastsqrt(_calc.pmod_vals[hand][PMOD_OHJ][itv] * 0.95f);
                    float a = adj_diff;
                    float b = _calc.init_base_diff_vals[hand][NPS][itv] *
                              pmod_product_cur_interval[(int)Skillset.Skill_Handstream];
                    _calc.base_adj_diff[hand][ss][itv] = adj_diff;
                    _calc.base_diff_for_stam_mod[hand][ss][itv] = Math.Max(a, b);
                    break;
                }
                case Skillset.Skill_Handstream:
                {
                    float a = adj_npsbase;
                    float b = _calc.init_base_diff_vals[hand][NPS][itv] *
                              pmod_product_cur_interval[(int)Skillset.Skill_Jumpstream];
                    _calc.base_diff_for_stam_mod[hand][ss][itv] = Math.Max(a, b);
                    break;
                }
                case Skillset.Skill_JackSpeed:
                    break;
                case Skillset.Skill_Chordjack:
                    _calc.base_adj_diff[hand][ss][itv] =
                        _calc.init_base_diff_vals[hand][CJB][itv] *
                        _basescalers[(int)Skillset.Skill_Chordjack] *
                        pmod_product_cur_interval[(int)Skillset.Skill_Chordjack];
                    break;
                case Skillset.Skill_Technical:
                {
                    float adj_diff =
                        _calc.init_base_diff_vals[hand][TB][itv] *
                        pmod_product_cur_interval[ss] * _basescalers[ss] /
                        Math.Max(M.fastpow(_calc.pmod_vals[hand][PMOD_CJ][itv] + 0.05f, 2f), 1f);
                    adj_diff *= M.fastsqrt(_calc.pmod_vals[hand][PMOD_OHJ][itv]);
                    _calc.base_adj_diff[hand][ss][itv] = adj_diff;
                    break;
                }
            }
        }

        // ---- main driver: operator()() ----
        public void Run()
        {
            reset_base_diffs();
            hand = 0;

            full_hand_reset();
            full_agnostic_reset();
            reset_row_sequencing();

            run_agnostic_pmod_loop();
            run_dependent_pmod_loop();
        }

        private void reset_base_diffs()
        {
            foreach (var h in Hands.both_hands)
            {
                var v = _calc.init_base_diff_vals[h][(int)CalcDiffValue.TechBase];
                Array.Clear(v, 0, v.Length);
                _calc.jack_diff[h].Clear();
            }
        }

        private void reset_row_sequencing() => _mitvi.reset();

        // ---- agnostic pmod loop (base) with 4K overrides ----
        private void full_agnostic_reset()
        {
            _s.full_reset();
            _js.full_reset();
            _hs.full_reset();
            _cj.full_reset();
            _mri.reset();
            _last_mri.reset();
        }

        private void setup_agnostic_pmods()
        {
            _s.setup();
            _fj.setup();
            _tt.setup();
            _tt2.setup();
        }

        private void advance_agnostic_sequencing()
        {
            _s.advance_sequencing(_mri.ms_now, _mri.notes);
            _fj.advance_sequencing(_mri.ms_now, _mri.notes);
            _tt.advance_sequencing(_mri.ms_now, _mri.notes);
            _tt2.advance_sequencing(_mri.ms_now, _mri.notes);
        }

        private void set_agnostic_pmods(int itv)
        {
            PatternMods.set_agnostic(_s._pmod, _s.op(_mitvi), itv, _calc);
            PatternMods.set_agnostic(_js._pmod, _js.op(_mitvi), itv, _calc);
            PatternMods.set_agnostic(_hs._pmod, _hs.op(_mitvi), itv, _calc);
            PatternMods.set_agnostic(_cj._pmod, _cj.op(_mitvi), itv, _calc);
            PatternMods.set_agnostic(_cjd._pmod, _cjd.op(_mitvi), itv, _calc);
            PatternMods.set_agnostic(_hsd._pmod, _hsd.op(_mitvi), itv, _calc);
            PatternMods.set_agnostic(_fj._pmod, _fj.op(), itv, _calc);
            PatternMods.set_agnostic(_tt._pmod, _tt.op(), itv, _calc);
            PatternMods.set_agnostic(_tt2._pmod, _tt2.op(), itv, _calc);
        }

        private void run_agnostic_pmod_loop()
        {
            setup_agnostic_pmods();

            for (int itv = 0; itv < _calc.numitv; ++itv)
            {
                for (int row = 0; row < _calc.itv_size[itv]; ++row)
                {
                    ref var ri = ref _calc.adj_ni[itv][row];
                    _mri.op(_last_mri, _mitvi, ri.row_time, ri.row_count, ri.row_notes);
                    advance_agnostic_sequencing();
                    (_mri, _last_mri) = (_last_mri, _mri);   // swap: now becomes last
                }
                set_agnostic_pmods(itv);
                _mitvi.handle_interval_end();
            }

            PatternMods.run_agnostic_smoothing_pass(_calc.numitv, _calc);
            PatternMods.bruh_they_the_same(_calc.numitv, _calc);
        }

        // ---- dependent pmod loop (4K) ----
        private void full_hand_reset()
        {
            _ohj.full_reset();
            _chain.full_reset();
            _cjohj.full_reset();
            _bal.full_reset();
            _roll.full_reset();
            _rolljs.full_reset();
            _oht.full_reset();
            _voht.full_reset();
            _ch.full_reset();
            _rm.full_reset();
            _wrr.full_reset();
            _wrjt.full_reset();
            _wrjj.full_reset();
            _wrb.full_reset();
            _wra.full_reset();
            _mj.full_reset();

            _seq.full_reset();
            _mitvhi.zero();
            _mhi.full_reset();
            _last_mhi.full_reset();
            _diffz.full_reset();
        }

        private void setup_dependent_mods()
        {
            _oht.setup();
            _voht.setup();
            _roll.setup();
            _rolljs.setup();
            _rm.setup();
            _wrr.setup();
            _wrjt.setup();
            _wrjj.setup();
            _wrb.setup();
            _wra.setup();
        }

        private void handle_row_dependent_pattern_advancement(float row_time)
        {
            _ohj.advance_sequencing(_mhi._ct, _mhi._bt);
            _cjohj.advance_sequencing(_mhi._ct, _mhi._bt);
            _chain.advance_sequencing(_mhi._ct, _mhi._bt, _mhi._last_ct, _seq._mw_any_ms.get_now());
            _oht.advance_sequencing(_mhi._mt, _seq._mw_any_ms);
            _voht.advance_sequencing(_mhi._mt, _seq._mw_any_ms);
            _rm.advance_sequencing(_mhi._ct, _mhi._bt, _mhi._mt, _seq._as);
            _wrr.advance_sequencing(_mhi._bt, _mhi._mt, _mhi._last_mt,
                                    _seq._mw_any_ms.get_now(), _seq.get_sc_ms_now(_mhi._ct));
            _wrjt.advance_sequencing(_mhi._bt, _mhi._mt, _mhi._last_mt, _seq._mw_any_ms);
            _wrjj.advance_sequencing(_mhi._ct, row_time);
            _ch.advance_sequencing(_seq._mw_any_ms);
            _roll.advance_sequencing(_mhi._ct, row_time);
            _rolljs.advance_sequencing(_mhi._ct, row_time);
            _mj.advance_sequencing(_mhi._ct, _seq.get_sc_ms_now(_mhi._ct));
        }

        private void set_dependent_pmods(int itv)
        {
            PatternMods.set_dependent(hand, _ohj._pmod, _ohj.op(_mitvhi), itv, _calc);
            PatternMods.set_dependent(hand, _chain._pmod, _chain.op(_mitvhi), itv, _calc);
            PatternMods.set_dependent(hand, _cjohj._pmod, _cjohj.op(_mitvhi), itv, _calc);
            PatternMods.set_dependent(hand, _oht._pmod, _oht.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _voht._pmod, _voht.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _bal._pmod, _bal.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _roll._pmod, _roll.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _rolljs._pmod, _rolljs.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _ch._pmod, _ch.op(_mitvhi._itvhi.get_taps_nowi()), itv, _calc);
            PatternMods.set_dependent(hand, _rm._pmod, _rm.op(_mitvhi._itvhi.get_taps_nowi()), itv, _calc);
            PatternMods.set_dependent(hand, _wrb._pmod, _wrb.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _wrr._pmod, _wrr.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _wrjt._pmod, _wrjt.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _wrjj._pmod, _wrjj.op(_mitvhi._itvhi), itv, _calc);
            PatternMods.set_dependent(hand, _wra._pmod, _wra.op(_mitvhi._itvhi, _seq._as), itv, _calc);
            PatternMods.set_dependent(hand, _mj._pmod, _mj.op(_mitvhi._itvhi), itv, _calc);
        }

        private void update_sequenced_base_diffs(col_type ct, int itv, int jack_counter, float row_time, float any_ms)
        {
            float second = M.ms_to_scaled_nps(_seq._as.get_lowest_jack_ms()) *
                           _basescalers[(int)Skillset.Skill_JackSpeed];
            if (float.IsNaN(second)) second = 0f;
            _calc.jack_diff[hand].Add((row_time, second));

            // (debug cv block skipped)

            _diffz._cj.advance_base(any_ms, _calc);

            _diffz._tc.advance_base(_seq, ct, _calc, hand, row_time);
            _diffz._tc.advance_rm_comp(_rm.get_highest_anchor_difficulty());
            _diffz._tc.advance_jack_comp(_seq._as.get_lowest_jack_ms());
        }

        private void set_sequenced_base_diffs(int itv)
        {
            _calc.init_base_diff_vals[hand][(int)CalcDiffValue.JackBase][itv] = _diffz._tc.get_itv_jack_diff();
            _calc.init_base_diff_vals[hand][(int)CalcDiffValue.CJBase][itv] = _diffz._cj.get_itv_diff(_calc);
            _calc.init_base_diff_vals[hand][(int)CalcDiffValue.TechBase][itv] =
                _diffz._tc.get_itv_diff(_calc.init_base_diff_vals[hand][(int)CalcDiffValue.NPSBase][itv], _calc);
            _calc.init_base_diff_vals[hand][(int)CalcDiffValue.RMABase][itv] = _diffz._tc.get_itv_rma_diff();
        }

        private void handle_dependent_interval_end(int itv)
        {
            _mitvhi.interval_end();
            _seq.interval_end();
            set_dependent_pmods(itv);
            set_sequenced_base_diffs(itv);
            _diffz.interval_end();
        }

        private void run_dependent_pmod_loop()
        {
            setup_dependent_mods();

            foreach (var ids in _calc.hand_col_masks)
            {
                float row_time = M.s_init;
                float last_row_time = M.s_init;
                float any_ms = M.ms_init;
                uint row_notes = 0u;
                col_type ct = col_init;

                full_hand_reset();
                _calc.jack_diff[hand].Clear();

                nps.actual_cancer(_calc, hand);

                M.Smooth(_calc.init_base_diff_vals[hand][(int)CalcDiffValue.NPSBase], 0f, _calc.numitv);
                M.MSSmooth(_calc.init_base_diff_vals[hand][(int)CalcDiffValue.MSBase], 0f, _calc.numitv);

                for (int itv = 0; itv < _calc.numitv; ++itv)
                {
                    int jack_counter = 0;
                    for (int row = 0; row < _calc.itv_size[itv]; ++row)
                    {
                        ref var ri = ref _calc.adj_ni[itv][row];
                        row_time = ri.row_time;
                        row_notes = ri.row_notes;
                        int row_count = ri.row_count;

                        any_ms = M.ms_from(row_time, last_row_time);

                        ct = HD.determine_col_type(row_notes, ids);

                        if (ct == col_empty)
                        {
                            _rm.advance_off_hand_sequencing();
                            _mj.advance_off_hand_sequencing();
                            if (row_count == 2)
                                _rm.advance_off_hand_sequencing();
                            continue;
                        }

                        _diffz._cj.update_flags(row_notes & ids, M.column_count(row_notes & ids));

                        _seq.advance_sequencing(ct, row_time, any_ms);

                        _mhi.op(_last_mhi, ct);

                        _mitvhi._itvhi.set_col_taps(ct);

                        handle_row_dependent_pattern_advancement(row_time);

                        update_sequenced_base_diffs(ct, itv, jack_counter, row_time, any_ms);
                        ++jack_counter;

                        if (_mhi._bt != base_type_init)
                        {
                            ++_mitvhi._base_types[(int)_mhi._bt];
                            ++_mitvhi._meta_types[(int)_mhi._mt];
                        }

                        (_mhi, _last_mhi) = (_last_mhi, _mhi);   // swap
                        last_row_time = row_time;
                    }

                    handle_dependent_interval_end(itv);
                }
                PatternMods.run_dependent_smoothing_pass(_calc.numitv, _calc);

                ++hand;
            }

            nps.grindscale(_calc);
        }
    }
}
