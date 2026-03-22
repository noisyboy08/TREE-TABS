<div align="center">

# Sowser

### *Spatial browsing — your tabs live on a canvas, not in a queue.*

**A Windows desktop workspace where every site is a movable card.**  
Pan, zoom, group, and connect ideas the way you think — powered by **WPF** and **Microsoft Edge WebView2**.

<br/>

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square&logo=windows)](https://www.microsoft.com/windows)
[![WPF](https://img.shields.io/badge/UI-WPF-68217A?style=flat-square)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![WebView2](https://img.shields.io/badge/WebView2-Embedded-005FB8?style=flat-square)](https://developer.microsoft.com/microsoft-edge/webview2/)

<br/>

</div>

---

## Demo

<video src="Tree%20Taps%20.mp4" width="100%" controls></video>

[Download the Video](./Tree%20Taps%20.mp4)

---

## Why Sowser?

Traditional browsers optimize for **one column of tabs**. Sowser optimizes for **space**: research layouts, comparison grids, mood boards, and deep dives where context matters. Each page is a **card** on an infinite canvas — drag, resize, zoom out for the big picture, zoom in for detail.

> *Less alt-tabbing. More seeing everything at once.*

---

## Highlights

| | |
|:---|:---|
| **Infinite canvas** | Pan (Space + drag), zoom, fit-all, themed backgrounds that flow through the whole window chrome. |
| **Live web cards** | Full Chromium rendering via WebView2 — not screenshots, real pages. |
| **Command palette** | `Ctrl+K` to jump to cards, bookmarks, or history by typing a few characters. |
| **Workspace memory** | Save and load layouts; optional auto-save; session restore modes. |
| **Read later & clips** | Queue articles for later; capture a card to an **image clip** on the canvas. |
| **Privacy-minded options** | Optional tracker blocking; per-card profiles; suspend offscreen cards to save resources. |

---

## Feature tour

<details>
<summary><strong>Canvas & navigation</strong></summary>

- **Browser cards** — create, drag, resize, connect with lines, color-coded **groups**.
- **Sticky notes** — quick annotations alongside pages.
- **Minimap** — orientation and click-to-navigate on large layouts.
- **Global find** — `Ctrl+Shift+F` filters cards by title or URL.
- **Undo** — `Ctrl+Z` for restorative actions (e.g. bringing back a closed card).
- **Focus cycling** — `Ctrl+Tab` / `Ctrl+Shift+Tab` moves focus between cards.

</details>

<details>
<summary><strong>Browser & productivity</strong></summary>

- **Bookmarks, history, downloads** — dedicated side panels (`Ctrl+B`, `Ctrl+H`, `Ctrl+J`).
- **Quick links** — configurable shortcuts in the top bar; sensible defaults out of the box.
- **Read later list** — persisted in settings; open any entry as a new card.
- **Screenshot to canvas** — capture the current card preview as a floating **image clip**.
- **Search / URL bar** — unified entry; multiple search engines supported in settings.
- **Card context menu** — read-later, capture, profile options, and more.

</details>

<details>
<summary><strong>Look & feel</strong></summary>

- **Material Design** surfaces (MaterialDesignInXAML).
- **Canvas themes** — dark, patterns, and gradients; window, caption bar, and shell stay in sync.
- **Custom window chrome** — caption controls (minimize, maximize, close) integrated with the glass aesthetic.
- **Toasts & overlays** — command palette, shortcuts help, expanded card view.

</details>

---

## Keyboard shortcuts

Press **`?`** (Shift + `/`) in the app for the full cheat sheet. Essentials:

| Shortcut | Action |
|:--|:--|
| `Ctrl+T` | New browser card |
| `Ctrl+Shift+N` | New sticky note |
| `Ctrl+K` | Command palette |
| `Ctrl+Shift+F` | Find on canvas |
| `Ctrl+Tab` | Next card focus |
| `Ctrl+Z` | Undo |
| `Ctrl+S` / `Ctrl+O` | Save / load workspace |
| `Ctrl+Plus` / `Ctrl+Minus` / `Ctrl+0` | Zoom in / out / reset |
| `Alt+M` | Toggle main menu |

**Fit all cards** — use the **fit** control next to the zoom widget on the canvas chrome (or the equivalent command from the main menu).

*Tip: If a shortcut does not fire, focus may be inside a web page — click the app chrome or canvas and try again.*

---

## Requirements

- **OS:** Windows 10 or Windows 11 (64-bit)
- **Runtime:** [.NET 8 SDK](https://dotnet.microsoft.com/download) (to build); target users need the [.NET 8 **Desktop** Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) unless you ship a self-contained build
- **WebView2:** [Evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (often already present with Edge)

---

## Build & run

```powershell
cd tree_tapsview_main
dotnet restore
dotnet build -c Release
```

Run the executable from the build output, for example:

```text
tree_tapsview_main\bin\Release\net8.0-windows\Sowser.exe
```

Or from Visual Studio / Rider: open `Sowser.sln` and start the **Sowser** project.

### Publish for sharing

**Framework-dependent** (smaller; users install .NET 8 Desktop Runtime):

```powershell
cd tree_tapsview_main
dotnet publish -c Release -r win-x64 --self-contained false -o ..\publish\win-x64
```

**Self-contained** (larger; no separate .NET install on the target PC):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o ..\publish\win-x64-sc
```

Distribute the **entire output folder** as a ZIP (include `WebView2Loader.dll` and `runtimes` when present). WebView2-based apps are not reliably “single file only.”

---

## Landing page (Vercel)

The **Sowser app does not run on Vercel** — it is a native Windows program. The **`website/`** folder is a static **download & info** page you can host on [Vercel](https://vercel.com): set the project **root directory** to `website`, framework **Other**, no build command. Point the download button to your **GitHub Release** ZIP or Microsoft Store link.

---

## Repository layout

```text
Tree Taps/
├── README.md                 ← You are here
├── website/                  ← Static landing page for Vercel / any host
└── tree_tapsview_main/       ← WPF application (Sowser)
    ├── Sowser.sln
    ├── Sowser.csproj
    ├── MainWindow.xaml(.cs)  ← Shell, canvas, shortcuts, themes
    ├── MainWindow.FeaturePack.cs
    ├── Controls/             ← BrowserCard, ImageClipCard, …
    ├── Models/               ← Settings, cards, read-later, …
    └── Services/             ← Persistence, blocking, bookmarks IO, …
```

---

## Configuration

Settings are persisted locally (e.g. default search engine, canvas theme, auto-save interval, tracker blocking, **read later** list, custom quick links, default browser profile). Paths and formats are handled by `AppSettingsStore` — your data stays on your machine unless you sync the profile folder yourself.

---

## Roadmap ideas

- Cross-platform or web companion (would be a separate product surface)
- Cloud sync for workspaces (opt-in, encrypted)
- Extension model or user scripts
- Deeper accessibility (narrator, high-contrast themes)

---

## Contributing

Issues and pull requests are welcome. Please keep changes focused, match existing naming and patterns, and verify `dotnet build` before submitting.

---

## Acknowledgments

Built with **WPF**, **MaterialDesignInXAML**, and **WebView2**. Thanks to the teams behind .NET and the Chromium-based WebView2 runtime.

---

<div align="center">

**Sowser** — *think in space, not in tabs.*

<br/>

<sub>If this project helped you, consider starring the repo and sharing your favorite workflow.</sub>

</div>
