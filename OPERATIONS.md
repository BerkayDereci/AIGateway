# Operations

## Bileşenler

| Servis | Rol | Kalıcı veri |
|---|---|---|
| `postgres` | tek doğruluk kaynağı (kullanıcı, tenant, istek, rezervasyon, kullanım) | `pgdata` |
| `minio` | özel görsel bucket'ı (`Storage__Bucket`) | `miniodata` |
| `migrate` | `--migrate` ile EF migration'larını uygular ve çıkar (idempotent) | — |
| `api` | ASP.NET Core; `/health/live` (süreç), `/health/ready` (DB + depolama) | `keyring` |
| `web` | nginx: statik panel + `/api` reverse proxy (same-origin) | — |

> MinIO artık resmi Docker imajı yayınlamıyor (Ekim 2025). Compose `cgr.dev/chainguard/minio:latest` kullanır
> (ücretsiz, kaynaktan derlenmiş; AGPLv3). Production için yönetilen S3 veya kendi derlediğiniz, sürümü
> sabitlenmiş bir imaj kullanın. Compose bu ortamda Docker olmadığı için **çalıştırılarak doğrulanmadı**.

## Yedekleme / geri yükleme

```bash
docker compose exec postgres pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB" > aigateway-$(date +%F).dump
docker compose exec -T postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean < aigateway-YYYY-MM-DD.dump
```

- Keyring volume'u **ayrıca** yedekleyin; keyring kaybı = tüm sağlayıcı anahtarları okunamaz (yeniden girilmeli).
  Keyring ve DB yedeklerini farklı yerlerde saklayın.
- Görseller geçicidir (≤ 24 saat); bucket yedeği gerekmez.

## Keyring kalıcılığı ve rotasyon

- Data Protection anahtarları varsayılan 90 günde bir otomatik döner; eski anahtarlar çözme için keyring'de kalır.
  Keyring'i silmeyin.
- Production: `DataProtection__CertificatePath` (+ parola) zorunludur; sertifikayı KMS/Key Vault'tan mount edin.
  Sertifika rotasyonunda eski sertifikayı çözme için bir süre erişilebilir bırakın.
- Sağlayıcı anahtarı rotasyonu: panelde “Anahtarı güncelle” (eski şifreli değerin üzerine yazar, durum
  `unverified` olur).

## Saklama

| Veri | Varsayılan | Not |
|---|---|---|
| Prompt / görsel içeriği | saklanmaz | görseller kullanım sonrası silinir |
| Non-stream tamamlanmış yanıt (`Requests.ResponseJson`) | süresiz | yalnızca idempotent tekrar için; elle temizleyin (aşağıda) |
| İstek/deneme/kullanım kayıtları | süresiz | faturalama mutabakatı için |
| Denetim kaydı | süresiz | sır içermez |

```sql
-- shortcut: retention job yok; 7 günden eski idempotency yanıtlarını elle temizleyin.
UPDATE "Requests" SET "ResponseJson" = NULL WHERE "CompletedAt" < now() - interval '7 days';
```

## Mutabakat (reconcile)

API içinde dakikada bir çalışan `Reconciler`:

1. 15 dakikadan eski `in_progress` istekler (çökme/yeniden başlatma): sağlayıcıya gönderilmiş deneme varsa
   `unknown` + rezervasyon tutarı **bilinmeyen yükümlülük**; gönderilmemişse rezervasyon serbest bırakılır.
2. `unknown` rezervasyonlar `review_required` olur (sağlayıcı kullanım mutabakat API'si entegre değil).
3. Süresi dolmuş/yetim görseller depodan silinir.

### Bilinmeyen kullanım

Zaman aşımı, bağlantı kopması, istemci iptali veya kullanımı raporlanmamış yanıt → maliyet `unknown`,
rezerve tutar `Buckets.Unknown`'a eklenir ve **TTL ile silinmez**; bütçe kontrolünde harcanmış sayılır.
Limitler ekranı `review_required` sayısını gösterir. İnceleme: sağlayıcı panelindeki kullanımla karşılaştırın,
ardından gerekirse:

```sql
-- Sağlayıcı faturasında karşılığı olmadığı doğrulanan yükümlülüğü kapatma (manuel, denetim notu tutun).
UPDATE "Buckets" SET "Unknown" = "Unknown" - <tutar> WHERE "WorkspaceId" = '<ws>' AND "Period" = 'YYYY-MM';
UPDATE "Reservations" SET "State" = 'settled' WHERE "RequestId" = '<id>';
```

## Reverse proxy / SSE

- Proxy buffering **kapalı** (`proxy_buffering off`, `X-Accel-Buffering: no` yanıt başlığı da gönderilir),
  sıkıştırma kapalı, okuma zaman aşımı ≥ 120 sn + heartbeat (15 sn).
- `client_max_body_size` ≥ `Limits__UploadMaxBytes` (varsayılan 10 MiB).
- TLS proxy'de sonlandırılır; production'da `X-Forwarded-Proto` ileterek cookie'lerin `Secure` kalmasını sağlayın.
- Farklı origin'den panel sunulacaksa `Cors__AllowedOrigins__0=...` (credentials ile allowlist).

## Gözlemlenebilirlik

`OTEL_EXPORTER_OTLP_ENDPOINT` ayarlanırsa OTLP ile dışa aktarılır. Metrikler: `gateway.requests`,
`gateway.request.duration`, `gateway.ttft`, `gateway.tokens` (etiketler: `provider`, `status` — kullanıcı/istek
ID'si yok) + ASP.NET Core / HttpClient enstrümantasyonu. Prompt içeriği trace'lere yazılmaz.

## Ölçek notları

Bütçe/eşzamanlılık kilitleri Postgres satır kilitleridir (`SELECT … FOR UPDATE`, deterministik sıra); birden
çok API örneği güvenle çalışır. Reconciler her örnekte çalışır ve idempotenttir. Yük testi yapılmadı.
