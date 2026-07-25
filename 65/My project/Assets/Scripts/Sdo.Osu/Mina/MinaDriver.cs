// Faithful C# port of MinaCalc.cpp (v515) driver: CalcMain, Chisel, CalcInternal, StamAdjust, JackStamAdjust,
// jackloss, InitializeHands, InitAdjDiff, TotalMaxPoints, and the MinaSDCalc entry point. Reads against the Calc data
// model (MinaCommon), the M helpers (MinaMath), FastWalk (MinaAcolytes), and the IUlbu seam (implemented by the Ulbu).
using System;
using System.Collections.Generic;

namespace Mina
{
    public sealed partial class Calc
    {
        // point buffer multipliers (MinaCalc.cpp). do not set below 1.
        private const float tech_pbm = 1f;
        private const float jack_pbm = 1.0175f;
        private const float stream_pbm = 1.01f;
        private const float bad_newbie_skillsets_pbm = 1f;

        public const int mina_calc_version = 515;

        private static float TotalMaxPoints(Calc calc)
        {
            int mp = 0;
            for (int i = 0; i < calc.numitv; i++)
                mp += calc.itv_points[Hands.left_hand][i] + calc.itv_points[Hands.right_hand][i];
            return mp;
        }

        private void InitializeKeycountLogic()
        {
            // 4K only: the great bazoinkazoink in the sky.
            if (ulbu_in_charge == null)
                ulbu_in_charge = new Ulbu4K(this);
        }

        public float[] CalcMain(IReadOnlyList<NoteInfo> NoteInfo, float music_rate, float score_goal)
        {
            InitializeKeycountLogic();
            var basescalers = ulbu_in_charge.GetBasescalers();

            const int num_offset_passes = 1;
            var all_skillset_values = new List<float[]>(num_offset_passes);

            for (int cur_iteration = 0; cur_iteration < num_offset_passes; ++cur_iteration)
            {
                bool skip = InitializeHands(NoteInfo, music_rate, 0.1f * cur_iteration);
                if (skip)
                    return M.DimplesAllZero();

                MaxPoints = TotalMaxPoints(this);
                int NUM = (int)Skillset.NUM_Skillset;
                var isv = new float[NUM];

                // overall and stam left 0 by this loop
                for (int i = 0; i < NUM; ++i)
                    isv[i] = Chisel(0.1f, 10.24f, score_goal, (Skillset)i, false);

                int highest_base_skillset = M.max_index(isv);
                float @base = isv[highest_base_skillset];

                for (int i = 0; i < NUM; ++i)
                    if (isv[i] > @base * 0.9f)
                        isv[i] = Chisel(isv[i] * 0.9f, 0.32f, score_goal, (Skillset)i, true);

                int highest_stam_adjusted_skillset = M.max_index(isv);

                float highest_stam_adj_ss_value = isv[highest_base_skillset];
                if (highest_stam_adjusted_skillset == (int)Skillset.Skill_JackSpeed)
                    highest_stam_adj_ss_value *= 0.8f;

                const float stam_curve_shift = 0.015f;
                float stam_adj_mult = (float)Math.Pow((highest_stam_adj_ss_value / @base) - stam_curve_shift, 2.5f);
                stam_adj_mult = Math.Clamp(stam_adj_mult, 0.8f, 1.08f);
                isv[(int)Skillset.Skill_Stamina] =
                    highest_stam_adj_ss_value * stam_adj_mult * basescalers[(int)Skillset.Skill_Stamina];

                // (debugmode block skipped)

                // keycount == 4 → no keymode scaling

                if (ssr)
                {
                    const float ssrcap = 40f;
                    for (int k = 0; k < NUM; k++)
                    {
                        float r = isv[k];
                        r = M.downscale_low_accuracy_scores(r, score_goal);
                        r = Math.Min(r, ssrcap);
                        if (highest_stam_adjusted_skillset == (int)Skillset.Skill_JackSpeed)
                            r = M.downscale_low_accuracy_scores(r, score_goal);
                        isv[k] = r;
                    }
                }

                float agg = M.aggregate_skill(isv, 0.25, 1.11f, 0.0f, 10.24f);
                float highest = M.max_val(isv);
                isv[(int)Skillset.Skill_Overall] = agg > highest ? agg : highest;

                all_skillset_values.Add(isv);
            }

            // final output = mean of all iterations (only 1)
            int n = (int)Skillset.NUM_Skillset;
            var output = new float[n];
            for (int i = 0; i < n; ++i)
            {
                float s = 0f;
                foreach (var vals in all_skillset_values) s += vals[i];
                output[i] = s / all_skillset_values.Count;
            }

            // lighten grindscaler for jack files only
            Skillset highest_final_ss = Skillset.Skill_Overall;
            float highest_final_ssv = -1f;
            for (int i = 0; i < output.Length; i++)
            {
                if (i == (int)Skillset.Skill_Overall) continue;
                if (output[i] > highest_final_ssv) { highest_final_ss = (Skillset)i; highest_final_ssv = output[i]; }
            }
            if (ssr)
            {
                if (highest_final_ss == Skillset.Skill_JackSpeed || highest_final_ss == Skillset.Skill_Chordjack)
                    grindscaler = M.fastsqrt(grindscaler);
                for (int i = 0; i < output.Length; i++) output[i] *= grindscaler;
            }
            return output;
        }

