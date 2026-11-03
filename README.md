# AutoCAD AI Designer V4 — AutoCAD 2022

AutoCAD içinde dockable bir sohbet paneli: doğal dille istek yazarsınız, yapay zekâ **öneri planı** üretir, plan çizimde **renkli hayalet önizleme** olarak görünür, siz onaylarsınız, değişiklik **tek adımda** uygulanır ve **tek UNDO ile geri alınır**.

> **Önemli (dürüst not):** Kod bu ortamda gerçek AutoCAD 2022 DLL'leriyle derlenemedi. Doğrulananlar: Python servisi (19 birim test), C# kodunun söz dizimi/tür kontrolü (AutoCAD API'si taklit edilerek) ve doğrulama mantığı (mono üzerinde çalıştırıldı). AutoCAD'e özgü davranış (panel, seçim, önizleme, işlem) **sizin makinenizde ilk kez çalışacak**. İlk denemeyi bir DWG **kopyasında** yapın.

## V3'e göre neler değişti
| Alan | V3 | V4 |
|---|---|---|
| Kurulum | 2 terminal, token ortam değişkeni, her seferinde NETLOAD | `install.ps1` tek komut; AutoCAD açılınca otomatik yüklenir |
| Servis | Elle başlatılıyordu | Eklenti gizli başlatır, rastgele port+token, AutoCAD kapanınca kendini kapatır |
| API anahtarı | Ortam değişkeni | Panelden girilir, Windows DPAPI ile şifreli saklanır |
| Seçim | `SelectImplied` (panel odaktayken güvenilmez) | `Nesne seç` düğmesi → gerçek AutoCAD komutu |
| Uygulama | Modeless koddan yazma | Gerçek komut içinde tek transaction → **tek UNDO**, hata olursa çizim değişmez |
| Önizleme | Sadece metin | Transient grafik hayaleti + Türkçe işlem özeti |
| İşlemler | taşı, katman, çizgi, daire | + kopyala, döndür, ölçekle, katman oluştur, polyline, dikdörtgen, yazı, blok ekle |
| Konuşma | Her istek bağımsız | Çok turlu sohbet belleği |
| Model hatası | Düz hata | Geçersiz plan otomatik bir kez düzeltilir; anlaşılır Türkçe hata (kota, anahtar, model) |
| Yedek | DWG'nin yanına çöp dosya | `%LOCALAPPDATA%\CadAiDesigner\backups` (son 30) |
| Düzeltilen hatalar | bkz. `DEGISIKLIKLER.md` | |

## Yapay zekâ sağlayıcısı (herhangi bir API)
Panel → **Ayarlar** → *Yapay zekâ sağlayıcı* listesinden seçin; Base URL ve model otomatik dolar, anahtar her sağlayıcı için ayrı ve şifreli saklanır.

| Sağlayıcı | Ücret | Not |
|---|---|---|
| Google Gemini | **Ücretsiz anahtar** (aistudio.google.com/apikey) | Önerilen; model `gemini-2.5-flash` |
| Groq | Ücretsiz anahtar (console.groq.com) | Hızlı; dakikalık limitleri düşük |
| OpenRouter | `:free` modeller ücretsiz | Tek anahtarla çok model |
| Ollama | Ücretsiz, yerel, anahtarsız | ollama.com + `ollama pull qwen2.5:7b`; güçlü GPU önerilir |
| Codex CLI | ChatGPT aboneliğiniz | API anahtarı gerekmez; bir kez `codex login` |
| Cursor CLI | Cursor aboneliğiniz | `cursor-agent login` |
| OpenAI / DeepSeek / Anthropic | Kredi gerekir | |
| Özel | — | Herhangi bir OpenAI uyumlu `/chat/completions` adresi |

