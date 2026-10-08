using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NewQ.App.Video;

/// <summary>
/// Capture devices as DirectShow lists them (webcams, HDMI/SDI capture cards with a Windows driver, virtual
/// cameras). libVLC's dshow input opens them by these same friendly names. Reading the list never opens a device.
/// </summary>
public static class CaptureDevices
{
    private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid AudioInputCategory = new("33D9A762-90C8-11D0-BD43-00A0C911CE86");

    public static IReadOnlyList<string> Video() => List(VideoInputCategory);

    public static IReadOnlyList<string> Audio() => List(AudioInputCategory);

    private static IReadOnlyList<string> List(Guid category)
    {
        var names = new List<string>();
        object? enumObject = null;
        IEnumMoniker? monikers = null;
        try
        {
            var type = Type.GetTypeFromCLSID(SystemDeviceEnum);
            if (type is null) return names;
            enumObject = Activator.CreateInstance(type);
            if (enumObject is not ICreateDevEnum devEnum) return names;
            // S_FALSE (1) and a null enumerator = no device of this kind.
            if (devEnum.CreateClassEnumerator(ref category, out monikers, 0) != 0 || monikers is null) return names;

            var one = new IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                var moniker = one[0];
                try
                {
                    var bagId = typeof(IPropertyBag).GUID;
                    moniker.BindToStorage(null!, null!, ref bagId, out var bagObject);
                    if (bagObject is IPropertyBag bag)
                    {
                        object? value = null;
                        if (bag.Read("FriendlyName", ref value, IntPtr.Zero) == 0 && value is string name && name.Length > 0 && !names.Contains(name))
                            names.Add(name);
                        Marshal.ReleaseComObject(bag);
                    }
                }
                catch (COMException) { /* a device that can't describe itself is skipped */ }
                finally { Marshal.ReleaseComObject(moniker); }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No DirectShow (stripped-down Windows): no capture devices.
        }
        finally
        {
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            if (enumObject is not null && Marshal.IsComObject(enumObject)) Marshal.ReleaseComObject(enumObject);
        }
        return names;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] ref Guid clsidDeviceClass, out IEnumMoniker? enumMoniker, [In] int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([In, MarshalAs(UnmanagedType.LPWStr)] string propertyName, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object? value, [In] IntPtr errorLog);

        [PreserveSig]
        int Write([In, MarshalAs(UnmanagedType.LPWStr)] string propertyName, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