        // ---- stamina model ----
        private static void StamAdjust(float x, int ss, Calc calc, int hand, bool debug = false)
        {
            const float stam_ceil = 1.075234f;
            const float stam_mag = 243f;
            const float stam_fscale = 500f;
            const float stam_prop = 0.69424f;

            float stam_floor = 0.95f;
            float mod = 0.95f;

            float avs1;
            float avs2 = 0f;
            float local_ceil;
            const float super_stam_ceil = 1.09f;

            float[] base_diff = calc.base_diff_for_stam_mod[hand][ss];
            float[] diff = calc.base_adj_diff[hand][ss];

            for (int i = 0; i < calc.numitv; i++)
            {
                avs1 = avs2;
                avs2 = base_diff[i];
                mod += ((((avs1 + avs2) / 2f) / (stam_prop * x)) - 1f) / stam_mag;
                if (mod > 0.95f)
                    stam_floor += (mod - 0.95f) / stam_fscale;
                local_ceil = stam_ceil * stam_floor;

                mod = Math.Min(Math.Clamp(mod, stam_floor, local_ceil), super_stam_ceil);
                calc.stam_adj_diff[i] = diff[i] * mod;
            }
        }

        private static List<(float first, float second)> JackStamAdjust(float x, Calc calc, int hand)
        {
            const float stam_ceil = 1.05234f;
            const float stam_mag = 23f;
            const float stam_fscale = 750f;
            const float stam_prop = 0.49424f;
            float stam_floor = 0.95f;
            float mod = 1f;
            float avs2 = 0f;
            const float super_stam_ceil = 1.01f;

            var diff = calc.jack_diff[hand];
            var output = new (float first, float second)[diff.Count];

            for (int i = 0; i < diff.Count; i++)
            {
                float avs1 = avs2;
                avs2 = diff[i].second;
                mod += ((((avs1 + avs2) / 2f) / (stam_prop * x)) - 1f) / stam_mag;
                if (mod > 0.95f)
                    stam_floor += (mod - 0.95f) / stam_fscale;
                float local_ceil = stam_ceil * stam_floor;

                mod = Math.Min(Math.Clamp(mod, stam_floor, local_ceil), super_stam_ceil);
                output[i].first = diff[i].first;
                output[i].second = diff[i].second * mod;
            }
            var list = new List<(float first, float second)>(output.Length);
            list.AddRange(output);
            return list;
        }

        private const float magic_num = 12f;
        private static float jack_pointloser_func(float x, float y)
            => Math.Max((float)(magic_num * M.Erf(0.04f * (y - x))), 0f);

        private static float jackloss(float x, Calc calc, int hand, bool stam)
        {
            var v = stam ? JackStamAdjust(x, calc, hand) : calc.jack_diff[hand];
            float total = 0f;
            foreach (var y in v)
                if (x < y.second && y.second > 0f)
                    total += jack_pointloser_func(x, y.second);
            return total;
        }

        private static void CalcInternal(ref float gotpoints, float x, int ss, bool stam, Calc calc, int hand)
        {
            if (stam)
                StamAdjust(x, ss, calc, hand);

            float[] v = stam ? calc.stam_adj_diff : calc.base_adj_diff[hand][ss];
            float pointloss_pow_val = 1.7f;
            if (ss == (int)Skillset.Skill_Chordjack) pointloss_pow_val = 1.7f;
            else if (ss == (int)Skillset.Skill_Technical) pointloss_pow_val = 2f;

            for (int i = 0; i < calc.numitv; ++i)
            {
                if (x < v[i])
                {
                    float pts = calc.itv_points[hand][i];
                    gotpoints -= (pts - (pts * M.fastpow(x / v[i], pointloss_pow_val)));
                }
            }
        }

        private bool InitializeHands(IReadOnlyList<NoteInfo> NoteInfo, float music_rate, float offset)
        {
            if (FastWalk.fast_walk_and_check_for_skip(NoteInfo, music_rate, this, offset))
                return true;

            // (debugmode || loadparams) param reload + reset skipped — never enabled.

            ulbu_in_charge.Run();

            foreach (var hand in Hands.both_hands)
                InitAdjDiff(this, hand);

            return false;
        }

