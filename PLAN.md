# SerialTool — Implementation Plan

A Windows serial console for testing and debugging UART and USB-serial devices.
Phase 1 is a normal (owning) connection to a COM port. Spy mode (monitoring a port
owned by another process) is deferred, but the architecture is designed so it can be
added as another data source without touching the views.

## Decisions (confirmed)

| Area | Decision |
|---|---|
| Language / runtime | C#, .NET 10 (LTS), Windows only |
| Distribution | Framework-dependent single-file exe (~1–3 MB, no installer). Requires the .NET 10 Desktop Runtime on the target machine |
| UI | WPF, MVVM (CommunityToolkit.Mvvm) |
| Serial I/O | Own Win32 wrapper (P/Invoke, overlapped I/O) — not System.IO.Ports |
| Ports per window | One. Run multiple instances for multiple ports. No CLI arguments; never auto-connect on start |
| Layout | Text view always visible; hex view in a split pane, **hidden by default**, toggled via toolbar button / View menu (Ctrl+H) |
| Chunking / timestamps | Default: one chunk per raw OS read (**each raw read starts a new line**). Setting to switch to *line delimiter* or *idle gap (ms)* |
| TX in views | Yes, interleaved and color-coded; toggle to hide TX |
| Input | Dedicated send box (text / hex / escaped modes) + history + macro button bar. Typing in views does not send |
| Retention | In-memory ring buffer with configurable cap + optional continuous "log to file" |
| Spy mode | Approach undecided — research later; keep `IDataSource` abstraction clean |
| Extras for v1 | None (no decoders, scripting, triggers, or checksum helpers) |

## Behaviour specification

### Line breaking and grouping
- **Raw mode** (default): every raw read event starts a new line. Newlines inside a
  read also break lines.
- **Delimiter mode**: lines end on the configured newline. Setting: `LF`, `CR`,
  `CRLF`, `Any (CR, LF or CRLF; CRLF counts as one)`, or a custom byte sequence
  (entered as hex). Default: *Any*. Delimiters spanning two reads must be handled
  (e.g. CR at end of one read, LF at start of the next).
- **Idle-gap mode**: a new line starts when the gap between consecutive reads exceeds
  N ms. Note: USB-serial latency (e.g. FTDI 16 ms) makes small gaps meaningless.
- The same newline setting drives text-view line breaks in all modes.

### TX interleaving
- Setting **"TX during RX line"**:
  - *Force break* (default): end the current RX line, show the TX on its own line(s),
    and continue RX on a new line.
  - *Show after line*: keep the RX line intact; the TX is displayed right after the
    RX line ends (on its next delimiter / read boundary). The TX keeps its own
    timestamp, so it can look out of order — this is expected.
- The "hide TX" toggle hides TX lines in both modes.

### Control events in views
- Open / Close / Error / PinChange / ConfigChange events appear **inline** as marker
  lines, e.g. `── Connected COM3 115200 8N1 ──`, `[FRAMING ERROR]`, `[OVERRUN]`,
  `CTS ↑`, `DSR ↓`, `[BREAK]`.
- View menu → "Show events" toggle, **on by default**. Optional per-kind toggles
  (pins, errors, connection) if they're cheap to add.
- Events always force a line break.

### Hex view rows
- Row breaks follow the **active grouping mode** (raw read / delimiter / idle gap)
  when timestamps are shown; otherwise bytes flow continuously at N bytes per row.

### Wrapping
- View menu → "Word wrap" toggle (default off = horizontal scroll).
- Hard cap on rendered line length (e.g. 64 KB). Longer lines are split with a visible
  continuation marker, so a binary stream with no newlines can't create one huge line.

### Connect / reconnect / buffer lifetime
- Reconnecting to the **same port** (manual or auto-reconnect) keeps the buffer — one
  continuous session with Open/Close markers.
- Changing baud / framing / flow control on the same port keeps the buffer
  (ConfigChange marker).
- Connecting to a **different port** clears the buffer (and stops any running log).
- "Clear" command (toolbar + Ctrl+L): clears the buffer and display; a running log keeps going.
- Pause: only pauses the **display**. Capture, logging, and counters keep running; the
  view catches up on resume.

### DTR / RTS on open
- Per-port setting for each line: *Leave de-asserted* (default) / *Assert on open* /
  *Restore last state*.
