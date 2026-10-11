#define BbaAppVersion GetEnv("BBA_APP_VERSION")
#define BbaPublishDir GetEnv("BBA_RELEASE_PUBLISH_DIR")
#define BbaOutputDir GetEnv("BBA_RELEASE_OUTPUT_DIR")

#if BbaAppVersion == ""
  #error "BBA_APP_VERSION must be set by scripts/Publish-Release.ps1"
#endif
#if BbaPublishDir == ""
  #error "BBA_RELEASE_PUBLISH_DIR must be set by scripts/Publish-Release.ps1"
#endif
#if BbaOutputDir == ""
  #error "BBA_RELEASE_OUTPUT_DIR must be set by scripts/Publish-Release.ps1"
#endif

#define BbaAppName "Basit Bilgisayar Araçları"
#define BbaAppExe "BasitBilgisayarAraclari.App.exe"

[Setup]
AppId={{0E2D419A-35EF-4DEB-B1F0-9F42AC77672C}
AppName={#BbaAppName}
AppVersion={#BbaAppVersion}
AppVerName={#BbaAppName} {#BbaAppVersion}
DefaultDirName={autopf}\Basit Bilgisayar Araçları
DefaultGroupName={#BbaAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=yes
UninstallDisplayName={#BbaAppName}
UninstallDisplayIcon={app}\{#BbaAppExe}
OutputDir={#BbaOutputDir}
OutputBaseFilename=BasitBilgisayarAraclari-{#BbaAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
SetupLogging=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UsePreviousLanguage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
english.ParanoidPageCaption=One question
english.ParanoidPageDescription=Are you paranoid?
english.ParanoidPageSubCaption=Choose one answer, then confirm. The terminal effect only displays fake text and runs no system commands. Another fixed drive letter is used when available; otherwise the standard location is used.
english.ParanoidYes=Yes, I am paranoid
english.ParanoidNo=No, I am not paranoid
english.ChooseAnswer=Choose one of the two answers to continue.
english.ConfirmChoice=Confirm
english.DesktopIcon=Create a desktop shortcut
english.AdditionalTasks=Additional options:
english.InstallFinished=Installation is complete.

turkish.ParanoidPageCaption=Bir soru
turkish.ParanoidPageDescription=Paranoyak mısın?
turkish.ParanoidPageSubCaption=Bir seçeneği işaretle ve onayla. Terminal efekti yalnızca sahte yazı gösterir; sistem komutu çalıştırmaz. Varsa C dışındaki sabit sürücü harfi kullanılır; yoksa standart konum seçilir.
turkish.ParanoidYes=Evet, ben bir paronayağım
turkish.ParanoidNo=Hayır, ben bir paronayak değilim
turkish.ChooseAnswer=Devam etmek için iki seçenekten birini işaretle.
turkish.ConfirmChoice=Onayla
turkish.DesktopIcon=Masaüstü kısayolu oluştur
turkish.AdditionalTasks=Ek seçenekler:
turkish.InstallFinished=Kurulum tamamlandı.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

[Files]
Source: "PrankTerminals\Terminal01.cmd"; Flags: dontcopy
Source: "PrankTerminals\Terminal02.cmd"; Flags: dontcopy
Source: "PrankTerminals\Terminal03.cmd"; Flags: dontcopy
Source: "PrankTerminals\Terminal04.cmd"; Flags: dontcopy
Source: "PrankTerminals\Terminal05.cmd"; Flags: dontcopy
Source: "PrankTerminals\Terminal06.cmd"; Flags: dontcopy
Source: "{#BbaPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#BbaAppName}"; Filename: "{app}\{#BbaAppExe}"
Name: "{autodesktop}\{#BbaAppName}"; Filename: "{app}\{#BbaAppExe}"; Check: IsParanoidInstall
Name: "{autodesktop}\{#BbaAppName}"; Filename: "{app}\{#BbaAppExe}"; Tasks: desktopicon; Check: IsRegularInstall

[Code]
const
  DriveTypeFixed = 3;

var
  ParanoidPage: TInputOptionWizardPage;
  ParanoidInstall: Boolean;
  InitialInstallDir: String;
  PseudoRandomState: Integer;

function GetLogicalDrives: LongWord;
  external 'GetLogicalDrives@kernel32.dll stdcall';

function GetDriveType(RootPathName: String): LongWord;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function GetTickCount: LongWord;
  external 'GetTickCount@kernel32.dll stdcall';

procedure Pause(Milliseconds: LongWord);
  external 'Sleep@kernel32.dll stdcall';

function NextRandom(Range: Integer): Integer;
begin
  PseudoRandomState := (PseudoRandomState * 25173 + 13849) mod 65536;
  Result := PseudoRandomState mod Range;
end;

function IsParanoidInstall: Boolean;
begin
  Result := ParanoidInstall;
end;

function IsRegularInstall: Boolean;
begin
  Result := not ParanoidInstall;
end;

function FindOtherFixedDrive(var SelectedRoot: String): Boolean;
var
  DriveMask: LongWord;
  DriveIndex: Integer;
  CandidateCount: Integer;
  CandidateRoot: String;
  WindowsRoot: String;
begin
  Result := False;
  SelectedRoot := '';
  CandidateCount := 0;
  DriveMask := GetLogicalDrives;
  WindowsRoot := Uppercase(Copy(ExpandConstant('{win}'), 1, 3));

  for DriveIndex := 0 to 25 do
  begin
    if (DriveMask and (1 shl DriveIndex)) <> 0 then
    begin
      CandidateRoot := Chr(Ord('A') + DriveIndex) + ':\';
      if (Uppercase(CandidateRoot) <> 'C:\') and
         (Uppercase(CandidateRoot) <> WindowsRoot) and
         (GetDriveType(CandidateRoot) = DriveTypeFixed) then
      begin
        CandidateCount := CandidateCount + 1;
        if NextRandom(CandidateCount) = 0 then
          SelectedRoot := CandidateRoot;
      end;
    end;
  end;

  Result := SelectedRoot <> '';
end;

function AddTrailingBackslash(Path: String): String;
begin
  Result := Path;
  if (Result <> '') and (Result[Length(Result)] <> '\') then
    Result := Result + '\';
end;

function ChooseParanoidInstallDir: String;
var
  DriveRoot: String;
  Candidate: String;
  Attempts: Integer;
begin
  if FindOtherFixedDrive(DriveRoot) then
  begin
    for Attempts := 1 to 100 do
    begin
      Candidate := AddTrailingBackslash(DriveRoot) +
        'BBA_' + IntToStr(10000000 + NextRandom(90000000));
      if not FileOrDirExists(Candidate) then
      begin
        Result := Candidate;
        exit;
      end;
    end;
  end;

  Result := ExpandConstant('{autopf}\Basit Bilgisayar Araçları');
end;

procedure InitializeWizard;
begin
  PseudoRandomState := Integer(GetTickCount mod 65536);
  if PseudoRandomState = 0 then
    PseudoRandomState := 1;
  ParanoidInstall := False;
  InitialInstallDir := WizardDirValue;

  ParanoidPage := CreateInputOptionPage(
    wpWelcome,
    CustomMessage('ParanoidPageCaption'),
    CustomMessage('ParanoidPageDescription'),
    CustomMessage('ParanoidPageSubCaption'),
    True,
    False);
  ParanoidPage.Add(CustomMessage('ParanoidYes'));
  ParanoidPage.Add(CustomMessage('ParanoidNo'));
  ParanoidPage.SelectedValueIndex := -1;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = ParanoidPage.ID then
    WizardForm.NextButton.Caption := CustomMessage('ConfirmChoice')
  else
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = ParanoidPage.ID then
  begin
    if ParanoidPage.SelectedValueIndex < 0 then
    begin
      MsgBox(CustomMessage('ChooseAnswer'), mbInformation, MB_OK);
      Result := False;
      exit;
    end;

    ParanoidInstall := ParanoidPage.SelectedValueIndex = 0;
    if ParanoidInstall then
      WizardForm.DirEdit.Text := ChooseParanoidInstallDir
    else
      WizardForm.DirEdit.Text := InitialInstallDir;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ParanoidInstall and
    ((PageID = wpSelectDir) or (PageID = wpSelectTasks) or (PageID = wpReady));
end;

procedure LaunchTerminal(TerminalIndex: Integer);
var
  FileName: String;
  TerminalPath: String;
  ResultCode: Integer;
begin
  case TerminalIndex of
    0: FileName := 'Terminal01.cmd';
    1: FileName := 'Terminal02.cmd';
    2: FileName := 'Terminal03.cmd';
    3: FileName := 'Terminal04.cmd';
    4: FileName := 'Terminal05.cmd';
  else
    FileName := 'Terminal06.cmd';
  end;

  TerminalPath := ExpandConstant('{tmp}\') + FileName;
  if Exec(
    ExpandConstant('{sys}\cmd.exe'),
    '/d /c ""' + TerminalPath + '""',
    ExpandConstant('{tmp}'),
    SW_SHOWNORMAL,
    ewNoWait,
    ResultCode) then
    Log('Started the harmless fake-terminal visual effect: ' + FileName)
  else
    Log('Could not start fake-terminal visual effect: ' + SysErrorMessage(ResultCode));
end;

procedure LaunchParanoidTerminals;
var
  Order: array[0..5] of Integer;
  I: Integer;
  J: Integer;
  TemporaryIndex: Integer;
  TerminalCount: Integer;
begin
  ExtractTemporaryFiles('Terminal*.cmd');
  for I := 0 to 5 do
    Order[I] := I;

  for I := 5 downto 1 do
  begin
    J := NextRandom(I + 1);
    TemporaryIndex := Order[I];
    Order[I] := Order[J];
    Order[J] := TemporaryIndex;
  end;

  TerminalCount := 4 + NextRandom(3);
  for I := 0 to TerminalCount - 1 do
  begin
    LaunchTerminal(Order[I]);
    if I < TerminalCount - 1 then
      Pause(300 + NextRandom(500));
  end;
end;

procedure LaunchOneFakeTerminal;
begin
  ExtractTemporaryFiles('Terminal*.cmd');
  LaunchTerminal(NextRandom(6));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and ParanoidInstall then
    LaunchParanoidTerminals
  else if (CurStep = ssPostInstall) and not ParanoidInstall then
    LaunchOneFakeTerminal;
end;
