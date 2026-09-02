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

### Đơn vị công việc là một lần tích hợp vào nhánh chính, không phải một commit

`scan-git` đọc `git log --first-parent --diff-merges=first-parent --name-status -M`. Ba lựa chọn, mỗi cái sửa một lỗ hổng đo được trên repo thật:

| Cờ | Sửa gì | Đo trên eShopOnWeb |
|---|---|---|
| `--diff-merges=first-parent` | `git log` thường **không sinh danh sách file cho merge commit**, nên mọi merge đóng góp con số 0 | 36/165 ticket (22%) **chỉ tồn tại trong message merge** |
| `--first-parent` | Đi theo đúng dòng tích hợp, nên đọc diff của merge không đếm trùng các commit trong nhánh | 825 commit → 503 đơn vị |
| `-M` (và quy đường dẫn về tên hiện tại) | Rename làm lịch sử một file bị cắt đôi; đường dẫn cũ không bao giờ khớp `symbols.jsonl` vì file đó chỉ chứa file còn tồn tại | **53% đường dẫn trong lịch sử không còn tồn tại**, 110 cặp rename truy được |

Với team đặt tên nhánh theo ticket (`SHO_1234-fix-cancel`), mã ticket nằm trong message merge `Merge pull request #456 from org/SHO_1234-fix-cancel` — commit trong nhánh có thể chỉ ghi "wip". Không đọc diff của merge thì mất trắng ticket đó.

> `--diff-merges` cần git ≥ 2.31. Git cũ hơn thì tool nói rõ và quay về cách cũ (merge không đóng góp file), phần còn lại vẫn chạy.

Lọc nhiễu tự động: bỏ đơn vị đụng **>100 file** (format toàn repo), chỉ tính file `.cs .cshtml .razor .ts .js .html .sql .json .config`, bỏ cặp co-change xuất hiện <3 lần.

Ngưỡng trước đây là 50, đặt ra để bỏ ba thứ: merge, rename hàng loạt, format toàn repo. Hai thứ đầu giờ được xử lý đúng bản chất (merge là đơn vị, rename được nhận diện là rename), chỉ còn format toàn repo cần cắt theo kích thước. Đo phân bố trên eShopOnWeb: p95 = 47 file, p98 = 69 — ngưỡng 50 đang loại 4% lịch sử, gồm cả pull request lớn hợp lệ; ngưỡng 100 chỉ loại 0,8% (các đơn vị 150–241 file).

**Kết quả đo được sau khi đổi**, cùng repo, cùng lệnh:

| | Ticket | Cặp co-change |
|---|---|---|
| eShop (squash-merge) | 85 → **90** | 67 → **117** |
| eShopOnWeb (merge PR) | 122 → **165** | ~584 → **1414** |

> ⚠ Dữ liệu git vẫn phản ánh **lịch sử**, không phải cấu trúc hiện tại — file đã xóa vẫn xuất hiện. File đã **đổi tên** thì nay đã được quy về tên hiện tại.

### Khi nào `scan-git` không chạy được, hoặc chạy được nhưng dữ liệu mỏng

Dừng hẳn, báo lỗi rõ (exit 1):

- **Không có `git` trên PATH.**
- **Repo không có commit nào**, hoặc `--since` quá hẹp.
- **Không commit nào khớp mẫu ticket.** Tool probe 200 commit đầu; không khớp thì **từ chối ghi file rỗng** và bảo bạn đặt `ticketPattern` trong `codemap.config.json`. Đây là ca hay gặp nhất.

### `ticketPattern`: hai cách sai mà tool không bắt được

Mẫu mặc định là `(?:#|TICKET-|BUG-|JIRA-)(\d{3,6})`. Probe chỉ kiểm "có khớp cái gì không", không kiểm "có khớp đúng thứ bạn muốn không":

