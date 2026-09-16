#define MyAppName "DDO Studio"
#define MyAppVersion "1.7.2"
#define MyAppExeName "DDOStudio.exe"
#define MyAppPublisher "DDO Studio"

[Setup]
; Keep this AppId stable forever. Inno uses it to recognize older versions as
; the same application, which enables in-place updates instead of side-by-side installs.
AppId={{A7B9E15E-64D9-4DF5-8C23-7D8CB91E31AD}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion=1.7.2
VersionInfoDescription=DDO Studio Setup
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion=1.7.2

DefaultDirName={autopf}\DDO Studio
DefaultGroupName=DDO Studio
DisableDirPage=auto
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
AllowNoIcons=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

OutputDir=..\release\installer
OutputBaseFilename=DDOStudio-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardResizable=no
SetupIconFile=assets\DDOAssetStudio.ico
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageStretch=yes

CloseApplications=yes
RestartApplications=no
UninstallDisplayName=DDO Studio
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupWindowTitle=DDO Studio {#MyAppVersion} Setup
WelcomeLabel1=Welcome to DDO Studio
WelcomeLabel2=Browse, inspect, preview, and export local Dungeons & Dragons Online assets.%n%nSetup can also update an existing DDO Studio installation in place.
FinishedHeadingLabel=DDO Studio is ready
FinishedLabel=Setup has finished installing DDO Studio on your computer.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: checkedonce

[InstallDelete]
; Clean replaceable application components during an upgrade so obsolete files
; from older builds cannot remain behind. User settings/cache live in LocalAppData
; and are deliberately untouched.
Type: filesandordirs; Name: "{app}\viewer"
Type: filesandordirs; Name: "{app}\backend"
Type: filesandordirs; Name: "{app}\exporter"

[Files]
Source: "..\release\portable\DDO Studio\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\release\prereqs\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\DDO Studio"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\DDO Studio"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Preparing the embedded 3D viewer…"; Flags: waituntilterminated runhidden; Check: NeedsWebView2
Filename: "{app}\{#MyAppExeName}"; Description: "Launch DDO Studio"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\viewer"
Type: filesandordirs; Name: "{app}\backend"
Type: filesandordirs; Name: "{app}\exporter"
Type: filesandordirs; Name: "{app}"

[Code]
var
  PreviousVersion: string;
  UpdatePage: TOutputMsgWizardPage;
  IsUpdating: Boolean;

function ReadInstalledVersion(var Version: string): Boolean;
var
  Key: string;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7B9E15E-64D9-4DF5-8C23-7D8CB91E31AD}_is1';
  Result := RegQueryStringValue(HKLM, Key, 'DisplayVersion', Version);
  if not Result then
    Result := RegQueryStringValue(HKCU, Key, 'DisplayVersion', Version);
end;

function WebView2RuntimeExists: Boolean;
var
  Root, AppDir: string;
  FindRec: TFindRec;
begin
  Result := False;

  Root := ExpandConstant('{pf32}\Microsoft\EdgeWebView\Application');
  if DirExists(Root) and FindFirst(Root + '\*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          AppDir := Root + '\' + FindRec.Name;
          if FileExists(AppDir + '\msedgewebview2.exe') then
          begin
            Result := True;
            Exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  Root := ExpandConstant('{pf}\Microsoft\EdgeWebView\Application');
  if DirExists(Root) and FindFirst(Root + '\*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          AppDir := Root + '\' + FindRec.Name;
          if FileExists(AppDir + '\msedgewebview2.exe') then
          begin
            Result := True;
            Exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function NeedsWebView2: Boolean;
begin
  Result := not WebView2RuntimeExists;
end;

procedure InitializeWizard;
begin
  IsUpdating := ReadInstalledVersion(PreviousVersion);

  { A little restrained branding without replacing standard Windows controls. }
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.WelcomeLabel1.Font.Size := 16;
  WizardForm.FinishedHeadingLabel.Font.Style := [fsBold];

  if IsUpdating then
  begin
    UpdatePage := CreateOutputMsgPage(
      wpWelcome,
      'Existing installation detected',
      'Update DDO Studio',
      'DDO Studio ' + PreviousVersion + ' is already installed.' + #13#10 + #13#10 +
      'Setup will update it in place to version {#MyAppVersion}. Your DDO path, settings, preview cache, and exported GLB files are kept.' + #13#10 + #13#10 +
      'You do not need to uninstall the current version first.');
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if IsUpdating and Assigned(UpdatePage) and (CurPageID = UpdatePage.ID) then
    WizardForm.NextButton.Caption := 'Update >'
  else
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserData: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    UserData := ExpandConstant('{localappdata}\DDO Asset Studio');
    if DirExists(UserData) then
    begin
      if MsgBox(
        'Remove DDO Studio settings and preview cache too?' + #13#10 + #13#10 +
        'Your exported GLB files are not stored here and will not be deleted.',
        mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(UserData, True, True, True);
      end;
    end;
  end;
end;
