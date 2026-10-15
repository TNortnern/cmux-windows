using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Cmux.Views;

/// <summary>
/// Asks the active keyboard layout whether a key types printable text while AltGr is held.
/// Layouts without an AltGr level (US) type nothing for Ctrl+Alt+digit, so those stay shortcuts.
/// </summary>
internal static class KeyboardLayoutHelper
{
    private const byte KeyDown = 0x80;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RMENU = 0xA5;

    // ToUnicodeEx flag (Windows 10 1607+): do not change the kernel's dead-key state.
    private const uint DoNotChangeKeyboardState = 0x4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    private const uint MapVkToScanCode = 0;

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        [Out] char[] pwszBuff,
        int cchBuff,
        uint wFlags,
        IntPtr dwhkl);

    /// <summary>
    /// True when the key, pressed with Ctrl+Alt and RightAlt held (the AltGr state), produces a printable
    /// character in the current keyboard layout. Dead keys count as text; control characters do not.
    /// </summary>
    public static bool AltGrProducesText(Key key)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
            return false;

        var layout = GetKeyboardLayout(0);
        uint scanCode = MapVirtualKeyEx((uint)virtualKey, MapVkToScanCode, layout);

        var keyState = new byte[256];
        keyState[VK_CONTROL] = KeyDown;
        keyState[VK_LCONTROL] = KeyDown;
        keyState[VK_MENU] = KeyDown;
        keyState[VK_RMENU] = KeyDown;

        var buffer = new char[8];
        int count = ToUnicodeEx(
            (uint)virtualKey,
            scanCode,
            keyState,
            buffer,
            buffer.Length,
            DoNotChangeKeyboardState,
            layout);

        return count != 0 && !char.IsControl(buffer[0]);
    }
}
