using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Still;

internal static class CaptureDevices
{
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
