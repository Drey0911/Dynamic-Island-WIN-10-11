// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DynamicIsland.Core;

/// <summary>Llamadas Win32 que WPF no expone.</summary>
internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // INPUT con unión: el tamaño debe cubrir MOUSEINPUT (el miembro más grande).
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    // Soltar archivos en una ventana elevada (el arrastre OLE entre niveles de permiso está bloqueado).
    public const int WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049;
    [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, uint size);
    [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);

    /// <summary>Acepta archivos soltados desde apps sin permisos de administrador.</summary>
    public static void EnableElevatedFileDrop(IntPtr hwnd)
    {
        RevokeDragDrop(hwnd);
        const uint MSGFLT_ALLOW = 1;
        ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
        DragAcceptFiles(hwnd, true);
    }

    public static string[] ReadDroppedFiles(IntPtr hDrop)
    {
        try
        {
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var files = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, null, 0) + 1;
                var sb = new StringBuilder((int)len);
                DragQueryFile(hDrop, i, sb, len);
                files[i] = sb.ToString();
            }
            return files;
        }
        finally { DragFinish(hDrop); }
    }
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static void SetNoActivate(IntPtr hwnd, bool noActivate)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        ex = noActivate ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public static void KeepTopmost(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// App a pantalla completa en primer plano. Las ventanas maximizadas no cuentan:
    /// debe cubrir todo el monitor y no tener barra de título.
    /// </summary>
    public static bool IsFullscreenAppRunning(IntPtr self, out int pid)
    {
        pid = 0;
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == self) return false;
            GetWindowThreadProcessId(fg, out uint p);
            pid = (int)p;

            if (SHQueryUserNotificationState(out int s) == 0 && (s == 3 || s == 4)) return true; // D3D exclusivo o presentación

            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            if (IsZoomed(fg) || IsIconic(fg)) return false;
            if ((GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;
            if (!GetWindowRect(fg, out var r)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                   r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
        catch { return false; }
    }

    /// <summary>true si el botón izquierdo o derecho del mouse está presionado.</summary>
    public static bool IsMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

    public static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return (int)pid;
    }

    // "No molestar" / Asistente de concentración: estado WNF del shell (no documentado por Microsoft).
    [DllImport("ntdll.dll")]
    static extern int NtUpdateWnfStateData(ref ulong stateName, byte[] buffer, int length, IntPtr typeId,
        IntPtr explicitScope, uint matchingChangeStamp, uint checkStamp);

    /// <param name="mode">0 = desactivado, 1 = solo prioridad (No molestar), 2 = solo alarmas.</param>
    public static bool SetFocusAssist(int mode)
    {
        try
        {
            ulong state = 0x0D83063EA3BF1C75; // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
            byte[] data = BitConverter.GetBytes(mode);
            return NtUpdateWnfStateData(ref state, data, data.Length, IntPtr.Zero, IntPtr.Zero, 0, 0) == 0;
        }
        catch { return false; }
    }

    public static bool IsCapsLockOn() => (GetKeyState(0x14) & 1) != 0;

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Trae una ventana al frente (con el truco de la tecla Alt para que Windows lo permita).</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SendKeys(0x12); // Alt
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Presiona y suelta una combinación de teclas (códigos virtuales) en la ventana activa.</summary>
    public static void SendKeys(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = keys[i] } };
            inputs[keys.Length * 2 - 1 - i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = keys[i], dwFlags = 2 } };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static bool TryGetCpuTimes(out long idle, out long total)
    {
        idle = total = 0;
        if (!GetSystemTimes(out idle, out long kernel, out long user)) return false;
        total = kernel + user;
        return true;
    }

    public static (double load, double used, double total) GetMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.ullTotalPhys, used = m.ullTotalPhys - m.ullAvailPhys;
        return (total > 0 ? used / total : 0, used, total);
    }

    public static string GetDownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr p) == 0)
            {
                string s = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                if (s.Length > 0) return s;
            }
        }
        catch { }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
