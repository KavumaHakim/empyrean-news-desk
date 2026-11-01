# NEWS DESK — Professional Broadcast & Projector Presentation Suite

**Target Platform:** Windows 10 & Windows 11  
**Architecture:** C# WPF + .NET 8 LTS + Microsoft WebView2 + SQLite + Embedded HTTP Loopback Server  
**Display Pipeline:** Dual-monitor Windows Extended Display (Private Operator Dashboard on Laptop + Borderless Fullscreen Output on HDMI Projector)

---

## Quick start on a new computer

1. Download this repository (green **Code** button → **Download ZIP**, then unzip), or `git clone` it.
2. Double-click **`SETUP.bat`**. It installs what's missing with `winget` (the .NET 8 SDK, the Microsoft Edge WebView2 Runtime and FFmpeg), builds the app, and puts a **NEWS DESK** shortcut on the desktop. You can run it again at any time.
3. Open NEWS DESK from the shortcut (or `LAUNCH_NEWS_DESK.bat`, which rebuilds first).

### Bringing a show over from another computer

Your stories, titles, lower thirds, ticker, settings and media are **not** in this repository. They stay on the computer that made them. To move them:

- On the old computer: **SETUP → MOVE THE SHOW → SAVE SHOW TO FILE…** writes one `.newsdesk` file with the database and every video, picture and sound the show uses. It can be several GB, so a USB drive works well.
- On the new computer: **SETUP → MOVE THE SHOW → OPEN SHOW FILE…**, then choose a folder for the media. NEWS DESK restarts with the show exactly as it was. Whatever show was there before is kept in `%LocalAppData%\NewsDesk\Backups`.

### Sound to the projector

**SETUP → SOUND OUTPUT** picks where all show sound plays (the HDMI projector or the laptop speakers). The 🔊 button on the AUDIO MIXER header flips between the two.

---

## 1. System Overview

NEWS DESK is a television-style news broadcast control application engineered for manual news presentation on a projector or secondary monitor connected via HDMI. 

### Key Architectural Highlights
- **Operator Control Room (Laptop):** Dark navy newsroom UI with restrained red highlights. Includes Program Monitor (On Air), Preview Monitor (Next Up), Big Red `TAKE LIVE (F5)` button, Media Library, News Rundown Playlist, Headlines & Graphics Studio, News Ticker Studio, Transitions Manager, and Breaking News bar.
- **Audience-Facing Broadcast Output (Projector):** Separate borderless fullscreen WPF window hosting Microsoft WebView2. Has zero playback controls, hidden mouse cursor, and no menus.
- **Hardware-Accelerated Dual-Buffer Scenes:** Outgoing and incoming scenes are rendered on dedicated alternating slots (`#scene-a` and `#scene-b`), enabling smooth visual transitions between videos and graphic slides.
- **Strict 500ms Transition Timing:** All 6 animated transitions (`Fade`, `Dissolve`, `Slide Left`, `Slide Right`, `Wipe`, `Zoom`) run at exactly **500 milliseconds**. `Cut` switches instantaneously (**0 milliseconds**).
- **Audio Crossfader:** Over 500ms transitions, outgoing audio linearly fades out (1.0 → 0.0) while incoming audio fades in (0.0 → 1.0), preventing doubled audio or harsh pops.
- **Continuous Overlays:**
  - **LIVE Indicator:** Fixed top-right corner with pulsating white dot and red badge. Remains visible and stable throughout all transitions.
  - **Moving News Ticker:** Positioned at the bottom ribbon. Uses a continuous double-track CSS loop that never stutters, resets, or disappears when scenes change. Draft ticker changes can be prepared and published live without stopping video playback.
  - **Lower Thirds:** Glassmorphic news graphics supporting categories, locations, breaking tags, and reporter/interviewee lower-thirds.
- **Embedded Loopback Media Service:** Built-in `HttpListener` on `127.0.0.1:8089` supporting HTTP 206 Byte-Range requests (`Range: bytes=start-end`), directory traversal protection, and library path whitelisting for video seeking and playback.
- **Local SQLite Database:** Embedded database stored at `%LocalAppData%\NewsDesk\newsdesk.db` managing media metadata, per-video transition assignments, playlists, graphic drafts, ticker items, and system settings.

---

## 2. Solution Structure

