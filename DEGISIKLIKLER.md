# V3 incelemesinde bulunan sorunlar ve düzeltmeler

## Hatalar (kullanıcıyı doğrudan etkiler)
1. **Yeni nesneler her zaman ModelSpace'e ekleniyordu**, paper space'teyken bile → artık aktif uzaya (`CurrentSpaceId`) eklenir.
2. **Kilitli katmandaki nesne** `eOnLockedLayer` istisnasıyla kriptik hata veriyordu → açık Türkçe mesaj, işlem hiç başlamadan iptal.
3. **`dz` eksikse** `KeyNotFoundException` ("The given key was not present") → varsayılan 0.
4. **HTTP 400/403 yanıtlarında gerçek hata gizleniyordu** (yalnızca "(400) Bad Request") → servis her zaman JSON hata döner, panel gösterir.
5. **`secrets.compare_digest` Türkçe/ASCII-dışı başlıkta TypeError** → bayt karşılaştırma.
6. **`temperature` alanı bazı modellerde (o-serisi/gpt-5) HTTP 400** → koşullu gönderim.
7. **OpenAI hata gövdesi okunmuyordu** (kota, yanlış anahtar, yanlış model hepsi "Model API HTTP 4xx") → ayrıştırılıp Türkçe açıklanır; anahtar maskelenir.
8. **Kurumsal proxy loopback trafiğini bozabilirdi** → `Proxy = null`.
9. **Seçim**: `SelectImplied()` modeless panelden çağrılınca seçim kaybolabiliyordu → `AIDSELECT` komutu.
10. **Dinamik bloklarda `*U12` gibi anonim ad** modele gidiyordu → gerçek blok adı.
11. **Parmak izi yalnızca handle'dı**: nesne taşınsa/katmanı değişse fark edilmiyordu → katman + sınır kutusu da karşılaştırılır.
12. **Yedek dosyası DWG'nin yanına çöp bırakıyordu** ve onaydan sonra alınıyordu → kullanıcı klasörüne, sınırlı sayıda, doğru sırayla.
13. `csproj` `System.Security` referansı içermiyordu (DPAPI için gerekli) ve derleme `x64` hedeflemiyordu → eklendi.
14. PowerShell 5.1'de native komutun stderr'i betiği durduruyordu (unittest/msbuild) → `Run-Native` sarmalayıcısı; betikler UTF-8 BOM'lu (Türkçe karakter bozulmaz).
15. `JavaScriptSerializer` dizileri `ArrayList`/`object[]` olarak verebilir; kod yalnızca `ArrayList` varsayıyordu → genel `IEnumerable` okuma.

## Tasarım zayıflıkları
- İki terminal + token enjeksiyonu + her oturumda NETLOAD → otomatik yükleme ve otomatik servis.
- Uygulama modeless koddan yapılıyordu (belge kilidi/UNDO belirsiz) → gerçek komut + tek transaction.
- Çok turlu bellek yoktu, katman oluşturma/döndürme/ölçekleme/kopyalama/blok/yazı yoktu → eklendi.
- Testler yalnızca 2 satırdı → 19 Python testi (doğrulama, yeniden deneme, HTTP, yetkilendirme).
- Model geçersiz plan üretirse kullanıcı hata görüyordu → otomatik bir kez düzeltme isteği.
