"""启动 WPF 程序并截取主窗口（纯 ctypes + GDI，不依赖 Pillow，输出 BMP）。"""
import ctypes
import subprocess
import sys
import time
from ctypes import wintypes

# 必须先声明 DPI 感知，否则在 150% 缩放下 GetWindowRect 返回的是虚拟化坐标，
# 截出来的图只有窗口左上角的一部分。
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)      # Win8.1+ 每显示器感知
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()       # 老系统兜底
    except Exception:
        pass

user32 = ctypes.WinDLL("user32", use_last_error=True)

# 截图缩放比（0.62 只适合看整体版面；要看细节传 --scale=1）
SCALE = 0.62

# --click=x,y 的坐标列表（客户区相对坐标，可重复传多次），空表示不点击
CLICKS = []

# --then-title=xxx：点完之后改抓标题包含 xxx 的窗口（用来抓点开后的模态对话框）
THEN_TITLE = ""

# --wheel=N：截图前向窗口滚轮滚动 N（正数向上，负数向下，一格约 120）
WHEEL = 0
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

user32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM), wintypes.LPARAM]
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]

gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
gdi32.BitBlt.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                         wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD]
gdi32.GetDIBits.argtypes = [wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
                            ctypes.c_void_p, ctypes.c_void_p, wintypes.UINT]
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.PrintWindow.argtypes = [wintypes.HWND, wintypes.HDC, wintypes.UINT]
user32.PrintWindow.restype = wintypes.BOOL


def find_window(pid):
    result = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n > 0:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                result.append((hwnd, buf.value))
        return True

    user32.EnumWindows(cb, 0)
    return result


def capture(hwnd, path, force_screen=False):
    """优先 PrintWindow（不怕被遮挡），失败回退 BitBlt。"""
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w = rect.right - rect.left
    h = rect.bottom - rect.top
    if w <= 0 or h <= 0:
        return None

    # 缩放（便于查看/传输）；--scale=N 可覆盖，看图标细节时用 1.0 或更大
    scale = SCALE
    w2 = max(1, int(w * scale))
    h2 = max(1, int(h * scale))

    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w2, h2)
    gdi32.SelectObject(hdc_mem, hbmp)

    SRCCOPY = 0x00CC0020
    HALFTONE = 4
    PW_RENDERFULLCONTENT = 0x00000002

    ok = False
    if hwnd and not force_screen:
        # 先按原尺寸渲染到临时位图，再缩放，避免直接缩放 PrintWindow 变形
        hdc_raw = gdi32.CreateCompatibleDC(hdc_screen)
        hbmp_raw = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
        gdi32.SelectObject(hdc_raw, hbmp_raw)
        if user32.PrintWindow(hwnd, hdc_raw, PW_RENDERFULLCONTENT):
            gdi32.SetStretchBltMode(hdc_mem, HALFTONE)
            gdi32.StretchBlt(hdc_mem, 0, 0, w2, h2, hdc_raw, 0, 0, w, h, SRCCOPY)
            ok = True
        else:
            print("  PrintWindow 失败，回退 BitBlt（可能截到遮挡物）")
        gdi32.DeleteObject(hbmp_raw)
        gdi32.DeleteDC(hdc_raw)

    if not ok:
        gdi32.SetStretchBltMode(hdc_mem, HALFTONE)
        gdi32.StretchBlt(hdc_mem, 0, 0, w2, h2, hdc_screen, rect.left, rect.top, w, h, SRCCOPY)

    w, h = w2, h2

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                    ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                    ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                    ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                    ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                    ("biClrImportant", wintypes.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # 负数 = 自上而下
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    stride = w * 4
    buf = ctypes.create_string_buffer(stride * h)
    gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bi), 0)

    # 写 32 位 BMP（BITMAPINFOHEADER + 像素 = 类似 24/32bit DIB）
    row_pad = (4 - (w * 4) % 4) % 4
    data_size = (stride + row_pad) * h
    file_size = 14 + 40 + data_size

    with open(path, "wb") as f:
        f.write(b"BM")
        f.write(file_size.to_bytes(4, "little"))
        f.write(b"\0\0\0\0")
        f.write((14 + 40).to_bytes(4, "little"))
        f.write((40).to_bytes(4, "little"))
        f.write(w.to_bytes(4, "little", signed=True))
        f.write((-h).to_bytes(4, "little", signed=True))
        f.write((1).to_bytes(2, "little"))
        f.write((32).to_bytes(2, "little"))
        f.write((0).to_bytes(4, "little"))
        f.write(data_size.to_bytes(4, "little"))
        f.write((2835).to_bytes(4, "little", signed=True))
        f.write((2835).to_bytes(4, "little", signed=True))
        f.write((0).to_bytes(4, "little"))
        f.write((0).to_bytes(4, "little"))
        f.write(buf.raw[:data_size])

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(0, hdc_screen)
    return (w, h)


