### SharpLink

----

*SharpLink* is a *C#* namespace containing classes that allow low privileged user accounts
to create file system and registry symbolic links on Windows operating systems. It uses
a technique that was first identified by [James Forshaw](https://twitter.com/tiraniddo)
and implemented within the [symboliclink-testing-tools](https://github.com/googleprojectzero/symboliclink-testing-tools).
*SharpLink* provides a *C#* implementation and allows to create symbolic links directly
from *PowerShell* in an object oriented fashion.


### Usage

----

The following listings show short usage examples of *SharpLink*. More details can be found
within the [docs folder](/docs).


#### File System

```console
PS C:\> $code = (iwr https://raw.githubusercontent.com/usdAG/SharpLink/main/SharpLink.cs).content
PS C:\> Add-Type $code
                                                                                                 
PS C:\> $s = New-Object de.usd.SharpLink.Symlink("C:\Users\Public\Example\link", "C:\ProgramData\target.txt")
PS C:\> $s.Open()
[+] Creating Junction: C:\Users\Public\Example -> \RPC CONTROL
[+] Creating DosDevice: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink setup successfully.

PS C:\> echo "Hello World :D" > C:\Users\Public\Example\link
PS C:\> type C:\ProgramData\target.txt
Hello World :D

PS C:\> $s.Close()
[+] Removing Junction: C:\Users\Public\Example
[+] Deleting DosDevice: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink deleted.
```


#### Windows Registry

```console
PS C:\> $code = (iwr https://raw.githubusercontent.com/usdAG/SharpLink/main/SharpLink.cs).content
PS C:\> Add-Type $code

PS C:\> $r = New-Object de.usd.SharpLink.RegistryLink("HKCU\link", "HKCU\Volatile Environment")
PS C:\> $r.Open()
[+] Creating registry key: \Registry\User\S-1-5-[...]-1001\link
[+] Assigning symlink property pointing to: \Registry\User\S-1-5-[...]-1001\Volatile Environment
[+] Symlink setup successful!

PS C:\> reg query HKCU\link /v HOMEDRIVE

HKEY_CURRENT_USER\link
    HOMEDRIVE    REG_SZ    C:

PS C:\> $r.Close()
[+] Registry key \Registry\User\S-1-5-[...]-1001\link was successfully removed.
```


#### LinkGroups

```console
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

### Acknowledgements and References

----

*SharpLink* ports some of the symlink functionalities from the *symboliclink-testing-tools* to *C#*.
The general idea as well as the required *Win32 API* calls were taken from the *symboliclink-testing-tools*
repository. Furthermore, the *C#* code for creating and deleting junctions and for working with the Windows
registry was basically copied from the below mentioned reference.

* [Abusing Symlinks on Windows](https://de.slideshare.net/OWASPdelhi/abusing-symlinks-on-windows)
* [Create, Delete and Examine Junction Points in C#](https://gist.github.com/LGM-AdrianHum/260bc9ab3c4cd49bc8617a2abe84ca74)
* [symboliclink-testing-tools](https://github.com/googleprojectzero/symboliclink-testing-tools)
* [Windows Registry Stuff](https://www.exploit-db.com/exploits/40573)
