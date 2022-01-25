### SharpLink Documentation

----

The *SharpLink namespace* currently contains five classes that are described in this document.

* [Symlink](#symlink)
* [RegistryLink](#registrylink)
* [LinkGroup](#linkgroup)
* [Junction](#junction)
* [DosDevice](#dosdevice)


### Symlink

----

An object of type `Symlink` represents a single file system symbolic link and is usually created
by using one of the two available constructors:

```powershell
$s = New-Object de.usd.SharpLink.Symlink("C:\link\path", "C:\target\path")         # Constructor with keepAlive default to false
$s = New-Object de.usd.SharpLink.Symlink("C:\link\path", "C:\target\path", $true)  # Constructor with explicit keepAlive setting
```

By default, `Symlink` objects perform a cleanup operation when they are garbage collected. This
cleanup operation removes the physical file system link and the parent directory, if it was created
while setting up the link. To keep file system links persistent, one can either specify the keep alive
property directly within the constructor or call the `KeepAlive()` method on an existing *Symlink*.

File system symbolic links created by *SharpLink* are not real symbolic links but a clever combination
of *NTFS Junctions* and *Object Manager Symbolic Links*. This technique was first discovered by
[James Forshaw](https://twitter.com/tiraniddo) and implemented within his
[symboliclink-testing-tools](https://github.com/googleprojectzero/symboliclink-testing-tools).
When creating a new `Symlink` object, the required *Junction* and *DosDevice* are not created
right away. The ``Open()`` function needs to be called to open the link. This function performs the following
steps:

  1. It checks whether a suitable *Junction* for the specified link path does already exist
  2. If not the case, it creates the *Junction* and assigns the resulting [Junction](#junction) object to the *Symlink*
  3. It checks whether a suitable *DosDevice* for the specified paths does already exist
  4. If not the case, it creates the *DosDevice* and assigns the resulting [DosDevice](#junction) object to the *Symlink*

As described above, only if a link creates the underlying *Junction* or *DosDevice*, a corresponding object
is assigned to it. This makes the *Symlink* the owner of the corresponding resources and makes it responsible
for managing their lifetime. E.g. only a *Symlink* that owns the underlying *Junction* and *DosDevice* is allowed
to remove them when calling the `Close()` function or when it performs it's cleanup operation. The `ForceClose()`
function can be used to enforce closing, even without ownership.

Apart from using the two constructors mentioned above, it is also possible to create `Symlink` objects by using
the following statically defined functions:

```powershell
$s = [de.usd.SharpLink.Symlink]::FromFile("C:\link\path", "C:\target\path")
$g = [de.usd.SharpLink.Symlink]::FromFolder("C:\folder\path", "C:\target\path")
$g = [de.usd.SharpLink.Symlink]::FromFolderToFolder("C:\folder\path", "C:\target\folder\path")
```

* The ``FromFile(string,string)`` function creates a single `Symlink` object from an already existing file.
* The ``FromFolder(string,string)`` function returns a [LinkGroup](#linkgroup) that contains one `Symlink` for 
  each file contained in the source folder. All of the contained links point to the same specified target.
* The ``FromFolderToFolder(string,string)`` function returns a [LinkGroup](#linkgroup) that contains one `Symlink` for
  each file contained in the source folder. Each of these links points to a file with the link name as file name
  within the target folder.

Status information on the current link status can be obtained from the `Status` method:

```powershell
PS C:\> $s = New-Object de.usd.SharpLink.Symlink("C:\Users\Public\Example\link", "C:\ProgramData\target.txt")
PS C:\> $s.Open()
[+] Creating Junction: C:\Users\Public\Example -> \RPC CONTROL
[+] Creating DosDevice: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink setup successfully.

PS C:\> $s.Status()
[+] Link type: File system symbolic link
[+]     Link path: C:\Users\Public\Example\link
[+]     Target path: C:\ProgramData\target.txt
[+]     Associated Junction: C:\Users\Public\Example
[+]     Associated DosDevice: Global\GLOBALROOT\RPC CONTROL\link

PS C:\> $s.Close()
[+] Removing Junction: C:\Users\Public\Example
[+] Deleting DosDevice: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink deleted.

PS C:\> $s.Status()
[+] Link type: File system symbolic link
[+]     Link path: C:\Users\Public\Example\link
[+]     Target path: C:\ProgramData\target.txt
[+]     Associated Junction: none
[+]     Associated DosDevice: none
```


### RegistryLink

----

An object of type `RegistryLink` represents a single registry link and is usually created
by using one of the two available constructors:

```powershell
$r = New-Object de.usd.SharpLink.RegistryLink("HKCU\LINK", "HKCU\TARGET")         # Constructor with keepAlive defaults to false
$r = New-Object de.usd.SharpLink.RegistryLink("HKCU\LINK", "HKCU\TARGET", $true)  # Constructor with explicit keepAlive setting
```

By default, `RegistryLink` objects perform a cleanup operation when they are garbage collected. This
cleanup operation removes the physical registry link. To keep registry links persistent, one can either
specify the keep alive property directly within the constructor or call the `KeepAlive()` method an an
existing *RegistryLink*.

The physical registry link is not created automatically when creating a `RegistryLink` object. The
`Open()` function needs to be called to open it. The `Open()` function first checks whether a suitable
registry link already exists. Only if this is not the case a link is created and the current `RegistryLink`
instance is marked as it's owner. Ownership makes the *RegistryLink* object responsible for managing
the lifetime of the physical registry link. E.g. only a *RegistryLink* that owns the underlying physical
link can remove it when calling the `Close()` method or when performing it's cleanup operation. The
`ForceClose()` method can be used to enforce removal of the registry link, even without ownership.

To create a registry symbolic link, the invoking user needs to have the `CreateLink` permission on the parent key.
The `CreateSubKey` permission is not sufficient. That being said, a possible workaround is to use `CreateSubKey` to
create a new key within the target. Since this key is owned by the invoking user, creating links in this subkey is
usually possible. To make such operations easier, the `RegistryLink` class exports some static functions:

```csharp
public static bool CreateLink(string key, string target);    // create the specified registry link manually
public static void DeleteLink(string key, string target);    // delete the specified registry link manually
public static void CreateKey(string key);                    // create the specified registry key
public static void DeleteKey(string key);                    // delete the specified registry key
public static string GetLinkTarget(string key);              // return the target name of a registry link
```

Status information on the current link status can be obtained from the `Status()` method:

```powershell
PS C:\> $r = New-Object de.usd.SharpLink.RegistryLink("HKCU\LINK", "HKCU\TARGET")
PS C:\> $r.Open()
[+] Creating registry key: \Registry\User\S-1-5-[...]-1001\LINK
[+] Assigning symlink property poitning to: \Registry\User\S-1-5-[...]-1001\TARGET
[+] RegistryLink setup successful!

PS C:\> $r.Status()
[+] Link Type: Registry symbolic link
[+]     Link key: \Registry\User\S-1-5-[...]-1001\LINK
[+]     Target key: \Registry\User\S-1-5-[...]-1001\TARGET
[+]     Created: True

PS C:\> $r.Close()
[+] Registry key \Registry\User\S-1-5-[...]-1001\LINK was successfully removed.

PS C:\> $r.Status()
[+] Link Type: Registry symbolic link
[+]     Link key: \Registry\User\S-1-5-[...]-1001\LINK
[+]     Target key: \Registry\User\S-1-5-[...]-1001\TARGET
[+]     Created: False
```

Registry symbolic links are limited in their capabilities by the operating system. According to
[abusing-symlinks-on-windows](https://de.slideshare.net/OWASPdelhi/abusing-symlinks-on-windows)
(another great talk by [James Forshaw](https://twitter.com/tiraniddo)), symlinks between untrusted (user)
and trusted (local machine) hives are blocked by the operating system since *Windows 7*. Using *SharpLink*,
it is possible to confirm this:

```powershell
PS C:\> $r = New-Object de.usd.SharpLink.RegistryLink("HKCU\LINK", "HKLM\SOFTWARE\TARGET")
PS C:\> $r.Open()
[+] Creating registry key: \Registry\User\S-1-5-[...]-1001\LINK
[+] Assigning symlink property poitning to: \Registry\Machine\SOFTWARE\TARGET
[+] RegistryLink setup successful!
PS C:\> reg query HKCU\LINK
ERROR: Access is denied.
```

However, it is worth noting that registry symbolic links pointing from a trusted into an untrusted
hive still work:

```powershell
PS C:\> $r = New-Object de.usd.SharpLink.RegistryLink("HKLM\SOFTWARE\TARGET\LINK", "HKCU\Volatile Environment")
PS C:\> $r.Open()
[+] Creating registry key: \Registry\Machine\SOFTWARE\TARGET\LINK
[+] Assigning symlink property poitning to: \Registry\User\S-1-5-[...]-1001\Volatile Environment
[+] RegistryLink setup successful!
PS C:\> reg query HKLM\SOFTWARE\TARGET\LINK /v HOMEDRIVE

HKEY_LOCAL_MACHINE\SOFTWARE\TARGET\LINK
    HOMEDRIVE    REG_SZ    C:\
```


### LinkGroup

----

A `LinkGroup` can be used to bundle multiple `Symlink` and `RegistryLink` objects into a single object
that allows to perform compound operations on them. This is useful when you have to work with several
links at the same time and want to simultaneously open or close them. The following listings shows an
usage example:

```powershell
PS C:\> $g = New-Object de.usd.SharpLink.LinkGroup
PS C:\> $g.AddSymlink("C:\Users\Public\Example\link", "C:\ProgramData\target.txt")
PS C:\> $g.AddSymlink("C:\Users\Public\Example\link2", "C:\ProgramData\target2.txt")
PS C:\> $g.AddRegistryLink("HKCU\LINK", "HKCU\TARGET")
PS C:\> $g.Open()
[+] Creating Junction: C:\Users\Public\Example -> \RPC CONTROL
[+] Creating DosDevice: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink setup successfully.
[+] Junction C:\Users\Public\Example -> \RPC CONTROL does already exist.
[+] Creating DosDevice: Global\GLOBALROOT\RPC CONTROL\link2 -> \??\C:\ProgramData\target2.txt
[+] Symlink setup successfully.
[+] Creating registry key: \Registry\User\S-1-5-[...]-1001\LINK
[+] Assigning symlink property poitning to: \Registry\User\S-1-5-[...]-1001\TARGET
[+] RegistryLink setup successful!

PS C:\> $g.Status()
[+] LinkGroup contains 3 link(s):
[+]
[+] Link type: File system symbolic link
[+]     Link path: C:\Users\Public\Example\link
[+]     Target path: C:\ProgramData\target.txt
[+]     Associated Junction: C:\Users\Public\Example
[+]     Associated DosDevice: Global\GLOBALROOT\RPC CONTROL\link
[+]
[+] Link type: File system symbolic link
[+]     Link path: C:\Users\Public\Example\link2
[+]     Target path: C:\ProgramData\target2.txt
[+]     Associated Junction: none
[+]     Associated DosDevice: Global\GLOBALROOT\RPC CONTROL\link2
[+]
[+] Link Type: Registry symbolic link
[+]     Link key: \Registry\User\S-1-5-[...]-1001\LINK
[+]     Target key: \Registry\User\S-1-5-[...]-1001\TARGET
[+]     Created: True

PS C:\> $g.Close()
[+] Removing Junction: C:\Users\Public\Example
[+] Deleting DosDevice: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink deleted.
[+] Deleting DosDevice: Global\GLOBALROOT\RPC CONTROL\link2 -> \??\C:\ProgramData\target2.txt
[+] Symlink deleted.
[+] Registry key \Registry\User\S-1-5-[...]-1001\LINK was successfully removed.
```


### Junction

----

The `Junction` class is used internally by *SharpLink* to create the required *Junctions* for *Symlink* creation.
Despite it is not recommended to use static functions from the `Junction` class directly, they are still exported and can
be consumed from *Powershell*. The following functions are available:

```csharp
public static Junction Create(string baseDir, string targetDir, bool keepAlive);    // Create a new Junction
public static void Close(string baseDir, string targetDir);                         // Close an existing Junction
public static void Close(string baseDir);                                           // Close an existing Junction
public static string GetTarget(string baseDir);                                     // Get the target path of an existing Junction
```

`Junction` objects are treated as resources and perform a cleanup operation when they go out of scope.
This cleanup operation removes the underlying physical Junction. If cleanup is not desired, the `keepAlive`
property should be set to true during creation. It is also possible to call the `KeepAlive()` function
on an already existing `Junction`.


### DosDevice

----

The `DosDevice` class is used internally by *SharpLink* to create the required *DosDevice* for *Symlink* creation.
Despite it is not recommended to use static functions from the `DosDevice` class directly, they are still exported and can
be consumed from *Powershell*. The following functions are available:

```csharp
public static DosDevice Create(string name, string target, bool keepAlive);   // Create a new DosDevice
public static void Close(string name, string target);                         // Close an existing DosDevice
public static void Close(string name);                                        // Close an existing DosDevice
public static string GetTarget(string name);                                  // Get the target path of an existing DosDevice
```

`DosDevice` objects are treated as resources and perform a cleanup operation when they go out of scope.
This cleanup operation removes the underlying physical device. If cleanup is not desired, the `keepAlive`
property should be set to true during creation. It is also possible to call the `KeepAlive()` function
on an already existing `DosDevice`.
