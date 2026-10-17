# Building from Source

1. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
2. Clone the repository and build:
   ```
   dotnet build -c Release
   ```
3. Output is in `bin/Release/`. Run `NvTgpHold.exe` as administrator.
4. Optional: run the hardware-free tests (they also run on Linux or macOS):
   ```
   dotnet test tests/NvTgpHold.Tests
   ```
