using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

/// <summary>
/// 主题画廊(Inc 8;菜单就绪后经 BaseLib「Theme gallery」入口打开):
/// 三列卡片网格 —— ThemePacks 已发现的每个主题一张卡(内置 + 外部包)。
/// 卡面 = 真 interpreter.gd + kit.gd 的 SubViewport:打开画廊时给每张卡同步
/// 应用伪启动剧本的中段快照,因此第一帧就有缩略图;
/// hover 卡片续播伪启动(工坊扫描 → 逐 mod 加载 → 读存档 → 开场 → 主菜单,
/// 进度跑满 100% 显示 Ready 后循环),移开即暂停定格;点击即写 cfg(TrySet,
/// 下次启动生效),当前主题卡蓝描边。容器/动画在此可用(帧流动期,非启动
/// 突发);画廊消费的正是 gd 渲染器本身。
/// </summary>
public static class ThemeGallery
{
    private const string KitPath = "user://itsloading/render/kit.gd";
    private const string InterpreterPath = "user://itsloading/render/interpreter.gd";

    private static readonly Color ActiveBorder = new(0.20f, 0.55f, 1.00f);
    private static readonly Color IdleBorder = new(1f, 1f, 1f, 0.22f);

    private static CanvasLayer _layer;
    private static Label _status;
    private static string _current; // 当前生效主题(点击应用后本地前移,蓝描边随之)
    private static GDScript _kitScript, _interpScript; // 每次 Show 各编译一次,卡间共享
    private static readonly List<Card> _cards = new();

