using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using G300sController.Models;
using Microsoft.Win32.SafeHandles;

namespace G300sController.Services;

public sealed class G300sHidDevice : IDisposable
{
    private SafeFileHandle? _deviceHandle;
    private FileStream? _inputStream;
    private CancellationTokenSource? _listenerCts;
    private readonly object _ioLock = new();

    public string? DevicePath { get; private set; }
    public bool IsConnected => _deviceHandle is { IsInvalid: false, IsClosed: false };

    public event Action<HardwareButtonEvent>? HardwareEventReceived;
    public event Action<bool>? ConnectionStateChanged;

    public static bool IsLgsRunning()
    {
        try
        {
            return Process.GetProcessesByName("LCore").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool Connect()
    {
        lock (_ioLock)
        {
            if (IsConnected) return true;

            string? path = FindVendorHidPath(
                G300sProtocol.VendorId,
                G300sProtocol.ProductId,
                G300sProtocol.VendorUsagePage);

            if (path is null)
            {
                ConnectionStateChanged?.Invoke(false);
                return false;
            }

            SafeFileHandle handle = NativeHid.CreateFileW(
                path,
                NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
                NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeHid.OPEN_EXISTING,
                NativeHid.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle = NativeHid.CreateFileW(
                    path,
                    0,
                    NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeHid.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
            }

            if (handle.IsInvalid)
            {
                handle.Dispose();
                ConnectionStateChanged?.Invoke(false);
                return false;
            }

            _deviceHandle = handle;
            DevicePath = path;
            StartInputListener();
            ConnectionStateChanged?.Invoke(true);
            return true;
        }
    }

    public void Disconnect()
    {
        lock (_ioLock)
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;

            try { _inputStream?.Dispose(); } catch { /* ignore */ }
            _inputStream = null;

            try { _deviceHandle?.Dispose(); } catch { /* ignore */ }
            _deviceHandle = null;
            DevicePath = null;
        }
        ConnectionStateChanged?.Invoke(false);
    }

    public byte[] GetFeature(byte reportId, int length)
    {
        lock (_ioLock)
        {
            if (!IsConnected || _deviceHandle is null)
                throw new InvalidOperationException("Logitech G300s is not connected.");

            byte[] buffer = new byte[length];
            buffer[0] = reportId;

            if (!NativeHid.HidD_GetFeature(_deviceHandle, buffer, length))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"HidD_GetFeature(0x{reportId:X2}) failed with Win32 error {err}.");
            }

            return buffer;
        }
    }

