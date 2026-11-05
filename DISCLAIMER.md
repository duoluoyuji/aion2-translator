# 风险与免责声明 / Risks & Disclaimer

## 中文

1. **性质**：本项目是一个**第三方非官方**辅助工具，与 NCSOFT、永恒之塔 2（AION 2）官方及其运营方
   没有任何关系，未获其授权或认可。
2. **它做什么**：只读地读取游戏进程内存中的**聊天文本**，并把该文本发送到**你自己配置**的翻译服务；
   向游戏窗口投递键盘字符消息，用于把译文填入聊天输入框。
   **不写游戏内存、不修改客户端文件、不含自动战斗 / 挂机 / 刷本 / 改数值等任何破坏平衡的功能。**
3. **账号风险**：读取进程内存、模拟输入，都可能被游戏的反作弊系统或用户协议视为不当行为，
   可能导致账号被警告、限制或封禁。**是否使用由使用者自行判断并承担全部后果**，作者不承担任何责任。
4. **数据与隐私**：
   - 聊天文本会发送到你在设置中选择的翻译服务（默认是一个公开的第三方接口）；
   - 你的 API Key 仅保存在本机 `%APPDATA%\AION2实时翻译\settings.json`，程序不会上传它；
   - 程序日志 `app.log` 只保存在本机，内容为连接状态等排障信息。
5. **可用性**：游戏客户端更新后内存布局会变化，程序会明确报「没连上游戏」而不会读错数据；
   作者可能跟进更新，也可能不跟进，不承诺持续维护。
6. **安全**：发布的二进制未做代码签名，部分杀毒软件可能误报；
   介意者请从源码自行构建，并与 Releases 中公布的 SHA256 核对。
7. **合规**：请遵守你所在地区的法律法规以及游戏用户协议。**是否使用、如何使用，由使用者自行决定。**

## English

1. **Nature**: an unofficial third-party helper. Not affiliated with, authorized by, or endorsed by
   NCSOFT or the AION 2 operators.
2. **What it does**: reads **chat text only** from the game's process memory and sends that text to a
   translation service **you** configure; posts keyboard character messages to the game window to
   place a translation into the chat input box.
   It does **not** write to game memory, modify client files, or provide automation / botting /
   stat manipulation.
3. **Account risk**: reading process memory and synthesizing input may be considered a violation of the
   game's Terms of Service and can lead to warnings, restrictions or bans.
   **Use at your own risk; the author accepts no liability.**
4. **Data & privacy**: chat text is sent to the translation endpoint you choose
   (a public third-party endpoint by default). Your API key stays on your machine in
   `%APPDATA%\AION2实时翻译\settings.json`; the tool never uploads it. Logs are local only.
5. **Availability**: a major client update breaks the memory layout; the tool then reports an explicit
   error instead of reading garbage. Maintenance is not guaranteed.
6. **Security**: released binaries are not code-signed and may be falsely flagged by antivirus
   software. Build from source and verify the published SHA256 if that matters.
7. **Compliance**: follow the laws of your jurisdiction and the game's Terms of Service.
   **Whether and how to use this tool is your decision.**
