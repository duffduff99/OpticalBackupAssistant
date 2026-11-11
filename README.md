# Optical Backup Assistant

A Windows program for long-term, off-site backups of photos and documents onto
optical discs (CD, DVD, Blu-ray, M-DISC).

- Point it at a folder (local drive, mapped drive or `\\server\share`).
- Choose a disc type. The backup is split across as many discs as it needs.
- It remembers what is already on disc, so later runs write only what is new or changed.
- Discs hold **ordinary files in their original folders**. Restoring needs no software.
- Each disc carries checksums and PAR2 recovery data, so a damaged disc can be detected and repaired.

The program produces one `.iso` image per disc. You burn the images with Windows'
built-in Disc Image Burner (there is a button for it) or any burning program such as ImgBurn.

## Getting the program

Every push builds `OpticalBackupAssistant.exe` (a single self-contained file; nothing to
install) on GitHub Actions: open the latest run of the **Build** workflow and download the
`OpticalBackupAssistant-win-x64` artifact. Pushing a tag such as `v0.1.0` attaches the
exe to a GitHub release.

To build it yourself (needs the .NET 8 SDK):

```
dotnet publish src/OpticalBackup.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Using it

**Back up**

1. Give the job a name (for example `Photos`) and choose the folder, disc type and an output folder with enough free space.
2. **Scan and plan** reads the folder and shows how many discs are needed and anything that will be skipped.
3. **Create discs** writes the images.
4. Burn each image, write its label on the disc (for example `PHOTOS_R001_D03`), then check it on the **Verify disc** tab.

Next quarter, pick the same job and repeat. Only new and changed files are written.

**Restore** rebuilds the folder exactly as it was at the last backup: choose the job and a
destination, insert each listed disc in any order and press Restore. On a new computer, first
use **Catalog > Load catalog from a disc** with your most recent disc.

**Without the program**, copy the `Files` folder of every disc into one destination folder,
oldest run first. `README.txt` on every disc explains this and how to repair a damaged disc.

## How it behaves

| Topic | Behaviour |
|---|---|
| Change detection | By content (SHA-256). A renamed or moved file is not written again. Files whose size and modified time are unchanged are not re-read unless you tick "Re-read every file". |
| Changed files | Written again as a new version; the old version stays on its disc. |
| Deleted files | Noted in the catalog. Nothing is ever removed from a disc. |
| Identical files | Stored once. Other locations are listed in `BackupInfo/disc.json` and recreated by Restore. |
| Files larger than one disc | Not split. They are listed as a warning and skipped until you choose a bigger disc type. |
| Skipped automatically | `Thumbs.db`, `desktop.ini`, `.DS_Store`, recycle-bin and NAS index folders, links to other folders. |
| Interrupted run | Finished discs stay recorded. Scan again to plan the rest. |
| Source changes mid-run | Every file is checked against its checksum as it is written; a mismatch stops that disc. |

### The catalog

The catalog (`catalog.json`) is the program's memory. By default it is in
`Documents\Optical Backup Assistant`; the Catalog tab can move it, for example to a NAS or a
synced folder. Every disc also carries a compressed copy of the catalog as of that disc, so
the most recent disc is always enough to rebuild it.

### What is on a disc

```
README.txt                  plain-language restore and repair instructions
SHA256SUMS.txt              checksums, usable with `sha256sum -c`
recovery-001.par2 ...       PAR2 recovery data (repair with MultiPar or par2cmdline)
BackupInfo/disc.json        list of the files on this disc
BackupInfo/catalog.json.gz  copy of the catalog
Files/...                   your files, in their original folder structure
```

Images use the UDF 1.02 file system (long Unicode names, files over 4 GB), which Windows,
macOS and Linux read natively.

### Disc sizes

| Type | Space used |
|---|---|
| CD-R 650 / 700 MB | 650 / 703 MiB |
| DVD±R, DVD±R DL | 4.38 / 7.96 GiB |
| BD-R 25 / 50 GB | 22.6 / 45.1 GiB |
| BD-R XL 100 / 128 GB | 91.0 / 116.2 GiB |

2 MB is left free on every disc as a margin. The Blu-ray figures assume the burning program
formats the disc with spare areas (the usual default), which costs about 3%. The BD-R 25 and
50 figures are the standard ones; the BDXL figures are extrapolated from them, so for a first
BDXL burn check the image size against what your burning program reports. "Custom size" lets
you enter any capacity.

## Limits worth knowing

- The program does not burn discs itself, and cannot repair a damaged disc itself: it tells you
  which files are bad, and MultiPar or `par2` does the repair from the `.par2` files.
- Recovery data protects against scratches and partial rot on a disc. It does not replace a
  disc that is lost or completely unreadable.
- There is no encryption. Anyone holding a disc can read it.
- Creating discs reads the source three times (checksum, recovery data, image), so a large
  first backup over a network share takes a while.

## Command line

`src/OpticalBackup.Cli` builds `obackup`, a cross-platform command-line front end to the same
engine (`backup`, `verify`, `restore`, `status`, `list`, `import`). Run it without arguments for usage.

## Development

```
dotnet test tests/OpticalBackup.Tests
```

When `par2` and `7z` are installed, the tests also repair deliberately damaged files with
par2cmdline and extract the generated images with 7-Zip.
