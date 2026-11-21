# Rogue Core Nexus Manager

面向 Steam 版《深岩银河：异动核心》的 Windows x64 Mod 管理器。配置档、Mod 启停和启动游戏的操作参考了 [MINT Maintained v0.6.2](https://github.com/gabrielfordevelopment/mint-maintained/releases/tag/v0.6.2)。

## 首次使用

1. 打开 `dist\RogueCoreNexus.exe`。管理器会从 Steam 库查找游戏。也可在「设置」里选择游戏目录。
2. 在「设置」里填写 [Nexus 个人 API 密钥](https://www.nexusmods.com/settings/api-keys)，然后保存。密钥使用 Windows DPAPI 加密保存在当前用户的本地数据中。
3. 普通账号在「设置」里启用「浏览器回传」。如已有其他 NXM 管理器，可在同一处恢复之前的关联。
4. 粘贴《异动核心》的 Nexus Mod 页面链接并点「安装 Mod」。一个条目有多个文件时，选择想装的版本。含 `file_id` 的链接会直接选中该文件。

**Premium 账号**通过 Nexus API 直接获取下载地址。**普通账号**在浏览器打开的 Nexus 页面确认管理器下载。网页回传 NXM 链接后，工具会接着下载、解包、安装并启用。请使用与 API 密钥相同的 Nexus 账号登录网页。[Nexus 的管理器下载说明](https://github.com/Nexus-Mods/Vortex/wiki/MODDINGWIKI-Users-General-Downloading-from-Nexus-Mods)

## 管理配置

- 粘贴 Nexus 链接时，管理器会检查所选文件的版本前置和 Mod 页的前置，把可下载的《异动核心》前置加入同一批安装。已下载且符合要求的前置可以复用。选中已添加的 Nexus Mod 后，点「补齐前置」可以单独执行这项操作。
- Premium 账号直接下载整批文件。普通账号按网页提示逐个确认，管理器会继续打开下一项。所有文件准备好后才统一应用到游戏。站外前置和 DLC 会列出作者填写的要求。
- 已下载的 Mod 会加入发起下载时的配置档。切换配置档、勾选启用、移除或更换版本后，可点「应用配置」同步游戏目录。「启动游戏」会先应用当前配置，再通过 Steam 启动。
- 点 Mod 列表上方的「导出 Mod 链接」，可将当前配置档中所有 Nexus 条目的链接复制到剪贴板，一行一个，包含已禁用的条目。保存了文件编号的条目会导出对应版本的页面。本地导入的文件没有记录 Nexus 链接。
- 支持 PAK、ZIP，以及 Windows 自带 `tar.exe` 能解包的 7z 和 RAR。可安装 PAK 和配套 IoStore 文件。
- 启用含 `js/main.js` 的 Mod 时，管理器可复用已有的 UE4SS Lite。首次需要部署时，它会从 [Mintcat 的发布源](https://github.com/iris-cat-dev/mintcat/blob/v0.5.5/src/apis/mintcat/release.ts)下载 UE4SS Lite 0.31.0，校验 SHA-256，再与 Mod 一起安装。切换至无需加载器的配置时，会撤下本工具部署的运行库文件。Lua Mod 需要作者要求的 UE4SS 或带 Lua 插件的 UE4SS Lite。
- 本工具会跟踪自己部署的文件。检测到已有的同名游戏文件或其他工具改过的文件时，会保留现有文件并显示冲突路径。
- 配置档和缓存位于 `%LOCALAPPDATA%\RogueCoreNexus`。

## 从源码构建

使用 Windows 的 .NET Framework 4.8 C# 编译器构建。

```powershell
.\build.ps1
.\build.ps1 -Test
```

测试通过模拟 Nexus API 与授权回传验证界面下载流程，并在独立的游戏目录中测试解包、启停、冲突处理和中断恢复。
