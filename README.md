# CodeMap

Tool CLI quét codebase .NET và sinh ra file tĩnh (`.md`, `.jsonl`) để **đưa vào chat AI (GitHub Copilot)**, trả lời câu hỏi: *"sửa chỗ này thì ảnh hưởng tới đâu?"*

- Chạy **hoàn toàn offline**, không gọi mạng, không gửi code đi đâu.
- **Chỉ đọc** solution của bạn, không bao giờ sửa file trong đó.
- Không phải MCP server — bạn chạy lệnh trong terminal, rồi tự attach file kết quả vào chat AI.

---

## Phần 1 — Cài đặt (làm 1 lần, máy nào cũng làm y hệt)

### Cần có sẵn

| Thứ | Bắt buộc? | Dùng để làm gì |
|---|---|---|
| **.NET SDK 8, 9 hoặc 10** | ✅ Bắt buộc | Build và chạy tool — **một bản bất kỳ trong ba là đủ**, không cần đúng .NET 8 |
| **git** | ✅ Bắt buộc | Clone repo này, và lấy lịch sử ticket/cảnh báo index cũ khi dùng tool |
| **node + npm** | Chỉ khi quét frontend | Đọc lời gọi API trong file Angular/TypeScript |

Kiểm tra nhanh:

```bash
dotnet --list-sdks
```

> Tool build ở `net8.0` nhưng bật `RollForward=Major`, nên **chạy được trên runtime 8, 9 hoặc 10**. Bạn không cần cài thêm .NET 8 chỉ để dùng nó. Codebase **đích** mà bạn đem đi quét cũng vậy — `net8.0`, `net9.0`, `net10.0` đều quét được (đã kiểm chứng thật).

### Lấy source code

```bash
git clone https://github.com/huytt-it/codemap-dotnet.git
cd codemap-dotnet
```

### Cài đặt — chọn 1 trong 2 cách

#### Cách A (khuyến nghị): cài thành lệnh `codemap` thật

Không cần quyền admin, không sửa PowerShell profile, không tạo alias. Chạy 2 lệnh trong thư mục vừa clone:

```bash
dotnet pack CodeMap.Cli -c Release
```

```bash
dotnet tool install --global --add-source ./nupkg CodeMap.Cli
```

Xong. Mở terminal mới rồi gõ `codemap` ở bất cứ đâu. Tool được cài vào thư mục người dùng (`~/.dotnet/tools`), không đụng gì tới hệ thống.

Sau này pull code mới về thì cập nhật bằng:

```bash
dotnet pack CodeMap.Cli -c Release; dotnet tool update --global --add-source ./nupkg CodeMap.Cli
```

> **Nếu gõ `codemap` báo "command not found"**: thư mục `~/.dotnet/tools` chưa nằm trong PATH (hiếm, thường bộ cài .NET SDK tự thêm). Gọi đầy đủ `"$HOME/.dotnet/tools/codemap"` cũng chạy y hệt.

#### Cách B: không cài gì, gọi thẳng file dll

Dùng khi chính sách máy chặn cả `dotnet tool install`:

```bash
dotnet build CodeMap.Cli -c Release
```

Sau đó thay mọi chữ `codemap` trong tài liệu này bằng `dotnet "<đường-dẫn-repo>\CodeMap.Cli\bin\Release\net8.0\CodeMap.Cli.dll"`, trong đó `<đường-dẫn-repo>` là thư mục bạn vừa clone.

### Kiểm tra cài đúng (khuyến khích cho máy mới)

```bash
dotnet test tests/CodeMap.Tests
```

Toàn bộ test phải pass. Nếu đỏ ngay từ máy mới clone, kiểm tra `dotnet --list-sdks` trước khi báo lỗi.

Đổi máy khác (hoặc đồng nghiệp clone lại) thì làm lại từ đầu Phần 1 — không có bước nào phụ thuộc vào máy cũ.

### Lười đọc? Bảo AI setup hộ

Nếu bạn dùng Copilot / Claude Code / Cursor, mở [docs/SETUP-PROMPT.md](docs/SETUP-PROMPT.md), copy nguyên khối prompt trong đó dán vào chat AI — nó sẽ tự dò SDK, chọn cách cài phù hợp với quyền hạn máy bạn, quét repo và tự điền đường dẫn vào file hướng dẫn cho AI.

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

`--solution` nhận **cả `.sln` lẫn `.slnx`** (định dạng XML mà .NET 10 SDK sinh ra mặc định).

> **Nếu bước này lỗi:** thử `dotnet restore` trong repo trước. Vẫn lỗi thì thêm `--syntax-only` — quét ở mức nông hơn, không cần solution build được, nhưng kết quả kém chi tiết hơn.
>
> Nếu repo có **cả `.sln` lẫn `.slnx`** (thường gặp khi đang chuyển đổi format), `dotnet restore` trần sẽ báo lỗi MSB1011 vì không biết chọn file nào — chỉ định rõ: `dotnet restore MyApp.sln`.

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

Muốn tự động quét mỗi đêm (hiện **đang tắt**, đang chạy tay): xem `docs/OPS-NIGHTLY-SCAN.md` — tài liệu nội bộ, không có trong repo public (xem ghi chú ở mục "Tài liệu thêm" cuối trang).

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
| `where` không ra gì | Tool báo rõ là "không tìm thấy", không đoán bừa. Thử lại bằng đúng ngôn ngữ team viết commit/ticket (tín hiệu mạnh nhất của `where` là khớp message ticket cũ), hoặc thử `find` với từ khóa tiếng Anh nếu đã đoán được tên symbol. |
| `scan-fe` báo "typescript package not found" | Chạy `npm install` trong thư mục frontend trước. |

**Nguyên tắc chung của tool:** chỗ nào không phân tích được thì ghi vào `diagnostics.json` và mục "Blind spots" trong report — **không bao giờ đoán bừa rồi im lặng**. Nếu report nói không biết, tức là thật sự không biết, đừng bỏ qua.

---

## Cho AI agent đọc (GitHub Copilot, Claude...)

[docs/copilot-instructions.md](docs/copilot-instructions.md) là bản hướng dẫn đầy đủ cho AI: quy trình hỏi-đáp, cách đọc report, ngôn ngữ nào dùng cho `where`, các điều cấm. Copy file này vào `.github/copilot-instructions.md` **trong repo bạn đang quét** (không phải repo `codemap-dotnet` này) để agent tự đọc mỗi session.

## Tài liệu thêm

- [docs/SETUP-PROMPT.md](docs/SETUP-PROMPT.md) — prompt copy-paste để AI tự cài đặt và cấu hình thay bạn
- [docs/FEATURES.md](docs/FEATURES.md) — tool làm được gì, **không** làm được gì (nên đọc trước khi tin kết quả)
- `docs/CODEMAP-SPEC.md`, `docs/OPS-NIGHTLY-SCAN.md`, `docs/TEST-REPORT-PHASE*.md` — tài liệu nội bộ (spec thiết kế, vận hành quét đêm, báo cáo test từng giai đoạn). **Không có trong repo public** — chỉ tồn tại trong bản làm việc gốc, chưa được đẩy lên git.
