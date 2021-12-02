using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
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
     * the C# code for created the junctions were mostly copied from these resources:
     *
     *      - https://gist.github.com/LGM-AdrianHum/260bc9ab3c4cd49bc8617a2abe84ca74
     *      - https://coderedirect.com/questions/136750/check-if-a-file-is-real-or-a-symbolic-link
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

    [StructLayout(LayoutKind.Sequential)]
    public struct OBJECT_ATTRIBUTES : IDisposable
    {
        public int Length;
        public IntPtr RootDirectory;
        private IntPtr objectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;

        public OBJECT_ATTRIBUTES(string name, uint attrs)
        {
            Length = 0;
            RootDirectory = IntPtr.Zero;
            objectName = IntPtr.Zero;
            Attributes = attrs;
            SecurityDescriptor = IntPtr.Zero;
            SecurityQualityOfService = IntPtr.Zero;

            Length = Marshal.SizeOf(this);
            ObjectName = new UNICODE_STRING(name);
        }

        public UNICODE_STRING ObjectName
        {
            get
            {
                return (UNICODE_STRING)Marshal.PtrToStructure(
                 objectName, typeof(UNICODE_STRING));
            }

            set
            {
                bool fDeleteOld = objectName != IntPtr.Zero;
                if (!fDeleteOld)
                    objectName = Marshal.AllocHGlobal(Marshal.SizeOf(value));
                Marshal.StructureToPtr(value, objectName, fDeleteOld);
            }
        }

        public void Dispose()
        {
            if (objectName != IntPtr.Zero)
            {
                Marshal.DestroyStructure(objectName, typeof(UNICODE_STRING));
                Marshal.FreeHGlobal(objectName);
                objectName = IntPtr.Zero;
            }
        }
    }

    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Buffer;

        public UNICODE_STRING(string str)
        {
            Length = (ushort)(str.Length * 2);
            MaximumLength = (ushort)((str.Length * 2) + 1);
            Buffer = str;
        }
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
                device.SetTarget(target);
        }

        public string GetTarget()
        {
            return this.target;
        }

        public Junction[] GetJunctions()
        {
            return junctions.ToArray<Junction>();
        }

        public DosDevice[] GetDosDevices()
        {
            return dosDevices.ToArray<DosDevice>();
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
                Console.WriteLine("[+]\t" + junction.GetBaseDir() + " -> " + junction.GetTargetDir());

            Console.WriteLine("[+] DosDevices:");
            foreach (DosDevice device in this.dosDevices)
                Console.WriteLine("[+]\t" + device.GetName() + " -> " + device.GetTargetName());
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

        public static Symlink FromFile(string path)
        {
            if (!File.Exists(path))
                throw new IOException("Unable to find file: " + path);

            Symlink sym = new Symlink();
            sym.SetLink(path);

            return sym;
        }

        public static Symlink FromFolder(string src)
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

        public static Symlink[] FromFolderToFolder(string src, string dst)
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

        public void SetOpen()
        {
            this.open = true;
        }

        public void SetTarget(string name)
        {
            this.target = @"\??\" + name;
            this.targetName = name;
        }

        public string GetTargetName()
        {
            return targetName;
        }

        public string GetName()
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
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        private bool open;
        private bool created;
        private string baseDir;
        private string targetDir;

        private const int ERROR_NOT_A_REPARSE_POINT = 4390;

        private const int FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const int FSCTL_GET_REPARSE_POINT = 0x000900A8;
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

        public void SetOpen()
        {
            this.open = true;
        }

        public string GetBaseDir()
        {
            return baseDir;
        }

        public string GetTargetDir()
        {
            return targetDir;
        }

        public void SetBaseDir(string baseDir)
        {
            this.baseDir = baseDir;
        }

        public void SetTargetDir(string targetDir)
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
                string target = this.GetTarget();

                if( target != null && target == @"\RPC CONTROL" )
                {
                    Console.WriteLine("[+] Junction " + baseDir + " -> \\RPC Control does already exist.");
                    return;
                }
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

                    int bytesReturned;
                    var result = DeviceIoControl(safeHandle.DangerousGetHandle(), FSCTL_SET_REPARSE_POINT,
                        inBuffer, targetDirBytes.Length + 20, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);

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

                    int bytesReturned;
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

        public string GetTarget()
        {
            if (!Directory.Exists(this.baseDir))
                return null;

            REPARSE_DATA_BUFFER reparseDataBuffer;

            using (SafeFileHandle fileHandle = this.OpenReparsePoint())
            {
                if (fileHandle.IsInvalid)
                {
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                }

                var outBufferSize = Marshal.SizeOf(typeof(REPARSE_DATA_BUFFER));
                var outBuffer = IntPtr.Zero;

                try
                {
                    outBuffer = Marshal.AllocHGlobal(outBufferSize);
                    int bytesReturned;
                    bool success = DeviceIoControl(fileHandle.DangerousGetHandle(), FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0,
                        outBuffer, outBufferSize, out bytesReturned, IntPtr.Zero);

                    fileHandle.Close();

                    if (!success)
                    {
                        if (((uint)Marshal.GetHRForLastWin32Error()) == ERROR_NOT_A_REPARSE_POINT)
                        {
                            return null;
                        }
                        Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                    }

                    reparseDataBuffer = (REPARSE_DATA_BUFFER)Marshal.PtrToStructure(outBuffer, typeof(REPARSE_DATA_BUFFER));
                }
                finally
                {
                    Marshal.FreeHGlobal(outBuffer);
                }
            }

            if (reparseDataBuffer.ReparseTag != IO_REPARSE_TAG_MOUNT_POINT)
            {
                return null;
            }

            string target = Encoding.Unicode.GetString(reparseDataBuffer.MountPointBuffer.PathBuffer,
                reparseDataBuffer.MountPointBuffer.SubstituteNameOffset, reparseDataBuffer.MountPointBuffer.SubstituteNameLength);

            return target;
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


    public class RegistryLink
    {
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern int NtCreateKey(out IntPtr KeyHandle, uint DesiredAccess, [In] OBJECT_ATTRIBUTES ObjectAttributes, int TitleIndex, [In] string Class, int CreateOptions, out int Disposition);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern int NtSetValueKey(SafeRegistryHandle KeyHandle, UNICODE_STRING ValueName, int TitleIndex, int Type, byte[] Data, int DataSize);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern int NtDeleteKey(SafeRegistryHandle KeyHandle);

        private const uint ATTRIBUT_FLAG_CASE_INSENSITIVE = 0x00000040;
        private const uint KEY_ALL_ACCESS = 0x02000000;
        private const int INTERNAL_REG_OPTION_CREATE_LINK = 0x00000002;
        private const int INTERNAL_REG_OPTION_OPEN_LINK = 0x00000100;
        private const int KEY_TYPE_LINK = 0x0000006;

        private bool keepOpen;
        private String target;
        private HashSet<string> links;
        private List<SafeRegistryHandle> handles;
        private Dictionary<SafeRegistryHandle, string> linkMapping;

        public RegistryLink()
        {
            this.target = "";
            this.keepOpen = false;
            this.links = new HashSet<string>();
            this.handles = new List<SafeRegistryHandle>();
            this.linkMapping = new Dictionary<SafeRegistryHandle, string>();
        }

        ~RegistryLink()
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
            this.target = RegistryLink.RegPathToNative(target);
        }

        public string GetTarget()
        {
            return this.target;
        }

        public string[] GetJunctions()
        {
            return links.ToArray<String>();
        }

        public void SetLink(string link)
        {
            links.Clear();
            this.AddLink(link);
        }

        public void AddLink(string link)
        {
            this.links.Add(RegistryLink.RegPathToNative(link));
        }

        private int GetVolatile()
        {
            if (this.keepOpen)
                return 0;

            return 1;
        }

        public void Open()
        {
            if (target == "")
                throw new IOException("SetTarget needs to be called first.");

            foreach(string link in links)
                Open(link, target);
        }

        public void Open(string from, string to)
        {
            Console.WriteLine("Creating registry link from " + from + " to " + to);

            SafeRegistryHandle handle = OpenKey(from);
            handles.Add(handle);
            linkMapping.Add(handle, from);

            UNICODE_STRING value_name = new UNICODE_STRING("SymbolicLinkValue");
            byte[] data = Encoding.Unicode.GetBytes(to);

            int status = NtSetValueKey(handle, value_name, 0, KEY_TYPE_LINK, data, data.Length);

            if (status != 0)
            {
                throw new IOException("Failure while linking " + from + " to " + to);
            }

            Console.WriteLine("Symlink setup successful!");
        }

        public void Close()
        {
            foreach (SafeRegistryHandle handle in handles)
            {
                Console.WriteLine("Deleting symlink " + linkMapping[handle]);
                NtDeleteKey(handle);
            }
        }

        public SafeRegistryHandle OpenKey(string path)
        {
            OBJECT_ATTRIBUTES obj_attr = new OBJECT_ATTRIBUTES(path, ATTRIBUT_FLAG_CASE_INSENSITIVE);
            int disposition = 0;

            IntPtr handle;
            int status = NtCreateKey(out handle, KEY_ALL_ACCESS, obj_attr, 0, null, INTERNAL_REG_OPTION_CREATE_LINK | this.GetVolatile(), out disposition);

            if(status == 0)
                return new SafeRegistryHandle(handle, true);

            throw new IOException("Failure while creating registry key " + path);
        }

        public static string RegPathToNative(string path)
        {
            string regpath = @"\Registry\";

            if (path[0] == '\\')
            {
                return path;
            }

            if (path.StartsWith(@"HKLM\"))
            {
                return regpath + @"Machine\" + path.Substring(5);
            }

            else if (path.StartsWith(@"HKU\"))
            {
                return regpath + @"User\" + path.Substring(4);
            }

            else if (path.StartsWith(@"HKCU\"))
            {
                return regpath + @"User\" + WindowsIdentity.GetCurrent().User.ToString() + @"\" + path.Substring(5);
            }

            throw new IOException("Registry path must be absolute or start with HKLM, HKU or HKCU");
        }
    }
}
