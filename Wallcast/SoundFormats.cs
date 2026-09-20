using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Wallcast;

// Asks a capture device what its sound pin is offering, in this process and in microseconds.
//
// It is worth the interop. A pin keeps whatever rate it negotiated, so a source that switches from a
// 44.1kHz track to a 48kHz one says nothing - and a card that quietly resamples to the pin's rate does
// not even change how much arrives per second, it just makes a mess of the sound. What does change is
// this list: the device puts what it is receiving at the head of it, and it answers while the device
// is being captured. That makes it the only reliable way to notice, and doing it with the capture
// engine would mean launching a process every few seconds for the life of the wallpaper.
//
// Everything here is best effort. A device that will not answer leaves the caller with an empty list,
// and the caller then settles for whatever was negotiated when playback started.
internal static class SoundFormats
{
    private static readonly Guid AudioMedia = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid WaveFormat = new("05589f81-c356-11ce-bf01-00aa0055595a");
    private static readonly Guid VideoCategory = new("860bb310-5d01-11d0-bd3b-00a0c911ce86");
    private static readonly Guid AudioCategory = new("33d9a762-90c8-11d0-bd43-00a0c9118956");

    // The rates the pin offers for stereo, in the device's own order. The head of that list is what it
    // is receiving now, which is the whole point of asking.
    public static List<int> Rates(string device)
    {
        var rates = new List<int>();
        // A card carries its sound on a pin of the video device, so that category comes first; a plain
        // audio device is only reached through the other one.
        foreach (var category in new[] { VideoCategory, AudioCategory })
        {
            var filter = Bind(device, category);
            if (filter is null) continue;
            try { Collect(filter, rates); }
            catch (COMException) { }
            catch (InvalidCastException) { }
            finally { Marshal.ReleaseComObject(filter); }
            if (rates.Count > 0) return rates;
        }
        return rates;
    }

    private static void Collect(object filter, List<int> rates)
    {
        if (((IBaseFilter)filter).EnumPins(out var pins) != 0 || pins is null) return;
        try
        {
            var found = new IPin[1];
            while (pins.Next(1, found, IntPtr.Zero) == 0)
            {
                var pin = found[0];
                try
                {
                    if (pin.QueryDirection(out var direction) != 0 || direction != 1) continue;
                    if (pin is not IAMStreamConfig config) continue;
                    if (config.GetNumberOfCapabilities(out var count, out var size) != 0) continue;
                    var scratch = Marshal.AllocCoTaskMem(size);
                    try
                    {
                        for (var i = 0; i < count; i++) Read(config, i, scratch, rates);
                    }
                    finally { Marshal.FreeCoTaskMem(scratch); }
                    if (rates.Count > 0) return;
                }
                finally { Marshal.ReleaseComObject(pin); }
            }
        }
        finally { Marshal.ReleaseComObject(pins); }
    }

    private static void Read(IAMStreamConfig config, int index, IntPtr scratch, List<int> rates)
    {
        if (config.GetStreamCaps(index, out var held, scratch) != 0 || held == IntPtr.Zero) return;
        try
        {
            var media = Marshal.PtrToStructure<MediaType>(held);
            if (media.Major != AudioMedia || media.FormatType != WaveFormat || media.Format == IntPtr.Zero) return;
            var wave = Marshal.PtrToStructure<WaveFormatEx>(media.Format);
            // Stereo only, because stereo is what is asked for and what is played.
            if (wave.Channels != 2 || wave.SamplesPerSecond == 0) return;
            var rate = (int)wave.SamplesPerSecond;
            if (!rates.Contains(rate)) rates.Add(rate);
        }
        finally { Release(held); }
    }

    // What DeleteMediaType does: the format block and the held reference are the caller's to free.
    private static void Release(IntPtr held)
    {
        var media = Marshal.PtrToStructure<MediaType>(held);
        if (media.Format != IntPtr.Zero) Marshal.FreeCoTaskMem(media.Format);
        if (media.Unknown != IntPtr.Zero) Marshal.Release(media.Unknown);
        Marshal.FreeCoTaskMem(held);
    }

    private static object? Bind(string device, Guid category)
    {
        object? maker = null;
        IEnumMoniker? monikers = null;
        try
        {
            maker = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"), true)!);
            if (((ICreateDevEnum)maker!).CreateClassEnumerator(ref category, out monikers, 0) != 0 || monikers is null) return null;
            var found = new IMoniker[1];
            while (monikers.Next(1, found, IntPtr.Zero) == 0)
            {
                var moniker = found[0];
                try
                {
                    if (Named(moniker) != device) continue;
                    var iid = typeof(IBaseFilter).GUID;
                    moniker.BindToObject(null!, null!, ref iid, out var filter);
                    return filter;
                }
                catch (COMException) { return null; }
                finally { Marshal.ReleaseComObject(moniker); }
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        finally
        {
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            if (maker is not null) Marshal.ReleaseComObject(maker);
        }
        return null;
    }

    private static string? Named(IMoniker moniker)
    {
        object? bag = null;
        try
        {
            var iid = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(null!, null!, ref iid, out bag);
            return ((IPropertyBag)bag).Read("FriendlyName", out var value, IntPtr.Zero) == 0 ? value as string : null;
        }
        catch (COMException) { return null; }
        finally { if (bag is not null) Marshal.ReleaseComObject(bag); }
    }

    // Only the slots that are actually called carry a signature; the rest are placeholders holding
    // their place in the vtable, because a slot in the wrong position is a crash rather than an error.
    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
        void SlotGetClassID();
        void SlotStop(); void SlotPause(); void SlotRun(); void SlotGetState();
        void SlotSetSyncSource(); void SlotGetSyncSource();
        [PreserveSig] int EnumPins(out IEnumPins? pins);
    }

    [ComImport, Guid("56a86892-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumPins
    {
        [PreserveSig] int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IPin[] pins, IntPtr fetched);
    }

    [ComImport, Guid("56a86891-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPin
    {
        void SlotConnect(); void SlotReceiveConnection(); void SlotDisconnect(); void SlotConnectedTo();
        void SlotConnectionMediaType(); void SlotQueryPinInfo();
        [PreserveSig] int QueryDirection(out int direction);
    }

    [ComImport, Guid("c6e13340-30ac-11d0-a18c-00a0c9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMStreamConfig
    {
        void SlotSetFormat(); void SlotGetFormat();
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out IntPtr mediaType, IntPtr caps);
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MediaType
    {
        public Guid Major, Subtype;
        [MarshalAs(UnmanagedType.Bool)] public bool FixedSizeSamples;
        [MarshalAs(UnmanagedType.Bool)] public bool TemporalCompression;
        public uint SampleSize;
        public Guid FormatType;
        public IntPtr Unknown;
        public uint FormatSize;
        public IntPtr Format;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort Tag, Channels;
        public uint SamplesPerSecond, BytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
}
