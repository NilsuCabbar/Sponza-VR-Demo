# Sponza VR Demo – Unity 6.3 + Quad Views Aç/Kapa

Unity 6.3 (6000.3.21f1) + URP + OpenXR projesi. Sahnede Khronos **Sponza** modeli var. Işığı bake edilmiş. HTC Vive Pro Eye için **quad views** (foveated rendering) Play modunda **1** tuşuyla açılıp kapatılabiliyor.

Quad views kurulumu [HTC-VIVE-Pro-Eye-Quad-View-](https://github.com/NilsuCabbar/HTC-VIVE-Pro-Eye-Quad-View-) projesinden alındı.

## Sahne: `Assets/Scenes/SampleScene.unity`

### Sponza neden bozuk görünüyordu?
Sponza bozuk, çok karanlık ve mavimsi görünüyordu. Bunun dört sebebi vardı:

1. **Lightmap UV'si (UV2) yoktu.** glTFast, Sponza'yı 16-bit index'li tek bir mesh olarak import ediyor (102 alt mesh, ~192 bin köşe noktası). UV2 üretilirken model dikişlerinden kesildiği için köşe sayısı artıyor ve 16-bit formatın sınırı (65.535) aşılıyor. Konsolda şu hata çıkıyordu: `SetPerIndexUVs failed because the output has too many vertices`. Import ayarında "Generate Lightmap UVs" açık olsa da her seferinde sessizce başarısız oluyordu. UV2 olmayınca bake, doku UV'lerini kullandı. Bu UV'ler tekrar eden ve üst üste binen bölgeler içerdiği için lightmap bozuk çıktı.
2. **Güneş ışığı `Baked` moddaydı.** Gerçek zamanlı doğrudan ışık ve gölge yoktu.
3. **Reflection probe yoktu.** Kapalı avludaki yüzeyler yansıma olarak mavi gökyüzünü kullanıyordu.
4. **Lighting Settings asset'i yoktu.** Bake, Unity'nin gizli varsayılan ayarlarıyla yapılıyordu.

### Yapılan düzeltmeler
- **UV2'li mesh:** `Assets/glTF/Generated/Sponza_LightmapUV.asset`. Orijinal mesh'in 32-bit index'li bir kopyası. Köşe, normal, UV ve alt mesh bilgileri birebir aynı. UV2, `Unwrapping.GenerateSecondaryUVSet` ile üretildi. Sahnedeki Sponza bu mesh'i kullanıyor. Orijinal `Sponza.gltf` değiştirilmedi.
- **Lighting Settings:** `Assets/Scenes/SampleScene/SampleSceneLighting.lighting`.
  - Progressive GPU lightmapper, Baked Indirect mixed mod.
  - 12 texel/birim çözünürlük, 2048 atlas.
  - 3 sekme (bounce), AO açık.
  - Indirect Intensity 1.6, Albedo Boost 1.4.
- **Directional Light:** Mixed mod, yoğunluk 4.5, hafif sıcak renk. Açısı (78°, 30°), avlunun açık tavanından içeri girecek şekilde.
- **Reflection Probe:** Bütün Sponza'yı kapsıyor. Baked, box projection açık.
- **Kamera:** Şablondan kalan `Main Camera` kapatıldı. Sponza'nın dışında duruyordu ve XR Origin'in kamerasıyla çakışıyordu. Artık XR Origin'in kamerası kullanılıyor.
- **Plane:** Sponza'nın zeminiyle aynı yükseklikte olduğu için iki yüzey üst üste binip titreşiyordu. Görüntüsü (renderer) kapatıldı. Collider'ı duruyor, VR'da üzerinde durulabiliyor.

- **Static batching kapatıldı:** Play modunda Unity statik mesh'leri birleştirirken 32-bit index'li büyük Sponza mesh'i bozuluyordu (üçgenler uzayıp sahne parçalanıyordu). Edit modunda görünmüyordu. Sponza tek mesh olduğu için static batching'in zaten bir faydası yoktu; diğer static bayrakları (lightmap vb.) duruyor.

Not: `Sponza.gltf` sahneye yeniden sürüklenirse, yeni kopya yine UV2'siz orijinal mesh'i kullanır. O durumda `Sponza_LightmapUV` mesh'ini elle atamak gerekir.

## Quad Views (foveated rendering) aç/kapa

### Dosyalar
- `Assets/Foveation/QuadViewsFoveationFeature.cs`: `XR_VARJO_quad_views` uzantısını isteyen OpenXR feature. Unity her göz için iki görüntü çizer: biri geniş ve düşük çözünürlüklü, diğeri dar ve tam çözünürlüklü. Teşhis bilgisini Console'a `[QuadViews]` ile başlayan satırlarla yazar.
- `Assets/Foveation/Editor/FoveationProjectSetup.cs`: **Tools → Foveated Rendering → Configure Eye-Tracked Quad Views (Vive Pro Eye)** menüsü. Şunları ayarlar:
  - Direct3D 11
  - Multi-pass render modu
  - Quad Views, HTC Vive Controller ve Eye Gaze feature'larını açar; VRS foveation'ı kapatır.
- `Assets/Foveation/QuadViewsToggle.cs`: Sahnedeki **Quad Views Toggle** objesinde duruyor.

### Nasıl kullanılır?
- Play modunda **1** tuşuna bas. Tuş Inspector'dan değiştirilebilir.
- Ya da Inspector'daki **Quad Views** tikini aç ya da kapat. Tik, o anki durumu gösterir.

### Nasıl çalışıyor?
Quad views, OpenXR oturumu açılırken seçilen bir görüntü yapılandırması. Oturum açıkken değiştirilemiyor. Unity de OpenXR çalışırken feature'ın açılıp kapatılmasına izin vermiyor (`OpenXRFeature.enabled cannot be changed while OpenXR is running`). Bu yüzden script her geçişte şunları yapar:

1. XR'ı durdurur (`StopSubsystems` + `DeinitializeLoader`).
2. `QuadViewsFoveationFeature`'ı açar ya da kapatır.
3. XR'ı yeniden başlatır (`InitializeLoader` + `StartSubsystems`).

Bu sırada gözlükte bir iki saniyelik siyah ekran olur. Uygulama ve sahne kapanmaz.

Editor'de Play modundan çıkınca feature eski haline döner. Böylece Play modunda yapılan geçişler OpenXR ayarlarını kalıcı olarak değiştirmez.

### Proje ayarları
- XR Plug-in Management: PC (Standalone) için OpenXR açık, "Initialize XR on Startup" açık.
- Windows grafik API'si: yalnızca Direct3D 11. Quad-Views-Foveated katmanı yalnızca bununla çalışıyor.
- OpenXR render modu: Multi-pass. Single Pass Instanced + quad views'ın PC'de çöktüğü bildirilmiş.
- OpenXR paketi 1.16.1. Bu sürüm `XR_VARJO_quad_views`'ı destekliyor.

## Gözlüksüz debug: Quad Views Simulator
Gözlük takılı değilken quad views'ı masaüstünde taklit eder. Sahnedeki **Quad Views Simulator** objesinde duruyor. Gözlük bağlanıp XR çalışmaya başlayınca kendini kapatır; o zaman gerçek quad views'ı Quad-Views-Foveated katmanı yapar.

**Nasıl çalışıyor?** Katmanın yaptığını ekranda yapıyor:
1. Ana kamera bütün görüntüyü düşük çözünürlükte çizer (çevre).
2. İkinci bir kamera sadece odak bölgesini, o bölgeye ait dar bir açıyla tam çözünürlükte çizer (odak).
3. Bir shader (`Assets/Foveation/Shaders/QuadViewsSimulator.shader`) ikisini kenarları yumuşatarak ekranda birleştirir.

Odak görüntüsü çevreyle piksel piksel hizalı. Bloom ve SSAO gibi ekran efektleri her görüntüye ayrı uygulandığı için sınırda küçük farklar görülebilir; gerçek quad views'da da aynısı olur.

**Tuşlar** (sağ üstteki yardım panelinde de yazıyor):
| Tuş | Ne yapar |
|---|---|
| **1** | Simülasyonu aç/kapat. Gözlük bağlıyken aynı tuş gerçek quad views'ı açıp kapatır. |
| **Ok tuşları** | Bakış noktasını (simüle edilen göz takibi) hareket ettirir. Shift ile 3 kat hızlı. |
| **C** | Bakışı ortaya al. |
| **M** | Sabit (göz takipsiz) ile göz takipli mod arasında geç. |
| **V** | Odak bölgesini göster/gizle: yeşil çerçeve odak, sarı kesikli çizgi yumuşatmanın başladığı yer, kırmızı artı bakış noktası. Çevre hafif maviye boyanır. |
| **O** | Simülasyon sırasında SSAO'yu aç/kapat (aşağıya bakın). |
| **H** | Yardım panelini gizle/göster. |
| **Sağ fare tuşu + fare** | Kamerayı çevir. |
| **WASD, E/Q** | Kamerayı hareket ettir, yukarı/aşağı. |

**Inspector ayarları** (Play modunda değiştirince anında uygulanır). Katmanın `settings.cfg` adlarıyla aynı, varsayılanları da katmanınkiyle aynı:
| Ayar | Katmandaki adı | Varsayılan |
|---|---|---|
| Mode | – | Eye tracked |
| Peripheral Multiplier | `peripheral_multiplier` | 0.5 |
| Focus Multiplier | `focus_multiplier` | 1.0 |
| Horizontal / Vertical Focus Section | `horizontal_focus_section`, `vertical_focus_section` | 0.35 / 0.35 |
| Vertical Focus Offset | `vertical_focus_offset` | 0 |
| Horizontal / Vertical Fixed Section | `horizontal_fixed_section`, `vertical_fixed_section` | 0.5 / 0.45 |
| Vertical Fixed Offset | `vertical_fixed_offset` | 0 |
| Smoothen Focus View Edges | `smoothen_focus_view_edges` | 0.2 |
| Sharpen Focus View | `sharpen_focus_view` | 0.7 |
| Gaze | – | (0, 0) = ortası |
| Gaze Follows Mouse | – | Kapalı. Açarsan bakış fare imlecini takip eder. |

**Tek tuşla gözlükteki gibi yapmak:** Inspector'ın en üstündeki **Match the headset** bölümünde üç düğme var. Edit modunda da Play modunda da çalışırlar ve Ctrl+Z ile geri alınabilirler:
- **Quad-Views-Foveated defaults:** Bütün çözünürlük, odak bölgesi, yumuşatma ve keskinleştirme ayarlarını katmanın varsayılanlarına döndürür, bakışı ortalar. Gözlükte `settings.cfg` yoksa katman bu değerleri kullanır.
- **Load installed layer settings:** Katmanın bu bilgisayarda gerçekten kullandığı değerleri yükler: önce varsayılanlar, sonra `%ProgramFiles%\OpenXR-Quad-Views-Foveated\settings.cfg`, sonra `%LocalAppData%\Quad-Views-Foveated\settings.cfg`. Katman gibi, bölümsüz satırları, `[SteamVR...]`, `[app:<ürün adı>]` ve `[exe:<exe adı>]` bölümlerini uygular; diğerlerini (örneğin `[Oculus]`) atlar. Hangi satırları uyguladığını Console'a yazar. Katman kurulu değilse düğme gri görünür.
- **Copy as settings.cfg:** Aşağıdaki paragrafa bakın.

Mod (sabit / göz takipli), SSAO, bakış kontrolleri ve tuşlar bu düğmelerden etkilenmez.

**Aynı ayarları gözlükte kullanmak:** **Copy as settings.cfg** düğmesine bas (bileşenin ⋮ menüsünde de var). Ayarlar katmanın formatında panoya kopyalanır. `%LocalAppData%\Quad-Views-Foveated\settings.cfg` dosyasına yapıştır. Katman bu dosyayı XR oturumu başlarken okur; gözlükte **1**'e iki kez basmak oturumu yeniden başlatır. Bu dosya o bilgisayardaki bütün uygulamalar için geçerli olduğu için otomatik yazmıyorum.

**Odak bölgesinde kamerayla hareket eden leke: SSAO.** SSAO (Screen Space Ambient Occlusion) köşe ve girintileri ekrandaki derinlik bilgisinden tahmin edip karartan bir efekt; `PC_Renderer`'da açık. Odak kamerası ayrı bir görüntü çizdiği için SSAO orada ayrıca hesaplanıyor. Kenarlarda komşu bilgi eksik kaldığı için koyu lekeler çıkıyor, içeride de SSAO'nun gürültü deseni çevredekiyle aynı yere düşmüyor. Bu farklar ekrana bağlı olduğu için kamerayla birlikte hareket ediyor ama odak dikdörtgeninden çıkmıyor. SSAO kapalıyken odak bölgesi tam çözünürlüklü görüntüyle birebir aynı çıkıyor. Gerçek quad views da odak görüntüsünü ayrı çizdiği için gözlükte de benzer bir etki beklenir; katmanın kenar yumuşatması bir kısmını gizler. **O** tuşu ya da Inspector'daki **Screen Space Ambient Occlusion** kutusuyla karşılaştırılabilir. Simülasyon bitince SSAO'nun eski durumu geri yüklenir.

**Simülasyonun eksikleri:**
- Keskinleştirme, katman gibi AMD CAS (Contrast Adaptive Sharpening) ile yapılıyor; katmanın kodundaki ayrıntıların hepsi birebir aynı değil. Önceki basit "unsharp mask" sürümü karanlık detaylarda siyah noktalar üretiyordu, bu yüzden değiştirildi.
- Lens distorsiyonu yok.
- Masaüstü kameranın görüş açısı (60°) gözlükten (~110°) dar; aynı oranlar ekranda daha küçük bir açıya denk geliyor.
- Göz takibinin gecikmesi ve titremesi yok.

## Sahnenin şu anki render ayarları
| Alan | Ayar |
|---|---|
| Kalite seviyesi | **PC** (Mobile seviyesi PC platformunda kapalı) |
| URP asset | `PC_RPAsset`: render scale 1.0, MSAA kapalı, HDR açık, SRP Batcher açık |
| Renderer | `PC_Renderer`: Forward+, **SSAO** açık |
| Gölgeler | Ana ışık 2048 çözünürlük, 50 m mesafe, 4 cascade, soft shadows |
| Post-process (`SampleSceneProfile`) | Tonemapping (Neutral), Bloom, Vignette (0.2). Motion Blur kapalı. Anti-aliasing yok. |
| Işık | Güneş Mixed (Baked Indirect), yoğunluk 4.5. Lightmap: GPU, 12 texel/m, 2048 atlas, 3 sekme, AO |
| Dokular | Normal Quality sıkıştırma (DXT/BC), en fazla 2048, bilinear, anisotropic: Forced On |
| Grafik API | Build için Direct3D 11. Editor hâlâ D3D12 ile açık; yeniden başlatınca D3D11'e geçer. |
| OpenXR | Multi-pass. Quad Views, Eye Gaze ve HTC Vive Controller feature'ları açık. |

## Gözlükte canlı performans göstergesi
Sahnedeki **Performance Overlay** objesi, oyun benchmark videolarındaki FPS göstergesi gibi, görüş alanının sol üstünde sabit duran bir panel gösterir. Panel quad views'ın odak bölgesinin içinde kalacak şekilde yerleştirildi, yani quad views açıkken de keskin görünür.

Gösterdikleri:
- FPS ve kare süresi (son 0.25 s ortalaması ve en kötü kare)
- GPU ve CPU süresi, ana thread ve render thread
- Gözlüğün yenileme hızı ve kare bütçesi (Vive Pro Eye için 90 Hz → 11.1 ms)
- Üçgen, köşe noktası, draw call ve SetPass sayısı
- Doku ve toplam grafik belleği
- **XR görüntü sayısı ve çizilen piksel:** quad views kapalıyken 2 görüntü, açıkken 4 görüntü görünmeli. Piksel sayısı da belirgin düşmeli.
- Quad views açık mı kapalı mı
- Kare süresi grafiği: beyaz kare süresi, yeşil GPU süresi, sarı çizgi gözlüğün kare bütçesi

Tuşlar:
- **F1**: paneli gizle veya göster.
- **F2**: kaydı başlat veya durdur. Kayıt her kareyi `BenchmarkResults/session_<tarih>.csv` dosyasına yazar. Durdurunca `session_<tarih>.md` dosyasına **quad views açık ve kapalı için ayrı ayrı** özet çıkarır.
- **1**: quad views'ı aç veya kapat (Quad Views Toggle).

Gözlükte quad views karşılaştırması için önerilen akış:
1. Gözlüğü bir masaya ya da standa koy ki görüntü sabit kalsın.
2. **F2** ile kaydı başlat.
3. 30 saniye bekle, **1**'e bas, yeniden 30 saniye bekle.
4. **F2** ile durdur. `session_*.md` dosyasında iki satır olur: quad views OFF ve ON.

Üçgen ve draw call sayaçları sadece Editor'de ve Development Build'de çalışır.

## Benchmark: performans ve görüntü kalitesi ölçümü

Sahnedeki **Quality Benchmark** objesi, sabit kamera açılarından farklı render ayarlarıyla görüntü alır ve her ayar için hem performansı hem de görüntü kalitesini ölçer. Amaç, "şunu düşürdük, fark edilmiyor" gibi bir yorumu sayıyla göstermek.

Görüntüler varsayılan olarak **Vive Pro Eye'ın bir gözü gibi** alınır: 1440×1600 çözünürlük, 93° dikey ve 110° köşegen görüş açısı.

### Ne ölçülüyor?
| Ölçüm | Nerede | Açıklama |
|---|---|---|
| GPU / CPU frame time (ortalama, medyan, p95, p99) | Unity | p99, en yavaş %1'lik karelerin süresi ("1% low"). |
| Üçgen, köşe noktası, draw call, batch, SetPass sayısı | Unity | Gölge haritası çizimleri de dahil. |
| Çizilen megapiksel | Unity | Quad views'ın asıl tasarrufu burada görünür. |
| Doku ve grafik belleği (MB) | Unity | Render texture'lar da dahil. |
| **SSIM** | Unity | Yapısal benzerlik (Wang et al. 2004). 1.0 = referansla aynı. Ayrıca **odak**, **çevre** ve aradaki **sınır** (24 px'lik şerit) için ayrı ayrı hesaplanır. |
| **PSNR** (dB) | Unity | Piksel farkı. Yüksek = referansa daha yakın. |
| **FovVideoVDP** (JOD) | Python | Foveated rendering için kullanılan algısal metrik (Mantiuk et al. 2021). Bakılan noktadan uzaklaştıkça gözün daha az detay gördüğünü hesaba katar. HTC Vive Pro ekran modeliyle, bakış merkezde. 10 = fark görülmüyor; makalelerde yaklaşık 9 ve üstü kabul edilebilir sayılıyor. |
| **LPIPS** | Python | Yapay sinir ağıyla ölçülen algısal fark (Zhang et al. 2018). 0 = aynı. Göze göre ağırlık vermez. |
| **FLIP** | Python | NVIDIA'nın "iki görüntü arasında gidip gelince ne kadar fark görülür" metriği (Andersson et al. 2020). 0 = aynı. Odak, çevre ve sınır için ayrı. |

Bütün kalite metrikleri "full-reference": her görüntü aynı açıdan alınmış bir referansla karşılaştırılır. Varsayılan referans en yüksek kalite senaryosu: PC ayarları + 2× süper örnekleme + MSAA 4× + 4096 gölge haritası.

Tessellation ölçülmüyor, çünkü URP'de donanım tessellation'ı yok ve Sponza'da da kullanılmıyor.

**Neden birden fazla metrik?** Foveated rendering çevreyi bilerek bulanıklaştırır. SSIM, PSNR ve LPIPS bütün pikselleri eşit saydığı için bu bulanıklığı gerçekte olduğundan kötü gösterir. FovVideoVDP ise gözün çevreyi zaten keskin görmediğini hesaba katar. Foveated rendering makaleleri bu yüzden FovVideoVDP'yi SSIM ya da LPIPS ile birlikte raporluyor ve hangi bölgede ölçüldüğünü belirtiyor.

### Quad views simülasyonu
Gözlük olmadan da quad views'ın görüntüye etkisini ölçebilmek için benchmark, Quad-Views-Foveated katmanının yaptığını taklit eder:
1. Bütün görüş alanını düşük çözünürlükte çizer (`peripheral_multiplier`, varsayılan 0.5 → her eksende yarı çözünürlük).
2. Ortadaki odak bölgesini ikinci bir kamerayla, sadece o bölgeyi kapsayan dar bir açıyla tam çözünürlükte çizer (`focus_multiplier` 1.0).
3. İkisini, katmanın varsayılanı olan %20'lik yumuşak kenarla birleştirir (`smoothen_focus_view_edges` 0.2).

Odak bölgesi görüş alanının oranı olarak verilir. Katmanın varsayılanları: göz takipsiz (sabit) 0.5 × 0.45, göz takipli 0.35 × 0.35. Göz takibi şu an çalışmadığı için odak hep ortada. Post-process ve SSAO odak görüntüsüne ayrıca uygulanır; gerçek quad views'da da öyle olduğu için, sınırdaki küçük farklar gerçekte de görülecek farklardır.

Simülasyon katmanın bütün ayrıntılarını içermiyor: katmanın odak görüntüsüne uyguladığı keskinleştirme (`sharpen_focus_view` 0.7) ve lens distorsiyonu yok. Bu yüzden görüntü kalitesi için iyi bir yaklaşım, ama **performans için gözlükte ölçülen değerler esas alınmalı.**

### Nasıl çalıştırılır?
1. **Tools → Benchmark → Run Benchmark**: Play moduna girer, ölçer ve bitince çıkar. Unity penceresi önde kalmalı.
2. Algısal metrikler için terminalde:
   ```
   Tools/metrics/.venv/Scripts/python Tools/metrics/compute_metrics.py
   ```
   En son çalıştırmayı alır ve klasörüne `perceptual.csv` ile `perceptual_summary.md` yazar (75 görüntü için yaklaşık 2 dakika). Python ortamı ilk kez kuruluyorsa: `python -m venv Tools/metrics/.venv` ve `Tools/metrics/.venv/Scripts/python -m pip install -r Tools/metrics/requirements.txt`. LPIPS ilk çalıştırmada AlexNet ağırlıklarını (233 MB) indirir.
3. **Tools → Benchmark → Open Results Folder**: sonuç klasörünü açar.

Senaryolar Inspector'dan değiştirilebilir. Her senaryoda şunlar seçilebilir:
- URP asset (örneğin `Mobile_RPAsset`)
- Render scale, MSAA, gölge çözünürlüğü ve mesafesi, doku çözünürlüğü, HDR
- Quad views simülasyonu ve ayarları

Benchmark ayarları kendi kopyası üzerinde değiştirir ve bitince eski haline döndürür.

### Çıktılar: `BenchmarkResults/<tarih_saat>/`
Bu klasör git'e eklenmez.
- `summary.md` ve `results.csv`: performans, SSIM ve PSNR.
- `perceptual_summary.md` ve `perceptual.csv`: FovVideoVDP, LPIPS ve FLIP (Python script'inden sonra).
- `images/`: her görüntü ve SSIM ısı haritası. Siyah = referansla aynı; kırmızıdan sarıya = fark büyüyor.
- `reference/`: referans görüntüler.

### Doku sıkıştırmasını ölçmek
Sıkıştırma, Unity'de import ayarı olduğu için çalışırken değiştirilemiyor. Bu yüzden iki ayrı çalıştırma gerekiyor:
1. **Tools → Benchmark → Sponza Texture Compression → None (uncompressed)** seç ve benchmark'ı çalıştır. Bu çalıştırmanın klasör adını not et.
2. İstediğin sıkıştırmayı seç (Low, Normal, High Quality veya Crunch). Inspector'da **External Reference Run** alanına ilk klasörün adını yaz ve tekrar çalıştır.

Referans senaryosuyla ölçülen senaryonun ayarları aynı olursa, fark sadece sıkıştırmadan gelir.

### Örnek sonuçlar (3 Kasım 2026)
Ortam: RTX 4070 SUPER, Ryzen 5 7600, **Editor Play modu (D3D12), masaüstü, gözlük yok**. 1440×1600 (Vive Pro göz başı), açı başına 60 ısınma + 240 ölçüm karesi. Değerler 5 açının ortalaması. Referans: 2× SSAA + MSAA 4× + 4K gölge.

**Performans:**

| Senaryo | GPU ms | Δ GPU | Çizilen MPix | Üçgen | Draw call | CPU ms | Kare p99 ms |
|---|---|---|---|---|---|---|---|
| PC (High) | 1.56 | 0% | 2.30 | 1,556,612 | 635 | 1.86 | 3.71 |
| Mobile (Low) | 0.79 | -49% | 1.47 | 526,243 | 227 | 1.31 | 2.55 |
| PC + render scale 0.75 | 1.17 | -25% | 1.30 | 1,556,044 | 635 | 1.64 | 3.77 |
| PC + render scale 0.5 | 0.82 | -47% | 0.58 | 1,553,727 | 635 | 1.52 | 2.70 |
| PC + MSAA 4× | 1.69 | +8% | 2.30 | 1,556,695 | 635 | 1.77 | 3.44 |
| PC + dokular 1/2 | 1.49 | -4% | 2.30 | 1,556,063 | 635 | 1.68 | 3.36 |
| PC + dokular 1/4 | 1.43 | -8% | 2.30 | 1,555,932 | 635 | 1.64 | 3.19 |
| PC + gölge haritası 512 | 1.44 | -8% | 2.30 | 1,556,373 | 635 | 1.64 | 2.83 |
| PC + realtime gölge yok | 1.27 | -19% | 2.30 | 526,224 | 232 | 1.47 | 2.64 |
| PC + HDR kapalı | 1.46 | -6% | 2.30 | 1,556,810 | 635 | 1.65 | 3.33 |
| **Quad views (sabit 0.5 × 0.45, çevre 0.5)** | 1.14 | -27% | 0.93 | 3,069,514 | 1248 | 2.24 | 5.55 |
| Quad views (göz takipli boyut 0.35 × 0.35) | 1.05 | -32% | 0.76 | 3,065,127 | 1246 | 2.23 | 5.92 |
| Quad views, çevre 0.25 | 1.02 | -35% | 0.50 | 3,065,864 | 1247 | 2.20 | 4.70 |
| Quad views, kenar yumuşatma yok | 1.13 | -28% | 0.93 | 3,058,453 | 1245 | 2.21 | 6.32 |

**Görüntü kalitesi:**

| Senaryo | FovVideoVDP JOD ↑ | JOD (en kötü açı) | LPIPS ↓ | SSIM ↑ | SSIM odak | SSIM çevre | SSIM sınır | FLIP ↓ | FLIP sınır |
|---|---|---|---|---|---|---|---|---|---|
| PC (High) | 9.03 | 8.35 | 0.065 | 0.961 | 0.938 | 0.966 | 0.941 | 0.037 | 0.051 |
| Mobile (Low) | 7.31 | 6.63 | 0.204 | 0.702 | 0.826 | 0.678 | 0.799 | 0.215 | 0.202 |
| PC + render scale 0.75 | 8.90 | 8.25 | 0.109 | 0.939 | 0.916 | 0.944 | 0.920 | 0.047 | 0.063 |
| PC + render scale 0.5 | 8.68 | 8.04 | 0.161 | 0.902 | 0.864 | 0.909 | 0.875 | 0.058 | 0.077 |
| PC + MSAA 4× | 9.05 | 8.35 | 0.060 | 0.964 | 0.948 | 0.967 | 0.947 | 0.035 | 0.049 |
| PC + dokular 1/2 | 8.93 | 8.24 | 0.113 | 0.930 | 0.928 | 0.930 | 0.924 | 0.053 | 0.063 |
| PC + dokular 1/4 | 8.61 | 7.92 | 0.214 | 0.902 | 0.903 | 0.902 | 0.896 | 0.063 | 0.078 |
| PC + gölge haritası 512 | 8.54 | 7.65 | 0.078 | 0.951 | 0.918 | 0.958 | 0.921 | 0.044 | 0.067 |
| PC + realtime gölge yok | 5.09 | 3.91 | 0.362 | 0.661 | 0.658 | 0.661 | 0.659 | 0.321 | 0.333 |
| PC + HDR kapalı | 9.03 | 8.35 | 0.065 | 0.961 | 0.938 | 0.966 | 0.941 | 0.037 | 0.051 |
| **Quad views (sabit 0.5 × 0.45, çevre 0.5)** | 8.82 | 8.27 | 0.197 | 0.915 | 0.931 | 0.913 | 0.883 | 0.056 | 0.077 |
| Quad views (göz takipli boyut 0.35 × 0.35) | 8.77 | 8.16 | 0.206 | 0.911 | 0.904 | 0.913 | 0.882 | 0.057 | 0.077 |
| Quad views, çevre 0.25 | 8.54 | 7.99 | 0.351 | 0.881 | 0.920 | 0.876 | 0.833 | 0.066 | 0.091 |
| Quad views, kenar yumuşatma yok | 8.83 | 8.28 | 0.191 | 0.917 | 0.939 | 0.913 | 0.900 | 0.056 | 0.078 |

SSIM odak / çevre / sınır ve FLIP sınır, sabit odak bölgesine (0.5 × 0.45) göre hesaplandı. Göz takipli senaryoda odak daha küçük olduğu için "odak" bölgesinin bir kısmı aslında çevre; değeri bu yüzden düşük.

**Doku sıkıştırması** (önceki ölçüm, 1920×1080; referans: aynı PC (High) ayarları, sıkıştırmasız dokular):

| Sıkıştırma | Doku + RT belleği MB | SSIM | SSIM (en kötü açı) | PSNR dB |
|---|---|---|---|---|
| Yok | 915 | 1.0000 | 1.0000 | – |
| Low Quality | 370 | 0.9706 | 0.9353 | 39.41 |
| Normal Quality (varsayılan) | 370 | 0.9706 | 0.9353 | 39.41 |
| High Quality | 458 | 0.9841 | 0.9659 | 42.24 |
| Normal + Crunch (50) | 334 | 0.9514 | 0.8922 | 37.36 |

### Bu sonuçlar nasıl okunmalı?
- **Quad views pikseli azaltıyor ama geometriyi ikiye katlıyor.** Çizilen piksel %60 azalıyor (2.30 → 0.93 MPix), GPU süresi %27 düşüyor. Ama sahne iki kez çizildiği için üçgen ve draw call sayısı iki katına çıkıyor, CPU süresi ve p99 kare süresi artıyor. Gözlükte de 4 görüntü çizildiği için benzer bir CPU maliyeti beklenir. Bu sahnede GPU çok hafif olduğundan net kazanç gözlükte ölçülmeli.
- **Odak bölgesi korunuyor.** Quad views'da odak SSIM 0.931, normal PC 0.938. Fark, kenar yumuşatmanın odak bölgesinin dış %20'sine çevreden bulanık görüntü karıştırmasından ve bloom/SSAO'nun her görüntüde ayrı hesaplanmasından geliyor.
- **Metrikler foveated rendering'i farklı değerlendiriyor.** LPIPS quad views'ı neredeyse Mobile kadar kötü buluyor (0.20), çünkü çevredeki bulanıklığı merkezdeki kadar ciddi sayıyor. Göze göre ağırlık veren FovVideoVDP ise 8.82 JOD veriyor: PC (High)'tan 0.2 JOD düşük, render scale 0.75 ile benzer. Foveated rendering'i değerlendirirken asıl bakılacak metrik FovVideoVDP; diğerleri hangi bölgede ne kaybedildiğini göstermek için.
- **Sınır en zayıf yer.** Bütün quad views senaryolarında sınır SSIM'i hem odaktan hem çevreden düşük. Yumuşatma kapalıyken sınır SSIM'i biraz yükseliyor (0.883 → 0.900), çünkü SSIM keskin bir geçişi bulanık bir geçişten daha az cezalandırıyor. Gözle bakınca keskin bir sınır genelde daha çok rahatsız eder; bunu gözlükte kontrol etmek gerekiyor.
- **Üçgen sayısının çoğu gölgeden geliyor.** Kameranın kendisi yaklaşık 0.5 milyon üçgen çiziyor. Realtime gölgeler (4 cascade) bunu yaklaşık 1.56 milyona çıkarıyor.
- **En kötü açıya da bakmak gerekiyor.** Ortalamalar bazı kayıpları gizleyebiliyor. Örneğin dokuları 1/4'e düşürmek ortalamada 8.61 JOD veriyor, en kötü açıda 7.92.
- **Low ve Normal Quality sıkıştırma PC'de aynı sonucu veriyor**, çünkü ikisi de aynı formata (DXT/BC1–BC3) sıkıştırıyor. High Quality BC7 kullanıyor.
- **HDR kapatmanın görüntüye etkisi neredeyse yok.**
- **Editor ölçümleri gürültülü.** Aynı senaryo farklı çalıştırmalarda birkaç yüzde puan farklı çıkabiliyor. Kesin frame time için Development Build alıp orada **B** tuşuyla ölç; sonuçlar exe'nin yanındaki `BenchmarkResults` klasörüne yazılır.

## Gereksinimler (VR bilgisayarı)
1. Vive Console for SteamVR kurulu, `sr_runtime.exe` (SRanipal) çalışıyor, göz kalibrasyonu yapılmış.
2. SteamVR, aktif OpenXR runtime olarak seçilmiş.
3. [Quad-Views-Foveated](https://github.com/mbucchia/Quad-Views-Foveated) API katmanı kurulu.
4. Unity Editor Direct3D 11 ile çalışıyor. Grafik API değiştikten sonra Unity'nin bir kez yeniden başlatılması gerekir.

## Test durumu
- Kod derleniyor. Play modunda gözlük olmadan çalıştırıldı: geçiş hatasız tetikleniyor, Play'den çıkınca ayar geri dönüyor.
- **Gözlükte henüz denenmedi.** XR'ı aynı uygulama içinde yeniden başlatmak SteamVR'da genelde sorunsuz çalışır, ama bu kurulumda (Vive katmanları + Quad-Views-Foveated) daha önce denenmedi.

### Gözlükte kontrol edilecekler
- Console'da 1'e bastıktan sonra şu satır görünmeli: `[QuadViews] XR restarted. Quad views requested: True, active: True`.
- `%LocalAppData%\Quad-Views-Foveated\Quad-Views-Foveated.log` dosyasında her açılışta `Session is using quad views` yazmalı.
