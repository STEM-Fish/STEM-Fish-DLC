using System;
using System.Collections.Generic;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  GunAudio - 把原版枪声加工成"更沉闷"的版本（MDRZ9 专用）
//  ---------------------------------------------------------------------------
//  反汇编确认：GunScript.Fire 里就是一行
//      Sound.Play(fireSound, transform.position, twoDimensional:true,
//                 pitchShift:true, follow:null, volume:1, pitch:1, ...)
//  → 枪声完全由 GunScript.fireSound 这个 AudioClip 决定，换 clip 即换声音。
//
//  做法：运行时把原 clip 的采样读出来（AudioClip.GetData），过两级一阶低通
//  （级联 ≈ 12dB/oct），把高频"炸裂感"削掉，再补一点增益，做成新 clip 挂回去。
//  读不到采样（压缩格式 / 被裁掉）时不做任何处理，保留原声，不报错不崩。
//
//  ⚠ GetData 只对"已解压进内存"的 clip 有效；如果是流式/压缩格式会返回 false。
// ============================================================================

public static class GunAudio
{
    private static readonly Dictionary<int, AudioClip> _cache = new Dictionary<int, AudioClip>();

    // cfg 可调（Plugin 启动时绑定）
    public static float Cutoff = 1250f;
    public static float Gain = 1.5f;
    public static int Stages = 3;

    // 三档预设（终端设置页点一下轮换一档）
    private static readonly float[] PresetCutoff = { 1900f, 1250f, 800f };
    private static readonly float[] PresetGain = { 1.25f, 1.5f, 1.8f };
    private static readonly int[] PresetStages = { 2, 3, 3 };
    private static readonly string[] PresetNames = { "轻（1900Hz ×2）", "中（1250Hz ×3）", "重（800Hz ×3）" };
    private static int _preset = 1;

    public static string PresetName =>
        (_preset >= 0 && _preset < PresetNames.Length) ? PresetNames[_preset] : "-";

    // 轮换档位：立刻改参数，之后生成/换弹时用新参数（已生成的枪不变，重进场景生效）
    public static void CyclePreset()
    {
        _preset = (_preset + 1) % PresetNames.Length;
        Cutoff = PresetCutoff[_preset];
        Gain = PresetGain[_preset];
        Stages = PresetStages[_preset];
        _cache.Clear();                              // 让下次生成重新加工
        Plugin.Log.LogInfo($"[STEMFishDLC] 枪声档位 → {PresetName}（{Cutoff:0}Hz ×{Stages} 级，增益 {Gain:0.##}）");
    }

    public static void Init(Plugin plugin)
    {
        try
        {
            Cutoff = Mathf.Clamp(plugin.Config.Bind("Guns", "DullCutoffHz", 1250f,
                "枪声低通截止频率（越低越闷，800~2000 之间试）").Value, 300f, 8000f);
            Gain = Mathf.Clamp(plugin.Config.Bind("Guns", "DullGain", 1.5f,
                "枪声补偿增益（低通会掉响度，1~2 之间）").Value, 0.5f, 3f);
            Stages = Mathf.Clamp(plugin.Config.Bind("Guns", "DullStages", 3,
                "低通级联级数（1~4，每级 6dB/oct，越多越闷）").Value, 1, 4);

            // 初始档位按配置里的数值猜一个，方便设置页显示得对
            _preset = 1;
            for (int i = 0; i < PresetCutoff.Length; i++)
                if (Mathf.Abs(PresetCutoff[i] - Cutoff) < 60f) { _preset = i; break; }
        }
        catch { }
    }

    // cutoff：低通截止频率；gain：补偿增益（低通会掉响度）
    // stages：级联级数，每级都是 6dB/oct（3 级 ≈ 18dB/oct，高频削得更狠）
    public static AudioClip MakeDull(AudioClip src, float cutoff = 1250f, float gain = 1.5f, int stages = 3)
    {
        if (src == null) return null;
        int key = src.GetInstanceID();
        if (_cache.TryGetValue(key, out var done)) return done;
        _cache[key] = null;                          // 先占位，失败不重复尝试

        AudioClip made = null;
        try
        {
            int ch = Mathf.Max(1, src.channels);
            int frames = src.samples;
            int total = frames * ch;
            if (total <= 0 || src.frequency <= 0)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 枪声 {src.name}: 采样数为 0，跳过");
                return null;
            }

            var data = new float[total];
            if (!src.GetData(data, 0))
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 枪声 {src.name}: GetData 失败（可能是压缩流式剪辑），保留原声");
                return null;
            }

            // 检查是不是真的读到了数据（压缩格式有时返回全 0 而不报错）
            bool any = false;
            for (int i = 0; i < total; i += 331)
                if (Mathf.Abs(data[i]) > 1e-5f) { any = true; break; }
            if (!any)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 枪声 {src.name}: 采样全为 0，保留原声");
                return null;
            }

            // 多级一阶低通级联：y += a*(x-y)
            //   三级把 2kHz 以上削掉 20dB 左右 → 只剩"闷"的低频冲击，中高频的脆响基本没了
            float fs = src.frequency;
            stages = Mathf.Clamp(stages, 1, 4);
            var a = new float[stages];
            for (int i = 0; i < stages; i++)
            {
                float fc = cutoff * Mathf.Pow(1.5f, i);        // 后面几级略宽，避免完全糊成一片
                a[i] = 1f - Mathf.Exp(-2f * Mathf.PI * fc / fs);
            }

            var y = new float[stages, ch];
            for (int f = 0; f < frames; f++)
            {
                int b = f * ch;
                for (int c = 0; c < ch; c++)
                {
                    float x = data[b + c];
                    for (int s2 = 0; s2 < stages; s2++)
                    {
                        y[s2, c] += a[s2] * (x - y[s2, c]);
                        x = y[s2, c];
                    }
                    // 软饱和：低频能量大，直接乘增益会削顶 → tanh 近似压一下更"厚"
                    data[b + c] = (float)Math.Tanh(x * gain);
                }
            }

            made = AudioClip.Create(src.name + "_dull", frames, ch, (int)fs, false);
            made.SetData(data, 0);
            Plugin.Log.LogInfo($"[STEMFishDLC] 枪声已加工: {src.name} → {made.name}（{frames} 帧 / {ch} 声道 / {fs}Hz，{stages} 级低通 {cutoff:0}Hz 起）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 枪声加工失败 {src?.name}: {e.Message}");
            made = null;
        }

        _cache[key] = made;
        return made;
    }
}
