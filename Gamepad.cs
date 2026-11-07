using System.Runtime.InteropServices;

namespace AllyHub;

[Flags]
public enum Btn : ushort
{
    Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// Le o comando do Ally (XInput) e devolve os botoes carregados agora (flanco).
public class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int user, out State state);

    const short Dead = 16000;
    Btn _prev;
    int _repeat;

    public Btn Poll()
    {
        Btn now = 0;
        for (int i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var s) != 0) continue;
            now = (Btn)s.Buttons;
            // stick esquerdo funciona como setas
            if (s.LX > Dead) now |= Btn.Right; else if (s.LX < -Dead) now |= Btn.Left;
            if (s.LY > Dead) now |= Btn.Up; else if (s.LY < -Dead) now |= Btn.Down;
            break;
        }

        var pressed = now & ~_prev;
        // repeticao ao manter as setas (poll a ~60 Hz: espera ~0.4 s, depois a cada ~0.1 s)
        const Btn dirs = Btn.Up | Btn.Down | Btn.Left | Btn.Right;
        if ((now & dirs) != 0 && (now & dirs) == (_prev & dirs))
        {
            if (++_repeat > 24 && _repeat % 6 == 0) pressed |= now & dirs;
        }
        else _repeat = 0;

        _prev = now;
        return pressed;
    }
}
