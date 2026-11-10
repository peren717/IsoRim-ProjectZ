# Source/Dev: ProjectZ's test modes

The `Test*.cs` files are the headless test modes (`-projectztest -projectzmode=<mode>`, list in `TestRunner.cs`).
`GameRangeTestMod` and `LadderTestMod` are XML test mods for the modes gamerange and customladders: they ship inside
Source but are never loaded from here. Put one in the Mods folder for a run, and take it out after, with
`Source/Dev/testmods.sh install|remove|status gamerange|ladders [ModsDir]` (Git Bash).