```
c:\Users\SHAMI\HAKIM\NEWS APP\
├── NewsDesk.sln
├── NewsDesk.Core/                     # Core Business Logic & Data
│   ├── Data/
│   │   └── DatabaseService.cs         # SQLite schema, seed data, and CRUD
│   ├── Models/
│   │   ├── TransitionType.cs          # The 7 transitions & 500ms timing
│   │   ├── MediaItem.cs               # Media library & category models
│   │   ├── HeadlineDraft.cs           # Graphics studio template models
│   │   ├── TickerModels.cs            # Draft & Live ticker configurations
│   │   ├── PlaylistItem.cs            # News rundown playlist model
│   │   └── BroadcastMessages.cs       # Structured host-to-page JSON messaging
│   ├── Server/
│   │   └── MediaHttpServer.cs         # Loopback server with Byte-Range seeking
│   └── Services/
│       └── BroadcastStateManager.cs   # Authoritative broadcast state controller
│
├── NewsDesk.App/                      # WPF Operator Dashboard & Broadcast Window
│   ├── BroadcastCanvas/               # WebView2 Broadcast Web Assets
│   │   ├── index.html                 # Canvas markup, dual-buffer scene slots
│   │   ├── broadcast.css              # 500ms animations, ticker, live badge
│   │   └── broadcast.js               # Audio crossfade, playback, interop
│   ├── Services/
│   │   └── DisplayManager.cs          # Win32 display detection & HDMI manager
│   ├── ViewModels/
│   │   ├── MainViewModel.cs           # MVVM controller for operator studio
│   │   └── ViewModelBase.cs           # INotifyPropertyChanged & RelayCommand
│   ├── Views/
│   │   ├── BroadcastWindow.xaml       # Fullscreen borderless projector window
│   │   └── BroadcastWindow.xaml.cs    # WebView2 lifecycle & message dispatch
│   ├── MainWindow.xaml                # Dark-themed operator control dashboard
│   └── MainWindow.xaml.cs             # Application entry and keyboard shortcuts
│
└── NewsDesk.Tests/                    # Automated Unit Test Suite (xUnit)
    └── NewsDeskTests.cs               # Verification of transitions, DB, and state
```

---

## 3. How to Build & Run

### Prerequisites
- .NET 8.0 SDK or later (`dotnet --version`)
- Windows 10 or Windows 11
- Microsoft Edge WebView2 Runtime (pre-installed on Windows 10/11)

### Build Solution
```powershell
dotnet build NewsDesk.sln
```

### Run Automated Tests
```powershell
dotnet test NewsDesk.sln
```
*All tests should pass.*

### Launch Operator Control Room
```powershell
dotnet run --project NewsDesk.App\NewsDesk.App.csproj
```

---

## 4. HDMI Projector Setup & Operation

1. Connect the laptop to your projector or TV using an HDMI cable.
2. Press `Win + P` on Windows and select **"Extend"** (Extended Desktop Mode).
3. Open **NEWS DESK**. In the top bar:
   - Select the projector from the **"PROJECTOR / HDMI DISPLAY"** dropdown.
   - Click **"LAUNCH PROJECTOR"**.
   - The borderless fullscreen broadcast window appears on the projector with the Standby Slate and real-time clock.
4. **Broadcast Operations:**
   - **Load a Video/Story:** Go to the *Media Library* or *News Rundown*, select a story, and click **"LOAD TO PREVIEW"**.
   - **Take Live:** Review the item in the PREVIEW monitor, select the transition (or use the video's assigned transition), and click the big red **"★ TAKE LIVE (F5)"** button.
   - **Transition:** The video transitions on the projector with the exact 500ms effect and audio crossfade, while the ticker and LIVE indicator continue uninterrupted.
   - **Play/Pause/Scrub:** Use the transport bar or press `Space` to play/pause.
   - **Headlines & Lower Thirds:** Prepare titles in the *Headlines & Graphics Studio*, click **"TAKE LIVE"** or **"UPDATE LIVE"**.
   - **News Ticker:** Edit ticker headlines in the *News Ticker Studio*. Click **"PUBLISH TICKER TO AIR"** to update the audience crawl in real-time without stopping video playback.
   - **Breaking News:** Enter urgent text in the red Breaking News bar and click **"TAKE BREAKING LIVE (F7)"**.

---

## 5. Keyboard Shortcuts Quick Reference

| Key | Action | Description |
|---|---|---|
| **F5** | **TAKE LIVE** | Switches prepared preview content to on-air via assigned transition |
| **F6** | **TOGGLE STANDBY** | Toggles station standby slate on the projector |
| **F7** | **TAKE BREAKING LIVE**| Instantly triggers Breaking News lower-third and urgent red ticker |
| **Space** | **PLAY / PAUSE** | Toggles playback of the current on-air video |
| **Esc** | **EMERGENCY STANDBY**| Instantly cuts to standby screen |
