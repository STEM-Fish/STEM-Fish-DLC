using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  StarterKit - 开局物资（只在新开一局时发一次）
//  ---------------------------------------------------------------------------
//  ★ 用户要求：不能"每次读档都发一次"。
//
//  反汇编确认的区分方式（PreRunScript）：
//    StartRun()      ← "开始新游戏"按钮
//    LoadRun()       ← "继续游戏"按钮
//    StartTutorial() ← 教程
//  → 在这三个入口各打一个 postfix，把 _wantKit 设成 true/false，
//    等世界生成完、玩家 body 就绪时按标记发放，发完立刻置 false。
//
//  另加一层保险：本档"已发放"记录写进 BepInEx 配置文件（按 runId 记），
//  这样即使中途崩了、重进同一局也不会重复发。
// ============================================================================

public static class StarterKit
{
    // 开局清单：id + 数量。背包/弹挂甲先发，后面的东西才有地方装
    private static readonly (string id, int count)[] Kit =
    {
        ("blackpack", 1),         // 黑色双肩包（我们的，容量更大、不损坏；先发，后面才装得下）
        ("platecarrier", 1),      // 战术弹挂甲（我们的）
        ("tachelmet", 1),         // 战术头盔（我们的）
        ("earplugs", 1),          // 战术耳塞（我们的，和头盔可同时戴）
        ("mdrz9", 1),             // MDRZ9
        ("mdrz9mag", 4),          // 满弹匣 ×4（优先塞进弹挂甲）
        ("flashlight", 1),        // 手电筒
        // 狗粮 + 纯净水 不再"拿在手上"，改成直接塞进双肩包（见 Grant）
    };

    private static bool _wantKit;                  // 本局要不要发
    private static bool _granted;                  // 本局已发过

