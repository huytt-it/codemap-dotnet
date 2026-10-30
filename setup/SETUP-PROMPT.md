# Prompt bảo AI setup CodeMap thay bạn

Dùng khi cài CodeMap lên một máy mới. Mỗi máy làm lại một lần: registry, file instructions và cấu hình
MCP nằm ở thư mục người dùng, không đi theo git.

**Làm trước, tự tay:** clone repo này về máy. Prompt **không** cho agent tự clone; nó sẽ hỏi bạn đường dẫn.

```bash
git clone https://github.com/huytt-it/codemap-dotnet.git
```

Sau đó copy **toàn bộ khối trong khung bên dưới** và dán vào Copilot Chat ở chế độ **Agent** (hoặc Claude
Code, Cursor: AI nào chạy được lệnh terminal cũng được). Không cần sửa gì trước. Mọi đường dẫn, số repo và
thư mục đặt index đều do bạn quyết định; agent sẽ hỏi.

---

```
Bạn giúp tôi cài CodeMap trên máy này để agent trong VS Code dùng được qua MCP. Làm tuần tự, mỗi bước
kiểm chứng bằng lệnh thật rồi mới sang bước sau. Không giả định kết quả, không bịa đường dẫn.

LUẬT CHUNG, áp dụng cho mọi bước:
- Tôi ĐÃ TỰ CLONE source CodeMap. KHÔNG chạy `git clone`, không tải source từ đâu về.
- KHÔNG sửa PowerShell profile, PATH hệ thống, không xin quyền admin.
- KHÔNG ghi, sửa hay xoá bất kỳ file nào BÊN TRONG các repo tôi muốn index (kể cả .gitignore, .github/,
  .vscode/, codemap.config.json), và không chạy lệnh có thể ghi vào đó (`dotnet restore`, `npm install`...)
  nếu tôi chưa đồng ý rõ ràng. Mọi thứ CodeMap cần đặt ở thư mục người dùng hoặc ở thư mục tôi chỉ định.
- Đường dẫn, số lượng repo, thư mục đặt index: HỎI TÔI. Được đề xuất, không được tự quyết.
- Trước khi ghi một file ngoài thư mục CodeMap, nói cho tôi biết sẽ ghi file nào, nội dung gì.

## Bước 1 — Môi trường
Chạy `dotnet --list-sdks` và `git --version`.
- Cần .NET SDK 8, 9 HOẶC 10, bản nào cũng được. Không có thì dừng lại, đưa tôi link tải.
Báo tôi thấy những gì.

## Bước 2 — Build từ source tôi đã clone
Hỏi tôi đường dẫn thư mục CodeMap. Xác nhận đúng thư mục: phải có `CodeMap.slnx`, `CodeMap.Cli/`,
`tests/CodeMap.Tests/`. Thiếu thì báo tôi, đừng tự sửa. Rồi chạy từ thư mục đó:
  dotnet build CodeMap.Cli -c Release
Lỗi thì dán nguyên văn cho tôi, đừng tự sửa code.

## Bước 3 — Cài lệnh `codemap`
Cách A (ưu tiên, không cần admin), từ thư mục CodeMap:
  dotnet pack CodeMap.Cli -c Release
  dotnet tool install --global --add-source ./nupkg CodeMap.Cli
Nếu báo đã cài rồi: thay `install` bằng `update` (bản cũ có thể chưa có lệnh `codemap mcp`).
Kiểm chứng: mở shell mới, chạy `codemap --help`. Output phải có dòng `codemap mcp`.

Nếu Cách A bị chính sách máy chặn: dùng Cách B, gọi thẳng
`dotnet <thư mục CodeMap>/CodeMap.Cli/bin/Release/net8.0/CodeMap.Cli.dll`. Từ đây về sau, mọi chỗ ghi
`codemap` hiểu là lệnh đó. Nói rõ tôi đang dùng cách nào.

## Bước 4 — Hỏi tôi các lựa chọn, trong MỘT lượt
Hỏi gộp các câu sau, chờ tôi trả lời hết rồi mới làm tiếp:
  1. Các repo backend cần index nằm ở đâu? Tôi có thể đưa từng đường dẫn, hoặc một thư mục chứa nhiều
     repo. Nếu là thư mục chứa: liệt kê các file .sln/.slnx tìm được (chỉ đọc, sâu tối đa 3 cấp, bỏ qua
     bin/obj/node_modules) để tôi chọn. Số repo là bao nhiêu cũng được.
  2. Đặt index ở thư mục nào? Đề xuất một thư mục chung NGOÀI mọi repo, ví dụ `D:/CodeMapIndex`. Mỗi
     project sẽ nằm ở `<thư mục đó>/<tên project>`.
  3. Với từng repo đã chọn: tên ngắn (không dấu, không trùng), mô tả một dòng, ngôn ngữ team viết
     commit/ticket (ja / vi / en / ...), và có frontend Angular/TypeScript riêng không (đường dẫn).
     Đề xuất sẵn tên và mô tả từ tên solution để tôi chỉ việc sửa.
  4. Trong VS Code tôi thường mở thư mục nào: một thư mục cố định (ví dụ thư mục chứa các repo), hay
     mỗi lần một repo khác nhau?

## Bước 5 — Registry
Ghi `~/.codemap/codemap.projects.json` (Windows: `C:\Users\<tôi>\.codemap\codemap.projects.json`).
Mọi đường dẫn trong file là TUYỆT ĐỐI. Mỗi repo một entry:

{
  "projects": [
    {
      "name": "<tên ngắn>",
      "description": "<mô tả>",
      "solution": "<đường dẫn .sln/.slnx>",
      "output": "<thư mục index>/<tên ngắn>",
      "frontend": "<thư mục FE; BỎ HẲN khoá này nếu không có>",
      "commitLanguage": "<ja|vi|en|...>"
    }
  ]
}

Nếu file đã tồn tại: đọc nó, cho tôi xem, rồi THÊM entry mới vào. Không xoá entry cũ nếu tôi không bảo.
Một frontend gọi nhiều backend: mỗi cặp (backend, frontend) một entry, cùng đường dẫn FE được lặp lại,
không bịa khoá mới.
Xong chạy `codemap projects`. Dòng đầu "Registry: ..." phải trỏ đúng file vừa ghi; trỏ chỗ khác nghĩa là
còn một bản `codemap.projects.json` cũ gần thư mục hiện tại hơn: báo tôi.

## Bước 6 — Quy ước ticket (kiểm trước khi quét)
Với từng repo, chạy `git log --pretty=format:"%s%n%b" -80` trong repo đó (chỉ đọc) và đọc message thật.
Mẫu mặc định là `(?:#|TICKET-|BUG-|JIRA-)(\d{3,6})`. Tự kiểm ba điều:
  1. Mặc định khớp chưa chắc đã đúng: GitHub squash-merge gắn số PR vào cuối message (`Fix cancel (#1019)`),
     và mẫu mặc định sẽ gom theo SỐ PR. Message có prefix riêng (`SHO_1234`, `ABC-42`...) thì cần mẫu riêng.
  2. Nhóm bắt thứ nhất là ID ticket, nên phải bao cả cụm: `([A-Z][A-Z0-9]*_\d+)`, không phải
     `(?:SHO|ABC)_(\d+)` (cách sau gộp `SHO_1234` và `ABC_1234` thành một ticket).
  3. Trong JSON viết `\\d`.
Cần mẫu riêng thì ghi `codemap.config.json` (`{ "ticketPattern": "..." }`). Tool chỉ tìm file này ngược
lên từ thư mục repo, nên để KHÔNG ghi vào repo, đặt nó ở một thư mục cha nằm NGOÀI repo, và nó áp dụng
cho mọi repo bên dưới. Đề xuất cho tôi thư mục cha cụ thể. Nếu các repo dưới cùng thư mục cha cần mẫu
khác nhau, hoặc không có thư mục cha nào nằm ngoài repo, hỏi tôi muốn làm sao.

## Bước 7 — Quét
  codemap sync --all
Quét chỉ ghi vào thư mục index (cùng một file khoá và một thư mục tạm cạnh nó), không ghi vào repo.
- Kiểm `<output>/index/sync-complete.json` của từng project: có file là sync hoàn tất, `steps` ghi bước
  nào bị bỏ qua. Không chỉ dựa vào `meta.json`.
- `scan` lỗi: báo tôi. Có thể cần `dotnet restore` trong repo (lệnh này ghi vào obj/ của repo), nên hỏi
  tôi trước. Phương án không đụng repo: `codemap scan --solution ... --out <output> --syntax-only` (nông
  hơn: thiếu lời gọi sang kiểu của NuGet/framework).
- Có frontend mà `scan-fe` bị bỏ qua: thư mục FE cần `npm install`. Hỏi tôi, đừng tự chạy.
- In cho tôi 10 ticket đầu trong `<output>/index/ticket-files.jsonl` của mỗi project và hỏi: đây có đúng
  là mã ticket của team không, hay là số PR. Chỉ đi tiếp khi tôi xác nhận.

## Bước 8 — File instructions cho agent
  mkdir -p ~/.copilot/instructions
  cp <thư mục CodeMap>/setup/codemap.instructions.md ~/.copilot/instructions/codemap.instructions.md
Nếu đã có bản cũ ở đó: ghi đè sau khi báo tôi. Phần "Ngôn ngữ" trong file viết cho commit tiếng Nhật;
nếu commitLanguage của tôi khác, sửa bản vừa chép cho khớp.
Rồi tìm (chỉ đọc) các bản instructions CodeMap cũ có thể làm agent bỏ qua MCP: file khác trong
`~/.copilot/instructions/` nhắc tới codemap, và `.github/copilot-instructions.md` hoặc
`.github/instructions/*` trong các repo đã chọn có nhắc tới codemap. Chỉ BÁO tôi đường dẫn, không tự xoá
hay sửa file trong repo.

## Bước 9 — MCP server cho VS Code (cấp user, không đụng repo)
Thêm server vào cấu hình MCP cấp user của VS Code (lệnh "MCP: Open User Configuration" mở đúng file này):
  Windows: %APPDATA%\Code\User\mcp.json
  macOS:   ~/Library/Application Support/Code/User/mcp.json
  Linux:   ~/.config/Code/User/mcp.json
File đã có server khác thì giữ nguyên, chỉ thêm mục "codemap". File rỗng thì tạo { "servers": { ... } }.

  "codemap": {
    "type": "stdio",
    "command": "codemap",
    "args": ["mcp", "--config", "<đường dẫn tuyệt đối tới ~/.codemap/codemap.projects.json>",
             "--workspace", "<thư mục ở câu 4 Bước 4>"]
  }

- Cách B (không cài được tool): "command": "dotnet", và thêm đường dẫn CodeMap.Cli.dll làm phần tử đầu
  của "args".
- Dùng đường dẫn tuyệt đối, KHÔNG dùng ${workspaceFolder}.
- Tôi mở MỘT thư mục cố định: --workspace là thư mục đó. Nếu nó chứa nhiều project, lần gọi đầu của mỗi
  chat sẽ báo AMBIGUOUS_PROJECT kèm danh sách và agent gọi lại với project=<tên>. Đó là bình thường.
- Tôi mở mỗi lần một repo khác: BỎ cặp "--workspace" và giá trị của nó. Server dùng thư mục VS Code
  khởi động nó; không khớp project nào thì tool báo NOT_INDEXED kèm danh sách project.

## Bước 10 — Kiểm chứng và tổng kết
1. Kiểm index bằng CLI: chạy `codemap where --project <tên> --query "<mô tả nghiệp vụ, bằng ngôn ngữ
   commit của team>"`, lấy một docId `M:...` trong kết quả, rồi chạy
   `codemap impact --project <tên> --symbol "<docId>"`. Cho tôi xem output.
2. Nhắc tôi: THOÁT HẲN VS Code (File > Exit, đóng mọi cửa sổ) rồi mở lại. Agent chỉ nạp instructions và
   cấu hình MCP lúc VS Code khởi động; Reload Window và chat mới đều không đủ. Sau đó mở chat mới (chế độ
   Agent) và hỏi một câu như "Nếu tôi sửa <Class.Method> thì ảnh hưởng tới đâu?". Agent phải tự tìm và
   gọi tool `codemap_impact`. Nếu nó in lệnh cho tôi chạy thay vì gọi tool, xem lại các bản instructions
   cũ đã báo ở Bước 8.
3. Tổng kết ngắn:
   - cách cài (A hay B) và lệnh dùng hằng ngày;
   - danh sách project, thư mục index, vị trí registry, instructions, mcp.json, codemap.config.json (nếu có);
   - bước nào bị bỏ qua và vì sao (không có FE, ticket dùng mẫu mặc định...);
   - lệnh cần chạy khi code đổi nhiều: `codemap sync --project <tên>` hoặc `codemap sync --all`.
```

---

## Ghi chú cho người dùng

- **Mỗi máy chạy prompt một lần.** Registry (`~/.codemap/`), instructions (`~/.copilot/instructions/`) và
  `mcp.json` của VS Code nằm ngoài git, nên clone repo sang máy khác không mang chúng theo.
- **Không có gì được ghi vào các repo bạn index.** Prompt cấm việc đó, trừ khi bạn đồng ý cho từng trường
  hợp: `dotnet restore` khi scan lỗi, `npm install` cho frontend, hay `codemap.config.json` khi không có
  thư mục cha nào nằm ngoài repo.
- **Thêm repo sau này:** chạy lại prompt (Bước 5 thêm entry vào registry có sẵn), hoặc tự thêm entry vào
  `~/.codemap/codemap.projects.json` rồi chạy `codemap sync --project <tên>`. Không cần sửa `mcp.json` nếu
  repo mới nằm trong thư mục `--workspace` đã chọn.
- **Agent không chạy được MCP** (công cụ khác VS Code): bỏ Bước 9. File instructions tự lùi về cách cũ:
  agent in lệnh `codemap` để bạn chạy. Muốn agent tự chạy các lệnh chỉ đọc, chép
  `setup/codemap.permissions.json` sang `~/.codemap/` (xem Task 3 trong `setup/README.md`).
- Prompt **cấm AI tự `git clone`** và **không** sửa PowerShell profile hay PATH: bạn kiểm soát source lấy
  từ đâu, và không vướng chính sách của máy công ty. Cách B (gọi thẳng dll) luôn chạy được khi
  `dotnet tool install` bị chặn.
- Index không tự cập nhật. Xem [README, Phần 4](../README.md#part-4--refreshing-the-index) để biết khi nào
  cần quét lại; server MCP tự nạp index mới khi `codemap sync` xong.
