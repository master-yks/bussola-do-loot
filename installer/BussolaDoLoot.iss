#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\app"
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
DisableWelcomePage=no
DisableReadyPage=no
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
LZMAUseSeparateProcess=yes

; Identidade visual Obsidian & Ouro Pirata (Inno Setup 6.7+)
WizardStyle=modern dark
WizardBackColor=$1B150A
WizardImageBackColor=$1B150A
WizardSmallImageBackColor=$2C2312
WizardImageFile=assets\wizard-sidebar.bmp
WizardSmallImageFile=assets\wizard-header.bmp
WizardKeepAspectRatio=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
SetupAppTitle=Instalador — Bússola do Loot
SetupWindowTitle=Bússola do Loot {#MyAppVersion} · Instalação
WelcomeLabel1=Bem-vindo à Bússola do Loot
WelcomeLabel2=Este assistente instalará a Bússola do Loot ({#MyAppVersion}) no seu computador.%n%n• Conta seus baús e os do esquadrão por replay e ao vivo (OCR).%n• Inclui mapa HD de Crossbones Isle, rotas e overlay para OBS.%n• Já inclui o runtime necessário — não precisa baixar nada à parte.%n%nSeus dados salvos em %APPDATA%\ChestCounter são preservados automaticamente.%n%nClique em Avançar para continuar.
WizardReady=Tudo pronto para instalar
ReadyLabel1=O instalador está pronto para configurar a Bússola do Loot no seu computador.
WizardInstalling=Instalando a Bússola do Loot
InstallingLabel=Aguarde um instante enquanto preparamos o aplicativo e os atalhos…
FinishedHeadingLabel=Bússola do Loot instalada!
FinishedLabel=A Bússola do Loot está pronta para uso.%n%nDica: ative a gravação de replays no Fortnite (Battle Royale ou Construção Zero) e clique em Sincronizar após as partidas.

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"; Flags: checkablealone

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Bússola do Loot"; Filename: "{app}\BussolaDoLoot.exe"; Comment: "Abrir Bússola do Loot"
Name: "{group}\Manual da Bússola do Loot"; Filename: "{app}\MANUAL.md"; Comment: "Abrir manual de uso"
Name: "{group}\Desinstalar Bússola do Loot"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Bússola do Loot"; Filename: "{app}\BussolaDoLoot.exe"; Tasks: desktopicon; Comment: "Tracker de baús do Fortnite"

[Run]
Filename: "{app}\BussolaDoLoot.exe"; Description: "Abrir a Bússola do Loot agora"; Flags: nowait postinstall skipifsilent
Filename: "{app}\LEIA-ME.txt"; Description: "Ler o guia rápido (LEIA-ME.txt)"; Flags: postinstall shellexec skipifsilent unchecked
