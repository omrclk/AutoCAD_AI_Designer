# AutoCAD AI Designer V4 â AutoCAD 2022

AutoCAD iÃ§inde dockable bir sohbet paneli: doÄal dille istek yazarsÄ±nÄ±z, yapay zekÃ¢ **Ã¶neri planÄ±** Ã¼retir, plan Ã§izimde **renkli hayalet Ã¶nizleme** olarak gÃ¶rÃ¼nÃ¼r, siz onaylarsÄ±nÄ±z, deÄiÅiklik **tek adÄ±mda** uygulanÄ±r ve **tek UNDO ile geri alÄ±nÄ±r**.

> **Ãnemli (dÃ¼rÃ¼st not):** Kod bu ortamda gerÃ§ek AutoCAD 2022 DLL'leriyle derlenemedi. DoÄrulananlar: Python servisi (19 birim test), C# kodunun sÃ¶z dizimi/tÃ¼r kontrolÃ¼ (AutoCAD API'si taklit edilerek) ve doÄrulama mantÄ±ÄÄ± (mono Ã¼zerinde Ã§alÄ±ÅtÄ±rÄ±ldÄ±). AutoCAD'e Ã¶zgÃ¼ davranÄ±Å (panel, seÃ§im, Ã¶nizleme, iÅlem) **sizin makinenizde ilk kez Ã§alÄ±Åacak**. Ä°lk denemeyi bir DWG **kopyasÄ±nda** yapÄ±n.

## V3'e gÃ¶re neler deÄiÅti
| Alan | V3 | V4 |
|---|---|---|
| Kurulum | 2 terminal, token ortam deÄiÅkeni, her seferinde NETLOAD | `install.ps1` tek komut; AutoCAD aÃ§Ä±lÄ±nca otomatik yÃ¼klenir |
| Servis | Elle baÅlatÄ±lÄ±yordu | Eklenti gizli baÅlatÄ±r, rastgele port+token, AutoCAD kapanÄ±nca kendini kapatÄ±r |
| API anahtarÄ± | Ortam deÄiÅkeni | Panelden girilir, Windows DPAPI ile Åifreli saklanÄ±r |
| SeÃ§im | `SelectImplied` (panel odaktayken gÃ¼venilmez) | `Nesne seÃ§` dÃ¼Ämesi â gerÃ§ek AutoCAD komutu |
| Uygulama | Modeless koddan yazma | GerÃ§ek komut iÃ§inde tek transaction â **tek UNDO**, hata olursa Ã§izim deÄiÅmez |
| Ãnizleme | Sadece metin | Transient grafik hayaleti + TÃ¼rkÃ§e iÅlem Ã¶zeti |
| Ä°Ålemler | taÅÄ±, katman, Ã§izgi, daire | + kopyala, dÃ¶ndÃ¼r, Ã¶lÃ§ekle, katman oluÅtur, polyline, dikdÃ¶rtgen, yazÄ±, blok ekle |
| KonuÅma | Her istek baÄÄ±msÄ±z | Ãok turlu sohbet belleÄi |
| Model hatasÄ± | DÃ¼z hata | GeÃ§ersiz plan otomatik bir kez dÃ¼zeltilir; anlaÅÄ±lÄ±r TÃ¼rkÃ§e hata (kota, anahtar, model) |
| Yedek | DWG'nin yanÄ±na Ã§Ã¶p dosya | `%LOCALAPPDATA%\CadAiDesigner\backups` (son 30) |
| DÃ¼zeltilen hatalar | bkz. `DEGISIKLIKLER.md` | |

## Yapay zekÃ¢ saÄlayÄ±cÄ±sÄ± (herhangi bir API)
Panel â **Ayarlar** â *Yapay zekÃ¢ saÄlayÄ±cÄ±* listesinden seÃ§in; Base URL ve model otomatik dolar, anahtar her saÄlayÄ±cÄ± iÃ§in ayrÄ± ve Åifreli saklanÄ±r.

| SaÄlayÄ±cÄ± | Ãcret | Not |
|---|---|---|
| Google Gemini | **Ãcretsiz anahtar** (aistudio.google.com/apikey) | Ãnerilen; model `gemini-2.5-flash` |
| Groq | Ãcretsiz anahtar (console.groq.com) | HÄ±zlÄ±; dakikalÄ±k limitleri dÃ¼ÅÃ¼k |
| OpenRouter | `:free` modeller Ã¼cretsiz | Tek anahtarla Ã§ok model |
| Ollama | Ãcretsiz, yerel, anahtarsÄ±z | ollama.com + `ollama pull qwen2.5:7b`; gÃ¼Ã§lÃ¼ GPU Ã¶nerilir |
| Codex CLI | ChatGPT aboneliÄiniz | API anahtarÄ± gerekmez; bir kez `codex login` |
| Cursor CLI | Cursor aboneliÄiniz | `cursor-agent login` |
| OpenAI / DeepSeek / Anthropic | Kredi gerekir | |
| Ãzel | â | Herhangi bir OpenAI uyumlu `/chat/completions` adresi |

