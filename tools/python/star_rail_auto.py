# -*- coding: utf-8 -*-
"""
星穹铁道 开战前自动操作脚本 (v2: 加入游戏启动 / 窗口识别 / 置前 / 按窗口截图)
依赖安装: pip install pydirectinput mss numpy keyboard opencv-python
请以【管理员身份】运行(游戏本体 StarRail.exe 要求管理员权限)，游戏建议窗口化/无边框全屏
热键: F8 = 手动触发"已进场"(没有模板图时使用)   F9 = 紧急停止
"""
import ctypes
import os
import time
import threading
import tkinter as tk
from tkinter import filedialog, messagebox
from ctypes import wintypes

# 必须在创建 Tk 窗口之前设置 DPI 感知，否则缩放屏幕上窗口坐标和截图坐标会对不上
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

import numpy as np
import mss
import pydirectinput
import keyboard

try:
    import cv2
except ImportError:
    cv2 = None

pydirectinput.PAUSE = 0
INTERVAL = 1.0                     # 每个操作之间的间隔(秒)
TEMPLATE_PATH = "enter.png"        # "角色进场"的模板截图(可选)
MATCH_THRESHOLD = 0.8
STUCK_DIFF_THRESHOLD = 1.5
STUCK_COUNT = 4
CHECK_PERIOD = 0.25
MAX_RUN_TIME = 20
DEFAULT_EXE_NAME = "StarRail.exe"  # 游戏本体进程名
LAUNCH_WAIT = 90                   # 启动游戏后最多等待窗口出现的秒数(与原工具一致)
FOCUS_WAIT = 10                    # 游戏不在前台时，最多等待多少秒让它回到前台

stop_flag = threading.Event()
trigger_flag = threading.Event()


# ======================= 游戏窗口 (移植自 GameLauncher.Core) =======================
user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

_HWND = wintypes.HWND
user32.IsWindow.argtypes = [_HWND]
user32.IsWindowVisible.argtypes = [_HWND]
user32.IsIconic.argtypes = [_HWND]
user32.ShowWindow.argtypes = [_HWND, ctypes.c_int]
user32.SetForegroundWindow.argtypes = [_HWND]
user32.GetForegroundWindow.restype = _HWND
user32.GetWindow.argtypes = [_HWND, wintypes.UINT]
user32.GetWindow.restype = _HWND
user32.GetWindowLongW.argtypes = [_HWND, ctypes.c_int]
user32.GetWindowLongW.restype = ctypes.c_long
user32.GetWindowThreadProcessId.argtypes = [_HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowRect.argtypes = [_HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetClientRect.argtypes = [_HWND, ctypes.POINTER(wintypes.RECT)]
user32.ClientToScreen.argtypes = [_HWND, ctypes.POINTER(wintypes.POINT)]
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.QueryFullProcessImageNameW.argtypes = [
    wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]

_WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, _HWND, wintypes.LPARAM)
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
GW_OWNER = 4
GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x80
SW_RESTORE = 9


def _image_path(pid):
    """进程完整路径；Windows 拒绝读取时返回 None。"""
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    try:
        buf = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buf))
        if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return buf.value
        return None
    finally:
        kernel32.CloseHandle(h)


def find_game_window(exe_name):
    """
    找属于 exe_name 的顶层窗口: 可见、无 owner、非工具窗口、尺寸有效(最小化也算)。
    只按"进程文件名"匹配，绝不只凭窗口标题。多个候选取面积最大的。
    """
    target = exe_name.strip().lower()
    found = []

    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        if user32.GetWindow(hwnd, GW_OWNER):
            return True
        if user32.GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW:
            return True
        rect = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        area = (rect.right - rect.left) * (rect.bottom - rect.top)
        if not (user32.IsIconic(hwnd) or area > 1):
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        path = _image_path(pid.value)
        if path and os.path.basename(path).lower() == target:
            found.append((area, hwnd))
        return True

    user32.EnumWindows(_WNDENUMPROC(cb), 0)
    return max(found)[1] if found else None


def is_foreground(hwnd):
    return bool(user32.IsWindow(hwnd)) and user32.GetForegroundWindow() == hwnd \
        and not user32.IsIconic(hwnd)


