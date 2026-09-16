# TOR POS Pro v0.7.19 – Windows Setup

Final customer artifact: `TOR-POS-Pro-Setup.exe`

## Kurulum düzeni

- Normal, Almanca Inno Setup kurulum sihirbazı
- Yönetici/UAC onayı
- Kullanım şartlarının kabulü
- İlk kurulumda KIOSK / IMBISS seçimi; güncellemede mevcut edition kilitli
- KIOSK ve IMBISS için ayrı lisans dosyası zorunluluğu
- Administrator şifresi ve dört haneli PIN
- Kurulum yolu: `C:\Program Files\TOR POS Pro`
- Desktop’ta yalnız `TOR POS Pro` kısayolu
- Start Menu kısayolu yok
- Güncellemede eski sürümden kalan TOR POS Start Menu kısayolu temizlenir
- Setup tamamlandığında TOR POS’u başlatma seçeneği

Program dosyaları `Program Files` altında bulunur. SQLite veritabanı, resimler,
loglar, lisans ve diğer değişebilir işletme verileri `%APPDATA%\TOR-POS-Pro`
altında kalır; böylece Program Files yazma koruması ihlal edilmez.

## İkon

`src\TorPos.App\Assets\TorPos.ico` EXE uygulama ikonu ve Windows kısayol
ikonu olarak kullanılır. Aynı ikon Inno Setup üzerinden Setup EXE’ye de eklenir.
ICO dosyasında 16, 24, 32, 48, 64, 128 ve 256 piksel katmanları bulunur.

## Build

Windows’ta `1-SETUP-ERSTELLEN.bat` çalıştırılır. Oluşan Setup:

`installer-output\TOR-POS-Pro-Setup.exe`

Setup dosyası Desktop’a kopyalanmaz; doğrudan bu klasörden başlatılır.
