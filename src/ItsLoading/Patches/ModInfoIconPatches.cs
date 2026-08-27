using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

// ---------------------------------------------------------------- mod 菜单图标补丁
// Harmony id: com.somiona.sts2.itsloading.icon(架构拆分 #4:注册 + 钩子同居一族文件)
//
// 游戏读图标走 ResourceLoader("res://<id>/mod_image.png"),而导出版 Godot
// 无法加载未导入的裸 PNG(需要 BaseLib 那种 .godot/imported/*.ctex + remap 链)。
// 所以改为 patch mod 信息面板:从 mod 目录磁盘直接读图,运行时 Image API 认裸 PNG。

internal static class ModInfoIconPatches
{
    internal static void Install()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading.icon");
        var fill = AccessTools.Method(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer"),
            "Fill");
        if (fill == null)
        {
            Log.Warn("[ItsLoading] NModInfoContainer.Fill not found — icon patch skipped");
            return;
        }
        harmony.Patch(fill, postfix: new HarmonyMethod(typeof(ModInfoIconPatches), nameof(AfterModInfoFill)));
        Log.Warn("[ItsLoading] mod info icon patch installed");
    }

    private static void AfterModInfoFill(object __instance, Mod mod)
    {
        if (mod?.manifest?.id != "ItsLoading") return;
        ItsLoading.Run("set mod icon", () =>
        {
            string imgPath = Path.Combine(mod.path, "mod_image.png");
            if (!File.Exists(imgPath))
            {
                Log.Warn("[ItsLoading] mod_image.png not found next to dll: " + imgPath);
                return;
            }
            var image = Image.LoadFromFile(imgPath);
            var tex = ImageTexture.CreateFromImage(image);
            var rect = (TextureRect)AccessTools.Field(
                AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer"),
                "_image").GetValue(__instance);
            if (rect != null && tex != null)
            {
                rect.Texture = tex;
                Log.Warn("[ItsLoading] mod menu icon set from " + imgPath);
            }
        });
    }
}
