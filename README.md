# SourceRelay

SourceRelay is a local Windows/WPF utility for handing a deliberately limited set of source files to an existing ChatGPT conversation and safely reviewing the returned edits. It has no OpenAI, ChatGPT, Git, GitHub, or Perforce API integration. The user remains responsible for manually moving the ZIP to and from the conversation.

> **Security invariant: SourceRelay may only apply returned changes to files that the user explicitly included as Editable in the corresponding original bundle.**
>
> **Context-only files may be included in the outbound bundle for reference, but SourceRelay will reject returned modifications to them.**

## Workflow

1. Choose the Unity/game project root and expand the lazy file tree.
2. Tick only the files you want to disclose. Included files default to **Editable**; change the per-file selector to **Context** when the file is reference-only.
3. Select **Generate Bundle**. SourceRelay writes the ZIP outside the source tree and copies this handoff message:

   `Apply the changes we agreed above to the attached SourceRelay bundle. Follow INSTRUCTIONS.md for the bundle and return format.`

4. Drag the ZIP into the existing conversation, press Ctrl+V, and send. The conversation—not SourceRelay—defines the coding task.
5. Drag the returned ZIP onto SourceRelay, open it with **Open Returned Bundle**, or let the passive Downloads watcher recognise it.
6. Review classifications and the simple line comparison. Explicitly tick and apply valid changes. Nothing is ever auto-applied.
7. Use the persistent multi-level **Undo** and **Redo** history if needed.

## Bundle protocol

```text
SourceRelay_<bundle-id>.zip
├── INSTRUCTIONS.md
├── manifest.json
└── files/
    └── <original relative paths>
```

The versioned manifest contains the bundle ID, project display name, relative paths, editable/context mode, byte size, and original SHA-256. It never contains the absolute project path. A private per-user record maps that ID back to the project root. There is deliberately no `TASK.md`; task intent stays in the conversation.

## Validation and safety

Returned archives are treated as untrusted. SourceRelay checks format/version and known bundle identity; exact manifest identity; entry and expanded-size limits; absolute and traversal paths; duplicate and case-colliding paths; unexpected, missing, and context-only changes; and containment beneath the recorded project root. It reads entries individually and never extracts an archive into the project.

Every returned hash is compared with both the export hash and current local file. If the local file changed after export, SourceRelay requires explicit overwrite confirmation. Read-only destinations are rejected with guidance to check the file out in Perforce; SourceRelay neither invokes `p4` nor changes the read-only flag. Writes use a sibling temporary file followed by replacement.

Undo history stores exact pre- and post-Apply data under the current user's local application data, defaults to 20 Apply operations, survives restart, and detects external changes before Undo/Redo overwrite. Settings, bundle records, and history live under `%LOCALAPPDATA%\SourceRelay`. Output defaults to `%USERPROFILE%\Documents\SourceRelay`; monitoring defaults to the user's Downloads folder.

Default tree exclusions include `.git`, `.vs`, `Library`, `Temp`, `Logs`, `obj`, `bin`, `.meta`, `.env`, common key/certificate/keystore extensions, and obviously named credential/secret files. These are practical defaults, not a comprehensive secret scanner—review every selection.

## Build and run

Requirements: Windows, the .NET 9 SDK, and a Visual Studio installation/workload capable of building WPF.

```powershell
dotnet restore SourceRelay/SourceRelay.sln
dotnet build SourceRelay/SourceRelay.sln
dotnet test SourceRelay/SourceRelay.sln
dotnet run --project SourceRelay/SourceRelay/SourceRelay.csproj
```

The automated tests use only temporary directories and synthetic files. They cover protocol round trips, hashing, bundle paths/modes, returned-bundle security validation, local conflicts, apply, persistent undo/redo, external edits, and read-only handling.

## Current limitations

- Files already present in the original bundle are the only possible return targets; arbitrary new files and renames are unsupported.
- Diff display is intentionally a simple line-oriented comparison and binary files have no text preview.
- Folder selection is lazy, but tri-state parents reflect direct interaction rather than continuously aggregating all unloaded descendants.
- Exclusions are persisted as understandable settings, but v1 has no dedicated settings editor.
- Downloads recognition is passive and never applies files. Unrelated ZIPs are ignored.
- There is no direct upload/download, browser automation, AI coding, project-wide indexing, Git integration, or Perforce CLI/API/automatic checkout.