        private static void InitAdjDiff(Calc calc, int hand)
        {
            var pmods_used = calc.ulbu_in_charge.GetPmods();
            var basescalers = calc.ulbu_in_charge.GetBasescalers();
            int NUM = (int)Skillset.NUM_Skillset;
            var pmod_product_cur_interval = new float[NUM];

            for (int i = 0; i < calc.numitv; ++i)
            {
                for (int s = 0; s < NUM; s++) pmod_product_cur_interval[s] = 1f;

                for (int ss = 0; ss < NUM; ++ss)
                {
                    if (ss == (int)Skillset.Skill_Overall || ss == (int)Skillset.Skill_Stamina) continue;
                    foreach (var pmod in pmods_used[ss])
                        pmod_product_cur_interval[ss] *= calc.pmod_vals[hand][pmod][i];
                }

                for (int ss = 0; ss < NUM; ++ss)
                {
                    if (ss == (int)Skillset.Skill_Overall || ss == (int)Skillset.Skill_Stamina) continue;

                    float adj_npsbase =
                        calc.init_base_diff_vals[hand][(int)CalcDiffValue.NPSBase][i] *
                        pmod_product_cur_interval[ss] * basescalers[ss];

                    calc.base_adj_diff[hand][ss][i] = adj_npsbase;
                    calc.base_diff_for_stam_mod[hand][ss][i] = adj_npsbase;

                    calc.ulbu_in_charge.AdjDiffFunc(i, hand, ss, adj_npsbase, pmod_product_cur_interval);
                }
            }
        }

        // each skillset should just be a separate calc function [todo]
        private float Chisel(float player_skill, float resolution, float score_goal, Skillset ss, bool stamina)
        {
            if (ss == Skillset.Skill_Overall || ss == Skillset.Skill_Stamina)
                return M.min_rating;

            float reqpoints = MaxPoints * score_goal;
            float max_slap_dash_jack_cap_hack_tech_hat = MaxPoints * 0.1f;
            int ssi = (int)ss;

            float calc_gotpoints(float curr_player_skill)
            {
                float gotpoints;
                switch (ss)
                {
                    case Skillset.Skill_Technical: gotpoints = MaxPoints * tech_pbm; break;
                    case Skillset.Skill_JackSpeed: gotpoints = MaxPoints * jack_pbm; break;
                    case Skillset.Skill_Stream: gotpoints = MaxPoints * stream_pbm; break;
                    case Skillset.Skill_Jumpstream:
                    case Skillset.Skill_Handstream:
                    case Skillset.Skill_Chordjack: gotpoints = MaxPoints * bad_newbie_skillsets_pbm; break;
                    default: gotpoints = 0f; break;
                }
                foreach (var hand in Hands.both_hands)
                {
                    if (ss == Skillset.Skill_JackSpeed)
                        gotpoints -= jackloss(curr_player_skill, this, hand, stamina);
                    else
                        CalcInternal(ref gotpoints, curr_player_skill, ssi, stamina, this, hand);

                    if (ss == Skillset.Skill_Technical)
                        gotpoints -= M.fastsqrt(Math.Min(
                            max_slap_dash_jack_cap_hack_tech_hat,
                            jackloss(curr_player_skill * 0.75f, this, hand, stamina) * 0.85f));
                }
                return gotpoints;
            }

            float gp;
            float cps = player_skill;
            float res = resolution;

            do
            {
                if (cps > M.max_rating) return M.min_rating;
                cps += res;
                gp = calc_gotpoints(cps);
            } while (gp < reqpoints);
            cps -= res;
            res /= 2f;

            for (int iter = 1; iter <= 7; iter++)
            {
                if (cps > M.max_rating) return M.min_rating;
                cps += res;
                gp = calc_gotpoints(cps);
                if (gp > reqpoints) cps -= res;
                res /= 2f;
            }

            return cps + 2f * res;
        }
    }

    /// Entry points (MinaCalc.cpp MinaSDCalc). 8 skillsets: [Overall, Stream, JS, HS, Stamina, JackSpeed, Chordjack, Technical].
    public static class MinaSD
    {
        public static float[] MinaSDCalc(IReadOnlyList<NoteInfo> NoteInfo, float musicrate, float goal, uint keycount, Calc calc)
        {
            if (NoteInfo.Count <= 1) return M.DimplesAllZero();
            calc.ssr = true;
            calc.debugmode = false;
            calc.keycount = keycount;
            return calc.CalcMain(NoteInfo, musicrate, Math.Min(goal, M.ssr_goal_cap));
        }

        public static int GetCalcVersion() => Calc.mina_calc_version;
    }
}
