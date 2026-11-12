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

---

## Features (v0.1)

- **Two side-by-side panels** with drive selector, free space, editable path bar and status line
- **Keyboard first**: everything works without a mouse (see shortcuts below)
- **Marking**: Insert / Space, mark by mask (`*.txt;*.md | *.bak`), invert, select all
- **File operations** via the Windows shell: copy, move, rename, new folder, delete to Recycle Bin or permanently
- **Built-in command line**: start typing anywhere and press Enter; `cd` navigates the active panel
- **Smart sorting**: folders first, natural order (`file2` before `file10`), click headers to sort
- Native Windows icons, hidden/system file toggle, swap panels, "target = source"

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
| `Ctrl+A` / `Ctrl+D` | Mark / unmark all | `Ctrl+H` | Show hidden files |
| `Ctrl+Enter` | Name → command line | `Ctrl+U` | Swap panels |
| `Esc` | Clear command line | `Ctrl+E` | Target = source |

## Roadmap

- [x] **v0.1 – Core**: dual panels, navigation, marking, shell file operations, command line
- [ ] **v0.2 – Operations**: own copy/move engine with background queue and progress, multi-rename tool, folder tabs, directory history and hotlist, file attributes dialog
- [ ] **v0.3 – Tools**: built-in viewer (text / hex / image), file search, compare by content, synchronize directories, quick view panel
- [ ] **v0.4 – Archives & network**: browse ZIP / 7z / TAR like folders, pack / unpack, FTP / SFTP client, checksums
- [ ] **v1.0 – Polish**: settings dialog, themes (incl. dark), customizable shortcuts and button bar, installer

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
  ChickenCommander.Core/   UI-independent logic: directory listing, sorting, masks, paths (unit tested)
  ChickenCommander/        WPF app (MVVM with CommunityToolkit.Mvvm)
    ViewModels/            Panel and main window state, commands
    Views/                 File panel control, dialogs
    Services/              Windows shell interop: icons, file operations
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
