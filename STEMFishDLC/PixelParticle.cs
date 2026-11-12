using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  PixelParticle - 命中时掉落的金橙色像素方块
//  ---------------------------------------------------------------------------
//  为什么自己写而不用 ParticleSystem：
//    · ParticleSystem 需要材质/贴图/prefab 配合，纯代码创建经常"配好了但不显示"，
//      前几版已经在这上面栽过三次
//    · 每粒一个 SpriteRenderer + 这个脚本，行为 100% 可控：
//      初速 → 重力下坠 → 空气阻尼 → 落地消失 → 淡出
//
//  视觉要点（作者要求"明亮的金橙色像素粒子掉落"）：
//    · 方块贴图用 FilterMode.Point，硬边、不糊
//    · 一开始全亮（alpha 1），后半段快速变暗；同时**轻微缩小**模拟"烧尽"
//    · 有重力 → 一定会往下掉，不是原地闪
// ============================================================================

public class PixelParticle : MonoBehaviour
{
    private Vector2 _vel;
    private float _life = 0.6f;
    private float _t;
    private float _gravity = -14f;      // 掉落感强一点（世界单位/秒²）
    private float _drag = 1.8f;         // 空气阻力，避免飞太远
    private SpriteRenderer _sr;
    private Color _c0;
    private Vector3 _scale0;

    public void Setup(Vector2 velocity, float life)
    {
        _vel = velocity;
        _life = Mathf.Max(0.08f, life);
        _t = 0f;

        _sr = GetComponent<SpriteRenderer>();
        if (_sr != null) _c0 = _sr.color;
        _scale0 = transform.localScale;
    }

    private void Update()
    {
        _t += Time.deltaTime;
        if (_t >= _life)
        {
            Destroy(gameObject);
            return;
        }

        float dt = Time.deltaTime;

        // 物理：重力 + 阻尼
        _vel.y += _gravity * dt;
        _vel *= Mathf.Max(0f, 1f - _drag * dt);

        var pos = transform.position;
        pos.x += _vel.x * dt;
        pos.y += _vel.y * dt;
        transform.position = pos;

        float k = _t / _life;

        // 颜色：前 40% 保持全亮，之后快速变暗（像余烬熄灭）
        if (_sr != null)
        {
            var c = _c0;
            c.a = k < 0.4f ? 1f : Mathf.Lerp(1f, 0f, (k - 0.4f) / 0.6f);
            // 顺便向暗红偏一点，更像火星/金橙余烬
            c.r = Mathf.Lerp(_c0.r, _c0.r * 0.85f, k);
            c.g = Mathf.Lerp(_c0.g, _c0.g * 0.25f, k);
            c.b = Mathf.Lerp(_c0.b, _c0.b * 0.15f, k);
            _sr.color = c;
        }

        // 尺寸：后半段缩小，模拟"烧尽"
        float shrink = k < 0.5f ? 1f : Mathf.Lerp(1f, 0.35f, (k - 0.5f) / 0.5f);
        transform.localScale = _scale0 * shrink;

        // 轻微旋转，像素颗粒看起来更"散"
        transform.Rotate(0f, 0f, 220f * dt * (0.5f + Mathf.Abs(_vel.x) * 0.1f));
    }
}
