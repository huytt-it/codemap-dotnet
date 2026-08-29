# CodeMap

Tool CLI quét codebase .NET và sinh ra file tĩnh (`.md`, `.jsonl`) để **đưa vào chat AI (GitHub Copilot)**, trả lời câu hỏi: *"sửa chỗ này thì ảnh hưởng tới đâu?"*

- Chạy **hoàn toàn offline**, không gọi mạng, không gửi code đi đâu.
- **Chỉ đọc** solution của bạn, không bao giờ sửa file trong đó.
- Không phải MCP server — bạn chạy lệnh trong terminal, rồi tự attach file kết quả vào chat AI.

---

## Phần 1 — Cài đặt (làm 1 lần)

### Cần có sẵn

| Thứ | Bắt buộc? | Dùng để làm gì |
|---|---|---|
| **.NET 8 SDK** | ✅ Bắt buộc | Build và chạy tool |
| **git** | Nên có | Lấy lịch sử ticket, cảnh báo index cũ |
| **node + npm** | Chỉ khi quét frontend | Đọc lời gọi API trong file Angular/TypeScript |

Kiểm tra nhanh:

```bash
dotnet --version
```

### Build tool

```bash
cd D:\Work\Projects\Tool
```

```bash
dotnet build CodeMap.Cli -c Release
```

Xong. File chạy nằm ở `CodeMap.Cli\bin\Release\net8.0\CodeMap.Cli.dll`.

### Đặt lệnh tắt cho gọn (khuyến khích)

Gõ `dotnet D:\Work\...\CodeMap.Cli.dll` mỗi lần rất dài. Tạo alias trong PowerShell profile:

```bash
notepad $PROFILE
```

Thêm dòng này vào file vừa mở, rồi lưu và mở lại terminal:

```powershell
function codemap { dotnet "D:\Work\Projects\Tool\CodeMap.Cli\bin\Release\net8.0\CodeMap.Cli.dll" @args }
```

Từ đây tài liệu này viết `codemap <lệnh>` cho ngắn. Nếu bạn không tạo alias, thay `codemap` bằng `dotnet "D:\Work\Projects\Tool\CodeMap.Cli\bin\Release\net8.0\CodeMap.Cli.dll"`.

---

## Phần 2 — Quét lần đầu cho 1 repo

Giả sử repo của bạn ở `D:\Repos\MyApp`, solution là `D:\Repos\MyApp\MyApp.sln`.

**Quan trọng:** luôn `cd` vào thư mục repo trước khi chạy — tool dùng thư mục hiện tại để kiểm tra index còn mới hay đã cũ.

```bash
cd D:\Repos\MyApp
```

### Bước 1 — Quét backend (bắt buộc)

```bash
codemap scan --solution MyApp.sln --out D:\CodeMapIndex\MyApp
```

> **Nếu bước này lỗi:** thử `dotnet restore` trong repo trước. Vẫn lỗi thì thêm `--syntax-only` — quét ở mức nông hơn, không cần solution build được, nhưng kết quả kém chi tiết hơn.

### Bước 2 — Quét lịch sử git (nên làm)

```bash
codemap scan-git --repo . --out D:\CodeMapIndex\MyApp
```

