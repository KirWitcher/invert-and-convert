# Rename

Rename is a portable Windows utility for batch file renaming and image conversion. It processes files locally and uses a Russian-language interface.

## Download

Get **Rename.exe** from the [latest release](https://github.com/KirWitcher/rename-windows/releases/latest), then run it on 64-bit Windows with .NET Framework 4.8.

## Batch renaming

- Preview names before applying changes.
- Number files from **1**, oldest first by Windows file creation time, or generate unique names from letters and symbols.
- Select a range with Shift, select individual files with Ctrl, or select all files.
- Back up the whole folder or the selected files. The app keeps a recovery journal during renaming.

## Image conversion

- Choose **PNG**, **JPG** (`.jpg`), or **JPEG** (`.jpeg`).
- Convert WebP files, including WebP images with a `.web` extension, using the bundled decoder. The format help button lists other supported inputs. HEIC, HEIF, and AVIF depend on the installed Windows codecs.
- The app verifies the new file before removing the original. If a name is taken, it adds a numeric suffix. Use **Make a backup** (Сделать резервную копию) to save the selected originals before conversion.
- PNG preserves transparency and adds no lossy compression. Encoding as JPG or JPEG is lossy and fills transparent areas with white. Changing an existing JPEG between `.jpg` and `.jpeg` preserves its bytes. Metadata may be dropped when re-encoding.
- GIF, video, and animated images are excluded from conversion. GIF and MP4 remain available for renaming.

## Build

On 64-bit Windows with .NET Framework 4.8, run the following from the repository root. The build downloads the official libwebp Windows x64 tools and embeds the decoder and its notices in the executable.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The output is `dist\Rename.exe`. libwebp's [license](WebP-LICENSE) and [patent grant](WebP-PATENTS) are also available from the app's WebP license link.

