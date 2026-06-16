; ChoreBuddy Agent — Inno Setup script
; Produces ChoreBuddyAgentSetup.exe — a single-file installer that
; drops the agent into Program Files, registers the Windows Service,
; runs the setup wizard for sign-in, and offers an uninstaller.

#define MyAppName "ChoreBuddy Agent"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "ChoreBuddy"
#define MyAppExeName "ChoreBuddy.TestApp.exe"
#define SourceDir "..\dist-1.0.5"

[Setup]
AppId={{8B2A6E3F-3F4C-4E29-8F8C-7B5D7C9E3F11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ChoreBuddy
DefaultGroupName=ChoreBuddy
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=ChoreBuddyAgentSetup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
; Everything from the published agent — exe + DLLs + wizard-ui folder.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ChoreBuddy Agent (Reconfigure)"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--setup"
Name: "{group}\Uninstall ChoreBuddy Agent"; Filename: "{uninstallexe}"

[Run]
; Launch the wizard immediately so the user signs in + picks the kid as
; part of the install flow. nowait so the installer can finish cleanly.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--setup"; Description: "Run setup wizard"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Tear down everything the wizard installed before removing files.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""try {{ Unregister-ScheduledTask -TaskName ChoreBuddyAgentWatchdog -Confirm:$false -ErrorAction SilentlyContinue }} catch {{}}"""; Flags: runhidden
Filename: "sc.exe"; Parameters: "stop ChoreBuddyAgent"; Flags: runhidden
Filename: "sc.exe"; Parameters: "delete ChoreBuddyAgent"; Flags: runhidden
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-ChildItem 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options' | Where-Object {{ (Get-ItemProperty $_.PsPath -EA SilentlyContinue).ChoreBuddyManaged -eq 1 }} | ForEach-Object {{ Remove-Item $_.PsPath -Recurse -Force }}"""; Flags: runhidden
Filename: "reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ChoreBuddyOverlay /f"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\ChoreBuddy"
