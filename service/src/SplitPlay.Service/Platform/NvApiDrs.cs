// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SplitPlay.Core.Nvidia;

namespace SplitPlay.Service.Platform;

/// <summary>
/// NVIDIA driver settings through NvAPI DRS (<c>nvapi64.dll</c> from System32, which the driver installs). The DLL
/// exports only <c>nvapi_QueryInterface</c>; functions are looked up by the interface ids of NVIDIA's open-source
/// <c>nvapi_interface.h</c>, and the structures are laid out by hand from <c>nvapi.h</c> (x64, natural alignment).
/// The DLL is unloaded with each session, so a driver update in between is picked up (upstream Sunshine does the same).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvApiDrs : IDrsProvider
{
    // Interface ids (nvapi_interface.h).
    private const uint IdInitialize = 0x0150E828;
    private const uint IdUnload = 0xD22BDD7E;
    private const uint IdGetErrorMessage = 0x6C2D048C;
    private const uint IdCreateSession = 0x0694D52E;
    private const uint IdDestroySession = 0xDAD9CFF8;
    private const uint IdLoadSettings = 0x375DBD6B;
    private const uint IdSaveSettings = 0xFCBC7E14;
    private const uint IdCreateProfile = 0xCC176068;
    private const uint IdDeleteProfile = 0x17093206;
    private const uint IdFindProfileByName = 0x7E4A9A0B;
    private const uint IdCreateApplication = 0x4347A9DE;
    private const uint IdGetApplicationInfo = 0xED1F8C69;
    private const uint IdSetSetting = 0x577DD202;
    private const uint IdGetSetting = 0x73BF8338;
    private const uint IdDeleteProfileSetting = 0xE4A26362;
    private const uint IdGetBaseProfile = 0xDA8466A0;

    // NvAPI_Status values (nvapi_lite_common.h).
    internal const int Ok = 0;
    internal const int LibraryNotFound = -2;
    internal const int NvidiaDeviceNotFound = -6;
    internal const int SettingNotFound = -160;
    internal const int ProfileNotFound = -163;
    internal const int ExecutableNotFound = -166;

    // NvAPI_UnicodeString is NvU16[2048].
    internal const int UnicodeStringChars = 2048;
    internal const int UnicodeStringBytes = UnicodeStringChars * 2;

    // NVDRS_SETTING_V1: version, settingName, settingId, settingType, settingLocation, isCurrentPredefined,
    // isPredefinedValid, then two 8-byte aligned unions of max(NVDRS_BINARY_SETTING = 4 + 4096, wchar[2048], u64).
    internal const int SettingSize = 12328;
    internal const int SettingIdOffset = 4 + UnicodeStringBytes;            // 4100
    internal const int SettingTypeOffset = SettingIdOffset + 4;             // 4104
    internal const int SettingLocationOffset = SettingTypeOffset + 4;       // 4108
    internal const int SettingCurrentValueOffset = 8224;                    // 4120 + 4104
    internal const uint SettingVersion1 = SettingSize | (1u << 16);         // NVDRS_SETTING_VER1 = 0x13028
    private const uint DwordType = 0;                                       // NVDRS_DWORD_TYPE
    private const uint CurrentProfileLocation = 0;                          // NVDRS_CURRENT_PROFILE_LOCATION

    // NVDRS_PROFILE_V1: version, profileName, gpuSupport, isPredefined, numOfApps, numOfSettings.
    internal const int ProfileSize = 4 + UnicodeStringBytes + 16;           // 4116
    internal const uint ProfileVersion1 = ProfileSize | (1u << 16);

    // NVDRS_APPLICATION_V1: version, isPredefined, appName, userFriendlyName, launcher.
    internal const int ApplicationSize = 8 + 3 * UnicodeStringBytes;        // 12296
    internal const uint ApplicationVersion1 = ApplicationSize | (1u << 16);
    private const int ApplicationNameOffset = 8;
    private const int ApplicationFriendlyNameOffset = ApplicationNameOffset + UnicodeStringBytes;
    private const int ApplicationLauncherOffset = ApplicationFriendlyNameOffset + UnicodeStringBytes;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceFn(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VoidFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ErrorMessageFn(int status, [Out] byte[] message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OutHandleFn(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionFn(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionOutHandleFn(IntPtr session, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionBufferOutHandleFn(IntPtr session, [In, Out] byte[] buffer, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionProfileFn(IntPtr session, IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionProfileBufferFn(IntPtr session, IntPtr profile, [In, Out] byte[] buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionProfileTwoBuffersFn(IntPtr session, IntPtr profile, [In] byte[] name, [In, Out] byte[] buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionProfileIdBufferFn(IntPtr session, IntPtr profile, uint id, [In, Out] byte[] buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionProfileIdFn(IntPtr session, IntPtr profile, uint id);

    public IDrsSession? Open()
    {
        if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(NvApiDrs).Assembly, DllImportSearchPath.System32, out var dll))
        {
            return null;
        }

        Api? api = null;
        try
        {
            if (!NativeLibrary.TryGetExport(dll, "nvapi_QueryInterface", out var queryPtr))
            {
                NativeLibrary.Free(dll);
                return null;
            }

            api = new Api(Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(queryPtr));
            var status = api.Initialize();
            if (status is LibraryNotFound or NvidiaDeviceNotFound)
            {
                NativeLibrary.Free(dll);
                return null;
            }

            api.Check(status, "NvAPI_Initialize");
        }
        catch (NvidiaDrsException)
        {
            api?.Unload();
            NativeLibrary.Free(dll);
            throw;
        }

        // From here the session owns the DLL and frees it when disposed.
        var session = new Session(api, dll);
        try
        {
            session.Load();
            return session;
        }
        catch (NvidiaDrsException)
        {
            session.Dispose();
            throw;
        }
    }

    internal static byte[] UnicodeString(string value)
    {
        var buffer = new byte[UnicodeStringBytes];
        WriteString(buffer, 0, value);
        return buffer;
    }

    internal static void WriteString(byte[] buffer, int offset, string value)
    {
        if (value.Length >= UnicodeStringChars)
        {
            throw new NvidiaDrsException($"name too long for NvAPI: {value[..32]}…");
        }

        Encoding.Unicode.GetBytes(value, 0, value.Length, buffer, offset);
    }

    /// <summary>A DWORD <c>NVDRS_SETTING_V1</c> for NvAPI_DRS_SetSetting.</summary>
    internal static byte[] DwordSetting(uint id, uint value)
    {
        var s = new byte[SettingSize];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0), SettingVersion1);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(SettingIdOffset), id);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(SettingTypeOffset), DwordType);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(SettingLocationOffset), CurrentProfileLocation);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(SettingCurrentValueOffset), value);
        return s;
    }

    /// <summary>The functions, resolved once per session.</summary>
    private sealed class Api(QueryInterfaceFn query)
    {
        private readonly Dictionary<uint, Delegate> _cache = [];

        public int Initialize() => Fn<VoidFn>(IdInitialize)();

        public void Unload()
        {
            try
            {
                Fn<VoidFn>(IdUnload)();
            }
            catch (NvidiaDrsException)
            {
            }
        }

        public T Fn<T>(uint id) where T : Delegate
        {
            if (_cache.TryGetValue(id, out var cached))
            {
                return (T)cached;
            }

            var ptr = query(id);
            if (ptr == IntPtr.Zero)
            {
                throw new NvidiaDrsException($"nvapi64.dll has no interface 0x{id:X8}");
            }

            var fn = Marshal.GetDelegateForFunctionPointer<T>(ptr);
            _cache[id] = fn;
            return fn;
        }

        public void Check(int status, string call)
        {
            if (status != Ok)
            {
                throw new NvidiaDrsException($"{call} failed: {Message(status)}");
            }
        }

        public string Message(int status)
        {
            try
            {
                var buffer = new byte[64];
                if (Fn<ErrorMessageFn>(IdGetErrorMessage)(status, buffer) == Ok)
                {
                    var end = Array.IndexOf(buffer, (byte)0);
                    return $"{Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end)} ({status})";
                }
            }
            catch (NvidiaDrsException)
            {
            }

            return $"NvAPI status {status}";
        }
    }

    private sealed class Session(Api api, IntPtr dll) : IDrsSession
    {
        private IntPtr _session;

        public void Load()
        {
            api.Check(api.Fn<OutHandleFn>(IdCreateSession)(out _session), "NvAPI_DRS_CreateSession");
            api.Check(api.Fn<SessionFn>(IdLoadSettings)(_session), "NvAPI_DRS_LoadSettings");
        }

        public bool ProfileExists(string name) => Find(name) != IntPtr.Zero;

        public void CreateProfile(string name)
        {
            var profile = new byte[ProfileSize];
            BinaryPrimitives.WriteUInt32LittleEndian(profile.AsSpan(0), ProfileVersion1);
            WriteString(profile, 4, name);
            api.Check(api.Fn<SessionBufferOutHandleFn>(IdCreateProfile)(_session, profile, out _), $"NvAPI_DRS_CreateProfile({name})");
        }

        public void DeleteProfile(string name)
        {
            if (Find(name) is var handle && handle != IntPtr.Zero)
            {
                api.Check(api.Fn<SessionProfileFn>(IdDeleteProfile)(_session, handle), $"NvAPI_DRS_DeleteProfile({name})");
            }
        }

        public bool HasApplication(string profile, string executable)
        {
            var app = new byte[ApplicationSize];
            BinaryPrimitives.WriteUInt32LittleEndian(app.AsSpan(0), ApplicationVersion1);
            var status = api.Fn<SessionProfileTwoBuffersFn>(IdGetApplicationInfo)(_session, Require(profile), UnicodeString(executable), app);
            if (status == ExecutableNotFound)
            {
                return false;
            }

            api.Check(status, $"NvAPI_DRS_GetApplicationInfo({profile}, {executable})");
            return true;
        }

        public void AddApplication(string profile, string executable)
        {
            var app = new byte[ApplicationSize];
            BinaryPrimitives.WriteUInt32LittleEndian(app.AsSpan(0), ApplicationVersion1);
            WriteString(app, ApplicationNameOffset, executable);
            WriteString(app, ApplicationFriendlyNameOffset, executable);
            WriteString(app, ApplicationLauncherOffset, "");
            api.Check(api.Fn<SessionProfileBufferFn>(IdCreateApplication)(_session, Require(profile), app),
                $"NvAPI_DRS_CreateApplication({profile}, {executable})");
        }

        public uint? GetSetting(string? profile, uint settingId)
        {
            var setting = new byte[SettingSize];
            BinaryPrimitives.WriteUInt32LittleEndian(setting.AsSpan(0), SettingVersion1);
            var status = api.Fn<SessionProfileIdBufferFn>(IdGetSetting)(_session, Handle(profile), settingId, setting);
            if (status == SettingNotFound)
            {
                return null;
            }

            api.Check(status, $"NvAPI_DRS_GetSetting({profile ?? "base"}, 0x{settingId:X8})");
            var location = BinaryPrimitives.ReadUInt32LittleEndian(setting.AsSpan(SettingLocationOffset));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(setting.AsSpan(SettingTypeOffset));
            if (location != CurrentProfileLocation || type != DwordType)
            {
                // Inherited from the global profile or the driver default: this profile does not set it.
                return null;
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(setting.AsSpan(SettingCurrentValueOffset));
        }

        public void SetSetting(string? profile, uint settingId, uint value) =>
            api.Check(api.Fn<SessionProfileBufferFn>(IdSetSetting)(_session, Handle(profile), DwordSetting(settingId, value)),
                $"NvAPI_DRS_SetSetting({profile ?? "base"}, 0x{settingId:X8})");

        public void DeleteSetting(string? profile, uint settingId)
        {
            var status = api.Fn<SessionProfileIdFn>(IdDeleteProfileSetting)(_session, Handle(profile), settingId);
            if (status != SettingNotFound)
            {
                api.Check(status, $"NvAPI_DRS_DeleteProfileSetting({profile ?? "base"}, 0x{settingId:X8})");
            }
        }

        public void Save() => api.Check(api.Fn<SessionFn>(IdSaveSettings)(_session), "NvAPI_DRS_SaveSettings");

        public void Dispose()
        {
            if (_session != IntPtr.Zero)
            {
                try
                {
                    api.Fn<SessionFn>(IdDestroySession)(_session);
                }
                catch (NvidiaDrsException)
                {
                }

                _session = IntPtr.Zero;
            }

            api.Unload();
            NativeLibrary.Free(dll);
        }

        private IntPtr Find(string name)
        {
            var status = api.Fn<SessionBufferOutHandleFn>(IdFindProfileByName)(_session, UnicodeString(name), out var handle);
            if (status == ProfileNotFound)
            {
                return IntPtr.Zero;
            }

            api.Check(status, $"NvAPI_DRS_FindProfileByName({name})");
            return handle;
        }

        private IntPtr Require(string name) =>
            Find(name) is var handle && handle != IntPtr.Zero ? handle : throw new NvidiaDrsException($"profile \"{name}\" not found");

        private IntPtr Handle(string? profile)
        {
            if (profile is not null)
            {
                return Require(profile);
            }

            api.Check(api.Fn<SessionOutHandleFn>(IdGetBaseProfile)(_session, out var handle), "NvAPI_DRS_GetBaseProfile");
            return handle;
        }
    }
}
