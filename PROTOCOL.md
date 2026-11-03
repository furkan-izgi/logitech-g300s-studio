# Logitech G300s — firmware interface, tersine mühendislik notları

Cihaz: **Logitech G300s Optical Gaming Mouse** — `USB\VID_046D&PID_C246&REV_7003`
(firmware 70.03). Kablolu, 2 HID arayüzü, onboard hafıza (3 profil), 9 tuş,
250–2500 DPI, tek bölge RGB LED (kanal başına 1 bit), 125/250/500/1000 Hz.

## 1. Firmware imajına erişim — durum

Cihaz USB üzerinden **sadece iki HID arayüzü** sunuyor; DFU / bootloader /
vendor komut arayüzü yok. Firmware MCU'nun iç flash'ında ve okuma korumalı.
Gerçek bir binary dump için fiziksel erişim gerekir (PCB sökümü + SWD/JTAG pad
veya flash çipi sökme). Bu yüzden "firmware'in dışa açık yüzü"nün tamamını
çıkardık: USB/HID descriptor'ları, vendor feature-report protokolü ve onboard
profil veri yapısı. Firmware'in kalıcı konfigürasyon yüzeyinin **%100'ü** bu.

## 2. USB / HID haritası

| Arayüz | Koleksiyon | İçerik |
|---|---|---|
| MI_00 | Usage 0x0001/0x0002 | Fare: 16 bit tuş maskesi + 16-bit X/Y + 8-bit tekerlek |
| MI_01 Col01 | Usage 0x0001/0x0006 | Boot klavye (Report ID 0x01): modifier + 5 keycode — **onboard makrolar buradan çıkıyor** |
| MI_01 Col02 | Usage 0xFF80/0x0080 | Vendor kanal (aşağıdaki protokol) |

Vendor kanal feature report'ları (kontrol endpoint'i, OUT endpoint yok):

