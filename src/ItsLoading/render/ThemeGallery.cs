using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

/// <summary>
/// 主题画廊(Inc 8;菜单就绪后经 BaseLib「Theme gallery」入口打开):
/// 列出 ThemePacks 已发现的全部主题(内置 + 外部包),右侧 SubViewport 用
/// 真 interpreter.gd + kit.gd 实时驱动所选主题(8 秒循环剧本:阶段推进 +
/// 不定进度 + 日志滚动),Apply 写 cfg 下次启动生效。
/// 容器/动画在此可用(帧流动期,非启动突发);预览即三渲染器共用同一份
/// theme.json 的活证 —— 画廊消费的正是 gd 渲染器本身。
/// </summary>
public static class ThemeGallery
{
    private const string KitPath = "user://itsloading/render/kit.gd";
    private const string InterpreterPath = "user://itsloading/render/interpreter.gd";

    private static CanvasLayer _layer;
    private static Godot.Collections.Dictionary _themeNode; // interpreter 实例(Node)
    private static PreviewDriver _driver;
    private static Label _status;
    private static string _selected;
    private static Control _previewRoot;

    public static void Show()
    {
        ItsLoading.Run("show theme gallery", () =>
        {
            if (_layer != null) { Close(); return; }
            var tree = (SceneTree)Engine.GetMainLoop();
            Vector2 vs = tree.Root.GetVisibleRect().Size;

            _layer = new CanvasLayer { Layer = 1200 };
            var dim = new ColorRect { Color = new Color(0, 0, 0, 0.92f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed) Close();
            };
            _layer.AddChild(dim);

            var title = new Label { Text = I18n.T("gallery.title") };
            title.Position = new Vector2(48, 24);
            title.AddThemeFontSizeOverride("font_size", 24);
            _layer.AddChild(title);

            var close = new Button { Text = I18n.T("gallery.close") };
            close.Position = new Vector2(vs.X - 180, 24);
            close.Pressed += Close;
            _layer.AddChild(close);

            // 左:主题列表(内置在前 —— ThemePacks 按 id 排序,来源标注)
            var list = new VBoxContainer { Position = new Vector2(48, 90) };
            _layer.AddChild(list);
            string current = ThemeRegistry.Current();
            foreach (var entry in ThemePacks.Discovered)
            {
                string id = entry.Id;
                bool builtin = entry.ModId == ItsLoading.ModId;
                var row = new HBoxContainer();
                var btn = new Button
                {
                    Text = id + (builtin ? "" : $"  ({entry.ModId})"),
                    ToggleMode = true,
                    ButtonPressed = id == current,
                };
                btn.Pressed += () =>
                {
                    foreach (var child in list.GetChildren())
                        if (child is HBoxContainer h && h.GetChild(0) is Button b && b != btn)
                            b.ButtonPressed = false;
                    Select(id);
                };
                row.AddChild(btn);
                list.AddChild(row);
            }

            // 右:预览(设计空间原尺寸 854×480,无 letterbox)+ Apply
            var preview = new SubViewportContainer
            {
                Position = new Vector2(vs.X / 2 + 40, 90),
                Size = new Vector2(854, 480),
                Stretch = true,
            };
            var vp = new SubViewport { Size = new Godot.Vector2I(854, 480) };
            preview.AddChild(vp);
            _previewRoot = new Control();
            _previewRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _previewRoot.MouseFilter = Control.MouseFilterEnum.Ignore;
            vp.AddChild(_previewRoot);
            _layer.AddChild(preview);

            var apply = new Button { Text = I18n.T("gallery.apply") };
            apply.Position = new Vector2(vs.X / 2 + 40, 590);
            apply.Pressed += () =>
            {
                if (_selected == null) return;
                if (ThemeRegistry.TrySet(_selected))
                {
                    _status.Text = I18n.T("gallery.applied");
                    Log.Warn($"[ItsLoading] gallery: theme '{_selected}' applied (next launch)");
                }
            };
            _layer.AddChild(apply);
            _status = new Label { Text = "" };
            _status.Position = new Vector2(vs.X / 2 + 40, 630);
            _status.AddThemeFontSizeOverride("font_size", 14);
            _layer.AddChild(_status);

            tree.Root.AddChild(_layer);

            Select(ThemePacks.DirOf(current) != null || IsBuiltin(current) ? current
                : (ThemePacks.Discovered.Count > 0 ? ThemePacks.Discovered[0].Id : null));
        });
    }