> **Nếu báo "No ticket ID matched":** repo của bạn đặt tên commit khác quy ước mặc định (`#1234`, `TICKET-1234`, `BUG-1234`, `JIRA-1234`). Tạo file `codemap.config.json` ở gốc repo — xem [Phần 5](#phần-5--cấu-hình-tùy-chọn).

### Bước 3 — Quét frontend (bỏ qua nếu không có FE riêng)

```bash
codemap scan-fe --root D:\Repos\MyApp.Web --out D:\CodeMapIndex\MyApp
```

```bash
codemap link --index D:\CodeMapIndex\MyApp\index
```

> **Lưu ý:** thư mục frontend phải đã chạy `npm install` (cần `node_modules/typescript`). Nếu chưa, tool vẫn chạy nhưng bỏ qua phần Angular, chỉ quét jQuery — và báo rõ trên màn hình.

### Bước 4 — Sinh bản đồ tổng quan

```bash
codemap map --index D:\CodeMapIndex\MyApp\index --out D:\CodeMapIndex\MyApp
```

Mở `D:\CodeMapIndex\MyApp\MAP.md` xem thử. File này người đọc được, ≤ 500 dòng.

---

## Phần 3 — Dùng hằng ngày

### Tình huống A: "Tôi sắp sửa method này, có nguy hiểm không?"

**Bước 1 — tìm mã định danh của method** (không ai gõ tay được cái này):

```bash
codemap find --index D:\CodeMapIndex\MyApp\index --query "OrderService.Cancel"
```

Copy dòng `M:...` ở kết quả.

**Bước 2 — xem ảnh hưởng:**

```bash
codemap impact --index D:\CodeMapIndex\MyApp\index --symbol "M:Orders.OrderService.Cancel(System.Int32)" --out impact.md
```

Mở `impact.md`, hoặc **attach thẳng vào chat Copilot** rồi hỏi bình thường.

### Tình huống B: "Ticket nói 'sửa lỗi hủy đơn hàng' — code nằm đâu?"

```bash
codemap where --index D:\CodeMapIndex\MyApp\index --query "hủy đơn hàng"
```

Trả về danh sách ứng viên **kèm lý do được chọn**. Lấy mã `M:...` phù hợp rồi đưa vào `impact` như trên.

### Tình huống C: "Tôi cần xem code thật + đường đi từ API tới đây"

```bash
codemap slice --index D:\CodeMapIndex\MyApp\index --symbol "M:Orders.OrderService.Cancel(System.Int32)" --out slice.md
```

`slice` đọc code **trực tiếp từ file trên đĩa lúc chạy**, nên dù index quét từ hôm qua, code trong file kết quả vẫn là code mới nhất.

### Khác nhau giữa `impact` và `slice`

| | `impact` | `slice` |
|---|---|---|
| Trả lời | "Có dám đụng không?" | "Đụng thì đụng cái gì?" |
| Nội dung | Danh sách gọn, đọc 10 giây | Có kèm code thật, ticket cũ |
| Khi nào dùng | Trước khi quyết định | Sau khi đã quyết định đào sâu |

---

## Phần 4 — Cập nhật lại index

Tool **không** tự cập nhật. Code đổi thì index cũ dần.

Không sao cả — mọi file `.md` sinh ra đều có dòng cảnh báo ở đầu, kiểu:

```
current HEAD b7e1d04 · 11 commit(s) behind, 6 relevant file(s) changed since the scan
```

Thấy con số lớn thì quét lại. Chạy lại đúng các lệnh ở [Phần 2](#phần-2--quét-lần-đầu-cho-1-repo) (ghi đè lên thư mục cũ, an toàn).

> Ghi chú tay của bạn trong `MAP.md` (phần giữa `<!-- human:start -->` và `<!-- human:end -->`) **luôn được giữ lại** khi quét lại. Cứ ghi chú thoải mái vào đó.

Muốn tự động quét mỗi đêm (hiện **đang tắt**, đang chạy tay): xem [docs/OPS-NIGHTLY-SCAN.md](docs/OPS-NIGHTLY-SCAN.md).

---

## Phần 5 — Cấu hình (tùy chọn)

Tạo file `codemap.config.json` ở **gốc repo** nếu cần. Không có file này thì tool dùng mặc định, vẫn chạy bình thường.

```json
{
  "ticketPattern": "(?:#|TICKET-|BUG-|JIRA-)(\\d{3,6})",
  "diAttribute": "InjectableAttribute",
  "frontendAppDir": "src/app"
}
```

| Khóa | Khi nào cần |
|---|---|
| `ticketPattern` | Commit của team đặt mã ticket theo kiểu khác (vd `ABC-123`) |
| `diAttribute` | Team dùng attribute tự viết để đánh dấu DI thay vì `AddScoped/AddSingleton` |
| `frontendAppDir` | Frontend không theo cấu trúc `src/app/` chuẩn của Angular CLI |

---

## Phần 6 — Gặp lỗi thì làm gì

| Hiện tượng | Cách xử lý |
|---|---|
| `scan` báo project bị "degraded" | Bình thường — project đó không build được, tool tự hạ xuống mức quét nông và **vẫn chạy tiếp**. Xem lý do trong `index\diagnostics.json`. |
| `impact` trả về 0 entry point | Tăng `--depth` (mặc định 5). Hoặc method đó thật sự không ai gọi. Hoặc entry point là dạng tool chưa nhận diện được (xem [FEATURES.md](docs/FEATURES.md) phần giới hạn). |
| `slice` báo "Could not re-locate this symbol" | Symbol đã bị đổi tên/xóa sau lần quét. Quét lại rồi `find` lại để lấy mã mới. |
| `where` không ra gì | Tool báo rõ là "không tìm thấy", không đoán bừa. Thử `find` với từ khóa tiếng Anh thay vì mô tả nghiệp vụ. |
| `scan-fe` báo "typescript package not found" | Chạy `npm install` trong thư mục frontend trước. |

**Nguyên tắc chung của tool:** chỗ nào không phân tích được thì ghi vào `diagnostics.json` và mục "Blind spots" trong report — **không bao giờ đoán bừa rồi im lặng**. Nếu report nói không biết, tức là thật sự không biết, đừng bỏ qua.

---

## Tài liệu thêm

- [docs/FEATURES.md](docs/FEATURES.md) — tool làm được gì, **không** làm được gì (nên đọc trước khi tin kết quả)
- [docs/CODEMAP-SPEC.md](docs/CODEMAP-SPEC.md) — spec thiết kế đầy đủ
- [docs/OPS-NIGHTLY-SCAN.md](docs/OPS-NIGHTLY-SCAN.md) — chạy tự động hằng đêm (hiện đang tắt)
- `docs/TEST-REPORT-PHASE*.md` — báo cáo test từng giai đoạn phát triển