| Report | Boyut (id dahil) | Anlam |
|---|---|---|
| `0x80` | 5 (input) | **Async donanım bildirimi** (DPI/profil/özel tuş basma/bırakma olayları) |
| `0xF0` | 4 | **Aktif profil, DPI seviyesi ve Flash Busy/Lock durumu** (RAM kontrolü) |
| `0xF1` | 2 | **Canlı LED rengi** (RAM, anında, flash'a yazmaz) |
| `0xF2` | 2 | **Canlı Polling Rate** (RAM, anında, `0x04 \| rate_code`) |
| `0xF3` | 35 | Onboard profil 0 (Flash EEPROM) |
| `0xF4` | 35 | Onboard profil 1 (Flash EEPROM) |
| `0xF5` | 35 | Onboard profil 2 (Flash EEPROM) |

### 0xF0 — aktif durum ve kontrol

```
F0  FLAGS  00 00
    bit0     : Config / Unlock modu (1=açık, 0=kapalı)
    bit1..2  : Aktif DPI seviyesi (0..3)
    bit3     : Flash Meşgul / Kilit Durumu (1=Meşgul/Yazılamaz, 0=Yazmaya hazır)
    bit4..7  : Aktif profil (0..2)
```

Yazma komutları (FLAGS baytı):

```
0x80 | (profil << 4)      -> aktif profili değiştir
0x40 | (seviye << 1)      -> aktif DPI seviyesini değiştir
0x01                      -> config / unlock moduna gir
0x00                      -> config modundan çık / kilitle
```

### 0xF1 — canlı LED (RAM)

```
F1  COLOR
    bit0: Kırmızı (1=Açık, 0=Kapalı)
    bit1: Yeşil   (1=Açık, 0=Kapalı)
    bit2: Mavi    (1=Açık, 0=Kapalı)
```

### 0xF2 — canlı polling rate (RAM)

```
F2  RATE_FLAGS
    bit0..1: Polling hızı kodu (0=1000 Hz, 1=125 Hz, 2=250 Hz, 3=500 Hz)
    bit2   : Daima 1 (0x04 bayrağı)
```

Yazma: `[0xF2, 0x04 | (rate_code & 0x03)]` -> Flash'a yazmadan anında farenin USB polling hızını değiştirir.

### 0x80 — async donanım olay bildirimi (5 byte input report)

Özel fare fonksiyon tuşlarına basıldığında veya bırakıldığında farenin vendor kanalından (0xFF80) asenkron fırlatılır:

```
[0] 0x80 (Report ID)
[1] Durum: 0x01 = Basıldı, 0x00 = Bırakıldı
[2] 0x00
[3] 0x00
[4] Özel Tuş Aksiyon Kodu (0x0A: DPI Up, 0x0B: DPI Down, 0x0C: DPI Cycle, 0x0D: Profile Cycle, 0x0E: Sniper / DPI Alternate, 0x0F: Default DPI)
```

### 0xF3..0xF5 — profil (35 byte, id dahil)

```
[0]     report id (F3/F4/F5)
[1]     LED: bit0=R bit1=G bit2=B (kanal başına 1 bit), bit3..7 yedek
[2]     polling: 0=1000 1=125 2=250 3=500 Hz
[3..6]  DPI seviyeleri 0..3: bit7 = varsayılan, bit0..6 = dpi/250 (1..10)
[7]     bilinmiyor (stok profillerde 0x40 / 0x02) — korunmalı
[8..]   9 tuş x 3 byte: {code, modifier, key}
```

Tuş `code`:

```
0x00        -> keystroke: modifier + tek HID keyboard usage
0x01..0x09  -> fare tuşu 1..9
0x0A        -> DPI yukarı
0x0B        -> DPI aşağı
0x0C        -> DPI seviyeleri arasında döngü
0x0D        -> profil döngüsü
0x0E        -> DPI alternatif ("sniper" / basılı tut)
0x0F        -> varsayılan DPI
```

`modifier` bitmask: 0x01 LCtrl, 0x02 LShift, 0x04 LAlt, 0x08 LWin,
0x10 RCtrl, 0x20 RShift, 0x40 RAlt, 0x80 RWin.
`key` tek bir HID keyboard usage (örn. 0x06 = `c`) — **tuş başına tek karakter**.

## 3. Aracın kullanımı

```
python g300s.py info              # aktif profil + 3 profilin özeti
python g300s.py dump              # JSON
python g300s.py save backup.json  # yedek
python g300s.py restore backup.json
python g300s.py profile 1         # aktif profili değiştir
python g300s.py dpi-level 2       # aktif DPI seviyesi
python g300s.py led 0 1 0 0       # profil 0 LED = kırmızı
python g300s.py dpi 0 2 1200 default
python g300s.py rate 0 500
python g300s.py btn 0 3 key:ctrl+shift+m
python g300s.py btn 0 8 dpi-alternate
python g300s.py led-live 0 1 1     # anlık LED (RAM, flash'a yazmaz)
python g300s.py ledfx rainbow 10   # rainbow | blink | strobe | police | pulse
python g300s.py monitor 10        # vendor bildirimlerini dinle
python g300s.py selftest          # parse/serialize doğrulaması
```

## 4. Sınırlar (firmware'in kendi koyduğu)

- Tuş başına **tek** keystroke + modifier. Çok adımlı makro onboard yazılamaz.
- 9 tuş, 3 profil, 4 DPI seviyesi (250 adım), tek LED bölgesi, 8 renk.
- LED kanal başına 1 bit — parlaklık/animasyon firmware'de yok.
- Makrolar klavye arayüzünden çıkar; fare hareketi/delay içeren makro onboard yok.

## 5. Flash yazma davranışı ve LGS Tersine Mühendislik Bulguları

- `F0` (profil/DPI/unlock), `F1` (canlı LED) ve `F2` (canlı polling rate) yazmaları **RAM üzerinden** yürütülür; anında uygulanır ve flash'a dokunmaz.
- `F3..F5` (profil) yazmaları doğrudan MCU flash/EEPROM'una yazılır.
- **Flash Kilit / Meşgul Mekanizması (Çözüldü):**
  - `LCore.exe` disassembly analizinde (`0x8F4D10`), profil yazılmadan önce `F0` raporu okunur ve `byte[1] & 0x08` (bit 3) denetlenir.
  - Eğer `bit 3 == 1` ise, farenin dahili flash denetleyicisi **meşguldür (BUSY)**. Bu durumda gönderilen profil yazma raporları firmware tarafından sessizce düşürülür.
  - LGS bu durumda profil yazmayı reddeder, profili `dirty = 1` olarak işaretler ve 500ms sonra tekrar denemek üzere `QTimer::singleShot(500, onDelayedSaveProfiles)` başlatır.
  - `bit 3 == 0` olduğunda profil yazılır ve ardından 50ms aralıklarla (en fazla 40 deneme = 2000ms) `onSavedOnboardProfile` ile yazımın tamamlandığı doğrulanır.
- **Profil Baytı `[7]`:** LGS (`LCore.exe`), profili `0x22` (34 bayt) blok olarak `rep movsb` ile okur ve yazar. Bayt `[7]` (offset 6) üzerinde hiçbir işlem yapmaz; donanım sağlama/üretim parametresi olarak aynen korur.
- **Asenkron Olaylar (`0x80`):** Fare, özel tuşlara (DPI, profil döngüsü, sniper) basıldığında veya bırakıldığında `0x80` input raporu fırlatır (`[0x80, pressed(0/1), 0, 0, action_code]`).

Bu bulgular sayesinde `g300s.py` ve yeni geliştirilecek modüler paket, farenin kilitlenme veya profil düşürme sorunlarını tamamen ortadan kaldıracak şekilde yapılandırılmıştır.
