# Third-party programs in NexusPC-Setup.exe

CI (`.github/workflows/ci.yml`, job "installer") downloads these exact files, checks their SHA-256 and puts
them into the installer unmodified. Nothing here is compiled into Nexus PC.

| Program | Version | File | SHA-256 | License |
|---------|---------|------|---------|---------|
| OpenRGB | 1.1 (8e16de2) | https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_Windows_64_8e16de2.zip | `63291726c6fc84eb479ef2ee752e97e1187f6ce9c49fd27eee40ded588a8172a` | GPL-2.0 ([OpenRGB/LICENSE](OpenRGB/LICENSE)) |
| PawnIO setup | 2.2.0 | https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe | `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032` | GPL-2.0 driver; the signed installer states "This installer can be redistributed unmodified." |

**OpenRGB** runs as a separate program (Program Files\NexusPC\OpenRGB), started by the Nexus PC service as
its SDK server on 127.0.0.1. Source code of this exact version:
https://codeberg.org/OpenRGB/OpenRGB/src/tag/release_1.1 (archive:
https://codeberg.org/OpenRGB/OpenRGB/archive/release_1.1.tar.gz). A copy of this file and the license is
installed next to OpenRGB.exe.

**PawnIO** (https://pawnio.eu, source https://github.com/namazso/PawnIO) is installed by its own installer
with `-install -silent` if it is not installed yet. It stays installed when Nexus PC is removed.

To update a version: change the URL and SHA-256 here and in ci.yml together.
