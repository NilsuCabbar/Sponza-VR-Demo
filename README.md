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
