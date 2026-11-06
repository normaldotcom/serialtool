# SerialTool

A fast serial console for Windows. It handles sustained multi-megabaud traffic and keeps every byte with a
high-resolution timestamp. You get a text view and a hex view with linked selection, plus send history,
macros, logging, session files and automatic reconnect when a USB adapter is unplugged.

## Requirements

- Windows 10 or 11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Windows offers to download it
  the first time you run SerialTool if it isn't installed.

SerialTool ships as a single `SerialTool.exe` with no installer. Settings are stored in
`%APPDATA%\SerialTool\settings.json`.

## Features

**Connecting**
- The port list shows the friendly name, USB VID:PID and serial number. It refreshes when devices are plugged
  in or removed, and includes virtual and Bluetooth ports that only appear in the registry.
- Baud rate (presets or any custom value), data bits, parity, stop bits and flow control (RTS/CTS, DTR/DSR,
  XON/XOFF). Changes apply immediately, even while connected.
- RTS, DTR and Break toggles, plus live CTS, DSR, DCD and RI indicators.
- **DTR and RTS stay de-asserted on open** by default, so Arduino and ESP32 boards don't reset when you
  connect. Turn on *Connection › Assert DTR/RTS on open* to change this. Some drivers toggle the lines inside
  `CreateFile` before SerialTool can configure the port; nothing in user mode can prevent that.
- Settings are remembered **per port**. Selecting a port restores the baud rate, framing, flow control and
  DTR/RTS choices you last used with it.
- **Auto-reconnect** (*Connection › Auto-reconnect after unplug*, on by default). When a USB adapter is
  unplugged, SerialTool shows "Waiting for COMx…" and reopens the port as soon as the adapter is back. The
  buffer and any running log carry on as one session. The device is recognized by its USB serial number, so
  it is found even if Windows gives it a different COM number. Click **Stop waiting** to give up.
- The window title shows the port and its settings (`COM3 115200 8N1 — SerialTool`), which helps tell several
  instances apart.

**Viewing**
- **Text view**: timestamps (absolute, delta, or since connect), ASCII, UTF-8 or Latin-1 decoding, and three
  ways to show control characters: hidden, escapes (`\r <02>`), or symbols (`← ↓ <02>`).
- **Line grouping**: start a new line on every raw read, only on newlines (CR, LF, CRLF or a custom byte
  sequence), or after an idle gap. Open *View › Line grouping settings…* for the details, including how TX is
  shown when it arrives in the middle of an RX line.
- **Hex view** (Ctrl+H) replaces the text view and shows offset, hex and ASCII columns at 8, 16 or 32 bytes
  per row, color-coded by direction and byte class. A selection carries over between the two views.
- Wrap, show/hide TX, show/hide events (connect, pin changes, errors), and pause. Pause freezes only the
  display; capture and logging keep running.
- Light and dark themes (*View › Theme*). *System* follows the Windows app theme.

**Finding and copying**
- **Find** (Ctrl+F) searches text or hex patterns, highlights every match and steps through them with F3 and
  Shift+F3. Matches can span reads but never mix RX and TX.
- **Copy as** text, hex string, C array or base64.

**Sending**
- Three send modes:
  - **Text** is sent as ASCII. Non-ASCII characters are rejected rather than replaced.
  - **Hex** is strict: whitespace-separated tokens, each with an optional `0x` and an even number of digits,
    e.g. `01 02 aa FF` or `0xDEADBEEF`.
  - **Escaped** accepts `\r \n \t \0 \\ \xHH`.
- A line ending (none, CR, LF, CRLF) can be appended in Text and Escaped modes.
- **History**: Up and Down in the send box walk previous sends. **▾ Recent** (Alt+Down) lists them, each with a
  one-click **Send ▶** and **+ Macro**.
- **Macros**: named buttons in the macro bar, sent with Ctrl+1…9. Right-click a button to edit, move or delete it.
- **Send file**: sends a file in chunks with an optional delay between them, with progress and cancel.
- With XON/XOFF flow control on, SerialTool warns if the data you're sending contains 0x11 or 0x13.

