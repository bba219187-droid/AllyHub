using System.IO;

namespace AllyHub;

/// Sons de interface sintetizados (sem ficheiros externos).
public static class Sfx
{
    public static bool On = true;
    static readonly System.Media.SoundPlayer MoveP = Make(new[] { (1500.0, 0.022) }, 0.12);
    static readonly System.Media.SoundPlayer SelectP = Make(new[] { (880.0, 0.045), (1320.0, 0.07) }, 0.18);
    static readonly System.Media.SoundPlayer BackP = Make(new[] { (620.0, 0.04), (440.0, 0.06) }, 0.15);
    static readonly System.Media.SoundPlayer BoostP = Make(new[] { (330.0, 0.07), (495.0, 0.07), (660.0, 0.07), (990.0, 0.16) }, 0.2);

    public static void Move() => Play(MoveP);
    public static void Select() => Play(SelectP);
    public static void Back() => Play(BackP);
    public static void Boost() => Play(BoostP);

    static void Play(System.Media.SoundPlayer p) { if (On) try { p.Play(); } catch { } }

    static System.Media.SoundPlayer Make((double freq, double secs)[] notes, double volume)
    {
        var p = new System.Media.SoundPlayer(new MemoryStream(WavGen.Wav(notes, volume)));
        try { p.Load(); } catch { }
        return p;
    }
}
