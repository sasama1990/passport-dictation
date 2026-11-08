# FoloToy AI Passport dictation

Board definition that lets the XiaoZhi voice assistant run on the
[FoloToy AI Passport](https://github.com/FoloToy/ai-passport) wearable.

## Hardware

- MCU: ESP32-C3, 8 MB flash, **no PSRAM**, USB Serial/JTAG console
- Audio: ES8311 codec (I2C 0x18) on the shared I2C bus, I2S full-duplex
  (amplifier enable not wired, treated as always on)
- Display: ST7789 (ST7789P3) 240x320 portrait, 4-line SPI
- Battery: CW2017 fuel gauge (I2C 0x63, optional)
- Buttons: UP / DOWN / OK share GPIO0 (ADC1_CH0) through a resistor ladder

Pin mapping follows `ai-passport/components/bsp/include/bsp_pins.h`:

| Function | Pin |
| --- | --- |
| LCD MOSI / SCLK / CS / DC | 9 / 8 / 1 / 20 |
| LCD backlight (PWM) | 21 |
| I2S MCLK / BCLK / WS / DOUT / DIN | 6 / 5 / 3 / 2 / 4 |
| I2C SDA / SCL | 10 / 7 |
| Buttons (ADC ladder) | GPIO0 |

## Dictation build

```sh
python scripts/build.py folotoy/ai-passport --name passport-dictation --language zh-CN
```

The variant enables `CONFIG_PASSPORT_DICTATION` and reports the unique board
identity `ai-passport-dictation`. It targets 8 MB flash and the USB Serial/JTAG
console. Firmware and assets are built independently of the EVE reference.

The Windows receiver supports LAN discovery and an outbound HTTPS relay.
For LAN operation it listens on TCP 18765 and UDP 47625. Open its 60-second
pairing window while both devices are on the same LAN. Initial discovery stores
the receiver identity and bearer token in the `dictation` NVS namespace.
Subsequent discovery replies use HMAC-SHA256 to authenticate address changes.

For cross-network operation, generate a code in the Windows receiver's
“跨网络设置” dialog. During the device's Wi-Fi AP setup, join its displayed
`Xiaozhi-XXXX` hotspot and open `http://192.168.4.1:18766`. Select cross-network
mode, save the code and matching HTTPS relay address, then use “继续设置 Wi-Fi”
to open the standard portal at `http://192.168.4.1`. Select a 2.4 GHz network,
enter its password and select “连接”. “配置成功！” confirms provisioning.
Rejoin the hotspot to retry. Both endpoints require internet access and can
use different networks. Runtime settings use the single `dictcloud/config`
NVS value. The device saves its own token before redeeming a pairing code, so
a lost reply can be retried. The relay confirms actual Windows outcomes.

The bridge waits for `WifiManager::IsInitialized()` before creating any
discovery socket or setup HTTP server. The manager initializes `esp_netif`
and its TCP/IP thread, which must exist before any lwIP socket operation.

Cloud provisioning and STT use the shared XiaoZhi protocol handler, covering
both MQTT/UDP and WebSocket transports. The selected cloud account/model must
return a complete `stt.text` result after manual `listen.stop`. Physical-device
validation is required to confirm that behavior for the selected cloud model.

Automatic firmware installation from the upstream OTA service is disabled for
this variant.

## Controls

The three physical keys control dictation and setup.

- Hold **OK**, wait for the recording prompt/sound, speak, then release.
- A recording can last up to 60 seconds. A longer hold cancels the utterance.
- Long press **UP** to cancel dictation, reset pairing and open a 10-minute
  connection-setup window. The device displays its current setup URL. When
  already connected, open that URL from a phone on the device's network to
  save a new cross-network code or select LAN mode.
- Long press **DOWN** to enter the Wi-Fi configuration flow and change
  networks. Both the Wi-Fi portal and the connection-setup page are available
  through the setup hotspot.

The receiver captures the input window before recording and verifies native
window/control focus before inserting the final Unicode text. Keep the input
field selected until recognition finishes. Focus-change and blocked-input
results remain in the Windows preview for manual copying.

Because the ladder shares one ADC pin, XiaoZhi reads it as three
independent ADC buttons (the same pattern as the ESP-BOX-Lite).

## Notes / calibration

- Display orientation, color inversion and the backlight PWM polarity were
  taken from the Passport BSP; verify on real hardware and adjust the
  `DISPLAY_*` macros in `config.h` if the image is rotated/inverted or the
  backlight is reversed.
- The ADC button voltage windows assume the Passport's external 10 kOhm
  pull-up. Re-measure with the Button page if thresholds drift.
- CW2017 presence is optional; without the chip the status bar shows no
  battery level and the board keeps working.
