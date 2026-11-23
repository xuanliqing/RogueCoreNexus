# Rogue Core Nexus Manager

Steam 版《深岩银河：异动核心》的 Windows Mod 管理器。

- 粘贴 Nexus Mod 链接，管理器会下载 Mod、找出并下载它的前置，再安装到游戏里。
- 用「配置档」保存不同的 Mod 组合，切换后一键应用到游戏。
- 只改动自己安装的文件。遇到游戏目录里已有的同名文件会停下提示，不会覆盖。

## 快速上手

1. 运行 `RogueCoreNexus.exe`。管理器会在 Steam 库里自动查找游戏。没找到时，打开「设置」，点「浏览」选择游戏的安装目录。
2. 在「设置」里点「获取密钥 ↗」，在 Nexus 网页上复制你的个人 API 密钥，粘贴到设置里，点「保存设置」。
3. 如果是免费账号，在「设置」里再点「启用浏览器回传」。Premium 账号可以跳过这一步。
4. 在顶部输入框粘贴 Mod 的 Nexus 页面链接，例如 `https://www.nexusmods.com/deeprockgalacticroguecore/mods/149`，点「安装 Mod」或按回车。
   - Mod 有多个文件时，会弹出列表让你选择版本。
   - 链接里带 `file_id` 时，会直接选中这个文件。
5. 安装完成后，点「启动游戏」。

## Premium 与免费账号

| | Premium 账号 | 免费账号 |
|---|---|---|
| 下载方式 | 管理器通过 Nexus API 直接下载 | 管理器在浏览器里打开 Nexus 下载页，你在网页上确认下载（「Slow download」按钮），网页再把下载链接交回管理器 |
| 是否需要浏览器回传 | 不需要 | 需要 |
| 一次装多个文件（含前置） | 自动逐个下载 | 每个文件确认一次，确认后管理器会自动打开下一个文件的页面 |

浏览器里登录的 Nexus 账号必须和 API 密钥属于同一个账号，否则管理器会拒绝这次下载。

更多背景见 [Nexus 的管理器下载说明](https://github.com/Nexus-Mods/Vortex/wiki/MODDINGWIKI-Users-General-Downloading-from-Nexus-Mods)。

## 配置档

- 每个配置档是一组 Mod 及各自的启用状态。安装或导入的 Mod 会加入当前配置档，并默认启用。
- 「新建」创建一个空配置档。「复制」复制当前配置档，包括其中的 Mod 和启用状态。
- 安装和导入会立即写入游戏目录。下面这些操作只会改配置档，点「应用配置」或「启动游戏」后才会改动游戏目录：
  - 切换配置档
  - 启用或停用 Mod
  - 移除 Mod
  - 更换 Mod 版本
- Mod 列表上方的按钮：
  - 「更换版本」：给选中的 Nexus Mod 选择另一个文件版本。
  - 「移除」：把选中的 Mod 从当前配置档删除。
  - 「导出 Mod 链接」：把当前配置档里所有 Nexus Mod 的链接复制到剪贴板，一行一个，停用的也包括在内。记录了文件编号的 Mod，导出的是对应版本的页面。本地导入的文件没有 Nexus 链接，不会导出。

## 前置

- 安装 Nexus Mod 时，管理器会检查两类前置：所选文件版本要求的前置，以及 Mod 页面上列出的前置。能从 Nexus 下载的《异动核心》前置，会加入同一批一起安装。
- 已经下载过、而且版本符合要求的前置会直接复用，不会重复下载。
- 一批文件全部下载并解包成功后，才会一起写入游戏。中途出错或点了「取消」，游戏目录和配置档都保持原样。
- 站外前置和 DLC 无法自动安装。安装完成后，管理器会弹窗列出作者写的这些要求。
- 已添加的 Nexus Mod，选中后点「补齐前置」，可以重新检查并补装缺少的前置。

## 支持的文件

- **压缩格式**：PAK、ZIP。7z 和 RAR 用 Windows 自带的 `tar.exe` 解包，只支持它能读取的压缩包。
- **PAK Mod**：可以安装 PAK 文件，以及配套的 IoStore 文件（`.utoc`、`.ucas`）。
- **UE4SS 脚本 Mod**：压缩包里带 `main.lua` 或 `main.js` 的 Mod。
  - **JavaScript Mod**（含 `js/main.js`）：需要 UE4SS Lite。游戏目录里已有时直接复用。首次需要时，管理器会从 [Mintcat 的发布源](https://github.com/iris-cat-dev/mintcat/blob/v0.5.5/src/apis/mintcat/release.ts) 下载 UE4SS Lite 0.31.0，校验 SHA-256 后和 Mod 一起安装。切换到不需要加载器的配置档时，管理器会撤下它自己安装的这些运行库文件。
  - **Lua Mod**：需要你按作者要求，自己安装 UE4SS 或带 Lua 插件的 UE4SS Lite。


## 数据位置与卸载

配置档、下载缓存和安装记录都保存在 `%LOCALAPPDATA%\RogueCoreNexus`。

API 密钥用 Windows DPAPI 加密，只有当前 Windows 用户能解密。换了电脑或 Windows 用户后，需要在设置里重新填写密钥。

卸载步骤：

1. 点「新建」创建一个空配置档，再点「应用配置」。这会撤下本工具装进游戏的所有文件。
2. 如果启用过浏览器回传，在「设置」里点「恢复原回传设置」。
3. 关闭管理器，删除 `RogueCoreNexus.exe` 和 `%LOCALAPPDATA%\RogueCoreNexus` 文件夹。
