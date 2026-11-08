# Building Passport Dictation

## Windows

Use .NET SDK 10 and PowerShell on Windows.

```powershell
.\build-windows.ps1
```

The self-contained application is written to `release/Windows`.

Run the published protocol checks with isolated state.

```powershell
.\release\Windows\PassportDictation.exe --self-test .\protocol-test.json --protocol-only --data-dir .\test-state
```

For the native input check, omit `--protocol-only` and keep the self-test window in the foreground. The native test restricts input to its own editor.

## Firmware

Use ESP-IDF 6.1 and its matching Python environment. In an ESP-IDF PowerShell environment, run the build script with the configured SDK paths.

```powershell
.\build-firmware.ps1 -IdfPath $env:IDF_PATH -IdfToolsPath $env:IDF_TOOLS_PATH -IdfPythonEnvironment $env:IDF_PYTHON_ENV_PATH
```

The canonical variant is `folotoy/ai-passport --name passport-dictation --language zh-CN`.
The build creates `firmware/build/merged-binary.bin` for installation from address `0x0`.
This merged image contains an empty NVS region. Existing device settings require a separate backup before replacement.

The optional `flash-firmware.ps1` script operates on the `Firmware` directory from the release ZIP. It backs up the device before writing the individual segments.

## Validation

`validation/windows-published-protocol-tests.json` records 22 passed local protocol checks.
`validation/firmware-validation.json` records the clean-baseline comparison and font check.
`validation/flash-script-tests.json` covers the mocked backup-before-flash flow and backup failure handling.

The firmware host suite has the same 17 failures and 23 errors as its clean upstream snapshot. The single-board distribution omits other boards and test resources referenced by that suite.
Firmware compilation passed under ESP-IDF 6.1. The shared MQTT/UDP and WebSocket code paths compile, while physical-device behavior remains unverified.

The firmware upstream snapshot is `72da544d1b51678f5d88967adc299b3aec956946`.
