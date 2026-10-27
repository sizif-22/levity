using System;
using System.Windows.Interop;

namespace Levity.Services
{
    public class HotkeyManager : IDisposable
    {
        private const int HOTKEY_ID = 9001;
        private IntPtr _hwnd;
        private HwndSource? _source;
        private bool _isRegistered = false;

        public event Action? OnHotkeyPressed;

        public void Register(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(HwndHook);

            // Register Win + Alt + A (Key 'A' is 0x41)
            _isRegistered = NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID, NativeMethods.MOD_WIN | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x41);

            // If Win+Alt+A is reserved by OS or fails, fallback to Ctrl + Alt + A
            if (!_isRegistered)
            {
                _isRegistered = NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x41);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                OnHotkeyPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_isRegistered && _hwnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID);
                _isRegistered = false;
            }
            _source?.RemoveHook(HwndHook);
            _source?.Dispose();
            _source = null;
        }
    }
}
