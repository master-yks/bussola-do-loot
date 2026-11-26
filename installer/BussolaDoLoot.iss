#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\portable"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "BussolaDoLoot-Setup-win-x64"
#endif

[Setup]
AppId={{8E5F3A21-6C4D-4B9E-9A12-4F7D3E8C1B50}
AppName=Bússola do Loot
AppVersion={#MyAppVersion}
AppVerName=Bússola do Loot {#MyAppVersion}
AppPublisher=master-yks
AppPublisherURL=https://github.com/master-yks/bussola-do-loot
AppSupportURL=https://github.com/master-yks/bussola-do-loot/issues
AppUpdatesURL=https://github.com/master-yks/bussola-do-loot/releases
DefaultDirName={autopf}\Bússola do Loot
DefaultGroupName=Bússola do Loot
AllowNoIcons=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex=Local\BussolaDoLootGuiSingleInstance
CloseApplications=yes
CloseApplicationsFilter=BussolaDoLoot.exe,chestcounter-live.exe
RestartApplications=yes
SetupIconFile=..\src\ChestCounter.Gui\Assets\token.ico
UninstallDisplayIcon={app}\BussolaDoLoot.exe
UninstallDisplayName=Bússola do Loot
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=master-yks
VersionInfoProductName=Bússola do Loot
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\BussolaDoLoot.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\chestcounter-live.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\MANUAL.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\licencas\*"; DestDir: "{app}\licencas"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Bússola do Loot"; Filename: "{app}\BussolaDoLoot.exe"
Name: "{group}\Manual da Bússola do Loot"; Filename: "{app}\MANUAL.md"
Name: "{group}\Desinstalar Bússola do Loot"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Bússola do Loot"; Filename: "{app}\BussolaDoLoot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BussolaDoLoot.exe"; Description: "{cm:LaunchProgram,Bússola do Loot}"; Flags: nowait postinstall skipifsilent
