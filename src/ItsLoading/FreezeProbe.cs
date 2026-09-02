using System;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

/// <summary>
/// 冻结窗口探针(诊断专用)。默认完全关闭:<c>ITSLOADING_PROBE=1</c> 才启用,
/// <c>ITSLOADING_PROBE_FIX=1</c> 追加 CATransaction.flush() 修复彩排。
/// 定位的问题:beta 启动同步跑在 Main::start 内、主循环零迭代期间,mod 突发的
/// present 全部静默蒸发(Ghidra m.14 实证):blit_render_targets_to_screen 前的
/// RenderingDevice::screen_prepare_for_drawing 返回错误即静默跳过 —— 引擎源码
/// 注释明示 resize 失败与 framebuffer 获取失败「允许静默」。本探针三件事:
///   1. 自解析主程序 dyld export trie 按名取符号(dlsym 对主程序导出不可见,实证)
///   2. 直调 screen_prepare_for_drawing(0) 拿 Error 码区分静默子路径
///      (20=ERR_CANT_CREATE 且无报错行 = framebuffer-nil 路;其他值 = resize 路)
///   3. ObjC 读主窗 CAMetalLayer 状态,验证「layer 未 settle」假设
/// 全部采样点在主线程钩子上(ObjC 与主程序符号都要求);异常一次即整体熔断。
/// </summary>
internal static class FreezeProbe
{
    private const string EnvProbe = "ITSLOADING_PROBE";
    private const string EnvFix = "ITSLOADING_PROBE_FIX";
    private const string EnvEvents = "ITSLOADING_PROBE_EVENTS";
    private const string EnvResize = "ITSLOADING_PROBE_RESIZE";

    // m.14 arm64 导出偏移,仅作自检锚点(游戏更新后失配 → 符号层自动降级)
    private const long SelfTestExpectOffset = 0x02A41878; // swap_buffers
    private const string SymSelfTest = "__ZN15RenderingDevice12swap_buffersEb";
    // 修复候选:主循环每迭代靠它落地 pending 的窗口操作(实测 resize 在突发后才生效)
    private const string SymProcessEvents = "__ZN18DisplayServerMacOS14process_eventsEv";
    private const string SymDsSingleton = "__ZN13DisplayServer9singletonE";

    internal static bool Enabled { get; private set; }

    private static bool _fix, _events, _resize, _broken, _objcOk, _symbolsOk;
    private static int _resizeCount;
    private static Vector2I? _baseSize;
    private static IntPtr _header, _trie, _trieEnd;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DProcessEvents(IntPtr displayServer);

    private static DProcessEvents _processEvents;
    private static IntPtr _dsSingletonStorage;

    // ================================================================ 初始化

    internal static void Init()
    {
        // ObjC 层与主程序 trie 都只在 macOS 有意义;其他平台/未设 env = 空操作
        if (System.Environment.GetEnvironmentVariable(EnvProbe) != "1" ||
            !OperatingSystem.IsMacOS())
        {
            return;
        }
        Enabled = true;
        _fix = System.Environment.GetEnvironmentVariable(EnvFix) == "1";
        _events = System.Environment.GetEnvironmentVariable(EnvEvents) == "1";
        _resize = System.Environment.GetEnvironmentVariable(EnvResize) == "1";
        _objcOk = true;
        ParseTrie();
        Log.Warn($"[ItsLoading][probe] init trie={_symbolsOk} fix={_fix} events={_events} " +
                 $"resize={_resize} header=0x{_header.ToString("x")}");
    }

