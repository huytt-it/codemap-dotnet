# Prompt bảo AI setup CodeMap thay bạn

Copy **toàn bộ khối trong khung bên dưới** dán vào Copilot Chat (chế độ Agent), Claude Code, Cursor —
bất cứ AI nào chạy được lệnh terminal. Không cần sửa gì trước: prompt tự dò môi trường và tự hỏi lại bạn
khi thiếu thông tin.

Nếu AI của bạn **không chạy được lệnh** (Copilot Chat chế độ Ask chẳng hạn), nó sẽ in lệnh ra cho bạn
chạy tay từng bước — vẫn dùng được, chỉ chậm hơn.

---

```
Bạn giúp tôi cài đặt CodeMap (https://github.com/huytt-it/codemap-dotnet) trên máy này và cấu hình
cho AI agent dùng được. Làm tuần tự, mỗi bước kiểm chứng bằng lệnh thật rồi mới sang bước sau.
Không giả định kết quả, không bịa đường dẫn.

## Bước 1 — Kiểm tra môi trường
Chạy `dotnet --list-sdks` và `git --version`.
- Cần .NET SDK phiên bản 8, 9 hoặc 10 — BẤT KỲ bản nào trong ba là đủ. Đừng bắt tôi cài đúng .NET 8.
- Nếu không có SDK nào: dừng lại, đưa tôi link tải, đừng tự tải về.
Báo tôi biết bạn thấy SDK nào.

## Bước 2 — Lấy source và build
Hỏi tôi muốn clone CodeMap vào thư mục nào (đừng tự chọn). Sau đó:
  git clone https://github.com/huytt-it/codemap-dotnet.git
  cd codemap-dotnet
  dotnet build CodeMap.Cli -c Release
Nếu build lỗi, dán nguyên văn lỗi cho tôi, đừng tự sửa code.

## Bước 3 — Cài lệnh `codemap` (thử theo đúng thứ tự này)
Cách A — ưu tiên, không cần quyền admin, không sửa PowerShell profile:
  dotnet pack CodeMap.Cli -c Release
  dotnet tool install --global --add-source ./nupkg CodeMap.Cli
Kiểm chứng bằng cách mở shell mới và chạy `codemap` (không tham số) — phải in ra hướng dẫn sử dụng.

Nếu Cách A bị chính sách máy chặn: ĐỪNG cố sửa PowerShell profile, ĐỪNG cố sửa PATH hệ thống,
ĐỪNG xin quyền admin. Chuyển sang Cách B: ghi nhớ đường dẫn đầy đủ tới
`CodeMap.Cli/bin/Release/net8.0/CodeMap.Cli.dll` và từ đây về sau gọi qua `dotnet <đường-dẫn-dll>`.
Nói rõ cho tôi biết bạn đang dùng cách nào.

## Bước 4 — Quét codebase của tôi
Hỏi tôi 3 thứ, đừng đoán:
  1. Đường dẫn repo cần quét
  2. File solution nào (.sln hoặc .slnx — CodeMap đọc được cả hai)
  3. Muốn để index ở đâu. Gợi ý mặc định: thư mục `.codemap/` ngay trong repo đó.
     Nếu chọn trong repo, nhớ thêm `.codemap/` vào .gitignore của repo đó.

Rồi `cd` vào thư mục repo và chạy lần lượt (dừng lại báo tôi nếu bước nào lỗi):
  codemap scan     --solution <file solution> --out <thư mục index>
  codemap scan-git --repo .                   --out <thư mục index>
  codemap map      --index <thư mục index>/index --out <thư mục index>

Lưu ý khi chạy:
- `scan` lỗi → thử `dotnet restore` trong repo trước, rồi chạy lại. Vẫn lỗi thì thêm `--syntax-only`.
- `scan-git` báo "No ticket ID matched" → quy ước commit của team tôi khác mặc định
  (`#123`, `TICKET-123`, `BUG-123`, `JIRA-123`). Hỏi tôi quy ước thật, rồi tạo `codemap.config.json`
  ở gốc repo với khóa `ticketPattern`.
- Có frontend Angular/TypeScript riêng thì hỏi tôi đường dẫn và chạy thêm:
    codemap scan-fe --root <thư mục FE> --out <thư mục index>
    codemap link    --index <thư mục index>/index
  (thư mục FE phải đã `npm install`). Không có FE riêng thì bỏ qua, đừng bịa.

Sau khi xong, mở `<thư mục index>/MAP.md` và tóm tắt cho tôi: bao nhiêu project, bao nhiêu entry point,
mục Blind Spots nói gì.

## Bước 5 — Cấu hình cho AI agent
Copy `docs/copilot-instructions.md` từ repo CodeMap sang repo TÔI ĐANG QUÉT, đặt tại
`.github/copilot-instructions.md` (tạo thư mục `.github` nếu chưa có).

QUAN TRỌNG — file đó có khối cấu hình đường dẫn ở ngay đầu:
    INDEX_DIR  = ...
    MAP_FILE   = ...
    REPORT_DIR = ...
Sửa 3 dòng đó thành đường dẫn THẬT vừa dùng ở Bước 4. Đây là chỗ duy nhất trong file có đường dẫn.

Đọc lướt phần "Ngôn ngữ" trong file đó và sửa cho khớp thực tế repo của tôi: nó đang viết sẵn cho
codebase có commit/ticket tiếng Nhật. Hỏi tôi team viết commit bằng ngôn ngữ nào rồi chỉnh lại,
đừng để nguyên nếu không đúng.

## Bước 6 — Kiểm chứng end-to-end
Chạy thử một truy vấn thật rồi cho tôi xem output:
  codemap where --index <thư mục index>/index --query "<một mô tả nghiệp vụ, bằng ngôn ngữ team tôi viết commit>"
Lấy một docId `M:...` từ kết quả rồi chạy:
  codemap impact --index <thư mục index>/index --symbol "<docId>"

Cuối cùng tổng kết ngắn gọn cho tôi:
- Đang dùng cách cài nào (A hay B), gõ lệnh gì để dùng hằng ngày
- Index nằm ở đâu, cần chạy lại lệnh nào khi code đổi nhiều
- Bước nào đã bỏ qua và vì sao (không có FE, không có ticket...)
```

---

## Ghi chú cho người dùng

- Prompt này **không** bảo AI sửa PowerShell profile hay PATH hệ thống — đó là chủ ý, vì nhiều máy công ty
  chặn. Nếu `dotnet tool install` cũng bị chặn thì Cách B (gọi thẳng dll) luôn chạy được.
- Nếu bạn quét **nhiều repo**, chạy lại từ Bước 4 cho từng repo. CodeMap chỉ cài một lần.
- Index không tự cập nhật. Xem [README, Phần 4](../README.md) để biết khi nào cần quét lại.
