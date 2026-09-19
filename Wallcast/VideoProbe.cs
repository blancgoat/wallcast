using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wallcast;

// What shape a video file is, which the placement arithmetic needs before anything is played.
//
// LibVLC can answer this too, but asking it costs a second preparser pass over the file on its own
// threads, and releasing the parsed media brought the process down here often enough to abandon the
// approach. The capture engine is already shipped, already trusted with the formats this app cares
// about, and answers from one short run that cannot disturb whatever is currently playing.
internal static class VideoProbe
{
    // The first WxH after the stream's "Video:" is its coded size; a sample aspect other than 1:1 is
    // how anamorphic video says it is wider than its pixel count, and it is the displayed shape that
    // has to be placed on the monitor.
    private static readonly Regex Stream = new(
        @"Stream #\d+:\d+.*?: Video: .*?[,\s](?<w>\d{2,5})x(?<h>\d{2,5})\b(?:\s*\[SAR (?<sn>\d+):(?<sd>\d+))?",
        RegexOptions.Compiled);

    public static async Task<Size?> Measure(string file, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        var report = await Describe(file, token);
        var match = Stream.Match(report);
        if (!match.Success) return null;
        var width = int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["sn"].Success
            && int.TryParse(match.Groups["sn"].Value, out var num) && num > 0
            && int.TryParse(match.Groups["sd"].Value, out var den) && den > 0)
            width = (int)Math.Round(width * (double)num / den);
        return width >= 16 && height >= 16 ? new Size(width, height) : null;
    }

    // ffmpeg with an input and no output prints what it found and then complains, which is exactly
    // the cheapest way to ask it a question. The complaint is on stderr along with the answer.
    private static async Task<string> Describe(string file, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-i", file }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The capture engine is missing.");
        var report = process.StandardError.ReadToEndAsync(token);
        // A file on a sleeping drive can take a moment; a file that hangs ffmpeg must not hang the form.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        return await report;
    }
}
