# Goalkeeper

A small Windows tray app that resumes stalled ChatGPT/Codex Goals, optionally retries model-capacity errors, and dismisses a few supported notices.

**[Download the Windows app](https://github.com/mingusb/goalkeeper/releases)** · [MIT license](LICENSE)

## Install

1. Download `Goalkeeper-v0.1.0-windows-x64.zip` from Releases.
2. Extract the entire ZIP into a folder you want to keep.
3. If an older copy of Goalkeeper or Goal Auto Resume is running, choose **Exit** from its tray menu.
4. Run `Goalkeeper.exe`. Right-click its icon in the notification area near the clock to change options.

Requires 64-bit Windows. The release includes its .NET runtime; you do not need to install .NET to run it. The executable is unsigned.

Keep the relevant chat loaded in the ChatGPT/Codex desktop app. Goalkeeper uses Windows accessibility controls to find and invoke specific buttons.

## What it does

The three tray options work together and can also be enabled independently.

| Tray option | New-install default | Behavior |
| --- | --- | --- |
| **Monitor Goals** | On | Finds **Resume goal** in the Goal toolbar, invokes it, and checks for **Pause goal**. |
| **Auto-click model Retry** | Off | Clicks an enabled **Retry** in a matching model-capacity error card in a Goal chat. |
| **Auto-close notices** | On | Dismisses the supported notices listed below, including in chats without a Goal. |

Checks run about every 2.5 seconds. Goal resume attempts have a 20-second cooldown. Turn off **Monitor Goals** when you want a Goal to stay paused: the app also resumes a Goal you paused manually.

Capacity Retry applies to cards saying “Selected model is at capacity. Please try a different model.” Retry attempts are at least five seconds apart. The app waits while ChatGPT displays **Retry in Ns** and lets ChatGPT handle its countdown. A matching Retry card takes priority over Goal resume; Goal resume can follow after the card clears.

### Supported notices

| Notice | Action |
| --- | --- |
| **This content can’t be shown** — Daybreak isn’t available for Astra | Presses the notice’s **Dismiss safety banner** control. |
| **This content can’t be shown** — Daybreak Blue approval notice requesting Advanced Account Security and a FIDO2 hardware key | Presses the same named dismiss control after matching the notice text. |
| **Full access is on** | Presses the session X, **Dismiss Full access warning for this session**. |
| **Giving this request a little extra thought** | Opens **Dismiss → This time** to hide the notice for the current turn. |

Notice attempts have a five-second cooldown. The extra-thought menu opens only while ChatGPT/Codex is the foreground window and no other menu is open. Its menu may appear briefly. Dismissal uses the app’s controls without moving the mouse, typing, or activating a window. It leaves persistent “Don’t show again” preferences and permission settings unchanged.

Goalkeeper shows no notification popovers. **Check now**, or a double-click on the tray icon, runs a check immediately; it bypasses the Goal cooldown but preserves the Retry and notice cooldowns. **Start with Windows** enables launch at sign-in. **Open log** shows activity, and **Exit** stops monitoring.

## Limits and preview status

Version 0.1.0 is a preview. Matching relies on exact English accessibility labels and known notice text; desktop app changes can break detection. Goalkeeper checks loaded chats and does not navigate through your chat history. Goal and capacity Retry handling require a Goal chat.

The **Full access** and **extra-thought** additions were inspected against app source/accessibility information and compiled, but their live dismissal has not yet been verified. Use **Open log** to distinguish a confirmed action from an attempt whose resulting state could not be confirmed.

## Settings and updates

Settings and the activity log remain in `%LOCALAPPDATA%\GoalAutoResume` for compatibility with the original Goal Auto Resume app. Existing preferences are reused. The single-instance and Windows startup identities are also preserved, so exit the older app before launching Goalkeeper.

After moving the executable to a different folder, enable **Start with Windows** from the new copy to update its startup path.

The utility makes no network requests. It reads local accessibility information, saves settings and logs locally, and invokes the matched controls in the desktop app.

## Build from source

Install the .NET 10 SDK on Windows, then run in the repository or extracted release folder:

```powershell
.\build.ps1
```

The self-contained Windows x64 executable is written to `artifacts/publish/Goalkeeper.exe`.

The release ZIP includes the executable, source, build script, README, license, and SHA-256 checksums. Application source is in [`src/Goalkeeper`](src/Goalkeeper).

## License

[MIT](LICENSE). Goalkeeper is an independent utility and is not affiliated with or endorsed by OpenAI.
