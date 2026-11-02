using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SkyBar.SystemIntegration;

public record AudioDeviceOption(string Id, string Name, bool IsDefault);

/// <summary>
/// Mendeteksi seluruh perangkat output audio (Speaker, Headphone, HDMI, Bluetooth, FxSound, dll)
/// di Windows menggunakan CoreAudio (IMMDeviceEnumerator) dan memungkinkan pergantian
/// perangkat audio default secara langsung dari bar Notch (via IPolicyConfig).
/// </summary>
public static class AudioEndpointService
{
    private const int DEVICE_STATE_ACTIVE = 0x00000001;

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClientComObject { }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    public static IReadOnlyList<AudioDeviceOption> GetPlaybackDevices()
    {
        var result = new List<AudioDeviceOption>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            string defaultId = string.Empty;

            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice defDev) == 0 && defDev != null)
                {
                    defDev.GetId(out defaultId);
                    Marshal.ReleaseComObject(defDev);
                }
            }
            catch { }

            if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out IMMDeviceCollection collection) == 0 && collection != null)
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out IMMDevice dev) == 0 && dev != null)
                    {
                        dev.GetId(out string id);
                        string name = ReadFriendlyName(dev) ?? $"Audio Output {i + 1}";
                        bool isDef = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                        result.Add(new AudioDeviceOption(id, name, isDef));
                        Marshal.ReleaseComObject(dev);
                    }
                }
                Marshal.ReleaseComObject(collection);
            }
            Marshal.ReleaseComObject(enumerator);
        }
        catch { }

        if (result.Count == 0)
        {
            result.Add(new AudioDeviceOption("default", "System Default Audio", true));
        }

        return result;
    }

    public static bool SetDefaultPlaybackDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == "default") return false;
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClientComObject();
            policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
            policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            Marshal.ReleaseComObject(policy);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0, out IPropertyStore store) == 0 && store != null)
            {
                PROPERTYKEY key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out PROPVARIANT pv) == 0)
                {
                    try
                    {
                        if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                            return Marshal.PtrToStringUni(pv.pwszVal);
                    }
                    finally
                    {
                        PropVariantClear(ref pv);
                    }
                }
                Marshal.ReleaseComObject(store);
            }
        }
        catch { }
        return null;
    }
}

