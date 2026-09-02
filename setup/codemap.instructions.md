---
applyTo: "**"
description: "CodeMap — cách dùng chỉ số codebase tĩnh (offline) khi trả lời câu hỏi quan hệ code"
---

# Hướng dẫn cho AI agent — CodeMap

<!--
  Đặt file này ở MỘT trong hai chỗ (chọn 1, xem setup/README.md mục "Cho AI đọc được"):

  A. ~/.copilot/instructions/codemap.instructions.md
     User-level, bật sẵn mặc định trong VS Code (chat.instructionsFilesLocations). Tự áp dụng
     cho MỌI workspace bạn mở trên máy này — không cần copy vào từng repo, không phụ thuộc
     project nào đang mở làm workspace root. Đúng lựa chọn nếu bạn quét nhiều project và
     thêm/bớt project theo thời gian. Nhược điểm: chỉ có tác dụng trên máy này, không chia sẻ
     qua git cho đồng nghiệp.

  B. <repo đích>/.github/instructions/codemap.instructions.md
     Theo repo, commit vào git — đồng nghiệp clone về là có luôn, không cần setup riêng. Cũng
     tự động (không cần setting gì thêm), nhưng chỉ áp dụng khi workspace root là chính repo đó
     (hoặc repo đó là 1 trong các folder gốc của multi-root workspace).

  Tên file cố ý không phải "copilot-instructions.md" — tên đó dành riêng cho 1 file duy nhất ở
  gốc workspace theo quy ước GitHub Copilot, dễ đụng với hướng dẫn khác của team. Đuôi
  ".instructions.md" là quy ước path-specific instructions của VS Code, nhiều file cùng tồn tại
  song song, không giới hạn tên.
-->

## ⚙️ Đường dẫn index — đọc từ `codemap.projects.json`, đừng đoán

**Việc đầu tiên mỗi session, trước khi in bất kỳ lệnh nào:** mở file `codemap.projects.json`
(tìm ở gốc repo, các thư mục cha, hoặc `~/.codemap/`). File đó khai báo mọi codebase đã index:

**Tìm file này bằng terminal thật (`cat`, `ls`, hoặc chạy thẳng `codemap projects`), KHÔNG dùng
công cụ tìm kiếm file có sẵn của bạn (semantic search / file search nội bộ).** Công cụ đó chỉ
thấy file bên trong workspace đang mở — `~/.codemap/` và `~/.copilot/instructions/` nằm ở thư
mục người dùng, ngoài mọi workspace, nên tìm bằng công cụ đó luôn ra "no matches" **dù file có
tồn tại thật**. Đây là nguyên nhân thật đã xảy ra: agent search file nội bộ, nhận "no matches",
rồi bỏ cuộc quay về grep — trong khi `codemap projects` chạy qua terminal vẫn tìm thấy đúng file
đó bình thường, bất kể cwd. "No matches" từ công cụ search nội bộ **không phải bằng chứng file
không tồn tại** — chỉ có `codemap projects` (hoặc `ls ~/.codemap/`) qua terminal mới là bằng
chứng thật.

```json
{
  "projects": [
    {
      "name": "shop",
      "description": "Backend đơn hàng — Razor Pages + PublicApi",
      "solution": "D:/Repos/Shop/Shop.sln",
      "output": "D:/CodeMapIndex/Shop",
      "commitLanguage": "ja"
    }
  ]
}
```

Cách dùng những gì đọc được:

- **`output`** là thư mục output. Index thật nằm ở **`<output>/index`** (đây mới là giá trị
  cho `--index`), bản đồ tổng quan ở **`<output>/MAP.md`**.
- **`name`** dùng để rút gọn lệnh: `--project shop` thay cho `--index <đường dẫn dài>`. Ưu
  tiên cách này khi in lệnh cho tôi — ngắn hơn và không sợ gõ sai đường dẫn.
- **`description`** cho bạn biết codebase đó là gì. Repo nào cũng có thể có nhiều entry.
- **`commitLanguage`** là ngôn ngữ team viết commit/ticket. **Quyết định trực tiếp** ngôn ngữ
  bạn nên dùng cho `where` — xem mục "Ngôn ngữ" bên dưới.

