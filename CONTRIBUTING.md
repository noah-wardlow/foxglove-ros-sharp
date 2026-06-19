# Contributing

This project is intentionally small and runtime-focused. Keep changes generic:

- Do not add robot-, customer-, or deployment-specific message types.
- Prefer ROS#-compatible public APIs where practical.
- Add focused tests for protocol behavior, CDR encoding, and public API changes.
- Keep examples neutral and runnable against generic ROS 2 systems.

Before opening a pull request:

```bash
dotnet test FoxgloveRosSharp.sln
dotnet pack src/FoxgloveRosSharp/FoxgloveRosSharp.csproj --configuration Release --output artifacts
```
