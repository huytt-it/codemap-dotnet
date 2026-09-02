# setup/ — mọi thứ cần điền, gom một chỗ

Các file trong thư mục này là toàn bộ phần "cấu hình" của CodeMap. Clone repo về, làm đúng 4 việc dưới đây là dùng được.

| File | Làm gì với nó |
|---|---|
| `codemap.projects.example.json` | **Copy thành `codemap.projects.json` rồi điền** — khai báo repo nào cần quét, index để đâu |
| `codemap.permissions.json` | Sửa trực tiếp — lệnh `codemap` nào AI được **tự chạy**, lệnh nào phải hỏi bạn trước |
| `SETUP-PROMPT.md` | Copy khối prompt dán vào AI để nó cài đặt thay bạn (tuỳ chọn) |
| `copilot-instructions.md` | **Copy sang repo bạn quét**, đặt tại `.github/copilot-instructions.md` |

---

## Việc 1 — Khai báo project

```bash
cp setup/codemap.projects.example.json setup/codemap.projects.json
```

Mở `setup/codemap.projects.json`, **xoá hết 3 entry mẫu**, điền của bạn:

| Khoá | Bắt buộc? | Ý nghĩa |
|---|---|---|
| `name` | ✅ | Tên ngắn để gõ `--project <tên>`. Không phân biệt hoa thường, không được trùng. |
| `solution` | ✅ | File `.sln` hoặc `.slnx` cần quét |
| `output` | ✅ | Thư mục output. **Index nằm ở `<output>/index`**, `MAP.md` ở `<output>/MAP.md` |
| `description` | — | Mô tả codebase này là gì — AI đọc để hiểu ngữ cảnh |
| `repo` | — | Gốc git. Bỏ trống thì lấy thư mục chứa solution |
| `frontend` | — | Thư mục Angular/TypeScript. **Bỏ hẳn khoá này** nếu không có FE riêng |
| `commitLanguage` | — | Ngôn ngữ team viết commit (`ja`/`vi`/`en`...) — AI đọc để biết nên hỏi `where` bằng tiếng gì |

> Bản `codemap.projects.json` bạn điền **đã được `.gitignore`**, nên đường dẫn nội bộ công ty không vô tình bị commit lên GitHub. Muốn chia sẻ cấu hình cho đồng nghiệp thì sửa file `.example.json` thay vì file thật.

> **Đặt file ở đâu cũng được**, không nhất thiết trong `setup/`. Tool tìm theo thứ tự: `--config <đường dẫn>` → `codemap.projects.json` ở thư mục hiện tại rồi ngược lên các thư mục cha → `~/.codemap/codemap.projects.json`. Nếu bạn hay chạy lệnh từ trong repo đích, đặt ở `~/.codemap/` là tiện nhất vì chỗ nào cũng thấy.

Kiểm tra đã điền đúng chưa:

```bash
codemap projects --config setup/codemap.projects.json
```

Lệnh này in ra đường dẫn **sau khi resolve** và trạng thái index của từng project — sai đường dẫn là thấy ngay.

## Việc 2 — Quét

```bash
codemap sync --config setup/codemap.projects.json --all
```

Chạy trọn `scan` → `scan-git` → `scan-fe` → `link` → `map` cho mọi project đã khai báo. Quét một project thôi thì `--project <tên>` thay cho `--all`.

## Việc 3 — Quyết định AI được tự chạy lệnh nào

Mặc định, AI **không tự chạy `codemap`** — nó in lệnh ra, bạn dán vào terminal, rồi dán output ngược lại cho nó đọc. An toàn nhưng chậm.

Mở `setup/codemap.permissions.json`, đổi `"autoRun"` cho từng lệnh con:

```json
"where": { "autoRun": true, "reason": "chỉ đọc index" }
```

Mặc định sẵn: `find` / `where` / `impact` / `slice` / `projects` (chỉ đọc, không side effect ngoài ghi 1 file report nếu có `--out`) là `true`; `scan` / `scan-fe` / `scan-git` / `sync` / `map` / `link` (ghi lại index, có thể chậm) là `false`. Đổi tuỳ ý — không có gì bắt buộc phải giữ nguyên.

> AI tìm file này **cùng chỗ với `codemap.projects.json`** (root repo đích / thư mục cha / `~/.codemap/`) — không phải trong `.github/`. Đặt `codemap.projects.json` ở đâu (Việc 1) thì đặt `codemap.permissions.json` ở đúng đó, đừng để 2 nơi khác nhau.

**Đây là quy ước mềm** — AI tuân theo vì `copilot-instructions.md` bảo nó đọc và làm theo, giống mọi quy tắc khác trong file đó (cách đọc "Blind spots", cách viết symbol...). Không phải cơ chế chặn của hệ điều hành, và không phân biệt AI/công cụ nào đang đọc.

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

Copy `copilot-instructions.md` sang **repo bạn đang quét** (không phải repo CodeMap này):

```bash
cp setup/copilot-instructions.md <đường-dẫn-repo-đích>/.github/copilot-instructions.md
```

File này AI tự đọc mỗi session. Nó **không chứa đường dẫn cứng** — nó tự đọc `codemap.projects.json` để biết index nằm đâu, và `codemap.permissions.json` để biết lệnh nào tự chạy được. Ba chỗ nên xem lại sau khi copy:

- Mục **"Ngôn ngữ"** đang viết sẵn cho codebase có commit tiếng Nhật. `commitLanguage` của bạn khác thì sửa lại.
- Đảm bảo AI **tìm thấy** `codemap.projects.json` từ trong repo đích (theo thứ tự tìm kiếm ở Việc 1). Không thấy thì chuyển file config sang `~/.codemap/`.
- Nếu bạn không tạo `codemap.permissions.json`, AI mặc định coi mọi lệnh là phải hỏi trước — an toàn, không cần làm gì thêm.

---

## Lười? Để AI làm thay

Mở [SETUP-PROMPT.md](SETUP-PROMPT.md), copy khối prompt trong đó dán vào Copilot Chat (chế độ Agent) / Claude Code / Cursor. Nó tự dò SDK, chọn cách cài phù hợp với quyền hạn máy bạn, hỏi bạn các đường dẫn rồi làm cả 3 việc trên.

Prompt cố tình **cấm agent tự `git clone`** và **cấm sửa PowerShell profile / PATH hệ thống** — bạn giữ quyền kiểm soát source lấy từ đâu, và không vướng chính sách máy công ty.
