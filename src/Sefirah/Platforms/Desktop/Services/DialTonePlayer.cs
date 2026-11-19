using System.Diagnostics;
using System.Text;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Plays the DTMF tone for a dial pad key. BlueZ telephony exposes no DTMF method, so this is
/// feedback for the person dialing; the tone does not reach the call.
/// </summary>
public static class DialTonePlayer
{
    private static readonly Dictionary<char, (double Low, double High)> Frequencies = new()
    {
        ['1'] = (697, 1209),
        ['2'] = (697, 1336),
        ['3'] = (697, 1477),
        ['4'] = (770, 1209),
        ['5'] = (770, 1336),
        ['6'] = (770, 1477),
        ['7'] = (852, 1209),
        ['8'] = (852, 1336),
        ['9'] = (852, 1477),
        ['*'] = (941, 1209),
        ['0'] = (941, 1336),
        ['#'] = (941, 1477),
    };

    private const int SampleRate = 8000;
    private const int Milliseconds = 90;
    private static string? playerPath;

    public static void Play(char key)
    {
        if (!OperatingSystem.IsLinux() || !Frequencies.TryGetValue(key, out var tone))
            return;

        playerPath ??= FindPlayer();
        if (playerPath is null)
            return;

        try
        {
            var start = new ProcessStartInfo(playerPath, ToneFile(key, tone)) { UseShellExecute = false };
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            // A missing tone must never break dialing.
            Debug.WriteLine($"Dial tone failed: {ex.Message}");
        }
    }

    private static string? FindPlayer()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var player in new[] { "pw-play", "paplay" })
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, player);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string ToneFile(char key, (double Low, double High) tone)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sefirah-dtmf-{(key == '*' ? "star" : key == '#' ? "hash" : key.ToString())}.wav");
        if (File.Exists(file))
            return file;

        var count = SampleRate * Milliseconds / 1000;
        var ramp = SampleRate / 200; // 5ms, keeps the edges click-free

        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + count * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);                                  // chunk size
        writer.Write((short)1);                            // PCM
        writer.Write((short)1);                            // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);                      // byte rate
        writer.Write((short)2);                            // block align
        writer.Write((short)16);                           // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(count * 2);

        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var sample = 0.05 * Math.Sin(2 * Math.PI * tone.Low * t) + 0.05 * Math.Sin(2 * Math.PI * tone.High * t);
            if (i < ramp) sample *= (double)i / ramp;
            if (i > count - ramp) sample *= (double)(count - i) / ramp;

            writer.Write((short)(sample * short.MaxValue));
        }

        return file;
    }
}
