using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  GlowFx - 一次性光点：淡出 + 轻微膨胀，结束后自毁
//  ---------------------------------------------------------------------------
//  ★ 必须是**顶层 public 类**：Unity 的 AddComponent 对嵌套/私有 MonoBehaviour
//    行为不可靠（可能返回 null 且只打一条警告），特效就会"生成了但看不见"。
//
//  ★ 不覆盖 localScale，而是**基于初始值**缩放：
//    载体是游戏的 Special/HitFlash 预制体，它自带一个非 1 的 localScale，
//    直接写 Vector3.one 会把光点放大/缩小成完全不同的尺寸。
// ============================================================================

public class GlowFx : MonoBehaviour
{
    private float _life = 0.1f;
    private float _t;
    private float _grow = 0.8f;          // 结束时膨胀到 (1 + grow) 倍
    private Vector3 _scale0 = Vector3.one;
    private SpriteRenderer _sr;
    private Color _c0 = Color.white;

    public void Setup(float life, float alpha0)
    {
        _life = Mathf.Max(0.01f, life);
        _scale0 = transform.localScale;
        if (_scale0.sqrMagnitude < 1e-6f) _scale0 = Vector3.one;

        _sr = GetComponent<SpriteRenderer>();
        if (_sr != null)
        {
            _c0 = _sr.color;
            _c0.a = Mathf.Clamp01(alpha0 <= 0f ? 1f : alpha0);
            _sr.color = _c0;
        }
        _t = 0f;
    }

    private void Update()
    {
        _t += Time.deltaTime;
        float k = _t / _life;
        if (k >= 1f)
        {
            Destroy(gameObject);
            return;
        }
        if (_sr != null)
        {
            var c = _c0;
            c.a = _c0.a * (1f - k);                    // 线性淡出
            _sr.color = c;
        }
        transform.localScale = _scale0 * (1f + k * _grow);
    }
}
