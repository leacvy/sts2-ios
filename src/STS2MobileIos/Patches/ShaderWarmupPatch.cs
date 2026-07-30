using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace STS2MobileIos.Patches;

// 首次启动预编译着色器,消除游戏中"遇到新画面时现场编译"的卡顿(尤其战局中卡牌首次打出)。
// 移植自安卓 ShaderWarmupScreen,做了多处 iOS 适配:
//  1. 不继承 Node/Control —— 本补丁在独立 assembly(STS2MobileIos),不在 Godot 主 assembly
//     的脚本注册表里,做成 Godot 节点其生命周期回调不会触发。改为纯静态逻辑 + SceneTree 信号驱动。
//  2. 去掉全部 UI,改用 PatchHelper.Log 输出进度。
//  3. 触发点: postfix NGame._EnterTree,async 后台跑,不阻塞。
//
// 【4GB 设备(iPad Pro 11" 2018 iPad8,1)内存实测结论 —— 决定了当前策略】
//  - 早期"全部材质+场景一次性 load 进缓存再统一编译" → 峰值爆表,被 iOS jetsam SIGKILL(signal 9)。
//  - 改流式(逐个 load→编译→释放)后: 【材质阶段(~2580 个,CacheMode.Ignore)实测能完整跑完不被杀】,
//    但【场景阶段(~947 个 .tscn)会累积内存被杀】—— 场景的 ext_resource 纹理依赖累加是主因;
//    换 IgnoreDeep 想绕开缓存累加,反而因逐材质重载共享依赖(churn)让材质阶段都更早崩。
//  - 且 Godot 在此 iOS 构建上 OS.GetMemoryInfo() 的 "available" 返回 -1(拿不到 os_proc_available_memory),
//    无法在循环里读余量自我节流。
//  => 故当前只做【材质阶段(普通 Ignore,唯一实测安全的配置)】,跳过场景阶段。卡牌着色器几乎都是
//     独立材质/.gdshader 资源,材质阶段即可覆盖,消除卡牌卡顿;场景阶段边角覆盖为避免 OOM 舍弃。
public static class ShaderWarmupPatch
{
    private const int WarmupVersion = 8;
    private const int BatchSize = 8;

    // 每加载 N 个资源就让一帧,防止首次从冷 pck 批量加载时主线程连续卡 >10s 触发看门狗。
    private const int YieldEvery = 4;

    private static bool _started = false;

    // postfix on MegaCrit.Sts2.Core.Nodes.NGame._EnterTree
    public static void EnterTreePostfix(Node __instance)
    {
        if (_started)
            return;
        _started = true;
        try
        {
            if (!NeedsWarmup())
            {
                PatchHelper.Log("[ShaderWarmup] 已预热过,跳过");
                return;
            }
            var tree = __instance.GetTree();
            if (tree == null)
            {
                PatchHelper.Log("[ShaderWarmup] SceneTree 不可用,跳过");
                return;
            }
            // fire-and-forget 后台流式预热(仅材质阶段,内存有界)
            _ = RunWarmup(tree);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 触发失败: {ex.Message}");
        }
    }