> **File này đang áp dụng cho nhiều project khác nhau (đặt ở `~/.copilot/instructions/`
> — xem đầu file).** Không có nghĩa mọi project đều dùng CodeMap. Nếu workspace hiện tại
> không khớp `name`/`solution` nào trong `codemap.projects.json`, coi như CodeMap chưa setup
> cho project này — đừng cố ép dùng, và đừng lấy nhầm số liệu của project khác.

> **Nếu không tìm thấy `codemap.projects.json`:** hỏi tôi index nằm đâu, rồi dùng
> `--index <đường dẫn>`. **Không tự bịa đường dẫn, không giả định `.ai/` hay `.codemap/`** —
> mỗi máy đặt một kiểu, có nơi để index ngoài repo hẳn.
>
> **Nếu file có nhưng project tôi hỏi chưa được index** (`status: NOT BUILT` khi tôi chạy
> `codemap projects`): nói tôi chạy `codemap sync --project <tên>` trước, đừng cố truy vấn.

## Bối cảnh

Repo này có một index tĩnh do tool `codemap` sinh ra, nằm ở `<output>/index`. Index được quét lại
một lần mỗi ngày. Nó trả lời câu hỏi "code nằm đâu và nối với cái gì" nhanh và đầy đủ hơn
grep rất nhiều.

Codebase là .NET (8/9/10 đều được). **Tên class, method, route luôn là tiếng Anh; còn comment
và message commit/ticket thì theo `commitLanguage` của project trong `codemap.projects.json`
(ví dụ dưới đây viết cho `"ja"` — tiếng Nhật).** Sự lệch ngôn ngữ này quyết định lệnh nào dùng
được cho câu hỏi nào — xem mục "Ngôn ngữ" bên dưới, đọc trước khi gõ lệnh đầu tiên.

## 🔑 Bạn CÓ THỂ tự chạy một số lệnh `codemap` — đọc `codemap.permissions.json`

Cùng chỗ với `codemap.projects.json` (tìm ở gốc repo, các thư mục cha, hoặc `~/.codemap/`) có
thể có file `codemap.permissions.json`, khai báo lệnh con nào bạn được tự chạy
(`"autoRun": true`) và lệnh nào phải in ra rồi dừng cho tôi chạy tay (`"autoRun": false`).

- **Có file, lệnh đang cần là `autoRun: true`** → tự chạy qua terminal của bạn, đọc thẳng output,
  rồi làm tiếp bước "đọc source thật" bên dưới. Không cần hỏi tôi trước mỗi lần.
- **Có file, lệnh đang cần là `autoRun: false`, hoặc không có trong danh sách** → theo quy trình
  cũ: in đúng một lệnh, dừng lại, đợi tôi chạy.
- **Không tìm thấy file này** → coi như mọi lệnh đều `autoRun: false` (an toàn hơn) — theo quy
  trình cũ.

Tự chạy được không có nghĩa là bỏ qua các quy tắc phân tích còn lại trong file này — vẫn phải
đọc mục "Blind spots", vẫn phải phân biệt cạnh chắc chắn với cạnh suy diễn, vẫn phải đọc source
thật trước khi trả lời "tại sao". `autoRun` chỉ đổi **ai gõ lệnh**, không đổi cách bạn phải đọc
kết quả.

**Không tự nới `autoRun` lên `true` cho lệnh đang ghi `false`.** Đó là lựa chọn của tôi, không
phải của bạn — kể cả khi bạn thấy tiện hơn.

**Quan trọng nhất trong toàn bộ file này:** khi câu hỏi cần dữ liệu quan hệ (impact, caller, entry
point...), **luôn mở terminal và chạy lệnh `codemap` thật** — dù `autoRun` cho phép bạn tự chạy
hay phải in lệnh chờ tôi. **Không được** tự ý dùng công cụ tìm kiếm/đọc file có sẵn của bạn để
thay thế, kể cả khi việc đó có vẻ nhanh hơn hoặc không cần xin phép. Danh sách "Cấm" ở cuối file
nói rõ vì sao (grep mù ra kết quả sai lệch, chậm hơn, và bỏ lỡ cạnh suy diễn `codemap` đã tính
sẵn).

## Quy trình làm việc

1. Tôi hỏi một câu về codebase.
2. Nếu câu hỏi cần dữ liệu quan hệ (ai gọi ai, sửa chỗ này ảnh hưởng đâu, chức năng này nằm
   đâu) → xem lệnh cần dùng có `autoRun: true` không (mục permission ở trên).
   - Có → **mở terminal, tự chạy lệnh `codemap` thật**, đọc output.
   - Không → in ra **đúng một lệnh** cho tôi chạy, rồi **dừng lại**.
   Cả hai trường hợp: không đoán trước kết quả, không tự search/grep thay thế.
