#define MyAppName      "Fuse"
#define MyAppPublisher "A. Shafie"
#define MyAppURL       "https://github.com/litenova/Fuse"
#define MyAppExeName   "fuse.exe"

; Version is injected at build time via /DMyAppVersion=x.y.z
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{A3F1E2D4-7C5B-4A9E-B8D6-2F0C3E1A4B7D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; User-mode install — no admin rights required
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

OutputDir=..\publish\installer
OutputBaseFilename=fuse-{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Sign the installer if a certificate is available (optional)
; SignTool=...

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\win-x64\fuse.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; No start menu shortcut needed for a CLI tool

[Registry]
; Add install dir to user PATH
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
// Returns true if Value is not already present in the semicolon-delimited Path string.
function NeedsAddPath(Value: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Value) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

// Broadcast WM_SETTINGCHANGE so the new PATH takes effect without a reboot.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: DWORD;
begin
  if CurStep = ssPostInstall then
    SendBroadcastMessage($001A, 0, 'Environment');
end;

// Remove install dir from PATH on uninstall.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  OldPath, NewPath, Entry: string;
  Parts: TStringList;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OldPath) then exit;

  Entry := ExpandConstant('{app}');
  Parts := TStringList.Create;
  try
    Parts.Delimiter := ';';
    Parts.StrictDelimiter := True;
    Parts.DelimitedText := OldPath;
    for I := Parts.Count - 1 downto 0 do
      if SameText(Parts[I], Entry) then Parts.Delete(I);
    NewPath := Parts.DelimitedText;
  finally
    Parts.Free;
  end;

  RegWriteStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', NewPath);
  SendBroadcastMessage($001A, 0, 'Environment');
end;
