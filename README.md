# Veteran Soldiers 1.0.1 — source for Nexus moderation review

**Creator and system designer: Kirapp.** This is the complete own-source snapshot for the two assemblies and optional save-transfer utility in Veteran Soldiers 1.0.1, Nexus mod **14**, Main file **31**, for *Diplomacy is Not an Option*.

- [Build instructions](BUILD.md): prerequisites, exact dependency layout, Release build, all eight solution test suites and the separate transfer-tool tests.
- [Review map](REVIEW.md): entry points, persistence, instrumentation, file access, AI disclosure and exclusions.
- [Original player documentation and permissions](player-package/CREDITS_AND_PERMISSIONS.md).
- `SOURCE-MANIFEST.json`: exact source and documentation hashes and provenance.

## Contents and provenance

`src/` contains the runtime and preloader patcher, including the source of the development harnesses that are excluded from the public build. `tests/` contains the eight solution test projects and the optional transfer utility's regression script. `scripts/Build.ps1` is the original build entry point. `scripts/Transfer-VeteranSave.ps1` and its unchanged copy under `player-package/` are the plain-text utility shipped with file 31.

The C# solution, projects, build properties and build entry point originate from local implementation commit `a3cae5f77d3205c50ba06588483573eb5a8e9dce`. They are unchanged from the previously supplied private source archive, SHA-256 `B4D399E686645058F46041C072A13D32CC2859EA9D927DA69D16289510623AF6`. The player-package files are extracted unchanged from the frozen Nexus ZIP. Review instructions and the manifest are prepared separately for this source snapshot. Its initial Git commit, if created, records this import; it is not a reconstruction of earlier development history.

The original local development repository and its history are not part of this snapshot. Proprietary game assemblies, decompiled game source, third-party loader/SDK binaries, compiled mod DLLs, the personal USB installer, saves, live configuration, private correspondence and local logs are excluded. External build dependencies must be obtained separately.

## Frozen submission

- Nexus page: https://www.nexusmods.com/diplomacyisnotanoption/mods/14
- Submitted archive: `VeteranSoldiers-1.0.1-Nexus.zip`, 169,528 bytes, 10 entries.
- ZIP SHA-256: `4497702915363F31F1EF5425903D38C8327A7DE3CBBD6EB613273E5C69950277`.
- Runtime SHA-256: `8859956744FB5863A18FD9DD07BC460B4C041DA52C0C5E557D7F064AF0055526`.
- Patcher SHA-256: `6830409958812BB512387A7EC65A9AF8F3FFB880EE309490EA8AFE45B9D0715D`.

Source access does not grant a public redistribution, modification or port license. Existing permissions and third-party rights remain as stated in the original player notice. Development used OpenAI Codex substantially under Kirapp's direction; see [REVIEW.md](REVIEW.md). This snapshot is review material, not a replacement download or an indication of Nexus approval.
