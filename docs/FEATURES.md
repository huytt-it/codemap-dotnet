# CodeMap — Báo cáo tính năng

Tài liệu này trả lời 2 câu hỏi: **tool làm được gì** và **tool KHÔNG làm được gì**. Phần thứ hai quan trọng hơn — vì người đọc kết quả cuối cùng là AI, mà AI sẽ giải thích trôi chảy kể cả khi dữ liệu thiếu. Bạn cần biết trước chỗ nào không nên tin.

Trạng thái: **hoàn thành đủ 6 giai đoạn theo spec**, 139/139 test tự động pass, đã kiểm chứng trên repo mã nguồn mở thật ([eShopOnWeb](https://github.com/dotnet-architecture/eShopOnWeb)).

---

## 1. Tổng quan 9 lệnh

| Lệnh | Nhóm | Làm gì | Cần gì |
|---|---|---|---|
| `scan` | Nạp dữ liệu | Quét C# bằng Roslyn → symbol, quan hệ gọi, DI, entry point | .NET SDK; solution build được (mức đầy đủ) |
| `scan-git` | Nạp dữ liệu | Đọc `git log` → ticket cũ, file hay sửa kèm nhau | git |
| `scan-fe` | Nạp dữ liệu | Quét Angular/jQuery → lời gọi API phía FE | node + `typescript` |
| `link` | Nạp dữ liệu | Khớp lời gọi FE ↔ endpoint BE | (đã chạy 2 lệnh trên) |
| `find` | Tra cứu | Tìm symbol theo **tên** | — |
| `where` | Tra cứu | Tìm symbol theo **mô tả nghiệp vụ** | `scan-git` (quan trọng) |
| `impact` | Phân tích | "Sửa cái này ảnh hưởng tới đâu" — bản gọn | — |
| `slice` | Phân tích | Như trên + code thật + lịch sử | — |
| `map` | Phân tích | Bản đồ tổng quan cả hệ thống (`MAP.md`) | — |

Ba lệnh nạp dữ liệu sau (`scan-git`, `scan-fe`, `link`) đều **tùy chọn** — thiếu thì tool vẫn chạy, chỉ kém thông tin hơn, và nói rõ là đang thiếu.

---

## 2. Tool đọc hiểu được gì trong code C#

### Quan hệ giữa các thành phần

| Loại | Nhận diện được | Ghi chú |
|---|---|---|
| Lời gọi method | ✅ | Chính xác cao (dùng semantic model của Roslyn) |
| Kế thừa / implement interface | ✅ | |
| Khởi tạo object (`new`) | ✅ | |
| Đọc/ghi field, property | ✅ | |
| **Gọi qua interface** | ✅ | Tự động mở rộng sang **mọi** class implement interface đó, đánh dấu `via:"interface"` |
| **Gọi qua MediatR** | ✅ | `mediator.Send(new XCommand())` → nối thẳng tới handler, đánh dấu `via:"mediatr"` |

> ⚠ Hai loại cuối là **suy luận theo quy ước**, không phải sự thật tuyệt đối — DI container lúc chạy thật có thể chọn implement khác. Tool luôn ghi rõ số lượng cạnh loại này trong mục "Blind spots" của mọi report.

### Dependency Injection

| Kiểu đăng ký | Nhận diện được |
|---|---|
| `services.AddScoped<IFoo, Foo>()` (và `AddSingleton`/`AddTransient`) | ✅ |
| Attribute tự viết (vd `[Injectable]` — cấu hình qua `codemap.config.json`) | ✅ |
| Assembly scanning (Scrutor, `AddClasses(...)`) | ❌ → ghi vào `diagnostics.json` |
| Đăng ký có điều kiện runtime (`if (env.IsProduction())`) | ⚠ Ghi nhận đăng ký nhưng không hiểu điều kiện |

### Điểm vào hệ thống (entry point)

| Loại | Nhận diện | Ví dụ |
|---|---|---|
| **HTTP** — MVC Controller | ✅ | `class OrdersController : ControllerBase` + `[HttpDelete("{id}")]` → `DELETE api/orders/{id}` |
| **Job** — background service | ✅ | `BackgroundService`, `IHostedService` |
| **Handler** — MediatR | ✅ | `IRequestHandler<,>`, `INotificationHandler<>` |
| Razor Pages | ❌ | `PageModel` + `OnGet`/`OnPost` — **không** nhận |
| Minimal API | ❌ | `app.MapGet("/x", ...)` — **không** nhận |
| gRPC, GraphQL, Azure Functions, SignalR Hub | ❌ | **không** nhận |

**Ghép route:** `[Route("api/[controller]")]` + `[HttpDelete("{id}")]` → `api/orders/{id}`. Token `[controller]` được thay đúng; token `[action]` **chưa** được thay.

---

## 3. Nối biên Frontend ↔ Backend

Đây là chỗ Roslyn hoàn toàn mù — được xử lý riêng bằng cách khớp route.

| Framework FE | Hỗ trợ | Độ tin cậy |
|---|---|---|
| **Angular** (`HttpClient`) | ✅ | Cao — dùng TypeScript Compiler API thật |
| **jQuery** (`$.ajax`, `$.get`, `$.post`) | ✅ | Thấp — regex, URL động thường không giải được |
| React / Vue / Svelte | ❌ | — |
| `fetch()` thuần, axios | ❌ | — |

**Cách nhận diện Angular:** tìm lời gọi `.get()/.post()/.put()/.patch()/.delete()` trên biến **có tên chứa "http"** (`this.http`, `this.httpClient`). Nếu bạn đặt tên khác hẳn (`this.api`, `this.dataService`) thì sẽ **bị bỏ sót**.

**Chuẩn hóa URL để khớp:** `` `/api/orders/${id}` `` (FE) và `api/orders/{id}` (BE) đều quy về `api/orders/{*}` rồi so khớp theo cặp `(HTTP method, route)`. Kết quả:
- Khớp đúng 1 → `exact`
- Khớp nhiều (do `{*}` làm mất kiểu) → `ambiguous`, ghi hết
- Không khớp → vào `diagnostics.json` (đây là **endpoint chết** hoặc **chỗ tool parse sai** — cả hai đều đáng xem)

Ngược lại, endpoint backend không FE nào gọi cũng được liệt kê riêng.

---

## 4. Dữ liệu từ git

`scan-git` bù đắp cho những quan hệ mà phân tích tĩnh không bao giờ thấy được (reflection, stored procedure, biên FE/BE, config).

| Sinh ra | Nội dung |
|---|---|
| **Ticket cũ** | Mã ticket trong commit message → đã từng sửa file nào |
| **File hay sửa kèm nhau** | Cặp file thường xuyên đổi chung, kèm chỉ số `strength` |

Lọc nhiễu tự động: bỏ commit đụng >50 file (merge, format toàn repo), chỉ tính file `.cs .ts .js .html .sql .json .config`, bỏ cặp co-change xuất hiện <3 lần.

> ⚠ Dữ liệu git phản ánh **lịch sử**, không phải cấu trúc hiện tại — file đã xóa/đổi tên vẫn xuất hiện.

---

## 5. Chống "nổ" report

Trong monolith phân tầng, một helper tầng dưới có thể bị gọi từ hàng trăm chỗ. Liệt kê phẳng thì vô dụng cho cả người lẫn AI. Cơ chế:

- Mặc định **không** liệt kê caller trung gian — chỉ hiện entry point và màn hình FE. Muốn đủ thì thêm `--full`.
- Gộp theo project/module, không liệt kê phẳng.
- **Ngưỡng hub:** chạm >30 entry point → bỏ hẳn danh sách, thay bằng cảnh báo *"đây là hub, blast radius toàn hệ thống, đừng đổi signature"*.
- `MAP.md` luôn **≤ 500 dòng**, kể cả trên repo rất lớn.
- Dù cắt gọn thế nào, **số fan-in thật luôn được ghi rõ**.

---

## 6. Xử lý index cũ

Tool không tự cập nhật, index luôn cũ hơn code. Hai cơ chế bù:

1. **Banner cảnh báo ở đầu mọi file `.md`** — so `git rev-parse HEAD` hiện tại với commit lúc quét, ghi rõ lệch bao nhiêu commit và bao nhiêu file **liên quan tới kết quả này** đã đổi. Banner nằm trong file (đi theo vào context của AI), không phải in ra màn hình rồi quên.
2. **`slice` đọc code trực tiếp từ đĩa lúc chạy** — index chỉ lưu "symbol nằm ở file nào", không lưu nội dung code. Nên index quét từ hôm qua vẫn cho ra code hôm nay. Nếu symbol đã bị đổi tên/xóa, tool báo rõ chứ không in code sai.

---

## 7. Danh sách giới hạn — đọc kỹ phần này

### Không bao giờ nhìn thấy (bản chất của phân tích tĩnh)

- **Reflection** (`Activator.CreateInstance`, `Type.GetMethod`, `dynamic`)
- **Stored procedure / SQL** — hoàn toàn ngoài tầm
- **Logic runtime** — điều kiện `if`, feature flag, config theo môi trường
- **Dữ liệu thật trong DB**

### Chưa hỗ trợ (có thể bổ sung sau)

| Hạng mục | Ảnh hưởng |
|---|---|
| Razor Pages, Minimal API làm entry point | ⚠ **Lớn** — project .NET 6+ dùng Minimal API sẽ mất phần lớn entry point |
| Mediator tự viết (không phải MediatR) | Mất cạnh nối tới handler |
| React/Vue/`fetch`/axios | Không nối được FE↔BE |
| VB.NET | Chỉ hỗ trợ C# |
| Đọc Pull Request (title, mô tả, comment) | Chỉ đọc `git log` cục bộ |
| Phân tích hình thái tiếng Nhật/Trung (MeCab, Kuromoji) | `where` cắt bigram ký tự thay thế — chạy được offline, nhưng ranh giới từ không chuẩn bằng từ điển |
| Nội dung comment và string literal | Index chỉ lưu symbol/quan hệ/route/lịch sử git — tìm theo nội dung comment vẫn phải dùng `grep` |

### Điểm cần lưu ý khi đọc kết quả

- **`where` khớp chuỗi thuần túy** — không có từ đồng nghĩa, không sửa chính tả. "hủy đơn" khớp "hủy đơn hàng", nhưng "cancel order" thì không (trừ khi commit message cũng viết vậy). Nó chỉ hoạt động tốt khi **đã từng có ticket mô tả bằng ngôn ngữ đó**.
- **Ngôn ngữ viết liền (Nhật/Trung) dùng được**, qua bigram ký tự chồng nhau + chuẩn hóa NFKC (gộp katakana nửa độ rộng và chữ Latin toàn độ rộng). Cặp toàn hiragana bị loại như stop-word vì đó là trợ từ/đuôi chia động từ. Giới hạn còn lại: **viết khác chữ là không khớp** — `キャンセル` / `取消` / `解約` cùng nghĩa nhưng không chung ký tự nào, nên nếu không ra kết quả thì phải thử lại bằng cách viết khác trước khi kết luận.
- **Điểm rủi ro (risk score) là ước lượng**, không phải công thức chuẩn — spec không quy định, đây là công thức tự thiết kế. Dùng để so sánh tương đối, đừng coi là con số tuyệt đối.
- **docId có thể trùng giữa 2 project khác nhau** (định danh không mã hóa tên assembly). Trường hợp này được ghi vào `diagnostics.json`.
- **Không có quét tăng dần (incremental)** — mỗi lần là quét lại toàn bộ. Đơn giản hơn và đủ dùng với nhịp 1 lần/ngày.

---

## 8. Nguyên tắc thiết kế xuyên suốt

Ba điều này quyết định cách bạn nên tin kết quả:

1. **Không đoán bừa.** Chỗ nào phân tích tĩnh không giải được thì ghi vào `diagnostics.json` và mục "Blind spots", không suy diễn. Report im lặng về chỗ nó không biết còn nguy hiểm hơn không có report.
2. **Thà báo thiếu còn hơn báo sai.** Lời gọi FE không parse được URL thì ghi nhận là "không parse được", không đoán đại một route.
3. **Chỉ đọc.** Không sửa gì trong solution đích, không chạy lệnh git nào làm đổi trạng thái repo, không gọi mạng.

---

## 9. Kiểm chứng — nên làm

Vì bạn sẽ hiểu codebase **thông qua AI** chứ không đọc index trực tiếp, gần như **không còn ai kiểm tra index đúng hay sai**. Index lệch, AI vẫn trả lời trôi chảy, không có tín hiệu báo động nào.

Hai việc bù lại (spec mục 9 yêu cầu, chưa làm — cần dữ liệu repo thật của bạn):

1. **Mỗi tháng liếc `MAP.md` một lần** — đối chiếu danh sách entry point và hub với thực tế bạn biết.
2. **Lấy ~10 bug đã fix trong quá khứ**, chạy `impact` trên symbol đã sửa, so với thứ **thực sự gãy** hồi đó. Đây là thứ duy nhất phân biệt được "AI trả lời trôi chảy" với "AI trả lời đúng".

---

## 10. Kiểm chứng đã thực hiện

| Hạng mục | Kết quả |
|---|---|
| Test tự động | **184/184 pass**, build 0 lỗi 0 warning |
| Khung .NET hỗ trợ | Chạy trên runtime **8 / 9 / 10** (`RollForward=Major`); quét được codebase đích `net8.0`, `net9.0`, `net10.0`; đọc được solution cả `.sln` lẫn `.slnx` |
| Repo thật (eShopOnWeb) | Phát hiện 28 entry point thật, 2 cạnh MediatR thật được nối đúng |
| Benchmark `where` — tiếng Việt | 8/10 top-1 đúng, nhưng chỉ 6/10 đủ tin cậy (`docs/BENCHMARK-WHERE.md`) |
| Benchmark `where` — tiếng Nhật | 9/10 top-1 đúng + 2/2 báo đúng "không tìm thấy" (`docs/BENCHMARK-WHERE-JA.md`) |
| Bug thật tìm ra trong quá trình test | 5 bug (chi tiết trong `docs/TEST-REPORT-PHASE*.md`) — trong đó nghiêm trọng nhất là lỗi encoding làm hỏng toàn bộ commit message tiếng Việt, âm thầm tồn tại từ giai đoạn 2.5 |
| Chưa kiểm chứng | Repo backend/frontend thật của bạn — **nên làm trước khi tin kết quả** |
