#ifndef Payload
  #error Payload is required
#endif
[Setup]
AppId={{50AE2197-7C7B-43DA-BE98-1CDEBE86B273}
AppName=NexaCL
AppVersion={#ProductVersion}
VersionInfoVersion={#NumericVersion}.0
DefaultDirName={autopf}\NexaCL
DefaultGroupName=NexaCL
UsePreviousAppDir=no
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed={#InstallArch}
ArchitecturesInstallIn64BitMode={#InstallArch}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile={#IconPath}
UninstallDisplayIcon={app}\Nexa.Desktop.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{commonprograms}\NexaCL"; Filename: "{app}\Nexa.Desktop.exe"; WorkingDir: "{app}"
Name: "{commondesktop}\NexaCL"; Filename: "{app}\Nexa.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
// The pinned Inno 6.4 compiler emits a 32-bit Setup process on both Windows RIDs.
// Set the token default owner before creating files, rather than repairing their
// permissions after copy while the installation account can still modify them.
function NexaGetCurrentProcess: THandle;
  external 'GetCurrentProcess@kernel32.dll stdcall';
function NexaOpenProcessToken(Process: THandle; Access: LongWord; var Token: THandle): Integer;
  external 'OpenProcessToken@advapi32.dll stdcall';
function NexaConvertStringSidToSid(SidText: String; var Sid: LongWord): Integer;
  external 'ConvertStringSidToSidW@advapi32.dll stdcall';
function NexaSetTokenOwner(Token: THandle; Kind: Integer; var Owner: LongWord; Length: LongWord): Integer;
  external 'SetTokenInformation@advapi32.dll stdcall';
function NexaLocalFree(Address: LongWord): LongWord;
  external 'LocalFree@kernel32.dll stdcall';
function NexaCloseHandle(Handle: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

function InitializeSetup: Boolean;
var
  Token: THandle;
  Owner: LongWord;
begin
  Result := False;
  Token := 0;
  Owner := 0;
  try
    // TOKEN_ADJUST_DEFAULT; TokenOwner contains one pointer-sized SID field.
    if NexaOpenProcessToken(NexaGetCurrentProcess, $80, Token) <> 0 then
      if NexaConvertStringSidToSid('S-1-5-32-544', Owner) <> 0 then
        Result := NexaSetTokenOwner(Token, 4, Owner, 4) <> 0;
  finally
    if Owner <> 0 then NexaLocalFree(Owner);
    if Token <> 0 then NexaCloseHandle(Token);
  end;
  if not Result then
    RaiseException('Cannot establish administrator-owned system installation.');
end;
