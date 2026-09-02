# Prompt bảo AI setup CodeMap thay bạn

**Làm trước, tự tay:** clone repo này về máy. Prompt bên dưới **không** cho agent tự clone — nó sẽ hỏi bạn
đường dẫn tới thư mục đã có.

```bash
git clone https://github.com/huytt-it/codemap-dotnet.git
```

Sau đó copy **toàn bộ khối trong khung bên dưới** dán vào Copilot Chat (chế độ Agent), Claude Code, Cursor —
bất cứ AI nào chạy được lệnh terminal. Không cần sửa gì trước: prompt tự dò môi trường và tự hỏi lại bạn
khi thiếu thông tin.

Nếu AI của bạn **không chạy được lệnh** (Copilot Chat chế độ Ask chẳng hạn), nó sẽ in lệnh ra cho bạn
chạy tay từng bước — vẫn dùng được, chỉ chậm hơn.

---

```
Bạn giúp tôi cài đặt CodeMap trên máy này và cấu hình cho AI agent dùng được. Làm tuần tự, mỗi bước
kiểm chứng bằng lệnh thật rồi mới sang bước sau. Không giả định kết quả, không bịa đường dẫn.

QUAN TRỌNG: tôi ĐÃ TỰ CLONE source CodeMap về máy rồi. TUYỆT ĐỐI KHÔNG chạy `git clone`, không tải
source từ đâu về, không tự tạo lại project. Bạn chỉ làm việc trên thư mục tôi chỉ ra ở Bước 2.

## Bước 1 — Kiểm tra môi trường
Chạy `dotnet --list-sdks` và `git --version`.
- Cần .NET SDK phiên bản 8, 9 hoặc 10 — BẤT KỲ bản nào trong ba là đủ. Đừng bắt tôi cài đúng .NET 8.
- Nếu không có SDK nào: dừng lại, đưa tôi link tải, đừng tự tải về.
Báo tôi biết bạn thấy SDK nào.

## Bước 2 — Build từ source tôi đã clone sẵn
Hỏi tôi đường dẫn tới thư mục CodeMap tôi đã clone. Đừng đoán, đừng đi dò khắp ổ đĩa, và
nhắc lại: đừng clone.

Trước khi build, xác nhận đúng thư mục bằng cách kiểm tra có đủ 3 thứ sau (không đủ thì báo tôi,
đừng tự sửa):
  - file `CodeMap.slnx`
  - thư mục `CodeMap.Cli`
  - thư mục `tests/CodeMap.Tests`

Rồi từ chính thư mục đó:
  dotnet build CodeMap.Cli -c Release
Nếu build lỗi, dán nguyên văn lỗi cho tôi, đừng tự sửa code.

## Bước 3 — Cài lệnh `codemap` (thử theo đúng thứ tự này)
Vẫn đứng trong thư mục CodeMap ở Bước 2.

Cách A — ưu tiên, không cần quyền admin, không sửa PowerShell profile:
  dotnet pack CodeMap.Cli -c Release
  dotnet tool install --global --add-source ./nupkg CodeMap.Cli
Kiểm chứng bằng cách mở shell mới và chạy `codemap` (không tham số) — phải in ra hướng dẫn sử dụng.

Nếu Cách A bị chính sách máy chặn: ĐỪNG cố sửa PowerShell profile, ĐỪNG cố sửa PATH hệ thống,
ĐỪNG xin quyền admin. Chuyển sang Cách B: ghi nhớ đường dẫn đầy đủ tới
`CodeMap.Cli/bin/Release/net8.0/CodeMap.Cli.dll` và từ đây về sau gọi qua `dotnet <đường-dẫn-dll>`.
Nói rõ cho tôi biết bạn đang dùng cách nào.

## Bước 4 — Quyết định phạm vi, rồi khai báo project vào codemap.projects.json

**Hỏi tôi TRƯỚC TIÊN, chỉ 1 câu này, và nhớ câu trả lời cho suốt các bước còn lại (đừng hỏi lại
ở Bước 7):** tôi định dùng CodeMap cho đúng 1 project này thôi, hay nhiều project và sẽ thêm/bớt
theo thời gian?

- **Nhiều project / sẽ thêm bớt** → tạo file **thẳng tại `~/.codemap/codemap.projects.json`**
  (trên Windows là `C:\Users\<tên tôi>\.codemap\codemap.projects.json`). **KHÔNG tạo
  `setup/codemap.projects.json` bên trong thư mục CodeMap** — nếu để đó, lệnh `codemap` chạy từ
  bên trong thư mục CodeMap sẽ vô tình ăn nhầm bản này thay vì bản global (`codemap.projects.json`
  gần cwd hơn luôn được ưu tiên so với bản ở `~/.codemap/`), 2 bản sẽ lệch dần mà không ai biết.
  Vì đường dẫn giờ nằm ngoài mọi repo, **mọi giá trị `solution`/`output`/`repo`/`frontend` phải
  là đường dẫn tuyệt đối** (`D:/Repos/...`), không dùng đường dẫn tương đối.
- **Chỉ 1 project** → đặt ở gốc repo đích cũng được, đơn giản hơn.

Hỏi tôi (đừng đoán), cho TỪNG codebase tôi muốn index:
  1. Đường dẫn repo, và file solution nào (.sln hoặc .slnx — CodeMap đọc được cả hai)
  2. Muốn để index ở đâu. Gợi ý: một thư mục chung ngoài repo, ví dụ `D:/CodeMapIndex/<tên>`.
     Nếu tôi chọn để trong repo, nhớ thêm thư mục đó vào .gitignore của repo đó.
  3. Có frontend Angular/TypeScript riêng không (đường dẫn), nếu có. Hỏi thêm 1 câu:
     frontend đó có gọi sang NHIỀU backend không, và backend này có phục vụ NHIỀU frontend
     không — xem ghi chú "một entry = một cặp" bên dưới.
  4. Team tôi viết commit/ticket bằng ngôn ngữ nào (ja / vi / en / ...)

Dùng nội dung mẫu trong `setup/codemap.projects.example.json` (có ví dụ đủ 4 tình huống: có FE
riêng / không FE / solution nằm sâu cần chỉ rõ `repo` / 1 FE gọi 2 BE tách thành 2 entry) làm
tham khảo cấu trúc, nhưng **ghi
thẳng vào vị trí đã chọn ở trên** — đừng copy ra `setup/` trước rồi tính di chuyển sau, dễ quên.
Định dạng:

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

**Một entry = một cặp (backend, frontend).** `link` chỉ khớp lời gọi FE với endpoint BE
*trong cùng một index*, nên mỗi entry chỉ nhận đúng 1 `solution` và 1 `frontend`. Nếu tôi trả
lời là FE gọi nhiều BE (hoặc BE phục vụ nhiều FE), **đừng nhét nhiều đường dẫn vào một entry và
đừng bịa ra khoá mới** — tạo mỗi cặp một entry, cùng một đường dẫn FE được phép lặp lại ở nhiều
entry, đặt `name` cho thấy rõ cặp nào (`shop-orders`, `shop-billing`). Nói trước cho tôi 2 hệ quả:
mỗi entry quét độc lập nên BE dùng chung bị quét lại nhiều lần; và trong `diagnostics.json` của
từng cặp, lời gọi FE nhắm sang BE khác sẽ nằm ở mục "unmatched frontend calls" — đó là ngoài
phạm vi cặp đó, không phải link hỏng, đừng báo cho tôi như thể là lỗi.

Xong thì chạy `codemap projects` để tôi xem lại đường dẫn đã resolve đúng chưa, và dòng đầu tiên
của output ("Registry: ...") có trỏ đúng vào file bạn vừa tạo không — nếu trỏ vào chỗ khác thì có
một bản `codemap.projects.json` khác đang che khuất, phải tìm và xoá nó.

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

## Bước 6 — Quyền tự chạy lệnh
Hỏi tôi: muốn agent (bạn) tự chạy các lệnh `codemap` chỉ-đọc (`find`/`where`/`impact`/`slice`/
`projects`) mà không cần dán tay vào terminal mỗi lần không, hay muốn giữ nguyên kiểu "agent in
lệnh, tôi tự chạy, tự dán output"?

Nếu tôi đồng ý cho tự chạy: copy `setup/codemap.permissions.json` từ thư mục CodeMap ra CÙNG chỗ
với `codemap.projects.json` đã tạo ở Bước 4 (không phải trong `.github/`). Giữ nguyên default trong đó
(`find`/`where`/`impact`/`slice`/`projects` = true; `scan`/`sync`/`map`/`link` = false) trừ khi
tôi nói rõ muốn đổi khác. KHÔNG tự ý đổi `autoRun` của lệnh nào tôi không nhắc tới.

Nếu tôi không muốn: bỏ qua bước này, không tạo file. Mặc định không có file này thì mọi lệnh
đều phải hỏi trước — an toàn.

## Bước 7 — Cấu hình cho AI agent
**Dùng lại đúng câu trả lời "1 project hay nhiều project" đã hỏi ở Bước 4 — đừng hỏi lại.**

- **Nhiều project / sẽ thêm bớt** (Bước 4 đã đặt registry ở `~/.codemap/`) → đặt
  `setup/codemap.instructions.md` ở vị trí user-level tương ứng, áp dụng cho MỌI workspace trên
  máy tôi, không cần lặp lại bước này khi thêm project mới:
    mkdir -p ~/.copilot/instructions
    cp setup/codemap.instructions.md ~/.copilot/instructions/codemap.instructions.md
  (Trên Windows, `~` là `C:\Users\<tên tôi>\`.)

- **Chỉ 1 project** (Bước 4 đã đặt registry ở gốc repo đích) → đặt instructions theo repo, cùng
  tinh thần với registry:
    mkdir -p <đường-dẫn-repo-đích>/.github/instructions
    cp setup/codemap.instructions.md <đường-dẫn-repo-đích>/.github/instructions/codemap.instructions.md
  Lưu ý cách này chỉ có tác dụng khi workspace root đúng là repo đó.

Nếu registry đặt ở `~/.codemap/` (nhiều project) mà instructions lại đặt theo repo (`.github/instructions/`),
hoặc ngược lại, thì 2 nơi sẽ không khớp phạm vi nhau — luôn dùng CÙNG MỘT lựa chọn cho cả hai.

**Tuyệt đối không đặt tên file là `copilot-instructions.md`** — đó là tên dành riêng cho 1 file
duy nhất ở gốc mỗi repo theo quy ước GitHub Copilot, nếu tôi đã có file đó cho mục đích khác thì
copy đè vào sẽ mất nội dung cũ. Giữ nguyên tên `codemap.instructions.md`.

File đó đã tự đọc `codemap.projects.json` và `codemap.permissions.json` nên KHÔNG cần điền
đường dẫn tay. Nhưng phải kiểm 2 điều:
- Agent có tìm thấy `codemap.projects.json` (và `codemap.permissions.json` nếu có ở Bước 6) từ
  vị trí vừa chọn ở trên không (tool tìm ngược lên thư mục cha, và cả `~/.codemap/`). Nếu chọn
  cách user-level ở trên, đặt `codemap.projects.json`/`codemap.permissions.json` ở `~/.codemap/`
  cũng là hợp lý nhất — cùng tinh thần "1 chỗ cho mọi project". Không tìm thấy thì hỏi tôi.
- Phần "Ngôn ngữ" trong file đang viết sẵn cho codebase commit tiếng Nhật. Nếu `commitLanguage`
  của tôi khác, sửa lại cho khớp — đừng để nguyên nếu không đúng.

## Bước 8 — Kiểm chứng end-to-end
Chạy thử một truy vấn thật rồi cho tôi xem output:
  codemap where --project <tên> --query "<mô tả nghiệp vụ, bằng đúng ngôn ngữ team tôi viết commit>"
Lấy một docId `M:...` từ kết quả rồi chạy:
  codemap impact --project <tên> --symbol "<docId>"

Nếu tôi chọn "nhiều project" ở Bước 4, chạy thêm `codemap projects` từ MỘT thư mục khác hẳn
(ví dụ từ chính thư mục repo đích, không phải từ thư mục CodeMap) — dòng "Registry: ..." đầu
output phải trỏ đúng vào `~/.codemap/codemap.projects.json`. Nếu trỏ vào chỗ khác, nghĩa là còn
sót một bản `codemap.projects.json` cũ nằm gần cwd hơn — tìm và xoá nó (`find` tên file này trên
các thư mục tôi đã làm việc cùng trong session này), rồi chạy lại để xác nhận.

Cuối cùng tổng kết ngắn gọn cho tôi:
- Đang dùng cách cài nào (A hay B), gõ lệnh gì để dùng hằng ngày
- Tôi chọn "1 project" hay "nhiều project" ở Bước 4, registry + instructions đang đặt ở đâu
- Có bật quyền tự chạy lệnh không (Bước 6), nếu có thì lệnh nào
- Cần chạy lại lệnh nào khi code đổi nhiều (`codemap sync --project ...`)
- Bước nào đã bỏ qua và vì sao (không có FE, không có ticket...)
```

