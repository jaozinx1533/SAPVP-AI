#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef PublishDir
  #error PublishDir must point to the Windows publish output folder.
#endif

[Setup]
AppId={{1F93BDE6-2B60-4CF2-9C9E-BD164FA4CC18}
AppName=SAPVP-AI
AppVersion={#AppVersion}
AppPublisher=SAPVP-AI
DefaultDirName={localappdata}\Programs\BlockWorldLauncher
DefaultGroupName=SAPVP-AI
UninstallDisplayIcon={app}\SAPVP-AI.exe
OutputBaseFilename=SAPVP-AI-Setup-x64
OutputDir=..\release
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SAPVP-AI"; Filename: "{app}\SAPVP-AI.exe"
Name: "{autodesktop}\SAPVP-AI"; Filename: "{app}\SAPVP-AI.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Run]
Filename: "{app}\SAPVP-AI.exe"; Description: "Abrir SAPVP-AI"; Flags: nowait postinstall skipifsilent
