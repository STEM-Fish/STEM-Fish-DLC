using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace STEMFishDLC;

// ============================================================================
//  FishLog - 独立于插件生命周期的日志
//  ---------------------------------------------------------------------------
//  为什么需要它（v0.11.4 的现象）：
//    `BaseUnityPlugin.Logger` 是**归属插件对象**的 ManualLogSource。
//    一旦插件对象被销毁（v0.11.4 里就发生了），这个 Logger 会被 Dispose，
//    之后所有 Log 调用要么静默失效、要么抛异常 —— 现象就是
//    「日志在某一帧之后突然整段消失，游戏还在跑」。
//
//  做法：
//    · 用 `Logger.CreateLogSource()` 建一个**不属于任何插件**的源，它不会随插件销毁；
//    · 外面再包一层 try/catch + 文件后备：连日志系统都不可用时，
//      直接 append 到 `BepInEx/STEMFishDLC_runtime.log`，保证排障信息不丢。
// ============================================================================

public static class FishLog
{
    private static ManualLogSource _src;
    private static string _file;
    private static int _fileFailLeft = 200;

    public static void Init()
    {
        try { _src = BepInEx.Logging.Logger.CreateLogSource("STEM Fish DLC"); }
        catch { _src = null; }

        try { _file = Path.Combine(Paths.BepInExRootPath, "STEMFishDLC_runtime.log"); }
        catch { _file = null; }
    }

    // 供其它模块直接当普通日志源用（内部仍是带文件后备的那一份）
    public static ManualLogSource Source => Src;

    private static ManualLogSource Src
    {
        get
        {
            if (_src == null)
            {
                try { _src = BepInEx.Logging.Logger.CreateLogSource("STEM Fish DLC"); } catch { }
            }
            return _src;
        }
    }

    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warning, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    private static void Write(LogLevel level, string msg)
    {
        bool ok = false;
        try
        {
            var s = Src;
            if (s != null)
            {
                switch (level)
                {
                    case LogLevel.Warning: s.LogWarning(msg); break;
                    case LogLevel.Error: s.LogError(msg); break;
                    default: s.LogInfo(msg); break;
                }
                ok = true;
            }
        }
        catch { }

        // 日志系统不可用 → 写文件（BepInEx 的磁盘日志也是 append，这里独立一份便于排查）
        if (!ok) WriteFile(level, msg);

        // 警告以上级别同时留一份到文件，避免控制台日志被冲掉
        if (level != LogLevel.Info) WriteFile(level, msg);
    }

    private static void WriteFile(LogLevel level, string msg)
    {
        if (_fileFailLeft <= 0 || string.IsNullOrEmpty(_file)) return;
        _fileFailLeft--;
        try
        {
            File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