- Default is the safest choice for auto-reset boards (Arduino, ESP32): DCB opens with
  `fDtrControl = DTR_CONTROL_DISABLE`, `fRtsControl = RTS_CONTROL_DISABLE`, and the
  wrapper never sends DTR/RTS changes during open.
- Caveat: some drivers briefly change the lines inside `CreateFile` before the DCB is
  applied. We can't prevent that from user mode — document it, and test with an
  ESP32/Arduino in milestone 1.
- When RTS-CTS / DTR-DSR handshaking is on, the matching manual toggle is disabled,
  because the driver controls that line.

### Send box parsing
- **Text mode**: encoded as **ASCII** by default (non-ASCII characters are rejected
  with an inline error, not silently replaced). The encoding can be changed in settings.
- **Hex mode (strict)**: input is split on whitespace into tokens (whitespace is
  optional). Each token may have one optional `0x`/`0X` prefix followed by an
  **even** number of hex digits (case-insensitive), read two digits per byte in order.
  Valid: `0xDEADBEEFCAFEFEED`, `DEADBEEFCAFEFEED`, `01 02 aa FF`, `0x0102 0xAA`.
  Rejected: odd digit count in a token (`1 2`, `0xABC`), commas or other separators,
  `0x` with no digits, non-hex characters. Errors are shown inline with the bad
  token's position; nothing is sent.
- **Escaped mode**: `\r \n \t \0 \\ \xHH` (exactly two hex digits). Unknown escapes
  are an error. No `\u`.
- The line-ending append (None / CR / LF / CRLF) applies to Text and Escaped modes,
  not Hex.

### Logging
- Text transcript follows the **current** view options (encoding, timestamps,
  control-char display, event markers). Changing options mid-log changes the output
  from that point on.
- Filename template, default `{port}_{date}_{time}.log` (e.g. `COM3_2026-11-05_143012.log`);
  extension depends on format (`.log` / `.bin` / `.stsession`). Configurable
  folder, plus an "auto-start log on connect" option.
- Raw binary logs RX only. No TX log file.
- No rotation. Disk-full or write errors stop the log with a visible error; capture
  continues.
- Written on a background thread from the event channel. It never blocks the read thread.

### Timestamps
- All event timestamps come from `Stopwatch.GetTimestamp()` (QPC, monotonic).
  Absolute display = session-start local `DateTime` + elapsed ticks. Local time; DST
  changes and wall-clock drift during long sessions are ignored.
- The session file stores the session-start `DateTime`, its UTC offset, and
  `Stopwatch.Frequency`.

### Session reopen
- `ReplaySource` loads the whole session file into the store instantly for browsing.
  No timed playback.

## Architecture

```
 IDataSource ──▶ Capture pipeline ──▶ Event store ──▶ Views (virtualized)
  SerialSource    timestamp, dir,      chunked ring     Text view
  (SpySource)     raw-read events      buffer           Hex view (toggle)
  ReplaySource                            │             Status bar / stats
  FakeSource (tests/bench)                ▼
       ▲                              File logger
  Send box / macros
```

### Data model
- `CaptureEvent` (immutable): `Seq`, `Timestamp` (Stopwatch ticks), `Direction`
  (Rx / Tx / Control), `Kind` (Data, PinChange, Open, Close, Error, ConfigChange;
  later IoCtl for spy), payload reference.
- The store keeps **raw read events**. Delimiter / idle-gap grouping is a *view
  projection* over the raw events, so the chunking setting can be changed after
  the fact without losing anything.
- **Ordering**: sources timestamp events where they happen (Rx at read completion,
  Tx at **write submission**, so a loopback echo is never shown before its Tx). The single
  channel consumer assigns `Seq`. Views order by `Seq`.

### Pipeline
- `SerialSource` runs a dedicated read thread: overlapped `ReadFile` with
  `COMMTIMEOUTS { ReadIntervalTimeout = MAXDWORD, ReadTotalTimeoutMultiplier = MAXDWORD,
  ReadTotalTimeoutConstant = N }` → returns as soon as any bytes are available.
  Each non-empty completion = one Rx `CaptureEvent`. **Zero-byte completions are dropped.**
- DCB: `fAbortOnError = FALSE`, `fBinary = TRUE`.
- Separate `WaitCommEvent` loop (EV_CTS/DSR/RLSD/RING/ERR/BREAK). On each event:
  `GetCommModemStatus` for pin states and `ClearCommError` for `CE_FRAME` /
  `CE_OVERRUN` / `CE_RXOVER` / `CE_RXPARITY` / `CE_BREAK`. Pulses shorter than the
  polling latency may be missed (documented limitation).
