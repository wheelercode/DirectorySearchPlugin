# Directory Search PowerToys Run plugin

Directory Search is a PowerToys Run plugin backed by a continuously updated
NTFS directory index. Search with the `\\` action keyword.

## Architecture

`DirectorySearchIndexer` runs as the automatic delayed-start Windows service
`WheelercodeDirectorySearch`. The service:

1. Captures the current USN journal position.
2. Enumerates directory records directly from the NTFS MFT.
3. Replays changes that occurred during enumeration.
4. Publishes an immutable base snapshot under
   `%ProgramData%\Wheelercode\DirectorySearchPlugin`.
5. Continues monitoring the USN journal and appends sequenced path updates.

The PowerToys plugin remains unelevated. It only reads the service-owned files:

1. It loads the current base generation and all existing updates.
2. It watches the shared directory for changes.
3. It applies new sequenced updates to its in-memory index.
4. It checks the files every two seconds as a backup for missed filesystem
   notifications.

There is no named pipe or command channel between the plugin and the elevated
service.

After 10,000 update records, the service publishes its current in-memory state
as a new base generation, resets the update log, and continues from the same
USN checkpoint. An MFT rescan is only required at service startup or after a
USN journal reset.

## Installation

Load the development commands:

```powershell
. .\custom_commands.ps1
```

Install or update the service:

```powershell
installsvc
```

The script displays one UAC prompt, publishes the service under
`%ProgramFiles%\Wheelercode\DirectorySearchPlugin`, configures its shared data
directory as read-only for ordinary users, registers automatic restart
recovery, and starts it.

Build and install the PowerToys plugin:

```powershell
buildp
buildJunction
runp
```

PowerToys does not need to run as administrator.

Check the service:

```powershell
svcstatus
```

Uninstall the service while preserving its index data:

```powershell
uninstallsvc
```

Run `.\uninstall-service.ps1 -RemoveData` to remove the service, installed
program files, and generated index data.

## Development

- `buildi` builds the service project.
- `runi` starts the service executable interactively with elevation.
- `buildp` builds the PowerToys plugin.
- `buildJunction` links the plugin output into PowerToys Run.
- `runp` starts PowerToys.

Run the indexer with `--self-test` to validate FRN path reconstruction,
parent-directory renames, path updates, creates, deletes, and USN record
parsing without reading a live volume.
