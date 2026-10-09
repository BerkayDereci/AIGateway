# Implementation status

Durum tarihi: 2026-10-09. Pilot MVP; production-ready değildir.

## Doğrulama sonuçları (bu ortamda gerçekten çalıştırıldı)

| Komut | Sonuç |
|---|---|
| `dotnet build -c Release` | ✅ 0 hata, 0 uyarı |
| `dotnet test -c Release` (PostgreSQL 14 üzerinde) | ✅ 45/45 geçti |
| `npm run typecheck` / `npm test -- --run` / `npm run build` | ✅ / ✅ 2/2 / ✅ |
| `npx playwright test` (API Development + demo, Vite dev) | ✅ 1/1 (login → proje → profil → anahtar → playground → fotoğraf analizi → düzenleme/indirme → kullanım) |
| `scripts/smoke.sh` (curl) | ✅ health, kayıt, idempotent tekrar, görsel + JSON Schema, SSE, 401 sözleşmesi, kullanım |
| `samples/dotnet` text / image / stream + 0.15 sn iptal | ✅ (iptalde istek `cancelled`, rezervasyon `unknown`) |
| `dotnet publish -r linux-x64` | ✅ (`libSkiaSharp.so` dahil) |
| `npm audit --omit=dev`, `dotnet list package --vulnerable` | ✅ açık yok |
| `docker compose up` | ❌ **çalıştırılmadı** — bu makinede Docker yok. Dockerfile/compose yazıldı, CI'da `docker compose build` var. |
| Gerçek OpenAI/Anthropic çağrısı | ❌ **doğrulanmadı** — anahtar sağlanmadı. Sadece offline sözleşme testleri. |

## Özellikler

| Alan | Durum |
|---|---|
| Register/login/logout/me/csrf, cookie + antiforgery, throttling, kilit | ✅ |
| Workspace, üyeler (mevcut kullanıcı), rol değişimi, son owner koruması | ✅ |
| Proje CRUD/arşiv, development/production ortamları | ✅ |
| Sağlayıcı bağlantıları (şifreli, maskeli, güncelle/sil, ücretsiz doğrulama) | ✅ |
| Proje API anahtarları (bir kez gösterim, hash, iptal, süre) | ✅ |
| Profil + değiştirilemez revizyon + ortam yayını, yetenek/model/bağlantı doğrulaması | ✅ |
| Metin + görsel inference, JSON Schema (native/validation), SSE, heartbeat, idle timeout | ✅ (gerçek sağlayıcıyla doğrulanmadı) |
| OpenAI Responses ve Anthropic Messages adaptörleri (ayrı) | ✅ offline sözleşme testleri |
| Demo sağlayıcı yalnızca Development/Testing + DEMO rozeti; production'da başlatma reddi | ✅ |
| Görsel yükleme: imza, piksel/boyut sınırı, animasyon reddi, yeniden kodlama, EXIF yönü | ✅ |
| Atomik çok kapsamlı bütçe/istek/token/eşzamanlılık rezervasyonu | ✅ (50 eşzamanlı test) |
| Retry (yalnızca 429, ≤1) / fallback (yalnızca çıktı öncesi) / timeout = unknown | ✅ |
| Idempotency (replay, 409 farklı gövde, in-progress, unknown'da yeniden üretim yok) | ✅ |
| Reconciler (çökme sonrası istek, review_required, görsel temizliği) | ✅ |
| Kullanım raporu, istek detayı/denemeler, genel bakış istatistikleri | ✅ |
| Fiyat sürümleri (manuel, kaynaklı), maliyet estimated/unknown ayrımı | ✅ |
| Panel: 11 ekran, açık/koyu tema, responsive, boş/yükleniyor/hata durumları | ✅ |
| OpenTelemetry metrik/trace (+ OTLP opsiyonel) | ✅ (exporter bu ortamda denenmedi) |
| OpenAPI (`docs/api.openapi.json`) | ⚠️ rotalar üretildi; `generations`/`uploads` gövdeleri elle okunduğu için şemada yok — normatif kaynak `specs/API.md` |

## Bilinen sınırlamalar / bekleyenler

- **Production gereksinimleri:** TLS, KMS/Key Vault ile korunan keyring sertifikası, DB/depolama yedekleri,
  e-posta doğrulama/parola sıfırlama/MFA, kayıt kötüye kullanım koruması, yük testi, olay prosedürleri.
- Owner devri ve workspace silme uç noktası yok. Üye daveti e-postası V2.
- `UsageRecords` ayrı tablo değil: her `ProviderAttempt` satırı ekleme-yalnız kullanım kaydıdır.
- Sağlayıcı kullanım mutabakat API'si yok → belirsiz yükümlülükler `review_required`, elle kapatılır (OPERATIONS.md).
- `ResponseJson` saklama işi yok (elle SQL). Audit/istek saklama politikası yapılandırılamaz.
- Ay sınırı: rezervasyon, oluşturulduğu UTC ayının kovasında kesinleşir (ayrı test yok; fiyat sürümü değişimi test edildi).
- Üye listesi sayfalanmaz (workspace başına küçük varsayıldı); diğer listeler cursor'lı veya ≤100.
- Görsel token tahmini fiyat sürümündeki “görsel başına üst sınır”a dayanır; metin tahmini `karakter/3`.
- Playground istekleri idempotency anahtarı kullanmaz.
- MinIO resmi imajı yayınlanmıyor; compose Chainguard imajı kullanır (doğrulanmadı).

## Sonraki komutlar

```bash
cp .env.example .env && docker compose up --build        # Docker olan makinede compose smoke
scripts/smoke.sh http://localhost:5080                    # compose API'sine karşı
AIGW_ALLOW_PAID=1 AIGW_LIVE_PROVIDER=openai AIGW_LIVE_MODEL=gpt-6-luna AIGW_LIVE_KEY=... scripts/smoke.sh   # opsiyonel, ücretli
```