3. Lệnh `autoRun: false` thì tôi chạy tay, dán output hoặc để bạn đọc terminal.
4. Bạn đọc kết quả, **rồi mới đọc source thật** ở những file report chỉ ra, và trả lời.

Report cho biết **ở đâu**. Source code cho biết **tại sao**. Đừng dừng ở report khi câu hỏi
là "tại sao" hoặc "sửa thế nào".

## Khi bạn cần thêm dữ liệu

**Lệnh `autoRun: true`:** mở terminal, chạy lệnh `codemap` thật ngay, không cần xin phép, rồi
tiếp tục trả lời. Vẫn nói rõ bạn vừa chạy lệnh gì (tôi cần thấy, không phải để xin phép).

**Lệnh `autoRun: false`:** in đúng một lệnh trong code block, kèm một dòng nói lệnh đó trả lời
được gì, rồi dừng. Ví dụ:

> Tôi cần biết `OrderService.Cancel` được gọi từ đâu. Chạy giúp:
> ```
> codemap impact --project <tên> --symbol "M:Orders.OrderService.Cancel(System.Int32)"
> ```

Không in nhiều lệnh cùng lúc. Không viết "sau đó chạy tiếp...". Mỗi lượt một lệnh.

## Catalog lệnh

| Cần gì | Lệnh |
|---|---|
| Tìm symbol theo tên (đã biết tên tiếng Anh) | `codemap find --project <tên> --query "OrderService.Cancel"` |
| Tìm symbol theo mô tả nghiệp vụ tiếng Nhật | `codemap where --project <tên> --query "注文のキャンセル"` |
| Ảnh hưởng, bản gọn | `codemap impact --project <tên> --symbol "<docId>"` |
| Ảnh hưởng, đủ caller trung gian | `codemap impact --project <tên> --symbol "<docId>" --full` |
| Ảnh hưởng + code thật + lịch sử ticket | `codemap slice --project <tên> --symbol "<docId>" --out <thư mục report>/current.md` |
| Bản đồ tổng quan | đọc `<output>/MAP.md` (đã có sẵn, không cần chạy lệnh) |

Output dài (`slice`, `impact --full` trên symbol lớn) thì luôn dùng `--out` rồi tôi sẽ
`#file` cho bạn, vì terminal bị cắt bớt.

Nếu chưa biết docId, đi qua `where` hoặc `find` trước. **Không bao giờ tự bịa docId.**

## Ngôn ngữ — đọc kỹ, phần này quyết định chọn lệnh

### `where` xếp hạng theo 3 nguồn, chỉ 1 nguồn hiểu tiếng Nhật

| Nguồn | Trọng số | Ngôn ngữ thực tế trong repo |
|---|---|---|
| Message ticket/commit cũ | cao nhất | **tiếng Nhật** |
| Route API + tên feature FE | trung bình | tiếng Anh |
| Tên type/method | thấp nhất | tiếng Anh |

Hệ quả trực tiếp:

- Hỏi bằng **tiếng Nhật** → chạm được nguồn mạnh nhất (lịch sử ticket). Đây là cách đúng khi
  bạn chỉ có mô tả nghiệp vụ.
- Hỏi bằng **tiếng Anh** → chỉ chạm nguồn 2 và 3, nhưng đó lại là 2 nguồn khớp trực tiếp với
  định danh trong code. Đây là cách đúng khi bạn đã đoán được vùng code.
- Hai cách bổ sung nhau chứ không thay thế nhau. Nếu lượt đầu không ra kết quả tin được, hãy
  đề nghị tôi chạy lượt thứ hai bằng ngôn ngữ còn lại — đừng kết luận "không tìm thấy" sau
  một lần thử.

### Cách khớp tiếng Nhật: bigram ký tự, không phải hiểu nghĩa

Tiếng Nhật không có dấu cách giữa các từ, nên tool cắt câu thành **từng cặp 2 ký tự chồng
nhau** rồi đếm cặp trùng (`注文をキャンセル` → `注文`, `文を`, `をキ`, `キャ`, `ャン`, `ンセ`,
`セル`). Không có từ điển, không có phân tích hình thái, không có từ đồng nghĩa. Hệ quả:

