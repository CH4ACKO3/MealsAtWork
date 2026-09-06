# 构建与发布

仓库：https://github.com/CH4ACKO3/MealsAtWork

## 触发方式

- 推送 main 或提交 PR：构建、XML/翻译检查、生产计时规则回归、发布包校验；只上传 Actions artifact，不发布工坊。
- 推送 `vX.Y.Z` 标签：标签必须与 `About/About.xml` 的 `modVersion` 一致，并存在 `Docs/releases/X.Y.Z.en.md` 和 `X.Y.Z.zh-CN.md`。先创建 GitHub Release，再将同一份已检验 artifact 上传工坊。
- 手动执行 Build and release，勾选 `verify_steam`：只检查认证、所有权和发布器读取权限，不上传文件。
- 填写 `verify_release_run`：下载对应运行的 artifact，对比工坊文件哈希，修复/同步中英文说明与更新记录；不重复上传模组文件。

初次工坊条目为私有。日常 tag 发布同步 `About/preview.png` 为工坊封面缩略图，并下载远端封面核对 SHA-256。图片须小于 1 MB。可见性、标题和标签保留条目现有设置；公开上架由所有者在 Steam 中单独调整。

## 发布步骤

1. 修改版本号和代码，在中英文 release notes 中描述变化；需要时更新 `Docs/workshop/Description.*.bbcode`。
2. 推送 main，确认构建成功。
3. 创建并推送与版本一致的标签，例如 `git tag v0.5.5`、`git push origin v0.5.5`。
4. 确认 `build`、`github-release`、`steam-workshop` 均成功。工坊失败时先检查失败阶段；文件可能已上传而说明同步失败，可使用验证流程完成检查。

`STEAM_PUBLISH_ENABLED=false` 可以停用工坊上传，保留 CI 与 GitHub Release。环境 `steam-workshop` 保存四项 Secrets：STEAM_USERNAME、STEAM_PASSWORD、STEAM_REFRESH_TOKEN、STEAM_CONFIG_VDF_BASE64。代码、发布包及日志均不保存这些凭据明文。SteamCMD 会话以 AES-GCM 加密 artifact 保存，绑定账号及目标条目。

## 本机检查

```powershell
./Tools/CI/Build.ps1 -OutputRoot artifacts/check-unique
./Tools/CI/PublishWorkshop.ps1 -ArtifactRoot artifacts/check-unique -DryRun
./Tools/CI/Test-Package.ps1 -ArtifactRoot artifacts/check-unique
./Tools/CI/Test-SteamSession.ps1
```

输出目录必须是新目录。CI 使用固定版本的 Krafs.Rimworld.Ref 和 Lib.Harmony 引用包，不分发游戏程序集或 Harmony。发布包只包含本模组 DLL、About、Defs、Languages、README 和 LICENSE；诊断辅助模组、完整源码、旧 ZIP 和用户测试存档不进入工坊载荷。

CI 直接编译生产代码并运行真实 LunchTiming 的独立回归。`Tests/MealsAtWork.Tests.csproj` 依赖真实游戏程序集用于 Harmony 安装和引擎对象检查，仍在本机安装 RimWorld 的环境运行；CI 不以空实现引用程序集冒充引擎实测。

发布器和加密会话流程改编自同作者的 SearchAndRescue 仓库（MIT），工坊目标 ID 独立固定。BootstrapWorkshop.ps1 仅用于首次创建私有条目；固定 ID 后它会拒绝运行，正常 tag 永远只更新现有条目。
