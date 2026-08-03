using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace STS2MobileIos.Patches;

// Adds a "Restart Room" button to the in-game pause menu (mobile equivalent of the
// Steam mod "Quick Restart 2"): reloads the most recent run save, dropping you back
// into the current room as it was when you entered it. Core convenience for save-scum
// ("sl") play on mobile where quitting to the main menu and hitting Continue by hand
// is tedious.
//
// Reload strategy — reuse the game's own, fully-tested reload path (the exact sequence
// a player performs manually as "Save & Quit is NOT used" -> quit to menu -> Continue):
//   1. NGame.ReturnToMainMenu(): fades to black, RunManager.CleanUp() (which clears
//      RunState so a fresh load is allowed), and loads the main menu behind the black
//      overlay. CRITICALLY, this does NOT write a save, so the on-disk checkpoint (the
//      room-entry autosave) is preserved untouched — this is what makes "restart room"
//      actually restart the room instead of persisting the messed-up current state.
//   2. NMainMenu.RefreshButtons(): re-reads the run save from disk into _readRunSaveResult.
//   3. Invoke NMainMenu.OnContinueButtonPressed(null): the identical code the Continue
//      button runs — SetUpSavedSingleplayer (increments the reload counter), networking
//      setup, NGame.LoadRun(...) and a fade-in into the reloaded room.
// Because ReturnToMainMenu leaves the screen faded to black and OnContinueButtonPressed
// fades back in only after the room is loaded, there is no visible main-menu flash.
public static class QuickRestartPatch
{
    private const string ButtonName = "RestartRoom";

    // 重开是一次完整场景重载(退主菜单→Continue)。两处防崩:
    //  ① 重入锁: 快速连点/淡出窗口内重复触发只执行一次(叠两条重载链必崩)。
    //  ② 场景释放帧等待: 见 ReloadCurrentRunAsync 注释。
    private static bool _reloadInProgress;

    // ReturnToMainMenu 内部 SetCurrentScene 把旧 NRun 从树里移除后 queue_free(帧末延迟释放),
    // 补丁若立刻同步发起下一次整场景重载会与"旧场景仍在释放"竞态 → 间歇 NRE(玩家手动
    // 退菜单→点Continue之间天然隔了几秒,不会踩到)。等若干帧让延迟释放跑完 + 新菜单 settle。
    private const int SettleFrames = 4;

    // postfix on MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu._Ready (iOS/移动版).
    // 加"一键重开"按钮 + 6 格存档位快照(时光回溯)。
    public static void ReadyPostfix(object __instance)
    {
        var saveBtn = AddRestartButton(__instance);
        if (saveBtn == null)
            return;
        try
        {
            var container = (Control)PatchHelper.Field(((Node)__instance).GetType(), "_buttonContainer")
                ?.GetValue(__instance);
            // Snapshot / rollback ("time-travel SL"): adds six buttons right below
            // Restart Room — Save Slot 1/2/3 and Load Slot 1/2/3.
            SnapshotPatch.AddButtons(container, saveBtn);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[QuickRestart] Snapshot buttons failed: {ex}");
        }
    }

    // postfix on NPauseMenu._Ready (桌面精简版): 只加"一键重开", 不带快照(菜单更干净)。
    public static void RestartOnlyPostfix(object __instance)
    {
        AddRestartButton(__instance);
    }

