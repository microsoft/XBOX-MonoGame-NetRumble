using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NetRumble.Game.UI.Screens;

sealed class NativeHostInputCheck : Microsoft.Xna.Framework.Game
{
    private readonly TestUi _ui = new();
    private readonly GraphicsDeviceManager _graphics;
    private int _frame;
    private nint _window;
    internal bool Passed { get; private set; }

    internal NativeHostInputCheck()
    {
        _graphics = new GraphicsDeviceManager(this);
        _graphics.PreferredBackBufferWidth = 400;
        _graphics.PreferredBackBufferHeight = 240;
        Window.Title = "NetRumble targeted host input check";
        IsFixedTimeStep = false;
        _ui.Party.Hosting = true;
        _ui.Screens.Push(new LobbyScreen(LobbyIntent.Host));
    }

    protected override void Update(GameTime gameTime)
    {
        var keys = Keyboard.GetState();
        _ui.Frame(keys: keys);
        _frame++;
        if (_frame == 10)
        {
            _window = FindWindow(null, Window.Title);
            GetWindowThreadProcessId(_window, out var process);
            if (_window == 0 || process != Environment.ProcessId)
            {
                throw new InvalidOperationException("Could not identify this process's test window.");
            }
            Console.WriteLine($"Posting Space only to test HWND {_window}.");
            if (!PostMessage(_window, 0x0100, 0x20, 0x00390001))
            {
                throw new InvalidOperationException($"PostMessage failed: {Marshal.GetLastWin32Error()}");
            }
        }
        else if (_frame == 20)
        {
            var network = _ui.LobbyNetwork;
            Passed = keys.IsKeyDown(Keys.Space) && network.LocalPlayer!.IsReady;
            Console.WriteLine($"Native keys={string.Join(",", keys.GetPressedKeys())}; host ready={network.LocalPlayer!.IsReady}");
            PostMessage(_window, 0x0101, 0x20, unchecked((nint)0xC0390001));
            Exit();
        }
        base.Update(gameTime);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _ui.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
