# Contributing to Thanos

Thank you for helping improve Thanos by Bipra.

## Before starting

1. Search existing issues before opening a duplicate.
2. For a significant feature or behavior change, open an issue first and explain the proposed user experience.
3. Follow the complete setup and ONNX model instructions in [README.md](README.md).

## Git workflow

1. Fork the repository on GitHub.
2. Clone your fork and create a focused branch:

```powershell
git clone https://github.com/<your-account>/Thanos.git
cd Thanos
git switch -c feature/short-description
```

3. Make small, scoped commits with clear messages.
4. Sync your branch with the upstream default branch before opening a pull request.
5. Push the branch to your fork and open a pull request against the upstream repository.

## Validate changes

Run the Release build and complete test suite:

```powershell
dotnet restore Thanos.slnx
dotnet build Thanos.slnx -c Release
dotnet test Thanos.slnx -c Release
```

For application or packaging changes, also verify the self-contained publish:

```powershell
dotnet publish .\src\Thanos.App\Thanos.App.csproj -c Release -r win-x64 --self-contained true
```

## Project rules

- Keep all photo analysis local. Do not add uploads, telemetry or network photo processing without prior discussion.
- Preserve safe deletion: Recycle Bin by default, explicit confirmation for permanent deletion, and no automatic deletion.
- Keep scan sessions independent and preserve existing SQLite data through schema migrations.
- Do not add video scanning unless the project scope is explicitly changed.
- Add or update tests for behavior, persistence, matching or deletion changes.
- Keep UI changes usable in both light and dark themes.
- Do not commit `bin`, `obj`, `.venv`, databases, test results, logs, personal photos or `models/*.onnx`.
- Do not include private photo paths or identifiable photos in issues, logs, screenshots or test fixtures.

## Pull request checklist

- Describe the problem and the implemented behavior.
- Keep unrelated formatting or refactoring out of the pull request.
- Include screenshots for visible UI changes.
- State the Windows version and architecture used for validation.
- Report the exact build, test and publish commands run.
- Confirm that no user photos, local databases, model binaries or generated build output are included.

Bug reports should include reproduction steps, expected behavior, actual behavior and a redacted log excerpt when relevant.
