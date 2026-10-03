# XSR-752: Windows protected update objects

The Windows update platform adapter admits actual opened objects rather than trusting a
Program Files path or an elevated token. It retains the ancestor handles, checks owner/DACL
from each handle, refuses reparse points, and opens subsequent single-component names with
`NtCreateFile.RootDirectory`. No caller-supplied full path is reused for writes or cleanup.

SYSTEM, Administrators and the Windows TrustedInstaller service SID are the allowed owners
and mutation principals. Current-object allow ACEs granting mutation to anyone else fail;
null DACLs and unsupported/conditional ACEs fail. Ancestor creation-only rights do not permit
replacement of an admitted child, while FILE_DELETE_CHILD is always disallowed for untrusted
principals. Fresh staging gets its protected ACL and Administrators owner during creation.
An existing object is never adopted. Read handles and new files retain sharing restrictions.

Tests exercise descriptor policy, ordinary-user tree rejection and real native object reads.
The separate elevated Windows CI test creates only a randomly named staging directory under
Program Files, exercises create-new, handle sharing, parent lifetime and object-bound cleanup,
and removes its fixture. It fails when the required elevated fixture cannot run. Local
unelevated tests do not claim coverage of these writes. NativeAOT executes the same adapter.

This closes the Windows admission/staging primitive only. The privileged helper executable,
signed installation identity, authenticated handoff, transactional replacement/recovery,
Unix adapters and real power-loss acceptance remain open. Automatic updates remain disabled.

Primary API references:
- https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile
- https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo
- https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights
