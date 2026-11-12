<p align="center">
  <img src="src/ChickenCommander/Assets/chicken.png" width="128" alt="Chicken Commander logo" />
</p>

<h1 align="center">Chicken Commander</h1>

<p align="center">
  A fast, keyboard-driven, dual-pane file manager for Windows.<br/>
  Built with C# / WPF on .NET 10, inspired by classic orthodox file managers.
</p>

<p align="center">
  <img alt="build" src="https://img.shields.io/badge/.NET-10-512BD4" />
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows-0078D6" />
  <img alt="license" src="https://img.shields.io/badge/license-MIT-green" />
</p>

<p align="center">
  <a href="https://github.com/luciangrozea/chicken-commander/releases/latest"><b>â¬‡ Download the latest release</b></a>
</p>

<p align="center">
  <img src="assets/screenshot.png" width="900" alt="Chicken Commander screenshot" />
</p>

---

## Features

**Panels & navigation**
- Two side-by-side panels with drive selector, free space, editable path bar and status line
- **Folder tabs** in each panel; tabs, panels, window size and hotlist are remembered between sessions
- **Back / forward history** per tab and a recent-folders menu
- **Directory hotlist** for favourite folders
- Keyboard first: everything works without a mouse (see shortcuts below)
- Smart sorting: folders first, natural order (`file2` before `file10`), click headers to sort

**File operations**
- **Own copy / move engine** running in the background: a queue of jobs with per-file and total progress, speed, time left, pause / resume and cancel
- Conflict handling: overwrite, skip, overwrite older, rename (keep both), each also "for all"
- Same-volume moves are instant renames; timestamps and attributes are preserved; half-written files are never left behind
- **Multi-Rename tool** with live preview: masks (`[N]`, `[N1-3]`, `[E]`, `[C]` counter, `[YMD]`, `[P]`â€¦), search & replace with regex, case conversion, collision detection, undo
- **Attributes dialog**: read-only / archive / hidden / system (three-state for mixed selections), modification date, recursive
- Delete to Recycle Bin or permanently, new folder, rename in place

**Marking & command line**
- Insert / Space, mark by mask (`*.txt;*.md | *.bak`), invert, select all
- Built-in command line: start typing anywhere and press Enter; `cd` navigates the active panel

## Keyboard shortcuts

| Key | Action | Key | Action |
|---|---|---|---|
| `Tab` | Switch panel | `F3` | View |
| `Enter` | Open folder / run file | `F4` | Edit |
| `Backspace` | Parent folder | `F5` | Copy |
| `Ctrl+\` | Drive root | `F6` | Move / rename |
| `Alt+F1` / `Alt+F2` | Choose drive | `Shift+F6` | Rename in place |
| `Ctrl+L` | Edit path | `F7` | New folder |
| `Insert` / `Space` | Mark item | `F8` / `Del` | Delete (Recycle Bin) |
| `Num +` / `Num -` | Mark / unmark by mask | `Shift+Del` | Delete permanently |
| `Num *` | Invert marks | `Ctrl+R` | Refresh |
| `Ctrl+A` / `Ctrl+Shift+A` | Mark / unmark all | `Ctrl+H` | Show hidden files |
| `Ctrl+Enter` | Name â†’ command line | `Ctrl+U` | Swap panels |
| `Esc` | Clear command line | `Ctrl+E` | Target = source |
| `Ctrl+T` / `Ctrl+W` | New / close tab | `Ctrl+M` | Multi-Rename tool |
| `Ctrl+Tab` | Next tab (`Shift`: previous) | `Ctrl+I` | Change attributes |
| `Ctrl+Up` | Open folder in new tab | `Ctrl+D` | Directory hotlist |
| `Alt+Left` / `Alt+Right` | Back / forward | `Alt+Down` | Folder history |
| `Ctrl+Shift+O` | File operations window | | |

## Roadmap

- [x] **v0.1 â€“ Core**: dual panels, navigation, marking, shell file operations, command line
- [x] **v0.2 â€“ Operations**: own copy/move engine with background queue and progress, multi-rename tool, folder tabs, directory history and hotlist, file attributes dialog, saved settings
- [ ] **v0.3 â€“ Tools**: built-in viewer (text / hex / image), file search, compare by content, synchronize directories, quick view panel
- [ ] **v0.4 â€“ Archives & network**: browse ZIP / 7z / TAR like folders, pack / unpack, FTP / SFTP client, checksums
- [ ] **v1.0 â€“ Polish**: settings dialog, themes (incl. dark), customizable shortcuts and button bar, installer

## Building

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
dotnet run --project src/ChickenCommander
```

Publish a single self-contained executable:

```bash
dotnet publish src/ChickenCommander -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Project structure

```
src/
  ChickenCommander.Core/   UI-independent logic (unit tested)
    FileSystem/            Directory listing, sorting, masks, paths, navigation history
    Operations/            Copy/move engine, multi-rename, attributes
    Settings/              JSON settings store
  ChickenCommander/        WPF app (MVVM with CommunityToolkit.Mvvm)
    ViewModels/            Panel and main window state, commands
    Views/                 File panel control, dialogs
    Services/              Operation queue, Windows shell interop (icons, delete to Recycle Bin)
tests/
  ChickenCommander.Core.Tests/
tools/
  make_icon.py             Generates the .ico / .png from the logo artwork
```

## Disclaimer

Chicken Commander is an independent project. It is not affiliated with or endorsed by the authors of any
other file manager; it only follows the well-known dual-pane, keyboard-centric workflow.

## License

[MIT](LICENSE)