    // 复制原生"保存并退出"按钮(样式/脚本自动一致), 改名改文案, 接一键重开。
    // 返回原 saveBtn(供快照版复用), 失败/已存在返回 null。
    private static Node AddRestartButton(object __instance)
    {
        try
        {
            var pauseMenu = (Node)__instance;

            var container = (Control)PatchHelper.Field(pauseMenu.GetType(), "_buttonContainer")
                ?.GetValue(pauseMenu);
            var saveBtn = (Node)PatchHelper.Field(pauseMenu.GetType(), "_saveAndQuitButton")
                ?.GetValue(pauseMenu);

            if (container == null || saveBtn == null)
            {
                PatchHelper.Log("[QuickRestart] _buttonContainer/_saveAndQuitButton not found");
                return null;
            }

            // Guard against adding the button twice if _Ready ever runs again.
            if (container.GetNodeOrNull(ButtonName) != null)
                return null;

            // Duplicate without DUPLICATE_SIGNALS (flag 1) so the original's code-made
            // Released -> Save&Quit connection is not copied. Keeps groups(2), scripts(4)
            // and scene instantiation(8) => flags = 14.
            var dup = saveBtn.Duplicate(14);
            dup.Name = ButtonName;

            var label = dup.GetNodeOrNull<MegaLabel>("Label");
            if (label != null)
                label.SetTextAutoSize(RestartLabelText());

            container.AddChild(dup);
            // Place it directly under "Resume" (index 0) for quick reach during sl play.
            container.MoveChild(dup, 1);

            ((NClickableControl)dup).Released += OnRestartPressed;

            PatchHelper.Log("[QuickRestart] Added Restart Room button to pause menu");
            return saveBtn;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[QuickRestart] AddRestartButton failed: {ex}");
            return null;
        }
    }

    private static string RestartLabelText()
    {
        try
        {
            var locale = TranslationServer.GetLocale();
            if (!string.IsNullOrEmpty(locale) && locale.StartsWith("zh"))
                return "重开本房间";
        }
        catch
        {
            // fall through to English
        }
        return "Restart Room";
    }

    // Released signal handler (ReleasedEventHandler => one NClickableControl arg).
    private static void OnRestartPressed(NClickableControl _)
    {
        TaskHelper.RunSafely(ReloadCurrentRunAsync());
    }

    // Reloads the on-disk run save via the game's own, fully-tested path (the exact
    // sequence a player performs manually: quit to menu -> Continue). Shared by the
    // Restart Room button and by the snapshot "Load" button (which restores files
    // first, then reloads through here). No save is written along the way, so the
    // on-disk checkpoint is honoured verbatim.
    public static async Task ReloadCurrentRunAsync()
    {
        // ① 重入锁: 淡出/加载窗口内重复触发直接忽略,避免叠两条重载链(间歇崩的主因之一)。
        if (_reloadInProgress)
        {
            PatchHelper.Log("[QuickRestart] reload 已在进行中,忽略本次触发");
            return;
        }
        _reloadInProgress = true;
        try
        {
            var game = NGame.Instance;
            if (game == null)
            {
                PatchHelper.Log("[QuickRestart] NGame.Instance is null");
                return;
            }

            // Tear down to the main menu behind a black fade WITHOUT saving, so the
            // on-disk checkpoint is preserved and RunState is cleared for a fresh load.
            await game.ReturnToMainMenu();

            var menu = game.MainMenu;
            if (menu == null)
            {
                PatchHelper.Log("[QuickRestart] MainMenu not available after ReturnToMainMenu");
                return;
            }

            // ② 让旧 NRun 的延迟 queue_free 真正跑完 + 新菜单 settle,再发起下一次整场景重载。
            //    否则会与"旧场景仍在释放"竞态 → 间歇 NRE(见 SettleFrames 注释)。
            var tree = (SceneTree)Engine.GetMainLoop();
            if (tree != null)
            {
                for (int i = 0; i < SettleFrames; i++)
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            // Re-read the run save from disk into the main menu's cached result.
            menu.RefreshButtons();

            // Run the exact Continue-button logic (private) to reload the run.
            // OnContinueButtonPressed 内含存档有效性判空 + FadeOut,复用最稳。
            var mi = PatchHelper.Method(menu.GetType(), "OnContinueButtonPressed");
            if (mi == null)
            {
                PatchHelper.Log("[QuickRestart] OnContinueButtonPressed not found");
                return;
            }

            mi.Invoke(menu, new object[] { null });
            PatchHelper.Log("[QuickRestart] Reload triggered via Continue path");
        }
        catch (Exception ex)
        {
            // 打完整 ex(含堆栈): 万一仍崩,真机 devicectl --console 能直接定位。
            PatchHelper.Log($"[QuickRestart] ReloadCurrentRunAsync failed: {ex}");
        }
        finally
        {
            _reloadInProgress = false;
        }
    }
}