Hata mesajları ayrıştırılır: **kota/kredi bitti** (ödeme gerekir → başka sağlayıcı seçin), **istek sınırı** (biraz bekleyin), **anahtar geçersiz**.
## Gereksinimler
Windows 10/11 · AutoCAD **2022** · Visual Studio 2022 **Build Tools** (".NET masaüstü derleme araçları" + ".NET Framework 4.8 hedefleme paketi") · Python **3.9+** (PATH'te) · bir yapay zekâ sağlayıcısı (ücretsiz Gemini anahtarı, Ollama, Codex CLI veya OpenAI vb.).

## Kurulum
1. Zip'i bir klasöre çıkarın (ör. `C:\AI-Agent-V4`).
2. AutoCAD'i kapatın. PowerShell'de:
   ```powershell
   cd C:\AI-Agent-V4
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   (AutoCAD farklı yerdeyse: `-AutoCADDir 'D:\Autodesk\AutoCAD 2022'`.) Betik testleri çalıştırır, derler ve `%APPDATA%\Autodesk\ApplicationPlugins\CadAiDesigner.bundle` altına kurar.
3. AutoCAD'i açın → `AIDESIGNER` → panelde **Ayarlar** → API anahtarını girin → Kaydet.

Kaldırma: `.\uninstall.ps1` (ayarları da silmek için `-RemoveData`).

## Kullanım
1. Bir DWG **kopyası** açın, kaydedin.
2. Nesneleri seçin → panelde **Nesne seç**.
3. İsteği yazın, **Ctrl+Enter**: ör. *"Seçilenleri X yönünde 500 birim kopyala"*, *"Seçili nesneleri DUVAR katmanına al, katman yoksa kırmızı oluştur"*, *"Seçilenleri sol kenara hizala"*, *"0,0 köşeli 4000×3000 dikdörtgen çiz"*.
4. Önizlemeyi AutoCAD'de inceleyin (yeşil: değişen, sarı: kopya, camgöbeği: yeni) → **Onayla / Uygula**.
5. Beğenmezseniz `Ctrl+Z` (tek adımda geri alır). Kaydetmek sizde.

`AIDSELECT` ve `AIDAPPLY` panelin iç komutlarıdır, elle çalıştırmayın.

## Güvenlik ve gizlilik
- Yalnızca izin listesindeki 12 işlem çalışır; silme, komut/script çalıştırma yoktur. Her plan hem Python'da hem C#'ta (yetkili) doğrulanır; seçili olmayan nesneye dokunulamaz; kilitli/dondurulmuş katman reddedilir.
- Servis yalnızca `127.0.0.1`'de dinler, oturuma özel rastgele token ister, proxy'yi atlar.
- **Seçili nesnelerin bilgisi (tür, katman, ölçü, metin içeriği) OpenAI'a gönderilir.** Gizli/kısıtlı çizimlerde kurumsal onay olmadan kullanmayın.
- Günlükler: `%LOCALAPPDATA%\CadAiDesigner\logs` (istek metni ve uygulanan plan özeti; anahtar ve geometri yazılmaz).

## Sorun giderme
| Belirti | Çözüm |
|---|---|
| `AIDESIGNER` tanınmıyor | Kurulum sonrası AutoCAD'i yeniden açın; `SECURELOAD`/güven uyarısı çıkarsa "Always load" deyin |
| "Python 3.9+ bulunamadı" | python.org'dan kurun (Add to PATH) veya Ayarlar'da `python.exe` yolunu girin. Microsoft Store'un sahte `python` kısayolu sayılmaz |
| "Servis başlamadı" | Mesajdaki son satırlar nedeni gösterir; `%LOCALAPPDATA%\CadAiDesigner\logs\service.log` |
| Kota/401/404 hatası | Mesaj Türkçe açıklar: kredi, anahtar veya model adı |
| Önizleme görünmüyor | Planı iptal edip yeniden hazırlayın (`REGEN`); uygulama önizlemesiz de çalışır |
| Derleme hatası `error CS...` | Satırı bana iletin; AutoCAD API'sine özgü bir uyumsuzluk olabilir |

Elle hata ayıklama: `scripts\dev-service.ps1 -LaunchAutoCAD`.

## Bilinen sınırlar
- Önizleme her işlemi **orijinal geometriden** gösterir (aynı nesneye zincirleme işlem varsa yaklaşıktır); uygulama doğru sırayla yapılır.
- Kopyalama `DeepClone` kullanır; dinamik blok parametreleri, xref içeriği ve annotative ayarlar özel olarak ele alınmaz.
- Koordinatlar her zaman WCS ve çizim birimindedir; UCS döndürülmüşse model yine WCS'e göre hesaplar.
- Seçim en fazla 100 nesne gönderir; çok büyük seçimlerde sonuç kalitesi düşer.
- Yedek yalnızca son **kaydedilmiş** DWG'dir; kaydedilmemiş iş için UNDO kullanın.
- Model geometriyi yanlış hesaplayabilir; önizlemeyi her zaman kontrol edin.

## Ticari ürün yolunda (kod dışı işler)
Kod imzalama (Authenticode), MSI/Inno Setup kurulum paketi (Python'u PyInstaller ile tek `.exe` yapıp bağımsız kurulum), lisanslama/aktivasyon, çoklu AutoCAD sürümü testi (2023-2025 için .NET 8 geçişi), otomatik güncelleme, kullanım koşulları/gizlilik politikası, kendi backend'iniz üzerinden API anahtarı yönetimi ve Autodesk App Store yayın süreci.


## Metraj, Keşif ve Revizyon (salt okunur)

Çizimi değiştirmez. Üç yol:

* Panelde **Metraj** düğmesi veya komut satırında `AIDMETRAJ` → kapsam (Tümü / Seçim / Katman), yazılar sayılsın mı, revizyon olarak kaydet (örn. `R1`), karşılaştır (Yok / kayıtlı revizyon / başka DWG).
* Panelde doğal dil: "metraj çıkar", "E-KABLO katmanının metrajı", "bunu R2 olarak kaydet", "R1 ile R2 arasında ne değişti", "eski.dwg ile karşılaştır". AI salt okunur `takeoff` eylemi üretir; onay gerekmez.

**Neler sayılır (yalnızca Model alanı):**
* Bloklar: efektif ada göre adet; TAG/TIP/ÖLÇÜ/MODEL/GÜÇ/KESİT… öznitelikleri ve dinamik görünürlük/genişlik değerleri ayrı kalem oluşturur.
* Kablo tavası (dinamik Uzunluk/Length/Boy/Distance parametresi olan ya da adında "tava/tray/kanal" geçen bloklar): genişliğe göre toplam metre (parametre değeri, yoksa X ölçeği × blok boyu).
* Line/Polyline/Arc/Spline: katman başına toplam uzunluk (m). Kapalı polyline ve tarama: katman başına alan (m²). İsteğe bağlı yazı adedi.
* Birimler INSUNITS'ten metreye çevrilir (tanımsızsa mm kabul edilir).

**Keşif:** `%APPDATA%\CadAiDesigner\birim_fiyat.csv` (veya `.xlsx`), sütunlar `poz_no;kalem;birim;birim_fiyat` (`;` veya `,`; `1.234,50` biçimi olur). Kalem adı metraj kalemiyle aynıysa ya da metraj kaleminin içinde geçiyorsa eşleşir (Türkçe harf/büyük-küçük duyarsız). Dosya yoksa ilk metrajda çizimin kalemleriyle şablon oluşturulur. Fiyatı olmayan kalemler raporda listelenir. Örnek: `templates\birim_fiyat_sablon.csv`.

**Çıktılar:**
* Excel: DWG'nin yanında `<dwg>_metraj_<tarih>.xlsx` (sayfalar: Metraj, Keşif, Revizyon Farkı). Ek kütüphane gerekmez. DWG kaydedilmemişse/klasör yazılamıyorsa `%APPDATA%\CadAiDesigner\metraj`.
* Revizyonlar: `<dwg klasörü>\<dwg>_revizyonlar\<etiket>.json`.
* Fark tablosu: kalem, eski, yeni, fark (+/−), durum, tutar farkı.

**Sınırlamalar:** iç içe bloklar ve XREF'ler açılmaz; Paper Space sayılmaz; kablo tavası köşe/dirsek parçaları ayrı kalem değildir; fiyat eşleşmesi metin tabanlıdır.
## Attribute (etiket) blokları → Excel

`AIDATTEXPORT` komutu, panelde **Attribute Excel** düğmesi veya panelde "etiket bloklarını excele aktar" isteği.

- Kapsam: seçim varsa seçili bloklar; yoksa `Attributeli` (varsayılan) / `Tumbloklar` / `Blok` (çizimdeki attribute'lu blok adları listelenir, ad veya numara girilir) / `Secim`. İsteğe bağlı olarak pafta (layout) alanları da dahil edilir. Dinamik bloklar gerçek (efektif) adlarıyla gruplanır.
- Çıktı: DWG'nin yanında `<dwg>_attribute_<tarih>.xlsx` (kaydedilmemiş çizimde `%APPDATA%\CadAiDesigner\metraj`), otomatik açılır.
  - **Tümü** sayfası ve her blok adı için ayrı sayfa. Sütunlar: Blok, Katman, Handle, X, Y (+ pafta varsa Alan), sonra her attribute etiketi (tanımdaki sırayla; etiket adı ne olursa olsun).
  - Sayısal görünen değerler (`12,5`, `12.5`, `1.234,5`) sayı olarak yazılır; sayısal sütunlara **TOPLAM** satırı eklenir (ör. KABLO METRAJI).
  - Çok satırlı (MText) ve sabit (constant) attribute'lar da dahildir.
  - İsteğe bağlı **Özet** sayfası: bir etikete göre gruplar (ör. `KABLO` → her kablo tipi için adet ve KABLO METRAJI toplamı).
- Salt okunurdur; çizimde hiçbir şey değişmez. Esc her adımda güvenle iptal eder.

## Tava yerleştirme (AIDTAVA / TAVA / KT)

AI kullanmadan, polyline/çizgi ile çizilmiş plana `cabletray` bloğunu EN ve BOY ayarlı yerleştirir. Panelde **Tava Yerleştir** düğmesi de aynı komutu çalıştırır.

1. Seçim: önceden seçili çizgi/polyline'lar; yoksa seçim istenir, `Tumu` ile plan katman(lar)ındaki tüm çizgi/polyline'lar (virgüllü liste, `*` = tümü; hatırlanır).
2. Seçenekler (Enter = Devam): `[En/Yukseklik/Kalinlik/Kopyala/Mod/Baglanti/Devam]`
   - **EN**: otomatik — paralel çift çizgi aralığından (50/100/150/200/300/400/500/600'e yuvarlanır, ±%15), tek çizgide polyline kalınlığından, yoksa son EN (varsayılan 300). `En` ile elle girilir (0 = otomatiğe dön).
   - **Yukseklik / Kalinlik**: YUKSEKLIK/KALINLIK attribute'ları (varsayılan 40 / 2, hatırlanır). `Kopyala`: mevcut bir tavadan EN, yükseklik ve kalınlığı alır.
   - **Mod**: `Otomatik` (çift çizgi varsa yalnız çiftler, uç kapama çizgileri yok sayılır; yoksa tek çizgi = tava ekseni), `Cift`, `Tek`.
   - **Baglanti** (varsayılan Evet): ardından AIDBAGLANTI mantığıyla ek parçalar yalnızca yeni tavalar ve onlara değen mevcut tavalar için yerleştirilir.
3. Her düz segmente bir blok: BOY = segment boyu, tava eksene ortalanır, soldan sağa/aşağıdan yukarı döndürülür, katman `E_CT` (yoksa oluşturulur). Yaylar kiriş olarak alınır. Mevcut bir tavayla çakışan segment atlanır (tekrar çalıştırmak güvenlidir).
4. Hepsi tek UNDO adımıdır; Esc her soruda güvenle çıkar. Ayarlar `%LOCALAPPDATA%\CadAiDesigner\tava.json`.

## Plandan keşif (AIDPLANKESIF / PK)

Çizgi/polyline ile çizilmiş plandan, **hiç blok eklemeden ve çizimi değiştirmeden** kablo tavası + ek parça keşfi çıkarır. Panelde **Plandan Keşif** düğmesi; panelde "plandan keşif çıkar" isteği de aynı komutu başlatır.

- Sorular AIDTAVA ile aynıdır (seçim veya `Tumu` + katmanlar, EN otomatik/elle, Yukseklik/Kalinlik, Mod; değerler `tava.json`'dan hatırlanır). Ek parçalar her zaman hesaplanır.
- Hesap: AIDTAVA + AIDBAGLANTI bellekte birebir uygulanır (YD, YT, dal genişliğinde Yarım T, AD; leg/clearance kesimleri), sonuç ölçülür ve işlem geri alınır — çizime hiçbir şey yazılmaz, UNDO gerekmez. Bu yüzden sonuç, aynı plana AIDTAVA uygulandıktan sonraki AIDMETRAJ ile aynıdır (Drawing2 kopyasında doğrulandı). Çizimde zaten bulunan tavalar dikkate alınmaz.
- Çıktı: metraj ile aynı kalem adları ve Excel biçimi, `birim_fiyat.csv` fiyatları; DWG'nin yanında `<dwg>_plankesif_<tarih>.xlsx`, otomatik açılır. Özet komut satırında ve panelde.

## AIDHATCHFIX (tava hatch onarımı)

Hatch'i bozuk görünen cabletray bloklarını (seçim veya `Tumu`) dinamik tanımdan yeniden yerleştirir; konum, açı, katman, EN/BOY ve attribute değerleri korunur. Sonrasında üçgen grip/STRETCH ile uzatma doğru çalışır. Teşhis: `AIDHATCHDIAG`.

## Seçim bağlamı (sınırsız)

Seçilen tüm nesneler yapay zekaya gönderilir (eski 100 sınırı kaldırıldı). 100'den büyük seçimler `selected_compact` tablosu olarak (handle, type, layer, block_name, x, y, z, rotation_deg, extents, props) kompakt gönderilir. `settings.json`: `MaxObjects` (0 = sınırsız, varsayılan) ve `CompactAbove` (100). Kırpma olursa bağlamda `selection_truncated` ile sayı belirtilir. Teşhis: `AIDCTXDIAG`.

## Planlar arası hizalama: AIDREFNOKTA (RN) ve AIDPLANKOPYA (PKOPYA)

- **Referans noktaları** (panel düğmesi veya `RN`): önce kaynak plandan bir nokta, sonra hedef plandaki aynı fiziksel noktayı fareyle (osnap) seçin; koordinat yazmak gerekmez. İsteğe bağlı ikinci çift dönme ve ölçek hesaplatır. Noktalar oturum boyunca çizim başına saklanır, panel durum çubuğunda görünür ve yapay zeka bağlamına `reference_points` / `reference_transform` olarak girer.
- **AIDPLANKOPYA / PKOPYA**: seçili nesneleri (veya `Katman`) referans noktalarıyla kaynağın planından hedef plana kopyalar/taşır (bloklar, dönme, attribute korunur; tek UNDO). Yapay zeka da `plan_copy` eylemiyle bunu başlatır.

## AIDOLCU (OLC): blok merkezlerinden ardışık ölçü

Panelde "Blok ölçüsü" veya `OLC`: seçilen blokların (karma tipler) geometrik merkezleri (attribute hariç; `Nokta` ile ekleme noktası seçilebilir) bulunur, 1 birimden yakın merkezler birleştirilir, sıranın ana doğrultusu (yatay/düşey/açılı) hesaplanır ve ardışık döndürülmüş ölçü zinciri oluşturulur. Ölçü çizgisinin tarafı/mesafesi tıklanarak verilir (Enter = varsayılan). Geçerli ölçü stili ve katman kullanılır; `Toplam` ile toplam ölçü eklenir. Tek UNDO.

## Attribute İçe Aktar (AIDATTIMPORT / ATI)

AIDATTEXPORT ile alınan Excel'de değerleri düzenleyip kaydedin, sonra panelde 'Attribute İçe Aktar' (veya ATI). Dosya seçilir (varsayılan: DWG yanındaki en son *_attribute_*.xlsx), önizleme gösterilir, Evet ile tek UNDO adımında yazılır.
- Eşleşme 'Handle (değiştirmeyin)' sütunu ile; handle yoksa/bozuksa blok adı + X/Y (0.01) ile.
- Blok sayfaları ve Tümü birlikte okunur, aynı değer iki kez uygulanmaz; ikisi farklı değiştirildiyse blok sayfası kazanır (çakışma raporlanır). TOPLAM satırları ve Özet yok sayılır.
- Yalnızca değeri değişen attribute etiketleri yazılır; Blok/Katman/Handle/X/Y/Alan ve sabit (constant) etiketler yazılmaz. Boşaltılan hücre değeri siler.
- Sayılar: değer aynıysa çizimdeki metin korunur (1,5 / 60.0); yeni sayılarda ondalık ayırıcı eski değere göre, yoksa virgül; 60.0 -> 60.
- Kilitli katmandaki bloklar atlanır ve raporlanır.


## Armatür yerleşimi (AIDARMATUR / ARM)

Panel: 'Armatür yerleşimi'. Armatür bloğunu tıklayın, açık/kapalı polyline'ları (çoklu) seçin, x mesafesini girin veya iki nokta tıklayın.
- Açık polyline (koridor): tek sıra; uçlardan x, armatürler arası ~2x (eşit dağıtılır), segment yönüne döndürülür.
- Kapalı polyline (oda): en uzun kenara yönlü grid; duvardan x, aralık ~2x, ortalanır; poligon içinde ve duvara >= 0.9x olan noktalar kalır.
- Kopyalar aynı bloğun yeni insert'i: ölçek, katman, attribute ve dinamik özellikler korunur. Tek UNDO adımı.


### Armatür yerleşimi v2 (DIALux tarzı)
- Alan: [Polyline/Icnokta] — Icnokta: oda içine tıklayın, sınır otomatik bulunur (BOUNDARY; bulunamazsa noktayı içeren en küçük kapalı polyline).
- Yöntem: [Aralik/Adet/Mesafe]. Aralik: maks. aralık S (varsayılan 3000 mm) -> n = ceil(L/S), d = L/(2n), aralık 2d, duvar/uç = d. Adet: koridor n, oda NxM veya toplam. Mesafe: eski sabit x.
- Dikdörtgen oda: yönler ayrı ayrı dA=A/(2nA), dB=B/(2nB). Düzensiz oda: en uzun kenara yönlü grid; gerçek duvar mesafesi < d olan noktalar içe kaydırılır, olmazsa atılır (raporlanır).



## Aydınlatma hesabı (lümen yöntemi)
- `ARM` → Yöntem **Lux**: hedef aydınlık (oda 500 lx, koridor 100 lx), H (3 m), Φ (bloktaki LUMEN/LM/FLUX/AKI/ISIKAKISI attribute'undan, yoksa sorulur), MF 0,8, yansıma 0,7/0,5/0,2, çalışma düzlemi 0,85 m, koridor genişliği 2 m.
  k = A/(h·P/2) (dikdörtgende = a·b/(h(a+b))), UF tablodan enterpolasyonla, n = ceil(E·A/(Φ·UF·MF)), oda oranına göre N×M grid (grid n'i yukarı tamamlayabilir).
- Diğer yöntemlerde yerleşimden sonra "Aydınlatma hesabı?" sorulur. Rapor: Eort, Emin≈, Uo≈ (komut satırı + panel), isteğe bağlı MTEXT etiket ve Excel (`*_aydinlatma_*.xlsx`, servis `/xlsx`).
- `AIDAYDHESAP` / `AHESAP` (panel: "Aydınlatma hesabı"): oda polyline / iç nokta + mevcut armatürler (yalnız oda içindekiler sayılır).
- **Yaklaşıktır:** UF tablosu genel doğrudan-geniş dağılımlı armatür için tipik değerlerdir (CIBSE/LiTG tarzı üretici UF tabloları, zemin 0,2). Uo nokta hesabı Lambert kabulü, yalnız doğrudan ışık (E = Φh²/(πd⁴)), duvar bandı 0,5 m. Kesin sonuç için DIALux/üretici fotometrisi kullanın.
