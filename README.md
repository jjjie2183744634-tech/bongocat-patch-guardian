# Bongo Cat Patch Guardian

这是一个面向 Windows Steam 版 **Bongo Cat（AppID 3419430）** 的非官方补丁包。

当前发布版已在 Steam Build **25088408** 上完成安装、覆盖恢复和原版回滚测试。安装包不包含游戏本体或 `Assembly-CSharp.dll`，而是在用户自己的游戏文件上做结构校验后生成补丁。

## 下载

[从 Releases 下载最新安装包](https://github.com/jjjie2183744634-tech/bongocat-patch-guardian/releases/latest)

下载 ZIP 后必须先完整解压，再双击 `安装补丁.exe`。不要直接在压缩包预览窗口内运行。

## 功能

- 普通装饰宝箱优先于表情宝箱。
- 宝箱就绪约 1 秒后领取；普通宝箱成功后，已就绪的表情宝箱再等待约 1 秒。
- 点数不足时约 60 秒后重试。
- 点数低于 1000 时，通过游戏自身 `Cat.Tap(1)` 路径逐步补足，不直接写存档。
- Bongo Cat 保持显示，但不占任务栏按钮，只保留系统托盘图标。
- 保存房间聊天记录；聊天窗口可直接发言；有未读消息时任务栏闪烁。
- Steam 更新覆盖补丁后，守护程序会在游戏退出后先备份，再尝试重新应用。

## 安装

1. 完全退出 Bongo Cat。
2. 下载并解压 Release ZIP。
3. 双击 `安装补丁.exe`。
4. 安装器会自动查找 Steam 库中的 Bongo Cat；找不到时可手动选择包含 `BongoCat.exe` 的目录。
5. 安装完成后从 Steam 正常启动游戏。

Windows SmartScreen 可能提示这是未签名程序。项目未购买代码签名证书；你可以在运行前查看本仓库源代码并核对 Release 中的 SHA256。

## 更新保护

守护程序安装在：

```text
%LOCALAPPDATA%\BongoCatPatchGuardian
```

它通过当前用户的登录启动项运行，不要求写死 Steam 路径，也不依赖管理员计划任务。

如果新游戏版本仍满足所有目标类型、字段、方法和注入后校验，补丁会自动重建；如果结构发生变化，程序会保留官方文件并在 `status.txt` / `incompatible-build.txt` 记录原因，不会强行覆盖。

如果 Steam 更新时游戏已经启动，本次运行可能暂时没有补丁；退出游戏后守护程序才会修复，下一次启动生效。

## 恢复原版

完全退出 Bongo Cat 后，双击 `恢复原版.exe`。程序只会在当前 DLL 与记录的补丁哈希一致时恢复；检测到未知修改时会停止，避免覆盖其他补丁。

原版备份和日志会继续保留，便于排查。

## 隐私与联网

安装器和守护程序本身不访问网络，不上传聊天记录。聊天记录保存在用户的“文档\BongoCat聊天记录”目录。联网部分仍由原游戏和 Steam 负责。

## 从源码构建

在 Windows PowerShell 中运行：

```powershell
.\build.ps1
```

脚本使用系统 .NET Framework C# 编译器，并从 NuGet 获取 Mono.Cecil 0.11.6。生成文件位于 `artifacts`。

## 声明

这是非官方社区工具，与 Irox Games、Bongo Cat 或 Valve 无隶属关系。请自行承担使用第三方补丁的风险。