def activate_window(hwnd, timeout=1.5):
    """还原最小化并请求置前，然后核实结果(Windows 可能拒绝)。"""
    if not user32.IsWindow(hwnd):
        return False
    if user32.IsIconic(hwnd):
        user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetForegroundWindow(hwnd)
    end = time.time() + timeout
    while time.time() < end:
        if is_foreground(hwnd):
            return True
        time.sleep(0.1)
    return False


def client_region(hwnd):
    """游戏画面(客户区)在屏幕上的位置，供 mss 截图使用。"""
    rect = wintypes.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(rect))
    pt = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    return {"left": pt.x, "top": pt.y, "width": rect.right, "height": rect.bottom}


def ensure_focus(hwnd, set_status):
    """发送任何按键之前确认游戏在前台；不在就尝试置前，最多等 FOCUS_WAIT 秒(期间可手动点游戏窗口)。"""
    end = time.time() + FOCUS_WAIT
    warned = False
    while not stop_flag.is_set() and time.time() < end:
        if not user32.IsWindow(hwnd):
            set_status("游戏窗口已关闭，已中止")
            return False
        if is_foreground(hwnd):
            return True
        if not warned:
            set_status("游戏不在前台，尝试切回(也可手动点击游戏窗口)...")
            warned = True
        activate_window(hwnd, 0.5)
    if not stop_flag.is_set():
        set_status("游戏一直不在前台，已中止(避免按键发到别的窗口)")
    return False


def prepare_game(cfg, set_status):
    """按配置启动游戏(可选)，找到游戏窗口并置前。返回 hwnd，失败返回 None。"""
    exe_name = cfg["exe_name"] or DEFAULT_EXE_NAME

    if cfg["launch"]:
        path = cfg["path"]
        set_status("正在启动游戏(如弹出 UAC 请确认)...")
        try:
            os.startfile(path)  # 等价于资源管理器里双击，支持 .exe 和 .lnk
        except OSError as e:
            if getattr(e, "winerror", None) == 1223:
                set_status("已取消启动(UAC 被拒绝)")
            else:
                set_status(f"启动失败: {e}")
            return None
        end = time.time() + LAUNCH_WAIT
        hwnd = None
        while not stop_flag.is_set() and time.time() < end:
            hwnd = find_game_window(exe_name)
            if hwnd:
                break
            left = int(end - time.time())
            set_status(f"等待游戏窗口出现... 剩余 {left}s  (如用启动器，请点击\"开始游戏\")")
            time.sleep(1.0)
        if not hwnd:
            if not stop_flag.is_set():
                set_status(f"{LAUNCH_WAIT} 秒内未检测到 {exe_name} 的窗口")
            return None
    else:
        hwnd = find_game_window(exe_name)
        if not hwnd:
            set_status(f"未检测到已运行的游戏({exe_name})，请先启动游戏再重试")
            return None

    set_status("已找到游戏窗口，切到前台...")
    if not activate_window(hwnd):
        set_status("Windows 未允许自动置前，请手动点击游戏窗口(将等待)")
    return hwnd


# ======================= 屏幕检测 =======================
def grab_gray(sct, region, step=8):
    img = np.array(sct.grab(region))[:, :, :3]
    return img.mean(axis=2)[::step, ::step]


def wait_for_enter(sct, monitor, set_status):
    """等待"角色进场"。有模板图就用模板匹配，否则等待按 F8。"""
    trigger_flag.clear()
    tpl = None
    if cv2 is not None and os.path.exists(TEMPLATE_PATH):
        tpl = cv2.imread(TEMPLATE_PATH, cv2.IMREAD_GRAYSCALE)
        set_status("等待进场检测(模板匹配)...  F9停止")
    else:
        set_status("未找到 enter.png，请在角色进场后按 F8 触发  (F9停止)")

    while not stop_flag.is_set():
        if trigger_flag.is_set():
            return True
        if tpl is not None:
            frame = np.array(sct.grab(monitor))[:, :, :3]
            gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            if gray.shape[0] >= tpl.shape[0] and gray.shape[1] >= tpl.shape[1]:
                res = cv2.matchTemplate(gray, tpl, cv2.TM_CCOEFF_NORMED)
                if res.max() >= MATCH_THRESHOLD:
                    return True
        time.sleep(0.2)
    return False


