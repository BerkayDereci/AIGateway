# Security

Pilot MVP. Güvenlik açığı bildirimi: depo sahibine özel olarak (GitHub Security Advisory) bildirin.

## Tehdit varsayımları

- Saldırgan kayıtlı bir kullanıcı olabilir ve başka workspace'lerin ID'lerini tahmin etmeye çalışabilir.
- Proje API anahtarı müşteri backend'inde tutulur; sızarsa yalnızca bağlı ortamda inference/upload yapabilir.
- Yüklenen görseller ve görsel içindeki metin güvenilmezdir (prompt injection, decompression bomb, sahte MIME).
- Sağlayıcı yanıtları güvenilmezdir: şemaya uymayan veya eksik çıktı mümkündür.
- Gateway sağlayıcı hesabının tamamını kontrol etmez (BYOK); limitler yalnızca gateway trafiği içindir.

## Kimlik doğrulama

- Yönetim API'si: ASP.NET Core Identity cookie (`HttpOnly`, `SameSite=Strict`, production'da `Secure`), tüm yazma
  uçlarında antiforgery (`X-CSRF-TOKEN`, login/register dahil). JWT veya localStorage oturumu yok.
- Login/register IP başına dakikada 10 deneme (`RateLimit:AuthPerMinute`), 5 hatalı denemede 15 dk hesap kilidi,
  genel hata mesajı (“E-posta veya parola hatalı”). Parola: Identity varsayılan hash, en az 10 karakter.
- Inference/upload: yalnızca `Authorization: Bearer <proje anahtarı>`; cookie dikkate alınmaz, anahtar yönetim
  API'sine erişemez. Şemalar karıştırılmaz.
- Proje anahtarı: 256 bit rastgele, `pgw_` öneki; DB'de yalnızca SHA-256 özeti + 16 karakterlik arama öneki;
  karşılaştırma sabit zamanlı; düz metin yalnızca oluşturma yanıtında bir kez döner.
- **Sınırlamalar:** e-posta doğrulama, parola sıfırlama, MFA, davet e-postası, kayıt kötüye kullanım koruması
  (CAPTCHA vb.) yok. Yerel HTTP geliştirmede cookie `Secure` değildir (yalnızca Development/Testing).

## Sağlayıcı anahtarları

- ASP.NET Core Data Protection ile şifrelenir (`purpose: AiGateway.ProviderConnections.dp-v1`), yalnızca son 4
  karakter saklanır/gösterilir; API hiçbir zaman düz metin döndürmez, güncelleme yalnızca üzerine yazar.
- Production'da keyring sertifika (PFX) ile korunmazsa uygulama başlamaz. Veritabanı + şifresiz keyring
  production şifrelemesi sayılmaz; sertifikayı KMS/Key Vault'ta tutun.
- Sağlayıcı host'ları sabittir (`api.openai.com`, `api.anthropic.com`), yönlendirme takibi kapalı; özel endpoint
  ve uzak görsel URL'si kabul edilmez (SSRF).

## Tenant izolasyonu

- Tenant bağlamı yalnızca oturum üyeliğinden veya API anahtarından gelir, istek gövdesinden asla.
- Her yönetim uç noktası üyeliği komut sınırında kontrol eder; üye olmayan için **404** (ID taranamaz).
- Tenant'a ait tablolarda `(WorkspaceId, Id)` alternatif anahtarları ve bileşik FK'lar çapraz workspace
  ilişkisini veritabanı seviyesinde engeller (test: `Database_rejects_cross_workspace_links`).
- Object storage anahtarları `workspace/environment/asset` biçimindedir; asset erişimi workspace + ortam + durum
  + süre ile filtrelenir. Arka plan işleri her satırı kendi `WorkspaceId`'siyle günceller.

## Görsel güvenliği

İmza tabanlı format tespiti (JPEG/PNG/WebP), animasyon reddi, decode öncesi piksel sınırı (20 MP), boyut sınırı
(10 MiB), yeniden kodlama ile EXIF/metaveri temizliği (EXIF yönü uygulanır), özel bucket, 15 dk geçerlilik,
kullanım sonrası silme, 24 saat içinde temizlik. SVG, GIF, PDF, base64 gövde ve URL kabul edilmez.

## Gizli veri ve loglar

Anahtarlar, prompt ve görsel içeriği loglanmaz, trace'e eklenmez, hata yanıtına konmaz; sağlayıcının ham hata
gövdesi istemciye iletilmez (test: `Logs_never_contain_secrets_or_prompt_content`). Denetim kaydı yalnızca
eylem/hedef ID tutar. Idempotent tekrar için tamamlanmış non-stream yanıt (çıktı dahil) veritabanında saklanır;
saklama süresi için bkz. OPERATIONS.md.

## Bağımlılıklar

Görsel işleme için MIT lisanslı SkiaSharp kullanılır (ImageSharp 3.x'te bilinen yüksek önemli açıklar, 4.x'te
lisans anahtarı zorunluluğu nedeniyle seçilmedi). `dotnet list package --vulnerable` ve `npm audit` CI'a
eklenmelidir.
