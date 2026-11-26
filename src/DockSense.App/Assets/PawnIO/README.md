# PawnIO module

`RyzenSMU.bin` is the signed AMD SMU module from [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)
release **0.2.12** (`release_0_2_12.zip`), unchanged. It is licensed under the LGPL-2.1 (`COPYING`); the source is
`RyzenSMU.p` in that repository at tag `0.2.12`.

SHA-256: `301d9ca397108e09f31bfbd5ac4c9bb4f352a5de68532c32db3ba7ddcde93450`

DockSense loads it into the [PawnIO](https://pawnio.eu/) driver to set the processor's power limits
(`Platform/TdpWriter.cs`). The driver itself is not part of DockSense: the user installs it. PawnIO only loads
modules that carry its author's signature, so the file cannot be replaced by a self-built one.