**Logging and sessions**
- **Log to file** (*File › Start Logging…*) has three formats:
  - **Text transcript** (`.log`): plain UTF-8 text laid out like the text view (grouping, timestamps,
    `TX>` prefixes, events). Control characters are written as `<XX>`.
  - **Raw** (`.bin`): received bytes only, exactly as they arrived.
  - **Session** (`.stsession`): every event with its timestamp. Reopen it later with *File › Open Session*
    (Ctrl+O) or by dragging the file onto the window.
- File names come from a template (`{port}_{date}_{time}`). Logging can start automatically when you connect.
- **Export Buffer** saves what is already captured, in any of the three formats.

## Keyboard shortcuts

| Key | Action |
|---|---|
| Enter | Send |
| Up / Down (in the send box) | Previous / next history entry |
| Alt+Down or F4 | Open the Recent list |
| Ctrl+1 … Ctrl+9 | Send macro 1–9 |
| Ctrl+F, F3, Shift+F3 | Find, next match, previous match |
| Ctrl+C, Ctrl+A, Esc | Copy, select all, clear selection |
| Ctrl+H | Switch between text and hex view |
| Ctrl+L | Clear the buffer (a running log keeps going) |
| Pause | Pause or resume the display |
| Ctrl+O | Open a session file |

## Settings file

`%APPDATA%\SerialTool\settings.json` holds the last port, per-port settings, view options, theme, window
position, history, macros and log settings. It is saved a couple of seconds after each change and again on
exit. With several instances running, the last one to save wins. Writes are atomic, so the file never ends
up half-written.

You can override the text and hex view colors per theme by editing the file while SerialTool is closed:

```json
"Colors": {
  "Dark":  { "Tx": "#4FC1FF", "Background": "#101010" },
  "Light": { "Rx": "#000000", "SearchHit": "#80FFD840" }
}
```

Color names: `Background`, `Rx`, `Tx`, `RxControl`, `TxControl`, `Timestamp`, `EventInfo`, `EventPin`,
`EventError`, `TxBackground`, `Selection`, `SearchHit`, `HexZero`, `HexFF`, `HexControl`, `HexHigh`,
`HexOffset`. Use `#RRGGBB`, or `#AARRGGBB` to include alpha.

## Building

Requires the .NET 10 SDK.

```
dotnet build SerialTool.slnx
dotnet test SerialTool.slnx
dotnet publish src/SerialTool.App -c Release -o publish
```

The release build is a framework-dependent single-file `publish\SerialTool.exe`.

Projects:

| Project | Contents |
|---|---|
| `SerialTool.Core` | Event store, capture pipeline, line projection, formatting, search, send parsing |
| `SerialTool.Serial` | Win32 overlapped serial I/O, port enumeration, hot-plug watcher |
| `SerialTool.Persistence` | Session files, log writers, transcripts, export |
| `SerialTool.App` | WPF shell, view models, the virtualized text and hex views |
| `tools/SerialTool.Harness` | Command-line loopback and throughput harness |

The serial loopback tests need a connected pair of ports (a null-modem cable or com0com). They run only when
`SERIALTOOL_LOOPBACK=COM4,COM5` is set.

### Developer switches (environment variables)

| Variable | Effect |
|---|---|
| `SERIALTOOL_FAKE=1` | Adds simulated ports (echo, 115200 text, 3 Mbaud text, 3 Mbaud random 1-byte reads). Always on in Debug builds. |
| `SERIALTOOL_SETTINGS=path` | Uses a different settings file |
| `SERIALTOOL_BENCH=FAKE-3M:10` | Runs the UI throughput benchmark, writes `%TEMP%\serialtool-bench.txt` and exits |
| `SERIALTOOL_SCENARIO=send\|log\|theme\|reconnect` | Runs a UI smoke scenario, writes `%TEMP%\serialtool-scenario.txt` and exits (`SERIALTOOL_SHOT=dir` saves screenshots; `reconnect` uses `SERIALTOOL_PORT`) |

## Known limitations

- Pin pulses shorter than the modem-status polling latency can be missed.
- With XON/XOFF flow control on, the driver strips 0x11 and 0x13 from received data.
- Spy mode (watching another program's port) is not implemented yet.
