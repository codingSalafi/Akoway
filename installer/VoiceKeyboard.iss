; ─────────────────────────────────────────────────────────────────────────────
;  Voice Keyboard  –  Inno Setup installer script
;
;  Prerequisites:
;    1. dotnet publish  (see Build step below)
;    2. Inno Setup 6   https://jrsoftware.org/isdl.php
;
;  Build step — run this in the repo root (PowerShell / cmd):
;
;    dotnet publish VoiceKeyboard\VoiceKeyboard\VoiceKeyboard.csproj ^
;      -c Release -r win-x64 --self-contained true ^
;      -p:PublishReadyToRun=true ^
;      -o publish
;
;  Then open this .iss in Inno Setup and press Build → Compile.
;  The finished installer lands in installer-output\VoiceKeyboard-Setup-v1.0.0.exe
; ─────────────────────────────────────────────────────────────────────────────

#define MyAppName      "Akoway"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "Akoway"
#define MyAppURL       "https://github.com/your-username/akoway"
#define MyAppExeName   "Akoway.exe"
#define MyPublishDir   "C:\Users\Dell\Desktop\VoiceKeyboard-publish"

; ── Setup metadata ────────────────────────────────────────────────────────────
[Setup]
AppId={{F3A2B1C0-D4E5-4F67-89AB-CDEF01234567}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Install to  C:\Program Files\Akoway
DefaultDirName={autopf}\Akoway
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output
OutputDir=C:\Users\Dell\Desktop
OutputBaseFilename=Akoway-Setup-v{#MyAppVersion}
SetupIconFile=C:\Users\Dell\Desktop\akoway.ico

; Compression  (lzma2/ultra64 gives best ratio – slow to compress, fast to extract)
Compression=lzma2/ultra64
SolidCompression=yes

; Appearance
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; Require 64-bit Windows
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Admin rights required to write to Program Files
PrivilegesRequired=admin

; Offer to close running instance before install
CloseApplications=yes
CloseApplicationsFilter=*{#MyAppExeName}*
RestartApplications=no

; ── Languages ─────────────────────────────────────────────────────────────────
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ── Optional tasks shown on the installer page ────────────────────────────────
[Tasks]
Name: "desktopicon"; \
  Description: "Create a &desktop shortcut"; \
  GroupDescription: "Additional icons:"; \
  Flags: unchecked

; ── Files ─────────────────────────────────────────────────────────────────────
[Files]
; Copy every file from the dotnet publish output folder
Source: "{#MyPublishDir}\*"; \
  DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; ── Start Menu / Desktop shortcuts ───────────────────────────────────────────
[Icons]
Name: "{group}\{#MyAppName}";          Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; \
  Tasks: desktopicon

; ── Run the app when installer finishes ──────────────────────────────────────
[Run]
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent

; ── Clean up AppData models folder on uninstall (optional prompt) ─────────────
[UninstallDelete]
; Leave  %AppData%\VoiceKeyboard  intact by default so the user keeps
; downloaded models.  Uncomment the line below to delete them on uninstall.
; Type: filesandordirs; Name: "{userappdata}\VoiceKeyboard"
