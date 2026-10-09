# Çalışma kuralları
- Gerçek implementasyon üret. Doküman veya mock tek başına teslim değildir.
- Kod/identifier İngilizce; kullanıcı arayüzü Türkçe, localization'a uygun. Teknik hata code'ları İngilizce.
- Paketlerin .NET 10 ve seçilen Node LTS ile uyumunu doğrula; lockfile commit et.
- Mevcut dosyaları silme; specs ve örnekler korunur.
- Secret'lar repo, log, trace, exception, screenshot veya frontend bundle içine girmez.
- Tenant scope her sorgu, object storage erişimi, composite FK ve background job'da korunur.
- Security ve billing kararlarını varsayımla geçiştirme; acceptance test yaz.
- Harici API çağrıları cancellation-aware; bounded timeout ve tek katman retry.
- Frontend backend'den gerçek veri alır; boş durumda gerçek empty state göster.
- API'de DTO kullan; EF entity ve provider SDK tiplerini dışarı çıkarma.
- Yeni bağımlılık veya mikroservis için somut ihtiyaç gerekir.
- Canlı deployment ve gerçek tahsilat bu görev kapsamı değildir.
- NETWORK/SDK/Docker kısıtlarını açık raporla; başarılı test uydurma.

## Beklenen depo
src/AiGateway.Api, src/AiGateway.Core, src/AiGateway.Infrastructure
web/, tests/, samples/dotnet/, docs/, docker-compose.yml

## Doğrulama
Backend (Postgres gerekir; `TEST_POSTGRES` bağlantı dizesi, Database hariç):
  dotnet build; dotnet test
Migration: dotnet run --project src/AiGateway.Api -- --migrate
Yeni migration: dotnet tool restore; dotnet ef migrations add <Ad> -p src/AiGateway.Infrastructure -s src/AiGateway.Api -o Migrations
Frontend (web/): npm ci; npm run typecheck; npm test -- --run; npm run build
Smoke (API Development + Demo:Enabled=true çalışırken): scripts/smoke.sh http://localhost:5080
Compose: cp .env.example .env (CHANGE_ME'leri değiştir); docker compose up --build → /health/ready
Playwright (API + `npm run dev` çalışırken, web/): npx playwright test — login → project → key → profile → playground → workout → usage