    /// <summary>解析主程序 Mach-O 头与 export trie。slide 用 header 指针与
    /// __TEXT 预期 vmaddr 直算,不依赖任何额外的 dyld API。</summary>
    private static void ParseTrie()
    {
        _header = _dyld_get_image_header(0);
        if (_header == IntPtr.Zero || ReadU32(_header) != 0xFEEDFACF)
        {
            Log.Warn("[ItsLoading][probe] no mach header — symbols off");
            return;
        }
        uint ncmds = ReadU32(_header + 16);
        IntPtr cmdsEnd = _header + (int)ReadU32(_header + 20);
        ulong textVm = 0, leVm = 0, leFile = 0, trieOff = 0, trieSize = 0;
        bool haveTrie = false, haveLe = false;
        IntPtr p = _header + 32;
        for (uint i = 0; i < ncmds && p + 8 <= cmdsEnd; i++)
        {
            uint cmd = ReadU32(p);
            uint cmdsize = ReadU32(p + 4);
            if (cmdsize == 0) break;
            if (cmd == 0x19) // LC_SEGMENT_64
            {
                string seg = ReadAscii(p + 8, 16);
                ulong vm = ReadU64(p + 24);
                ulong fo = ReadU64(p + 40);
                if (seg == "__TEXT") textVm = vm;
                if (seg == "__LINKEDIT") { leVm = vm; leFile = fo; haveLe = true; }
            }
            else if (cmd == 0x80000033 || cmd == 0x80000022 || cmd == 0x37)
            {
                // LC_DYLD_EXPORTS_TRIE / LC_DYLD_INFO(_ONLY) 的 export 区
                ulong off = cmd == 0x80000033 ? ReadU32(p + 8) : ReadU32(p + 40);
                ulong size = cmd == 0x80000033 ? ReadU32(p + 12) : ReadU32(p + 44);
                if (size > 0) { trieOff = off; trieSize = size; haveTrie = true; }
            }
            p += (int)cmdsize;
        }
        if (!haveTrie || !haveLe || textVm == 0)
        {
            Log.Warn("[ItsLoading][probe] trie/segment not found — symbols off");
            return;
        }
        long slide = (long)_header - (long)textVm;
        _trie = (IntPtr)((long)leVm + ((long)trieOff - (long)leFile) + slide);
        _trieEnd = (IntPtr)((long)_trie + (long)trieSize);
        // 自检锚点:已知符号的偏移必须逐字命中(m.14 实测值;游戏更新后失配即降级,
        // 首 3 字节顺带打进日志辅助人工判读 —— 根三字节离线为 00 01 5F)
        long got = Lookup(SymSelfTest);
        bool pass = got == (long)_header + SelfTestExpectOffset;
        if (pass)
        {
            _processEvents = DelegateFor<DProcessEvents>(SymProcessEvents);
            _dsSingletonStorage = (IntPtr)Lookup(SymDsSingleton);
            _symbolsOk = _processEvents != null && _dsSingletonStorage != IntPtr.Zero;
        }
        Log.Warn($"[ItsLoading][probe] selfTest={pass} expect=0x{SelfTestExpectOffset:x} " +
                 $"got=0x{(got - (long)_header):x} root={ReadByte(_trie):x2}{ReadByte(_trie + 1):x2}" +
                 $"{ReadByte(_trie + 2):x2} symbols={_symbolsOk}");
    }

    private static T DelegateFor<T>(string symbol) where T : class
    {
        long addr = Lookup(symbol);
        if (addr == 0) return null;
        return Marshal.GetDelegateForFunctionPointer((IntPtr)addr, typeof(T)) as T;
    }

    // ================================================================ 采样

    // 主线程代理:主原生线程跑第一个托管线程(加载链宿主),ManagedThreadId==1;
    // 上一轮探针实证 thread=1 与 NGame.IsMainThread() 恒同值。ObjC 调用依赖它。
    private static bool OnMainThread =>
        System.Threading.Thread.CurrentThread.ManagedThreadId == 1;

    private static long _firstFrame = long.MinValue;

    /// <summary>冻结窗口闸门:帧计数器仍停在首个采样值 = 主循环未迭代。
    /// 返回当前冻结值(诊断标签用)。</summary>
    private static bool FrozenFrame(out long frozen)
    {
        long f = Engine.GetFramesDrawn();
        if (_firstFrame == long.MinValue) _firstFrame = f;
        frozen = _firstFrame;
        return f == _firstFrame;
    }

