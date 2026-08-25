[Setup]
AppName=Macro Supremes
AppVersion=1.9.0
AppPublisher=Supremes Guild
AppPublisherURL=https://github.com/Bruno-Martins-tech/macro-os-humildes
DefaultDirName={autopf}\MacroSupremes
DefaultGroupName=Macro Supremes
UninstallDisplayIcon={app}\MacroSupremes.exe
OutputDir=dist
OutputBaseFilename=MacroSupremes-Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=app.ico
DisableProgramGroupPage=yes
DisableDirPage=yes
WizardStyle=modern
CloseApplications=force
CloseApplicationsFilter=MacroSupremes.exe
RestartApplications=yes

[Files]
Source: "bin\Release\net8.0-windows\win-x64\publish\MacroSupremes.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\net8.0-windows\win-x64\publish\brasao.jpg"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userdesktop}\Macro Supremes"; Filename: "{app}\MacroSupremes.exe"; IconFilename: "{app}\MacroSupremes.exe"; Comment: "Macro Supremes - WYD Global"
Name: "{commondesktop}\Macro Supremes"; Filename: "{app}\MacroSupremes.exe"; IconFilename: "{app}\MacroSupremes.exe"; Comment: "Macro Supremes - WYD Global"
Name: "{group}\Macro Supremes"; Filename: "{app}\MacroSupremes.exe"
Name: "{group}\Desinstalar Macro Supremes"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\MacroSupremes.exe"; Description: "Abrir Macro Supremes"; Flags: nowait postinstall skipifsilent runascurrentuser

[Messages]
WelcomeLabel1=Macro Supremes
WelcomeLabel2=Vai instalar o Macro Supremes no seu PC.%n%nE rapido, so clicar em Instalar.
FinishedHeadingLabel=Pronto!
FinishedLabel=Macro Supremes foi instalado. O atalho ja esta na sua area de trabalho.
ButtonInstall=Instalar
ButtonFinish=Fechar
