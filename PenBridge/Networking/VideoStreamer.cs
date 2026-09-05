using System.Diagnostics;
using PenBridge.Input;

namespace PenBridge.Networking;

internal static class VideoStreamer
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    internal static ProcessStartInfo BuildStartInfo(MonitorRect monitor, int fps, int width, int crf)
    {
        fps = Math.Clamp(fps, 15, 60);
        width = Math.Clamp(width, 640, 1920);
        crf = Math.Clamp(crf, 18, 32);
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        string[] args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "gdigrab",
            "-framerate", fps.ToString(), "-draw_mouse", "1", "-offset_x", monitor.Left.ToString(),
            "-offset_y", monitor.Top.ToString(), "-video_size", $"{monitor.Width}x{monitor.Height}",
            "-probesize", "32", "-analyzeduration", "0",
            "-i", "desktop", "-an", "-vf", $"scale=w='trunc(min({width},iw)/2)*2':h=-2:flags=fast_bilinear",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline",
            "-level:v", "4.2", "-pix_fmt", "yuv420p", "-crf", crf.ToString(), "-g", Math.Max(1, fps / 4).ToString(),
            "-keyint_min", "1", "-sc_threshold", "0", "-bf", "0", "-threads", "4",
            // Flush every frame instead of holding frames until the next keyframe (~250ms).
            "-movflags", "frag_every_frame+empty_moov+default_base_moof", "-flush_packets", "1", "-f", "mp4", "pipe:1"];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task StreamAsync(Stream output, MonitorRect monitor, int fps, int width,
        int crf, CancellationToken token)
    {
        using var process = new Process { StartInfo = BuildStartInfo(monitor, fps, width, crf) };
        process.Start();
        var errors = process.StandardError.ReadToEndAsync();
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) {} });
        try
        {
            var buffer = new byte[32768];
            while (true)
            {
                int count = await process.StandardOutput.BaseStream.ReadAsync(buffer, token);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                await output.FlushAsync(token);
            }
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0 && !token.IsCancellationRequested)
                throw new IOException($"Video encoder failed: {await errors}");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) {}
            await process.WaitForExitAsync();
            await errors;
        }
    }
}
