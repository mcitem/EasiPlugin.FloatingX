# FloatingX 开发说明

## 环境与构建

使用 Windows 10 或更高版本、支持 .NET 6 Windows/WPF 项目的 .NET SDK，以及已经安装并至少启动过一次的希沃白板 5。插件 SDK 会读取本机希沃安装信息并引用宿主程序集。

```powershell
dotnet restore EasiPlugin.FloatingX.sln
dotnet build EasiPlugin.FloatingX.sln -c Release
```

NuGet 依赖通过 `NuGet.config` 从 NuGet.org 还原。Release 安装包和插件包输出到仓库根目录的 `bin/Release/`，用于上传 GitHub Releases。SDK 的 Debug 构建可能在本机希沃扩展目录中建立调试链接，测试前应检查已有安装。

## 项目结构

- `src/FloatingX/Program.cs` 在 Cloud 进程注册桌面浮球增强和空白白板入口。
- `DesktopToolbar.cs` 扩展原生工具集合、按钮样式和展开布局。
- `DesktopToolbarBehavior.cs` 解除原生闲置超时及拖动收起回调，保留主按钮点击切换。
- `DesktopLauncher.cs` 从登录窗口或 Cloud 主界面启动原生桌面批注，并处理返回入口窗口。
- `PresentationInput.cs` 通过 Windows 前台事件跟踪外部全屏窗口和确认焦点。整页按钮在后台调用 COM，无法使用时重新确认目标再发送左右方向键。
- `BlankWhiteboard.cs` 添加设置中心标题栏入口，并使用宿主存储接口创建、打开临时空白 ENBX。通过 Shell 窗口事件等待激活时机。
- `BlankWhiteboardBanner.cs` 隐藏放映窗口顶部的激活横幅，包含已有和后来加载的控件。
- `BlankWhiteboardSyncGuard.cs` 在 Shell 内识别插件白板，启用宿主禁止提交云同步任务的开关，切换文档后恢复原值。该开关属于本机 5.2.4.11610 的内部接口。
- `FloatingSettingsWindow.xaml` 与代码文件提供按钮排序、独立显示开关及排列预览。
- `FloatingSettings.cs` 将设置原子写入 `%LOCALAPPDATA%/EasiPlugin.FloatingX/settings.json`，读取失败时使用默认值。
- `SlideNavigation.cs` 按放映进程连接正在运行的 WPS 或 PowerPoint，通过活动放映和窗口标题核对目标，再跳转到下一张或上一张未隐藏的幻灯片。
- `PresentationInk.cs` 在桌面批注层可见时读取当前放映页，按放映窗口、文稿和 `SlideID` 在内存中切换原生笔画。
- `build/Version.props` 管理版本号。
- `Directory.Build.props` 管理作者、仓库地址和构建元数据。

插件 ID 与程序集名称均为 `EasiPlugin.FloatingX`，显示名称为「希沃悬浮窗增强」，作者为 `mcitem`。

## 实机验证

构建通过后，在目标希沃版本检查以下操作。

1. 标题栏只出现一个悬浮窗设置中心，拖动行左侧或使用箭头调整九个按钮的顺序和显示开关并保存，确认立即生效且重启后保留。检查向上、向下、首尾插入、Esc 取消拖放与列表边缘自动滚动。取消设置不应用改动，恢复默认后保存可重置顺序和显示状态。四个原生按钮均可隐藏并重新开启，全部隐藏时保留设置入口。
2. 设置中心的空白白板入口可以进入一页空白全屏课件，并能使用原生笔书写。
3. 最小化后展开原生浮球，原有工具与清屏、左右按钮均可使用。
4. 分别用 WPS 和 PowerPoint 全屏放映含动画和隐藏页的文件。选择笔写一笔，验证上一步／下一步推进动画或页面，整页按钮跳过剩余动画和隐藏页，在首尾页停留。用不支持 COM 的全屏放映验证整页按钮回退到方向键，并确认笔模式保持选中。
5. 清屏后继续写，确认笔迹清除并且可以直接再次落笔。
6. 使用原生恢复按钮返回空白白板，检查展开、收起及显示缩放。
7. 分别从未登录的登录窗口和主界面点击双箭头按钮打开悬浮窗，确认可以书写、擦除和清屏，再点放大按钮进入一页空白白板。从授课模式进入时检查它返回原课件。
8. 在未登录和已登录状态分别打开普通课件和插件空白白板，检查顶部激活横幅隐藏。对插件白板验证书写、回到备课、自动保存、关闭和本地导出，并核查云空间没有新增该白板。切换普通课件后验证原有同步功能恢复。仅本地白板也会阻止手动云同步，请使用本地导出保存。
9. 工具展开后闲置超过 60 秒，检查保持展开。分别在展开和收起时拖动主按钮，检查原状态保留；点击主按钮应正常切换。修改按钮排序或显示开关后，检查展开状态和按钮布局。

10. 在 WPS 和 PowerPoint 分别给两页写不同的笔迹，用浮球和键盘来回翻页，确认每页恢复自己的笔迹。推进页内动画时确认笔迹保留。用原生橡皮擦修改恢复的笔迹，再用清屏清除其中一页，确认另一页仍保留。退出并重开全屏放映，确认新会话从空白开始。

页绑定使用最近的外部前台全屏放映窗口。浮球显示、放映窗口变化或收到 COM 放映事件时，插件在后台查询页 ID。如果事件早于实际换页，会在约 750 毫秒内做有限次确认。切到普通桌面时恢复桌面笔迹。墨迹仅保留在放映和桌面批注会话的内存中。对多显示器和不同希沃版本，应分别做实机验证。

## 来源与发布

项目基于 [EasiNote/EasiPlugin.Sample](https://github.com/EasiNote/EasiPlugin.Sample) 开发，使用 `dotnetCampus.EasiPlugin.Sdk`。仓库中的 [原生插件开发技能](easinote-native-plugin-development/SKILL.md) 保留了宿主开发的参考资料。

提交源码、项目配置、开发文档和 LICENSE。`bin/`、`obj/`、IDE 用户设置、日志、附件和 NuGet 包缓存已排除在 Git 上传内容之外。
