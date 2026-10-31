# PawnIO modules

`LpcACPIEC.bin` is the unmodified, signed `LpcACPIEC` module from
[namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) release 0.2.11 (`release_0_2_11.zip`).
It exposes byte reads and writes on the ACPI EC ports 0x62/0x66 and is used by `EcLadder.cs`.

- SHA-256: `C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906`
- License: LGPL-2.1-or-later, full text in [PawnIO.Modules.COPYING](PawnIO.Modules.COPYING).
- Corresponding source: [PawnIO.Modules 0.2.11 source archive](https://github.com/namazso/PawnIO.Modules/archive/refs/tags/0.2.11.zip), including `LpcACPIEC.p` and build files.
- Release ZIPs include that archive as `third_party/PawnIO.Modules-0.2.11-source.zip`.
- Requires the PawnIO driver (`C:\Program Files\PawnIO\PawnIOLib.dll`).

## RyzenSMU.bin

`RyzenSMU.bin` is the unmodified signed module from the same 0.2.11 release.
Its SHA-256 was checked against the official release archive:
`301D9CA397108E09F31BFBD5AC4C9BB4F352A5DE68532C32DB3BA7DDCDE93450`.
It provides the SMU register and mailbox access used by the experimental CPU
tuning feature. The same LGPL-2.1-or-later license and bundled source archive
apply; that archive includes `RyzenSMU.p` and build files. It requires PawnIO.