    public void SendFeature(byte[] payload)
    {
        lock (_ioLock)
        {
            if (!IsConnected || _deviceHandle is null)
                throw new InvalidOperationException("Logitech G300s is not connected.");

            if (!NativeHid.HidD_SetFeature(_deviceHandle, payload, payload.Length))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"HidD_SetFeature(0x{payload[0]:X2}) failed with Win32 error {err}.");
            }
        }
    }

    public ActiveHardwareState GetActiveState()
    {
        byte[] buf = GetFeature(G300sProtocol.ReportActiveState, 4);
        return G300sProtocol.ParseF0State(buf);
    }

    public bool WaitNotBusy(int timeoutMs = 2500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var state = GetActiveState();
            if (!state.IsFlashBusy) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    public void SetActiveProfile(int profileIndex)
    {
        SendFeature(G300sProtocol.MakeSetActiveProfileCmd(profileIndex));
    }

    public void SetActiveDpiStage(int stageIndex)
    {
        SendFeature(G300sProtocol.MakeSetDpiStageCmd(stageIndex));
    }

    public (int R, int G, int B) GetLiveLed()
    {
        byte[] buf = GetFeature(G300sProtocol.ReportLiveLed, 2);
        return G300sProtocol.ParseF1Led(buf);
    }

    public void SetLiveLed(int r, int g, int b)
    {
        SendFeature(G300sProtocol.MakeF1LedCmd(r, g, b));
    }

    public int GetLiveRate()
    {
        byte[] buf = GetFeature(G300sProtocol.ReportLiveRate, 2);
        return G300sProtocol.ParseF2Rate(buf);
    }

    public void SetLiveRate(int hz)
    {
        SendFeature(G300sProtocol.MakeF2RateCmd(hz));
    }

    public byte[] GetProfileRaw(int profileIndex)
    {
        if (profileIndex is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(profileIndex));
        byte reportId = G300sProtocol.ProfileReportIds[profileIndex];
        return GetFeature(reportId, 35);
    }

    public G300sProfile GetProfile(int profileIndex)
    {
        byte[] raw = GetProfileRaw(profileIndex);
        return G300sProfile.FromRawReport(raw, profileIndex);
    }

    public void SaveProfile(G300sProfile profile, bool verify = true)
    {
        byte[] payload = profile.ToRawReport();
        byte[] current = GetProfileRaw(profile.Index);

        if (current.AsSpan().SequenceEqual(payload))
            return;

        if (!WaitNotBusy(2500))
            throw new TimeoutException("Mouse onboard flash controller is busy (0xF0 bit 3).");

        SendFeature(payload);

        for (int poll = 0; poll < 40; poll++)
        {
            Thread.Sleep(50);
            if (!GetActiveState().IsFlashBusy)
                break;
        }

        if (verify)
        {
            byte[] readback = GetProfileRaw(profile.Index);
            if (!readback.AsSpan().SequenceEqual(payload))
            {
                Thread.Sleep(500);
                if (WaitNotBusy(2500))
                {
                    SendFeature(payload);
                    Thread.Sleep(150);
                }
            }
        }
    }

    private void StartInputListener()
    {
        if (_deviceHandle is null || _deviceHandle.IsInvalid) return;

        try
        {
            _inputStream = new FileStream(_deviceHandle, FileAccess.Read, 5, isAsync: true);
            _listenerCts = new CancellationTokenSource();
            CancellationToken token = _listenerCts.Token;

            Task.Run(async () =>
            {
                byte[] buf = new byte[5];
                while (!token.IsCancellationRequested && _inputStream is not null)
                {
                    try
                    {
                        int read = await _inputStream.ReadAsync(buf.AsMemory(0, 5), token);
                        if (read >= 5 && buf[0] == G300sProtocol.ReportInputEvent)
                        {
                            var evt = G300sProtocol.ParseInputEvent(buf);
                            if (evt is not null)
                            {
                                HardwareEventReceived?.Invoke(evt);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        await Task.Delay(200, token).ConfigureAwait(false);
                    }
                }
            }, token);
        }
        catch
        {
            // Device opened in feature-only mode if overlapped read is unavailable
        }
    }

    private static string? FindVendorHidPath(ushort vid, ushort pid, ushort usagePage)
    {
        NativeHid.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfoSet = NativeHid.SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            return null;

        try
        {
            var interfaceData = new NativeHid.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<NativeHid.SP_DEVICE_INTERFACE_DATA>()
            };

            uint index = 0;
            while (NativeHid.SetupDiEnumDeviceInterfaces(
                deviceInfoSet,
                IntPtr.Zero,
                ref hidGuid,
                index,
                ref interfaceData))
            {
                index++;
                string? devPath = GetDevicePath(deviceInfoSet, ref interfaceData);
                if (devPath is null) continue;

                using SafeFileHandle testHandle = NativeHid.CreateFileW(
                    devPath,
                    0,
                    NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeHid.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (testHandle.IsInvalid) continue;

                var attr = new NativeHid.HIDD_ATTRIBUTES
                {
                    Size = Marshal.SizeOf<NativeHid.HIDD_ATTRIBUTES>()
                };

                if (!NativeHid.HidD_GetAttributes(testHandle, ref attr))
                    continue;

                if (attr.VendorID != vid || attr.ProductID != pid)
                    continue;

                if (NativeHid.HidD_GetPreparsedData(testHandle, out IntPtr preparsedData) && preparsedData != IntPtr.Zero)
                {
                    try
                    {
                        if (NativeHid.HidP_GetCaps(preparsedData, out NativeHid.HIDP_CAPS caps) == NativeHid.HIDP_STATUS_SUCCESS)
                        {
                            if (caps.UsagePage == usagePage)
                            {
                                return devPath;
                            }
                        }
                    }
                    finally
                    {
                        NativeHid.HidD_FreePreparsedData(preparsedData);
                    }
                }
            }
        }
        finally
        {
            NativeHid.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return null;
    }

    private static string? GetDevicePath(IntPtr deviceInfoSet, ref NativeHid.SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        NativeHid.SetupDiGetDeviceInterfaceDetailW(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            IntPtr.Zero);

        if (requiredSize == 0) return null;

        IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            // On 64-bit Windows, cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8; on 32-bit it is 6
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

            if (!NativeHid.SetupDiGetDeviceInterfaceDetailW(
                deviceInfoSet,
                ref interfaceData,
                detailBuffer,
                requiredSize,
                out _,
                IntPtr.Zero))
            {
                return null;
            }

            IntPtr pDevicePath = IntPtr.Add(detailBuffer, 4);
            return Marshal.PtrToStringUni(pDevicePath);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    public void Dispose() => Disconnect();

    private static class NativeHid
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        public const uint DIGCF_PRESENT = 0x00000002;
        public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll", SetLastError = true)]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);
    }
}
