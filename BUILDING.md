# Building Passport Dictation

## Windows

Use .NET SDK 10 and PowerShell on Windows.

```powershell
.\build-windows.ps1
```

The self-contained application is written to `release/Windows`. Use `-DotnetPath` to select a portable SDK and `-OutputDirectory` to choose another destination.

## Headless integration checks

The protocol harness links the production receiver, state store, LAN server and relay client. It uses a simulated input sink and runs without a GUI. Supply a relay URL, a test-state directory and a report file. Each invocation creates isolated state inside that directory.

```powershell
dotnet run --project tests/ProtocolTests/ProtocolTests.csproj -c Release -- `
  http://8.133.186.187:23000/passport-dictation `
  .\test-state .\protocol-test.json
```

The checks create a temporary pairing on the selected relay. Both the normal application and this harness accept HTTP or HTTPS relay URLs, including project subpaths.

The existing Windows self-test remains available with isolated state.

```powershell
.\release\Windows\PassportDictation.exe --self-test .\input-test.json --protocol-only --data-dir .\test-state
```

For the native input check, omit `--protocol-only` and keep the self-test window in the foreground. The native test restricts input to its own editor. Native input and real-device tests remain manual validation steps for this preview.

## Relay

Use Node.js 22.13 or newer for the built-in SQLite development adapter. The deployed Worker uses a D1 binding named `DB`. Development and deployment details are in [relay/README.md](relay/README.md).

```powershell
Set-Location relay
npm ci
npm test
npm run build
```

## Firmware

Use ESP-IDF 6.1 and its matching Python environment. In an ESP-IDF PowerShell environment, run the build script with the configured SDK paths.

```powershell
.\build-firmware.ps1 -IdfPath $env:IDF_PATH -IdfToolsPath $env:IDF_TOOLS_PATH -IdfPythonEnvironment $env:IDF_PYTHON_ENV_PATH
```

The canonical variant is `folotoy/ai-passport --name passport-dictation --language zh-CN`.
The build creates `firmware/build/merged-binary.bin` for installation from address `0x0`.
This merged image contains an empty NVS region. Back up existing device settings before replacement.

The optional `flash-firmware.ps1` script operates on the `Firmware` directory from the release ZIP. It backs up the device before writing the individual segments.

Cloud configuration uses the separate `dictcloud/config` NVS value. Existing LAN credentials in `dictation` remain readable. The firmware's `CONFIG_PASSPORT_RELAY_URL` and Windows `RelayClient.DefaultUrl` select the shipped default service. Runtime setup can select another HTTP or HTTPS relay on both ends.

## Release synchronization

Every new community firmware revision must have a corresponding GitHub release containing the latest Windows companion ZIP, the compatible merged firmware and SHA-256 checksums.

1. Build and validate the Windows companion and firmware for the release.
2. Publish the GitHub release and verify its downloadable attachments and hashes.
3. Include that release's download URL and compatible Windows version in the community description and separate usage instructions.
4. Submit the community revision with its own changelog, then verify the submitted firmware hash and actual review status.

## Validation records

`validation/v0.2.0-preview.5/` contains this revision's protocol and firmware validation summary. The earlier directories retain their respective release records. The root-level validation files belong to `0.1.0-preview.1`.

The firmware host suite has the same 17 failures and 23 errors as its clean upstream snapshot. The single-board distribution omits other boards and test resources referenced by that suite. Firmware compilation passed under ESP-IDF 6.1. The shared MQTT/UDP and WebSocket code paths compile. The selected cloud service's complete `stt.text` response after manual `listen.stop` still needs physical-device testing.

The firmware upstream snapshot is `72da544d1b51678f5d88967adc299b3aec956946`.
