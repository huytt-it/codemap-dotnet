# setup/ — mọi thứ cần điền, gom một chỗ

Ba file trong thư mục này là toàn bộ phần "cấu hình" của CodeMap. Clone repo về, làm đúng 3 việc dưới đây là dùng được.

| File | Làm gì với nó |
|---|---|
| `codemap.projects.example.json` | **Copy thành `codemap.projects.json` rồi điền** — khai báo repo nào cần quét, index để đâu |
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

## Việc 3 — Cho AI đọc được

Copy `copilot-instructions.md` sang **repo bạn đang quét** (không phải repo CodeMap này):

```bash
cp setup/copilot-instructions.md <đường-dẫn-repo-đích>/.github/copilot-instructions.md
```

File này AI tự đọc mỗi session. Nó **không chứa đường dẫn cứng** — nó tự đọc `codemap.projects.json` để biết index nằm đâu. Hai chỗ nên xem lại sau khi copy:

- Mục **"Ngôn ngữ"** đang viết sẵn cho codebase có commit tiếng Nhật. `commitLanguage` của bạn khác thì sửa lại.
- Đảm bảo AI **tìm thấy** `codemap.projects.json` từ trong repo đích (theo thứ tự tìm kiếm ở Việc 1). Không thấy thì chuyển file config sang `~/.codemap/`.

---

## Lười? Để AI làm thay

Mở [SETUP-PROMPT.md](SETUP-PROMPT.md), copy khối prompt trong đó dán vào Copilot Chat (chế độ Agent) / Claude Code / Cursor. Nó tự dò SDK, chọn cách cài phù hợp với quyền hạn máy bạn, hỏi bạn các đường dẫn rồi làm cả 3 việc trên.

Prompt cố tình **cấm agent tự `git clone`** và **cấm sửa PowerShell profile / PATH hệ thống** — bạn giữ quyền kiểm soát source lấy từ đâu, và không vướng chính sách máy công ty.