    private static bool IsBuiltin(string id) =>
        System.Linq.Enumerable.Any(ThemePacks.Discovered, e => e.Id == id && e.ModId == ItsLoading.ModId);

    private static void Close()
    {
        StopPreview();
        _layer?.QueueFree();
        _layer = null;
        _status = null;
        _previewRoot = null;
        _selected = null;
    }

    private static void Select(string id)
    {
        if (id == null) return;
        _selected = id;
        _status.Text = "";
        StopPreview();
        ItsLoading.Run($"preview theme {id}", () =>
        {
            string dir = ThemePacks.DirOf(id);
            if (dir == null) return;
            var kitScript = ResourceLoader.Load<GDScript>(KitPath, "", ResourceLoader.CacheMode.Ignore);
            var interpScript = ResourceLoader.Load<GDScript>(InterpreterPath, "", ResourceLoader.CacheMode.Ignore);
            if (kitScript == null || interpScript == null)
            {
                _status.Text = "render scripts missing";
                return;
            }
            // kit 素材根 = 主题目录的上一级(与 boot.gd 的解析规则一致)
            var kit = kitScript.New(dir.GetBaseDir()).AsGodotObject();
            var theme = interpScript.New().As<Node>();
            _previewRoot.AddChild(theme);
            _themeNode = new Godot.Collections.Dictionary
            {
                { "node", theme },
                { "kit", kit },
                { "dir", dir },
            };
            var ok = theme.Call("theme_build", new Godot.Collections.Dictionary
            {
                { "root", _previewRoot },
                { "viewport", new Vector2(854, 480) },
                { "mod_dir", "" },
                { "txt", Callable.From<string, string>(k => k) },
                { "kit", kit },
                { "theme_id", id },
                { "mod_version", typeof(ItsLoading).Assembly.GetName().Version?.ToString() ?? "" },
                { "theme_dir", dir },
                { "calib", false },
            });
            if (ok.VariantType == Variant.Type.Bool && !ok.AsBool())
            {
                _status.Text = "theme.json invalid";
                StopPreview();
                return;
            }
            _driver = new PreviewDriver(theme);
            _previewRoot.AddChild(_driver);
        });
    }

    private static void StopPreview()
    {
        if (_driver != null) { _driver.QueueFree(); _driver = null; }
        if (_themeNode != null)
        {
            if (_themeNode["node"].As<Node>() is { } n && GodotObject.IsInstanceValid(n))
                n.Call("theme_retire");
            if (_themeNode["kit"].AsGodotObject() is { } k && GodotObject.IsInstanceValid(k))
                k.Free();
            if (_themeNode["node"].As<Node>() is { } n2 && GodotObject.IsInstanceValid(n2))
                n2.QueueFree();
            _themeNode = null;
        }
    }

    /// <summary>预览剧本驱动:8 秒循环 —— 阶段 1→7、阶段内 0→1、尾部 1.5s 不定进度,
    /// 日志滚动;与启动期真实节奏同形(阶段推进 + 不定呼吸)。</summary>
    private sealed class PreviewDriver : Node
    {
        private readonly Node _theme;
        private double _t;
        private int _lastStage;
        private readonly Godot.Collections.Array _log = new();

        public PreviewDriver(Node theme) => _theme = theme;

        public override void _Process(double delta)
        {
            _t += delta;
            double cycle = _t % 8.0;
            int stage = Math.Clamp((int)(cycle / 8.0 * 7) + 1, 1, 7);
            double localFrac = (cycle % (8.0 / 7)) / (8.0 / 7);
            bool indeterminate = cycle > 6.5;
            bool stageChanged = stage != _lastStage;
            _lastStage = stage;
            if (stageChanged)
                _log.Add($"stage {stage}");
            else if (_log.Count < 8 && (int)(cycle * 2) != (int)((cycle - delta) * 2))
                _log.Add($"demo line {_log.Count}");
            _theme.Call("theme_apply", new Godot.Collections.Dictionary
            {
                { "overall", cycle / 8.0 },
                { "local", indeterminate ? 0.0f : (float)localFrac },
                { "indeterminate", indeterminate },
                { "t", cycle },
                { "stage", stage },
                { "stage_changed", stageChanged },
                { "step", $"[{stage}/7] preview" },
                { "detail", indeterminate ? "" : $"file {(int)(localFrac * 40)}/40" },
                { "log_entries", _log },
            });
        }
    }
}
