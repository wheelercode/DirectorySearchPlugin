# Directory Search PowerToys Run plugin

Directory Search is a PowerToys Run plugin backed by an independent NTFS
directory index.

## Index lifecycle

1. `DirectorySearchIndexer` captures the current USN journal position.
2. It enumerates NTFS directory records through `FSCTL_ENUM_USN_DATA`.
3. It builds an FRN-based in-memory index and replays journal changes that
   occurred during enumeration.
4. It continues reading `FSCTL_READ_USN_JOURNAL`, applying directory creates,
   deletes, renames and relevant attribute changes directly to the live index.
5. It serves PowerToys queries through the
   `Wheelercode.DirectorySearchPlugin.Index` named pipe.

The indexer also writes `directory-index.tsv` after startup. The plugin uses
that snapshot only when the live helper cannot be reached.

Because opening the NTFS volume requires elevation, the indexer must be run as
administrator. The PowerToys plugin itself remains unelevated.

## Development commands

After loading `custom_commands.ps1`:

- `buildi` builds the elevated indexer.
- `runi` starts the indexer. Leave it running to receive USN updates.
- `buildp` builds the PowerToys plugin.
- `buildJunction` links the debug plugin output into PowerToys Run.
- `runp` starts PowerToys.

Run the indexer with `--self-test` to validate FRN path reconstruction,
parent-directory renames, creates, deletes and USN record parsing without
reading a live volume.

The indexer prints each journal batch that changes the in-memory index. Create,
rename or delete a directory to confirm that the corresponding `USN update`
line appears.