---

## Ghi chú cho người dùng

- Prompt này **cấm AI tự `git clone`** — bạn tự clone, rồi chỉ đường dẫn cho nó. Chủ ý: bạn kiểm soát
  source lấy từ đâu và nằm ở đâu, agent không tự tải code về máy bạn.
- Prompt này **không** bảo AI sửa PowerShell profile hay PATH hệ thống — cũng là chủ ý, vì nhiều máy công ty
  chặn. Nếu `dotnet tool install` cũng bị chặn thì Cách B (gọi thẳng dll) luôn chạy được.
- Quét **nhiều repo**: thêm nhiều entry vào mảng `projects` của cùng một `codemap.projects.json`, rồi `codemap sync --all`. CodeMap chỉ cài một lần.
- Index không tự cập nhật. Xem [README, Phần 4](../README.md) để biết khi nào cần quét lại.
- Bước 4 và Bước 7 cố tình bắt AI hỏi phạm vi (1 hay nhiều project) **đúng 1 lần** rồi dùng lại,
  thay vì hỏi riêng từng bước — làm vậy để tránh registry đặt ở `~/.codemap/` (nhiều project) mà
  instructions lại đặt theo repo (`.github/instructions/`), 2 nơi không khớp nhau. Nếu AI hỏi lại
  câu này lần 2, đó là dấu hiệu nó không đọc kỹ prompt — nhắc nó dùng lại câu trả lời cũ.
