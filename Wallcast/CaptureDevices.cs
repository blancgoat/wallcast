using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;

namespace Wallcast;

internal static class CaptureDevices
{
    // Sound is asked of the capture engine rather than of DirectShow directly, because the two do not
    // agree. A capture card carries its audio on a pin of the video device instead of registering an
    // audio device of its own, so the audio category alone would leave the card off the list; the
    // engine's own listing is the one that matches what it will accept.
    private static readonly Regex Listed = new(@"""(?<name>[^""]+)"" \((?<kinds>[^)]*)\)", RegexOptions.Compiled);

    public static List<string> EnumerateAudio()
    {
        var names = new List<string>();
        foreach (Match match in Listed.Matches(Ask("-list_devices", "true", "-f", "dshow", "-i", "dummy")))
        {
            var name = match.Groups["name"].Value;
            if (match.Groups["kinds"].Value.Contains("audio") && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    // What one device's sound pin will actually accept. The engine prints the pin's formats even while
    // refusing to open it, which is the only way to ask: a card carries its sound on a pin of the video
    // device, so there is no audio device to query about it.
    private static readonly Regex Offered = new(@"ch=\s*(?<ch>\d+), bits=\s*(?<bits>\d+), rate=\s*(?<rate>\d+)", RegexOptions.Compiled);

    public static List<string> SoundRates(string device)
    {
        var rates = new List<string>();
        if (string.IsNullOrWhiteSpace(device)) return rates;
        foreach (Match match in Offered.Matches(Ask("-list_options", "true", "-f", "dshow", "-i", "audio=" + device)))
        {
            // Only the stereo layouts, because stereo is what is asked for and what is played.
            if (match.Groups["ch"].Value != "2") continue;
            var rate = match.Groups["rate"].Value;
            if (!rates.Contains(rate)) rates.Add(rate);
        }
        return rates;
    }

    // The engine answers on stderr and exits non-zero for every one of these questions, because it was
    // asked to list rather than to open something. Only the report matters.
    private static string Ask(params string[] arguments)
    {
        var engine = Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe");
        if (!File.Exists(engine)) return "";
        var info = new ProcessStartInfo(engine)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-nostdin");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        if (process is null) return "";
        var report = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000)) { try { process.Kill(true); } catch { } }
        return report;
    }

    public static List<string> Enumerate()
    {
        var names = new List<string>();
        object? instance = null;
        IEnumMoniker? enumerator = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"), true)!);
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            if (((ICreateDevEnum)instance!).CreateClassEnumerator(ref category, out enumerator, 0) != 0 || enumerator is null) return names;
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                object? bag = null;
                try
                {
                    var iid = typeof(IPropertyBag).GUID;
                    monikers[0].BindToStorage(null!, null!, ref iid, out bag);
                    if (((IPropertyBag)bag).Read("FriendlyName", out var value, IntPtr.Zero) == 0 && value is string name) names.Add(name);
                }
                finally
                {
                    if (bag is not null) Marshal.ReleaseComObject(bag);
                    Marshal.ReleaseComObject(monikers[0]);
                }
            }
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (instance is not null) Marshal.ReleaseComObject(instance);
        }
        return names;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker? enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
