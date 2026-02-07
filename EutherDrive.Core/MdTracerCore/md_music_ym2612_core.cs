using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    //FM synthesis  : chips: YAMAHA YM2612
    //----------------------------------------------------------------
    internal partial class md_ym2612
    {
        private static readonly int FmVolumeDivisor = GetFmVolumeDivisor();
        private double _sampleCycleFrac;
        private static readonly bool AudioMuteDac =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_MUTE_DAC"), "1", StringComparison.Ordinal);
        private static readonly bool AudioMuteFmPsg =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_MUTE_FMPSG"), "1", StringComparison.Ordinal);
        private static readonly bool TraceDacOutput =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DAC_OUTPUT"), "1", StringComparison.Ordinal);
        private static readonly double DacMixGain = GetDacMixGain();
        private static readonly bool TraceYmOutput =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_OUTPUT"), "1", StringComparison.Ordinal);
        private long _dacOutLastTicks;
        private int _dacOutMin = int.MaxValue;
        private int _dacOutMax = int.MinValue;
        private long _dacOutSum;
        private long _dacOutCount;
        private int _dacOutScaledMin = int.MaxValue;
        private int _dacOutScaledMax = int.MinValue;
        private long _dacOutScaledSum;
        private long _dacOutScaledCount;
        private long _ymOutLastTicks;
        private int _ymOutMin = int.MaxValue;
        private int _ymOutMax = int.MinValue;
        private long _ymOutSum;
        private long _ymOutCount;
        private static int GetFmVolumeDivisor()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_FM_VOLUME_DIVISOR");
            if (!string.IsNullOrWhiteSpace(raw)
                && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
                && value > 0)
            {
                return value;
            }
            // Default to 64 to reduce overly hot FM output.
            return 64;
        }

        private static double GetDacMixGain()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_DAC_MIX_GAIN");
            if (!string.IsNullOrWhiteSpace(raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                && value > 0)
            {
                return value;
            }
            return 25.0;
        }

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
            
            // Timers advance via EnsureAdvanceEachFrame (SystemCycles), not audio samples.
            
            int w_out_l = 0;
            int w_out_r = 0;

            // FM volume divisor controls final output level; env override allows tuning.
            int FM_VOLUME_DIVISOR = FmVolumeDivisor;
            
            // SIMPLE FIX: Advance operators by ONE audio sample
            // The original code advanced operators here (lfo_calc, phase_generator, envelop_generator)
            // We keep that but need to ensure timing is correct
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
                        // otheremumdemu does: sample * 128 / 8 = sample * 16
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
                        // Match otheremumdemu-core: DAC sample should be same scaling as FM samples
                        // FM samples get: (sample * 128) / FM_VOLUME_DIVISOR
                        // So DAC should be: w_dac * 128 / FM_VOLUME_DIVISOR
                        int dac_output = (w_dac << 7) / FM_VOLUME_DIVISOR;
                        if (DacMixGain != 1.0)
                        {
                            double scaled = dac_output * DacMixGain;
                            if (scaled > short.MaxValue) dac_output = short.MaxValue;
                            else if (scaled < short.MinValue) dac_output = short.MinValue;
                            else dac_output = (int)Math.Round(scaled);
                        }
                        TraceDacOutputSample(w_dac, dac_output);
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

            TraceYmOutputSample(w_out_l, w_out_r);
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

            // Timers advance via EnsureAdvanceEachFrame (SystemCycles), not audio samples.

            double cyclesPerSample = Z80_CLOCK / YM2612_SAMPLING;
            long endCycle = md_main.g_md_z80 != null ? md_main.g_md_z80.DebugTotalCycles : -1;
            double startCycle = _dacStreamCycleCursor;
            if (endCycle >= 0)
            {
                startCycle = endCycle - ((frames - 1) * cyclesPerSample);
                if (_dacStreamCycleCursor < 0 || Math.Abs(startCycle - _dacStreamCycleCursor) > cyclesPerSample * frames * 4)
                    _dacStreamCycleCursor = startCycle;
            }
            else if (_dacStreamCycleCursor < 0)
            {
                _dacStreamCycleCursor = 0;
            }
            double cyclePos = _dacStreamCycleCursor;

            for (int i = 0; i < maxFrames; i++)
            {
                int w_out_l = 0;
                int w_out_r = 0;
                int FM_VOLUME_DIVISOR = FmVolumeDivisor;
                
                // SIMPLE FIX: Advance operators by ONE audio sample
                // The original code advanced operators here (lfo_calc, phase_generator, envelop_generator)
                // We keep that but need to ensure timing is correct
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
                            int ch_output = (g_ch_out[w_ch] << 7) / FM_VOLUME_DIVISOR;
                            if (g_reg_b4_l[w_ch] == true) w_out_l += (int)(ch_output * md_main.g_md_music.g_out_vol[w_ch]);
                            if (g_reg_b4_r[w_ch] == true) w_out_r += (int)(ch_output * md_main.g_md_music.g_out_vol[w_ch]);
                        }
                    }
                    else
                    {
                        int dacData = g_reg_2a_dac_data;
                        if (endCycle >= 0)
                        {
                            dacData = UpdateDacStreamForCycle((long)cyclePos);
                            cyclePos += cyclesPerSample;
                        }
                        int w_dac = dacData - 0x100;
                        if (!AudioMuteDac)
                        {
                            int dac_output = (w_dac << 7) / FM_VOLUME_DIVISOR;
                            if (DacMixGain != 1.0)
                            {
                                double scaled = dac_output * DacMixGain;
                                if (scaled > short.MaxValue) dac_output = short.MaxValue;
                                else if (scaled < short.MinValue) dac_output = short.MinValue;
                                else dac_output = (int)Math.Round(scaled);
                            }
                            TraceDacOutputSample(w_dac, dac_output);
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

                TraceYmOutputSample(w_out_l, w_out_r);
            }

            if (endCycle >= 0)
            {
                if ((g_reg_2b_dac & 0x80) == 0)
                    UpdateDacStreamForCycle(endCycle);
                _dacStreamCycleCursor = cyclePos;
            }

            int written = maxFrames * 2;
            if (written < dst.Length)
                dst.Slice(written).Clear();
        }
        private void lfo_calc()
        {
            if (g_reg_22_lfo_enable == true)
            {
                // Scale LFO increment to match YM step scaling.
                double total = g_com_lfo_frac + (g_reg_22_lfo_inc * YmStepScale);
                int step = (int)total;
                g_com_lfo_frac = total - step;
                g_com_lfo_cnt += step;
                int w_cnt = (g_com_lfo_cnt >> CNT_LOW_BIT) & CNT_MASK;
                g_com_lfo_freq_cnt = LFO_FREQ_TABLE[w_cnt];
                g_com_lfo_env_cnt = LFO_ENV_TABLE[w_cnt];
            }
        }

        private void TraceDacOutputSample(int w_dac, int dac_output)
        {
            if (!TraceDacOutput)
                return;

            if (w_dac < _dacOutMin) _dacOutMin = w_dac;
            if (w_dac > _dacOutMax) _dacOutMax = w_dac;
            _dacOutSum += w_dac;
            _dacOutCount++;

            if (dac_output < _dacOutScaledMin) _dacOutScaledMin = dac_output;
            if (dac_output > _dacOutScaledMax) _dacOutScaledMax = dac_output;
            _dacOutScaledSum += dac_output;
            _dacOutScaledCount++;

            long nowTicks = Stopwatch.GetTimestamp();
            if (_dacOutLastTicks == 0)
                _dacOutLastTicks = nowTicks;
            if (nowTicks - _dacOutLastTicks < Stopwatch.Frequency)
                return;

            double avg = _dacOutCount > 0 ? _dacOutSum / (double)_dacOutCount : 0.0;
            double avgScaled = _dacOutScaledCount > 0 ? _dacOutScaledSum / (double)_dacOutScaledCount : 0.0;
            int vol = md_main.g_md_music.g_out_vol[5];
            int panL = g_reg_b4_l[5] ? 1 : 0;
            int panR = g_reg_b4_r[5] ? 1 : 0;
            int enabled = (g_reg_2b_dac & 0x80) != 0 ? 1 : 0;

            Console.WriteLine(
                "[DAC-OUT] writes={0} w_dac=min{1} max{2} avg{3:0.0} out=min{4} max{5} avg{6:0.0} enabled={7} panL={8} panR={9} vol={10} mute={11}",
                _dacOutCount, _dacOutMin, _dacOutMax, avg, _dacOutScaledMin, _dacOutScaledMax, avgScaled,
                enabled, panL, panR, vol, AudioMuteDac ? 1 : 0);

            _dacOutCount = 0;
            _dacOutSum = 0;
            _dacOutMin = int.MaxValue;
            _dacOutMax = int.MinValue;
            _dacOutScaledCount = 0;
            _dacOutScaledSum = 0;
            _dacOutScaledMin = int.MaxValue;
            _dacOutScaledMax = int.MinValue;
            _dacOutLastTicks = nowTicks;
        }

        private void TraceYmOutputSample(int w_out_l, int w_out_r)
        {
            if (!TraceYmOutput)
                return;

            int min = w_out_l < w_out_r ? w_out_l : w_out_r;
            int max = w_out_l > w_out_r ? w_out_l : w_out_r;
            if (min < _ymOutMin) _ymOutMin = min;
            if (max > _ymOutMax) _ymOutMax = max;
            _ymOutSum += w_out_l;
            _ymOutSum += w_out_r;
            _ymOutCount += 2;

            long nowTicks = Stopwatch.GetTimestamp();
            if (_ymOutLastTicks == 0)
                _ymOutLastTicks = nowTicks;
            if (nowTicks - _ymOutLastTicks < Stopwatch.Frequency)
                return;

            double avg = _ymOutCount > 0 ? _ymOutSum / (double)_ymOutCount : 0.0;
            int dacEnabled = (g_reg_2b_dac & 0x80) != 0 ? 1 : 0;
            Console.WriteLine(
                "[YM-OUT] min={0} max={1} avg={2:0.0} dacEnabled={3}",
                _ymOutMin, _ymOutMax, avg, dacEnabled);

            _ymOutMin = int.MaxValue;
            _ymOutMax = int.MinValue;
            _ymOutSum = 0;
            _ymOutCount = 0;
            _ymOutLastTicks = nowTicks;
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
                        * (1 << g_reg_a4_block[in_ch, w_slot_m])) >> 1;
                    int kc = g_slot_keycode[in_ch, w_slot_m];
                    double phaseInc = (finc + DT_TABLE[g_reg_30_dt[in_ch, w_slot], kc])
                        * g_reg_30_multi[in_ch, w_slot];
                    phaseInc *= YmStepScale;
                    g_slot_phase_inc_f[in_ch, w_slot] = phaseInc;
                    g_slot_phase_inc[in_ch, w_slot] = (int)phaseInc;
                    //envelop_generator
                    int ksr = kc >> g_reg_50_key_scale[in_ch, w_slot];
                    double envA = ENV_RATE_A_TABLE[g_slot_env_indexA[in_ch, w_slot] + ksr] * YmStepScale;
                    double envD = ENV_RATE_D_TABLE[g_slot_env_indexD[in_ch, w_slot] + ksr] * YmStepScale;
                    double envS = ENV_RATE_D_TABLE[g_slot_env_indexS[in_ch, w_slot] + ksr] * YmStepScale;
                    double envR = ENV_RATE_D_TABLE[g_slot_env_indexR[in_ch, w_slot] + ksr] * YmStepScale;
                    g_slot_env_incA_f[in_ch, w_slot] = envA;
                    g_slot_env_incD_f[in_ch, w_slot] = envD;
                    g_slot_env_incS_f[in_ch, w_slot] = envS;
                    g_slot_env_incR_f[in_ch, w_slot] = envR;
                    g_slot_env_incA[in_ch, w_slot] = (int)envA;
                    g_slot_env_incD[in_ch, w_slot] = (int)envD;
                    g_slot_env_incS[in_ch, w_slot] = (int)envS;
                    g_slot_env_incR[in_ch, w_slot] = (int)envR;
                }
            }
        }
        private void phase_generator(int in_ch)
        {
            for (int w_slot = 0; w_slot < NUM_SLOT; w_slot++)
            {
                double w_lfo_inc = 0;
                if (g_reg_22_lfo_enable == true)
                {
                    double lfoMul = (g_reg_b4_pms[in_ch] * g_com_lfo_freq_cnt) / (double)(1 << CNT_LOW_BIT);
                    w_lfo_inc = g_slot_phase_inc_f[in_ch, w_slot] * lfoMul;
                }
                g_slot_op_calc[in_ch, w_slot] = g_slot_freq_cnt[in_ch, w_slot];
                double total = g_slot_phase_frac[in_ch, w_slot] + g_slot_phase_inc_f[in_ch, w_slot] + w_lfo_inc;
                int step = (int)total;
                g_slot_phase_frac[in_ch, w_slot] = total - step;
                g_slot_freq_cnt[in_ch, w_slot] += step;
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

                double w_inc = 0;
                ENV_COND w_cond = g_slot_env_cond[in_ch, w_slot];
                switch (w_cond)
                {
                    case ENV_COND.ATTACK:
                        w_inc = g_slot_env_incA_f[in_ch, w_slot];
                        break;
                    case ENV_COND.DECAY:
                        w_inc = g_slot_env_incD_f[in_ch, w_slot];
                        break;
                    case ENV_COND.SUBSTAIN:
                        if (g_slot_env_cnt[in_ch, w_slot] < ENV_LEN_END)
                        {
                            w_inc = g_slot_env_incS_f[in_ch, w_slot];
                        }
                        break;
                    case ENV_COND.RELEASE:
                        if (g_slot_env_cnt[in_ch, w_slot] < ENV_LEN_END)
                        {
                            w_inc = g_slot_env_incR_f[in_ch, w_slot];
                        }
                        break;
                }
                double total = g_slot_env_frac[in_ch, w_slot] + w_inc;
                int step = (int)total;
                g_slot_env_frac[in_ch, w_slot] = total - step;
                g_slot_env_cnt[in_ch, w_slot] += step;
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
            // YM2612 key-on should reset phase/envelope for the slot.
            g_slot_freq_cnt[in_ch, in_slot] = 0;
            g_slot_phase_frac[in_ch, in_slot] = 0;
            g_slot_phase_out[in_ch, in_slot] = 0;
            g_slot_op_calc[in_ch, in_slot] = 0;
            if (in_slot == 0)
            {
                // Feedback uses slot 0 history.
                g_slot_phase_out[in_ch, 1] = 0;
            }

            if (g_slot_CNT_MASK[in_ch, in_slot])
            {
                g_slot_env_cnt[in_ch, in_slot] =
                    (int)(ENV_D2A[ENV_TABLE[g_slot_env_cnt[in_ch, in_slot] >> CNT_LOW_BIT]] + ENV_LEN_ATTACK);
            }
            else
            {
                g_slot_env_cnt[in_ch, in_slot] = ENV_LEN_ATTACK;
                g_slot_env_frac[in_ch, in_slot] = 0;
            }

            g_slot_CNT_MASK[in_ch, in_slot] = true;
            g_slot_env_cmp[in_ch, in_slot] = ENV_LEN_DECAY;
            g_slot_env_cond[in_ch, in_slot] = ENV_COND.ATTACK;
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
            // Match otheremumdemu-core: no extra scaling, DAC should be same range as FM samples
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
            // Sync YM2612 when Z80 executes, but not for every tiny cycle
            // Accumulate Z80 cycles and only sync when enough have passed
            
            if (z80Cycles <= 0)
                return;
            
            // Accumulate Z80 cycles
            _z80CycleAccumulator += z80Cycles;
            
            // Only sync when we have accumulated "enough" Z80 cycles
            // This prevents too many AdvanceTimersFromSystemCycles() calls but ensures YM2612 advances
            const int Z80_CYCLES_PER_SYNC = 256; // Sync every ~256 Z80 cycles
            
            if (_z80CycleAccumulator >= Z80_CYCLES_PER_SYNC)
            {
                // Reset accumulator
                _z80CycleAccumulator = 0;
                
                // DON'T call AdvanceTimersFromSystemCycles() here
                // It's now called from EnsureAdvanceEachFrame() each frame
                // This prevents double-counting and ensures consistent timing
                
                // Log for debugging
                if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_TIMING") == "1")
                {
                    Console.WriteLine($"[Z80-TIMING-SYNC] TickTimersFromZ80Cycles accumulated {z80Cycles} cycles (AdvanceTimersFromSystemCycles now frame-based)");
                }
            }
        }

        public void AdvanceTimersFromSystemCycles()
        {
            // Advance Timer A/B based on elapsed SystemCycles
            // This fixes "elastic music" where tempo depends on when audio is generated
            
            // Get current SystemCycles
            long currentSystemCycles = md_main.SystemCycles;
            
            // First call: initialize _lastSyncSystemCycles to current time
            // This prevents huge elapsedCycles on first call
            if (_lastSyncSystemCycles == 0)
            {
                _lastSyncSystemCycles = currentSystemCycles;
                return;
            }
            
            long elapsedCycles = currentSystemCycles - _lastSyncSystemCycles;
            
            if (elapsedCycles <= 0)
                return;
            
            // Update last sync cycle
            _lastSyncSystemCycles = currentSystemCycles;
            
            // Convert M68K cycles to YM2612 timer ticks
            // YM2612 clock: 7.67MHz (7670454 Hz)
            // Timer ticks at 72Hz (YM2612_CLOCK / 72)
            // So: elapsedCycles * (YM2612_CLOCK / 72) / M68K_CLOCK
            // Since YM2612_CLOCK ≈ M68K_CLOCK, simplifies to: elapsedCycles / 72
            // So 72 M68K cycles ≈ 1 timer tick at YM2612_CLOCK / 72 rate
            
            // Accumulate master cycles for timer ticks
            _timerTickFrac += elapsedCycles;
            
            // Calculate how many complete timer ticks we have
            // 72 M68K cycles = 1 YM2612 timer tick (at YM2612_CLOCK / 72 rate)
            const long CYCLES_PER_TIMER_TICK = 72;
            int ticks = (int)(_timerTickFrac / CYCLES_PER_TIMER_TICK);
            if (ticks <= 0)
                return;
                
            // Keep remainder for next time
            _timerTickFrac = _timerTickFrac % CYCLES_PER_TIMER_TICK;
            
            // Count this as a YM advance call (for DIAG-FRAME logging)
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_DIAG_FRAME") == "1" && ticks > 0)
            {
                md_main.IncrementYmAdvanceCalls();
            }
            
            // Debug logging for timer advancement
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM_TIMING") == "1")
            {
                Console.WriteLine($"[YM-TIMING-ADVANCE] elapsedCycles={elapsedCycles} ticks={ticks} timerA={_timerACount} SystemCycles={md_main.SystemCycles}");
            }
                
            StepTimers(ticks);
        }

         private void AdvanceTimersForSamples(int frames)
        {
            if (frames <= 0)
                return;

            double cyclesPerSample = (double)YM2612_CLOCK / YM2612_SAMPLING;
            double total = _sampleCycleFrac + (frames * cyclesPerSample);
            long deltaCycles = (long)total;
            _sampleCycleFrac = total - deltaCycles;
            AdvanceTimersFromDeltaCycles(deltaCycles);
        }

        private void AdvanceTimersFromDeltaCycles(long deltaCycles)
        {
            if (deltaCycles <= 0)
                return;

            _timerTickFrac += deltaCycles;
            const long CYCLES_PER_TIMER_TICK = 72;
            int ticks = (int)(_timerTickFrac / CYCLES_PER_TIMER_TICK);
            if (ticks <= 0)
                return;

            _timerTickFrac %= CYCLES_PER_TIMER_TICK;
            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_DIAG_FRAME") == "1" && ticks > 0)
            {
                md_main.IncrementYmAdvanceCalls();
            }
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
        
        public void EnsureAdvanceEachFrame()
        {
            AdvanceTimersFromSystemCycles();
        }
    }
}
