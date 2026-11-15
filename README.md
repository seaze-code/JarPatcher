# JarStudio (`JarPatcher`)

A binary-safe Java Archive (`.jar`) Constant Pool string editor, embedded config/resource patcher, and live bytecode inspector for Windows.

JarStudio patches `CONSTANT_String` / `CONSTANT_Utf8` entries directly inside compiled Java `.class` files (`0xCAFEBABE`) without recompiling source code or requiring build dependencies.

---

## Core Capabilities

- **Binary-Safe Bytecode String Patching (`0xCAFEBABE`)**
  - Parses the full JVM Constant Pool (`CONSTANT_Utf8`, `CONSTANT_String`, `CONSTANT_Class`, `CONSTANT_Fieldref`, `CONSTANT_Methodref`, `CONSTANT_InterfaceMethodref`, `CONSTANT_NameAndType`, `CONSTANT_InvokeDynamic`, `CONSTANT_Dynamic`, etc.).
  - Encodes and decodes strings using spec-compliant **JVM Modified UTF-8** (supporting Thai script, Unicode symbols, and Minecraft formatting characters `§`).
  - **Shared-Slot Protection**: If a `CONSTANT_String` literal shares its `CONSTANT_Utf8` slot with a class name, method name, field name, or type descriptor, JarStudio leaves the original `CONSTANT_Utf8` slot intact and appends a dedicated `CONSTANT_Utf8` entry at the end of the Constant Pool so method/field linkage is never broken.
- **Global Constant Pool Search & Batch Find/Replace**
  - Indexes all `CONSTANT_String` literals across every `.class` file in milliseconds.
  - Filter by category (`Minecraft §/&`, `URLs & Hosts`, `Commands / Keys`, `Modified Only`) or scope to a specific package or class in the archive tree.
  - Batch Find & Replace (supporting case-sensitive and Regular Expression matching) plus one-click `& -> §` legacy color code conversion.
- **Live Minecraft Tooltip & Hex Color Renderer**
  - Real-time preview of `§0`–`§f`, `&0`–`&f`, `&#RRGGBB` hex colors, bold (`§l`), italic (`§o`), underline (`§n`), and strikethrough (`§m`).
- **Embedded Config & Asset Patching**
  - Edit `.yml`, `.yaml`, `.json`, `.properties`, `.toml`, `.mcmeta`, `.txt`, and `MANIFEST.MF` files in-place.
  - Preview and replace embedded `.png` / `.jpg` / `.gif` image textures directly inside the archive.
  - Automatically strips stale JAR signature block files (`META-INF/*.SF`, `*.RSA`, `*.DSA`, `*.EC`) when saving modified archives to prevent `SecurityException` digest errors at runtime.
- **Live Vineflower Decompiler Integration**
  - Decompile any `.class` file (including its current in-memory Constant Pool patches and inner classes) using Vineflower 1.12.0 directly inside the workbench.

---

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl + O` | Open `.jar` archive |
| `Ctrl + S` | Save changes to current `.jar` |
| `Ctrl + Shift + S` | Export patched archive as new `.jar` |
| `Ctrl + F` | Focus Constant Pool search bar |
| `Ctrl + H` | Toggle Batch Find & Replace bar |

---

## Headless CLI Usage

JarStudio also supports automated command-line batch patching:

```powershell
JarPatcher.exe --cli-patch <input.jar> <output.jar> "<find>" "<replace>"
```

---

## Building from Source

Requires Windows `.NET Framework 4.8` (`MSBuild.exe`):

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe JarPatcher.csproj /p:Configuration=Release /p:Platform=x64
```
