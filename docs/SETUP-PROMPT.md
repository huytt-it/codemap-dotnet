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

## Bước 4 — Khai báo project vào codemap.projects.json
Hỏi tôi (đừng đoán), cho TỪNG codebase tôi muốn index — tôi có thể có nhiều:
  1. Đường dẫn repo, và file solution nào (.sln hoặc .slnx — CodeMap đọc được cả hai)
  2. Muốn để index ở đâu. Gợi ý: một thư mục chung ngoài repo, ví dụ `D:/CodeMapIndex/<tên>`.
     Nếu tôi chọn để trong repo, nhớ thêm thư mục đó vào .gitignore của repo đó.
  3. Có frontend Angular/TypeScript riêng không (đường dẫn), nếu có
  4. Team tôi viết commit/ticket bằng ngôn ngữ nào (ja / vi / en / ...)

Rồi tạo file `codemap.projects.json`. Hỏi tôi muốn đặt ở đâu — gốc repo, một thư mục workspace
chung, hay `~/.codemap/`. Định dạng:

{
  "projects": [
    {
      "name": "<tên ngắn, không dấu>",
      "description": "<mô tả codebase này là gì>",
      "solution": "<đường dẫn .sln hoặc .slnx>",
      "output": "<thư mục output>",
      "frontend": "<thư mục FE, BỎ HẲN khoá này nếu không có>",
      "commitLanguage": "<ja|vi|en|...>"
    }
  ]
}

Xong thì chạy `codemap projects` để tôi xem lại đường dẫn đã resolve đúng chưa.

## Bước 5 — Quét
  codemap sync --project <tên>      (hoặc `codemap sync --all` nếu khai báo nhiều project)

Lưu ý khi chạy:
- `scan` lỗi → thử `dotnet restore` trong repo trước, rồi chạy lại. Vẫn lỗi thì báo tôi;
  còn cách `codemap scan --solution ... --out ... --syntax-only` quét nông hơn.
- `scan-git` báo "No ticket ID matched" → quy ước commit của team tôi khác mặc định
  (`#123`, `TICKET-123`, `BUG-123`, `JIRA-123`). Hỏi tôi quy ước thật, rồi tạo
  `codemap.config.json` ở GỐC REPO BỊ QUÉT với khoá `ticketPattern`. Lưu ý đây là file khác
  với `codemap.projects.json` ở Bước 4 — đừng gộp làm một.
- `scan-fe` bị bỏ qua → thư mục FE phải đã chạy `npm install`. Không có FE riêng thì đúng là
  phải bỏ qua, đừng bịa ra.

Sau khi xong, chạy `codemap projects` để xác nhận trạng thái index, rồi mở `<output>/MAP.md`
và tóm tắt cho tôi: bao nhiêu project, bao nhiêu entry point, mục Blind Spots nói gì.

## Bước 6 — Cấu hình cho AI agent
Copy `docs/copilot-instructions.md` từ repo CodeMap sang repo TÔI ĐANG QUÉT, đặt tại
`.github/copilot-instructions.md` (tạo thư mục `.github` nếu chưa có).

File đó đã tự đọc `codemap.projects.json` nên KHÔNG cần điền đường dẫn tay. Nhưng phải kiểm 2 điều:
- Agent có tìm thấy `codemap.projects.json` từ trong repo đó không (tool tìm ngược lên thư mục
  cha, và cả `~/.codemap/`). Nếu không, bảo tôi chuyển file config tới chỗ tìm được.
- Phần "Ngôn ngữ" trong file đang viết sẵn cho codebase commit tiếng Nhật. Nếu `commitLanguage`
  của tôi khác, sửa lại cho khớp — đừng để nguyên nếu không đúng.

## Bước 7 — Kiểm chứng end-to-end
Chạy thử một truy vấn thật rồi cho tôi xem output:
  codemap where --project <tên> --query "<mô tả nghiệp vụ, bằng đúng ngôn ngữ team tôi viết commit>"
Lấy một docId `M:...` từ kết quả rồi chạy:
  codemap impact --project <tên> --symbol "<docId>"

Cuối cùng tổng kết ngắn gọn cho tôi:
- Đang dùng cách cài nào (A hay B), gõ lệnh gì để dùng hằng ngày
- File codemap.projects.json nằm ở đâu, khai báo mấy project
- Cần chạy lại lệnh nào khi code đổi nhiều (`codemap sync --project ...`)
- Bước nào đã bỏ qua và vì sao (không có FE, không có ticket...)
```

---

## Ghi chú cho người dùng

- Prompt này **không** bảo AI sửa PowerShell profile hay PATH hệ thống — đó là chủ ý, vì nhiều máy công ty
  chặn. Nếu `dotnet tool install` cũng bị chặn thì Cách B (gọi thẳng dll) luôn chạy được.
- Quét **nhiều repo**: thêm nhiều entry vào mảng `projects` của cùng một `codemap.projects.json`, rồi `codemap sync --all`. CodeMap chỉ cài một lần.
- Index không tự cập nhật. Xem [README, Phần 4](../README.md) để biết khi nào cần quét lại.
