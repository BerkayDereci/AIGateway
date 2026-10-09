# Kabul ve teslim

## Zorunlu senaryolar
- İki workspace oluştur: B hiçbir A project/key/profile/usage/upload kaydını okuyamaz/değiştiremez. Admin ve API key routes ayrı ayrı denenir.
- Viewer key üretemez/provider verify/inference çalıştıramaz; owner işlemleri yetkili.
- Revoked/expired key 401. API key yönetim endpointine giremez.
- Key ilk yanıttan sonra plaintext görünmez; veritabanı/provider storage encrypted; logs/trace redaction tests.
- Giriş CSRF/session/login throttling davranışı doğrulanır.
- Demo açıkça gösterilir; production demo/keyring unsafe config ile başlamaz.
- Text ve image request her iki provider'a doğru formatla iletilir; fake HTTP fixtures ile adapter contract tests.
- Gerçek provider smoke opsiyonel/manual; otomatik testler para harcamaz.
- Unsupported capability provider çağrısından önce reddedilir.
- Valid schema output parse/validate; malformed output açık hata; unknown field handling schema'ya göre.
- Cross-environment asset, expired asset, wrong MIME, oversize/decode bomb, arbitrary URL reddedilir.
- Stream başladıktan sonra provider hata verir: partial error, başka model çağrısı yok.
- Client cancel provider'ı iptal eder; slot release, finansal state doğru; completed bir kere.
- Safe retry ve ambiguous timeout ayrı; SDK retry multiplication yok.
- 50 eşzamanlı istek, düşük bütçe: rezervasyonlar tüm scope'larda atomik, limit kontrolü delinemiyor.
- Fallback/retry maliyeti kaybolmaz. Missing usage unknown; TTL muhasebe borcunu silmez.
- Duplicate settlement/worker retry çift ücret kaydı yaratmaz.
- Idempotency replay extra network call yok; farklı body 409; unknown/inflight için regen yok.
- UTC month boundary ve fiyat sürümü değişimi geçmiş maliyetleri bozmaz.
- Process crash/restart in-flight request reconciliation ve upload cleanup testi.
- UI gerçek API'ye bağlı; empty data ile sahte chart yok.
- Playwright demo end-to-end: login/project/key/profile/photo analysis/edit/output/usage.

## Teslim dosyaları
Çalışan kaynak, migrations, Docker Compose, Dockerfiles, lockfiles, .env.example.
README: host prerequisites, demo boot, production boot farkı, migration, provider key/profile config, test commands.
SECURITY.md: threat assumptions, credential protection, tenant isolation, auth limitations.
OPERATIONS.md: backup/restore, keyring persistence/rotation, retention, reconcile, unknown usage handling, proxy SSE settings.
docs/provider-compatibility.md: gerçek capability/SDK/source/date tablosu.
docs/api.openapi.json: generated contract and curl samples.
samples/dotnet: text, image upload, SSE cancellation consumer.
CI: build/test/typecheck, no live secrets.
IMPLEMENTATION_STATUS.md: feature status, commands/results, known limitations; completed/pending ayrımı.

## Pilot ile production ayrımı
Production için external protected keyring, TLS, database/storage backups, signup abuse controls, email verification/recovery, hosting limits/load tests ve incident procedures gerekir. UI ürün kullanılabilir görünmeli; bu maddeler tamamlanmadıysa production-ready iddiası yapılmaz.
Gerçek provider auth, image/schema/stream davranışı gerçek anahtar olmadan doğrulanmış sayılmaz; offline contract tests ile ayrı raporlanır.