    /// <summary>主线程采样点统一入口:CAMetalLayer/NSWindow 状态 + Godot 侧
    /// 窗口尺寸 + 帧计数。gdWin 与 NSWindow 实测尺寸分道扬镳 = resize 请求已进
    /// Godot 记账、未落地 AppKit(pending 的直接证据)。
    /// 纯只读 —— screen_prepare_for_drawing 的直调已被移除:它有真实副作用
    /// (擦写 framebuffer 表 + present 排队中的 swapchain),在 mod 边界反复调用
    /// 会把 swapchain 永久搞挂(2026-09-01 实机事故:画面钉死在旧帧、菜单逻辑
    /// 照跑)。prepare 的错误码已由该次事故前的采样拿到,不再需要主动调。</summary>
    internal static void Sample(string tag)
    {
        if (!Enabled || _broken || !OnMainThread) return;
        try
        {
            string fix = "";
            if (_fix) { FlushCa(); fix += "+flush"; }
            if (_events && _symbolsOk)
            {
                // 修复候选①(已证伪):提前落地主循环本该处理的窗口事件
                _processEvents(ReadPtr(_dsSingletonStorage));
                fix += "+events";
            }
            if (_resize && FrozenFrame(out long frozen))
            {
                // 修复候选③:Metal 路的 drawable 池按 layer 尺寸惰性分配,同尺寸
                // 回弹会复用同一口 wedged 池,换尺寸才重建(logo-play 的大 resize
                // 实证有效)。wedge 会随获取次数重新形成 → 每次采样在 base 与
                // base+2 间交替,逐 mod 重建池(等价 stable 上持续 present 的池轮换)。
                // 冻结期 boot 窗 2px 抖动不可见;帧恢复即停,游戏自身 resize 定尺寸。
                if (_baseSize == null) _baseSize = DisplayServer.WindowGetSize();
                Vector2I b = _baseSize.Value;
                bool plus = (_resizeCount++ & 1) == 0;
                Vector2I target = plus ? new Vector2I(b.X + 2, b.Y + 2) : b;
                DisplayServer.WindowSetSize(target);
                fix += $"+resize{frozen}";
            }
            Vector2I gdSize = DisplayServer.WindowGetSize();
            Log.Warn($"[ItsLoading][probe] {tag}{fix} {LayerInfo()} " +
                     $"gdWin={gdSize.X}x{gdSize.Y} frame={Engine.GetFramesDrawn()}");
        }
        catch (Exception e)
        {
            _broken = true;
            Log.Warn($"[ItsLoading][probe] disabled after error at '{tag}': {e.Message}");
        }
    }

    /// <summary>修复彩排:显式事务提交 —— Apple 对「runloop 被阻塞」场景给出的
    /// 官方姿势(CATransaction.h 注释),用于检验 pending 的 layer 状态能否 settle。</summary>
    private static void FlushCa()
    {
        if (!_objcOk) return;
        IntPtr cls = objc_getClass("CATransaction");
        if (cls != IntPtr.Zero) ObjcVoid(cls, Sel("flush"));
    }

    private static string LayerInfo()
    {
        if (!_objcOk) return "layer=n/a";
        IntPtr nsAppClass = objc_getClass("NSApplication");
        IntPtr app = nsAppClass == IntPtr.Zero
            ? IntPtr.Zero
            : ObjcId(nsAppClass, Sel("sharedApplication"));
        IntPtr win = app == IntPtr.Zero ? IntPtr.Zero : ObjcId(app, Sel("mainWindow"));
        if (win == IntPtr.Zero)
        {
            // mainWindow 未定时退化取 windows[0]
            IntPtr list = app == IntPtr.Zero ? IntPtr.Zero : ObjcId(app, Sel("windows"));
            if (list != IntPtr.Zero && ObjcCount(list, Sel("count")) > 0)
            {
                win = ObjcIdAt(list, Sel("objectAtIndex:"), 0);
            }
        }
        if (win == IntPtr.Zero) return "win=none";
        IntPtr layer = ObjcId(ObjcId(win, Sel("contentView")), Sel("layer"));
        if (layer == IntPtr.Zero) return "layer=none";
        CGSize size = ObjcSize(layer, Sel("drawableSize"));
        double scale = ObjcDouble(layer, Sel("contentsScale"));
        bool hidden = ObjcBool(layer, Sel("hidden"));
        bool winVis = ObjcBool(win, Sel("isVisible"));
        CGRect frame = ObjcRect(win, Sel("frame"));
        // styleMask bit 0x10000 = NSWindowStyleMaskFullScreen
        ulong style = ObjcCount(win, Sel("styleMask"));
        return $"layer={size.Width:F0}x{size.Height:F0}@{scale:F1} " +
               $"win={frame.Width:F0}x{frame.Height:F0} fs={(style & 0x10000) != 0} " +
               $"layerHidden={hidden} winVis={winVis}";
    }

