using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    //FM synthesis  : chips: YAMAHA YM2612
    //----------------------------------------------------------------
    internal partial class md_ym2612
    {
        private static readonly bool AudioMuteDac =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_MUTE_DAC"), "1", StringComparison.Ordinal);
        private static readonly bool AudioMuteFmPsg =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_MUTE_FMPSG"), "1", StringComparison.Ordinal);

        public (int out1, int out2) YM2612_Update()
        {
            // Diagnostic logging
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_DIAG_FRAME") == "1")
            {
                md_main.IncrementYmAdvanceCalls();
            }
            
            // Detailed YM timing logging
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1")
            {
                Console.WriteLine($"[YM-TIMING] YM2612_Update called at SystemCycles={md_main.SystemCycles}");
            }
            
            // Advance timers based on master SystemCycles first
            AdvanceTimersFromSystemCycles();
            
            int w_out_l = 0;
            int w_out_r = 0;

            // FM_VOLUME_DIVISOR controls the final output level
            // Original was 32 for reasonable volume
            // Increased to 64 to lower volume (user reported headphones cracking)
            const int FM_VOLUME_DIVISOR = 64;

            lfo_calc();
            for (int w_ch = 0; w_ch < NUM_CHANNELS; w_ch++)
            {
                register_change(w_ch);
                phase_generator(w_ch);
                envelop_generator(w_ch);
                if ((w_ch != 5) || ((g_reg_2b_dac & 0x80) == 0))
                {
                    operator_update(w_ch);
                    if (g_ch_out[w_ch] > OUT_CH_LIMIT) g_ch_out[w_ch] = OUT_CH_LIMIT;
                    else if (g_ch_out[w_ch] < -OUT_CH_LIMIT) g_ch_out[w_ch] = -OUT_CH_LIMIT;
                    if (!AudioMuteFmPsg)
                    {
                        // g_ch_out has been shifted right by OUT_DOWN_BIT (9) in slot_mixer
                        // clownmdemu does: sample * 128 / 8 = sample * 16
                        // So we shift left 7 (multiply by 128) then divide by 8 = multiply by 16
                        int ch_output = (g_ch_out[w_ch] << 7) / FM_VOLUME_DIVISOR;
                        if (g_reg_b4_l[w_ch] == true) w_out_l += (int)(ch_output * md_main.g_md_music.g_out_vol[w_ch]);
                        if (g_reg_b4_r[w_ch] == true) w_out_r += (int)(ch_output * md_main.g_md_music.g_out_vol[w_ch]);
                    }
                }
                else
                {
                    int w_dac = dac_control();
                    if (!AudioMuteDac)
                    {
                        // Match clownmdemu-core: DAC sample should be same scaling as FM samples
                        // FM samples get: (sample * 128) / FM_VOLUME_DIVISOR
                        // So DAC should be: w_dac * 128 / FM_VOLUME_DIVISOR
                        int dac_output = (w_dac << 7) / FM_VOLUME_DIVISOR;
                        // DEBUG: Log DAC output
                        // Console.WriteLine($"[DAC-OUTPUT] w_dac={w_dac} output={dac_output} vol={md_main.g_md_music.g_out_vol[5]}");
                        if (g_reg_b4_l[5] == true) w_out_l += (int)(dac_output * md_main.g_md_music.g_out_vol[5]);
                        if (g_reg_b4_r[5] == true) w_out_r += (int)(dac_output * md_main.g_md_music.g_out_vol[5]);
                    }
                }
            }
            // timer_control() is now called from AdvanceTimersFromSystemCycles()

            // Final output limiting to prevent clipping
            if (w_out_l > short.MaxValue) w_out_l = short.MaxValue;
            else if (w_out_l < short.MinValue) w_out_l = short.MinValue;
            if (w_out_r > short.MaxValue) w_out_r = short.MaxValue;
            else if (w_out_r < short.MinValue) w_out_r = short.MinValue;

            return (w_out_l, w_out_r);
        }

        public void YM2612_UpdateBatch(Span<short> dst, int frames)
        {
            int maxFrames = dst.Length / 2;
            if (frames < maxFrames)
                maxFrames = frames;

            // Diagnostic logging - count this as one advance call for the entire batch
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_DIAG_FRAME") == "1")
            {
                md_main.IncrementYmAdvanceCalls();
            }

            // Detailed YM timing logging
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1")
            {
                Console.WriteLine($"[YM-TIMING] YM2612_UpdateBatch called at SystemCycles={md_main.SystemCycles} frames={frames}");
            }

            // Advance timers based on master SystemCycles first (once per batch)
            AdvanceTimersFromSystemCycles();
            
            for (int i = 0; i < maxFrames; i++)
            {
                // Update LFO once per sample
                lfo_calc();
                
                int w_out_l = 0;
                int w_out_r = 0;
                const int FM_VOLUME_DIVISOR = 64;

                for (int w_ch = 0; w_ch < NUM_CHANNELS; w_ch++)
                {
                    register_change(w_ch);
                    phase_generator(w_ch);
                    envelop_generator(w_ch);
                    if ((w_ch != 5) || ((g_reg_2b_dac & 0x80) == 0))
                    {
                        operator_update(w_ch);
                        if (g_ch_out[w_ch] > OUT_CH_LIMIT) g_ch_out[w_ch] = OUT_CH_LIMIT;
                        else if (g_ch_out[w_ch] < -OUT_CH_LIMIT) g_ch_out[w_ch] = -OUT_CH_LIMIT;
                        if (!AudioMuteFmPsg)
                        {
                            int ch_output = (g_ch_out[w_ch] << 7) / FM_VOLUME_DIVISOR;
                            if (g_reg_b4_l[w_ch] == true) w_out_l += (int)(ch_output * md_main.g_md_music.g_out_vol[w_ch]);
                            if (g_reg_b4_r[w_ch] == true) w_out_r += (int)(ch_output * md_main.g_md_music.g_out_vol[w_ch]);
                        }
                    }
                    else
                    {
                        int w_dac = dac_control();
                        if (!AudioMuteDac)
                        {
                            int dac_output = (w_dac << 7) / FM_VOLUME_DIVISOR;
                            if (g_reg_b4_l[5] == true) w_out_l += (int)(dac_output * md_main.g_md_music.g_out_vol[5]);
                            if (g_reg_b4_r[5] == true) w_out_r += (int)(dac_output * md_main.g_md_music.g_out_vol[5]);
                        }
                    }
                }

                // Final output limiting
                if (w_out_l > short.MaxValue) w_out_l = short.MaxValue;
                else if (w_out_l < short.MinValue) w_out_l = short.MinValue;
                if (w_out_r > short.MaxValue) w_out_r = short.MaxValue;
                else if (w_out_r < short.MinValue) w_out_r = short.MinValue;

                int idx = i * 2;
                dst[idx] = (short)w_out_l;
                dst[idx + 1] = (short)w_out_r;
            }

            int written = maxFrames * 2;
            if (written < dst.Length)
                dst.Slice(written).Clear();
        }
        private void lfo_calc()
        {
            if (g_reg_22_lfo_enable == true)
            {
                g_com_lfo_cnt += g_reg_22_lfo_inc;
                int w_cnt = (g_com_lfo_cnt >> CNT_LOW_BIT) & CNT_MASK;
                g_com_lfo_freq_cnt = LFO_FREQ_TABLE[w_cnt];
                g_com_lfo_env_cnt = LFO_ENV_TABLE[w_cnt];
            }
        }
        private void register_change(int in_ch)
        {
            if (g_ch_reg_reflesh[in_ch] == true)
            {
                g_ch_reg_reflesh[in_ch] = false;
                for (int w_slot = 0; w_slot < NUM_SLOT; w_slot++)
                {
                    //ch3 mode support
                    int w_slot_m = 0;
                    if ((in_ch == 2) && ((g_reg_27_mode & 0x40) > 0))
                    {
                        w_slot_m = CH3CSM_MAP[w_slot];
                    }
                    //phase_generator
                    int finc = (int)((float)g_slot_fnum[in_ch, w_slot_m]
                        * (1 << g_reg_a4_block[in_ch, w_slot_m]))>> 1;
                    int kc = g_slot_keycode[in_ch, w_slot_m];
                    g_slot_phase_inc[in_ch, w_slot] = 
                        (int)((finc + DT_TABLE[g_reg_30_dt[in_ch, w_slot], kc])
                        * EMU_CORRECTION
                        * g_reg_30_multi[in_ch, w_slot]);
                    //envelop_generator
                    int ksr = kc >> g_reg_50_key_scale[in_ch, w_slot];
                    g_slot_env_incA[in_ch, w_slot] = (int)ENV_RATE_A_TABLE[g_slot_env_indexA[in_ch, w_slot] + ksr];
                    g_slot_env_incD[in_ch, w_slot] = (int)ENV_RATE_D_TABLE[g_slot_env_indexD[in_ch, w_slot] + ksr];
                    g_slot_env_incS[in_ch, w_slot] = (int)ENV_RATE_D_TABLE[g_slot_env_indexS[in_ch, w_slot] + ksr];
                    g_slot_env_incR[in_ch, w_slot] = (int)ENV_RATE_D_TABLE[g_slot_env_indexR[in_ch, w_slot] + ksr];
                }
            }
        }
        private void phase_generator(int in_ch)
        {
            for (int w_slot = 0; w_slot < NUM_SLOT; w_slot++)
            {
                int w_lfo_inc = 0;
                if (g_reg_22_lfo_enable == true)
                {
                    w_lfo_inc = (int)(g_slot_phase_inc[in_ch, w_slot] * (g_reg_b4_pms[in_ch] * g_com_lfo_freq_cnt)) >> CNT_LOW_BIT;
                }
                g_slot_op_calc[in_ch, w_slot] = g_slot_freq_cnt[in_ch, w_slot];
                g_slot_freq_cnt[in_ch, w_slot] += g_slot_phase_inc[in_ch, w_slot] + w_lfo_inc;
            }
        }

        private void operator_calc(int in_ch)
        {
            g_slot_op_calc[in_ch, 0] += (g_slot_phase_out[in_ch, 0] + g_slot_phase_out[in_ch, 1]) >> g_reg_b0_fb[in_ch];
            g_slot_phase_out[in_ch, 1] = g_slot_phase_out[in_ch, 0];
            g_slot_phase_out[in_ch, 0] = TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 0] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 0]];
        }
        private void slot_mixer(int in_ch, int in_input1, int in_input2, int in_input3, int in_input4)
        {
            g_ch_out[in_ch] = (TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, in_input1] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, in_input1]]);
            if (in_input2 != -1) g_ch_out[in_ch] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, in_input2] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, in_input2]];
            if (in_input3 != -1) g_ch_out[in_ch] += (int)TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, in_input3] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, in_input3]];
            if (in_input4 != -1) g_ch_out[in_ch] += (int)TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, in_input4] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, in_input4]];
            g_ch_out[in_ch] >>= OUT_DOWN_BIT;
        }

        private void operator_update(int in_ch)
        {
            switch (g_reg_b0_algo[in_ch])
            {
                case 4:
                    if ((g_slot_env_cnt[in_ch, 2] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 3] == ENV_LEN_END)) return;
                    break;
                case 5:
                    if ((g_slot_env_cnt[in_ch, 2] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 1] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 3] == ENV_LEN_END)) return;
                    break;
                case 6:
                    if ((g_slot_env_cnt[in_ch, 2] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 1] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 3] == ENV_LEN_END)) return;
                    break;
                case 7:
                    if ((g_slot_env_cnt[in_ch, 0] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 2] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 1] == ENV_LEN_END) && (g_slot_env_cnt[in_ch, 3] == ENV_LEN_END)) return;
                    break;
                default:
                    if (g_slot_env_cnt[in_ch, 3] == ENV_LEN_END) return;
                    break;
            }

            switch (g_reg_b0_algo[in_ch])
            {
                case 0:
                    operator_calc(in_ch);
                    g_slot_op_calc[in_ch, 1] += g_slot_phase_out[in_ch, 1];
                    g_slot_op_calc[in_ch, 2] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 1] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 1]];
                    g_slot_op_calc[in_ch, 3] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 2] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 2]];
                    slot_mixer(in_ch, 3, -1, -1, -1);
                    break;
                case 1:
                    operator_calc(in_ch);
                    g_slot_op_calc[in_ch, 2] += g_slot_phase_out[in_ch, 1];
                    g_slot_op_calc[in_ch, 2] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 1] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 1]];
                    g_slot_op_calc[in_ch, 3] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 2] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 2]];
                    slot_mixer(in_ch, 3, -1, -1, -1);
                    break;
                case 2:
                    operator_calc(in_ch);
                    g_slot_op_calc[in_ch, 2] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 1] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 1]];
                    g_slot_op_calc[in_ch, 3] += g_slot_phase_out[in_ch, 1];
                    g_slot_op_calc[in_ch, 3] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 2] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 2]];
                    slot_mixer(in_ch, 3, -1, -1, -1);
                    break;
                case 3:
                    operator_calc(in_ch);
                    g_slot_op_calc[in_ch, 1] += g_slot_phase_out[in_ch, 1];
                    g_slot_op_calc[in_ch, 3] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 1] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 1]]
                                              + TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 2] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 2]];
                    slot_mixer(in_ch, 3, -1, -1, -1);
                    break;
                case 4:
                    operator_calc(in_ch);
                    g_slot_op_calc[in_ch, 1] += g_slot_phase_out[in_ch, 1];
                    g_slot_op_calc[in_ch, 3] += TL_TABLE[SIN_TABLE[(g_slot_op_calc[in_ch, 2] >> CNT_LOW_BIT) & CNT_MASK] + g_slot_env_out[in_ch, 2]];
                    slot_mixer(in_ch, 1, 3, -1, -1);
                    break;
                case 5:
                    operator_calc(in_ch);
                    g_slot_op_calc[in_ch, 1] += g_slot_phase_out[in_ch, 1];
                    g_slot_op_calc[in_ch, 2] += g_slot_phase_out[in_ch, 1];
                    g_slot_op_calc[in_ch, 3] += g_slot_phase_out[in_ch, 1];
                    slot_mixer(in_ch, 1, 2, 3, -1);
                    break;
                case 6:
                    operator_calc(in_ch);
                    g_slot_op_calc[in_ch, 1] += g_slot_phase_out[in_ch, 1];
                    slot_mixer(in_ch, 1, 2, 3, -1);
                    break;
                case 7:
                    operator_calc(in_ch);
                    slot_mixer(in_ch, 0, 1, 2, 3);
                    break;
            }
        }

        private void envelop_generator(int in_ch)
        {
            for (int w_slot = 0; w_slot < NUM_SLOT; w_slot++)
            {
                int w_lfo_inc = 0;
                if (g_reg_22_lfo_enable == true)
                {
                    w_lfo_inc = (g_com_lfo_env_cnt >> g_slot_ams[in_ch, w_slot]);
                }
                int w_env = g_slot_env_out[in_ch, w_slot];
                if ((g_reg_90_ssg[in_ch, w_slot] & 4) == 0)
                {
                    w_env = (int)ENV_TABLE[(g_slot_env_cnt[in_ch, w_slot] >> CNT_LOW_BIT)] + g_reg_40_tl[in_ch, w_slot] + w_lfo_inc;
                }
                else
                {
                    w_env = (int)ENV_TABLE[(g_slot_env_cnt[in_ch, w_slot] >> CNT_LOW_BIT)] + g_reg_40_tl[in_ch, w_slot];
                    if (w_env > CNT_MASK)
                    {
                        w_env = 0;
                    }
                    else
                    {
                        w_env = (w_env ^ CNT_MASK) + w_lfo_inc;
                    }
                }
                g_slot_env_out[in_ch, w_slot] = w_env;

                int w_inc = 0;
                ENV_COND w_cond = g_slot_env_cond[in_ch, w_slot];
                switch (w_cond)
                {
                    case ENV_COND.ATTACK:
                        w_inc = g_slot_env_incA[in_ch, w_slot];
                        break;
                    case ENV_COND.DECAY:
                        w_inc = g_slot_env_incD[in_ch, w_slot];
                        break;
                    case ENV_COND.SUBSTAIN:
                        if (g_slot_env_cnt[in_ch, w_slot] < ENV_LEN_END)
                        {
                            w_inc = g_slot_env_incS[in_ch, w_slot];
                        }
                        break;
                    case ENV_COND.RELEASE:
                        if (g_slot_env_cnt[in_ch, w_slot] < ENV_LEN_END)
                        {
                            w_inc = g_slot_env_incR[in_ch, w_slot];
                        }
                        break;
                }
                g_slot_env_cnt[in_ch, w_slot] += w_inc;
                if (g_slot_env_cnt[in_ch, w_slot] >= g_slot_env_cmp[in_ch, w_slot])
                {
                    switch (w_cond)
                    {
                        case ENV_COND.ATTACK:
                            g_slot_env_cnt[in_ch, w_slot] = ENV_LEN_DECAY;
                            g_slot_env_cmp[in_ch, w_slot] = g_reg_80_sl[in_ch, w_slot];
                            g_slot_env_cond[in_ch, w_slot] = ENV_COND.DECAY;
                            break;
                        case ENV_COND.DECAY:
                            g_slot_env_cnt[in_ch, w_slot] = g_reg_80_sl[in_ch, w_slot];
                            g_slot_env_cmp[in_ch, w_slot] = ENV_LEN_END;
                            g_slot_env_cond[in_ch, w_slot] = ENV_COND.SUBSTAIN;
                            break;
                        case ENV_COND.SUBSTAIN:
                            if ((g_reg_90_ssg[in_ch, w_slot] & 8) != 0)
                            {
                                if ((g_reg_90_ssg[in_ch, w_slot] & 1) == 0)
                                {
                                    g_slot_env_cnt[in_ch, w_slot] = 0;
                                    g_slot_env_cmp[in_ch, w_slot] = ENV_LEN_DECAY;
                                    g_slot_env_cond[in_ch, w_slot] = ENV_COND.ATTACK;
                                }
                                else
                                {
                                    g_slot_env_cnt[in_ch, w_slot] = ENV_LEN_END;
                                    g_slot_env_cmp[in_ch, w_slot] = ENV_LEN_END + 1;
                                }
                                g_reg_90_ssg[in_ch, w_slot] ^= (g_reg_90_ssg[in_ch, w_slot] & 2) << 1;
                            }
                            else
                            {
                                g_slot_env_cnt[in_ch, w_slot] = ENV_LEN_END;
                                g_slot_env_cmp[in_ch, w_slot] = ENV_LEN_END + 1;
                            }
                            break;
                        case ENV_COND.RELEASE:
                            g_slot_env_cnt[in_ch, w_slot] = ENV_LEN_END;
                            g_slot_env_cmp[in_ch, w_slot] = ENV_LEN_END + 1;
                            break;
                    }
                }
            }
        }
        private void Slot_Key_on(int in_ch, int in_slot)
        {
            if (g_slot_env_cond[in_ch, in_slot] == ENV_COND.RELEASE)
            {
                g_slot_freq_cnt[in_ch, in_slot] = 0;
                if (g_slot_CNT_MASK[in_ch, 0] == true)
                {
                    g_slot_env_cnt[in_ch, in_slot] = (int)(ENV_D2A[ENV_TABLE[g_slot_env_cnt[in_ch, in_slot] >> CNT_LOW_BIT]] + ENV_LEN_ATTACK);
                }
                g_slot_CNT_MASK[in_ch, 0] = true;
                g_slot_env_cmp[in_ch, in_slot] = ENV_LEN_DECAY;
                g_slot_env_cond[in_ch, in_slot] = ENV_COND.ATTACK;
            }
        }
        private void Slot_Key_off(int in_ch, int in_slot)
        {

            if (g_slot_env_cond[in_ch, in_slot] != ENV_COND.RELEASE)
            {
                if (g_slot_env_cnt[in_ch, in_slot] < ENV_LEN_DECAY)
                {
                    g_slot_env_cnt[in_ch, in_slot] = (int)((ENV_TABLE[g_slot_env_cnt[in_ch, in_slot] >> CNT_LOW_BIT] << CNT_LOW_BIT) + ENV_LEN_DECAY);
                }

                g_slot_env_cmp[in_ch, in_slot] = ENV_LEN_END;
                g_slot_env_cond[in_ch, in_slot] = ENV_COND.RELEASE;
            }
        }
        private int dac_control()
        {
            // g_reg_2a_dac_data is now stored as 9-bit unsigned value (0x000..0x1FF)
            // The DAC input was: ((val + 0x80) << 1) & 0x1FF
            // So we need to reverse: divide by 2 and add 0x100 to get signed
            // But since values are always even, we can just use the value directly
            // after adjusting for the bit 0 that was preserved
            int w_dac = (int)g_reg_2a_dac_data - 0x100;
            // Match clownmdemu-core: no extra scaling, DAC should be same range as FM samples
            return w_dac;
        }
        private void timer_control()
        {
            md_main.IncrementTimerControlCalls();
            // ALADDIN FIX: Timers are now driven by SystemCycles via AdvanceTimersFromSystemCycles()
            // This function is kept for backward compatibility but does nothing
            // Console.WriteLine($"[ALADDIN-AUDIO-TIMER] timer_control called but timers now SystemCycles-driven");
        }

        public void TickTimersFromZ80Cycles(int z80Cycles)
        {
            // Still needed for Z80-driven timing when Z80 is active
            if (z80Cycles <= 0)
                return;
            _timersDrivenByZ80 = true;
            double oldFrac = _timerTickFrac;
            _timerTickFrac += z80Cycles * (YM2612_CLOCK / 72.0) / Z80_CLOCK;
            int ticks = (int)_timerTickFrac;
            if (ticks <= 0)
                return;
            
            // ALADDIN DEBUG: Log timer ticks
            if (md_main.g_md_z80?.DebugPc == 0x0DC2 || md_main.g_md_z80?.DebugPc == 0x0DF7)
            {
                Console.WriteLine($"[ALADDIN-TIMER] z80Cycles={z80Cycles} oldFrac={oldFrac:F2} newFrac={_timerTickFrac:F2} ticks={ticks} timerA={_timerACount} reload={_timerAReload} enabled={g_reg_27_enable_A}");
            }
            
            _timerTickFrac -= ticks;
            StepTimers(ticks);
        }

        public void AdvanceTimersFromSystemCycles()
        {
            if (md_main.g_md_z80 != null && md_main.g_md_z80.g_active)
            {
                // When Z80 is active, timers are driven by Z80 cycles
                // Don't double-advance from SystemCycles
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1")
                {
                    Console.WriteLine($"[YM-TIMING] Z80 active, skipping SystemCycles advance");
                }
                return;
            }
            
            long currentCycles = md_main.SystemCycles;
            if (_lastSystemCycles < 0)
            {
                _lastSystemCycles = currentCycles;
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1")
                {
                    Console.WriteLine($"[YM-TIMING] First call, setting _lastSystemCycles={currentCycles}");
                }
                return;
            }
            
            long deltaCycles = currentCycles - _lastSystemCycles;
            if (deltaCycles <= 0)
                return;
                
            _lastSystemCycles = currentCycles;
            
            // Convert M68K cycles to YM2612 timer ticks
            // YM2612 clock: 7.67MHz (7670454 Hz)
            // M68K clock: 7.67MHz (same bus)
            // Timer ticks at 72Hz (YM2612_CLOCK / 72)
            // So: deltaCycles * (YM2612_CLOCK / 72) / M68K_CLOCK
            // Since YM2612_CLOCK ≈ M68K_CLOCK, simplifies to: deltaCycles / 72
            double timerTicks = deltaCycles / 72.0;
            
            if (timerTicks <= 0)
                return;
                
            double oldFrac = _timerTickFrac;
            _timerTickFrac += timerTicks;
            int ticks = (int)_timerTickFrac;
            if (ticks <= 0)
                return;
                
            // Debug logging for timer advancement
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1")
            {
                Console.WriteLine($"[YM-TIMING] deltaCycles={deltaCycles} timerTicks={timerTicks:F2} oldFrac={oldFrac:F2} newFrac={_timerTickFrac:F2} ticks={ticks} timerA={_timerACount}");
            }
                
            _timerTickFrac -= ticks;
            StepTimers(ticks);
        }

        private void StepTimers(int ticks)
        {
            // ALADDIN DEBUG: Log StepTimers call
            if (ticks > 0 && (md_main.g_md_z80?.DebugPc == 0x0DC2 || md_main.g_md_z80?.DebugPc == 0x0DF7))
            {
                Console.WriteLine($"[ALADDIN-STEP-TIMERS] ticks={ticks} enA={g_reg_27_enable_A} enB={g_reg_27_enable_B} timerA={_timerACount}");
            }
            
            if (g_reg_27_enable_A)
            {
                int remaining = _timerACount - ticks;
                while (remaining <= 0)
                {
                    g_com_status |= 0x01;
                    _timerAEvents++;
                    // ALADDIN DEBUG: Track timer A overflows
                    md_main.IncrementYmTimerAOverflow();
                    UpdateYmIrq("timerA");
                    if ((g_reg_27_mode & 0x80) != 0)
                    {
                        Slot_Key_on(2, 0);
                        Slot_Key_on(2, 1);
                        Slot_Key_on(2, 2);
                        Slot_Key_on(2, 3);
                    }
                    remaining += _timerAReload;
                }
                _timerACount = remaining;
            }
            if (g_reg_27_enable_B)
            {
                int remaining = _timerBCount - ticks;
                while (remaining <= 0)
                {
                    g_com_status |= 0x02;
                    _timerBEvents++;
                    // ALADDIN DEBUG: Track timer B overflows
                    md_main.IncrementYmTimerBOverflow();
                    UpdateYmIrq("timerB");
                    remaining += _timerBReload;
                }
                _timerBCount = remaining;
            }
        }
        
        public void ForceAdvanceOneFrame(long deltaCycles = 0)
        {
            // Force YM2612 to advance by one frame's worth of time
            // This ensures chip timing progresses even when no audio is generated
            long currentCycles = md_main.SystemCycles;
            if (_lastSystemCycles < 0)
            {
                _lastSystemCycles = currentCycles;
                return;
            }
            
            // Use provided deltaCycles or default to one frame's worth
            if (deltaCycles <= 0)
            {
                // Default to approximately one frame's worth of M68K cycles
                // 255712 cycles per frame (from DIAG-FRAME logs for NTSC)
                deltaCycles = 255712;
            }
            
            _lastSystemCycles += deltaCycles;
            
            // Convert to YM2612 timer ticks
            double timerTicks = deltaCycles / 72.0;
            if (timerTicks <= 0)
                return;
                
            double oldFrac = _timerTickFrac;
            _timerTickFrac += timerTicks;
            int ticks = (int)_timerTickFrac;
            if (ticks <= 0)
                return;
                
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1")
            {
                Console.WriteLine($"[YM-TIMING-FORCE] deltaCycles={deltaCycles} timerTicks={timerTicks:F2} ticks={ticks}");
            }
                
            _timerTickFrac -= ticks;
            StepTimers(ticks);
        }
    }
}
