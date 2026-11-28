using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine.Networking;
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
    private static AudioClip _reloadFull;      // 用户提供的 mp3 原声（换弹 + 拉栓整段）
    private static bool _fullTried;

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

    public static void PlayReloadOut()
    {
        // 优先用整段原声（换弹 + 拉栓），它自己就覆盖了整次换弹的时间线；
        // 没有这个文件时才退回我们合成的那两段 wav。
        if (PlayFull()) return;
        Play(ref _reloadOut, "reload_out.wav", "reload_out");
    }

    public static void PlayReloadIn()
    {
        if (_reloadFull != null) return;        // 整段里已经有拉栓声，别叠
        Play(ref _reloadIn, "reload_in.wav", "reload_in");
    }

    private static bool PlayFull()
    {
        if (!Enabled) return false;
        try
        {
            if (!_fullTried)
            {
                _fullTried = true;
                var dir = Dir();
                if (dir != null)
                {
                    var p = Path.Combine(dir, "reload.mp3");
                    _reloadFull = LoadMp3(p, "sfx_reload_full");
                    if (_reloadFull != null)
                        Plugin.Log.LogInfo($"[STEMFishDLC] 换弹原声已加载: reload.mp3（{_reloadFull.length:0.00}s / {_reloadFull.frequency}Hz）");
                }
            }
            if (_reloadFull == null) return false;

            EnsureSource();
            if (_src == null) return false;
            _src.PlayOneShot(_reloadFull, Mathf.Clamp01(Volume));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 播放原声失败: {e.Message}");
            return false;
        }
    }

    private static void EnsureSource()
    {
        if (_src != null) return;
        var go = new GameObject("STEMFishDLC_Sfx");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _src = go.AddComponent<AudioSource>();
        _src.playOnAwake = false;
        _src.spatialBlend = 0f;      // 2D
        _src.loop = false;
    }

    // ---- mp3：交给 Unity 自己解码（工程里没有 ffmpeg，也不该为了一个音效引入） ----
    private static AudioClip LoadMp3(string path, string name)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string url = "file:///" + path.Replace("\\", "/").Replace(" ", "%20");

            var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG);
            req.SendWebRequest();

            // 同步等一小会儿：48KB 的 mp3 解码只要几十毫秒，且只发生一次
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!req.isDone && sw.ElapsedMilliseconds < 4000) { }

            if (!req.isDone) { req.Abort(); return null; }
            if (req.result != UnityWebRequest.Result.Success)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] mp3 加载失败: {req.error}");
                return null;
            }

            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip != null) clip.name = name;
            return clip;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] mp3 解析失败: {e.Message}");
            return null;
        }
    }

    private static void Play(ref AudioClip clip, string file, string name)
    {
        if (!Enabled) return;
        try
        {
            // 不做"只试一次"的缓存：文件是随插件拷进来的，
            // 万一第一次因为路径/读写时序没读到，后面每次还能再试（File.Exists 很便宜）
            if (clip == null) clip = LoadWav(file, "sfx_" + name);
            if (clip == null) return;

            EnsureSource();
            if (_src == null) return;
            _src.PlayOneShot(clip, Mathf.Clamp01(Volume));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 音效播放失败: {e.Message}");
        }
    }

    private static string Dir()
    {
        // ★ 插件的 dll 就放在 BepInEx/plugins/ 下，而 sfx 在
        //   BepInEx/plugins/STEMFishDLC/sfx —— 所以不能直接拼 dll 目录 + "sfx"，
        //   那样找的是 plugins/sfx（不存在），音效就静默不播。
        //   这里把所有可能的位置都试一遍，取第一个真实存在的。
        var cands = new List<string>();
        try
        {
            var p = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(p))
            {
                cands.Add(Path.Combine(p, "STEMFishDLC", "sfx"));
                cands.Add(Path.Combine(p, "sfx"));
            }
        }
        catch { }
        try { cands.Add(Path.Combine(BepInEx.Paths.PluginPath, "STEMFishDLC", "sfx")); } catch { }
        try { cands.Add(Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx", "plugins", "STEMFishDLC", "sfx")); } catch { }

        for (int i = 0; i < cands.Count; i++)
            if (Directory.Exists(cands[i])) return cands[i];

        if (!_pathWarned)
        {
            _pathWarned = true;
            Plugin.Log.LogWarning("[STEMFishDLC] 找不到音效目录，试过: " + string.Join(" | ", cands));
        }
        return null;
    }

    private static bool _pathWarned;

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
