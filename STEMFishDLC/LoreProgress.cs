using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  LoreProgress - 每下到新的一层，自动解锁一篇档案
//  ---------------------------------------------------------------------------
//  作者的反馈：世界里那张纸片太难碰到（随机位置 + 每局只投一张），
//  所以改成"层进度"驱动：**biomeDepth 涨一次，就补一篇未读的档案**。
//
//  为什么不用固定映射（第 3 层 = sf03）：
//    档案里 autoUnlock 的条目开局就在终端里，未读池是动态的；
//    按"下一个未读"发放永远不会跳号，也不会因为某篇已读而卡住。
//
//  纸片投放（LoreNote）保留：它会在开局投"再下一篇"，
//  捡到就等于提前解锁一条，和层解锁互补，不会互相顶掉（NextUnread 自动跳过已读）。
// ============================================================================

public static class LoreProgress
{
    public static bool Enabled = true;

    private static int _lastDepth = -9999;
    private static int _loggedDepth = -9999;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Lore", "AutoUnlockPerLayer", true,
                "每下到新的一层自动解锁一篇档案（关掉就只靠世界里的纸片）").Value;
            Plugin.Log.LogInfo($"[STEMFishDLC] 档案自动解锁: {(Enabled ? "按层推进" : "关闭")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 档案解锁配置读取失败: {e.Message}");
        }
    }

    public static void Tick()
    {
        if (!Enabled) return;

        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { }
        if (w == null) { _lastDepth = -9999; return; }

        // 世界还没生成完 / 主菜单 → 不动
        var pc = PlayerCamera.main;
        if (pc == null || pc.body == null) return;
        try { if (!w.worldExists) return; } catch { return; }

        int depth;
        try { depth = w.biomeDepth; } catch { return; }

        // 第一次进世界（新开局或读档）：把当前层当作起点，不额外补 ——
        // 开局那一篇由 LoreData 的 autoUnlock 条目负责，读档的进度在 .lore 旁文件里
        if (_lastDepth == -9999) { _lastDepth = depth; return; }

        // 回到上层（或被传送回浅层）→ 水位跟着退回去，下次往下再补
        if (depth < _lastDepth) { _lastDepth = depth; return; }
        if (depth <= _lastDepth) return;
        _lastDepth = depth;

        var e = LoreData.NextUnread();
        if (e == null)
        {
            if (_loggedDepth != depth)
            {
                _loggedDepth = depth;
                Plugin.Log.LogInfo("[STEMFishDLC] 档案已全部解锁，本层不再补");
            }
            return;
        }

        LoreData.MarkRead(e.id);
        try { FishVoice.SayLine(FishVoice.RandomLine("loremore", "……又多了一条。")); } catch { }
    }
}