- Writes go through a serialized write queue. The UI shows when sends are pending,
  with a "Cancel pending sends" action (`CancelIoEx`). This covers CTS held low under
  hardware flow control. Send File shows progress and can be cancelled.
- Events flow through `System.Threading.Channels` into the store; UI pulls new
  events on a ~30–60 Hz `DispatcherTimer` (never per-byte dispatcher invokes).
- Open runs off the UI thread (Bluetooth SPP ports can block in `CreateFile`) and
  uses `\\.\COMn` naming.
- USB unplug: detect via read/write errors (`ERROR_ACCESS_DENIED`,
  `ERROR_BAD_COMMAND`, `ERROR_OPERATION_ABORTED`, `ERROR_GEN_FAILURE`) plus
  `CM_Register_Notification`. Close cleanly and post a Close event. Optional
  auto-reconnect when the device reappears. Match on: USB serial number → USB
  location path → COM name. With identical adapters and no serial number, match by
  location only. System resume is treated as a possible unplug.
- `CM_Register_Notification` callbacks only queue work (no blocking) and are
  debounced (~250 ms), because one plug produces several notifications.

### Event store
- Payload bytes are stored in large contiguous arena segments (e.g. 4 MB).
  Events are compact structs (seq, timestamp, dir/kind, segment, offset, length) —
  no per-event `byte[]` — so tiny reads at low baud rates don't bloat memory.
  The byte cap (default 100 MB, configurable) counts arena bytes plus index overhead.
- Evicts oldest segments when the cap is exceeded. Views handle "first available seq"
  moving forward. Selections and search hits that point into evicted data are
  clamped or dropped.
- Maintains a line/row index incrementally for the active grouping mode so the
  views can virtualize by row number. Changing the mode rebuilds the index in the background.

### Views
- **Text view**: custom `FrameworkElement` rendering only visible lines
  (`GlyphRun`/`FormattedText`), monospace. Options: encoding (ASCII / UTF-8 /
  Latin-1), control-char display (hide / escapes `\r` `<02>` / symbols: dimmed
  arrows CR `←` LF `↓` TAB `→`, `<XX>` for others — default),
  timestamp column (off / absolute / delta-from-previous / since-connect),
  RX/TX colors, auto-scroll with "pinned to bottom" detection, pause display,
  wrap toggle, show-events toggle.
  - UTF-8 decoding works on the byte stream, not per event, so multibyte
    sequences split across reads decode correctly. A per-line char→(seq, offset) map
    supports linked selection.
  - Font: Cascadia Mono → Consolas, with per-glyph fallback to other system fonts
    (`GlyphRun` has no automatic fallback). Control Pictures (U+2400 block) were dropped:
    they render illegibly small in a monospace cell.
- **Hex view**: custom virtualized control. Columns: timestamp (optional),
  offset, hex, ASCII. Bytes-per-row configurable (8/16/32). Rows follow the grouping
  mode (see above). Color coding by direction plus byte class (0x00, 0xFF, control,
  printable, high-bit), theme-configurable.
- **Linked selection**: selecting a range in either view highlights the same
  bytes in the other (shared `(seq, offset)` addressing).
- Copy as: text, hex string, C array, base64. Large selections are copied off the UI thread with a size warning.
- Search: text or hex pattern, next/prev, highlight all. Runs incrementally in the
  background; matches can span read boundaries but never cross between RX and TX.

### Send box
- Modes: Text (ASCII default), Hex (strict), Escaped — see behaviour spec.
- Line ending append: None / CR / LF / CRLF (Text and Escaped).
- Up/down history (persisted).
- Macro bar: named buttons, each with payload + mode; edit dialog; persisted.
- Send file (raw), with optional inter-chunk delay, progress, and cancel.
- Warning shown if XON/XOFF flow control is on and the payload contains 0x11 or 0x13.
  (With XON/XOFF on, the driver also strips those bytes from RX — say so in the
  flow-control tooltip.)