- **Số PR bị nhận nhầm thành ticket.** GitHub squash-merge gắn `(#1019)` vào cuối message, mẫu mặc định khớp đúng cái đó. Trên repo eShop thật, 292 commit khớp và ticket sinh ra chính là số PR. Không sai về kỹ thuật (PR cũng là một đơn vị thay đổi), nhưng nếu team dùng mã ticket riêng thì đây không phải thứ bạn muốn — mà không có cảnh báo nào.
- **Nhóm bắt chỉ lấy phần số làm gộp nhầm ticket.** Tool lấy nhóm bắt thứ nhất làm ID ([TicketExtractor.cs](../CodeMap.Query/Git/TicketExtractor.cs)). Với `(?:SHO|MONKAI)_(\d+)`, hai ticket `SHO_1234` và `MONKAI_1234` cùng ra ID `1234` và bị trộn thành một. Bắt cả cụm thay vì phần số: `([A-Z][A-Z0-9]*_\d+)` — cũng không cần liệt kê prefix, thêm dự án mới vẫn khớp.

Trong JSON phải escape thành `"([A-Z][A-Z0-9]*_\d+)"`. Viết `\d` thì tool báo lỗi parse rõ ràng chứ không bỏ qua.

Cách kiểm nhanh: chạy `git log --pretty=format:%s -80`, rồi sau khi `scan-git` xong, mở `<output>/index/ticket-files.jsonl` xem 10 dòng đầu — ID sinh ra có đúng là mã ticket của team không.

Chạy xong, báo thành công, nhưng dữ liệu thiếu — cần tự biết:

- **Shallow clone** (`git clone --depth`, checkout mặc định của nhiều CI): lịch sử bị cắt cụt.
- **Đơn vị đụng >100 file vẫn bị bỏ hoàn toàn.** Hiếm hơn ngưỡng 50 cũ nhiều, nhưng một lần format toàn repo kèm sửa logic thật thì phần logic đó cũng mất theo.

### Đường dẫn phải khớp với `scan`, nếu không dữ liệu git thành vô dụng

`git log` luôn in đường dẫn tính từ **gốc repo**, còn `scan` ghi đường dẫn symbol tính từ **thư mục chứa solution**. Hai bên được ghép bằng **so khớp chuỗi chính xác**, nên khi solution nằm sâu trong repo (`src/App.sln`) thì trước đây không cặp nào khớp: `where` mất nguồn mạnh nhất, `impact` mất sạch ticket và co-change — **không có lỗi, không có file rỗng, không có gì trong `diagnostics.json`**.

Hiện `scan-git` đọc `meta.json` để quy đường dẫn git về đúng gốc mà `scan` dùng, và in ra dòng `Solution is at 'src/' inside the repo — rebasing git paths onto it.` khi có quy đổi. Hai hệ quả:

- **Chạy `scan` trước `scan-git`.** `codemap sync` đã làm đúng thứ tự này. Chạy `scan-git` khi chưa có `meta.json` thì không quy đổi được, và tool nói rõ điều đó.
- Nếu ghép xong mà **không ticket nào chạm được file đã index**, tool in `WARNING` ngay tại chỗ — thường là `--repo` đang trỏ sang repo khác với solution đã quét.

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
| Test tự động | **194/194 pass**, build 0 lỗi 0 warning |
| Khung .NET hỗ trợ | Chạy trên runtime **8 / 9 / 10** (`RollForward=Major`); quét được codebase đích `net8.0`, `net9.0`, `net10.0`; đọc được solution cả `.sln` lẫn `.slnx` |
| Repo thật (eShopOnWeb) | Phát hiện 28 entry point thật, 2 cạnh MediatR thật được nối đúng |
| Benchmark `where` — tiếng Việt | 8/10 top-1 đúng, nhưng chỉ 6/10 đủ tin cậy (`docs/BENCHMARK-WHERE.md`) |
| Benchmark `where` — tiếng Nhật | 9/10 top-1 đúng + 2/2 báo đúng "không tìm thấy" (`docs/BENCHMARK-WHERE-JA.md`) |
| Bug thật tìm ra trong quá trình test | 5 bug (chi tiết trong `docs/TEST-REPORT-PHASE*.md`) — trong đó nghiêm trọng nhất là lỗi encoding làm hỏng toàn bộ commit message tiếng Việt, âm thầm tồn tại từ giai đoạn 2.5 |
| Chưa kiểm chứng | Repo backend/frontend thật của bạn — **nên làm trước khi tin kết quả** |
