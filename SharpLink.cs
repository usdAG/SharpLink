using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace de.usd.SharpLink
{
    /**
     * This namespace contains classes that allow low privileged user accounts to create
     * file system and registry symbolic links.
     *
     * File system symbolic links created by functions from this namespace are pseudo-links
     * that consist out of the combination of a junction with an object manager symbolic link
     * in the '\RPC Control' object directory. This technique was publicized by James Forshaw
     * and implemented within his symboliclink-testing-tools:
     *
     *      - https://github.com/googleprojectzero/symboliclink-testing-tools)
     *
     * We used James's implementation as a reference for the classes implemented in this namespace.
     * Moreover, the C# code for creating the junctions was mostly copied from these resources:
     *
     *      - https://gist.github.com/LGM-AdrianHum/260bc9ab3c4cd49bc8617a2abe84ca74
     *      - https://coderedirect.com/questions/136750/check-if-a-file-is-real-or-a-symbolic-link
     *
     * Also the implementation of registry symbolic links is very close to the one within the
     * symboliclink-testing-tools and all credits go to James again. Furthermore, the following
     * resource was used as a refernece:
     *
     *      - https://bugs.chromium.org/p/project-zero/issues/detail?id=872
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    [StructLayout(LayoutKind.Sequential)]
    struct KEY_VALUE_INFORMATION
    {
        public uint TitleIndex;
        public uint Type;
        public uint DataLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x400)]
        public byte[] Data;
    }

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

    /**
     * The Symlink class is used for creating file system symbolic links. In the easiest case,
     * an instance of Symlink is coupled to one symbolic link on the file system. However, this
     * does not need to be the case and an instance of Symlink may contain multiple links that
     * point to the same target.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class Symlink
    {
        // whether to keep the symbolic link alive when the Symlink instance is cleaned up
        private bool keepOpen;

        // the shared target of all symbolic links contained within this container
        private String target;

        // all Junctions opened by this container
        private HashSet<Junction> junctions;

        // all DosDevices opened to this contained
        private HashSet<DosDevice> dosDevices;

        /**
         * Instances of Symlink should be created without any arguments. Required information
         * should be added later on the existing object.
         */
        public Symlink()
        {
            this.target = "";
            this.keepOpen = false;
            this.junctions = new HashSet<Junction>();
            this.dosDevices = new HashSet<DosDevice>();
        }

        /**
         * If keepOpen is not set to true, all remaining symbolic links opened by this container
         * are closed.
         */
        ~Symlink()
        {
            if (keepOpen)
                return;

            this.Close();
        }

        /**
         * Tell the instance to keep it's symoblic links alive.
         */
        public void keepAlive()
        {
            this.keepOpen = true;
        }

        /**
         * Set the target used by all symbolic links contained in this container. The specified
         * target is set for all alreday added symbolic links and for all links that are added in
         * future.
         *
         * @param target file system path to the target that is shared among all links in this container
         */
        public void SetTarget(string target)
        {
            this.target = target;

            foreach (DosDevice device in dosDevices)
                device.SetTarget(target);
        }

        /**
         * Return the currently configured target.
         * 
         * @return file system path of the target
         */
        public string GetTarget()
        {
            return this.target;
        }

        /**
         * Return all Junction objects assigned to this container.
         * 
         * @return Junctions associated to the Symlink container
         */
        public Junction[] GetJunctions()
        {
            return junctions.ToArray<Junction>();
        }

        /**
         * Return all DosDevice objects assigned to this container.
         * 
         * @return DosDevices associated to the Symlink container
         */
        public DosDevice[] GetDosDevices()
        {
            return dosDevices.ToArray<DosDevice>();
        }

        /**
         * Removes all previously added Junctions and DosDevices and replaces them with
         * a Junction and a DosDevice for the specified link. Junctions and DosDevices are not
         * closed during this action.
         *
         * @param link file system path for the new symbolic link
         */
        public void SetLink(string link)
        {
            junctions.Clear();
            dosDevices.Clear();
            this.AddLink(link);
        }

        /**
         * Add a new symbolic link to the container. This creates the requied Junction and
         * DosDevice, but does not open the link already.
         *
         * @param link file system path for the new symbolic link
         */
        public void AddLink(string link)
        {
            String linkFile = Path.GetFileName(link);
            String linkDir = Path.GetDirectoryName(link);

            if (String.IsNullOrEmpty(linkDir))
                throw new IOException("Link names are required to contain at least one directory (e.g. example\\link)");

            junctions.Add(new Junction(linkDir));
            dosDevices.Add(new DosDevice(linkFile, target));
        }

        /**
         * Show some status information on the container. This prints the target name, all the configured
         * Junctions and the configured DosDevices.
         */
        public void GetDetails()
        {
            Console.WriteLine("[+] Target: {0}", target);

            Console.WriteLine("[+] Junctions:");

            foreach (Junction junction in this.junctions)
                Console.WriteLine("[+]\t {0} -> {1}", junction.GetBaseDir(), junction.GetTargetDir());

            Console.WriteLine("[+] DosDevices:");

            foreach (DosDevice device in this.dosDevices)
                Console.WriteLine("[+]\t {0} -> {1}", device.GetName(), device.GetTargetName());
        }

        /**
         * Checks whether a target was specified and open all Junctions and DosDevices that were
         * configured for this container.
         */
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

        /**
         * Closes all Junctions and DosDevices configured for this container. DosDevices and Junctions
         * are only closed when they were created by the corresponding object.
         */
        public void Close()
        {
            foreach (Junction junction in this.junctions)
                junction.Close();

            foreach (DosDevice device in this.dosDevices)
                device.Close();

            Console.WriteLine("[+] Symlink(s) deleted.");
        }

        /**
         * Enforces a close operation on all contained Junctions and DosDevices. This skips the created
         * check.
         */
        public void ForceClose()
        {
            foreach (Junction junction in this.junctions)
                junction.ForceClose();

            foreach (DosDevice device in this.dosDevices)
                device.ForceClose();

            Console.WriteLine("[+] Symlink(s) deleted.");
        }

        /**
         * Creates a Symlink object from a file. The specified path is basically turned
         * into a symlink. This operation does not set a target for the link and the
         * target needs to be set before or after this operation.
         *
         * @param path file system path that is replaced by the symlink
         * @return Symlink object that replaces the file
         */
        public static Symlink FromFile(string path)
        {
            if (!File.Exists(path))
                throw new IOException("Unable to find file: " + path);

            Console.Write("Delete existing filer? (y/N) ");
            ConsoleKey response = Console.ReadKey(false).Key;
            Console.WriteLine();

            if (response == ConsoleKey.Y)
                File.Delete(path);

            Symlink sym = new Symlink();
            sym.SetLink(path);

            return sym;
        }

        /**
         * Creates a Symlink that contains one link for each file within a folder. Optionally
         * deletes the files in the folder. This operation does not set a target for these links
         * and the target needs to be set before or after this operation.
         *
         * @param src file system path to a folder to create links from
         * @return Symlink object containing one link for each file
         */
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

        /**
         * Whereas the FromFolder method creates one Symlink instance that contains multiple
         * file system symbolic links poiting to the same target, this method creates multiple
         * Symlink objects with each of them pointing to a different target.
         *
         * For each file in the src folder, a symbolic link is created that points to a file with
         * the same name in the destination folder. Files in the src folder are optionally deleted.
         *
         * @param src file system path to a folder to create links from
         * @param dst file system path to a folder where the links point to
         * @return array of Symlink objects
         */
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

            foreach (string filename in Directory.EnumerateFiles(src))
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

        /**
         * Open an array of Symlink objects. This is useful together with the FromFolderToFolder
         * method.
         *
         * @param links array of Symlink objects
         */
        public static void Open(Symlink[] links)
        {
            foreach (Symlink link in links)
                link.Open();
        }

        /**
         * Close an array of Symlink objects. This is useful together with the FromFolderToFolder
         * method.
         *
         * @param links array of Symlink objects
         */
        public static void Close(Symlink[] links)
        {
            foreach (Symlink link in links)
                link.Close();
        }

        /**
         * Enforce the close operation on an array of Symlinks.
         *
         * @param links array of Symlink objects
         */
        public static void ForceClose(Symlink[] links)
        {
            foreach (Symlink link in links)
                link.ForceClose();
        }
    }

    /**
     * The DosDevice class is used for creating mappings between the RPC Control object directory
     * and the file system. These mappings are required for creating the pseudo file system links.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class DosDevice
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

        // whether the current object has created the DosDevice
        private bool created;

        // name of the DosDevice (should match symlink name)
        private string name;

        // path to the target file on the file system with the \??\ prefix
        private string target;

        // plain path to the  target file on the file system
        private string targetName;

        private const uint DDD_RAW_TARGET_PATH = 0x00000001;
        private const uint DDD_REMOVE_DEFINITION = 0x00000002;
        private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;
        private const uint DDD_NO_BROADCAST_SYSTEM = 0x00000008;


        /**
         * Creating a DosDevice instance requires the DosDevice name that is created within the RPC Control
         * object directory and the file system path to the targeted file.
         *
         * @param name DosDevice name
         * @param target file system path of the target
         */
        public DosDevice(string name, string target)
        {
            this.created = false;
            this.name = @"Global\GLOBALROOT\RPC CONTROL\" + name;
            this.target = @"\??\" + target;
            this.targetName = target;
        }

        /**
         * Set the target of the DosDevice.
         *
         * @param name file system path of the new target
         */
        public void SetTarget(string name)
        {
            this.target = @"\??\" + name;
            this.targetName = name;
        }

        /**
         * Get the target of the DosDevice.
         *
         * @return currently configured target of the DosDevice
         */
        public string GetTargetName()
        {
            return targetName;
        }

        /**
         * Set the name of the DosDevice.
         *
         * @param name new name for the DosDevice
         */
        public void SetName(string name)
        {
            this.name = name;
        }

        /**
         * Get the name of the DosDevice.
         *
         * @return currently configured name of the DosDevice
         */
        public string GetName()
        {
            return name;
        }

        /**
         * The actual Open function is defined as a static function. This allows users to manually control
         * the opening of DosDevices if required. This function is a wrapper around the static function that
         * passes the preconfigured file system paths. If the static Open function returns true, the DosDevice
         * was created and we set created to true.
         */
        public void Open()
        {
            this.created = Open(name, target);
        }

        /**
         * The actual Close function is defined as a static function. This allows users to manually close
         * DosDevices if required. This function is a wrapper around the static function that passes the
         * preconfigured file system paths to it. DosDevices are only closed if they were created by this
         * object.
         */
        public void Close()
        {
            if (this.created)
                Close(name, target);
        }

        /**
         * Similar to the previously defined close function, but enforces closing even if device was not created
         * by this object.
         */
        public void ForceClose()
        {
            Close(name, target);
        }

        /**
         * Symlink objects store DosDevices within a Set which requires a GetHashCode method to be present.
         * This implementation uses the combination name+target as a unique identifier.
         */
        public override int GetHashCode()
        {
            return (name + " -> " + target).GetHashCode();
        }

        /**
         * Equals wrapper.
         *
         * @param obj object to compare with
         */
        public override bool Equals(object obj)
        {
            return Equals(obj as DosDevice);
        }

        /**
         * Two DosDevices are treated to be equal when they have the same name and target.
         *
         * @param other DosDevice to compare with
         */
        public bool Equals(DosDevice other)
        {
            return (name == other.name) && (target == other.target);
        }

        /**
         * Open a new DosDevice with the user specified paths.
         *
         * @param name name of the DosDevice
         * @param to file system path to point to
         * @return true if the device was successfully created
         */
        public static bool Open(string name, string to)
        {
            if (CheckOpen(name, to, true))
            {
                Console.WriteLine("[+] DosDevice {0} -> {1} does already exist.", name, to);
                return false;
            }

            Console.WriteLine("[+] Creating DosDevice: {0} -> {1}", name, to);

            if (DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, name, to) &&
                DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, name, to))
            {
                return true;
            }

            throw new IOException("Unable to create DosDevice.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        /**
         * Close the specified DosDevice.
         *
         * @param name name of the DosDevice
         * @param to file system path the DosDevice points to
         */
        public static void Close(string name, string to)
        {
            if (!CheckOpen(name, to, true))
                return;

            Console.WriteLine("[+] Deleting DosDevice: {0} -> {1}", name, to);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, to);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, to);
        }

        /**
         * Uses the QueryDosDevice function to determine whether the DosDevice does already exist. Returns true
         * in this case, false otherwise. When called with verbose set to true, a warning is printed when a DosDevive
         * with the requested device name does alreday exist, but the target name is different.
         * 
         * @param name name of the DosDevice
         * @param to file system path the DosDevice points to
         * @return true if the dos device exists, false otherwise
         */
        public static bool CheckOpen(string name, string to, bool verbose)
        {
            StringBuilder pathInformation = new StringBuilder(250);
            uint result = QueryDosDevice(name, pathInformation, 250);

            String destination = pathInformation.ToString();

            if (result == 0)
                return false;

            if (destination != to && verbose)
            {
                Console.WriteLine("[!] Warning: DosDevice {0} exists but is pointing to {1}.", name, destination);
                Console.WriteLine("[!] DosDevice is treated as open, but may point to an unintended location.");
            }

            return true;
        }
    }

    /**
     * The Junction class is used for creating file system junctions from C#. Together with
     * DosDevices, Junctions are used to build pseudo symbolic links on the file system.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class Junction
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(string filename, FileAccess access, FileShare share, IntPtr securityAttributes, FileMode fileMode, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        // whether the current instance has created the Junction
        private bool created;

        // whether the junction directory was created by this instance
        private bool dirCreated;

        // base directory the junction starts from
        private string baseDir;

        // target directory the junction is pointing to
        private string targetDir;

        private const int FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const int FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const int FSCTL_DELETE_REPARSE_POINT = 0x000900AC;
        private const uint ERROR_NOT_A_REPARSE_POINT = 0x80071126;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        /**
         * By default, Junctions are created using the user specified baseDir and point to the
         * RPC Control object directory.
         *
         * @param baseDir base directory for the junction
         */
        public Junction(String baseDir) : this(baseDir, @"\RPC CONTROL") { }

        /**
         * For some reasons, users may want to create a Junction with a custom target.
         *
         * @param baseDir base directory for the junction
         * @param targetDir target directory of the junction
         */
        public Junction(String baseDir, String targetDir)
        {
            this.created = false;
            this.dirCreated = false;

            this.baseDir = baseDir;
            this.targetDir = targetDir;
        }

        /**
         * Return the base directory of the junction.
         *
         * @return base directory of the junction
         */
        public string GetBaseDir()
        {
            return baseDir;
        }

        /**
         * Return the target directory of the junction.
         *
         * @return target directory of the junction
         */
        public string GetTargetDir()
        {
            return targetDir;
        }

        /**
         * Set the base directory of the junction.
         *
         * @return baseDir file system path used as base directory for the junction
         */
        public void SetBaseDir(string baseDir)
        {
            this.baseDir = baseDir;
        }

        /**
         * Set the target directory of the junction.
         *
         * @return targetDir file system path used as target directory of the junction
         */
        public void SetTargetDir(string targetDir)
        {
            this.targetDir = targetDir;
        }

        /**
         * Wrapper around the static Open function that performs the actual operation.
         */
        public void Open()
        {
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
                this.dirCreated = true;
            }

            this.created = Open(baseDir, targetDir);
        }

        /**
         * Wrapper around the static Close function that performs the actual operation.
         * Junctions are only removed if they were created by this object.
         */
        public void Close()
        {
            if (!this.created)
                return;

            Close(baseDir, targetDir);

            if (this.dirCreated)
                Directory.Delete(baseDir);
        }

        /**
         * Enforce closing of the junction object. Skips the created check.
         */
        public void ForceClose()
        {
            Close(baseDir);

            if (this.dirCreated)
                Directory.Delete(baseDir);
        }

        /**
         * Instances of Symlink store Junction objects within a Set. This requires the Junction class to implement
         * the GetHashCode method. Junctions are uniquly identified by the combination of baseDir+targetDir.
         */
        public override int GetHashCode()
        {
            return (baseDir + " -> " + targetDir).GetHashCode();
        }

        /**
         * Equals wrapper.
         */
        public override bool Equals(object obj)
        {
            return Equals(obj as Junction);
        }

        /**
         * Two Junction objects are treated equals if they have a matching baseDir and targetDir.
         */
        public bool Equals(Junction other)
        {
            return (baseDir == other.baseDir) && (targetDir == other.targetDir);
        }

        /**
         * Create (open) the junction. The function first checks whether a corresponding junction already
         * exists and uses the DeviceIoControl function to create one if this is not the case.
         *
         * @param baseDir directory to create the junction from
         * @param targetDir directory the junction is pointing to
         * @return true if the junction was created by this function
         */
        public static bool Open(string baseDir, string targetDir)
        {
            if (!Directory.Exists(baseDir))
            {
                throw new IOException("Junction base directory " + baseDir + " does not exist");
            }

            string existingTarget = GetTarget(baseDir);

            if (existingTarget != null && existingTarget == @"\RPC CONTROL")
            {
                Console.WriteLine("[+] Junction {0} -> \\RPC Control does already exist.", baseDir);
                return false;
            }

            DirectoryInfo baseDirInfo = new DirectoryInfo(baseDir);

            if (baseDirInfo.EnumerateFileSystemInfos().Any())
            {
                Console.Write("[!] Junction directory {0} isn't empty. Delete files? (y/N) ", baseDir);
                ConsoleKey response = Console.ReadKey(false).Key;
                Console.WriteLine();

                if (response == ConsoleKey.Y)
                {
                    foreach (FileInfo file in baseDirInfo.EnumerateFiles())
                    {
                        file.Delete();
                    }
                    foreach (DirectoryInfo dir in baseDirInfo.EnumerateDirectories())
                    {
                        dir.Delete(true);
                    }
                }

                else
                    throw new IOException("Junction directory needs to be empty!");
            }

            Console.WriteLine("[+] Creating Junction: {0} -> {1}", baseDir, targetDir);

            using (var safeHandle = OpenReparsePoint(baseDir))
            {
                var targetDirBytes = Encoding.Unicode.GetBytes(targetDir);
                var reparseDataBuffer = new REPARSE_DATA_BUFFER
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

                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }
        }

        /**
         * Remove (close) the junction. The function first checks whether the junction is open and needs to be closed.
         * If this is the case, the DeviceIoControl function is used to close it. If the junction points to a unexpected
         * target, it isn't closed.
         *
         * @param baseDir base directory of the junction
         * @param targetDir target directory of the junction
         */
        public static void Close(string baseDir, string targetDir)
        {
            string target = GetTarget(baseDir);

            if (target == null)
            {
                Console.WriteLine("[+] Junction was already closed.");
                return;
            }

            else if (target != targetDir)
            {
                Console.WriteLine("[!] Junction points to an unexpected location.");
                Console.WriteLine("[!] Keeping it open.");
                return;
            }

            Close(baseDir);
        }

        /**
         * Simplified version of the Close function that skips check on the junction target.
         *
         * @param baseDir base directory of the junction
         */
        public static void Close(string baseDir)
        {
            Console.WriteLine("[+] Removing Junction: {0}", baseDir);

            using (var safeHandle = OpenReparsePoint(baseDir))
            {
                var reparseDataBuffer = new REPARSE_DATA_BUFFER
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
                }

                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }
        }

        /**
         * Attempt to obtain the repase point from the current junction configuration. This can be used to
         * determine whether the junction is open and points to the exepcetd location.
         *
         * @param baseDir base directory of the junction
         * @return target the junction is poiting to
         */
        public static string GetTarget(string baseDir)
        {
            if (!Directory.Exists(baseDir))
                return null;

            REPARSE_DATA_BUFFER reparseDataBuffer;

            using (SafeFileHandle fileHandle = OpenReparsePoint(baseDir))
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

        /**
         * Create a SafeFileHandle for the current junction configuration.
         *
         * @param baseDir base directory of the junction
         * @return SafeFileHandle file handle for the junction
         */
        private static SafeFileHandle OpenReparsePoint(string baseDir)
        {
            IntPtr handle = CreateFile(baseDir, FileAccess.Read | FileAccess.Write, FileShare.None, IntPtr.Zero, FileMode.Open,
                                       FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

            if (Marshal.GetLastWin32Error() != 0)
                throw new IOException("OpenReparsePoint failed!", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

            return new SafeFileHandle(handle, true);
        }
    }

    /**
     * The RegistryLink class can be used to create symbolic links within the Windows registry.
     * Registry links are limited in their capabilities by the operating system. Therefore, it
     * is only possible to create links within the same registry hive.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class RegistryLink
    {
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern int NtCreateKey(out IntPtr KeyHandle, uint DesiredAccess, [In] OBJECT_ATTRIBUTES ObjectAttributes, int TitleIndex, [In] string Class, int CreateOptions, out int Disposition);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern int NtSetValueKey(SafeRegistryHandle KeyHandle, UNICODE_STRING ValueName, int TitleIndex, int Type, byte[] Data, int DataSize);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern int NtDeleteKey(SafeRegistryHandle KeyHandle);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern uint NtOpenKeyEx(out IntPtr hObject, uint DesiredAccess, [In] OBJECT_ATTRIBUTES ObjectAttributes, int OpenOptions);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern int NtQueryValueKey(SafeRegistryHandle KeyHandle, UNICODE_STRING ValueName, uint InformationClass, out KEY_VALUE_INFORMATION ValueInformation, int size, out int sizeRequired);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        static extern int NtClose(SafeRegistryHandle KeyHandle);

        enum KEY_VALUE_INFORMATION_CLASS : uint
        {
            KeyValueBasicInformation,
            KeyValueFullInformation,
            KeyValuePartialInformation,
            KeyValueFullInformationAlign64,
            KeyValuePartialInformationAlign64,
            KeyValueLayerInformation,
            MaxKeyValueInfoClass
        }

        private const uint ATTRIBUT_FLAG_OBJ_OPENLINK = 0x00000100;
        private const uint ATTRIBUT_FLAG_CASE_INSENSITIVE = 0x00000040;

        private const uint KEY_ALL_ACCESS = 0x02000000;
        private const uint KEY_READ = 0x20019;
        private const int KEY_TYPE_LINK = 0x0000006;

        private const int REG_OPTION_OPEN_LINK = 0x0008;
        private const int REG_OPTION_CREATE_LINK = 0x00000002;

        private const string regpath = @"\Registry\";

        // whether to keep the registry link open after the RegistryLink instance was cleaned up
        private bool keepOpen;

        // shared target of all registry links
        private String target;

        // a set containing all associated registry links that point to the shared target 
        private HashSet<string> links;

        // a set containing all links that have been opened by this cotnainer
        private HashSet<string> openedLinks;

        /**
         * Create a new instance of RegistryLink. The constructor is used without specifying any
         * attributes and only performs some initalization. Attributes like the target and the actual
         * links should be assigned on the created object.
         */
        public RegistryLink()
        {
            this.target = "";
            this.keepOpen = false;
            this.links = new HashSet<string>();
            this.openedLinks = new HashSet<string>();
        }

        /**
         * When a RegistryLink instance is cleaned up, it closes all remaining links automatically, except
         * when keepOpen is set to true.
         */
        ~RegistryLink()
        {
            if (keepOpen)
                return;

            this.Close();
        }

        /**
         * Tell the RegistryLink instance to keep registry links alive even after closing.
         */
        public void keepAlive()
        {
            this.keepOpen = true;
        }

        /**
         * Set the shared target for all registry links.
         *
         * @param target shared target for all registry links
         */
        public void SetTarget(string target)
        {
            this.target = RegistryLink.RegPathToNative(target);
        }

        /**
         * Return the currently configured shared target.
         *
         * @return shared target for all registry links
         */
        public string GetTarget()
        {
            return this.target;
        }

        /**
         * Return a list with all registry links that are stored in this container.
         *
         * @return list of registry links in this container
         */
        public string[] GetLinks()
        {
            return links.ToArray<String>();
        }

        /**
         * Remove all alreday configured links and replace them by the specified one.
         *
         * @param link registry key to create the link in
         */
        public void SetLink(string link)
        {
            links.Clear();
            this.AddLink(link);
        }

        /**
         * Add a new registry link to the list of configured links.
         *
         * @param link registry key to create the link in
         */
        public void AddLink(string link)
        {
            this.links.Add(RegistryLink.RegPathToNative(link));
        }

        /**
         * Wrapper around the static Open function. Open all links contained within this container.
         * This requires a target to be set.
         */
        public void Open()
        {
            if (target == "")
                throw new IOException("SetTarget needs to be called first.");

            foreach (string link in links)

                if (Open(link, target))
                    openedLinks.Add(link);
        }


        /**
         * Wrapper around the static Close function. Closes all opened keys in the current container.
         */
        public void Close()
        {
            foreach (String key in openedLinks)
                Close(key, target);
        }

        /**
         * Enforce closing of all keys in the container, independent of whether they were opened by
         * this object.
         */
        public void ForceClose()
        {
            foreach (String key in links)
                Close(key);
        }

        /**
         * Open a registry symbolic link from the specified location to the requested target.
         * If the key location already exists, the user is requested whether it should be deleted.
         * If the key location is already a symbolic link, the link is left untouched.
         *
         * @param from registry key to create the link from
         * @param to target for the symbolic link registry key
         * @return true if the key was created by this function
         */
        public static bool Open(string from, string to)
        {
            SafeRegistryHandle handle = OpenKey(from);

            if (handle == null)
                handle = CreateKey(from);

            else
            {
                String linkPath = GetLinkTarget(handle);

                if (linkPath == null)
                {
                    Console.Write("[!] Registry key {0} does already exist and is not a symlink. Delete it (y/N)? ", from);
                    ConsoleKey response = Console.ReadKey(false).Key;
                    Console.WriteLine();

                    if (response == ConsoleKey.Y)
                    {
                        NtDeleteKey(handle);
                        NtClose(handle);
                        handle = CreateKey(from);
                    }

                    else
                    {
                        Console.WriteLine("[!] Cannot continue without deleting the key.");
                        return false;
                    }
                }

                else
                {
                    if (linkPath == to)
                    {
                        Console.WriteLine("[+] Registry link {0} -> {1} alreday exists.", from, to);
                        return false;
                    }

                    Console.WriteLine("[!] Registry symlink already exists but pointing to {0}", linkPath);
                    Console.WriteLine("[!] They key is treated as open, but may point to an unintended target.");
                    return false;
                }
            }

            UNICODE_STRING value_name = new UNICODE_STRING("SymbolicLinkValue");
            byte[] data = Encoding.Unicode.GetBytes(to);

            Console.WriteLine("Making registry key {0} a symlink poitning to {1}.", from, to);
            int status = NtSetValueKey(handle, value_name, 0, KEY_TYPE_LINK, data, data.Length);
            NtClose(handle);

            if (status != 0)
            {
                throw new IOException("Failure while linking " + from + " to " + to);
            }

            Console.WriteLine("Symlink setup successful!");
            return true;
        }

        /**
         * Close the specified registry key. This function also expects the target of a potential symlink
         * and compares it with the actual target during the delete process. If the targets do not match, the
         * key is not closed.
         *
         * @param key registry key to close
         * @param target expected target of the registry key
         */
        public static void Close(string key, string target)
        {
            SafeRegistryHandle handle = OpenKey(key);

            if (key == null)
                Console.WriteLine("[!] Registry link {0} was already deleted.", key);

            else
            {
                string linkTarget = GetLinkTarget(handle);

                if (linkTarget == null)
                {
                    Console.WriteLine("[!] Registry key {0} is no longer a symlink.", key);
                    Console.WriteLine("[!] Not deleting it.");
                }

                else if (linkTarget != target)
                {
                    Console.WriteLine("[!] Registry key {0} is pointing to an unexpected target: {1}.", key, linkTarget);
                    Console.WriteLine("[!] Not deleting it.");
                }

                else
                    DeleteKey(handle, key);
            }

            NtClose(handle);
        }

        /**
         * A simplified version of the Close function that just removes the specified key without further
         * checks.
         *
         * @param key registry key to close
         */
        public static void Close(string key)
        {
            SafeRegistryHandle handle = OpenKey(key);

            if (key == null)
                Console.WriteLine("[!] Registry link {0} was already deleted.", key);

            else
                DeleteKey(handle, key);

            NtClose(handle);
        }

        /**
         * Delete the specified registry key.
         *
         * @param key registry key to delete
         */
        public static void DeleteKey(SafeRegistryHandle handle)
        {
            DeleteKey(handle, "");
        }

        /**
         * Delete the specified registry key.
         *
         * @param key registry key to delete
         * @param display name of the key
         */
        public static void DeleteKey(SafeRegistryHandle handle, String key)
        {
            int status = NtDeleteKey(handle);

            if (status != 0)
                throw new IOException("Unable to remove registry key " + key);

            Console.WriteLine("[+] Registry key {0} was successfully removed.", key);
        }

        /**
         * Create a new registry key.
         *
         * @param path registry key to create
         * @return SafeRegistryHandle for the created key
         */
        public static SafeRegistryHandle CreateKey(string path)
        {
            OBJECT_ATTRIBUTES obj_attr = new OBJECT_ATTRIBUTES(path, ATTRIBUT_FLAG_CASE_INSENSITIVE);
            int disposition = 0;

            Console.WriteLine("Creating registry key {0}.", path);

            IntPtr handle;
            int status = NtCreateKey(out handle, KEY_ALL_ACCESS, obj_attr, 0, null, REG_OPTION_CREATE_LINK, out disposition);

            if (status == 0)
                return new SafeRegistryHandle(handle, true);

            throw new IOException("Failure while creating registry key " + path);
        }

        /**
         * Open a SafeRegistryHandle for the specified registry path.
         *
         * @param path registry key to open the handle on
         * @return SafeRegistryHandle for the specified key
         */
        public static SafeRegistryHandle OpenKey(string path)
        {
            OBJECT_ATTRIBUTES obj_attr = new OBJECT_ATTRIBUTES(path, ATTRIBUT_FLAG_CASE_INSENSITIVE | ATTRIBUT_FLAG_OBJ_OPENLINK);

            IntPtr handle;
            uint status = NtOpenKeyEx(out handle, KEY_ALL_ACCESS, obj_attr, REG_OPTION_OPEN_LINK);

            if (status == 0)
                return new SafeRegistryHandle(handle, true);

            if (status == 0xC0000034)
                return null;

            throw new IOException("Unable to open registry key " + path);
        }

        /**
         * Return the target of a registry symbolic link.
         *
         * @param handle SafeRegistryHandle of an opened registry key
         * @return symbolic link target or null, if not a symbolic link
         */
        public static string GetLinkTarget(SafeRegistryHandle handle)
        {
            KEY_VALUE_INFORMATION record = new KEY_VALUE_INFORMATION
            {
                TitleIndex = 0,
                Type = 0,
                DataLength = 0,
                Data = new byte[0x400]
            };

            int status;
            int length = 0;
            status = NtQueryValueKey(handle, new UNICODE_STRING("SymbolicLinkValue"), (uint)KEY_VALUE_INFORMATION_CLASS.KeyValuePartialInformation, out record, Marshal.SizeOf(record), out length);

            if (status == 0)
                return System.Text.Encoding.Unicode.GetString(record.Data.Take((int)record.DataLength).ToArray());

            return null;
        }

        /**
         * Translate registry paths to their native format.
         *
         * @param path user specified registry path
         * @return native registry path
         */
        public static string RegPathToNative(string path)
        {
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