- **Đổi trợ từ, đảo trật tự, thêm bớt từ → vẫn khớp.** `注文のキャンセル処理` vẫn khớp ticket
  viết `注文をキャンセルできない不具合`.
- **Viết khác chữ → không khớp gì cả.** `キャンセル` / `取消` / `解約` cùng nghĩa "hủy" nhưng
  không chung ký tự nào → 0 điểm. Đây là giới hạn thật của cơ chế, không phải bug. Cũng vậy
  với `注文` ↔ `オーダー`, `在庫` ↔ `ストック`.
  → Khi không ra kết quả, thử lại với **cách viết khác** (kanji ↔ katakana ↔ từ mượn tiếng
  Anh) trước khi kết luận là code không tồn tại.
- **Trợ từ và đuôi chia động từ đã được loại.** Cặp gồm 2 ký tự hiragana (`ない`, `でき`, `から`)
  bị bỏ, vì tiếng Nhật viết từ mang nghĩa bằng kanji/katakana còn ngữ pháp bằng hiragana — nếu
  không bỏ thì `削除できない` và `合わない` khớp nhau chỉ vì chung `ない`. Ngoại lệ: cụm viết
  **toàn hiragana** (`ひもづけ`) thì giữ nguyên, vì không còn gì khác để khớp.
- **Vẫn không có ngưỡng điểm tối thiểu.** `where` luôn trả về top N kể cả khi điểm rất thấp,
  nên điểm thấp không có nghĩa là "gần đúng" — nó thường có nghĩa là "không liên quan".

### Bắt buộc: đọc dòng lý do, đừng chỉ nhìn thứ hạng

Mỗi kết quả `where` in kèm dòng `- Past ticket #... shares term(s) [...]`. Danh sách trong
ngoặc vuông là những gì thực sự trùng. Phân biệt:

- Trùng từ/cặp ký tự **mang nội dung nghiệp vụ** → kết quả đáng tin.
- Chỉ trùng trợ từ, từ đệm, cặp ký tự vụn → **nhiễu**, dù nó đang đứng đầu danh sách.
- Nhiều kết quả **đồng điểm nhau** → thứ hạng không có ý nghĩa, đừng chọn kết quả đầu rồi
  khẳng định. Nói thẳng là chưa đủ tin cậy và đề nghị chạy lại với câu khác.

### Index không lưu comment và string literal

Index chỉ có: symbol, quan hệ gọi, entry point, route, link FE↔BE, lịch sử git. **Không có
nội dung comment, không có string literal.**

Nên với codebase này, chia việc rõ ràng:

- "Chỗ nào có comment nói về X", "tìm message tiếng Nhật này trong code", "hằng số này khai
  báo ở đâu theo giá trị chuỗi" → **grep là đúng**, `codemap` không trả lời được.
- "Ai gọi cái này", "sửa chỗ này ảnh hưởng đâu", "chức năng này nằm ở đâu" → **`codemap` là
  đúng**, grep sẽ ngập kết quả vô dụng.

## Cách đọc kết quả — phần quan trọng nhất

### "0 entry point" không có nghĩa là không ảnh hưởng

Tool chỉ nhận diện được ba loại entry point: MVC Controller (`ControllerBase`/`Controller`),
BackgroundService/IHostedService, MediatR handler. **Razor Pages và Minimal API
(`app.MapGet`/`MapPost`) không được nhận diện.**

Nếu report ghi 0 entry point nhưng fan-in lớn hơn 0, kết luận đúng là *"tool không phân loại
được"*, không phải *"không có gì bị ảnh hưởng"*. Lúc đó đề nghị tôi chạy lại với `--full` để
xem caller trung gian.

### Cạnh suy diễn có thể thừa

- `via:"interface"` — tool nhân bản lời gọi sang **mọi** class implement interface đó. Runtime
  thật thường chỉ đi một nhánh. Đây là suy diễn, có thể phóng đại phạm vi ảnh hưởng.
- `via:"mediatr"` — nối theo quy ước MediatR, không phải quan hệ gọi trực tiếp.

Report tách sẵn hai nhóm: entry point dưới heading chính là loại **có đăng ký DI thật**;
những cái nằm dưới **"Other possible implementations"** chỉ là class cùng implement interface,
không xác nhận được là nhánh chạy thật. Khi trả lời, giữ nguyên sự phân biệt đó — đừng gộp
hai nhóm lại thành một danh sách "bị ảnh hưởng".

