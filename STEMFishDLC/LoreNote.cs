using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  LoreNote - 世界里的档案纸片（P3）
//  ---------------------------------------------------------------------------
//  反汇编确认的游戏机制：
//    纸片物件 = Resources.Load("LoreNote") + Instantiate（WorldGeneration.GenerateLifePods 里撒的）
//    玩家点击 → PlayerCamera.TryPickupFromWorld → SendMessage("OnUse")
//    SurvivorNote.OnUse() → Clicked()：距离 ≤6 才播放，SetTimeScale(0.16倍) + 打字机 + 读完恢复
//
//  我们的做法：
//    ① 克隆 LoreNote prefab（外观/层级/音效都是原版）
//    ② 把 SurvivorNote 换成自己的 FishLoreNote 组件
//       —— SurvivorNote.Start() 会用 Locale.GetNote 随机覆盖文本，必须换掉才可控
//    ③ FishLoreNote.OnUse() 读我们指定的 entryId
//
//  ★ 重要：PatchAll 在本环境不可靠，这里用手动 patch（沿用项目约定）。
// ============================================================================

public static class LoreNote
{
    public static bool SpawnEnabled = true;        // 终端设置页可切

    private static bool _pending;                  // 世界生成完，等玩家就绪再撒
    private static bool _greeted;                  // 每局第一次落地的那句自言自语
    private static int _spawnedLayer = -1;
    private static readonly List<GameObject> _placed = new List<GameObject>();

    public static void Init(Plugin plugin)
    {
        try
        {
            var target = AccessTools.Method(typeof(WorldGeneration), "Awake");
            if (target == null)
            {
                Plugin.Log.LogError("[STEMFishDLC] 找不到 WorldGeneration.Awake");
                return;
            }
            var post = typeof(LoreNote).GetMethod("WorldAwake_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.lore").Patch(target, postfix: new HarmonyMethod(post));
            var info = Harmony.GetPatchInfo(target);
            Plugin.Log.LogInfo($"[STEMFishDLC] 档案生成 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 档案生成 hook 失败: {e}");
        }
    }

    // 世界生成完 → 标记待投放（此时玩家 body 可能还没就绪）
    private static void WorldAwake_Postfix()
    {
        _pending = true;
        _greeted = false;
        ClearPlaced();
    }

    public static void Update()
    {
        if (!_pending) return;
        if (!SpawnEnabled) { _pending = false; return; }
        var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) return;                  // 等玩家出生

        _pending = false;
        try
        {
            // 每局第一次落地：让角色自己想一句"终端里有东西"
            // （不提示按键，只是让他惦记着——玩家自然会去点那个图标）
            if (!_greeted)
            {
                _greeted = true;
                FishVoice.SayLine(FishVoice.RandomLine("lorefound", "终端里有东西。"));
            }

            int layer = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0;
            if (layer == _spawnedLayer) return;
            _spawnedLayer = layer;

            var entry = LoreData.NextUnread();
            if (entry == null)
            {
                Plugin.Log.LogInfo("[STEMFishDLC] 档案已全部归档，本层不再投放");
                return;
            }
            Spawn(entry, body.transform.position);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 投放档案失败: {e.Message}");
        }
    }

    // 在玩家附近撒一张纸片（稍微高一点，避免卡进地板）
    private static void Spawn(LoreData.Entry entry, Vector3 origin)
    {
        try
        {
            var prefab = Resources.Load<GameObject>("LoreNote");
            if (prefab == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 LoreNote prefab");
                return;
            }

            // 随机方向 + 6~11 单位（足够近能看见，又不至于一出生就踩到）
            float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float dist = UnityEngine.Random.Range(6f, 11f);
            var pos = new Vector3(origin.x + Mathf.Cos(ang) * dist, origin.y + Mathf.Sin(ang) * dist * 0.5f + 1.2f, origin.z);

            var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
            go.name = "STEMFishLore_" + entry.id;

            // 换掉 SurvivorNote（否则它 Start 时会用原版随机笔记覆盖文本）
            var orig = go.GetComponent<SurvivorNote>();
            if (orig != null)
            {
                orig.enabled = false;                  // 先禁用，避免销毁延迟导致 Start 抢跑
                UnityEngine.Object.Destroy(orig);
            }

            var mine = go.AddComponent<FishLoreNote>();
            mine.entryId = entry.id;

            // 未读档案给一点微光，方便在黑暗层里找到（读完就恢复正常观感）
            var sr = go.GetComponentInChildren<SpriteRenderer>();
            if (sr != null) sr.color = new Color(1f, 1f, 1f, 1f);

            _placed.Add(go);
            Plugin.Log.LogInfo($"[STEMFishDLC] 投放档案 {entry.id}「{entry.title}」于玩家附近 {dist:0.#}u");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成档案物件失败: {e.Message}");
        }
    }

    private static void ClearPlaced()
    {
        for (int i = 0; i < _placed.Count; i++)
        {
            var go = _placed[i];
            if (go != null) UnityEngine.Object.Destroy(go);
        }
        _placed.Clear();
        _spawnedLayer = -1;
    }
}

// ============================================================================
//  挂在纸片上的组件：点击 → 用我们的文本播放
// ============================================================================
public class FishLoreNote : MonoBehaviour
{
    public string entryId;

    // 游戏通过 SendMessage("OnUse") 触发（PlayerCamera.TryPickupFromWorld）
    public void OnUse()
    {
        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            if (Vector2.Distance(transform.position, body.transform.position) > 6f) return;

            var entry = LoreData.Get(entryId);
            if (entry == null)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 档案缺失: {entryId}");
                return;
            }
            LoreData.Show(entry);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 打开档案失败: {e.Message}");
        }
    }
}
