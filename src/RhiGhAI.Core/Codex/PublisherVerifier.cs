using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RhiGhAI.Core.Codex;

internal static class PublisherVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static void Verify(string path, string requiredOrganization)
    {
        WINTRUST_FILE_INFO fileInfo = new(path);
        IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            WINTRUST_DATA data = new(fileInfoPointer);
            int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            if (result != 0)
            {
                throw new Win32Exception(result, "Authenticode verification failed.");
            }

            using X509Certificate2 certificate = new(X509Certificate.CreateFromSignedFile(path));
            string organization = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.Equals(organization, requiredOrganization, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected publisher: {organization}.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.FilePath);
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, ref WINTRUST_DATA trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WINTRUST_FILE_INFO(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
            FilePath = Marshal.StringToCoTaskMemUni(path);
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;

        public WINTRUST_DATA(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WINTRUST_DATA>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000010;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}
