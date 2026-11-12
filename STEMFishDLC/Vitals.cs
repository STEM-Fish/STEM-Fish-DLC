using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  Vitals - 生理"变化速率"采样（原版健康面板看不到的东西）
//  ---------------------------------------------------------------------------
//  反汇编确认的可用字段（Body，全 public）：
//    bloodVolume(100=正常, <40 进入濒死) / totalBleedSpeed(>0.134 濒死)
//    fibrillationProgress(>1 濒死) / get_fibrillationRising()（缺氧/低压/低体温等触发）
//    temperature(危险 <29 或 >41) / tempDiffFromNormal
//    totalEncumberance + maxEncumberance（HUD 负重条用的就是这两个）
//    consciousness / consciousnessRiseRate / consciousnessFallRate
//    get_bleedClottingSpeed() / bleedingSpeedMultiplier
//
//  游戏并没有暴露"每秒变化量"，所以这里**自己采样求导**：
//  每 0.2s 取一次值，算 (new-old)/dt，再做指数平滑（EMA），避免数字乱跳。
// ============================================================================

public static class Vitals
{
    // 危险阈值（来自 Body.get_isDying 反汇编）
    public const float BloodVolumeCritical = 40f;
    public const float FibrillationCritical = 1f;
    public const float BleedCritical = 0.134f;
    public const float TempLowCritical = 29f;
    public const float TempHighCritical = 41f;

    // 速率（每秒变化量，正=上升，负=下降）
    public static float BloodVolumeRate { get; private set; }
    public static float TemperatureRate { get; private set; }
    public static float EncumberanceRate { get; private set; }
    public static float FibrillationRate { get; private set; }
    public static float ConsciousnessRate { get; private set; }

    private const float SampleInterval = 0.2f;
    private const float EmaAlpha = 0.4f;

    private static float _nextSample;
    private static float _t;
    private static bool _primed;
    private static float _pvBlood, _pvTemp, _pvEnc, _pvFib, _pvCons;

    public static void Tick()
    {
        try
        {
            float now = Time.unscaledTime;
            if (now < _nextSample) return;
            float dt = _primed ? (now - _t) : 0f;
            _nextSample = now + SampleInterval;

            var body = PlayerCamera.main?.body;
            if (body == null)
            {
                _primed = false;
                return;
            }

            float enc = Body_Encumberance(body);

            if (!_primed || dt <= 0.01f)
            {
                _primed = true;
                _t = now;
                _pvBlood = body.bloodVolume;
                _pvTemp = body.temperature;
                _pvEnc = enc;
                _pvFib = body.fibrillationProgress;
                _pvCons = body.consciousness;
                return;
            }

            float k = EmaAlpha;
            BloodVolumeRate = Ema(BloodVolumeRate, (body.bloodVolume - _pvBlood) / dt, k);
            TemperatureRate = Ema(TemperatureRate, (body.temperature - _pvTemp) / dt, k);
            EncumberanceRate = Ema(EncumberanceRate, (enc - _pvEnc) / dt, k);
            FibrillationRate = Ema(FibrillationRate, (body.fibrillationProgress - _pvFib) / dt, k);
            ConsciousnessRate = Ema(ConsciousnessRate, (body.consciousness - _pvCons) / dt, k);

            _t = now;
            _pvBlood = body.bloodVolume;
            _pvTemp = body.temperature;
            _pvEnc = enc;
            _pvFib = body.fibrillationProgress;
            _pvCons = body.consciousness;
        }
        catch { /* 采样失败绝不影响游戏 */ }
    }

    private static float Ema(float prev, float sample, float k) => Mathf.Lerp(prev, sample, k);

    // 负重（Body.totalEncumberance 是 public 字段，直接读）
    private static float Body_Encumberance(Body body)
    {
        try { return body.totalEncumberance; }
        catch { return 0f; }
    }

    // ==========================================================================
    //  血容量临界倒计时（秒）：按当前失血速率算多久掉到 40
    //  返回 -1 表示没在掉血；返回 0 表示已经低于阈值
    // ==========================================================================
    public static float BloodCriticalEta()
    {
        var body = PlayerCamera.main?.body;
        if (body == null) return -1f;
        if (body.bloodVolume <= BloodVolumeCritical) return 0f;
        // 只有"真在明显掉血"时才给倒计时（阈值太低会被体温/回血的微小抖动误触发）
        if (body.totalBleedSpeed < 0.002f) return -1f;
        float rate = BloodVolumeRate;
        if (rate >= -0.05f) return -1f;
        return (body.bloodVolume - BloodVolumeCritical) / Mathf.Abs(rate);
    }

    // 心跳失常多久恶化到临界（秒）；-1 = 没有恶化
    public static float FibrillationEta()
    {
        var body = PlayerCamera.main?.body;
        if (body == null) return -1f;
        if (body.fibrillationProgress >= FibrillationCritical) return 0f;
        float rate = FibrillationRate;
        if (rate <= 0.0005f) return -1f;
        return (FibrillationCritical - body.fibrillationProgress) / rate;
    }

    public static string FormatEta(float sec)
    {
        if (sec < 0f) return "—";
        if (sec <= 0f) return "已超阈值";
        if (sec >= 600f) return ">10min";
        if (sec >= 60f) return $"{Mathf.FloorToInt(sec / 60f)}分{Mathf.RoundToInt(sec % 60f)}秒";
        return $"{Mathf.RoundToInt(sec)}秒";
    }

    public static string FormatRate(float perSec, string unit)
    {
        if (Mathf.Abs(perSec) < 0.005f) return "—";
        string sign = perSec > 0f ? "+" : "";
        return $"{sign}{perSec:0.00}{unit}/s";
    }
}
