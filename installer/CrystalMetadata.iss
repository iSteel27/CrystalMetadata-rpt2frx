; ===========================================================================================
;  CrystalMetadata - Windows installer (Inno Setup 6)
;
;  Build:  powershell -File installer\build-installer.ps1        (publishes the app, then compiles this script)
;  Output: installer\output\CrystalMetadataSetup-<version>.exe   (ONE file to give to users)
;
;  What it installs (per-user, no administrator rights needed):
;    {app}\CrystalMetadata.Console.exe   the program users start (self-contained: no .NET install needed)
;    {app}\server\                        the internal extractor (.NET Framework 4.8); users never start it
;
;  What it deliberately does NOT install: the SAP Crystal Reports runtime. SAP supplies it under its own licence.
;  The installer checks for it and, if it is missing, explains why and how to get it (system-check page below).
; ===========================================================================================

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "CrystalMetadata"
#define AppExe "rpt2frx.exe"
#define SapRuntimeUrl "https://pages.community.sap.com/topics/crystal-reports/visual-studio"
#define NetFx48Url "https://dotnet.microsoft.com/download/dotnet-framework/net48"

[Setup]
AppId={{6F0B3E5A-6D0F-4B57-9C57-3F2C8A11C0DE}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=CrystalMetadata
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=rpt2frxSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\THIRD-PARTY-NOTICES.txt
MinVersion=10.0
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "addtopath"; Description: "Add rpt2frx to PATH (lets you type ""rpt2frx"" in any new terminal window)"; GroupDescription: "Convenience:"
Name: "rptcontext"; Description: "Add ""Convert with CrystalMetadata"" to the right-click menu of .rpt files"; GroupDescription: "Convenience:"

[Files]
Source: "staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{userdocs}"
Name: "{group}\Check my setup"; Filename: "{app}\{#AppExe}"; Parameters: "doctor --pause"; WorkingDir: "{userdocs}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{userdocs}"; Tasks: desktopicon

[Registry]
; right-click a .rpt file -> convert it (writes <name>.frx next to it, plus a preview) - per user, removed on uninstall
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.rpt\shell\CrystalMetadata.Convert"; ValueType: string; ValueName: ""; ValueData: "Convert with CrystalMetadata"; Flags: uninsdeletekey; Tasks: rptcontext
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.rpt\shell\CrystalMetadata.Convert"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: rptcontext
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.rpt\shell\CrystalMetadata.Convert\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" convert --in ""%1"" --preview --font-scale 0.87 --pause"; Tasks: rptcontext
; per-user PATH (no administrator needed); removed again by CurUninstallStepChanged. Takes effect in NEW terminal windows.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}')); Tasks: addtopath

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "doctor --pause"; Description: "Check my setup now (recommended)"; Flags: postinstall nowait skipifsilent

[Code]
{ ------------------------------------------------------------------------------------------
  System-check page. Runs before anything is copied, lists what is present / missing, and for
  every problem says what it means and what to do. Nothing here blocks the install: the app
  works for converting existing .metadata.json files without the SAP runtime, and its own
  "Check my setup" repeats these checks after installation.
  ------------------------------------------------------------------------------------------ }

var
  CheckPage: TOutputMsgMemoWizardPage;
  SapButton: TNewButton;
  NetFxButton: TNewButton;
  CrystalOk: Boolean;
  NetFxOk: Boolean;

