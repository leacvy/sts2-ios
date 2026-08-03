using System;
using Godot;

namespace STS2MobileIos.Patches;

// [iOS 诊断 · 默认不织入] 定位"对局内无背景音乐"。
//
// ⚠️ 本补丁默认【不在 manifest.json 里】=不织入=运行时零开销。它的使命已完成(坐实了
//   FMOD .xcframework 被 Godot 判为静态库导致扩展不加载,见 移植修复记录.md 7.2)。保留此
//   文件供日后再查音频时按需启用——把下面两条加回 manifest.json 的 "patches" 数组即可:
//     { "targetType":"MegaCrit.Sts2.Core.Nodes.Audio.NRunMusicController",
//       "targetMethod":"UpdateMusic","kind":"postfix",
//       "hookType":"STS2MobileIos.Patches.AudioDiagnosticsPatch","hookMethod":"UpdateMusicPostfix" }
//     { "targetType":"MegaCrit.Sts2.Core.Nodes.Audio.NRunMusicController",
//       "targetMethod":"LoadActBank","kind":"postfix",
//       "hookType":"STS2MobileIos.Patches.AudioDiagnosticsPatch","hookMethod":"LoadActBankPostfix" }
//
// 资源审计结论(已确认): 战斗/各幕音乐走 FMOD,所有 bank 都已正确打进 pck 的
// banks/desktop/(Master、act1_a1/a2/b1、act2_a1/a2、act3_a1/a2、ambience、sfx…),
// 且项目设置 Fmod/General/banks_path = res://banks/desktop 与实际路径一致。FMOD 引擎
// framework 也由 deploy-slim 注入。故【资源层面没有漏处理】,故障在运行时加载。
//
// 运行链: NRunMusicController.UpdateMusic → LoadActBank(bankPath) → GDScript proxy
//         load_act_banks → FMOD 加载对应 act bank → update_music 播放。
//
// 本补丁在关键点打日志。一次真机 devicectl --console 抓 [AudioDiag] 即可分流根因:
//   • UpdateMusic 没出现        → 战斗根本没调用换曲(上游 gate/时机)
//   • currentTrack=<null> 或曲目空 → Act.BgMusicOptions 为空(数据/模型问题)
//   • LoadActBank 的 exists 全 false → bankPath 在 pck 里解析不到(路径/挂载问题)
//   • FMOD state HasSingleton=False → FMOD 扩展没起来(注入/加载问题)
//   • 以上都正常但仍无声        → 内存/播放层(desktop bank 在 iOS 解码或内存不足静默丢音)
public static class AudioDiagnosticsPatch
{
    private static bool _fmodStateLogged;

    // postfix on MegaCrit.Sts2.Core.Nodes.Audio.NRunMusicController.UpdateMusic
    public static void UpdateMusicPostfix(object __instance)
    {
        try
        {
            var track =
                PatchHelper.Field(__instance.GetType(), "_currentTrack")?.GetValue(__instance) as string;
            PatchHelper.Log($"[AudioDiag] UpdateMusic done, currentTrack={track ?? "<null>"}");
            LogFmodStateOnce();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[AudioDiag] UpdateMusicPostfix err: {ex.Message}");
        }
    }

    // postfix on MegaCrit.Sts2.Core.Nodes.Audio.NRunMusicController.LoadActBank(string bankPath)
    public static void LoadActBankPostfix(object __instance, string bankPath)
    {
        try
        {
            bool raw = !string.IsNullOrEmpty(bankPath) && Godot.FileAccess.FileExists(bankPath);
            string cand = "res://banks/desktop/" + (bankPath ?? "");
            string candBank = cand.EndsWith(".bank", StringComparison.Ordinal) ? cand : cand + ".bank";
            bool r1 = Godot.FileAccess.FileExists(cand);
            bool r2 = Godot.FileAccess.FileExists(candBank);
            PatchHelper.Log(
                $"[AudioDiag] LoadActBank('{bankPath}') exists.raw={raw} '{cand}'={r1} '{candBank}'={r2}");
            LogFmodStateOnce();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[AudioDiag] LoadActBankPostfix err: {ex.Message}");
        }
    }

    // FMOD 扩展是否真的注册进来了(注入成功=类型/单例存在)。只打一次,避免刷屏。
    private static void LogFmodStateOnce()
    {
        if (_fmodStateLogged)
            return;
        _fmodStateLogged = true;
        try
        {
            bool srv = Engine.HasSingleton("FmodServer");
            bool cls = ClassDB.ClassExists("FmodServer");
            bool emitter = ClassDB.ClassExists("FmodEventEmitter2D");
            PatchHelper.Log(
                $"[AudioDiag] FMOD state: HasSingleton(FmodServer)={srv} ClassExists(FmodServer)={cls} ClassExists(FmodEventEmitter2D)={emitter}");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[AudioDiag] FMOD state err: {ex.Message}");
        }
    }
}