    // ================================================================ trie 走查

    /// <summary>按名查 export trie,返回运行时地址(0 = 未命中)。读全程限界在
    /// [trie, trieEnd),越界一律干净未命中 —— 探针级代码不允许读崩宿主。</summary>
    private static long Lookup(string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        return Walk(_trie, bytes, 0);
    }

    private static long Walk(IntPtr node, byte[] name, int pos)
    {
        // terminal 尺寸的 uleb 字节本身要先跳过;子节点从 terminal 数据之后开始
        int uLen = Uleb(node, out long termSize);
        if (uLen == 0) return 0;
        IntPtr termData = node + uLen;
        if (pos >= name.Length)
        {
            return termSize > 0 ? Terminal(termData) : 0;
        }
        IntPtr children = (IntPtr)((long)termData + termSize);
        if (children >= _trieEnd) return 0;
        int count = ReadByte(children);
        IntPtr q = children + 1;
        for (int i = 0; i < count && q < _trieEnd; i++)
        {
            int eLen = 0;
            while (q + eLen < _trieEnd && ReadByte(q + eLen) != 0) eLen++;
            if (q + eLen >= _trieEnd) return 0;
            bool match = eLen > 0 && pos + eLen <= name.Length;
            for (int k = 0; match && k < eLen; k++)
            {
                if (name[pos + k] != ReadByte(q + k)) match = false;
            }
            q += eLen + 1;
            int cLen = Uleb(q, out long childOff);
            if (cLen == 0) return 0;
            q += cLen;
            if (match)
            {
                // 子节点偏移相对 trie 起点(不是相对当前节点)
                IntPtr child = (IntPtr)((long)_trie + childOff);
                if (child < _trie || child >= _trieEnd) return 0;
                return Walk(child, name, pos + eLen);
            }
        }
        return 0;
    }

    private static long Terminal(IntPtr data)
    {
        int n = Uleb(data, out long flags);
        if (n == 0 || (flags & 0x3) != 0) return 0; // reexport/stub/resolver 不支持
        if (Uleb(data + n, out long off) == 0) return 0;
        return (long)_header + off; // image 偏移 → 运行时 = mach header + off
    }

    /// <summary>uleb128;越界(10 字节内没结束)返回 0 表示无效。</summary>
    private static int Uleb(IntPtr p, out long value)
    {
        value = 0;
        int shift = 0, n = 0;
        while (p + n < _trieEnd && n < 10)
        {
            byte b = ReadByte(p + n);
            n++;
            value |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return n;
            shift += 7;
        }
        return 0;
    }

    // ================================================================ 平台互操作

    // coreclr 上没有 "__Internal" 约定(Mono 专属);宿主库要显式点名。
    // libdyld 在共享缓存里,只能按完整路径 dlopen(coreclr 默认搜索路径全部落空,实证)。
    private const string LibDyld = "/usr/lib/system/libdyld.dylib";
    private const string LibObjc = "libobjc.A.dylib";

    [DllImport(LibDyld, EntryPoint = "_dyld_get_image_header")]
    private static extern IntPtr _dyld_get_image_header(uint index);

    [DllImport(LibObjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcId(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjcVoid(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern byte ObjcBoolRaw(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern double ObjcDouble(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern ulong ObjcCount(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcIdAt(IntPtr self, IntPtr sel, ulong index);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern CGSize ObjcSize(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern CGRect ObjcRect(IntPtr self, IntPtr sel);

    private static bool ObjcBool(IntPtr self, IntPtr sel) => ObjcBoolRaw(self, sel) != 0;

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr ReadPtr(IntPtr p) => Marshal.ReadIntPtr(p);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    private static byte ReadByte(IntPtr p) => Marshal.ReadByte(p);

    private static uint ReadU32(IntPtr p) => unchecked((uint)Marshal.ReadInt32(p));

    private static ulong ReadU64(IntPtr p) => unchecked((ulong)Marshal.ReadInt64(p));

    private static string ReadAscii(IntPtr p, int max)
    {
        var sb = new StringBuilder(max);
        for (int i = 0; i < max; i++)
        {
            byte b = Marshal.ReadByte(p + i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
