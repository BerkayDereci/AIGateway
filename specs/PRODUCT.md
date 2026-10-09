# Ürün şartnamesi

## Amaç
Küçük yazılım ekiplerine tek API üzerinden AI erişimi, model politikaları, kullanıcı bazlı kullanım kontrolü, fotoğraf analizi ve maliyet görünürlüğü sağlamak.
Pilot MVP önce sahibinin uygulamasında çalışacak; ardından birkaç dış ekipte denenecek.

## Roller
Owner: workspace, üyeler, sağlayıcı bağlantıları, tüm ayarlar.
Admin: proje/profil/anahtar yönetimi; owner devri ve workspace silme hariç.
Viewer: redacted ayarlar ve kullanım; secret oluşturamaz veya inference başlatamaz.
Project API key: sadece bağlı environment kapsamındaki inference/upload; yönetim API'sine erişemez.
İlk sürüm üyeler mevcut kayıtlı hesaplar arasından eklenebilir; e-posta gönderen davet akışı V2.

## Kapsam
- Register/login/logout; workspace oluşturma; mevcut kullanıcıya rol atama.
- Project CRUD/archive; development/production environment.
- Provider connection oluşturma, anahtar yenileme/silme; masked key bilgisi. Key bir kez alınıp şifrelenir, asla geri okunmaz.
- Gateway API key create/revoke: secret yalnızca create yanıtında bir kez gösterilir.
- Profile draft ve immutable published revision; environment'a yayınlama.
- Metin, görsel giriş, structured JSON ve SSE; model capability doğrulama.
- End-user quota: kimliği güvenilir backend API key üzerinden gelir; kullanıcı adı hassas olmayan opaque reference.
- Workspace/project/end-user request, concurrency, token ve estimated USD sınırları.
- Playground ve kullanım tablosu; tüm çağrıların attempt maliyetlerini kapsayan rapor.
- Fotoğraftan antrenman programı demo; kullanıcı düzeltip JSON indirebilir.

## Fotoğraf sınırları
JPEG/PNG/WebP; 10 MiB dosya, en fazla 20 megapiksel, istek başına 4 görsel. Sayılar config ile değişebilir.
MIME bildirimi yeterli değil: gerçek decode/signature kontrolü, EXIF temizleme, decompression bomb sınırı.
Upload object 15 dakika içinde kullanılabilir; provider çağrısı için server-side okuma.
Generation sonrası sil; kullanılmamış upload en geç 24 saatte temizlenir.
Object public olmaz. Arbitrary remote URL, SVG, animated GIF, PDF ve base64 body V1'de kabul edilmez.
Workout extraction tıbbi veya antrenman uygunluğu değerlendirmesi değildir; sadece belgede görüleni aktarır.

## Ticari kapsam
BYOK; manuel plan/entitlement (Starter/Pilot örnek plan adı; fiyat yok).
Gerçek abonelik checkout, tax, invoice, managed-provider credits V2. UI bu eksikleri gizlemez.
Kullanım maliyet tahmini provider faturası değildir.

## V2
Embeddings/RAG, tool execution, agents/MCP, speech/realtime, semantic cache, OpenAI-compatible API, payment processing.