def hold_w_until_stuck(sct, monitor, set_status):
    """按住W，直到画面不再变化(无法前进)，然后松开W。monitor 为游戏画面区域。"""
    w, h = monitor["width"], monitor["height"]
    left = {"left": monitor["left"], "top": monitor["top"] + h // 4,
            "width": w // 5, "height": h // 2}
    right = {"left": monitor["left"] + w * 4 // 5, "top": monitor["top"] + h // 4,
             "width": w // 5, "height": h // 2}

    set_status("按住 W 前进中...")
    pydirectinput.keyDown("w")
    try:
        time.sleep(0.8)
        prev = (grab_gray(sct, left), grab_gray(sct, right))
        still = 0
        start = time.time()
        while not stop_flag.is_set() and time.time() - start < MAX_RUN_TIME:
            time.sleep(CHECK_PERIOD)
            cur = (grab_gray(sct, left), grab_gray(sct, right))
            diff = (np.abs(cur[0] - prev[0]).mean() + np.abs(cur[1] - prev[1]).mean()) / 2
            prev = cur
            still = still + 1 if diff < STUCK_DIFF_THRESHOLD else 0
            if still >= STUCK_COUNT:
                break
    finally:
        pydirectinput.keyUp("w")
    set_status("已无法前进，松开 W")


# ======================= 操作序列 =======================
def build_steps(order, skill, attack):
    steps = [("key", str(order[0]), f"切换到角色{order[0]}")]

    for i, c in enumerate(order[:-1]):
        if skill[c]:
            steps.append(("key", "e", f"角色{c} 释放秘技(E)"))
        steps.append(("key", str(order[i + 1]), f"切换到角色{order[i + 1]}"))

    last = order[-1]
    if skill[last] and attack:
        steps.append(("key", "e", f"角色{last} 释放秘技(E)"))
        steps.append(("click", None, "鼠标左键普攻进战"))
    elif (not skill[last]) and attack:
        steps.append(("click", None, "鼠标左键普攻进战"))
    elif skill[last] and (not attack):
        steps.append(("key", "e", f"角色{last} 释放秘技(E)"))
    return steps


def run_steps(steps, hwnd, set_status):
    for idx, (kind, arg, desc) in enumerate(steps):
        if stop_flag.is_set():
            return False
        if not ensure_focus(hwnd, set_status):   # 每一步之前都确认游戏在前台
            return False
        set_status(desc)
        if kind == "key":
            pydirectinput.press(arg)
        else:
            pydirectinput.click(button="left")
        if idx != len(steps) - 1:
            time.sleep(INTERVAL)
    return True


# ======================= 主流程线程 =======================
def worker(order, skill, attack, game_cfg, set_status, on_done):
    stop_flag.clear()
    try:
        hwnd = prepare_game(game_cfg, set_status)
        if not hwnd:
            return

        with mss.mss() as sct:
            monitor = client_region(hwnd)   # 只截游戏画面区域，不再是整个主显示器
            if not wait_for_enter(sct, monitor, set_status):
                set_status("已停止")
                return
            time.sleep(0.3)

            if not ensure_focus(hwnd, set_status):
                return
            monitor = client_region(hwnd)   # 窗口可能被移动/缩放过，重新取
            hold_w_until_stuck(sct, monitor, set_status)
            if stop_flag.is_set():
                set_status("已停止")
                return

            time.sleep(INTERVAL)
            if run_steps(build_steps(order, skill, attack), hwnd, set_status):
                set_status("全部完成 ✔")
    except Exception as e:
        pydirectinput.keyUp("w")
        set_status(f"出错: {e}")
    finally:
        on_done()


# ======================= 界面 =======================
class App:
    def __init__(self, root):
        self.root = root
        root.title("星铁自动秘技")
        root.attributes("-topmost", True)

        # 0. 游戏启动 / 窗口识别
        f0 = tk.LabelFrame(root, text="游戏")
        f0.pack(fill="x", padx=10, pady=6)
        row1 = tk.Frame(f0)
        row1.pack(fill="x", padx=6, pady=2)
        tk.Label(row1, text="入口(.exe/.lnk):").pack(side="left")
        self.path_var = tk.StringVar()
        tk.Entry(row1, textvariable=self.path_var, width=30).pack(side="left", padx=4)
        tk.Button(row1, text="浏览…", command=self.browse).pack(side="left")
        row2 = tk.Frame(f0)
        row2.pack(fill="x", padx=6, pady=2)
        tk.Label(row2, text="游戏进程名:").pack(side="left")
        self.exe_var = tk.StringVar(value=DEFAULT_EXE_NAME)
        tk.Entry(row2, textvariable=self.exe_var, width=16).pack(side="left", padx=4)
        self.launch_var = tk.BooleanVar(value=False)
        tk.Checkbutton(row2, text="点击开始时先启动游戏(不勾=只识别已运行的游戏)",
                       variable=self.launch_var).pack(side="left", padx=6)

        # 1. 各角色是否释放秘技
        f1 = tk.LabelFrame(root, text="是否自动释放秘技(勾选=释放)")
        f1.pack(fill="x", padx=10, pady=6)
        self.skill_vars = {}
        for i in range(1, 5):
            v = tk.BooleanVar(value=False)
            self.skill_vars[i] = v
            tk.Checkbutton(f1, text=f"角色{i}号", variable=v).pack(side="left", padx=8)

        # 2. 顺序
        f2 = tk.LabelFrame(root, text="操作顺序(输入1234的一个排列，如 2134)")
        f2.pack(fill="x", padx=10, pady=6)
        self.order_var = tk.StringVar(value="1234")
        tk.Entry(f2, textvariable=self.order_var, width=10).pack(padx=8, pady=4)

        # 3. 是否普攻进战
        f3 = tk.LabelFrame(root, text="是否通过普攻进入战斗")
        f3.pack(fill="x", padx=10, pady=6)
        self.attack_var = tk.BooleanVar(value=True)
        tk.Radiobutton(f3, text="是", variable=self.attack_var, value=True).pack(side="left", padx=10)
        tk.Radiobutton(f3, text="否", variable=self.attack_var, value=False).pack(side="left", padx=10)

        f4 = tk.Frame(root)
        f4.pack(pady=6)
        self.btn = tk.Button(f4, text="开始监听", width=12, command=self.start)
        self.btn.pack(side="left", padx=6)
        tk.Button(f4, text="停止(F9)", width=12, command=self.stop).pack(side="left", padx=6)

        self.status = tk.StringVar(value="就绪")
        tk.Label(root, textvariable=self.status, fg="blue", wraplength=380).pack(pady=6)

        keyboard.add_hotkey("f8", trigger_flag.set)
        keyboard.add_hotkey("f9", self.stop)

    def browse(self):
        p = filedialog.askopenfilename(
            title="选择游戏入口",
            filetypes=[("程序或快捷方式", "*.exe *.lnk"), ("所有文件", "*.*")])
        if p:
            self.path_var.set(p)

    def set_status(self, text):
        self.root.after(0, lambda: self.status.set(text))

    def on_done(self):
        self.root.after(0, lambda: self.btn.config(state="normal"))

    def start(self):
        s = self.order_var.get().strip()
        if sorted(s) != ["1", "2", "3", "4"]:
            messagebox.showerror("顺序错误", "请输入1、2、3、4的一个排列(每个数字恰好出现一次)")
            return

        path = self.path_var.get().strip().strip('"')   # 兼容"复制文件地址"带引号
        launch = self.launch_var.get()
        if launch:
            if not os.path.isfile(path):
                messagebox.showerror("入口错误", "勾选了\"先启动游戏\"，但入口路径不是有效文件")
                return
            if not path.lower().endswith((".exe", ".lnk")):
                messagebox.showerror("入口错误", "入口必须是 .exe 或 .lnk")
                return
        elif path and not os.path.isfile(path) and os.path.isfile(path + ".lnk"):
            messagebox.showwarning("提示", "资源管理器隐藏了 .lnk 扩展名，真实文件名是 " + path + ".lnk")

        game_cfg = {"launch": launch, "path": path,
                    "exe_name": self.exe_var.get().strip() or DEFAULT_EXE_NAME}
        order = [int(c) for c in s]
        skill = {i: v.get() for i, v in self.skill_vars.items()}
        self.btn.config(state="disabled")
        threading.Thread(
            target=worker,
            args=(order, skill, self.attack_var.get(), game_cfg, self.set_status, self.on_done),
            daemon=True,
        ).start()

    def stop(self):
        stop_flag.set()
        pydirectinput.keyUp("w")


if __name__ == "__main__":
    root = tk.Tk()
    App(root)
    root.mainloop()
