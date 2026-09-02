# setup/ — mọi thứ cần điền, gom một chỗ

Các file trong thư mục này là toàn bộ phần "cấu hình" của CodeMap. Clone repo về, làm đúng 4 việc dưới đây là dùng được.

| File | Làm gì với nó |
|---|---|
| `codemap.projects.example.json` | **Copy thành `codemap.projects.json` rồi điền** — khai báo repo nào cần quét, index để đâu |
| `codemap.permissions.json` | Sửa trực tiếp — lệnh `codemap` nào AI được **tự chạy**, lệnh nào phải hỏi bạn trước |
| `SETUP-PROMPT.md` | Copy khối prompt dán vào AI để nó cài đặt thay bạn (tuỳ chọn) |
| `codemap.instructions.md` | Copy vào 1 trong 2 chỗ — xem "Việc 4" bên dưới |

---

## Việc 1 — Khai báo project

**Quyết định trước: 1 project hay nhiều project?** Việc này quyết định nên đặt file ở đâu, và ảnh hưởng luôn tới Việc 4 (chọn cùng 1 phạm vi cho cả registry lẫn instructions) — chọn 1 lần, đừng đổi qua lại.

### Nhiều project, thêm/bớt theo thời gian (khuyến nghị nếu bạn quét nhiều repo)

Tạo **thẳng** tại `~/.codemap/codemap.projects.json` — đừng copy ra `setup/` trước rồi tính chuyển sau, dễ quên:

```bash
mkdir -p ~/.codemap
cp setup/codemap.projects.example.json ~/.codemap/codemap.projects.json
```

(Windows: đường dẫn thật là `C:\Users\<tên bạn>\.codemap\codemap.projects.json`.) Đây là nơi tool luôn tìm tới cuối cùng nếu không thấy bản nào gần hơn — để sót 1 bản khác ở `setup/` trong chính repo CodeMap, hay ở gốc từng repo đích, thì bản gần thư mục đang đứng hơn **luôn được ưu tiên và che khuất bản ở `~/.codemap/`**, 2 bản dễ lệch dần mà không ai để ý. Vì nằm ngoài mọi repo, **mọi đường dẫn trong file phải là tuyệt đối**, không dùng đường dẫn tương đối.

### Chỉ 1 project

```bash
cp setup/codemap.projects.example.json setup/codemap.projects.json
```

Không có rủi ro shadow vì chỉ có 1 bản duy nhất — đặt trong `setup/` hoặc gốc repo đích đều được.

### Điền nội dung (áp dụng cho cả 2 cách)

Mở file vừa tạo, **xoá hết 3 entry mẫu**, điền của bạn:

| Khoá | Bắt buộc? | Ý nghĩa |
|---|---|---|
| `name` | ✅ | Tên ngắn để gõ `--project <tên>`. Không phân biệt hoa thường, không được trùng. |
| `solution` | ✅ | File `.sln` hoặc `.slnx` cần quét |
| `output` | ✅ | Thư mục output. **Index nằm ở `<output>/index`**, `MAP.md` ở `<output>/MAP.md` |
| `description` | — | Mô tả codebase này là gì — AI đọc để hiểu ngữ cảnh |
| `repo` | — | Gốc git. Bỏ trống thì lấy thư mục chứa solution |
| `frontend` | — | Thư mục Angular/TypeScript. **Bỏ hẳn khoá này** nếu không có FE riêng |
| `commitLanguage` | — | Ngôn ngữ team viết commit (`ja`/`vi`/`en`...) — AI đọc để biết nên hỏi `where` bằng tiếng gì |

> File `codemap.projects.json` bạn điền **đã được `.gitignore`** (dù đặt ở `setup/` hay tự tạo ở `~/.codemap/` thì cũng không nằm trong repo Git để commit), nên đường dẫn nội bộ công ty không vô tình lộ ra. Muốn chia sẻ cấu hình cho đồng nghiệp thì sửa file `.example.json` thay vì file thật.

Tool tìm theo thứ tự: `--config <đường dẫn>` → `codemap.projects.json` ở thư mục hiện tại rồi ngược lên các thư mục cha → `~/.codemap/codemap.projects.json`.