### Mục "Blind spots" là bắt buộc đọc

Mọi report đều có mục này. Nếu câu hỏi của tôi rơi vào vùng mù, nói thẳng là không kết luận
được từ report, đừng lấp bằng suy đoán.

Tool **không bao giờ** nhìn thấy: reflection, stored procedure và SQL, logic runtime và
feature flag, config theo môi trường, dữ liệu thật trong DB, quan hệ Razor View ↔ ViewModel.

### Banner staleness ở đầu mỗi file

Mỗi report ghi index quét lúc nào và lệch bao nhiêu commit so với HEAD. Nếu lệch nhiều và có
file liên quan đã đổi sau khi quét, nói cho tôi biết trước khi kết luận.

### Dữ liệu git là lịch sử, không phải cấu trúc

Mục "ticket cũ" và "file hay sửa kèm" phản ánh quá khứ. File đã xóa hoặc đổi tên vẫn xuất
hiện. Dùng nó làm gợi ý điều tra, không dùng làm bằng chứng về cấu trúc hiện tại.

Nếu report **không có ticket nào** hoặc `where` báo không khớp được message nào, khả năng cao
là quy ước commit của repo không khớp mẫu mặc định (`#123`, `TICKET-123`, `BUG-123`,
`JIRA-123`). Báo cho tôi biết để tôi đặt `ticketPattern` trong `codemap.config.json` — đừng im
lặng coi như repo không có lịch sử.

## Cấm

- **Không tự ý dùng công cụ search/read-file có sẵn của bạn thay cho lệnh `codemap`** khi câu
  hỏi cần dữ liệu quan hệ, kể cả khi bạn đã đọc `codemap.projects.json` và biết index tồn tại.
  Biết index tồn tại mà không dùng nó thì cũng vô nghĩa như không có index.
- Không grep mù **cho câu hỏi quan hệ**. Grep `Save` trong monolith trả về hàng nghìn kết quả
  vô dụng; `impact` trả lời trực tiếp. (Grep vẫn đúng cho comment và string literal — xem mục
  "Index không lưu comment".)
- Không viết tên symbol rút gọn. Luôn viết `Orders.OrderService.Cancel(int)`, không viết
  `Cancel`. Repo có nhiều method trùng tên.
- Không khẳng định chắc chắn dựa trên cạnh suy diễn.
- Không kết luận "không có code cho việc này" chỉ vì `where` không ra kết quả. Thử cách viết
  khác, hoặc đổi sang tiếng Anh, hoặc dùng `find` trước đã.
- Không tự sinh code sửa lỗi khi tôi chỉ hỏi thông tin. Nếu tôi cần code, tôi sẽ nói rõ.

---

# Prompt template

## 1. Điều tra bug từ mô tả nghiệp vụ

```
Ticket #4821: 支払い済みの注文をキャンセルするとエラーになる。

Tìm giúp tôi chỗ nào trong code xử lý việc này. Dùng codemap theo đúng quyền autoRun đã
cấu hình. Sau khi định vị được, đọc source thật rồi giải thích luồng xử lý.
Nếu lượt tìm đầu không ra kết quả tin được, đề xuất cách viết lại câu truy vấn.
```

## 2. Đánh giá rủi ro trước khi sửa

```
Tôi định đổi signature của <symbol>. Trước khi làm, cho tôi biết:
- endpoint và màn hình nào bị ảnh hưởng
- phần nào là chắc chắn, phần nào là tool suy diễn
- vùng mù nào khiến đánh giá này chưa đầy đủ

Dùng codemap theo đúng quyền autoRun đã cấu hình, đừng đoán.
```

## 3. Review hệ quả của một thay đổi đã làm

```
Tôi vừa sửa các file sau: <danh sách>.
Với mỗi file, dùng codemap kiểm tra xem thay đổi lan tới đâu, và có file nào
theo lịch sử thường phải sửa kèm mà tôi đang bỏ sót không.
```

## 4. Khi nghi ngờ chính report

```
Report này nói <X>. Đọc source thật ở các file nó chỉ ra và xác nhận giúp tôi
điều đó có đúng không. Nếu source mâu thuẫn với report, tin source.
```
