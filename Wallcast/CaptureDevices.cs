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
        var engine = Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe");
        if (!File.Exists(engine)) return names;
        var info = new ProcessStartInfo(engine)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        if (process is null) return names;
        // Listing is the whole job here, so the non-zero exit for the dummy input is expected.
        var report = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000)) { try { process.Kill(true); } catch { } }
        foreach (Match match in Listed.Matches(report))
        {
            var name = match.Groups["name"].Value;
            if (match.Groups["kinds"].Value.Contains("audio") && !names.Contains(name)) names.Add(name);
        }
        return names;
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