Sau khi điền xong, chạy `codemap projects` **từ vài thư mục khác nhau** (gốc repo CodeMap, gốc 1 repo đích...) — dòng `Registry: ...` đầu output phải luôn trỏ về đúng 1 file bạn vừa tạo. Nếu có lúc trỏ vào chỗ khác, tức là còn sót bản `codemap.projects.json` cũ ở đâu đó gần cwd hơn — tìm và xoá.

Nếu đặt ở `setup/`, kiểm tra bằng:

```bash
codemap projects --config setup/codemap.projects.json
```

Lệnh này in ra đường dẫn **sau khi resolve** và trạng thái index của từng project — sai đường dẫn là thấy ngay. Nếu đặt ở `~/.codemap/`, bỏ `--config` — tool tự tìm thấy.

## Việc 2 — Quét

```bash
codemap sync --all
```

(Thêm `--config setup/codemap.projects.json` nếu bạn chọn đặt trong `setup/`.) Chạy trọn `scan` → `scan-git` → `scan-fe` → `link` → `map` cho mọi project đã khai báo. Quét một project thôi thì `--project <tên>` thay cho `--all`.

## Việc 3 — Quyết định AI được tự chạy lệnh nào

Mặc định, AI **không tự chạy `codemap`** — nó in lệnh ra, bạn dán vào terminal, rồi dán output ngược lại cho nó đọc. An toàn nhưng chậm.

Mở `setup/codemap.permissions.json`, đổi `"autoRun"` cho từng lệnh con:

```json
"where": { "autoRun": true, "reason": "chỉ đọc index" }
```

Mặc định sẵn: `find` / `where` / `impact` / `slice` / `projects` (chỉ đọc, không side effect ngoài ghi 1 file report nếu có `--out`) là `true`; `scan` / `scan-fe` / `scan-git` / `sync` / `map` / `link` (ghi lại index, có thể chậm) là `false`. Đổi tuỳ ý — không có gì bắt buộc phải giữ nguyên.

> AI tìm file này **cùng chỗ với `codemap.projects.json`** (root repo đích / thư mục cha / `~/.codemap/`) — không phải trong `.github/`. Đặt `codemap.projects.json` ở đâu (Việc 1) thì đặt `codemap.permissions.json` ở đúng đó, đừng để 2 nơi khác nhau.

**Đây là quy ước mềm** — AI tuân theo vì `codemap.instructions.md` bảo nó đọc và làm theo, giống mọi quy tắc khác trong file đó (cách đọc "Blind spots", cách viết symbol...). Không phải cơ chế chặn của hệ điều hành, và không phân biệt AI/công cụ nào đang đọc.

### Chặn cứng (tuỳ chọn, riêng cho Claude Code)

Muốn chặn thật — agent không thể lách kể cả khi "quên" đọc file — thêm vào `.claude/settings.json` (hoặc `.claude/settings.local.json`) của repo đích:

```json
{
  "permissions": {
    "allow": [
      "Bash(codemap find:*)",
      "Bash(codemap where:*)",
      "Bash(codemap impact:*)",
      "Bash(codemap slice:*)",
      "Bash(codemap projects:*)"
    ]
  }
}
```

Đây là cơ chế permission gốc của Claude Code — chạy đúng những lệnh được liệt kê mà không hỏi lại, mọi lệnh khác vẫn phải xác nhận. Copilot Chat / Cursor có cơ chế tương tự riêng (thường gọi là "auto-approve" cho terminal), tên setting khác nhau tuỳ phiên bản — xem tài liệu của công cụ đó nếu muốn chặn cứng thay vì chỉ dựa vào `codemap.permissions.json`.

## Việc 4 — Cho AI đọc được

`codemap.instructions.md` dùng đúng đuôi `.instructions.md` — quy ước "path-specific instructions" của VS Code (GitHub Copilot Chat, và cả Claude Code/Cursor đọc kiểu tương tự), **khác** với `copilot-instructions.md` là tên file cố định dành riêng cho 1 file duy nhất ở gốc mỗi repo. Nhờ vậy tên không đụng hướng dẫn khác team đã có, và có thể đặt ở 1 trong 2 chỗ tuỳ nhu cầu:

### Cách A — 1 lần cho tất cả project (khuyến nghị nếu bạn quét nhiều repo, thêm/bớt liên tục)

