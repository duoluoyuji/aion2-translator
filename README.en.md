# AION2 Live Translator (unofficial)

A small Windows tool that reads the chat log of **AION 2** from the game process memory,
translates foreign messages into Chinese in an overlay, and can translate your own Chinese text
into English/Japanese for copying or typing into the in-game chat.

> Unofficial project. Not affiliated with, authorized by, or endorsed by NCSOFT / AION 2.

## Features

- **Live chat translation** for world / race / nearby / party channels
- **Recordable overlay**: shows up normally in OBS / NVIDIA replay / screenshots
- **Manual translation strip (F9)**: Chinese → English / Japanese; copy or push into the chat box
- **Quick phrases**: one click fills the input box and copies to clipboard
- **Channel colors**: speaker names are colored per channel; Chinese messages are shown as-is
  and never sent to the API
- **One endpoint is enough**: keyless public endpoint by default, or any OpenAI-compatible service
  (SiliconFlow / DeepSeek / custom)

## Requirements

- Windows 10/11 x64
- Game: AION 2 (Steam client)
- **Must run as Administrator** (reading process memory requires it)
- No .NET runtime installation needed (self-contained build)

## Usage

1. Download `AION2实时翻译.exe` from [Releases](../../releases)
2. Run it as Administrator (accept the UAC prompt)
3. Settings → "Translation channel": the default keyless endpoint works out of the box
4. Back on the main window: channel filter "All" → press "Start"
5. F9 toggles the manual translation strip; F10 brings the main window back (it lives in the tray)

## Build from source

```powershell
dotnet build   src\AionTranslator.UI\AionTranslator.UI.csproj -c Release
dotnet publish src\AionTranslator.UI\AionTranslator.UI.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Output: `src\AionTranslator.UI\bin\Release\net10.0-windows\win-x64\publish\AION2实时翻译.exe` (~72 MB)

## How it works (short)

1. Find the window whose class is `UnrealWindow`, resolve the `AION2.exe` process and module base
2. Verify the client SHA256 and match a known build profile (a set of RVAs)
3. Walk the pointer chain to the chat system object, iterate the record array (stride 336 bytes)
4. De-duplicate new messages and send them to an OpenAI-compatible `/chat/completions` endpoint
5. The "Send" button posts the translation character-by-character into the in-game chat input box,
   and also copies it to the clipboard

Technical handover (Chinese): [docs/交接文档.md](docs/交接文档.md).
Reverse-engineering notes for the send channel: [docs/快捷发送方案.md](docs/快捷发送方案.md).

## Risks and disclaimer

**Read [DISCLAIMER.md](DISCLAIMER.md) first.** In short:

- The tool **only reads** chat text from the game process and sends that text to a translation
  service **you configure**
- It posts keyboard character messages to the game window to place a translation into the chat box
- Reading process memory and synthesizing input may violate the game's Terms of Service and can put
  your account at risk; the author accepts no liability
- A major client update changes the memory layout; the tool then reports "not connected" until a new
  build profile is added
- No cheating features: no memory writes, no client file modification, no automation/botting
- The binary is not code-signed and may be falsely flagged by antivirus software;
  build from source and verify the published SHA256 if that matters to you

## License

[MIT](LICENSE)
