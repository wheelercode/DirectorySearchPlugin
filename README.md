# Directory Search PowerToys Run plugin

Initial project for a PowerToys Run directory-search plugin.

The project currently contains a local compile-time copy of the small `Wox.Plugin`
contract used by PowerToys Run. This keeps the project buildable in the Linux
development environment. On Windows, the contract file will be replaced by a
project reference to PowerToys' `Wox.Plugin` project before installing the plugin.

The current `Main` class returns one hard-coded directory as a smoke test. The
filesystem indexer and query engine are intentionally not implemented yet.
