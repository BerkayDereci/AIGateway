# AI Gateway — Claude geliştirme paketi

Bu paket çalışan uygulamanın kendisi değil; Claude Code'un uygulamayı uçtan uca geliştirmesi için hazırlanmış şartname, sözleşme, örnek ve kabul kriterleridir.

## Kullanım
1. Paketi boş bir proje klasörüne aç.
2. Claude Code'u o klasörde başlat. Terminal erişimi olan Claude Code kullan; yalnızca sohbet arayüzü build/test/çalıştırma doğrulaması yapamaz.
3. PROMPT.md içeriğinin tamamını tek mesaj olarak gönder.
4. Claude belgeleri okuyup backend, frontend, migration, Docker yapılandırması ve testleri oluşturacak.
5. Gerçek sağlayıcı anahtarlarını sohbet mesajına yazma. Uygulamanın Provider Connections ekranına yerel olarak gir.

Tek prompt tek seferde talimat vermek demektir; kod üretimi birçok dosya, araç çağrısı ve doğrulama adımı içerecektir. Ortamın izinleri, oturum veya bağlam limitleri devam etmeyi gerektirebilir.

## Kararlar
- .NET 10 LTS / ASP.NET Core; React + TypeScript + Vite.
- PostgreSQL; S3 uyumlu özel depolama, yerelde MinIO.
- BYOK varsayımı: müşterinin kendi sağlayıcı anahtarı. Bu ticari tercih henüz ayrıca onaylanmadığından mimari provider credentials abstraction ile değiştirilebilir.
- OpenAI ve Anthropic: metin, görsel, şemalı çıktı ve streaming; yalnızca doğrulanmış model yetenekleri.
- Multi-tenant pilot MVP. Production sertifikası veya tamamlanmış ticari SaaS iddiası yok.
- Platform aboneliği için entitlement modeli; ilk sürüm manuel plan atama. Sahte ödeme entegrasyonu yok.
- İlk showcase: antrenman fotoğrafını düzenlenebilir JSON'a dönüştürmek.

## Dosyalar
- PROMPT.md: Claude'a tek mesaj.
- CLAUDE.md: kalıcı geliştirme kuralları.
- specs/PRODUCT.md: ürün, kapsam, kullanıcı akışları.
- specs/ARCHITECTURE.md: modüller, veri modeli ve güvenlik.
- specs/API.md: normatif API sözleşmesi.
- specs/ACCEPTANCE.md: testler ve teslim koşulları.
- specs/UI.md: panel tasarımı.
- examples/: JSON Schema, istek ve profil konfigürasyon örnekleri.
- .env.example: yapılandırma isimleri; gerçek secret içermez.

Dokümanlarda uyuşmazlık olursa güvenlik ve tenant izolasyonu korunur; API.md dış sözleşmede, ARCHITECTURE.md veri/işlem davranışında belirleyicidir.
