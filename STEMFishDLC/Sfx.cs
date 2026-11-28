using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  Sfx - 自己合成的换弹音效
//  ---------------------------------------------------------------------------
//  素材是 codex 现场合成的（sfx/*.wav，44.1kHz / 16-bit / 单声道）：
//    reload_out.wav  卸弹匣：低频闷响 + 金属轻碰
//    reload_in.wav   插弹匣 + 拉栓：三声带通噪声的金属咔哒
//
//  播放不走游戏自己的音效系统（那些 clip 都在 Resources 里、名字对不上），
//  而是自带一个 AudioSource + PlayOneShot —— 2D 音源（spatialBlend 0），
//  音量只受本模块的 Volume 控制，不会被 2D 衰减吃掉。
// ============================================================================

public static class Sfx
{
    public static bool Enabled = true;
    public static float Volume = 0.65f;

    private static AudioSource _src;
    private static AudioClip _reloadOut, _reloadIn;
    private static bool _tried;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Audio", "ReloadSfx", true,
                "换弹音效（卸弹匣 + 插弹匣拉栓）").Value;
            Volume = Mathf.Clamp(plugin.Config.Bind("Audio", "ReloadSfxVolume", 0.65f,
                "换弹音效音量（0~1）").Value, 0f, 1f);
        }
        catch { }
    }

    public static void PlayReloadOut() => Play(ref _reloadOut, "reload_out.wav", "reload_out");
    public static void PlayReloadIn() => Play(ref _reloadIn, "reload_in.wav", "reload_in");

    private static void Play(ref AudioClip clip, string file, string name)
    {
        if (!Enabled) return;
        try
        {
            if (!_tried)
            {
                _tried = true;
                _reloadOut = LoadWav("reload_out.wav", "sfx_reload_out");
                _reloadIn = LoadWav("reload_in.wav", "sfx_reload_in");
            }

            if (clip == null) clip = LoadWav(file, "sfx_" + name);
            if (clip == null) return;

            if (_src == null)
            {
                var go = new GameObject("STEMFishDLC_Sfx");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _src = go.AddComponent<AudioSource>();
                _src.playOnAwake = false;
                _src.spatialBlend = 0f;      // 2D
                _src.loop = false;
            }
            _src.PlayOneShot(clip, Mathf.Clamp01(Volume));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 音效播放失败: {e.Message}");
        }
    }

    private static string Dir()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var p = Path.GetDirectoryName(asm.Location);
            if (!string.IsNullOrEmpty(p)) return Path.Combine(p, "sfx");
        }
        catch { }
        try { return Path.Combine(BepInEx.Paths.PluginPath, "STEMFishDLC", "sfx"); } catch { }
        return null;
    }

    // ---- 最小 WAV 解析：只认 PCM 16-bit ----
    private static AudioClip LoadWav(string file, string name)
    {
        try
        {
            var dir = Dir();
            if (dir == null) return null;
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44) return null;

            int channels = 1, rate = 44100, bits = 16, pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int size = BitConverter.ToInt32(bytes, pos + 4);
                if (size < 0) break;

                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(bytes, pos + 10);
                    rate = BitConverter.ToInt32(bytes, pos + 12);
                    bits = BitConverter.ToInt16(bytes, pos + 22);
                }
                else if (id == "data")
                {
                    if (bits != 16) return null;
                    int count = size / 2;
                    int frames = channels > 0 ? count / channels : count;
                    if (frames <= 0) return null;

                    var data = new float[count];
                    int start = pos + 8;
                    for (int i = 0; i < count; i++)
                        data[i] = BitConverter.ToInt16(bytes, start + i * 2) / 32768f;

                    var clip = AudioClip.Create(name, frames, Mathf.Max(1, channels), rate, false);
                    clip.SetData(data, 0);
                    Plugin.Log.LogInfo($"[STEMFishDLC] 音效已加载: {file}（{frames} 帧 / {rate}Hz / {channels}ch）");
                    return clip;
                }

                pos += 8 + size + (size & 1);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 音效加载失败 {file}: {e.Message}");
        }
        return null;
    }
}
