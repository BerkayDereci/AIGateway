# Panel tasarımı

Ürün geçici adı: PrismGate. Marka/ticari alan adı uygunluğu araştırılmış değil.
Modern geliştirici paneli; Türkçe UI. Renk: koyu slate, indigo aksan, emerald başarı, amber uyarı. Açık/koyu tema sistem tercihi ve kalıcı kullanıcı seçimi. Inter veya sistem fontu; icon için lucide.
Desktop sol sidebar 240px, üstte workspace/environment seçici. Mobil drawer. İçerik max 1440px, 8px spacing scale, yumuşak 12px köşeler. Büyük gradient/glassmorphism ve rastgele dashboard süsleri yok.

## Ekranlar
1. Auth: kayıt/giriş, anlaşılır field errors.
2. Overview: çağrı sayısı, estimated USD, p95 latency, error rate; zaman aralığı ve proje filtresi; fiyatı bilinmeyen çağrı sayısı ayrıca.
3. Projects: gerçek liste, create dialog, environment tabs, archive confirmation.
4. API Keys: prefix, created/expiry/revoked; create → one-time secret dialog, copy; revoke confirmation.
5. Provider Connections: OpenAI/Anthropic cards, masked suffix, status, update key. Secret yeniden gösterilemez.
6. Profiles: draft editor; primary/fallback; capability; output limit; timeout; publish revision; environment binding. Unsupported settings disabled with reason.
7. Playground: profile picker, text field, image upload/dropzone/preview, text/JSON mode, send/cancel, stream output, validated JSON tree/download, usage summary. Demo badge; ücretli verify action explicit.
8. Workout Import: program fotoğrafı → extract → editable days/exercises; warnings yanında; confirmation → JSON download. Tıbbi öneri veya otomatik egzersiz reçetesi yok.
9. Usage: sortable/paged requests; status, feature, provider/model, duration, tokens, cost state; request drawer attempts.
10. Limits: USD/request/concurrency/end-user limits; current reserved/settled; unknown exposure visible.
11. Settings: members/roles, plan entitlement, audit readout. Billing says pilot/manual plan; nonfunctional checkout button yok.

UI tüm empty/loading/error/offline durumları içerir. Form labels, focus rings, keyboard dialogs, accessible contrast. Secret copy action screen-reader accessible; success toast key'i içermez. Playground content browser storage'a yazılmaz.
