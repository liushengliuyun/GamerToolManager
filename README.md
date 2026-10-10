# GamerTool 管理器

一款 Steam 清单解锁管理工具：**拉取清单 → 生成解锁 Lua → 部署注册到 Steam**，并支持**一键还原未破解**。内置网络引擎（多镜像并行下载），无需任何外部工具；也可手动配合 SteamTools 悬浮窗使用。

> 仅供学习交流，请支持正版游戏。

## ✨ 功能

| 功能 | 说明 |
|---|---|
| 📥 拉取清单 | 输入 AppID / 游戏名 / 粘贴商店链接，自动从多个清单仓库（分支即 AppID 的组织形式）检索并下载清单与 Key.vdf，**多任务并发** |
| 🚀 部署解锁 | Lua → `stplug-in`、清单 → `depotcache`、addappid 目标登记进 `appids.db`，等价于拖 SteamTools 悬浮窗 |
| ↩️ 还原未破解 | 精确删除该游戏的 Lua / 清单 / 登记行；支持一键"还原全部" |
| 🌐 联网补名 | Steam 官方接口抓中文名，直连失败自动走代理 |
| 🧹 孤儿登记清理 | 清理 appids.db 中无 Lua 覆盖的残留登记 |
| 🪟 布局记忆 | 窗口宽高、分割线位置、最大化状态关窗即存 |
| 🎨 细节 | 双击复制游戏名/AppID、注册时间显示与排序、空文件夹标记、回收站删除、CJK/高 DPI 自适应布局 |

## 📦 下载

到 [Releases](../../releases) 页面下载：

- `GamerToolManager-Setup.exe` — 安装版（内置 SteamTools 安装包，装完可在程序内一键安装 SteamTools）
- `GamerToolManager-portable.exe` — 便携版，单文件直接运行

## 🧰 使用前置

1. **Steam** 已安装并登录
2. **SteamTools** 已安装并保持运行（程序会在未安装时引导你安装）
3. Windows 10/11 x64（无需 .NET 运行时，自带）

## 🚀 快速上手

```
① 拉取：主页面输入框输入 AppID 或游戏名 → 回车
② 部署：列表选中游戏 → 点【部署到 Steam 解锁】→ 按提示重启 Steam
③ 游玩：Steam 库里出现游戏，正常下载
④ 还原：【还原未破解】恢复原状
```

重启 Steam 时程序会按 **Steam 退出 → SteamTools 重启并等就绪 → 再启动 Steam** 的顺序拉起，最大化避免"重启后解锁失效"。

## 🛠️ 从源码构建

需要 .NET 9 SDK 与 Inno Setup 6/7：

```cmd
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish-sc
```

打包安装程序前，把 SteamTools 官方安装包放到 `tools\st-setup-<版本>.exe`（被 `.gitignore` 排除，需自备），然后：

```cmd
ISCC installer.iss
```

## 📁 数据与配置

| 路径 | 内容 |
|---|---|
| `%APPDATA%\GamerToolManager\settings.json` | 设置与窗口布局记忆 |
| `%APPDATA%\GamerToolManager\games.json` | 游戏名/注册时间记录 |
| `C:\GamerTool\[AppID]` | 拉取产物（清单 + Lua），目录可在设置中更改 |

## ⚠️ 免责声明

本项目为本地文件管理工具，本身不包含任何破解代码；解锁能力来自 SteamTools 与公开清单仓库。请支持正版游戏，于 24 小时内自行承担使用风险，仅供学习交流使用。
