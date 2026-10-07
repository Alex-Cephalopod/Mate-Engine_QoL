using System;
using System.Runtime.InteropServices;
using System.Text;
using MateEngineQoL.Settings;

namespace MateEngineQoL.Bridge
{
    /// <summary>
    /// Encrypts qol_secrets.json with Windows DPAPI, bound to the current Windows user. A copied file can't be read
    /// by another account or on another PC. It does not stop malware already running as you; nothing local can.
    /// </summary>
    public static class SecretProtectors
    {
        public static ISecretProtector CreateForPlatform()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return new DpapiSecretProtector();
#else
            return null; // Linux port: stored as plain base64; see docs/SECURITY.md.
#endif
        }
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        // Extra entropy so other DPAPI users on this account can't decrypt the blob by accident.
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MateEngineQoL.qol_secrets.v1");
        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public string Name => "dpapi-user";

        public byte[] Protect(byte[] plain) => Run(plain, true);
        public byte[] Unprotect(byte[] cipher) => Run(cipher, false);

        static byte[] Run(byte[] input, bool protect)
        {
            var inBlob = new DataBlob();
            var entropyBlob = new DataBlob();
            var outBlob = new DataBlob();
            GCHandle inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            GCHandle entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = input.Length;
                inBlob.pbData = inHandle.AddrOfPinnedObject();
                entropyBlob.cbData = Entropy.Length;
                entropyBlob.pbData = entropyHandle.AddrOfPinnedObject();

                bool ok = protect
                    ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
                if (!ok)
                    throw new System.Security.Cryptography.CryptographicException(Marshal.GetLastWin32Error());

                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
                return output;
            }
            finally
            {
                inHandle.Free();
                entropyHandle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr hMem);
    }
#endif
}