```bash
mkdir -p ~/.copilot/instructions
cp setup/codemap.instructions.md ~/.copilot/instructions/codemap.instructions.md
```

Trên Windows, `~` là `C:\Users\<tên bạn>\` — đường dẫn thật là
`C:\Users\<tên bạn>\.copilot\instructions\codemap.instructions.md`.

Đây là vị trí **user-level, VS Code bật sẵn theo mặc định** (setting `chat.instructionsFilesLocations`) — verify trực tiếp từ mã nguồn VS Code, không phải suy đoán. File ở đây tự áp dụng cho **mọi workspace bạn mở trên máy này**, không quan tâm bạn đang mở project nào làm gốc, không cần copy lại khi thêm project mới vào `codemap.projects.json`. Làm **đúng 1 lần**, xong không phải nghĩ tới nữa dù sau này thêm bao nhiêu project.

Đánh đổi: chỉ có tác dụng trên máy bạn, đồng nghiệp clone repo về không tự có.

### Cách B — theo từng repo, chia sẻ được qua git

```bash
mkdir -p <đường-dẫn-repo-đích>/.github/instructions
cp setup/codemap.instructions.md <đường-dẫn-repo-đích>/.github/instructions/codemap.instructions.md
```

Commit file này vào repo đích — đồng nghiệp clone về là có sẵn, không cần setup riêng. Vẫn tự động, không cần chỉnh setting gì, **nhưng chỉ có tác dụng khi workspace root đúng là repo đó** (hoặc repo đó là 1 folder gốc trong multi-root workspace) — mở nhầm thư mục cha chứa nhiều repo thì AI sẽ không thấy file này. Nếu chọn cách B mà có nhiều repo, lặp lại bước này cho từng repo.

### Sau khi copy (áp dụng cho cả 2 cách)

File **không chứa đường dẫn cứng** — nó tự đọc `codemap.projects.json` để biết index nằm đâu, và `codemap.permissions.json` để biết lệnh nào tự chạy được. Ba chỗ nên xem lại:

- Mục **"Ngôn ngữ"** đang viết sẵn cho codebase có commit tiếng Nhật. `commitLanguage` của bạn khác thì sửa lại.
- Đảm bảo AI **tìm thấy** `codemap.projects.json` (theo thứ tự tìm kiếm ở Việc 1) — nếu bạn chọn Cách A (nhiều project), đặt `codemap.projects.json` ở `~/.codemap/` là hợp lý nhất, vì đó cũng là nơi mọi workspace đều thấy được, giống tinh thần của Cách A.
- Nếu bạn không tạo `codemap.permissions.json`, AI mặc định coi mọi lệnh là phải hỏi trước — an toàn, không cần làm gì thêm.

> **Vì sao không dùng `copilot-instructions.md`**: đó là tên file GitHub Copilot dành riêng cho đúng 1 file ở gốc workspace — nếu bạn (hoặc team) đã có hướng dẫn khác dùng tên đó, CodeMap sẽ ghi đè mất nội dung cũ. Đặt tên `codemap.instructions.md` để 2 file cùng tồn tại song song, mỗi file lo một việc.

> **Chỉ chọn 1 trong 2 cách, đừng làm cả hai** cho cùng một repo. Khác với `codemap.projects.json` (bản gần hơn che khuất bản xa hơn), các file `.instructions.md` **cộng dồn** — nếu vừa có bản ở `~/.copilot/instructions/` vừa có bản ở `.github/instructions/` của cùng repo đó, AI nhận cả hai cùng lúc, nội dung trùng lặp bị gửi 2 lần cho model, tốn hơn chứ không sai.

---

## Lười? Để AI làm thay

Mở [SETUP-PROMPT.md](SETUP-PROMPT.md), copy khối prompt trong đó dán vào Copilot Chat (chế độ Agent) / Claude Code / Cursor. Nó tự dò SDK, chọn cách cài phù hợp với quyền hạn máy bạn, hỏi bạn các đường dẫn rồi làm cả 3 việc trên.

Prompt cố tình **cấm agent tự `git clone`** và **cấm sửa PowerShell profile / PATH hệ thống** — bạn giữ quyền kiểm soát source lấy từ đâu, và không vướng chính sách máy công ty.
