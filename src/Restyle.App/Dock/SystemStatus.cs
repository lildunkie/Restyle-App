using System.Management;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace Restyle.App.Docking;

/// <summary>Speaker volume and mute for the default output device (Core Audio API).</summary>
internal sealed class Audio
{
    private IAudioEndpointVolume? _volume;
    private string? _deviceId;

    /// <summary>0..1, or null if there's no output device.</summary>
    public float? Volume
    {
        get => Try(v => { v.GetMasterVolumeLevelScalar(out var level); return (float?)level; });
        set
        {
            if (value is float level)
                Try(v => { v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), Guid.Empty); return (float?)null; });
        }
    }

    public bool Muted
    {
        get => Try(v => { v.GetMute(out var m); return (bool?)m; }) ?? false;
        set => Try(v => { v.SetMute(value, Guid.Empty); return (bool?)null; });
    }

    public string DeviceName { get; private set; } = "";

    /// <summary>How loud the sound playing right now is, 0..1 (for the music bars). 0 when silent.</summary>
    public float Peak => Try(_ =>
    {
        float level = 0;
        _meter?.GetPeakValue(out level);
        return (float?)level;
    }) ?? 0;

    private IAudioMeterInformation? _meter;

    private T? Try<T>(Func<IAudioEndpointVolume, T?> action)
    {
        try
        {
            Connect();
            return _volume is null ? default : action(_volume);
        }
        catch (Exception)
        {
            _volume = null; // device unplugged or changed: reconnect next time
            return default;
        }
    }

    /// <summary>Re-binds to the default output device if it changed (e.g. headphones plugged in).</summary>
    private void Connect()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0)
            {
                _volume = null;
                DeviceName = "";
                return;
            }

            device.GetId(out var idPtr);
            var id = Marshal.PtrToStringUni(idPtr);
            Marshal.FreeCoTaskMem(idPtr);
            if (_volume is not null && id == _deviceId)
                return;

            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
            _volume = (IAudioEndpointVolume)obj;
            var meterIid = typeof(IAudioMeterInformation).GUID;
            device.Activate(ref meterIid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var meter);
            _meter = (IAudioMeterInformation)meter;
            _deviceId = id;
            DeviceName = FriendlyName(device);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string FriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0, out var store);
            var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            store.GetValue(ref key, out var value);
            var name = value.vt == 31 ? Marshal.PtrToStringUni(value.pointer) : null;
            PropVariantClear(ref value);
            return name ?? "Speakers";
        }
        catch (Exception)
        {
            return "Speakers";
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId(out IntPtr id);
        void GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, Guid context);
        void SetMasterVolumeLevelScalar(float level, Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr pointer; public IntPtr extra; }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}

/// <summary>Battery level and charging state (GetSystemPowerStatus).</summary>
internal static class Battery
{
    public sealed record Status(bool HasBattery, int Percent, bool Charging, TimeSpan? Remaining);

    public static Status Read()
    {
        if (!GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent > 100)
            return new Status(false, 100, true, null);
        TimeSpan? remaining = s.BatteryLifeTime is > 0 and < int.MaxValue ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new Status(true, s.BatteryLifePercent, s.ACLineStatus == 1, remaining);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>Built-in screen brightness (laptops/tablets) through WMI. External monitors aren't supported.</summary>
internal static class Brightness
{
    /// <summary>0..100, or null if this screen's brightness can't be controlled.</summary>
    public static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var o in searcher.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception)
        {
        }

        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in searcher.Get())
                o.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)Math.Clamp(percent, 0, 100)]);
        }
        catch (Exception)
        {
            // Not supported on this screen.
        }
    }
}

/// <summary>Wi-Fi/Bluetooth radios and connection info via the Windows device APIs.</summary>
internal static class Connectivity
{
    public sealed record Network(string Name, bool IsWifi, int Bars);

    public static Network? CurrentNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
                return null;
            if (profile.IsWlanConnectionProfile)
                return new Network(profile.WlanConnectionProfileDetails.GetConnectedSsid(), true, profile.GetSignalBars() ?? 3);
            return new Network(profile.ProfileName is { Length: > 0 } n ? n : "Ethernet", false, 5);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<Radio?> FindRadio(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
                return null;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public sealed record BluetoothDeviceInfo(string Name, bool Connected);

    /// <summary>Paired Bluetooth devices, connected ones first.</summary>
    public static async Task<List<BluetoothDeviceInfo>> PairedBluetoothDevices()
    {
        var result = new Dictionary<string, BluetoothDeviceInfo>();
        string[] props = ["System.Devices.Aep.IsConnected"];
        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            try
            {
                foreach (var d in await DeviceInformation.FindAllAsync(selector, props, DeviceInformationKind.AssociationEndpoint))
                {
                    if (string.IsNullOrWhiteSpace(d.Name))
                        continue;
                    var connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true;
                    if (!result.TryGetValue(d.Name, out var existing) || connected && !existing.Connected)
                        result[d.Name] = new BluetoothDeviceInfo(d.Name, connected);
                }
            }
            catch (Exception)
            {
                // Bluetooth missing or turned off.
            }
        }

        return result.Values.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }
}