    public static void Show()
    {
        ItsLoading.Run("show theme gallery", () =>
        {
            if (_layer != null) { Close(); return; }
            var tree = (SceneTree)Engine.GetMainLoop();
            Vector2 vs = tree.Root.GetVisibleRect().Size;

            _kitScript = ResourceLoader.Load<GDScript>(KitPath, "", ResourceLoader.CacheMode.Ignore);
            _interpScript = ResourceLoader.Load<GDScript>(InterpreterPath, "", ResourceLoader.CacheMode.Ignore);

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

            _status = new Label { Text = "" };
            _status.Position = new Vector2(48, vs.Y - 44);
            _status.Size = new Vector2(vs.X - 96, 32);
            _status.AddThemeFontSizeOverride("font_size", 14);
            _layer.AddChild(_status);

            // 三列卡片网格:卡宽按窗口均分;窗口装不下高度时竖向滚动
            const float margin = 48f, gap = 24f, top = 90f, bottom = 76f;
            float cardW = (vs.X - 2 * margin - 2 * gap) / 3f;
            float previewH = MathF.Round(cardW * 480f / 854f);
            var scroll = new ScrollContainer
            {
                Position = new Vector2(margin, top),
                Size = new Vector2(vs.X - 2 * margin, vs.Y - top - bottom),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            var grid = new GridContainer { Columns = 3 };
            grid.AddThemeConstantOverride("h_separation", (int)gap);
            grid.AddThemeConstantOverride("v_separation", (int)gap);
            scroll.AddChild(grid);
            _layer.AddChild(scroll);

            _current = ThemeRegistry.Current();
            foreach (var entry in ThemePacks.Discovered)
            {
                var card = new Card(entry, cardW, previewH);
                _cards.Add(card);
                grid.AddChild(card.Panel);
            }
            if (_cards.Count == 0) _status.Text = "no themes";
            RefreshBorders();

            tree.Root.AddChild(_layer);
            foreach (var card in _cards) card.ShowSnapshot();
        });
    }

    private static void RefreshBorders()
    {
        foreach (var card in _cards)
            card.SetBorder(card.Entry.Id == _current);
    }

    private static void Close()
    {
        foreach (var card in _cards) card.Retire();
        _cards.Clear();
        _layer?.QueueFree();
        _layer = null;
        _status = null;
        _current = null;
        _kitScript = null;
        _interpScript = null;
    }

    /// <summary>点击卡 = 应用:TrySet 写 cfg(下次启动生效),蓝描边前移。</summary>
    private static void Apply(Card card)
    {
        string id = card.Entry.Id;
        if (ThemeRegistry.TrySet(id))
        {
            _current = id;
            RefreshBorders();
            _status.Text = I18n.T("gallery.applied");
            Log.Warn($"[ItsLoading] gallery: theme '{id}' applied (next launch)");
        }
        else
        {
            _status.Text = I18n.T("gallery.applyFailed");
        }
    }

    // ================================================================ 卡片

    /// <summary>
    /// 一张主题卡:PanelContainer(描边随选中态)+ 预览 SubViewport + 名称/
    /// 作者/来源。interpreter、kit 与剧本驱动都常驻到画廊关闭;播放态只由
    /// 驱动的 Playing 表达:播放 = 逐帧 theme_apply,暂停 = 停推进(画面
    /// 自然定格)。视口更新模式恒为 Always、终生不变 —— 在该引擎上切换
    /// RenderTargetUpdateMode(Disabled 定格 → Always 复播)实测不可靠:
    /// 突发后 Disabled 得到黑卡,再切回 Always 也不复播(恒 Always 的卡
    /// 一切正常);静止内容本就无画布变化,恒 Always 即免费定格。
    /// </summary>
    private sealed class Card
    {
        public readonly ThemePacks.ThemeEntry Entry;
        public readonly PanelContainer Panel;
        public readonly SubViewport Viewport;
        public bool Built, Failed;

        private readonly Control _previewRoot;
        private readonly StyleBoxFlat _style;
        private Node _theme;   // interpreter 实例
        private GodotObject _kit;
        private PreviewDriver _driver;

        public Card(ThemePacks.ThemeEntry entry, float cardW, float previewH)
        {
            Entry = entry;
            const float pad = 10f;

            _style = new StyleBoxFlat
            {
                BgColor = new Color(1, 1, 1, 0.06f),
                ContentMarginLeft = pad,
                ContentMarginRight = pad,
                ContentMarginTop = pad,
                ContentMarginBottom = pad,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
            };
            _style.SetBorderWidthAll(1);
            _style.BorderColor = IdleBorder;

            Panel = new PanelContainer();
            Panel.AddThemeStyleboxOverride("panel", _style);
            Panel.CustomMinimumSize = new Vector2(cardW, previewH + 96f);
            Panel.MouseFilter = Control.MouseFilterEnum.Stop;

            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 2);
            box.MouseFilter = Control.MouseFilterEnum.Ignore;

            // 预览:设计空间主题等比缩进卡面;screen 主题(如 classic)按视口
            // 像素排布,与既有 854×480 预览同类等比变形 —— 缩略图与 hover 实况一致
            var svc = new SubViewportContainer
            {
                Stretch = true,
                CustomMinimumSize = new Vector2(cardW - 2 * pad, previewH),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            Viewport = new SubViewport
            {
                Size = new Vector2I((int)(cardW - 2 * pad), (int)previewH),
                // 恒 Always、终生不变(模式切换在该引擎上不可靠,见类注释);
                // 定格 = 驱动停推进,无画布变化画面自然冻结
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                TransparentBg = true, // 无 bg 元素的主题(classic)透出卡片底色
            };
            svc.AddChild(Viewport);
            _previewRoot = new Control();
            _previewRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _previewRoot.MouseFilter = Control.MouseFilterEnum.Ignore;
            Viewport.AddChild(_previewRoot);
            box.AddChild(svc);

            // 名称 / 作者 / 来源(meta 块可选;缺名回退 id,缺作者整行不显示)
            ThemeMetaDef meta = ThemeDef.ReadMeta(entry.Dir);
            var name = new Label { Text = meta?.Name ?? entry.Id };
            name.AddThemeFontSizeOverride("font_size", 17);
            name.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(name);
            if (!string.IsNullOrEmpty(meta?.Author))
            {
                var author = new Label { Text = meta.Author };
                author.AddThemeFontSizeOverride("font_size", 13);
                author.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.72f));
                author.MouseFilter = Control.MouseFilterEnum.Ignore;
                box.AddChild(author);
            }
            var source = new Label
            {
                Text = entry.ModId == ItsLoading.ModId ? I18n.T("gallery.builtin") : entry.ModId,
            };
            source.AddThemeFontSizeOverride("font_size", 12);
            source.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.52f));
            source.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(source);

            Panel.AddChild(box);
            Panel.MouseEntered += StartLive;
            Panel.MouseExited += StopLive;
            Panel.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                    Apply(this);
            };
        }

        public void SetBorder(bool active)
        {
            _style.SetBorderWidthAll(active ? 3 : 1);
            _style.BorderColor = active ? ActiveBorder : IdleBorder;
        }

        /// <summary>hover:续播(从暂停处继续;持续 hover 则循环)。</summary>
        public void StartLive()
        {
            if (Failed) return;
            if (!Built) ShowSnapshot();
            if (_driver == null) return;
            _driver.Playing = true;
        }

        /// <summary>unhover:暂停定格在当前帧(再 hover 从这里续播)。</summary>
        public void StopLive()
        {
            Pause();
        }

        /// <summary>打开画廊时立即应用伪启动中段快照。</summary>
        public void ShowSnapshot()
        {
            if (!Built && !Build()) return;
            EnsureDriver();
            _driver.Seek(BootScript.SnapshotAt);
        }

        private void Pause()
        {
            if (_driver != null) _driver.Playing = false;
        }

        private void EnsureDriver()
        {
            if (_driver != null) return;
            _driver = new PreviewDriver(_theme, _previewRoot);
        }

        public void Retire()
        {
            if (_driver != null)
            {
                _driver.Dispose();
                _driver = null;
            }
            if (_theme != null && GodotObject.IsInstanceValid(_theme))
                _theme.Call("theme_retire");
            if (_kit != null && GodotObject.IsInstanceValid(_kit))
                _kit.Free();
            _theme = null;
            _kit = null; // interpreter 节点随画廊树 QueueFree 一并释放
        }

        private bool Build()
        {
            if (_kitScript == null || _interpScript == null)
            {
                Failed = true;
                return false;
            }
            // kit 素材根 = 主题目录的上一级(与 boot.gd 的解析规则一致)
            _kit = _kitScript.New(Entry.Dir.GetBaseDir()).AsGodotObject();
            _theme = _interpScript.New().As<Node>();
            _previewRoot.AddChild(_theme);
            var ok = _theme.Call("theme_build", new Godot.Collections.Dictionary
            {
                { "root", _previewRoot },
                { "viewport", (Vector2)Viewport.Size },
                { "mod_dir", "" },
                // 与真实启动同一张本地化表:text.loc 键在预览里也显示本语言文案
                { "txt", Callable.From<string, string>(k => I18n.T(k)) },
                { "kit", _kit },
                { "theme_id", Entry.Id },
                { "mod_version", typeof(ItsLoading).Assembly.GetName().Version?.ToString() ?? "" },
                { "theme_dir", Entry.Dir },
                { "calib", false },
            });
            if (ok.VariantType != Variant.Type.Bool || !ok.AsBool())
            {
                Log.Warn($"[ItsLoading] gallery: theme '{Entry.Id}' preview unavailable");
                Retire();
                Failed = true;
                return false;
            }
            Built = true;
            return true;
        }
    }

    /// <summary>
    /// 预览剧本适配:BootScript(纯 BCL 的伪启动剧本,离线单测覆盖)接到
    /// interpreter 的 theme_apply。Playing 门控推进(hover = 播放,移开 =
    /// 定格暂停,再 hover 从暂停处续播);卡面的"静态缩略图"就是剧本推进到
    /// 中段后的定格,与实况同源。
    /// </summary>
    private sealed class PreviewDriver : IDisposable
    {
        private readonly Node _theme;
        private readonly BootScript _script;
        private readonly Timer _timer;
        private readonly Stopwatch _clock = new();
        private double _last;

        public bool Playing
        {
            get => _script.Playing;
            set
            {
                _script.Playing = value;
                _theme.Call("theme_set_playing", value);
                if (value)
                {
                    _last = 0;
                    _clock.Restart();
                    _timer.Start();
                }
                else
                {
                    _timer.Stop();
                    _clock.Stop();
                }
            }
        }

        public PreviewDriver(Node theme, Node parent)
        {
            _theme = theme;
            _script = new BootScript((key, args) => I18n.T(key, args));
            _timer = new Timer { WaitTime = 1.0 / 60.0 };
            _timer.Timeout += OnFrame;
            parent.AddChild(_timer);
            Playing = false;
        }

        private void OnFrame()
        {
            double now = _clock.Elapsed.TotalSeconds;
            Apply(_script.Advance(now - _last));
            _last = now;
        }

        /// <summary>跳到 t 并立即应用(静态预览用)。</summary>
        public void Seek(double t)
        {
            Apply(_script.Seek(t));
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Timeout -= OnFrame;
            _timer.QueueFree();
        }

        private void Apply(BootScript.Snapshot s)
        {
            var log = new Godot.Collections.Array();
            foreach (string line in s.Log) log.Add(line);
            _theme.Call("theme_apply", new Godot.Collections.Dictionary
            {
                { "overall", s.Overall },
                { "local", s.Local },
                { "indeterminate", s.Indeterminate },
                { "t", s.T },
                { "stage", s.Stage },
                { "stage_changed", s.StageChanged },
                { "step", s.Step },
                { "detail", s.Detail },
                { "log_entries", log },
            });
        }
    }

}
