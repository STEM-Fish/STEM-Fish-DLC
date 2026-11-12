using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace STEMFishDLC;

// ============================================================================
//  LightFx - 一次性 2D 光照：强度从峰值线性衰减到 0 后自毁
//  ---------------------------------------------------------------------------
//  为什么需要它（作者反馈："开枪时那条枪线没有影响游戏的光照渲染"）：
//    这个游戏用的是 **URP 2D 光照**（Light2D）。Sprite 的亮度只是"自己亮"，
//    **不会照亮周围环境**；要影响场景亮度必须加 Light2D。
//
//    游戏自己就是这么做的 —— 炮塔开枪（TurretScript.Update）：
//        fireLight.intensity = Mathf.Max(0, 80 - timeSinceFired * 300);
//    → 强度从 80 起、按 300/秒衰减，约 0.27 秒归零。本类照这个参数来。
//
//  ★ 为什么优先**克隆模板**而不是 AddComponent：
//    游戏里 Light2D 的所有配置（blendStyleIndex / affectedSortingLayers /
//    shapeLightFalloffSize / 排序层…）都写在 prefab 里，代码只改 intensity。
//    自己 AddComponent 出来的 Light2D 用默认值，很可能因为混合模式或"受影响的排序层"
//    不匹配而**完全不显示**。所以优先克隆 Body.radGlow（玩家身上的辐射光，
//    一定存在、一定配好了）；克隆体是独立对象，原组件每帧改 intensity 不影响我们。
// ============================================================================

public class LightFx : MonoBehaviour
{
    private float _life = 0.25f;
    private float _peak = 80f;
    private float _t;
    private Light2D _light;
    private bool _ready;
    private float _radius0 = 1f;    // 初始外半径
    private float _grow = 1f;       // 生命期内外半径扩张倍率（>1 表示向外扩散）

    // ==========================================================================
    //  模板
    //  ---------------------------------------------------------------------------
    //  ★★ 安全红线（作者明确要求：绝不能改原版光照，比如篝火）
    //    · 只**读取**模板来 Instantiate，从不写模板的任何属性
    //    · `affectedSortingLayers` 是**只读**属性，没法复制 → 所以必须靠克隆继承；
    //      但克隆前一定要确认模板不是"整个实体"，否则会复制出第二个玩家
    //    · 判定：模板自身挂着 Body / Limb / Item / BuildingEntity 就拒绝（说明它是实体根对象）
    //      实体**子物体**上的光可以克隆（Instantiate 会放到场景根，不会带父级）
    // ==========================================================================
    private static GameObject _template;
    private static bool _templateSearched;

    public static GameObject Template
    {
        get
        {
            if (_template != null || _templateSearched) return _template;
            _templateSearched = true;

            // ① 玩家身上的辐射光（配置最完整）
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                var glow = body != null ? body.radGlow : null;
                if (glow != null)
                {
                    var go = glow.gameObject;
                    if (IsSafeTemplate(go))
                    {
                        _template = go;
                        Plugin.Log.LogInfo($"[STEMFishDLC] 光照模板: {go.name}（玩家辐射光，只读克隆）");
                        return _template;
                    }
                    Plugin.Log.LogWarning($"[STEMFishDLC] radGlow 挂在实体根对象上（{go.name}），" +
                                          "为避免克隆出第二个玩家，改用自建 Light2D");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 取 radGlow 模板失败: {e.Message}"); }

            // ② 场景里任意一个**安全**的 Light2D
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Light2D>();
                if (all != null)
                {
                    foreach (var l in all)
                    {
                        if (l == null) continue;
                        var go = l.gameObject;
                        if (!IsSafeTemplate(go)) continue;
                        _template = go;
                        Plugin.Log.LogInfo($"[STEMFishDLC] 光照模板: {go.name}（场景中的 Light2D，只读克隆）");
                        return _template;
                    }
                }
            }
            catch { }

            _templateSearched = true;
            Plugin.Log.LogWarning("[STEMFishDLC] 没有可安全克隆的 Light2D，改用自建（排序层可能不匹配）");
            return _template;
        }
    }

