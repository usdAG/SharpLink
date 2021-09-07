using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace usd
{
    /**
     * Introduces the usd.Symlink type that can be used to create 'file system symbolic links'
     * from a low privileged user account. The created links are not real filesystem symbolic
     * links, but the combination of a junction with an object manager symbolic link in the 
     * '\RPC Control' object directory. This technique was publicized by James Forshaw and
     * implemented within his symboliclink-testing-tools:
     *
     *      - https://github.com/googleprojectzero/symboliclink-testing-tools)
     *
     * We used James's implementation as a reference for the usd.Symlink type. Furthermore,
     * the C# code for created the junctions were mostly copied from this resource:
     *
     *      - https://gist.github.com/LGM-AdrianHum/260bc9ab3c4cd49bc8617a2abe84ca74
     *  
     * The type is intended to be used from PowerShell:
     *
     *      PS C:\> $type = @"
     *      <CODE>
     *      "@
     *      PS C:\> Add-Type $type
     *
     *      PS C:\> $s = New-Object usd.Symlink("C:\ProgramData\target.txt", "C:\Users\Public\example\link")
     *      PS C:\> $s.Open()
     *      [+] Creating Junction: C:\Users\Public\example -> \RPC CONTROL
     *      [+] Creating Symlink: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
     *      [+] Symlink was setup successfully.
     *
     *      PS C:\> echo test > C:\Users\Public\example\link
     *      PS C:\> type C:\ProgramData\target.txt
     *      test
     *
     *      PS C:\> $s.Close()
     *      [+] Removing Junction: C:\Users\Public\example -> \RPC CONTROL
     *      [+] Deleting Symlink: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
     *      [+] Symlink deleted.
     *
     *
     *  Author: Tobias Neitzel (@qtc_de)
     */
    [StructLayout(LayoutKind.Sequential)]
    struct MOUNT_POINT_REPARSE_BUFFER
    {
        public ushort SubstituteNameOffset;
        public ushort SubstituteNameLength;
        public ushort PrintNameOffset;
        public ushort PrintNameLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x3FF0)]
        public byte[] PathBuffer;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct REPARSE_DATA_BUFFER
    {
        [FieldOffset(0)] public uint ReparseTag;
        [FieldOffset(4)] public ushort ReparseDataLength;
        [FieldOffset(6)] public ushort Reserved;
        [FieldOffset(8)] public MOUNT_POINT_REPARSE_BUFFER MountPointBuffer;
    }

    public class Symlink
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(string filename, FileAccess access, FileShare share, IntPtr securityAttributes,
                                               FileMode fileMode, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
                                           IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);

        public string link;
        public string target;
        public string linkDir;

        private bool created = false;
        private bool linkOpen = false;
        private bool junctionOpen = false;
        private const string objdir = @"\RPC CONTROL";

        private const uint DDD_RAW_TARGET_PATH = 0x00000001;
        private const uint DDD_REMOVE_DEFINITION = 0x00000002;
        private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;
        private const uint DDD_NO_BROADCAST_SYSTEM = 0x00000008;

        private const int FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const int FSCTL_DELETE_REPARSE_POINT = 0x000900AC;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        public Symlink() { }

        public Symlink(string link)
        {
            this.SetLink(link);
        }

        public Symlink(string target, string link)
        {
            this.SetTarget(target);
            this.SetLink(link);
        }

        ~Symlink()
        {
            if (this.linkOpen)
                this.DeleteSymlink();

            if (this.junctionOpen)
                this.DeleteJunction();
        }

        public void SetTarget(string target)
        {
            this.target = target;
        }

        public string GetTarget()
        {
            return this.target;
        }

        public void SetLink(string link)
        {
            this.link = Path.GetFileName(link);
            this.linkDir = Path.GetDirectoryName(link);

            if (String.IsNullOrEmpty(this.linkDir))
                throw new IOException("Link names are required to contain at least one directory (e.g. example\\link)");
        }

        public string GetLink()
        {
            return this.linkDir + @"\" + this.link;
        }

        public void Open()
        {
            if (!this.CheckAttrs())
                return;

            this.CreateJunction();
            this.CreateSymlink();

            Console.WriteLine("[+] Symlink was setup successfully.");
        }

        public void Close()
        {
            if (!this.CheckAttrs())
                return;

            this.DeleteJunction();
            this.DeleteSymlink();

            Console.WriteLine("[+] Symlink deleted.");
        }

        public void CreateSymlink()
        {
            if (!this.CheckAttrs())
                return;

            string linkName = @"Global\GLOBALROOT" + Symlink.objdir + @"\" + this.link;
            string targetName = @"\??\" + this.target;

            Console.WriteLine("[+] Creating Symlink: " + linkName + " -> " + targetName);

            if (DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, linkName, targetName) &&
                DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, linkName, targetName))
            {
                this.linkOpen = true;
                return;
            }

            throw new IOException("Unable to create DosDevice.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        public void DeleteSymlink()
        {
            if (!this.CheckAttrs())
                return;

            string linkName = @"Global\GLOBALROOT" + Symlink.objdir + @"\" + this.link;
            string targetName = @"\??\" + this.target;

            Console.WriteLine("[+] Deleting Symlink: " + linkName + " -> " + targetName);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, linkName, targetName);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, linkName, targetName);

            this.linkOpen = false;
        }

        public void CreateJunction()
        {
            if (String.IsNullOrEmpty(this.linkDir))
            {
                Console.WriteLine("[-] Symlink is missing attributes.");
                Console.WriteLine("[-] Use SetLink function before creating the Junction.");

                return;
            }

            if (!Directory.Exists(this.linkDir))
            {
                Directory.CreateDirectory(this.linkDir);
                this.created = true;
            }
            else
            {
                this.created = false;
            }

            if (Directory.EnumerateFileSystemEntries(this.linkDir).Any())
                throw new IOException("Directory containing the link needs to be empty!");

            Console.WriteLine("[+] Creating Junction: " + this.linkDir + " -> " + Symlink.objdir);

            using (var safeHandle = this.OpenReparsePoint())
            {
                var targetDirBytes = Encoding.Unicode.GetBytes(Symlink.objdir);

                var reparseDataBuffer =
                    new REPARSE_DATA_BUFFER
                    {
                        ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                        ReparseDataLength = (ushort)(targetDirBytes.Length + 12),
                        MountPointBuffer = new MOUNT_POINT_REPARSE_BUFFER
                        {
                            SubstituteNameOffset = 0,
                            SubstituteNameLength = (ushort)targetDirBytes.Length,
                            PrintNameOffset = (ushort)(targetDirBytes.Length + 2),
                            PrintNameLength = 0,
                            PathBuffer = new byte[0x3ff0]
                        }
                    };

                Array.Copy(targetDirBytes, reparseDataBuffer.MountPointBuffer.PathBuffer, targetDirBytes.Length);

                var inBufferSize = Marshal.SizeOf(reparseDataBuffer);
                var inBuffer = Marshal.AllocHGlobal(inBufferSize);

                try
                {
                    Marshal.StructureToPtr(reparseDataBuffer, inBuffer, false);

                    uint bytesReturned;
                    var result = DeviceIoControl(safeHandle.DangerousGetHandle(), FSCTL_SET_REPARSE_POINT,
                        inBuffer, (uint)targetDirBytes.Length + 20, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);

                    if (!result)
                        throw new IOException("Unable to create Junction!", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

                    this.junctionOpen = true;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }
        }

        public void DeleteJunction()
        {
            if (String.IsNullOrEmpty(this.linkDir))
            {
                Console.WriteLine("[-] Symlink is missing attributes.");
                Console.WriteLine("[-] Use SetLink function before deleting a Junction.");

                return;
            }

            Console.WriteLine("[+] Removing Junction: " + this.linkDir + " -> " + Symlink.objdir);

            using (var safeHandle = this.OpenReparsePoint())
            {
                var targetDirBytes = Encoding.Unicode.GetBytes(Symlink.objdir);

                var reparseDataBuffer =
                    new REPARSE_DATA_BUFFER
                    {
                        ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                        ReparseDataLength = 0,
                        MountPointBuffer = new MOUNT_POINT_REPARSE_BUFFER
                        {
                            PathBuffer = new byte[0x3ff0]
                        }
                    };

                var inBufferSize = Marshal.SizeOf(reparseDataBuffer);
                var inBuffer = Marshal.AllocHGlobal(inBufferSize);

                try
                {
                    Marshal.StructureToPtr(reparseDataBuffer, inBuffer, false);

                    uint bytesReturned;
                    var result = DeviceIoControl(safeHandle.DangerousGetHandle(), FSCTL_DELETE_REPARSE_POINT,
                        inBuffer, 8, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);

                    if (!result)
                        throw new IOException("Unable to delete Junction!", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

                    this.junctionOpen = false;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }

            if (!this.junctionOpen && this.created)
                Directory.Delete(this.linkDir);
        }

        private SafeFileHandle OpenReparsePoint()
        {
            IntPtr handle = CreateFile(this.linkDir, FileAccess.Read | FileAccess.Write, FileShare.None, IntPtr.Zero, FileMode.Open, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

            if (Marshal.GetLastWin32Error() != 0)
                throw new IOException("OpenReparsePoint failed!", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

            return new SafeFileHandle(handle, true);
        }

        private bool CheckAttrs()
        {
            if (String.IsNullOrEmpty(this.link) || String.IsNullOrEmpty(this.target))
            {
                Console.WriteLine("[-] Symlink is missing attributes.");
                Console.WriteLine("[-] Use the SetTarget and SetLink functions before creating the link.");

                return false;
            }

            return true;
        }
    }
}
