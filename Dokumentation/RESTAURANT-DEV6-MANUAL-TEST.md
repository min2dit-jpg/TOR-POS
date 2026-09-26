# TOR Restaurant DEV6 – Manuel Test (sabah)

**Kurulum:** `TOR-Restaurant-Setup-DEV6-<sha>` (PR #96 CI artifact).
**DB:** Yeni, silinebilir test veritabanı. Eski DEV4/DEV5 test DB'si kullanılmaz
(DEV4 DB bilerek reddedilir – bkz. senaryo 1).
**Arayüz dili:** yalnız Almanca (Restaurant ürünü).
**Fiskal:** Test/Simülasyon modu. Gerçek TSE, gerçek terminal, gerçek yazıcı bu testin konusu değil.

Hata olursa her senaryoda şunları al:
- ekran görüntüsü (tüm pencere),
- log: `%LOCALAPPDATA%\TOR Restaurant\Logs\TOR-POS-latest.log` (başlangıç/çökme günlüğü) ve
  `%APPDATA%\TOR-Restaurant\Logs\` klasörü; veritabanı `%APPDATA%\TOR-Restaurant\`,
- durum satırındaki metin (alt satır) ve varsa Fehler-ID.

Otomatik testlerin zaten kanıtladıkları (tekrar etmene gerek yok, ama göz ucuyla bakabilirsin):
migration 42 = C-4 / 43–48 Restaurant, DEV4 reddi, eşzamanlı ödeme, kısmi ödemede
kuruş kalanı, bölme toplamları, mutfak tekrar gönderimi, Zwischenrechnung yan etkisizliği,
Kellnerabrechnung salt-okunur, THEKE/TISCHPLAN geçiş kilidi, 1024×640 ve 1366×768 düzen.

---

## 1. İlk açılış ve eski DB reddi

| | |
|---|---|
| Başlangıç | DEV6 yeni kurulum, `%APPDATA%\TOR-Restaurant` boş |
| İşlem | Programı aç, ilk kurulum sihirbazını bitir, giriş yap |
| Beklenen | Doğrudan **TISCHPLAN** açılır; üstte TISCHPLAN / THEKE / ABMELDEN görünür |
| Ek (isteğe bağlı) | Eski bir DEV4 test DB'sini yerine koyup aç → Almanca mesaj: „Diese Restaurant-DEV4-Testdatenbank kann nicht sicher übernommen werden…“, program açılmaz, dosya değişmez |
| Hata olursa | Başlangıç hata penceresinin ekran görüntüsü + log |

## 2. Masa açma, sipariş, BESTELLUNG SENDEN

| | |
|---|---|
| Başlangıç | Tischplan, boş masa |
| İşlem | Masaya bir kez dokun → 2 ürün ekle (birine Bestelloption, ör. „ohne Zwiebeln“) → **BESTELLUNG SENDEN** → aynı masada tekrar **BESTELLUNG SENDEN** |
| Beklenen | Tek dokunuşla sipariş açılır; ilk gönderim „Bestellung gesendet“; ikinci gönderim „Bestellung bereits gesendet“, mutfakta/KDS'de ikinci kopya **yok** |
| Kontrol | Alt butonların etiketleri tam okunuyor: BESTELLUNG SENDEN, ZWISCHENRECHNUNG, UMBUCHEN, TEILEN, BEZAHLEN (DEV6 düzeltmesi) |
| Hata olursa | Ekran görüntüsü, KDS ekranı görüntüsü, log |

## 3. Yeni ürün ekleyip tekrar gönderme

| | |
|---|---|
| Başlangıç | Senaryo 2'deki masa |
| İşlem | Bir ürün daha ekle → **BESTELLUNG SENDEN** |
| Beklenen | Mutfağa **yalnız yeni ürün** gider, öncekiler tekrar basılmaz |

## 4. Zwischenrechnung

| | |
|---|---|
| Başlangıç | Siparişli masa |
| İşlem | **ZWISCHENRECHNUNG** 2 kez yazdır |
| Beklenen | „keine Zahlung“ açıklamalı rapor; Bon-Historie'de yeni fiş **yok**, fiş numarası artmaz, masa açık kalır |

## 5. Ödeme iptali ve eksik nakit

| | |
|---|---|
| Başlangıç | Siparişli masa |
| İşlem A | **BEZAHLEN** → ödeme penceresinde **İptal** |
| Beklenen A | „TISCH-ZAHLUNG ABGEBROCHEN · Tisch bleibt unverändert offen“; masa açık, pozisyonlar aynı; Tischplan tekrar kullanılabilir |
| İşlem B | **BEZAHLEN** → BAR, verilen tutar toplamdan az |
| Beklenen B | „BARZAHLUNG: Gegebener Betrag ist kleiner… Tisch bleibt offen“; masa açık |
| Hata olursa | Ekran + log; masanın kilitli kalıp kalmadığı (Tischplan'da durum) |

## 6. Tam ödeme (BAR) ve fiş

| | |
|---|---|
| Başlangıç | Siparişli masa |
| İşlem | **BEZAHLEN** → BAR, yeterli tutar → onay |
| Beklenen | Satış tamamlanır, masa kapanır (FREI); fiş (test/simülasyon) basılır/önizlenir; Bon-Historie'de 1 fiş |
| Kontrol | Aynı masa tekrar ödemeye açılamaz (pozisyon kalmadı) |

## 7. Kısmi ödeme (TEILEN)

| | |
|---|---|
| Başlangıç | Masada 3 × aynı ürün (Stück) + 1 başka ürün |
| İşlem | **TEILEN** → 1 adet seç → öde; sonra kalanları öde |
| Beklenen | İlk ödemeden sonra masa **açık**, ödenen adet artık listede yok ya da azaltılmış; yarım adet seçilemez; aynı kalem iki kez seçilemez (Almanca uyarı); son ödemeyle masa kapanır; toplam ödenen = sipariş toplamı |

## 8. Kişi başı bölme

| | |
|---|---|
| Başlangıç | Masada birkaç farklı ürün |
| İşlem | **TEILEN** → „auf Personen“ (ör. 3 kişi) → kişi kişi öde |
| Beklenen | Her ürün tek kişiye; kişi toplamlarının toplamı masa toplamına **kuruşu kuruşuna** eşit; son kişiden sonra masa kapanır |

## 9. UMBUCHEN

| | |
|---|---|
| Başlangıç | İki masa, biri siparişli |
| İşlem | **UMBUCHEN** → hedef masa |
| Beklenen | Pozisyonlar hedef masaya geçer, kaynak masa boşalır; iki masada çift pozisyon yok |

## 10. THEKE ↔ TISCHPLAN

| | |
|---|---|
| İşlem A | THEKE'ye geç, sepete ürün ekle, **TISCHPLAN**'a basmayı dene |
| Beklenen A | Geçiş reddedilir: „TISCHPLAN: Zuerst den aktuellen Vorgang abschließen oder leeren.“ |
| İşlem B | THEKE'de satış yap (BAR) |
| Beklenen B | Normal kasa satışı; hiçbir masa oturumu açılmaz |
| İşlem C | Masa ödeme penceresi açıkken THEKE'ye geçmeyi dene |
| Beklenen C | Geçiş reddedilir |

## 11. Kellnerabrechnung

| | |
|---|---|
| Başlangıç | En az bir ödenmiş, bir kısmen ödenmiş ve bir açık masa |
| İşlem | **KELLNERABRECHNUNG** aç, kapat, tekrar aç |
| Beklenen | Garson başına BAR/KARTE, storno düşülmüş; açık masa tutarı = kalan tutar (kısmen ödenmişte ödenen kısım düşülmüş); açıp kapamak hiçbir şeyi değiştirmez |

## 12. Küçük ekran

| | |
|---|---|
| Başlangıç | Pencere 1024×640 (veya küçük kasa ekranı) |
| İşlem | Tischplan, sipariş ekranı, THEKE, ödeme penceresi |
| Beklenen | ABMELDEN, TISCHPLAN, THEKE ve 5 aksiyon butonu görünür, etiketler kesilmez |

## 13. Program kapanıp açılınca

| | |
|---|---|
| İşlem | Açık siparişli masa varken programı kapat, tekrar aç |
| Beklenen | Masa ve pozisyonlar aynen durur; ödeme kilidi kalmamışsa masa açık; „BESTELLUNG/TSE · PRÜFUNG ERFORDERLICH“ görünürse ekran görüntüsü al |

---

**Sonuç kaydı:** Her senaryo için ✅ / ❌ ve ❌ ise ekran görüntüsü + log adı.
Manuel test geçmeden PR #96 main'e merge edilmez.