    private static bool NeedsWarmup()
    {
        try
        {
            var markerPath = Path.Combine(OS.GetUserDataDir(), "shader_warmup_version");
            if (File.Exists(markerPath))
                return File.ReadAllText(markerPath).Trim() != WarmupVersion.ToString();
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static void WriteVersionMarker()
    {
        try
        {
            File.WriteAllText(
                Path.Combine(OS.GetUserDataDir(), "shader_warmup_version"),
                WarmupVersion.ToString()
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 写 marker 失败: {ex.Message}");
        }
    }

    // 流式预热主流程(仅材质阶段)。内存有界: 任一时刻只驻留"当前批(<=BatchSize 个材质)"。
    private static async Task RunWarmup(SceneTree tree)
    {
        var sw = Stopwatch.StartNew();
        LogMemory("启动");
        SubViewport viewport = null;
        try
        {
            // 等主菜单先加载显示,减少与游戏启动的资源竞争
            for (int f = 0; f < 30; f++)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            viewport = new SubViewport
            {
                Size = new Vector2I(64, 64),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                TransparentBg = true,
            };
            tree.Root.AddChild(viewport);

            var whiteImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            whiteImage.SetPixel(0, 0, Colors.White);
            var whiteTex = ImageTexture.CreateFromImage(whiteImage);

            // 已编译着色器的 key 集合(只存字符串,极省内存)。用它去重,避免重复编译。
            var warmed = new HashSet<string>();
            // 当前批的预热节点: 渲染若干帧后统一释放,峰值只保留 <=BatchSize 个材质。
            var batch = new List<Node>(BatchSize);
            int compiled = 0;

            // 渲染两帧强制编译当前批,随后 QueueFree 并再让一帧确保回收,峰值不累积。
            async Task FlushBatch()
            {
                if (batch.Count == 0)
                    return;
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                foreach (var n in batch)
                    n.QueueFree();
                batch.Clear();
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            // 把一个材质纳入预热(仅新着色器才建节点)。材质随节点释放,不缓存。
            async Task WarmMaterial(Material mat)
            {
                if (mat == null)
                    return;
                string key;
                try
                {
                    key = GetShaderKey(mat);
                }
                catch
                {
                    return; // 材质已被回收等,跳过不致命
                }
                if (!warmed.Add(key))
                    return; // 该着色器已编译过
                Node node;
                try
                {
                    node = CreateWarmupNode(mat, whiteTex);
                }
                catch
                {
                    return;
                }
                if (node == null)
                    return;
                viewport.AddChild(node);
                batch.Add(node);
                compiled++;
                if (batch.Count >= BatchSize)
                    await FlushBatch();
            }

            // ── 材质/着色器资源文件(逐个 load→预热→释放,唯一实测安全的阶段)──
            var matPaths = new List<string>();
            CollectMaterialPaths("res://", matPaths);
            PatchHelper.Log($"[ShaderWarmup] 流式预热: {matPaths.Count} 个材质/着色器资源");
            for (int i = 0; i < matPaths.Count; i++)
            {
                await WarmMaterial(LoadMaterialStreaming(matPaths[i]));
                if (i % YieldEvery == 0)
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            await FlushBatch();

            viewport.QueueFree();
            viewport = null;
            WriteVersionMarker();
            LogMemory("结束");
            PatchHelper.Log(
                $"[ShaderWarmup] 完成: 编译 {compiled} 个着色器,耗时 {sw.ElapsedMilliseconds}ms。下次启动将跳过。"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 预热失败: {ex}");
            try
            {
                viewport?.QueueFree();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void LogMemory(string tag)
    {
        try
        {
            var info = OS.GetMemoryInfo();
            if (info == null)
                return;
            long phys = info.ContainsKey("physical") ? info["physical"].AsInt64() : -1;
            long avail = info.ContainsKey("available") ? info["available"].AsInt64() : -1;
            PatchHelper.Log(
                $"[ShaderWarmup] 内存({tag}) physical={(phys > 0 ? phys / 1024 / 1024 : phys)}MB "
                    + $"available={(avail > 0 ? avail / 1024 / 1024 : avail)}MB"
            );
        }
        catch
        {
            // 仅诊断用
        }
    }

    private static Node CreateWarmupNode(Material mat, ImageTexture whiteTex)
    {
        if (mat is ParticleProcessMaterial particleMat)
        {
            return new GpuParticles2D
            {
                ProcessMaterial = particleMat,
                Amount = 1,
                Emitting = true,
                OneShot = false,
                Texture = whiteTex,
            };
        }
        return new Sprite2D { Texture = whiteTex, Material = mat };
    }

    private static string GetShaderKey(Material mat)
    {
        if (mat is ShaderMaterial sm && sm.Shader != null)
            return sm.Shader.ResourcePath ?? sm.Shader.GetRid().ToString();
        if (mat is ParticleProcessMaterial)
            return $"particle#{mat.GetRid()}";
        return mat.ResourcePath ?? mat.GetRid().ToString();
    }

    // 只列候选资源路径(便宜的目录遍历, 不加载), 实际加载交给流式循环。
    private static void CollectMaterialPaths(string dirPath, List<string> outPaths)
    {
        try
        {
            using var dir = DirAccess.Open(dirPath);
            if (dir == null)
                return;
            dir.ListDirBegin();
            string fileName;
            while ((fileName = dir.GetNext()) != "")
            {
                if (fileName == "." || fileName == "..")
                    continue;
                var fullPath = $"{dirPath}/{fileName}";
                if (dir.CurrentIsDir())
                {
                    if (fileName == "debug")
                        continue;
                    CollectMaterialPaths(fullPath, outPaths);
                    continue;
                }
                var cleanName = fileName.Replace(".remap", "");
                if (
                    !cleanName.EndsWith(".tres")
                    && !cleanName.EndsWith(".gdshader")
                    && !cleanName.EndsWith(".material")
                )
                    continue;
                outPaths.Add($"{dirPath}/{cleanName}");
            }
            dir.ListDirEnd();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 枚举失败 {dirPath}: {ex.Message}");
        }
    }

    // 流式加载单个材质/着色器资源。CacheMode.Ignore: 不进资源缓存,本地引用一丢即回收,峰值不累积。
    // (实测: 材质阶段用 Ignore 能完整跑完;换 IgnoreDeep 会因逐材质重载共享依赖 churn 而更早 OOM。)
    private static Material LoadMaterialStreaming(string cleanPath)
    {
        try
        {
            if (!ResourceLoader.Exists(cleanPath))
                return null;
            if (cleanPath.EndsWith(".tres"))
            {
                var mat =
                    ResourceLoader.Load(cleanPath, "Material", ResourceLoader.CacheMode.Ignore)
                    as Material;
                if (mat != null)
                    return mat;
                var shader =
                    ResourceLoader.Load(cleanPath, "Shader", ResourceLoader.CacheMode.Ignore)
                    as Shader;
                return shader != null ? new ShaderMaterial { Shader = shader } : null;
            }
            var res = ResourceLoader.Load(cleanPath, null, ResourceLoader.CacheMode.Ignore);
            if (res is Material resMat)
                return resMat;
            if (res is Shader resShader)
                return new ShaderMaterial { Shader = resShader };
            return null;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 加载失败 {cleanPath}: {ex.Message}");
            return null;
        }
    }
}