function GacHas(const Gac, Assembly: String): Boolean;
begin
  { the SAP runtime puts its assemblies in the classic GAC (%WINDIR%\assembly) on most machines }
  Result := DirExists(ExpandConstant('{win}\assembly\' + Gac + '\' + Assembly)) or
            DirExists(ExpandConstant('{win}\Microsoft.NET\assembly\' + Gac + '\' + Assembly));
end;

function NetFramework48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

procedure OpenUrl(const Url: String);
var
  ErrorCode: Integer;
begin
  ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure OnSapClick(Sender: TObject);
begin
  OpenUrl('{#SapRuntimeUrl}');
end;

procedure OnNetFxClick(Sender: TObject);
begin
  OpenUrl('{#NetFx48Url}');
end;

function BuildReport: String;
var
  Engine, Ras64, Ras32: Boolean;
  NL: String;
begin
  NL := #13#10;
  Result := '';

  { 1. Windows 64-bit }
  if Is64BitInstallMode then
    Result := Result + '[ OK ]  Windows 64-bit' + NL
  else
    Result := Result + '[FAIL]  This is not 64-bit Windows. CrystalMetadata needs 64-bit Windows 10 or 11.' + NL;

  { 2. .NET Framework 4.8 (used by the part that reads .rpt files) }
  NetFxOk := NetFramework48Installed;
  if NetFxOk then
    Result := Result + '[ OK ]  .NET Framework 4.8' + NL
  else
    Result := Result + '[WARN]  .NET Framework 4.8 was not found.' + NL +
      '           Why it matters: the part that reads .rpt files needs it.' + NL +
      '           What to do: click "Get .NET Framework 4.8" below (free, from Microsoft), or run Windows Update.' + NL +
      '           Windows 10 (May 2019 update or later) and Windows 11 already include it.' + NL;

  { 3. SAP Crystal Reports runtime, 64-bit }
  Engine := GacHas('GAC_MSIL', 'CrystalDecisions.CrystalReports.Engine');
  Ras64 := GacHas('GAC_64', 'CrystalDecisions.ReportAppServer.ClientDoc');
  Ras32 := GacHas('GAC_32', 'CrystalDecisions.ReportAppServer.ClientDoc');
  CrystalOk := Engine and Ras64;

  if CrystalOk then
    Result := Result + '[ OK ]  SAP Crystal Reports runtime (64-bit)' + NL
  else if Ras32 and (not Ras64) then
    Result := Result + '[WARN]  Only the 32-bit SAP Crystal Reports runtime is installed.' + NL +
      '           Why it matters: CrystalMetadata is 64-bit and cannot use the 32-bit runtime.' + NL +
      '           What to do: click "Get SAP runtime" below and install the 64-bit package' + NL +
      '           (CRRuntime_64bit_13_0_xx.msi). It can sit next to the 32-bit one.' + NL
  else
    Result := Result + '[WARN]  The SAP Crystal Reports runtime (64-bit) is not installed.' + NL +
      '           Why it matters: it is what opens .rpt files. It is free but comes from SAP, so it is not' + NL +
      '           included in this installer. Without it you can still convert existing .metadata.json files.' + NL +
      '           What to do: click "Get SAP runtime" below, download the 64-bit package' + NL +
      '           (CRRuntime_64bit_13_0_xx.msi), install it, then run "Check my setup" from the Start menu.' + NL;

  Result := Result + NL;
  if Is64BitInstallMode and NetFxOk and CrystalOk then
    Result := Result + 'Everything CrystalMetadata needs is already on this PC. Click Next to install.'
  else if not Is64BitInstallMode then
    Result := Result + 'This PC cannot run CrystalMetadata (see the first line).'
  else
    Result := Result + 'You can click Next to install now and add the missing items afterwards.' + NL +
      'After installing, "Check my setup" (Start menu) tells you again what is still missing.';
end;

procedure InitializeWizard;
begin
  CheckPage := CreateOutputMsgMemoPage(wpWelcome, 'System check',
    'Checking that this PC has what CrystalMetadata needs',
    'Missing items do not stop the installation. Read the notes below and use the buttons to get them.', '');
  CheckPage.RichEditViewer.Font.Name := 'Consolas';
  CheckPage.RichEditViewer.Font.Size := 9;

  SapButton := TNewButton.Create(WizardForm);
  SapButton.Parent := WizardForm;
  SapButton.Caption := 'Get SAP runtime';
  SapButton.Width := ScaleX(110);
  SapButton.Height := ScaleY(24);
  SapButton.Left := WizardForm.ClientWidth - ScaleX(2) - SapButton.Width - ScaleX(120);
  SapButton.Top := WizardForm.NextButton.Top;
  SapButton.OnClick := @OnSapClick;
  SapButton.Visible := False;

  NetFxButton := TNewButton.Create(WizardForm);
  NetFxButton.Parent := WizardForm;
  NetFxButton.Caption := 'Get .NET Framework 4.8';
  NetFxButton.Width := ScaleX(150);
  NetFxButton.Height := ScaleY(24);
  NetFxButton.Left := ScaleX(8);
  NetFxButton.Top := WizardForm.NextButton.Top;
  NetFxButton.OnClick := @OnNetFxClick;
  NetFxButton.Visible := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = CheckPage.ID then
  begin
    CheckPage.RichEditViewer.Text := BuildReport;
    SapButton.Visible := not CrystalOk;
    NetFxButton.Visible := not NetFxOk;
  end
  else
  begin
    SapButton.Visible := False;
    NetFxButton.Visible := False;
  end;
end;

function NeedsAddPath(Param: String): Boolean;
var
  OrigPath: String;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then
    OrigPath := '';
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  OrigPath, AppDir: String;
  P: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then Exit;
  AppDir := ExpandConstant('{app}');
  P := Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(OrigPath) + ';');
  if P = 0 then Exit;
  { P is 1-based in the string with a leading ';', so the entry starts at P in OrigPath }
  Delete(OrigPath, P, Length(AppDir) + 1);
  if (Length(OrigPath) > 0) and (OrigPath[Length(OrigPath)] = ';') then
    Delete(OrigPath, Length(OrigPath), 1);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', OrigPath);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('CrystalMetadata needs 64-bit Windows 10 or 11.' + #13#10 + 'This PC is running 32-bit Windows, so the installation cannot continue.', mbError, MB_OK);
    Result := False;
  end;
end;
