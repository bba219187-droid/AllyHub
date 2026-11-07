using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AllyHub;

/// Ventoinhas do ROG Ally atraves do driver ASUS ATK (\\.\ATKACPI), o mesmo que o Armoury Crate usa.
/// Curva = 8 pontos (temperatura C -> velocidade %). Em todos os perfis a ventoinha chega a 100% em calor alto.
public static class Fans
{
    public record Preset(string Name, byte[]? Temps, byte[]? Percent);

    static readonly byte[] T = { 40, 50, 60, 70, 75, 80, 85, 90 };

    public static readonly Preset[] Presets =
    {
        new("Automático (ASUS)", null, null),
        new("Silencioso", T, new byte[] { 0, 10, 20, 35, 50, 65, 85, 100 }),
        new("Equilibrado", T, new byte[] { 15, 25, 35, 50, 60, 75, 90, 100 }),
        new("Desempenho", T, new byte[] { 25, 35, 50, 65, 75, 85, 100, 100 }),
        new("Máximo", T, new byte[] { 100, 100, 100, 100, 100, 100, 100, 100 }),
    };

    const uint Ioctl = 0x0022240C;
    const uint DSTS = 0x53545344, DEVS = 0x53564544, INIT = 0x54494E49;
    const uint PerformanceMode = 0x00120075;   // 0 equilibrado, 1 turbo, 2 silencioso
    const uint CpuFanCurve = 0x00110024, GpuFanCurve = 0x00110025;
    const uint CpuFan = 0x00110013, GpuFan = 0x00110014;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

    static SafeFileHandle? _h;
    static readonly object Lock = new();

    public static bool Available
    {
        get { lock (Lock) return Open() != null; }
    }

    static SafeFileHandle? Open()
    {
        if (_h is { IsInvalid: false, IsClosed: false }) return _h;
        try
        {
            var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h.IsInvalid) return null;
            _h = h;
            Call(INIT, new byte[4]);
            return _h;
        }
        catch { return null; }
    }

    /// Buffer do metodo ACPI: [id do metodo][tamanho dos argumentos][argumentos]
    public static byte[] Buffer(uint method, byte[] args)
    {
        var buf = new byte[8 + args.Length];
        BitConverter.GetBytes(method).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buf, 4);
        args.CopyTo(buf, 8);
        return buf;
    }

    static int Call(uint method, byte[] args)
    {
        if (_h == null) return -1;
        var input = Buffer(method, args);
        var output = new byte[16];
        return DeviceIoControl(_h, Ioctl, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero)
            ? BitConverter.ToInt32(output, 0) : -1;
    }

    static int DeviceSet(uint device, byte[] value) => Call(DEVS, BitConverter.GetBytes(device).Concat(value).ToArray());
    static int DeviceGet(uint device) => Call(DSTS, BitConverter.GetBytes(device)) - 65536;

    /// Curva em 16 bytes: 8 temperaturas e depois 8 percentagens.
    public static byte[] Curve(Preset p)
    {
        if (p.Temps == null || p.Percent == null || p.Temps.Length != 8 || p.Percent.Length != 8)
            throw new ArgumentException("curva invalida");
        for (int i = 1; i < 8; i++)
            if (p.Temps[i] < p.Temps[i - 1] || p.Percent[i] < p.Percent[i - 1]) throw new ArgumentException("curva tem de subir");
        if (p.Percent[7] < 100) throw new ArgumentException("o ultimo ponto tem de ser 100%");
        return p.Temps.Concat(p.Percent.Select(x => Math.Min((byte)100, x))).ToArray();
    }

    /// Aplica o perfil. Devolve null se correu bem, senao o erro.
    public static string? Apply(int index)
    {
        var p = Presets[Math.Clamp(index, 0, Presets.Length - 1)];
        lock (Lock)
        {
            if (Open() == null) return "driver ASUS (ATKACPI) não encontrado";
            if (p.Temps == null)
            {
                // voltar a curva de fabrica: repor o modo de desempenho da ASUS
                return DeviceSet(PerformanceMode, BitConverter.GetBytes(0)) >= 0 ? null : "não foi possível repor a curva da ASUS";
            }
            var curve = Curve(p);
            var a = DeviceSet(CpuFanCurve, curve);
            var b = DeviceSet(GpuFanCurve, curve);
            return a >= 0 || b >= 0 ? null : "o firmware recusou a curva";
        }
    }

    /// Rotacoes atuais das ventoinhas (RPM), ou null se nao der para ler.
    public static (int? cpu, int? gpu) Rpm()
    {
        lock (Lock)
        {
            if (Open() == null) return (null, null);
            int? R(uint dev) { var v = DeviceGet(dev); return v is > 0 and < 200 ? v * 100 : (v == 0 ? 0 : null); }
            return (R(CpuFan), R(GpuFan));
        }
    }
}
