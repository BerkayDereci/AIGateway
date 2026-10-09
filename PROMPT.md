Bu klasördeki belgeleri temel alarak ticari ürüne dönüşebilecek AI Gateway pilot MVP'sini uçtan uca geliştir. Tasarım anlatımıyla yetinme: gerçek dosyaları oluştur, uygulamayı çalıştır, test et ve hataları düzelt.

Önce README.md, CLAUDE.md, specs altındaki tüm belgeleri ve examples altındaki dosyaları oku. Bu dosyaları koru. Kısa bir uygulama planı çıkar ve aynı oturumda uygulamaya geç; rutin kararlar için onay bekleme.

Stack: .NET 10 LTS ASP.NET Core, PostgreSQL/EF Core, React/TypeScript/Vite, Docker Compose, özel S3-compatible storage/MinIO, OpenTelemetry. Backend modüler monolit olacak. Redis, Kafka, RabbitMQ, Kubernetes, RAG, agent çalıştırma, MCP veya gerçek ödeme sistemini ilk sürüme ekleme.

Ürün: workspace/project/environment izolasyonu, güvenli BYOK bağlantıları, proje API anahtarları, sürümlü model profilleri, metin+görsel inference, SSE, JSON Schema çıktısı, kota/bütçe rezervasyonu, kullanım raporu ve modern yönetim paneli. İlk demo fotoğraftan antrenman programı çıkarma.

Güncel sağlayıcı SDK ve resmi API belgelerini doğrula. Model adları/fiyatları uydurma. Sabit model fiyatlarını doğruymuş gibi gömme. SDK seçimini ve doğruladığın capability sınırlarını docs/provider-compatibility.md içinde kaynak/tarihle belirt. Ortam belgeleri veya SDK erişimi sağlamıyorsa bunu açık yaz; doğrulanmamış entegrasyonu tamamlanmış sayma.

Mock provider yalnızca Development/Testing'de ve görünür DEMO rozetiyle çalışsın. Gerçek OpenAI ve Anthropic adaptörlerini ayrı uygula; normal çalışma ortamında mock'a sessiz geçiş yasak. Anahtar yoksa anlaşılır kurulum ekranı göster. API'de desteklenmeyen özellik için açık hata dön.

Önce çalışan bir dikey akış kur, sonra tüm kapsama genişlet. Profil üzerinden üretim, özel görsel yükleme, güvenli kimlik doğrulama, tenant izolasyonu, atomik bütçe rezervasyonu ve streaming iptali önceliklidir. Testleri özelliklerle birlikte yaz. Sahte dashboard sayıları, çalışmayan butonlar, TODO ile bırakılmış kritik işlevler ve hardcoded kullanıcı/anahtar istemiyorum.

API ve veri davranışlarını specs belgelerinden uygula. Özellikle maliyeti estimated/actual-provider-usage/unknown olarak ayır; bilinmeyen fiyatı sıfır kabul etme. Başlamış stream için sessiz fallback yapma. Provider timeout sonrasında isteğin ücretlendirilmediğini varsayma.

Frontend: tüm ekranları gerçek backend'e bağla. Temiz geliştirici SaaS paneli; açık/koyu tema; iyi tipografi; responsive layout; error/loading/empty states; erişilebilir klavye akışları. specifications/UI.md değil specs/UI.md dosyasını kullan.

Teslim: kaynak kod, migration'lar, compose dosyası, Dockerfile'lar, örnek env, .NET örnek istemcisi, curl örnekleri, OpenAPI, CI, README çalıştırma rehberi, SECURITY.md, OPERATIONS.md ve IMPLEMENTATION_STATUS.md. CLAUDE.md içindeki komutları final stack'e göre güncelle.

Backend build/test, frontend typecheck/test/build ve mümkünse Docker Compose smoke testi çalıştır. Test sonuçlarını gerçek komut ve sonuçlarla raporla. Ağ/SDK/Docker kısıtında yapamadığın doğrulamayı açıkça belirt. Gerçek provider smoke testi sadece kullanıcı yerel olarak anahtar sağlamış ve ücretli çağrıyı etkinleştirmişse çalışsın. Gizli anahtar arama veya kullanıcı hesabına giriş yapma.

Kapsam bağlam limitine yaklaşırsa uygulamayı tutarlı halde bırakıp IMPLEMENTATION_STATUS.md dosyasına tamamlanan işler, başarısız kontroller ve tam sonraki komutları yaz. Tamamlanmamış özelliği tamamlanmış gösterme.

Şimdi tüm dosyaları oku ve uygulamayı üret. Son yanıtta çalıştırma komutları, gerçekten çalışan kapsam, test sonuçları ve kalan production gereksinimlerini kısa ve net belirt.