    // 模板安全检查：绝不克隆整个实体
    private static bool IsSafeTemplate(GameObject go)
    {
        if (go == null) return false;
        try
        {
            // 自身挂着这些组件 → 它是实体根对象，克隆会复制出第二个实体
            if (go.GetComponent<Body>() != null) return false;
            if (go.GetComponent<Limb>() != null) return false;
            if (go.GetComponent<Item>() != null) return false;
            if (go.GetComponent<BuildingEntity>() != null) return false;

            // 层级太复杂也不是纯灯光容器
            if (go.transform.childCount > 6) return false;

            // 必须真的有 Light2D
            if (go.GetComponent<Light2D>() == null && go.GetComponentInChildren<Light2D>(true) == null) return false;

            return true;
        }
        catch { return false; }
    }

    public static bool Available => true;   // 类型是编译期引用，一定存在

    // 在 pos 生成一个点光源，强度 peak → 0 衰减，共 life 秒
    // grow > 1 时，外半径在生命期内扩张到 radius*grow（"光晕向外扩散"）
    public static LightFx Spawn(Vector3 pos, Color color, float peak, float radius, float life,
                                float falloff = -1f, float grow = 1f)
    {
        try
        {
            GameObject go = null;
            Light2D light = null;

            // ---- ① 克隆模板（只读，不写模板）----
            var tpl = Template;
            if (tpl != null)
            {
                go = UnityEngine.Object.Instantiate(tpl, pos, Quaternion.identity);
                go.name = "stemfish_light";

                // 光可能在子物体上
                light = go.GetComponent<Light2D>();
                if (light == null) light = go.GetComponentInChildren<Light2D>(true);

                if (light != null)
                {
                    // 二次保险：万一克隆体里混进了实体组件，立刻销毁重来
                    if (go.GetComponent<Body>() != null || go.GetComponent<Item>() != null)
                    {
                        Plugin.Log.LogWarning("[STEMFishDLC] 克隆体含实体组件，已丢弃并改为自建");
                        UnityEngine.Object.Destroy(go);
                        go = null;
                        light = null;
                    }
                    else
                    {
                        // 模板上的其它脚本（辐射逻辑等）一律关掉，避免它们改我们的光
                        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb == null || mb is Light2D || mb is LightFx) continue;
                            try { mb.enabled = false; } catch { }
                        }
                    }
                }
            }

            // ---- ② 兜底：自己建 ----
            if (light == null)
            {
                if (go == null) go = new GameObject("stemfish_light");
                go.transform.position = pos;
                light = go.GetComponent<Light2D>();
                if (light == null) light = go.AddComponent<Light2D>();
            }

            if (light == null) { if (go != null) UnityEngine.Object.Destroy(go); return null; }

            var fx = go.AddComponent<LightFx>();
            if (fx == null) return null;
            fx._light = light;
            fx._life = Mathf.Max(0.02f, life);
            fx._peak = Mathf.Max(0.02f, peak);
            fx.Configure(color, radius, falloff, grow);

