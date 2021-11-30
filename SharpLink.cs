using System;
using System.Collections.Generic;
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
        private bool keepOpen;
        private String target;
        private HashSet<Junction> junctions;
        private HashSet<DosDevice> dosDevices;

        public Symlink()
        {
            this.target = "";
            this.keepOpen = false;
            this.junctions = new HashSet<Junction>();
            this.dosDevices = new HashSet<DosDevice>();
        }

        ~Symlink()
        {
            if (keepOpen)
                return;

            this.Close();
        }

        public void keepAlive()
        {
            this.keepOpen = true;
        }

        public void SetTarget(string target)
        {
            this.target = target;

            foreach (DosDevice device in dosDevices)
                device.setTarget(target);
        }

        public string GetTarget()
        {
            return this.target;
        }

        public void SetLink(string link)
        {
            junctions.Clear();
            dosDevices.Clear();
            this.AddLink(link);
        }

        public void AddLink(string link)
        {
            String linkFile = Path.GetFileName(link);
            String linkDir = Path.GetDirectoryName(link);

            if (String.IsNullOrEmpty(linkDir))
                throw new IOException("Link names are required to contain at least one directory (e.g. example\\link)");

            junctions.Add(new Junction(linkDir));
            dosDevices.Add(new DosDevice(linkFile, target));
        }

        public void GetDetails()
        {
            Console.WriteLine("[+] Target: " + target);

            Console.WriteLine("[+] Junctions:");
            foreach (Junction junction in this.junctions)
                Console.WriteLine("[+]\t" + junction.getBaseDir() + " -> " + junction.getTargetDir());

            Console.WriteLine("[+] DosDevices:");
            foreach (DosDevice device in this.dosDevices)
                Console.WriteLine("[+]\t" + device.getName() + " -> " + device.getTargetName());
        }

        public void Open()
        {
            if (String.IsNullOrEmpty(this.target))
                throw new IOException("Link target is empty!");

            foreach (Junction junction in this.junctions)
                junction.Open();

            foreach (DosDevice device in this.dosDevices)
                device.Open();

            Console.WriteLine("[+] Symlink setup successfully.");
        }

        public void Close()
        {
            foreach (Junction junction in this.junctions)
                junction.Close();

            foreach (DosDevice device in this.dosDevices)
                device.Close();

            Console.WriteLine("[+] Symlink(s) deleted.");
        }

        public static Symlink fromFile(string path)
        {
            if (!File.Exists(path))
                throw new IOException("Unable to find file: " + path);

            Symlink sym = new Symlink();
            sym.SetLink(path);

            return sym;
        }

        public static Symlink fromFolder(string src)
        {
            if (!Directory.Exists(src))
                throw new IOException("Unable to find directory: " + src);

            Console.Write("Delete files in link folder? (y/N) ");
            ConsoleKey response = Console.ReadKey(false).Key;
            Console.WriteLine();

            Symlink sym = new Symlink();

            foreach (string filename in Directory.EnumerateFiles(src))
            {
                if (response == ConsoleKey.Y)
                    File.Delete(filename);

                sym.AddLink(filename);
            }

            return sym;
        }

        public static Symlink[] fromFolderToFolder(string src, string dst)
        {
            if (!Directory.Exists(src))
                throw new IOException("Unable to find directory: " + src);

            if (!Directory.Exists(dst))
                throw new IOException("Unable to find directory: " + dst);

            Console.Write("Delete files in link folder? (y/N) ");
            ConsoleKey response = Console.ReadKey(false).Key;
            Console.WriteLine();

            List<Symlink> symlinks = new List<Symlink>();

            foreach( string filename in Directory.EnumerateFiles(src) )
            {
                Symlink sym = new Symlink();

                if (response == ConsoleKey.Y)
                    File.Delete(filename);

                sym.SetLink(filename);
                sym.SetTarget(dst + "\\" + Path.GetFileName(filename));

                symlinks.Add(sym);
            }

            return symlinks.ToArray();
        }

        public static void Open(Symlink[] links)
        {
            foreach(Symlink link in links)
                link.Open();
        }

        public static void Close(Symlink[] links)
        {
            foreach (Symlink link in links)
                link.Close();
        }
    }

    public class DosDevice
    {

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);

        private bool open;
        private string name;
        private string target;
        private string targetName;

        private const uint DDD_RAW_TARGET_PATH = 0x00000001;
        private const uint DDD_REMOVE_DEFINITION = 0x00000002;
        private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;
        private const uint DDD_NO_BROADCAST_SYSTEM = 0x00000008;

        public DosDevice(string name, string target)
        {
            this.open = false;
            this.name = @"Global\GLOBALROOT\RPC CONTROL\" + name;
            this.target = @"\??\" + target;
            this.targetName = target;
        }

        public void setOpen()
        {
            this.open = true;
        }

        public void setTarget(string name)
        {
            this.target = @"\??\" + name;
            this.targetName = name;
        }

        public string getTargetName()
        {
            return targetName;
        }

        public string getName()
        {
            return name;
        }

        public void Open()
        {
            if (open)
                return;

            Console.WriteLine("[+] Creating DosDevice: " + name + " -> " + target);

            if (DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, name, target) &&
                DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, name, target))
            {
                open = true;
                return;
            }

            throw new IOException("Unable to create DosDevice.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        public void Close()
        {
            if (!open)
                return;

            Console.WriteLine("[+] Deleting DosDevice: " + name + " -> " + target);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, target);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, target);

            open = false;
        }
        public override int GetHashCode()
        {
            return (name + " -> " + target).GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DosDevice);
        }

        public bool Equals(DosDevice other)
        {
            return (name == other.name) && (target == other.target);
        }
    }

    public class Junction
    {

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(string filename, FileAccess access, FileShare share, IntPtr securityAttributes, FileMode fileMode, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        private bool open;
        private bool created;
        private string baseDir;
        private string targetDir;

        private const int FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const int FSCTL_DELETE_REPARSE_POINT = 0x000900AC;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        public Junction(String baseDir) : this(baseDir, @"\RPC CONTROL") { }

        public Junction(String baseDir, String targetDir)
        {
            this.baseDir = baseDir;
            this.targetDir = targetDir;

            this.open = false;
            this.created = false;
        }

        public void setOpen()
        {
            this.open = true;
        }

        public string getBaseDir()
        {
            return baseDir;
        }

        public string getTargetDir()
        {
            return targetDir;
        }

        public void setBaseDir(string baseDir)
        {
            this.baseDir = baseDir;
        }

        public void setTargetDir(string targetDir)
        {
            this.targetDir = targetDir;
        }

        public void Open()
        {
            if (this.open)
                return;

            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
                this.created = true;
            }
            else
            {
                this.created = false;
            }

            if (Directory.EnumerateFileSystemEntries(baseDir).Any())
            {

                Console.Write("[-] Junction directory " + baseDir + " isn't empty. Delete files? (y/N) ");
                ConsoleKey response = Console.ReadKey(false).Key;
                Console.WriteLine();

                if (response == ConsoleKey.Y)
                    foreach (string filename in Directory.EnumerateFileSystemEntries(baseDir))
                        File.Delete(filename);

                else
                    throw new IOException("Junction directory needs to be empty!");
            }

            Console.WriteLine("[+] Creating Junction: " + baseDir + " -> " + targetDir);

            using (var safeHandle = this.OpenReparsePoint())
            {
                var targetDirBytes = Encoding.Unicode.GetBytes(targetDir);

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

                    this.open = true;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }
        }

        public void Close()
        {
            if (!this.open)
                return;

            Console.WriteLine("[+] Removing Junction: " + baseDir + " -> " + targetDir);

            using (var safeHandle = this.OpenReparsePoint())
            {
                var targetDirBytes = Encoding.Unicode.GetBytes(targetDir);

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

                    this.open = false;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }

            if (!this.open && this.created)
                Directory.Delete(baseDir);
        }

        private SafeFileHandle OpenReparsePoint()
        {
            IntPtr handle = CreateFile(baseDir, FileAccess.Read | FileAccess.Write, FileShare.None, IntPtr.Zero, FileMode.Open, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

            if (Marshal.GetLastWin32Error() != 0)
                throw new IOException("OpenReparsePoint failed!", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

            return new SafeFileHandle(handle, true);
        }

        public override int GetHashCode()
        {
            return (baseDir + " -> " + targetDir).GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Junction);
        }

        public bool Equals(Junction other)
        {
            return (baseDir == other.baseDir) && (targetDir == other.targetDir);
        }
    }
}
