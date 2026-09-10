# FoxVoice Desktop

Windows 11 x64 WPF shell for the FoxVoice native supervisor. The desktop app reads hardware,
audio-device and model data from the Rust process and keeps the realtime audio process isolated
from the UI.

Development run:

```powershell
dotnet run --project desktop\FoxVoice.Desktop\FoxVoice.Desktop.csproj
```

Self-contained release:

```powershell
.\scripts\bootstrap-native.ps1
.\scripts\build-release.ps1
```

The output in `artifacts\FoxVoice-win-x64` includes `FoxVoice.exe`, both native processes and a
SHA-256 manifest. The script also creates `FoxVoice-win-x64.zip` plus its SHA-256 file. End users
do not need Rust, Visual Studio or .NET installed.