def white_ratio(path):
    """统计像素里纯白的比例。WPF 的客户区被 PrintWindow 抓成白色时，只有标题栏是有内容的。"""
    try:
        data = open(path, "rb").read()
        body = data[54:]
        if len(body) < 1000:
            return 1.0
        samples = [body[i] for i in range(0, len(body), 499)]
        return sum(1 for b in samples if b == 0xFF) / len(samples)
    except Exception:
        return 1.0


def main():
    global SCALE, CLICKS, THEN_TITLE, WHEEL
    for a in sys.argv[4:]:
        if a.startswith("--scale="):
            SCALE = float(a.split("=", 1)[1])
        if a.startswith("--then-title="):
            THEN_TITLE = a.split("=", 1)[1]
        if a.startswith("--wheel="):
            WHEEL = int(a.split("=", 1)[1])
        if a.startswith("--click="):
            pair = a.split("=", 1)[1].split(",")
            CLICKS.append((int(pair[0]), int(pair[1])))

    exe = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else "ui.png"
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 5

    force = any(a == "--screen" for a in sys.argv[4:])
    extra = [a for a in sys.argv[4:] if not a.startswith("--title=") and not a.startswith("--scale=")
             and not a.startswith("--click=") and not a.startswith("--then-title=")
             and not a.startswith("--wheel=") and a != "--screen"]
    want = next((a.split("=", 1)[1] for a in sys.argv[4:] if a.startswith("--title=")), None)
    proc = subprocess.Popen([exe] + extra)
    print(f"启动 pid={proc.pid}，等待 {wait}s…")
    time.sleep(wait)

    wins = find_window(proc.pid)
    if not wins:
        print("未找到窗口（可能被安全策略阻止 GUI 显示）")
        proc.terminate()
        return 1

    if want:
        picked = [w for w in wins if want in w[1]]
        hwnd, title = picked[0] if picked else wins[0]
    else:
        hwnd, title = wins[0]
    print(f"窗口: {title!r}")
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.8)

    # --click=x,y：在窗口客户区相对坐标点一下（验收「选中行 → 操作按钮」这类交互）。
    # 用 PostMessage 直接投递鼠标消息，而不是全局 mouse_event：
    # 截图脚本没有前台焦点，全局点击常常打空，而带坐标的 WM_LBUTTONDOWN 由 WPF 自己做命中测试。
    for point in CLICKS:
        lparam = (point[1] << 16) | (point[0] & 0xFFFF)
        user32.PostMessageW(hwnd, 0x0200, 0, lparam)   # WM_MOUSEMOVE
        time.sleep(0.1)
        user32.PostMessageW(hwnd, 0x0201, 1, lparam)   # WM_LBUTTONDOWN（MK_LBUTTON）
        time.sleep(0.05)
        user32.PostMessageW(hwnd, 0x0202, 0, lparam)   # WM_LBUTTONUP
        time.sleep(0.7)

    # --wheel=N：向窗口投递滚轮消息，让长列表滚动到目标区域。
    # 用 PostMessage 而不是全局 mouse_event：截图脚本没有前台焦点，全局滚动常打空。
    if WHEEL:
        # lParam 用窗口中心（客户区坐标），wParam 高 16 位是 delta
        r = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        cx = (r.right - r.left) // 2
        cy = (r.bottom - r.top) // 2
        lparam = (cy << 16) | (cx & 0xFFFF)
        step = 120 if WHEEL > 0 else -120
        for _ in range(abs(WHEEL)):
            user32.PostMessageW(hwnd, 0x020A, (step & 0xFFFF) << 16, lparam)
            time.sleep(0.05)
        time.sleep(0.6)

    # 点完之后可能要抓的是新弹出的对话框（模态窗口是独立 hwnd）
    if THEN_TITLE:
        again = [w for w in find_window(proc.pid) if THEN_TITLE in w[1]]
        if again:
            hwnd, title = again[0]
            print(f"改抓: {title!r}")
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.5)

    size = capture(hwnd, out, force_screen=force)

    if size and not force:
        ratio = white_ratio(out)
        print(f"  白色像素占比 {ratio:.1%}")
        if ratio > 0.85:
            print("  客户区为空白，改用屏幕抓取重试")
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.8)
            size = capture(hwnd, out, force_screen=True)
            print(f"  重试后白色占比 {white_ratio(out):.1%}")

    print(f"截图: {out}  {size}" if size else "截图失败")

    proc.terminate()
    try:
        proc.wait(timeout=5)
    except Exception:
        proc.kill()
    print("已关闭进程")
    return 0


if __name__ == "__main__":
    sys.exit(main())
