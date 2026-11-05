# AION2 实时翻译（非官方）

《永恒之塔 2》(AION 2) 的聊天实时翻译小工具：从游戏进程里读取已有聊天记录，把外文翻成中文显示在悬浮窗；
也能把手打的中文翻成英语/日语，一键复制或填入游戏聊天框。

> 非官方项目，与 NCSOFT / 永恒之塔 2 官方无关，未获授权或认可。仅供个人学习与交流使用。

## 功能

- **实时翻译游戏聊天**：世界 / 种族 / 附近 / 组队等频道，逐条翻译后显示在主界面列表与悬浮窗
- **悬浮窗可被录制**：能正常出现在 N 卡录制 / OBS / 截图里（不做录屏排除）
- **手动翻译条（F9）**：中文 → 英语 / 日语等；译文可复制，或填入游戏聊天框
- **快捷用语**：常用句一键填进输入框并复制
- **频道配色**：玩家 ID 按频道上色；中文消息原样显示、不送翻译、不消耗额度
- **一条通道即可**：默认免 Key 的公共接口，也可换硅基流动 / DeepSeek / 任意 OpenAI 兼容服务

## 系统要求

- Windows 10 / 11 x64
- 游戏：AION 2（Steam 客户端）
- **必须以管理员身份运行**（读取游戏进程内存需要）
- 发布版自带 .NET 运行时，无需另装

## 使用

1. 到 [Releases](../../releases) 下载 `AION2实时翻译.exe`
2. 右键「以管理员身份运行」（或双击，UAC 弹窗点「是」）
3. 打开设置 →「翻译通道」，默认「B站公共接口」即可直接用；想用自己的模型就填 Key
4. 回主界面，频道选「全部」，点「开始翻译」
5. F9 调出手动翻译条；F10 唤出主界面（程序启动后自动缩在托盘）

详细步骤见 [使用说明.txt](使用说明.txt)。

## 自己构建

```powershell
dotnet build   src\AionTranslator.UI\AionTranslator.UI.csproj -c Release
dotnet publish src\AionTranslator.UI\AionTranslator.UI.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

产物：`src\AionTranslator.UI\bin\Release\net10.0-windows\win-x64\publish\AION2实时翻译.exe`（约 72 MB）

## 它是怎么工作的（简述）

1. 找到窗口类名为 `UnrealWindow` 的窗口 → 定位 `AION2.exe` 进程与模块基址；
2. 校验客户端可执行文件的 SHA256，匹配一条已知的版本布局（一组 RVA）；
3. 按指针链找到聊天系统对象，遍历记录数组（步长 336 字节）解出频道 / 说话人 / 正文；
4. 新消息去重后送翻译通道（OpenAI 兼容 `/chat/completions`），结果回填到列表与悬浮窗；
5. 「发送」按钮把译文用窗口消息逐字投递进游戏聊天输入框，并同时复制到剪贴板。

技术细节见 [docs/交接文档.md](docs/交接文档.md)。

## 风险与免责

**请先读 [DISCLAIMER.md](DISCLAIMER.md)。** 简要：

- 程序**只读地读取游戏聊天文本**，并把文本发给你**自己配置**的翻译服务；
- 程序向游戏窗口投递键盘字符消息，以把译文填入聊天输入框；
- 读取进程内存、模拟输入可能违反游戏用户协议，存在账号被处罚的风险，作者不承担任何后果；
- 客户端大版本更新后内存布局会变，程序会明确报「没连上游戏」，需要更新版本才能继续用；
- 不带任何破解、外挂、自动战斗、修改数值的功能；不写游戏内存，不改客户端文件；
- 未做代码签名，个别杀软可能误报；介意请自行从源码构建并核对 Releases 里的 SHA256。

## 许可

[MIT](LICENSE)
