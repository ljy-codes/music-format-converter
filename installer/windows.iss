#ifndef SourceDir
  #error SourceDir is required; invoke scripts/package-windows.ps1
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{5214EA64-F158-4F0B-9A04-89DBCBF47E8D}
AppName=音乐格式转换器
AppVersion={#AppVersion}
AppPublisher=MusicFormatConverter
DefaultDirName={localappdata}\Programs\MusicFormatConverter
DefaultGroupName=音乐格式转换器
UninstallDisplayName=音乐格式转换器
UninstallDisplayIcon={app}\Assets\app-icon.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=MusicFormatConverter-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
; Inno Setup 6.7+; styles apply to Setup and Uninstall, including their title bars.
WizardStyle=modern dark polar includetitlebar hidebevels
WizardBackColor=#0B1020
SetupIconFile=..\src\MusicFormatConverter.App\Assets\app-icon.ico
WizardImageFile=branding\wizard-panel.bmp
WizardSmallImageFile=branding\wizard-small.bmp
DisableWelcomePage=no
DisableProgramGroupPage=yes
CloseApplications=yes
SetupLogging=yes
InfoBeforeFile=offline-notice.txt
; No network download, post-install launch, autorun, service or file association.

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=0
DialogFontName=Microsoft YaHei UI
WelcomeFontName=Microsoft YaHei UI

[Messages]
SetupAppTitle=安装
SetupWindowTitle=安装 · %1
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 · %1
InformationTitle=提示
ConfirmTitle=确认
ErrorTitle=错误
ButtonBack=上一步(&B)
ButtonNext=下一步(&N)
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)…
ButtonWizardBrowse=浏览(&R)…
ClickNext=点击“下一步”继续，或点击“取消”退出安装。
WelcomeLabel1=让音乐，回归纯粹。
WelcomeLabel2=欢迎安装 [name/ver]。%n%n多格式批量转换 · 全程本地离线%n智能保真 · 为你的音乐资料库而生%n%n源文件保持不变，转换结果单独保存。
WizardInfoBefore=离线与数据安全
InfoBeforeLabel=在开始之前，请了解以下说明。
InfoBeforeClickLabel=阅读后点击“下一步”继续。
WizardSelectDir=选择安装位置
SelectDirDesc=将 [name] 安装到哪里？
SelectDirLabel3=程序将安装到以下文件夹。
SelectDirBrowseLabel=点击“下一步”继续，或点击“浏览”选择其他位置。
DiskSpaceGBLabel=至少需要 [gb] GB 可用空间。
DiskSpaceMBLabel=至少需要 [mb] MB 可用空间。
WizardSelectTasks=快捷方式
SelectTasksDesc=按你的习惯设置入口。
SelectTasksLabel2=选择安装时需要创建的快捷方式，然后点击“下一步”。
WizardReady=准备就绪
ReadyLabel1=即将在你的电脑上安装 [name]。
ReadyLabel2a=点击“安装”开始，或点击“上一步”修改设置。
ReadyLabel2b=点击“安装”开始。
ReadyMemoDir=安装位置：
ReadyMemoGroup=开始菜单：
ReadyMemoTasks=附加任务：
WizardPreparing=正在准备
PreparingDesc=正在准备安装 [name]。
WizardInstalling=正在安装
InstallingLabel=正在安装 [name]，请稍候。
StatusExtractFiles=正在写入程序文件…
StatusCreateIcons=正在创建快捷方式…
FinishedHeadingLabel=准备好，聆听更多。
FinishedLabel=已完成 [name] 的安装。%n%n可以从开始菜单或快捷方式打开程序，添加文件或文件夹开始转换。
FinishedLabelNoIcons=已完成 [name] 的安装。
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。现在退出将不会完成安装。%n%n确定退出吗？
ConfirmUninstall=确定卸载 %1 吗？%n%n仅移除程序文件，不会删除你的源音乐、转换结果和导出报告。
UninstallStatusLabel=正在从电脑上移除 %1，请稍候。
WizardUninstalling=正在卸载
StatusUninstalling=正在卸载 %1…
UninstalledAll=%1 已卸载。%n%n你的音乐文件与转换结果已保留。
UninstalledMost=%1 卸载完成。%n%n部分程序文件未能删除，可以稍后手动清理。音乐文件与转换结果不受影响。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "可选快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\音乐格式转换器"; Filename: "{app}\MusicFormatConverter.App.exe"; IconFilename: "{app}\Assets\app-icon.ico"
Name: "{autodesktop}\音乐格式转换器"; Filename: "{app}\MusicFormatConverter.App.exe"; IconFilename: "{app}\Assets\app-icon.ico"; Tasks: desktopicon

[UninstallDelete]
; User source music, output music and reports are never removed.
Type: files; Name: "{app}\MusicFormatConverter.App.pdb"