            // 最终保险：确保操作的实例不是原版的光
            if (fx._light == null || fx._light.gameObject == Template)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 光照实例与原版重叠，已丢弃");
                UnityEngine.Object.Destroy(go);
                return null;
            }
            return fx;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 创建 Light2D 失败: {e.Message}");
            return null;
        }
    }

    private static bool _cfgDiagDone;

    private void Configure(Color color, float radius, float falloffOverride, float grow)
    {
        try
        {
            if (!_cfgDiagDone)
            {
                _cfgDiagDone = true;
                var src = Template;
                Plugin.Log.LogInfo($"[STEMFishDLC] 光照实例来源: 克隆自身的独立对象（模板={((src != null) ? src.name : "无")}，" +
                                   $"本体未被修改）");
            }

            // ★★★ 让光"向外柔和扩散"的唯一正解（v0.12.7 修正，之前方向全反了）
            //
            //   URP 2D 的光照衰减**不是**数学公式，而是查一张烘焙好的 LUT：
            //       mappedUV.x = 归一化剩余距离;  mappedUV.y = _FalloffIntensity;
            //       attenuation = SAMPLE_TEXTURE2D(_FalloffLookup, ...).r;
            //   也就是说 falloffIntensity 是"取哪一行曲线"的行号。
            //
            //   官方文档（Light 2D 组件参考）写得很直白：
            //       "Falloff Strength: 设置光强减弱的快慢。**衰减强度越高，光照边缘越柔和**"
            //   反汇编也确认 setter 是 Mathf.Clamp(v, 0f, 1f) —— 即**取值 0~1，1.0 最柔**。
            //
            //   所以 v0.12.0~v0.12.6 一路把 0.40 压到 0.10 是在往**最硬**的一端调：
            //   0.10 那一档的 LUT 曲线几乎是矩形（整个圆盘亮度一致、到了边缘垂直掉到 0）
            //   → 一块亮度均匀、边缘像刀切开的圆饼，这就是作者反复说的「亮斑」。
            //   正确区间是 0.85~1.0。
            //
            //   强度的量级参考游戏自己：radGlow（辐射光）0~1、营火 1~2，
            //   炮塔那个 80 是给远处固定炮塔用的，直接照抄会闪瞎。
            _light.lightType = Light2D.LightType.Point;      // 只有 Point 用 pointLightOuterRadius
            _light.color = color;
            _light.intensity = _peak;
            _radius0 = Mathf.Max(0.5f, radius);
            _grow = Mathf.Max(1f, grow);
            _light.pointLightOuterRadius = _radius0;
            // 内圈必须真正为 0：内圈是完全不衰减的区域，哪怕半径的 4%
            // 也会在中心形成一块硬芯
            _light.pointLightInnerRadius = 0.01f;
            _light.falloffIntensity = falloffOverride >= 0f
                ? falloffOverride
                : AutoFalloff(_peak);
            _light.shadowsEnabled = false;                   // 阴影很贵，闪光不需要
            _light.volumeIntensityEnabled = false;
            // 枪线上多个光源会在中间叠加：默认的 Additive 正好能连成一条光带
            _ready = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 配置 Light2D 失败: {e.Message}");
            _ready = false;
        }
    }

    // 强度 → falloffIntensity 的自动映射（0~1，越大越柔）
    // ---------------------------------------------------------------------------
    //  0.85~1.0 是"边缘完全化开"的区间；0.5 是 URP 出厂默认；低于 0.3 开始出现
    //  平顶亮盘（越逼近 0 越像一块边缘齐整的贴纸）。
    //  光越亮越容易糊成亮斑，所以亮度越高给的柔度越高，下限锁在 0.45。
    //  | 强度 | falloff |
    //  | 0.3  |  1.00   |
    //  | 1.0  |  1.00   |
    //  | 2.5  |  0.94   |
    //  | 5.0  |  0.83   |
    //  | 10   |  0.60   |
    public static float AutoFalloff(float peak)
    {
        float p = Mathf.Max(0f, peak);
        return Mathf.Clamp(1.05f - 0.045f * p, 0.45f, 1f);
    }

    private void Update()
    {
        if (!_ready || _light == null)
        {
            Destroy(gameObject);
            return;
        }

        _t += Time.deltaTime;
        float k = _t / _life;
        if (k >= 1f)
        {
            Destroy(gameObject);
            return;
        }

        try
        {
            // ---- 扩散：外半径随生命期张开（前段快、后段慢），光晕像被"推开" ----
            if (_grow > 1.001f)
                _light.pointLightOuterRadius = _radius0 * Mathf.Lerp(1f, _grow, Mathf.Sqrt(k));

            // ---- 衰减：从峰值开始指数下降，不留"恒定亮块" ----
            //   旧版前 25% 保持峰值，人眼会把它读成一个稳定发光的实体 → 亮斑感。
            //   ★ v0.12.9：指数从 2.8 放缓到 2.0。2.8 掉得太快（k=0.25 时只剩 50%），
            //     光一闪就没，看起来就是"不明显"。2.0 到 1/4 生命还有 61%，
            //     整条线能被眼睛抓到，但仍然没有"平台区"，不会变回亮块。
            float a = Mathf.Exp(-2.0f * k);
            if (k > 0.90f) a *= (1f - k) / 0.10f;    // 收尾压到 0，避免"啪"一下消失
            _light.intensity = Mathf.Max(0f, _peak * a);
        }
        catch { Destroy(gameObject); }
    }
}
