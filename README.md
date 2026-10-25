# Thanos

Thanos by Bipra is an open-source Windows desktop app for finding and safely removing similar-looking photos using local ONNX inference. Photos stay on the device, videos are excluded, and deletion uses the Recycle Bin by default.

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Install the app

Recommended installation for most users:

1. Use Windows 10 version 1809 or newer, or Windows 11 x64.
2. Install the latest [Microsoft Visual C++ Redistributable x64](https://aka.ms/vc14/vc_redist.x64.exe).
3. Download the complete x64 ZIP from the project's [latest GitHub release](../../releases/latest).
4. Extract the entire ZIP. Do not move only `Thanos.App.exe`; it depends on the other published files.
5. Run the installer included in the extracted folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-Thanos.ps1
```

The installer adds **Thanos by Bipra** to the Start menu and Programs and Features. A published release is self-contained: end users do not need the .NET SDK, Python, PyTorch, a separate Windows App SDK runtime, or a separate ONNX download.

Only download releases from this project's official GitHub repository. The release must contain `models\dinov2-small.onnx` beside the application files.

## Optional Windows image codecs

JPG, PNG, BMP, GIF and TIFF work without extra setup. Install the official Microsoft extensions for additional formats:

- [HEIF Image Extension](https://apps.microsoft.com/detail/9PMMSR1CGPWG) for HEIF/HEIC
- [Raw Image Extension](https://apps.microsoft.com/detail/9NCTDW2W1BH8) for supported camera RAW formats
- [WebP Image Extension](https://apps.microsoft.com/detail/9PG2DK419DRG) when the Windows WebP codec is unavailable

Some HEIC files also require Microsoft's HEVC codec. Availability and pricing can vary by Windows installation and region.

## Contributor downloads

Install these before building from source:

- [Git for Windows](https://git-scm.com/install/windows)
- [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0); this repository currently targets SDK `10.0.401`
- [Python for Windows](https://www.python.org/downloads/windows/) only when generating the model; Python 3.13 x64 is recommended
- [PyTorch for Windows](https://pytorch.org/get-started/locally/) only when generating the model

Visual Studio is optional. The .NET CLI is sufficient. NuGet restores the Windows App SDK, ONNX Runtime DirectML, SQLite, Serilog and test dependencies automatically. Image decoding and thumbnail generation use Windows Imaging Component, so contributors do not need a separate image-processing SDK.

## Generate the required ONNX model

The model is intentionally excluded from Git because it is approximately 84 MB. Generate it once after cloning:

```powershell
py -3.13 -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install torch onnx
.\.venv\Scripts\python.exe .\tools\export_dinov2.py
Test-Path .\models\dinov2-small.onnx
```

The export script uses Meta's official [DINOv2 `dinov2_vits14` model](https://github.com/facebookresearch/dinov2#pretrained-backbones-via-pytorch-hub), downloads its official weights on first use, and exports ONNX opset 17. An internet connection is required for the first export.

Do not use an ONNX model from an unrelated third-party download. Generate it with the repository script, or use the model bundled in an official Thanos release. Python, PyTorch and the `onnx` package are export tools only; the application performs inference through the restored [`Microsoft.ML.OnnxRuntime.DirectML`](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML/1.24.4) package.

## Build and test

```powershell
dotnet --version
dotnet restore Thanos.slnx
dotnet build Thanos.slnx -c Release
dotnet test Thanos.slnx -c Release
```

## Build the redistributable app

```powershell
dotnet publish .\src\Thanos.App\Thanos.App.csproj -c Release -r win-x64 --self-contained true
```

Publish output:

```text
src\Thanos.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish
```

Distribute the complete `publish` folder as one ZIP. It includes the installer scripts and `models\dinov2-small.onnx`. Install that build locally with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\Thanos.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\Install-Thanos.ps1
```

## Repository size

`bin`, `obj`, `.venv`, test results, databases and the generated ONNX model are ignored by Git. They must not be committed.

```powershell
dotnet clean Thanos.slnx -c Release
Get-ChildItem . -Directory -Recurse -Force | Where-Object Name -In bin,obj,TestResults | Remove-Item -Recurse -Force
Remove-Item .\.venv -Recurse -Force -ErrorAction SilentlyContinue
```

After cleanup, the GitHub source should remain small; each contributor can regenerate the ignored model using the steps above.
