# AIGateway (PrismGate) — pilot MVP

Küçük ekipler için tek API üzerinden AI erişimi: workspace/proje/ortam izolasyonu, BYOK sağlayıcı bağlantıları,
proje API anahtarları, sürümlü model profilleri, metin + görsel inference, SSE, JSON Schema çıktısı,
atomik bütçe/kota rezervasyonu, kullanım raporu ve Türkçe yönetim paneli. İlk demo: antrenman fotoğrafından
düzenlenebilir program JSON'u.

> Pilot MVP'dir; production-ready değildir. Eksikler: [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md).

| Katman | Teknoloji |
|---|---|
| API | .NET 10 / ASP.NET Core minimal API, modüler monolit (`src/AiGateway.Api`, `.Core`, `.Infrastructure`) |
| Veri | PostgreSQL + EF Core migrations, S3 uyumlu depolama (yerelde MinIO) |
| Panel | React 19 + TypeScript + Vite (`web/`) |
| Gözlem | OpenTelemetry (metrik + trace, prompt içeriği yok) |

Şartname ve kabul kriterleri: [`specs/`](specs/), örnekler: [`examples/`](examples/), Claude paketi: [`docs/claude-pack.md`](docs/claude-pack.md).

## Gereksinimler

- .NET SDK 10.0.1xx+ (`global.json`), Node.js 22+, PostgreSQL 14+
- Docker + Compose (yalnızca compose ile çalıştırmak için)

## Hızlı başlangıç — Docker Compose (demo)

```bash
cp .env.example .env   # tüm CHANGE_ME değerlerini değiştirin; Storage__AccessKey/SecretKey = MINIO_ROOT_USER/PASSWORD
docker compose up --build
```

Panel: http://localhost:8080 · API: http://localhost:5080 · Sağlık: `/health/live`, `/health/ready`.
`migrate` servisi migration'ları uygular ve çıkar; API ondan sonra başlar.

## Yerel geliştirme (Docker olmadan)

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Username=postgres;Password=...;Database=aigateway"
export ASPNETCORE_ENVIRONMENT=Development Demo__Enabled=true DataProtection__KeyRingPath=$HOME/.aigateway/keyring
dotnet run --project src/AiGateway.Api -- --migrate      # migration (idempotent)
dotnet run --project src/AiGateway.Api                   # http://localhost:5080
cd web && npm ci && npm run dev                          # http://localhost:5173 (/api proxy)
```

`Storage:Endpoint` boşsa Development'ta bellek içi depolama kullanılır (yeniden başlatınca yüklemeler silinir).

## Demo ile production farkı

| | Development / Testing | Production |
|---|---|---|
| `demo` sağlayıcı | `Demo:Enabled=true` ise açık, panelde **DEMO** rozeti | Açılırsa uygulama **başlamaz** |
| Keyring | dosya sistemi (`DataProtection:KeyRingPath`) | `DataProtection:CertificatePath` (PFX) zorunlu, yoksa başlamaz |
| Depolama | MinIO veya bellek içi | `Storage:Endpoint` zorunlu |
| Cookie | HTTP'de `SameAsRequest` | yalnızca `Secure` (TLS gerekli) |
| OpenAPI | `/openapi/v1.json` | kapalı |

Production'da `ASPNETCORE_ENVIRONMENT=Production`, `Demo__Enabled=false`, TLS sonlandıran bir reverse proxy,
KMS/Key Vault ile korunan sertifika ve yedekli Postgres/S3 gerekir (bkz. [OPERATIONS.md](OPERATIONS.md)).

## Sağlayıcı anahtarı ve profil

1. Panel → **Sağlayıcı bağlantıları**: OpenAI/Anthropic anahtarını girin (şifrelenir, yalnızca son 4 karakter görünür).
   “Doğrula” sağlayıcının ücretsiz `/v1/models` uç noktasını çağırır.
2. **Limitler ve fiyatlar** → fiyat sürümü ekleyin (sağlayıcının güncel fiyat sayfasından, kaynağıyla). Fiyat yoksa maliyet `unknown`.
3. **Profiller** → profil oluşturun, revizyon kaydedin (doğrulanmış modeller: [docs/provider-compatibility.md](docs/provider-compatibility.md)),
   ortama yayınlayın.
4. **API anahtarları** → ortam için anahtar oluşturun (bir kez gösterilir).

## API kullanımı

Sözleşme: [`specs/API.md`](specs/API.md), üretilmiş OpenAPI: [`docs/api.openapi.json`](docs/api.openapi.json).
curl örnekleri ve uçtan uca smoke: [`scripts/smoke.sh`](scripts/smoke.sh) (kayıt → proje → profil → anahtar →
metin/idempotency → görsel + JSON Schema → SSE → hata sözleşmesi → kullanım).

```bash
scripts/smoke.sh http://localhost:5080
```

.NET örnek istemci (metin, görsel yükleme, SSE + iptal): [`samples/dotnet`](samples/dotnet/Program.cs)

```bash
AIGATEWAY_KEY=pgw_... dotnet run --project samples/dotnet -- stream 2
```

## Testler

```bash
dotnet build && dotnet test                     # Postgres gerekir: TEST_POSTGRES="Host=...;Username=...;Password=..."
cd web && npm ci && npm run typecheck && npm test -- --run && npm run build
cd web && npx playwright install chromium && npx playwright test   # API (Development, demo) + `npm run dev` çalışırken
```

Otomatik testler para harcamaz: sağlayıcılar sahte HTTP ile, e2e `demo` sağlayıcı ile çalışır.
Gerçek sağlayıcı smoke testi yalnızca siz açıkça izin verirseniz çalışır (ücretlidir):

```bash
AIGW_ALLOW_PAID=1 AIGW_LIVE_PROVIDER=anthropic AIGW_LIVE_MODEL=claude-haiku-5-5 AIGW_LIVE_KEY=... scripts/smoke.sh
```

## Belgeler

[SECURITY.md](SECURITY.md) · [OPERATIONS.md](OPERATIONS.md) · [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) ·
[docs/provider-compatibility.md](docs/provider-compatibility.md)
