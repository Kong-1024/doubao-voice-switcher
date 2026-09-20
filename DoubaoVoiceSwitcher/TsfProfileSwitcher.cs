using System.Runtime.InteropServices;

namespace DoubaoVoiceSwitcher;

internal sealed class TsfProfileSwitcher
{
    private const uint TfProfileTypeInputProcessor = 0x0001;
    private const uint TfIppmfForSession = 0x20000000;
    private const uint TfIppmfDontCareCurrentInputLanguage = 0x00000004;
    private const ushort ChineseSimplifiedLangId = 0x0804;

    private static readonly Guid ClsidTfInputProcessorProfiles =
        new("33C53A50-F456-4884-B049-85FD643ECFED");

    private static readonly Guid GuidTfcatTipKeyboard =
        new("34745C63-B2F0-4784-8B67-5E12C8701A31");

    private static readonly Guid DoubaoClsid =
        new("9D2B2E2B-3C93-4D2F-9D35-6EEB85F0D2B0");

    private static readonly Guid DoubaoProfile =
        new("2B4D4B3A-4D4F-4C0A-8E66-7F771A2B9C10");

    private static readonly Guid WeTypeClsid =
        new("86598FB9-66A2-463E-B9C2-AEB906D477AD");

    private static readonly Guid WeTypeProfile =
        new("607FDF85-FCC8-4DBD-A365-41296F980C9C");

    internal void ActivateDoubao() => Activate(DoubaoClsid, DoubaoProfile, "豆包输入法");

    internal void ActivateWeType() => Activate(WeTypeClsid, WeTypeProfile, "微信输入法");

    internal void ValidateProfiles()
    {
        using ProfileManager manager = new();
        manager.GetProfile(DoubaoClsid, DoubaoProfile, "豆包输入法");
        manager.GetProfile(WeTypeClsid, WeTypeProfile, "微信输入法");
    }

    internal bool IsDoubaoActive() => IsActive(DoubaoClsid, DoubaoProfile);

    internal bool IsWeTypeActive() => IsActive(WeTypeClsid, WeTypeProfile);

    private static bool IsActive(Guid clsid, Guid profile)
    {
        using ProfileManager manager = new();
        TfInputProcessorProfile active = manager.GetActiveProfile();
        return active.ProfileType == TfProfileTypeInputProcessor &&
               active.LangId == ChineseSimplifiedLangId &&
               active.Clsid == clsid &&
               active.ProfileGuid == profile;
    }

    private static void Activate(Guid clsid, Guid profile, string name)
    {
        Logger.Log($"Activate: Starting for {name} ({clsid}, {profile})...");
        using ProfileManager manager = new();
        int hr = manager.Instance.ActivateProfile(
            TfProfileTypeInputProcessor,
            ChineseSimplifiedLangId,
            ref clsid,
            ref profile,
            IntPtr.Zero,
            TfIppmfForSession | TfIppmfDontCareCurrentInputLanguage);

        Logger.Log($"Activate: {name} HRESULT = 0x{hr:X8} ({hr})");

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        if (hr == 1)
        {
            throw new InvalidOperationException($"{name}配置存在，但当前用户未启用。");
        }
    }

    private sealed class ProfileManager : IDisposable
    {
        internal ITfInputProcessorProfileMgr Instance { get; }

        internal ProfileManager()
        {
            Type type = Type.GetTypeFromCLSID(ClsidTfInputProcessorProfiles, throwOnError: true)!;
            object instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("无法创建 Windows TSF 配置管理器。");
            Instance = (ITfInputProcessorProfileMgr)instance;
        }

        internal void GetProfile(Guid clsid, Guid profile, string name)
        {
            int hr = Instance.GetProfile(
                TfProfileTypeInputProcessor,
                ChineseSimplifiedLangId,
                ref clsid,
                ref profile,
                IntPtr.Zero,
                out TfInputProcessorProfile result);

            if (hr < 0)
            {
                throw new InvalidOperationException($"Windows 未找到{name}的 TSF 配置（HRESULT 0x{hr:X8}）。");
            }

            if (result.Clsid != clsid || result.ProfileGuid != profile)
            {
                throw new InvalidOperationException($"{name}的 TSF 配置与预期不一致。");
            }
        }

        internal TfInputProcessorProfile GetActiveProfile()
        {
            Guid category = GuidTfcatTipKeyboard;
            int hr = Instance.GetActiveProfile(ref category, out TfInputProcessorProfile profile);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            return profile;
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(Instance))
            {
                Marshal.FinalReleaseComObject(Instance);
            }
        }
    }

    [ComImport]
    [Guid("71C6E74C-0F28-11D8-A82A-00065B84435C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        [PreserveSig]
        int ActivateProfile(
            uint profileType,
            ushort langId,
            ref Guid clsid,
            ref Guid profileGuid,
            IntPtr keyboardLayout,
            uint flags);

        [PreserveSig]
        int DeactivateProfile(
            uint profileType,
            ushort langId,
            ref Guid clsid,
            ref Guid profileGuid,
            IntPtr keyboardLayout,
            uint flags);

        [PreserveSig]
        int GetProfile(
            uint profileType,
            ushort langId,
            ref Guid clsid,
            ref Guid profileGuid,
            IntPtr keyboardLayout,
            out TfInputProcessorProfile profile);

        [PreserveSig]
        int EnumProfiles(ushort langId, out IntPtr enumerator);

        [PreserveSig]
        int ReleaseInputProcessor(ref Guid clsid, uint flags);

        [PreserveSig]
        int RegisterProfile(
            ref Guid clsid,
            ushort langId,
            ref Guid profileGuid,
            [MarshalAs(UnmanagedType.LPWStr)] string description,
            uint descriptionLength,
            [MarshalAs(UnmanagedType.LPWStr)] string iconFile,
            uint iconFileLength,
            uint iconIndex,
            IntPtr substituteKeyboardLayout,
            uint preferredLayout,
            [MarshalAs(UnmanagedType.Bool)] bool enabledByDefault,
            uint flags);

        [PreserveSig]
        int UnregisterProfile(
            ref Guid clsid,
            ushort langId,
            ref Guid profileGuid,
            uint flags);

        [PreserveSig]
        int GetActiveProfile(
            ref Guid category,
            out TfInputProcessorProfile profile);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TfInputProcessorProfile
    {
        internal uint ProfileType;
        internal ushort LangId;
        internal Guid Clsid;
        internal Guid ProfileGuid;
        internal Guid CategoryId;
        internal IntPtr SubstituteKeyboardLayout;
        internal uint Capabilities;
        internal IntPtr KeyboardLayout;
        internal uint Flags;
    }
}
