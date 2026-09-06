using System.Diagnostics;

namespace PenBridge.Networking;

/// <summary>
/// Picks the fastest available H.264 encoder once per process lifetime. PenBridge shows a live
/// screen preview so the user can see where the pen is about to draw — it's not a recording or
/// broadcast — so every choice here favors lower encode latency over compression efficiency,
/// bitrate, or image quality.
/// </summary>
internal static class VideoEncoders
{
    // ponytail: probed once and cached for the process lifetime — hardware encoder availability
    // doesn't change while PenBridge is running, so there's no reason to re-spawn ffmpeg per stream.
    private static readonly Lazy<string> _selected = new(DetectBestEncoder);

    /// <summary>Test-only override so encoder-selection tests don't depend on what GPU happens to
    /// be in the machine running them.</summary>
    internal static string? TestOverride { private get; set; }

    public static string Selected => TestOverride ?? _selected.Value;

    /// <summary>ffmpeg args for the given encoder, tuned for the lowest possible encode latency
    /// (no B-frames, fastest preset, small GOP) rather than quality or file size.</summary>
    public static string[] LowLatencyArgs(string encoder, int fps, int crf)
    {
        string gop = Math.Max(1, fps / 4).ToString();
        return encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ll", "-rc", "vbr",
                "-cq", crf.ToString(), "-zerolatency", "1", "-profile:v", "baseline",
                "-g", gop, "-bf", "0"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "veryfast", "-async_depth", "1",
                "-profile:v", "baseline", "-g", gop, "-bf", "0"],
            "h264_amf" => ["-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed",
                "-profile:v", "baseline", "-g", gop, "-bf", "0"],
            _ => ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline",
                "-crf", crf.ToString(), "-g", gop, "-keyint_min", "1", "-sc_threshold", "0", "-bf", "0"],
        };
    }

    private static string DetectBestEncoder()
    {
        // Priority: GPU vendor encoders first, plain libx264 (CPU) as the universal fallback.
        foreach (var candidate in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            if (CanEncode(candidate)) return candidate;
        return "libx264";
    }

    /// <summary>Actually tries to open the encoder on a tiny throwaway frame — the presence of the
    /// codec in `ffmpeg -encoders` only means ffmpeg was built with it, not that this PC's GPU/driver
    /// supports it (e.g. an Intel-codec build running on an NVIDIA-only PC).</summary>
    private static bool CanEncode(string encoder)
    {
        if (!File.Exists(VideoStreamer.Executable)) return false;
        try
        {
            var start = new ProcessStartInfo(VideoStreamer.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            string[] args = ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "nullsrc=s=256x256:d=0.1",
                "-pix_fmt", "yuv420p"];
            foreach (var arg in args) start.ArgumentList.Add(arg);
            // Probe the exact low-latency option set used by a real stream. Some drivers expose an
            // encoder but reject a requested profile, tune, or rate-control mode at startup.
            foreach (var arg in LowLatencyArgs(encoder, 30, 23)) start.ArgumentList.Add(arg);
            foreach (var arg in new[] { "-frames:v", "1", "-f", "null", "-" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(3000)) { process.Kill(true); return false; }
            return process.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }
}
