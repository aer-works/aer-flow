# UI driving harness

Tooling for verifying UI behaviour against the **running** product — the desktop app driven by
window handle, and `Aer.Mobile` driven on an Android emulator over `adb`.

It exists because a UX evaluation produced three confidently-wrong findings that were only
corrected by actually driving the UI: the reviewed output *was* rendered (under a non-default tab),
Author *does* draw a live DAG (inside a collapsed expander), and a paused run *can* be approved from
the phone (when the daemon's open task happens to be that one). Reading source and clicking around
was not enough for any of them.

Not wired into `pixi run` or CI: these drive a real desktop session and a real emulator, so they are
a deliberate manual tool. Issue #313 builds automated journeys on top of them.

## Desktop (Windows, Avalonia)

| Script | Purpose |
|---|---|
| `List-Windows.ps1 -ProcessId <pid>` | Every visible top-level window for a process |
| `Capture-Window.ps1 -TitleLike <s> -OutPath <png>` | Capture the main window by title match |
| `Capture-Handle.ps1 -Handle 0x… -OutPath <png> [-ClickX -ClickY]` | Capture any HWND, optionally clicking first |
| `Click-Capture.ps1 -X -Y -OutPath <png>` | Click a window-relative point, then capture |
| `Click-Type.ps1 -Handle 0x… -X -Y -Text <s>` | Click a field and type into it |
| `Scroll-Capture.ps1 -X -Y -Notches <n> -OutPath <png>` | Scroll (negative = down), then capture |

Four things that are not obvious and cost real time to discover:

- **The first click on a background window is swallowed.** Windows delivers it as an activation and
  the control never sees it. These scripts call `SetForegroundWindow`, but Windows *refuses* that
  call from a process that is not already foreground — it fails silently and returns. So the first
  `-ClickX/-ClickY` after the app loses focus does nothing, and **the app looks broken when it is
  not**: during the M25 evaluation this manufactured three false product defects in a row, including
  a `Start new chat` button that was fine. **Click twice**, or verify with a control whose response
  is visible (a dropdown opening) before trusting that a click landed.
- **`Click-Type.ps1` is worse, and lies.** `SendKeys` targets whatever window actually holds focus,
  so when activation fails the text lands somewhere else entirely — while the script still prints
  `OK typed N chars`. Screenshot the field afterwards; never trust the return value. For anything
  load-bearing, drive the daemon's HTTP API instead and keep the UI for what you can *see*.
- **Avalonia modal dialogs and popups are separate HWNDs** and never appear in
  `Process.MainWindowTitle` — this includes `ComboBox` dropdowns, not just dialogs. One can be open
  and fully invisible to `Get-Process`. Use `List-Windows.ps1`, then `Capture-Handle.ps1`.
- Capture uses `PrintWindow` with `PW_RENDERFULLCONTENT` (`0x2`), which asks the compositor to
  re-render the window into a bitmap rather than copying screen pixels — so it works when the window
  is occluded, on a second monitor, or off-screen. Geometry comes from
  `DwmGetWindowAttribute(hwnd, 9, …)` (extended frame bounds), not `GetWindowRect`.
- The scripts save and restore the cursor position, so driving the app does not steal the pointer
  from whoever is using the machine.

Alt-mnemonics only respond after clicking into the view *and* navigating away from Home.

## Mobile (Android emulator)

Driven directly with `adb`; no wrapper scripts. The non-obvious parts:

- `adb shell input text` **splits on spaces** — escape them as `%s`:
  `adb shell input text 'two%swords'`
- The composer moves when the soft keyboard is up, so tap coordinates differ between keyboard-up and
  keyboard-down states. Screenshot between steps rather than assuming a layout.
- Capture with `adb exec-out screencap -p > out.png`.
- Pairing: the desktop advertises only its Tailscale address once tsnet is ready, and that address
  is unreachable from a device not enrolled in the tailnet. Type the **LAN** address into the Host
  field by hand. Pairing codes expire in 60 seconds, so mint and enter in one pass:
  `curl -H "Authorization: Bearer $(cat ~/.aer/daemon.token)" http://127.0.0.1:5000/api/pairing/code`

Running the app on an x86_64 emulator at all depends on the `tailscale` package patch (#303) —
without it the process is killed by seccomp before Flutter renders. See `scripts/patch-tailscale-dart.sh`.

## Coverage inventory

```
python tools/ui-harness/inventory.py
```

Parses `src/**/*.axaml` and `src/**/*.dart` for interactive controls, and reports **disclosure
containers** separately — expanders, tabs and popup menus, the places capability hides. Every
finding the evaluation initially got wrong was behind one of those.

The point is that coverage becomes a checklist rather than a memory of where someone clicked. The
current walked/unwalked state is tracked on #313.