    public static bool Enabled = true;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Modules", "StarterKit", true,
                "新开一局时发放开局物资（双肩包/弹挂甲/MDRZ9/弹药/手电/水/狗粮）").Value;

            var harmony = new Harmony("com.stemfish.dlc.starterkit");
            int n = 0;
            foreach (var name in new[] { "StartRun", "LoadRun", "StartTutorial" })
            {
                var m = AccessTools.Method(typeof(PreRunScript), name);
                if (m == null) { Plugin.Log.LogWarning($"[STEMFishDLC] 找不到 PreRunScript.{name}"); continue; }
                var pre = typeof(StarterKit).GetMethod("RunEntry_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(m, prefix: new HarmonyMethod(pre));
                n++;
            }
            Plugin.Log.LogInfo($"[STEMFishDLC] 开局物资 hook 完成 | {n} 个入口");
            PatchUnremovable(harmony);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 开局物资 hook 失败: {e}");
        }
    }

    // 三件"贴皮衣物"：不可脱下
    // -----------------------------------------------------------------------
    //  Body.DropWearable(Item) 是唯一的下装路径（右键/拖拽脱下都会走这里），
    //  拦掉它等于"穿死了"。物品本身 hitDurabilityLoss=0 + indestructible，打不坏。
    private static readonly string[] Unremovable = { "fishboots", "fishcoat", "fishpants" };

    private static void PatchUnremovable(Harmony harmony)
    {
        try
        {
            var m = AccessTools.Method(typeof(Body), "DropWearable", new[] { typeof(Item) });
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Body.DropWearable"); return; }
            var pre = typeof(StarterKit).GetMethod("DropWearable_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(m, prefix: new HarmonyMethod(pre));
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 贴身衣物不可脱下 hook 完成 | prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 不可脱下 hook 失败: {e.Message}");
        }
    }

    private static bool DropWearable_Prefix(Item item)
    {
        try
        {
            if (item == null || string.IsNullOrEmpty(item.id)) return true;
            for (int i = 0; i < Unremovable.Length; i++)
                if (item.id == Unremovable[i])
                {
                    FishVoice.SayLine(FishVoice.RandomLine("clothing", "这是长在身上的。"));
                    return false;
                }
        }
        catch { }
        return true;
    }

    // prefix 里读被调用的方法名，决定要不要发
    private static void RunEntry_Prefix(MethodBase __originalMethod)
    {
        try
        {
            string name = __originalMethod != null ? __originalMethod.Name : "";

            // 教程保持原版：不发装备、不改技能（v0.23.1）。
            // `StartTutorial` 是独立入口，原版教程本身就是教学关，塞一套装备反而破坏节奏。
            if (name == "StartTutorial")
            {
                _wantKit = false;
                _granted = true;
                PlayerSkills.OnRunEntry(false);
                Plugin.Log.LogInfo("[STEMFishDLC] 局入口 StartTutorial → 教程模式，不发开局物资、不改技能");
                return;
            }

            bool isNewRun = name == "StartRun";
            _wantKit = isNewRun;
            _granted = false;
            PlayerSkills.OnRunEntry(isNewRun);      // 新开局才拉技能

            // 原版角色：刷怪倍率 / 禁用名单 / 动态投放全部回到原版数值。
            // 我们模式的 SetMode 只服务 SF-07，但上一局留下的静态状态会跨局，
            // 所以在局入口再压一次。
            if (isNewRun && CharacterSelect.Current != CharacterSelect.Sf07) SpawnDirector.ResetToVanilla();
            Plugin.Log.LogInfo($"[STEMFishDLC] 局入口 {name} → 开局物资 {(_wantKit ? "待发" : "不发（读档）")}");
        }
        catch { }
    }

    // ==========================================================================
    //  世界就绪后发放
    // ==========================================================================
    public static void Tick()
    {
        if (!_wantKit || _granted) return;
        if (!Enabled) { _wantKit = false; return; }

        var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) return;                   // 等玩家出生

        // ★ 这套装备是 SF-07 的（设定上"为测试战斗数据发的"）。
        //   选原版 EXPie / Milky / Dune 时不该发。
        if (!CharacterSelect.IsSf07(body))
        {
            _granted = true;
            _wantKit = false;
            Plugin.Log.LogInfo("[STEMFishDLC] 开局物资：当前不是 SF-07，跳过");
            return;
        }

        _granted = true;
        _wantKit = false;
        try { Grant(body); }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 发放开局物资失败: {e.Message}"); }
    }

    private static void Grant(Body body)
    {
        var pos = (Vector2)body.transform.position;
        int ok = 0;

        // 1) 先放背包、弹挂甲、头盔（并立刻穿上/背上，后面才有空间装东西）
        var pack = SpawnAndPick(body, "blackpack", pos);
        if (pack != null)
        {
            ok++;
            try { if (!body.HasWearable(pack)) body.WearWearable(pack); }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 背双肩包失败: {e.Message}"); }
        }

        var helmet = SpawnAndPick(body, "tachelmet", pos);
        if (helmet != null)
        {
            ok++;
            try { if (!body.HasWearable(helmet)) body.WearWearable(helmet); }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 戴头盔失败: {e.Message}"); }
        }

        // 耳塞：独立槽位 "ears"，戴上不影响头盔
        var plugs = SpawnAndPick(body, "earplugs", pos);
        if (plugs != null)
        {
            ok++;
            try { if (!body.HasWearable(plugs)) body.WearWearable(plugs); }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 戴耳塞失败: {e.Message}"); }
        }

        var carrier = SpawnAndPick(body, "platecarrier", pos);
        if (carrier != null)
        {
            ok++;
            try { if (!body.HasWearable(carrier)) body.WearWearable(carrier); }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 穿弹挂甲失败: {e.Message}"); }
        }

        // 1.5) 贴身三件（不可脱下 / 不可破坏 / 不显示外观）
        foreach (var wid in Unremovable)
        {
            var cloth = SpawnAndPick(body, wid, pos);
            if (cloth == null) continue;
            ok++;
            try { if (!body.HasWearable(cloth)) body.WearWearable(cloth); }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 穿 {wid} 失败: {e.Message}"); }
        }

        // 2) 主武器
        var gun = SpawnAndPick(body, "mdrz9", pos);
        if (gun != null) ok++;

        // 3) 弹药：优先塞进弹挂甲容器（和补给逻辑一致）
        var cont = carrier != null ? carrier.GetComponent<Container>() : null;
        for (int i = 0; i < 4; i++)
        {
            var mag = AmmoCompat.SpawnMagazine(AmmoCompat.Mdrz9AmmoType, pos, int.MaxValue);
            if (mag == null) continue;
            if (cont != null && cont.CanHoldItem(mag)) { cont.LoadItem(mag); ok++; }
            else if (SpawnAndPick(body, null, pos, mag) != null) ok++;
        }

        // 4) 狗粮 + 纯净水：直接塞进双肩包（不占手）
        var packCont = pack != null ? pack.GetComponent<Container>() : null;
        foreach (var sid in new[] { "dogfood", "waterbottle" })
        {
            try
            {
                // ★ 原版物品必须用 Utils.Create（ItemRegistry.Create 只认我们自己注册的 id）
                var g = Utils.Create(sid, pos, 0f);
                var it = g != null ? g.GetComponent<Item>() : null;
                if (it == null) { Plugin.Log.LogWarning($"[STEMFishDLC] 生成 {sid} 失败"); continue; }
                if (packCont != null && packCont.CanHoldItem(it))
                {
                    packCont.LoadItem(it); ok++;
                    Plugin.Log.LogInfo($"[STEMFishDLC] 开局：{sid} 已放进双肩包");
                }
                else
                {
                    body.AutoPickUpItem(it);
                    ok++;
                    Plugin.Log.LogInfo($"[STEMFishDLC] 开局：{sid} 背包放不下，改放身上");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 装 {sid} 失败: {e.Message}"); }
        }

        // 5) 手电筒
        foreach (var (id, count) in Kit)
        {
            if (id == "blackpack" || id == "platecarrier" || id == "tachelmet" || id == "earplugs" || id == "mdrz9" || id == "mdrz9mag") continue;
            for (int i = 0; i < count; i++)
                if (SpawnAndPick(body, id, pos) != null) ok++;
        }

        Plugin.Log.LogInfo($"[STEMFishDLC] 开局物资已发放（{ok} 件）");

        // 6) 模式额外配发（「急速下坠」给一把金属镐，方便快速开路）
        var bonus = RunConfig.Current != null ? RunConfig.Current.bonusKit : null;
        if (bonus != null && bonus.Length > 0)
        {
            int extra = 0;
            foreach (var bid in bonus)
            {
                if (string.IsNullOrEmpty(bid)) continue;
                if (SpawnAndPick(body, bid, pos) != null) { ok++; extra++; }
            }
            if (extra > 0) Plugin.Log.LogInfo($"[STEMFishDLC] 模式额外配发 {extra} 件（{RunConfig.Current.label}）");
        }

        FishVoice.SayLine(FishVoice.RandomLine("starterkit", "配给就这些。省着点用。"));
    }

    // 生成一个原版物品并塞给玩家；id 为空时直接用传入的 item
    private static Item SpawnAndPick(Body body, string id, Vector2 pos, Item existing = null)
    {
        try
        {
            Item item = existing;
            if (item == null)
            {
                if (ItemRegistry.IsCustom(id))
                {
                    item = ItemRegistry.Create(id, pos);
                }
                else
                {
                    var go = Utils.Create(id, pos, 0f);
                    if (go != null) item = go.GetComponent<Item>();
                }
            }
            if (item == null)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 开局物资缺失: {id}");
                return null;
            }
            body.AutoPickUpItem(item);
            return item;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 发放 {id} 失败: {e.Message}");
            return null;
        }
    }
}
