# 发布规则（maintainer 用）

> **硬性要求：每个 Release 页面的说明都必须包含「📋 清单怎么用」「📤 清单收集与上传」「📥 导入清单包」三个固定章节**，
> 让任何从 Release 页进来的用户都能看到：本工具含清单上传功能、清单的完整用法、导入三步图解。
> 本文是唯一模板来源，发布时直接复制「说明模板」再在最上方加本次更新要点。

## 发布流程

```cmd
rem 1. 提交并推送（master + dev 同步；push 走本地代理）
git -c http.proxy=http://127.0.0.1:7890 push origin master
git -c http.proxy=http://127.0.0.1:7890 push origin master:dev

rem 2. 构建（版本号改 GamerToolManager.csproj 的 <Version>）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish-sc

rem 3. 自检（唯一允许的 FAIL：本机没有 legacy Manifest2Lua.exe 的环境项）
publish-sc\GamerToolManager.exe --selftest

rem 4. 安装包（Inno Setup，版本自动读 publish-sc exe）
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer.iss

rem 5. 发布（gh 直连；附件按需带清单包）
gh release create vX.Y.Z installer/GamerToolManager-Setup.exe --target master --title "..." --notes-file notes.md
```

## 图片地址

Release 页不解析相对路径，固定章节里的图一律用 master 分支绝对 raw 地址：

```
https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/collect.png
https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import.png
https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import-step1-release.png
https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import-step2-import.png
https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import-step3-deploy.png
```

界面截图用程序自带模式生成（真实界面、不抢焦点）：

```cmd
publish-sc\GamerToolManager.exe --shot docs
```

## 说明模板

复制以下全部内容到 notes 文件，**最上方补一段本次更新要点**（bullet 几行），附件名按实际调整：

````markdown
（这里写本次更新要点，2~5 行）

---

## 📋 清单怎么用（完整流转）

清单 = 让 Steam 认出游戏的「户口（AppID/depot）+ 钥匙（depot 解密密钥）+ 版本（manifest）」：

```text
                ┌───────────── 获取清单（三条路） ─────────────┐
                ↓                 ↓                           ↓
         在线仓库拉取      从 Steam 提取（需拥有游戏）      导入清单包
      （输入 AppID 即可）（【清单收集】登录后提取）    （别人分享的 .gtm）
                └──────────────────────┬──────────────────────┘
                                       ↓
                         【部署到 Steam 解锁】→ 重启 Steam → 游玩
                                       ↓
                    【还原未破解】 · 【重新拉取/重新提取】随时更新
```

### 📤 清单收集与上传 —— 你的提取就是别人的清单源

**本工具内置清单上传**。把登录 Steam 后提取到的清单一键传到社区收集库，所有用户拉取时都会自动检索到它
—— **提取 → 上传 → 大家拉取** 的闭环，别人"未收录"的游戏，可能就差你这一份：

![清单收集与上传](https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/collect.png)

1. 工具栏【清单收集】→【登录 Steam】输一次账号密码（只存本机 refresh token）
2. 【从 Steam 提取】输 AppID/游戏名/链接，或【从已拥有列表选】→ 生成 [AppID] 文件夹
3. 勾选要贡献的清单集 →【上传选中】（令牌已内置，SHA 去重、只增不删、冲突保护）
4. **懒人路径【⚡ 一键收集全部】**：自动遍历账号拥有的全部游戏，已收录且一年内有更新的跳过、一年未更新的重新收集更新，逐个提取+上传，可中途【停止】，重跑自动续传
5. 上传后其他用户输入该 AppID 即可拉取到

### 📥 导入别人分享的清单包

![导入预览](https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import.png)

从 Release 下载 `.gtm` 清单包 → 主界面点【导入清单】（或拖进主窗口）→ 预览确认 → 全选【部署到 Steam 解锁】：

| ① 从 Release 下载 | ② 主界面点【导入清单】 | ③ 全选 → 部署 |
|---|---|---|
| <img src="https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import-step1-release.png" width="420"/> | <img src="https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import-step2-import.png" width="420"/> | <img src="https://raw.githubusercontent.com/liushengliuyun/GamerToolManager/master/docs/import-step3-deploy.png" width="420"/> |

> 仅供学习交流，请支持正版游戏。
````

## 发布前检查清单

- [ ] csproj `<Version>` 已升号
- [ ] `--selftest` 通过（除已知环境 FAIL）
- [ ] notes 按本模板包含三个固定章节
- [ ] 附件：Setup 必带；**示例清单包 .gtm 必带**（无新版时复用上一版的，绝不能让 Release 页缺包）
- [ ] push 已同步 master 与 dev
