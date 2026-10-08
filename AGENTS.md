# Project conventions

- Write source code in English, including identifiers, comments, exception messages,
  application logs, UI text, tests, and build scripts.
- Keep Git commit subjects and bodies in Spanish. Use a clear subject followed by
  a detailed body explaining the problem or purpose, changes and resulting behavior,
  relevant design decisions, validation performed, and material limitations.
  For an initial commit, describe the complete baseline being introduced.
- Keep user-facing project documentation in Spanish unless the user requests another language.
- Define the application version in `src/ProductInfo.cs`. Keep the application UI,
  assembly metadata, release notes, and installation instructions consistent with it.
- Run `powershell -NoProfile -File .\scripts\Build.ps1` after code changes. This runs
  the core tests, checks compilation against the installed NinjaTrader assemblies,
  and generates a source bundle. Generated files in `build/` are ignored by Git.
- Unit tests use a fake gateway. Do not describe them or a successful compilation
  as evidence of actual execution compatibility with Tradovate or Rithmic.
