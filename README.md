# Backloggd Logger

Windows tray app. Logs the games you play on [Backloggd](https://backloggd.com) and adds your play time to the journal.

## Use

1. Run it, then tray > **Open Backloggd window** and sign in.
2. Play. Steam, Epic, GOG and emulator games are detected. Add anything else with tray > **Track a running game...**
3. When a game closes, a popup offers **Open log** (log box with status and dates filled in) or **Mark as** (status only).

## Saved without asking

- Each day's play time, in the game's journal
- **Sync sessions**, turned on for that log
- Your **Mark as** choice

Rating, review, likes and notes are never touched.

## Build

.NET 10 SDK and WebView2 Runtime.

```
dotnet test
dotnet publish src/BackloggdLogger -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