Hata mesajlarÄ± ayrÄ±ÅtÄ±rÄ±lÄ±r: **kota/kredi bitti** (Ã¶deme gerekir â baÅka saÄlayÄ±cÄ± seÃ§in), **istek sÄ±nÄ±rÄ±** (biraz bekleyin), **anahtar geÃ§ersiz**.
## Gereksinimler
Windows 10/11 Â· AutoCAD **2022** Â· Visual Studio 2022 **Build Tools** (".NET masaÃ¼stÃ¼ derleme araÃ§larÄ±" + ".NET Framework 4.8 hedefleme paketi") Â· Python **3.9+** (PATH'te) Â· bir yapay zekÃ¢ saÄlayÄ±cÄ±sÄ± (Ã¼cretsiz Gemini anahtarÄ±, Ollama, Codex CLI veya OpenAI vb.).

## Kurulum
1. Zip'i bir klasÃ¶re Ã§Ä±karÄ±n (Ã¶r. `C:\AI-Agent-V4`).
2. AutoCAD'i kapatÄ±n. PowerShell'de:
   ```powershell
   cd C:\AI-Agent-V4
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   (AutoCAD farklÄ± yerdeyse: `-AutoCADDir 'D:\Autodesk\AutoCAD 2022'`.) Betik testleri Ã§alÄ±ÅtÄ±rÄ±r, derler ve `%APPDATA%\Autodesk\ApplicationPlugins\CadAiDesigner.bundle` altÄ±na kurar.
3. AutoCAD'i aÃ§Ä±n â `AIDESIGNER` â panelde **Ayarlar** â API anahtarÄ±nÄ± girin â Kaydet.

KaldÄ±rma: `.\uninstall.ps1` (ayarlarÄ± da silmek iÃ§in `-RemoveData`).

## KullanÄ±m
1. Bir DWG **kopyasÄ±** aÃ§Ä±n, kaydedin.
2. Nesneleri seÃ§in â panelde **Nesne seÃ§**.
3. Ä°steÄi yazÄ±n, **Ctrl+Enter**: Ã¶r. *"SeÃ§ilenleri X yÃ¶nÃ¼nde 500 birim kopyala"*, *"SeÃ§ili nesneleri DUVAR katmanÄ±na al, katman yoksa kÄ±rmÄ±zÄ± oluÅtur"*, *"SeÃ§ilenleri sol kenara hizala"*, *"0,0 kÃ¶Åeli 4000Ã3000 dikdÃ¶rtgen Ã§iz"*.
4. Ãnizlemeyi AutoCAD'de inceleyin (yeÅil: deÄiÅen, sarÄ±: kopya, camgÃ¶beÄi: yeni) â **Onayla / Uygula**.
5. BeÄenmezseniz `Ctrl+Z` (tek adÄ±mda geri alÄ±r). Kaydetmek sizde.

`AIDSELECT` ve `AIDAPPLY` panelin iÃ§ komutlarÄ±dÄ±r, elle Ã§alÄ±ÅtÄ±rmayÄ±n.

## GÃ¼venlik ve gizlilik
- YalnÄ±zca izin listesindeki 12 iÅlem Ã§alÄ±ÅÄ±r; silme, komut/script Ã§alÄ±ÅtÄ±rma yoktur. Her plan hem Python'da hem C#'ta (yetkili) doÄrulanÄ±r; seÃ§ili olmayan nesneye dokunulamaz; kilitli/dondurulmuÅ katman reddedilir.
- Servis yalnÄ±zca `127.0.0.1`'de dinler, oturuma Ã¶zel rastgele token ister, proxy'yi atlar.
- **SeÃ§ili nesnelerin bilgisi (tÃ¼r, katman, Ã¶lÃ§Ã¼, metin iÃ§eriÄi) OpenAI'a gÃ¶nderilir.** Gizli/kÄ±sÄ±tlÄ± Ã§izimlerde kurumsal onay olmadan kullanmayÄ±n.
- GÃ¼nlÃ¼kler: `%LOCALAPPDATA%\CadAiDesigner\logs` (istek metni ve uygulanan plan Ã¶zeti; anahtar ve geometri yazÄ±lmaz).

## Sorun giderme
| Belirti | ÃÃ¶zÃ¼m |
|---|---|
| `AIDESIGNER` tanÄ±nmÄ±yor | Kurulum sonrasÄ± AutoCAD'i yeniden aÃ§Ä±n; `SECURELOAD`/gÃ¼ven uyarÄ±sÄ± Ã§Ä±karsa "Always load" deyin |
| "Python 3.9+ bulunamadÄ±" | python.org'dan kurun (Add to PATH) veya Ayarlar'da `python.exe` yolunu girin. Microsoft Store'un sahte `python` kÄ±sayolu sayÄ±lmaz |
| "Servis baÅlamadÄ±" | Mesajdaki son satÄ±rlar nedeni gÃ¶sterir; `%LOCALAPPDATA%\CadAiDesigner\logs\service.log` |
| Kota/401/404 hatasÄ± | Mesaj TÃ¼rkÃ§e aÃ§Ä±klar: kredi, anahtar veya model adÄ± |
| Ãnizleme gÃ¶rÃ¼nmÃ¼yor | PlanÄ± iptal edip yeniden hazÄ±rlayÄ±n (`REGEN`); uygulama Ã¶nizlemesiz de Ã§alÄ±ÅÄ±r |
| Derleme hatasÄ± `error CS...` | SatÄ±rÄ± bana iletin; AutoCAD API'sine Ã¶zgÃ¼ bir uyumsuzluk olabilir |

Elle hata ayÄ±klama: `scripts\dev-service.ps1 -LaunchAutoCAD`.

## Bilinen sÄ±nÄ±rlar
- Ãnizleme her iÅlemi **orijinal geometriden** gÃ¶sterir (aynÄ± nesneye zincirleme iÅlem varsa yaklaÅÄ±ktÄ±r); uygulama doÄru sÄ±rayla yapÄ±lÄ±r.
- Kopyalama `DeepClone` kullanÄ±r; dinamik blok parametreleri, xref iÃ§eriÄi ve annotative ayarlar Ã¶zel olarak ele alÄ±nmaz.
- Koordinatlar her zaman WCS ve Ã§izim birimindedir; UCS dÃ¶ndÃ¼rÃ¼lmÃ¼Åse model yine WCS'e gÃ¶re hesaplar.
- SeÃ§im en fazla 100 nesne gÃ¶nderir; Ã§ok bÃ¼yÃ¼k seÃ§imlerde sonuÃ§ kalitesi dÃ¼Åer.
- Yedek yalnÄ±zca son **kaydedilmiÅ** DWG'dir; kaydedilmemiÅ iÅ iÃ§in UNDO kullanÄ±n.
- Model geometriyi yanlÄ±Å hesaplayabilir; Ã¶nizlemeyi her zaman kontrol edin.

## Ticari Ã¼rÃ¼n yolunda (kod dÄ±ÅÄ± iÅler)
Kod imzalama (Authenticode), MSI/Inno Setup kurulum paketi (Python'u PyInstaller ile tek `.exe` yapÄ±p baÄÄ±msÄ±z kurulum), lisanslama/aktivasyon, Ã§oklu AutoCAD sÃ¼rÃ¼mÃ¼ testi (2023-2025 iÃ§in .NET 8 geÃ§iÅi), otomatik gÃ¼ncelleme, kullanÄ±m koÅullarÄ±/gizlilik politikasÄ±, kendi backend'iniz Ã¼zerinden API anahtarÄ± yÃ¶netimi ve Autodesk App Store yayÄ±n sÃ¼reci.


## Metraj, KeÅif ve Revizyon (salt okunur)

Ãizimi deÄiÅtirmez. ÃÃ§ yol:

* Panelde **Metraj** dÃ¼Ämesi veya komut satÄ±rÄ±nda `AIDMETRAJ` â kapsam (TÃ¼mÃ¼ / SeÃ§im / Katman), yazÄ±lar sayÄ±lsÄ±n mÄ±, revizyon olarak kaydet (Ã¶rn. `R1`), karÅÄ±laÅtÄ±r (Yok / kayÄ±tlÄ± revizyon / baÅka DWG).
* Panelde doÄal dil: "metraj Ã§Ä±kar", "E-KABLO katmanÄ±nÄ±n metrajÄ±", "bunu R2 olarak kaydet", "R1 ile R2 arasÄ±nda ne deÄiÅti", "eski.dwg ile karÅÄ±laÅtÄ±r". AI salt okunur `takeoff` eylemi Ã¼retir; onay gerekmez.

**Neler sayÄ±lÄ±r (yalnÄ±zca Model alanÄ±):**
* Bloklar: efektif ada gÃ¶re adet; TAG/TIP/ÃLÃÃ/MODEL/GÃÃ/KESÄ°Tâ¦ Ã¶znitelikleri ve dinamik gÃ¶rÃ¼nÃ¼rlÃ¼k/geniÅlik deÄerleri ayrÄ± kalem oluÅturur.
* Kablo tavasÄ± (dinamik Uzunluk/Length/Boy/Distance parametresi olan ya da adÄ±nda "tava/tray/kanal" geÃ§en bloklar): geniÅliÄe gÃ¶re toplam metre (parametre deÄeri, yoksa X Ã¶lÃ§eÄi Ã blok boyu).
* Line/Polyline/Arc/Spline: katman baÅÄ±na toplam uzunluk (m). KapalÄ± polyline ve tarama: katman baÅÄ±na alan (mÂ²). Ä°steÄe baÄlÄ± yazÄ± adedi.
* Birimler INSUNITS'ten metreye Ã§evrilir (tanÄ±msÄ±zsa mm kabul edilir).

**KeÅif:** `%APPDATA%\CadAiDesigner\birim_fiyat.csv` (veya `.xlsx`), sÃ¼tunlar `poz_no;kalem;birim;birim_fiyat` (`;` veya `,`; `1.234,50` biÃ§imi olur). Kalem adÄ± metraj kalemiyle aynÄ±ysa ya da metraj kaleminin iÃ§inde geÃ§iyorsa eÅleÅir (TÃ¼rkÃ§e harf/bÃ¼yÃ¼k-kÃ¼Ã§Ã¼k duyarsÄ±z). Dosya yoksa ilk metrajda Ã§izimin kalemleriyle Åablon oluÅturulur. FiyatÄ± olmayan kalemler raporda listelenir. Ãrnek: `templates\birim_fiyat_sablon.csv`.

**ÃÄ±ktÄ±lar:**
* Excel: DWG'nin yanÄ±nda `<dwg>_metraj_<tarih>.xlsx` (sayfalar: Metraj, KeÅif, Revizyon FarkÄ±). Ek kÃ¼tÃ¼phane gerekmez. DWG kaydedilmemiÅse/klasÃ¶r yazÄ±lamÄ±yorsa `%APPDATA%\CadAiDesigner\metraj`.
* Revizyonlar: `<dwg klasÃ¶rÃ¼>\<dwg>_revizyonlar\<etiket>.json`.
* Fark tablosu: kalem, eski, yeni, fark (+/â), durum, tutar farkÄ±.

**SÄ±nÄ±rlamalar:** iÃ§ iÃ§e bloklar ve XREF'ler aÃ§Ä±lmaz; Paper Space sayÄ±lmaz; kablo tavasÄ± kÃ¶Åe/dirsek parÃ§alarÄ± ayrÄ± kalem deÄildir; fiyat eÅleÅmesi metin tabanlÄ±dÄ±r.
## Attribute (etiket) bloklarÄ± â Excel

`AIDATTEXPORT` komutu, panelde **Attribute Excel** dÃ¼Ämesi veya panelde "etiket bloklarÄ±nÄ± excele aktar" isteÄi.

- Kapsam: seÃ§im varsa seÃ§ili bloklar; yoksa `Attributeli` (varsayÄ±lan) / `Tumbloklar` / `Blok` (Ã§izimdeki attribute'lu blok adlarÄ± listelenir, ad veya numara girilir) / `Secim`. Ä°steÄe baÄlÄ± olarak pafta (layout) alanlarÄ± da dahil edilir. Dinamik bloklar gerÃ§ek (efektif) adlarÄ±yla gruplanÄ±r.
- ÃÄ±ktÄ±: DWG'nin yanÄ±nda `<dwg>_attribute_<tarih>.xlsx` (kaydedilmemiÅ Ã§izimde `%APPDATA%\CadAiDesigner\metraj`), otomatik aÃ§Ä±lÄ±r.
  - **TÃ¼mÃ¼** sayfasÄ± ve her blok adÄ± iÃ§in ayrÄ± sayfa. SÃ¼tunlar: Blok, Katman, Handle, X, Y (+ pafta varsa Alan), sonra her attribute etiketi (tanÄ±mdaki sÄ±rayla; etiket adÄ± ne olursa olsun).
  - SayÄ±sal gÃ¶rÃ¼nen deÄerler (`12,5`, `12.5`, `1.234,5`) sayÄ± olarak yazÄ±lÄ±r; sayÄ±sal sÃ¼tunlara **TOPLAM** satÄ±rÄ± eklenir (Ã¶r. KABLO METRAJI).
  - Ãok satÄ±rlÄ± (MText) ve sabit (constant) attribute'lar da dahildir.
  - Ä°steÄe baÄlÄ± **Ãzet** sayfasÄ±: bir etikete gÃ¶re gruplar (Ã¶r. `KABLO` â her kablo tipi iÃ§in adet ve KABLO METRAJI toplamÄ±).
- Salt okunurdur; Ã§izimde hiÃ§bir Åey deÄiÅmez. Esc her adÄ±mda gÃ¼venle iptal eder.

## Tava yerleÅtirme (AIDTAVA / TAVA / KT)

AI kullanmadan, polyline/Ã§izgi ile Ã§izilmiÅ plana `cabletray` bloÄunu EN ve BOY ayarlÄ± yerleÅtirir. Panelde **Tava YerleÅtir** dÃ¼Ämesi de aynÄ± komutu Ã§alÄ±ÅtÄ±rÄ±r.

1. SeÃ§im: Ã¶nceden seÃ§ili Ã§izgi/polyline'lar; yoksa seÃ§im istenir, `Tumu` ile plan katman(lar)Ä±ndaki tÃ¼m Ã§izgi/polyline'lar (virgÃ¼llÃ¼ liste, `*` = tÃ¼mÃ¼; hatÄ±rlanÄ±r).
2. SeÃ§enekler (Enter = Devam): `[En/Yukseklik/Kalinlik/Kopyala/Mod/Baglanti/Devam]`
   - **EN**: otomatik â paralel Ã§ift Ã§izgi aralÄ±ÄÄ±ndan (50/100/150/200/300/400/500/600'e yuvarlanÄ±r, Â±%15), tek Ã§izgide polyline kalÄ±nlÄ±ÄÄ±ndan, yoksa son EN (varsayÄ±lan 300). `En` ile elle girilir (0 = otomatiÄe dÃ¶n).
   - **Yukseklik / Kalinlik**: YUKSEKLIK/KALINLIK attribute'larÄ± (varsayÄ±lan 40 / 2, hatÄ±rlanÄ±r). `Kopyala`: mevcut bir tavadan EN, yÃ¼kseklik ve kalÄ±nlÄ±ÄÄ± alÄ±r.
   - **Mod**: `Otomatik` (Ã§ift Ã§izgi varsa yalnÄ±z Ã§iftler, uÃ§ kapama Ã§izgileri yok sayÄ±lÄ±r; yoksa tek Ã§izgi = tava ekseni), `Cift`, `Tek`.
   - **Baglanti** (varsayÄ±lan Evet): ardÄ±ndan AIDBAGLANTI mantÄ±ÄÄ±yla ek parÃ§alar yalnÄ±zca yeni tavalar ve onlara deÄen mevcut tavalar iÃ§in yerleÅtirilir.
3. Her dÃ¼z segmente bir blok: BOY = segment boyu, tava eksene ortalanÄ±r, soldan saÄa/aÅaÄÄ±dan yukarÄ± dÃ¶ndÃ¼rÃ¼lÃ¼r, katman `E_CT` (yoksa oluÅturulur). Yaylar kiriÅ olarak alÄ±nÄ±r. Mevcut bir tavayla Ã§akÄ±Åan segment atlanÄ±r (tekrar Ã§alÄ±ÅtÄ±rmak gÃ¼venlidir).
4. Hepsi tek UNDO adÄ±mÄ±dÄ±r; Esc her soruda gÃ¼venle Ã§Ä±kar. Ayarlar `%LOCALAPPDATA%\CadAiDesigner\tava.json`.

## Plandan keÅif (AIDPLANKESIF / PK)

Ãizgi/polyline ile Ã§izilmiÅ plandan, **hiÃ§ blok eklemeden ve Ã§izimi deÄiÅtirmeden** kablo tavasÄ± + ek parÃ§a keÅfi Ã§Ä±karÄ±r. Panelde **Plandan KeÅif** dÃ¼Ämesi; panelde "plandan keÅif Ã§Ä±kar" isteÄi de aynÄ± komutu baÅlatÄ±r.

- Sorular AIDTAVA ile aynÄ±dÄ±r (seÃ§im veya `Tumu` + katmanlar, EN otomatik/elle, Yukseklik/Kalinlik, Mod; deÄerler `tava.json`'dan hatÄ±rlanÄ±r). Ek parÃ§alar her zaman hesaplanÄ±r.
- Hesap: AIDTAVA + AIDBAGLANTI bellekte birebir uygulanÄ±r (YD, YT, dal geniÅliÄinde YarÄ±m T, AD; leg/clearance kesimleri), sonuÃ§ Ã¶lÃ§Ã¼lÃ¼r ve iÅlem geri alÄ±nÄ±r â Ã§izime hiÃ§bir Åey yazÄ±lmaz, UNDO gerekmez. Bu yÃ¼zden sonuÃ§, aynÄ± plana AIDTAVA uygulandÄ±ktan sonraki AIDMETRAJ ile aynÄ±dÄ±r (Drawing2 kopyasÄ±nda doÄrulandÄ±). Ãizimde zaten bulunan tavalar dikkate alÄ±nmaz.
- ÃÄ±ktÄ±: metraj ile aynÄ± kalem adlarÄ± ve Excel biÃ§imi, `birim_fiyat.csv` fiyatlarÄ±; DWG'nin yanÄ±nda `<dwg>_plankesif_<tarih>.xlsx`, otomatik aÃ§Ä±lÄ±r. Ãzet komut satÄ±rÄ±nda ve panelde.

## AIDHATCHFIX (tava hatch onarÄ±mÄ±)

Hatch'i bozuk gÃ¶rÃ¼nen cabletray bloklarÄ±nÄ± (seÃ§im veya `Tumu`) dinamik tanÄ±mdan yeniden yerleÅtirir; konum, aÃ§Ä±, katman, EN/BOY ve attribute deÄerleri korunur. SonrasÄ±nda Ã¼Ã§gen grip/STRETCH ile uzatma doÄru Ã§alÄ±ÅÄ±r. TeÅhis: `AIDHATCHDIAG`.

## SeÃ§im baÄlamÄ± (sÄ±nÄ±rsÄ±z)

SeÃ§ilen tÃ¼m nesneler yapay zekaya gÃ¶nderilir (eski 100 sÄ±nÄ±rÄ± kaldÄ±rÄ±ldÄ±). 100'den bÃ¼yÃ¼k seÃ§imler `selected_compact` tablosu olarak (handle, type, layer, block_name, x, y, z, rotation_deg, extents, props) kompakt gÃ¶nderilir. `settings.json`: `MaxObjects` (0 = sÄ±nÄ±rsÄ±z, varsayÄ±lan) ve `CompactAbove` (100). KÄ±rpma olursa baÄlamda `selection_truncated` ile sayÄ± belirtilir. TeÅhis: `AIDCTXDIAG`.

## Planlar arasÄ± hizalama: AIDREFNOKTA (RN) ve AIDPLANKOPYA (PKOPYA)

- **Referans noktalarÄ±** (panel dÃ¼Ämesi veya `RN`): Ã¶nce kaynak plandan bir nokta, sonra hedef plandaki aynÄ± fiziksel noktayÄ± fareyle (osnap) seÃ§in; koordinat yazmak gerekmez. Ä°steÄe baÄlÄ± ikinci Ã§ift dÃ¶nme ve Ã¶lÃ§ek hesaplatÄ±r. Noktalar oturum boyunca Ã§izim baÅÄ±na saklanÄ±r, panel durum Ã§ubuÄunda gÃ¶rÃ¼nÃ¼r ve yapay zeka baÄlamÄ±na `reference_points` / `reference_transform` olarak girer.
- **AIDPLANKOPYA / PKOPYA**: seÃ§ili nesneleri (veya `Katman`) referans noktalarÄ±yla kaynaÄÄ±n planÄ±ndan hedef plana kopyalar/taÅÄ±r (bloklar, dÃ¶nme, attribute korunur; tek UNDO). Yapay zeka da `plan_copy` eylemiyle bunu baÅlatÄ±r.

## AIDOLCU (OLC): blok merkezlerinden ardÄ±ÅÄ±k Ã¶lÃ§Ã¼

Panelde "Blok Ã¶lÃ§Ã¼sÃ¼" veya `OLC`: seÃ§ilen bloklarÄ±n (karma tipler) geometrik merkezleri (attribute hariÃ§; `Nokta` ile ekleme noktasÄ± seÃ§ilebilir) bulunur, 1 birimden yakÄ±n merkezler birleÅtirilir, sÄ±ranÄ±n ana doÄrultusu (yatay/dÃ¼Åey/aÃ§Ä±lÄ±) hesaplanÄ±r ve ardÄ±ÅÄ±k dÃ¶ndÃ¼rÃ¼lmÃ¼Å Ã¶lÃ§Ã¼ zinciri oluÅturulur. ÃlÃ§Ã¼ Ã§izgisinin tarafÄ±/mesafesi tÄ±klanarak verilir (Enter = varsayÄ±lan). GeÃ§erli Ã¶lÃ§Ã¼ stili ve katman kullanÄ±lÄ±r; `Toplam` ile toplam Ã¶lÃ§Ã¼ eklenir. Tek UNDO.

## Attribute Ä°Ã§e Aktar (AIDATTIMPORT / ATI)

AIDATTEXPORT ile alÄ±nan Excel'de deÄerleri dÃ¼zenleyip kaydedin, sonra panelde 'Attribute Ä°Ã§e Aktar' (veya ATI). Dosya seÃ§ilir (varsayÄ±lan: DWG yanÄ±ndaki en son *_attribute_*.xlsx), Ã¶nizleme gÃ¶sterilir, Evet ile tek UNDO adÄ±mÄ±nda yazÄ±lÄ±r.
- EÅleÅme 'Handle (deÄiÅtirmeyin)' sÃ¼tunu ile; handle yoksa/bozuksa blok adÄ± + X/Y (0.01) ile.
- Blok sayfalarÄ± ve TÃ¼mÃ¼ birlikte okunur, aynÄ± deÄer iki kez uygulanmaz; ikisi farklÄ± deÄiÅtirildiyse blok sayfasÄ± kazanÄ±r (Ã§akÄ±Åma raporlanÄ±r). TOPLAM satÄ±rlarÄ± ve Ãzet yok sayÄ±lÄ±r.
- YalnÄ±zca deÄeri deÄiÅen attribute etiketleri yazÄ±lÄ±r; Blok/Katman/Handle/X/Y/Alan ve sabit (constant) etiketler yazÄ±lmaz. BoÅaltÄ±lan hÃ¼cre deÄeri siler.
- SayÄ±lar: deÄer aynÄ±ysa Ã§izimdeki metin korunur (1,5 / 60.0); yeni sayÄ±larda ondalÄ±k ayÄ±rÄ±cÄ± eski deÄere gÃ¶re, yoksa virgÃ¼l; 60.0 -> 60.
- Kilitli katmandaki bloklar atlanÄ±r ve raporlanÄ±r.


## ArmatÃ¼r yerleÅimi (AIDARMATUR / ARM)

Panel: 'ArmatÃ¼r yerleÅimi'. ArmatÃ¼r bloÄunu tÄ±klayÄ±n, aÃ§Ä±k/kapalÄ± polyline'larÄ± (Ã§oklu) seÃ§in, x mesafesini girin veya iki nokta tÄ±klayÄ±n.
- AÃ§Ä±k polyline (koridor): tek sÄ±ra; uÃ§lardan x, armatÃ¼rler arasÄ± ~2x (eÅit daÄÄ±tÄ±lÄ±r), segment yÃ¶nÃ¼ne dÃ¶ndÃ¼rÃ¼lÃ¼r.
- KapalÄ± polyline (oda): en uzun kenara yÃ¶nlÃ¼ grid; duvardan x, aralÄ±k ~2x, ortalanÄ±r; poligon iÃ§inde ve duvara >= 0.9x olan noktalar kalÄ±r.
- Kopyalar aynÄ± bloÄun yeni insert'i: Ã¶lÃ§ek, katman, attribute ve dinamik Ã¶zellikler korunur. Tek UNDO adÄ±mÄ±.


### ArmatÃ¼r yerleÅimi v2 (DIALux tarzÄ±)
- Alan: [Polyline/Icnokta] â Icnokta: oda iÃ§ine tÄ±klayÄ±n, sÄ±nÄ±r otomatik bulunur (BOUNDARY; bulunamazsa noktayÄ± iÃ§eren en kÃ¼Ã§Ã¼k kapalÄ± polyline).
- YÃ¶ntem: [Aralik/Adet/Mesafe]. Aralik: maks. aralÄ±k S (varsayÄ±lan 3000 mm) -> n = ceil(L/S), d = L/(2n), aralÄ±k 2d, duvar/uÃ§ = d. Adet: koridor n, oda NxM veya toplam. Mesafe: eski sabit x.
- DikdÃ¶rtgen oda: yÃ¶nler ayrÄ± ayrÄ± dA=A/(2nA), dB=B/(2nB). DÃ¼zensiz oda: en uzun kenara yÃ¶nlÃ¼ grid; gerÃ§ek duvar mesafesi < d olan noktalar iÃ§e kaydÄ±rÄ±lÄ±r, olmazsa atÄ±lÄ±r (raporlanÄ±r).



## AydÄ±nlatma hesabÄ± (lÃ¼men yÃ¶ntemi)
- `ARM` â YÃ¶ntem **Lux**: hedef aydÄ±nlÄ±k (oda 500 lx, koridor 100 lx), H (3 m), Î¦ (bloktaki LUMEN/LM/FLUX/AKI/ISIKAKISI attribute'undan, yoksa sorulur), MF 0,8, yansÄ±ma 0,7/0,5/0,2, Ã§alÄ±Åma dÃ¼zlemi 0,85 m, koridor geniÅliÄi 2 m.
  k = A/(hÂ·P/2) (dikdÃ¶rtgende = aÂ·b/(h(a+b))), UF tablodan enterpolasyonla, n = ceil(EÂ·A/(Î¦Â·UFÂ·MF)), oda oranÄ±na gÃ¶re NÃM grid (grid n'i yukarÄ± tamamlayabilir).
- DiÄer yÃ¶ntemlerde yerleÅimden sonra "AydÄ±nlatma hesabÄ±?" sorulur. Rapor: Eort, Eminâ, Uoâ (komut satÄ±rÄ± + panel), isteÄe baÄlÄ± MTEXT etiket ve Excel (`*_aydinlatma_*.xlsx`, servis `/xlsx`).
- `AIDAYDHESAP` / `AHESAP` (panel: "AydÄ±nlatma hesabÄ±"): oda polyline / iÃ§ nokta + mevcut armatÃ¼rler (yalnÄ±z oda iÃ§indekiler sayÄ±lÄ±r).
- **YaklaÅÄ±ktÄ±r:** UF tablosu genel doÄrudan-geniÅ daÄÄ±lÄ±mlÄ± armatÃ¼r iÃ§in tipik deÄerlerdir (CIBSE/LiTG tarzÄ± Ã¼retici UF tablolarÄ±, zemin 0,2). Uo nokta hesabÄ± Lambert kabulÃ¼, yalnÄ±z doÄrudan Ä±ÅÄ±k (E = Î¦hÂ²/(Ïdâ´)), duvar bandÄ± 0,5 m. Kesin sonuÃ§ iÃ§in DIALux/Ã¼retici fotometrisi kullanÄ±n.

## Oda blok sayımı (AIDODASAYIM / OSAY)

Panel: **Oda blok sayımı**. Varsayılan: **tek pencere seçimi** (kapalı polyline istemez).
- Mahal etiketi: blok tıkla + attribute seç (veya Enter=otomatik); çizimde hatırlanır (**Ayarlar** ile değişir). Pinlenen blok sayılmaz.
- **Duvar boşlukları:** varsayılan 1200 mm (birime göre ölçeklenir; Ayarlar’da değişir). Kapı açıklıklarını köprüler, T-birleşim / kısa kalan uçları uzatır, sonra düzlemsel yüz; yedek TraceBoundary (HPGAPTOL) ve ışın tarama. Bulunamayan odalar raporlanır.
- Excel: `*_odasayim_*.xlsx`.

## Oda sınırı (AIDODASINIR / OSINIR)

Panel: **Oda sınırı çiz**. Tek pencere seçimi; OSAY ile aynı oda tespiti (mahal etiketi, 1200 mm boşluk). Her oda için kapalı LWPOLYLINE katman ``A_ODA_SINIR`` (yoksa oluşturulur, yeşil); oda adı XData ``CADAI_ODA``. Aynı sınır varsa atlar. Tek UNDO.


## Z=0 (ZSIFIRLA / AIDZSIFIRLA)

Panelde **Z=0** düğmesi veya komut satırında `ZSIFIRLA` (alias `AIDZSIFIRLA`). Seçim istenmez.

Geçerli alandaki bütün nesneler tek işlemde WCS Z=0 yapılır: Model, ya da açık paftanın kağıt alanı. Bu alandaki blokların tanımları da (iç içe bloklar ve dinamik bloğun *U kopyaları) düzlenir; bloğun Origin Z değeri 0 yapılır (X,Y durur). Xref tanımlarına girilmez. Blok referansının ekleme Z değeri ve attribute'ları da sıfırlanır.

- Çizgi, spline ve 3B polyline: nokta Z=0. Yalnız düşey çizgi (XY boyu sıfır) atlanır.
- LW polyline WCS noktalarına izdüşürülür; normal `+Z`, Elevation 0. Normali `-Z` olanlarda bulge ters çevrilir, plan değişmez. Z eksenine çok yakın normaller `+Z` yapılır.
- Yay, çember, elips, yazı, blok, ölçü, tarama ve multileader: normal `+Z`, WCS XY korunur. Eğik olanlar da atlanmaz.
- Kilitli katmanlar işlem sırasında açılır, bitince yeniden kilitlenir. Özet, kilitli katmandaki adedi ve normali düzlenen adedi yazar. İlk hata komut satırına basılır.
- Çizimin `ELEVATION` sistem değişkeni 0 yapılır. UCS Dünya değilse özet, UCS orijininin Z değerini yazar (UCS değiştirilmez).
- Özet: düzeltilen nesne, sıfırlanan kot, sıfırlanan kalınlık, normali düzlenen, atlanan düşey, kilitli katman, blok tanımı ve blok içi düzeltilen. Tek UNDO.
