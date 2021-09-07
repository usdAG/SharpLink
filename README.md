### SharpLink

----

The *SharpLink* repository contains currently a single *C#* class that can be used to
create file system symbolic links from a low privileged user account. The class uses
a technique that was first identified by [James Forshaw](https://twitter.com/tiraniddo)
and implemented within the [symboliclink-testing-tools](https://github.com/googleprojectzero/symboliclink-testing-tools).
The ``usd.Symlink`` type defined in this repository allows you to create symlinks directly
from *PowerShell* in an object oriented fashion.

In future, more link related classes and functionalities may be added. This is why we have
chosen to store the ``usd.Symlink`` class in an repository instead of a *Gist*.


### Usage

----

The ``usd.Symlink`` type is intended to be used from *PowerShell*:

```powershell
PS C:\> $type = @"
<CODE>
"@
PS C:\> Add-Type $type
                                                                                                 
PS C:\> $s = New-Object usd.Symlink("C:\ProgramData\target.txt", "C:\Users\Public\example\link")
PS C:\> $s.Open()
[+] Creating Junction: C:\Users\Public\example -> \RPC CONTROL
[+] Creating Symlink: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink was setup successfully.
                                                                                                 
PS C:\> echo test > C:\Users\Public\example\link
PS C:\> type C:\ProgramData\target.txt
test
                                                                                                 
PS C:\> $s.Close()
[+] Removing Junction: C:\Users\Public\example -> \RPC CONTROL
[+] Deleting Symlink: Global\GLOBALROOT\RPC CONTROL\link -> \??\C:\ProgramData\target.txt
[+] Symlink deleted.
```


### Acknowledgements and References

----

*SharpLink* ports some of the symlink functionalities from the *symboliclink-testing-tools* to *C#*.
The general idea as well as the required Win32 API calls were taken from the *symboliclink-testing-tools*
repository. Furthermore, the *C#* code for creating and deleting the junctions was basically copied from
the below mentioned reference.

* [Create, Delete and Examine Junction Points in C#](https://gist.github.com/LGM-AdrianHum/260bc9ab3c4cd49bc8617a2abe84ca74)
* [Abusing Symlinks on Windows](https://de.slideshare.net/OWASPdelhi/abusing-symlinks-on-windows)
* [symboliclink-testing-tools](https://github.com/googleprojectzero/symboliclink-testing-tools)