### Connection panel
- Port dropdown with friendly name, VID:PID, serial number. Sources: SetupAPI
  `GUID_DEVINTERFACE_COMPORT` **merged with** `HKLM\HARDWARE\DEVICEMAP\SERIALCOMM`
  (com0com / virtual / some Bluetooth ports don't register the interface). VID/PID/serial
  come from the parent USB device node for composite devices (`MI_xx`). Live refresh on plug/unplug.
- Baud (presets + custom), data bits, parity, stop bits, flow control
  (None / RTS-CTS / DTR-DSR / XON-XOFF).
- Manual RTS / DTR toggles (disabled when the matching handshake is on), Break; live CTS / DSR / DCD / RI indicators.
- DTR/RTS-on-open settings (see behaviour spec).
- Status bar: state, Rx/Tx byte counts, current throughput, buffer usage, pending-send indicator.
- Window title shows port + settings (e.g. `COM3 115200 8N1 — SerialTool`) to tell instances apart.

### Logging / persistence
- Optional "log to file" while connected: choose format —
  raw binary (Rx only), text transcript (follows current view), or native session
  file (all events; reopenable via `ReplaySource`). See behaviour spec.
- Export current buffer in any of the above.
- Session file: versioned header (magic, format version, Stopwatch frequency,
  session start time + UTC offset, port config), then an append-only event stream.
- Settings (JSON in `%APPDATA%\SerialTool`, with a schema version): last port + config,
  per-port DTR/RTS prefs, view options, colors, macros, history, log folder/template.
- Multiple instances: no merging — the last instance to save wins. Writes are atomic
  (temp file + rename) so a concurrent save can't corrupt the file.

## Solution layout

```
SerialTool.slnx
 src/
  SerialTool.Core          CaptureEvent, IDataSource, pipeline, EventStore,
                           groupers (raw/delimiter/idle), formatters, send-text parser,
                           FakeSource
  SerialTool.Serial        Win32 interop, SerialSource, port enumeration, hot-plug watcher
  SerialTool.Persistence   settings, log writers, session file format, ReplaySource
  SerialTool.App           WPF shell, view models, TextView/HexView controls, themes
 tools/
  SerialTool.Harness       console harness (stharness.exe): list / watch / open / loopback / bench
 tests/
  SerialTool.Core.Tests    groupers (incl. delimiters split across reads), formatters,
                           UTF-8 split decoding, hex/escape parsers, store eviction,
                           FakeSource throughput benchmark
  SerialTool.Serial.Tests  integration tests against a com0com loopback pair (opt-in;
                           com0com confirmed installed on the dev machine)
```
`SerialTool.Spy` gets added later; nothing in Core/App should reference serial-specific types.

## Milestones
1. **Core + Serial**: Win32 wrapper, SerialSource, enumeration; console harness
   verified against com0com loopback and a real USB-serial adapter. Check that the
   ESP32/Arduino doesn't reset on open. **Smoke-test a framework-dependent single-file
   publish of a minimal WPF app** (including the missing-runtime prompt on a clean machine/VM).
2. **App shell**: connection panel, text view (raw-read chunks, timestamps, colors,
   inline events, wrap toggle), send box. Throughput benchmark with FakeSource.
3. **Grouping modes**: delimiter / idle-gap projections, TX-interleave setting, settings UI.
4. **Hex view**: virtualized control, color coding, toggle/split, linked selection.
5. **Macros, history, search, copy formats.**
6. **Logging / export / session reopen.**
7. **Polish**: dark/light theme, settings persistence,
   unplug/auto-reconnect, release publish profile: `SelfContained=false`,
   `PublishSingleFile=true`, `RuntimeIdentifier=win-x64`, `SatelliteResourceLanguages=en`,
   no trimming (WPF doesn't support it).
8. **Spy mode research spike** (filter driver vs. virtual-port proxy vs. USBPcap).

## Risks / notes
- High throughput (1–3 Mbaud sustained): requires the batched UI pull and custom
  rendering; benchmark early in milestone 2.
- Timestamp precision is bounded by the driver: FTDI's default 16 ms latency timer
  means "per raw read" timestamps are read-completion times, not wire times.
- Win32 overlapped I/O + cancellation (`CancelIoEx`) on unplug must be robust —
  this is the main thing System.IO.Ports gets wrong.
- Some drivers toggle DTR/RTS inside `CreateFile`; user mode can't fully prevent it.
- Distribution depends on the .NET 10 Desktop Runtime being installed (one-time ~55 MB,
  serviced via Microsoft Update). If it's missing, the apphost shows a download prompt
  instead of starting. Mention this in the README. A self-contained build (~70 MB
  compressed) remains possible later from the same project if a locked-down machine needs it.
- Single-file framework-dependent publish: ensure no code relies on `Assembly.Location`
  (returns empty in single-file); use `AppContext.BaseDirectory` instead.
