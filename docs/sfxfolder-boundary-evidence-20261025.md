# SfxFolderMod native boundary evidence (2026-10-25)

The installed `SfxFolderMod` 1.7.3 build was tested in the disposable AA lab
`artifacts/native-lab-20261025-15`. The probe was read-only: it did not open
SoundExplorer, invoke the Mod's search/filter methods, change configuration,
play or stop audio, or write Mod data.

## Static identity and IL audit

- Assembly: `SfxFolderMod.dll`, version `1.7.3.0`
- MVID: `5da3b0cd-f4f3-421e-bc88-3ee5d6206c87`
- SHA-256: `29CA0E83813704E07FE610060B177A85AB6B28EAB7FD9805E796D85B5D8B25D3`
- ModClockAudit: 0 direct wall/Unity/audio clock call sites
- IL-only I/O: `Application.persistentDataPath`, `Directory.CreateDirectory`,
  `File.Exists`, `File.WriteAllText`, and `File.ReadAllLines` are confined to
  the embedded sound-name table and optional `config.txt` under the Mod's
  `SfxFolderMod` data directory.
- Harmony targets are UI/data paths only:
  `SoundExplorer.Start`, `SoundItem.Init`,
  `ScenarioResourceManager.GetSoundNames`, and `SoundExplorer.Filter`.
  The IL contains no `AudioSource`, `AudioManager`, `Play`, `Stop`, or audio
  clip loading call sites.
- `Patch_GetSoundNames.OnNames` filters the returned string list using the
  current SoundExplorer query and the in-memory Chinese-name dictionary; it
  does not resolve or load an audio clip. `Patch_SoundItemInit` only updates a
  label and layout.

The static result cannot prove IL2CPP/native behavior, so the Mod remains
`Opaque` until a native story interaction and two-pass audio/PTS test exists.

## Native read-only ownership probe

Evidence: `artifacts/acceptance-20261025-68-sfxfolder-boundary/`.

The probe waited for `CatalogScene`, then sampled before, during and after a
short `RenderControlV1` lease. All three snapshots were stable:

- `data_loaded=True`, `item_count=774`;
- Harmony handle installed;
- `SfxSearch.Explorer` unbound in CatalogScene;
- 122 process threads;
- 3 AudioSources, 3 active, 0 playing.

Result: `PATH-CHECK RESULT 1 passed; 0 failed`. Ownership was restored after
the lease. This is a bounded observation only: it does not certify behavior
after opening SoundExplorer or when an actual sound is triggered.

## Admission decision

Keep SfxFolderMod at `Opaque`. The evidence supports a likely editor display and
resource-name lookup scope, but strict export requires a story-path probe that
opens the sound browser, triggers an effect, and performs two independent
exports with identical event/PCM/PTS results. No source, configuration, or
runtime files belonging to SfxFolderMod were changed.
