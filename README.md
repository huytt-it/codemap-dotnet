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

Clone repo này về (bước "Lấy source code" ở trên) rồi mở [setup/SETUP-PROMPT.md](setup/SETUP-PROMPT.md), copy nguyên khối prompt trong đó dán vào Copilot / Claude Code / Cursor — nó sẽ tự dò SDK, chọn cách cài phù hợp với quyền hạn máy bạn, khai báo `codemap.projects.json` và quét. Prompt cố tình **cấm agent tự `git clone`**: bạn clone, rồi chỉ đường dẫn cho nó.

---

## Phần 2 — Khai báo project rồi quét (cách khuyến nghị)

Thay vì nhớ 4 đường dẫn tuyệt đối cho mỗi repo, khai báo **một lần** vào file `codemap.projects.json`, rồi mọi lệnh về sau chỉ cần `--project <tên>`.

### Bước 1 — Tạo `codemap.projects.json`

Có sẵn template kèm 3 ví dụ trong [setup/](setup/) — copy ra rồi điền:

```bash
cp setup/codemap.projects.example.json setup/codemap.projects.json
```

Đặt ở đâu cũng được: trong `setup/`, gốc repo, một thư mục "workspace" chung, hoặc `~/.codemap/`. Tool tìm theo thứ tự `--config` → thư mục hiện tại rồi ngược lên các thư mục cha → `~/.codemap/`.

```json
{
  "description": "Các codebase tôi đang index",
  "projects": [
    {
      "name": "shop",
      "description": "Backend đơn hàng — Razor Pages + PublicApi",
      "solution": "D:/Repos/Shop/Shop.sln",
      "output": "D:/CodeMapIndex/Shop",
      "frontend": "D:/Repos/Shop.Web",
      "commitLanguage": "ja"
    },
    {
      "name": "billing",
      "description": "Dịch vụ hoá đơn, tách riêng",
      "solution": "D:/Repos/Billing/Billing.slnx",
      "output": "D:/CodeMapIndex/Billing",
      "commitLanguage": "en"
    }
  ]
}
```

| Khoá | Bắt buộc? | Ý nghĩa |
|---|---|---|
| `name` | ✅ | Tên ngắn dùng cho `--project`. Không phân biệt hoa thường, không được trùng nhau. |
| `solution` | ✅ | File `.sln` hoặc `.slnx` cần quét. |
| `output` | ✅ | Thư mục output. **Index nằm ở `<output>/index`**, `MAP.md` ở `<output>/MAP.md`. |
| `description` | — | Mô tả cho người (và AI) đọc hiểu codebase này là gì. |
| `repo` | — | Gốc git. Bỏ trống thì lấy thư mục chứa solution. |
| `frontend` | — | Thư mục Angular/TypeScript. Bỏ trống thì bỏ qua hẳn bước quét FE. |
| `commitLanguage` | — | Ngôn ngữ team viết commit (`ja`/`vi`/`en`...). AI đọc để biết nên hỏi `where` bằng ngôn ngữ nào. |

> Đường dẫn có thể là **tương đối** — tính từ vị trí chính file `codemap.projects.json`, không phải từ thư mục bạn đang đứng. Nhờ vậy cả cây thư mục copy sang máy khác vẫn chạy.

### Bước 2 — Quét

```bash
codemap sync --project shop
```

Một lệnh chạy trọn `scan` → `scan-git` → `scan-fe` → `link` → `map`, đúng thứ tự phụ thuộc dữ liệu. Quét tất cả project cùng lúc:

```bash
codemap sync --all
```

`scan` lỗi thì dừng project đó lại (index dở còn tệ hơn không có). `scan-git` và `scan-fe` chỉ là bổ sung — repo chưa có git hay không có FE riêng thì bỏ qua và vẫn ra `MAP.md` dùng được.

### Bước 3 — Kiểm tra

```bash
codemap projects
```

Liệt kê mọi project, đường dẫn thật sau khi resolve, và **trạng thái index**: đã build chưa, bao nhiêu symbol, quét lúc nào, cách đây mấy ngày.

### Dùng hằng ngày

```bash
codemap where --project shop --query "注文のキャンセル"
```

Mọi lệnh query (`find`, `where`, `impact`, `slice`, `map`, `link`) đều nhận `--project <tên>` thay cho `--index <đường dẫn dài>`.

---

## Phần 2b — Quét thủ công, không dùng file config

Vẫn chạy được bình thường nếu bạn không muốn tạo `codemap.projects.json`.

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

Thấy con số lớn thì quét lại: `codemap sync --project <tên>` (hoặc `--all`). Ghi đè lên thư mục cũ, an toàn. Không dùng file config thì chạy lại các lệnh ở [Phần 2b](#phần-2b--quét-thủ-công-không-dùng-file-config).

> Ghi chú tay của bạn trong `MAP.md` (phần giữa `<!-- human:start -->` và `<!-- human:end -->`) **luôn được giữ lại** khi quét lại. Cứ ghi chú thoải mái vào đó.

Muốn tự động quét mỗi đêm (hiện **đang tắt**, đang chạy tay): xem `docs/OPS-NIGHTLY-SCAN.md` — tài liệu nội bộ, không có trong repo public (xem ghi chú ở mục "Tài liệu thêm" cuối trang).

---

## Phần 5 — Cấu hình (tùy chọn)

Có **hai** file config, khác nhau hoàn toàn, đừng nhầm:

| File | Đặt ở đâu | Trả lời câu hỏi |
|---|---|---|
| `codemap.projects.json` | Nơi bạn chọn (workspace, hoặc `~/.codemap/`) | **Quét cái gì, kết quả để đâu** — xem [Phần 2](#phần-2--khai-báo-project-rồi-quét-cách-khuyến-nghị) |
| `codemap.config.json` | **Gốc của repo bị quét** | **Quét như thế nào** — quy ước riêng của repo đó (dưới đây) |

Một repo có quy ước lạ thì cần file thứ hai; nhiều repo cùng lúc thì cần file thứ nhất. Không liên quan gì nhau, có thể dùng một hoặc cả hai.

### `codemap.config.json` — quy ước riêng của repo

Tạo ở **gốc repo bị quét** nếu cần. Không có file này thì tool dùng mặc định, vẫn chạy bình thường.

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

[setup/copilot-instructions.md](setup/copilot-instructions.md) là bản hướng dẫn đầy đủ cho AI: quy trình hỏi-đáp, cách đọc report, ngôn ngữ nào dùng cho `where`, các điều cấm. Copy file này vào `.github/copilot-instructions.md` **trong repo bạn đang quét** (không phải repo `codemap-dotnet` này) để agent tự đọc mỗi session. Nó không chứa đường dẫn cứng — tự đọc `codemap.projects.json` để biết index nằm đâu.

## Tài liệu thêm

- **[setup/](setup/)** — mọi thứ cần điền, gom một chỗ: template `codemap.projects.json`, prompt cho AI, và file hướng dẫn agent. Xem [setup/README.md](setup/README.md).
- [docs/FEATURES.md](docs/FEATURES.md) — tool làm được gì, **không** làm được gì (nên đọc trước khi tin kết quả)
- `docs/CODEMAP-SPEC.md`, `docs/OPS-NIGHTLY-SCAN.md`, `docs/TEST-REPORT-PHASE*.md` — tài liệu nội bộ (spec thiết kế, vận hành quét đêm, báo cáo test từng giai đoạn). **Không có trong repo public** — chỉ tồn tại trong bản làm việc gốc, chưa được đẩy lên git.
