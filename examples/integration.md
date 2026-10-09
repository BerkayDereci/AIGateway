# İstemci akışı

1. Backend proje anahtarını secret manager üzerinden alır.
2. POST /api/v1/uploads ile multipart görsel gönderir.
3. assetId değerini workout.request.json içine koyar.
4. POST /api/v1/generations ile sonucu alır.
5. Kullanıcı düzenleme/onay ekranında sonucu kontrol eder.

Kalıcı key mobil uygulamaya gömülmez. Ağ hatasında idempotency key olmadan kör retry yapılmaz. Model ve profil fiyatları yapılandırılmadan bütçeli canlı çağrı başlamaz.

Streaming istemcisi SSE çerçevelerini tam event sınırında parse eder; UTF-8 ağ chunk sınırları metin sınırı değildir. Cancel sırasında CancellationToken bütün HTTP akışına iletilir.
