# MCPVault

Obsidian の Vault を Claude などの AI エージェントから操作するスタンドアロン MCP サーバー（Windows / Linux 対応、C# / .NET 8）。

Obsidian アプリや REST API に依存しない。2.0 では SQLite を正とし、ノートの全文・version・書き込み履歴を SQLite に保持して md へ書き戻す。全文検索は SQLite FTS5 trigram で日本語・英語に対応する。
複数 Vault（メイン・ゲスト）と KnowledgeCell（AI 用の KV ストア）を、同じ 14 ツールの `target` 引数で扱う。

- プロトコル: JSON-RPC 2.0 over stdio（設定で有効にすると 127.0.0.1 の HTTP でも同じツールを受ける）
- MCP プロトコルバージョン: 2024-11-05
- 対応メソッド: `initialize` / `tools/list` / `tools/call` / `resources/list`（main の scope 内ノートを `obsidian://notes/{path}` で列挙）
- サーバー名: `mcpvault` / バージョン: `2.0.0`
- ターゲットフレームワーク: .NET 8.0

---

## セットアップ

### Windows

```
publish.bat
```

`publish\MCPVault.exe` が生成される。任意の場所に置いて MCP クライアントから `--vault` 付きで起動する。

### Linux

```
dotnet publish MCPVault/MCPVault.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish-linux
```

ARM の場合は `linux-arm64`。以下の設定例の `command` / `--vault` は Linux のパスに読み替える。

- Linux ではファイル名の大文字小文字を区別する（`Note.md` と `note.md` は別ノート）
- ノート数・フォルダ数が多い Vault では inotify の監視上限に達し、変更の自動反映が止まることがある。その場合は `fs.inotify.max_user_watches` を引き上げる

### クライアント別設定

#### Claude Desktop

`%APPDATA%\Claude\claude_desktop_config.json` の `mcpServers` に追加する。

```json
{
  "mcpServers": {
    "mcpvault": {
      "command": "C:\\path\\to\\MCPVault.exe",
      "args": ["--vault", "C:\\MCPVault"]
    }
  }
}
```

#### Claude Code（CLI）

プロジェクトルートの `.mcp.json` に記述する。ユーザー共通設定は `%USERPROFILE%\.claude\mcp.json` でも可。

```json
{
  "mcpServers": {
    "mcpvault": {
      "command": "C:\\path\\to\\MCPVault.exe",
      "args": ["--vault", "C:\\MCPVault"]
    }
  }
}
```

#### VS Code / Visual Studio 2022（GitHub Copilot Agent モード）

VS Code は `.vscode/mcp.json`、VS2022 はソリューションと同じフォルダの `.mcp.json` に、トップレベルキー `servers` で記述する。

```json
{
  "servers": {
    "mcpvault": {
      "type": "stdio",
      "command": "C:\\path\\to\\MCPVault.exe",
      "args": ["--vault", "C:\\MCPVault"]
    }
  }
}
```

Claude Code（`mcpServers`）と VS2022（`servers`）はどちらも `.mcp.json` を読むが、キーが異なるため 1 ファイルに両方を書けば共存できる。

### コマンドライン引数

| 引数 | 説明 |
|------|------|
| `--vault <パス>` | Vault のルートフォルダパス（推奨）。環境変数 `OBSIDIAN_VAULT_PATH` より優先 |
| `--log [ファイルパス]` | 詳細ログをファイルに出力。パス省略時は `.MCPVault/.log/` に自動生成 |

### 環境変数

| 変数名 | 必須 | 説明 |
|--------|------|------|
| `OBSIDIAN_VAULT_PATH` | 任意 | `--vault` 未指定時の Vault パス。どちらもない場合は Windows では `C:\Vault`、Linux では `~/Vault` |
| `MCP_EXCLUDED_FOLDERS` | 任意 | 除外フォルダをセミコロン区切りで指定（例: `Tools3;Private`） |

### 初回起動時の動作

`--vault` で指定したフォルダに `.MCPVault` フォルダが存在しない場合、自動的に作成してデフォルト設定を生成する。

```
{VaultRoot}/
├── .MCPVault/
│   ├── mcp_config.json      # 設定ファイル（人間が編集する。AI からは変更できない）
│   ├── notes.db             # ノートの全文・version・履歴・検索インデックス
│   ├── KnowledgeCell.db     # KnowledgeCell（target=kcell）
│   ├── guestnotes.db        # ゲスト Vault の DB（guest_open 中のみ）
│   └── .log/                # ログファイル
└── README.md                # ウェルカムノート（存在しない場合のみ生成）
```

### 設定ファイル（mcp_config.json）

ツール呼び出しのたびに読み直すため、編集後の再起動は不要。ただし `http` の各項目は起動時にだけ読む。JSON の書式が壊れている場合は直前に読めた設定を使い続け、エラーログに記録する。

```json
{
  "exclusions": {
    "exclude_dot_folders": true,
    "exclude_underscore_folders": false,
    "exclude_ide_folders": true,
    "additional_folders": []
  },
  "scope": {
    "main": [],
    "guest": []
  },
  "guest": {
    "mode": "read_only"
  },
  "permissions": {
    "allow_delete": false,
    "allow_folder_delete": false
  },
  "editing": {
    "max_edit_pairs": 20
  },
  "http": {
    "enabled": false,
    "port": 39680,
    "token": ""
  }
}
```

| キー | 型 | 説明 |
|------|----|------|
| `exclusions.additional_folders` | string[] | 取込対象外フォルダ |
| `exclusions.exclude_dot_folders` | bool | `.` 始まりフォルダを除外（`.MCPVault` もここで除外される） |
| `exclusions.exclude_underscore_folders` | bool | `_` 始まりフォルダを除外 |
| `exclusions.exclude_ide_folders` | bool | `node_modules` / `.git` / `.vscode` 等を除外 |
| `scope.main` / `scope.guest` | string[] | AI が触れるフォルダ。空なら Vault 全体。指定すると範囲外のノートは一覧・検索・読み書きの対象外 |
| `guest.mode` | string | `read_only`（既定）/ `read_write`。ゲスト Vault への書き込み可否 |
| `permissions.allow_delete` | bool | `delete` を許可する（既定 OFF。不要なノートは archive/ へ move する運用） |
| `permissions.allow_folder_delete` | bool | `folder op=delete` を許可する |
| `editing.max_edit_pairs` | int | `edit` の 1 回あたりのペア数上限 |
| `http.enabled` / `port` / `token` | | HTTP インターフェース（後述）。token が空なら起動しない |

---

## アーキテクチャ

SQLite を正とし、md へは変更分だけを書き戻す。AI の読み書きはすべて SQLite を通る。

- **正規形**: UTF-8、CRLF、BOM なし。取込時に CRLF と BOM を除去して LF で保持し、書き戻し時に CRLF を付け直す。AI との間では LF でやりとりする
- **version**: ノートごとに整数の version を持つ。MCPVault の書き込みと、外部変更の取込のたびに 1 上がる。`write`（上書き時）/ `edit` の `if_version` で楽観ロックに使う
- **変更検出**: ファイルの mtime とサイズを DB の記録と比較する。`read` / 書き込み系の呼び出し時に違っていれば取り込み直し、本文が変わっていれば version を上げる。ハッシュは使わない
- **書き戻し前の確認**: 対象の md を読み直し、取込時と同じ場合だけ書く。違う場合は書かずにエラーを返す
- **rollback**: SQLite の履歴から戻す。書き込み前の全文を持つので、1 ノート単位の巻き戻しを md にも反映できる
- **外部編集**: FileSystemWatcher が `.md` の作成・変更・削除・リネームを監視し（500ms デバウンス）、実行中の外部編集も取り込む。起動時はフルスキャンで差分を取り込む

---

## ツール一覧

ツールは 14 個。すべてのツールは `target` で対象を選び、ツールの種類は target によって増減しない。ゲスト Vault の開閉だけ `guest_open` / `guest_close` が別にある。

| target | 実体 | 備考 |
|--------|------|------|
| `main` | メイン Vault（既定） | `--vault` で指定した Vault |
| `guest` | `guest_open` で開いたゲスト Vault | 既定は read_only。`scope.guest` の対象 |
| `kcell` | KnowledgeCell.db | AI 用の KV ストア。path は「セル名/キー」。scope・削除権限の対象外で常に read_write |

| 区分 | ツール | 役割 |
|------|--------|------|
| 読み取り | `read` | 1 件（`path`）または複数（`paths`）のノートを version 付きで読む |
| 読み取り | `list` | フォルダとノートの階層一覧 |
| 読み取り | `recent` | 更新が新しい順のノート |
| 読み取り | `search` | 全文検索。`frontmatter` 条件も指定できる |
| 読み取り | `inbox` | 宛先が自分で未処理のメッセージを取る |
| 書き込み | `write` | 新規作成、または全文上書き |
| 書き込み | `edit` | 部分置換（`old_str` / `new_str` のペアを配列で渡す） |
| 書き込み | `append` | 末尾に追記する。既存の中身を先に読む必要がない |
| 管理 | `move` | 移動・改名（他ノートの `[[wikilink]]` を修正） |
| 管理 | `delete` | 削除。既定では権限 OFF |
| 管理 | `folder` | `op` で作成・改名・空フォルダ削除を切り替える |
| 復元・確認 | `rollback` | 直前の書き込みを元に戻す |
| 復元・確認 | `note_status` | 本文を読まずに version・mtime・サイズ・DB との一致を確認する |
| 情報 | `server_info` | 各 target の接続状態、guest のモード、scope、削除権限、HTTP、バージョン |
| ゲスト | `guest_open` / `guest_close` | ゲスト Vault を開く・閉じる |

### 読み取り

#### `read`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | main / guest / kcell（既定 main） |
| `path` | string | いずれか | 相対パス（例: `Daily/2026-01-15.md`） |
| `paths` | string[] | いずれか | 複数パス。各ノートは `=== path (version N) ===` ヘッダー区切りで返る |

ファイルが外部で変更されていれば取り込み直してから返すので、返る version は常に最新。`.md` 以外のファイルは DB を通さずそのまま読む（version 0）。

#### `list`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `folder` | string | 任意 | フォルダで絞り込む（配下を再帰）。省略時は全件 |

インデント付きのツリーで、各ノートに更新日時と version を付けて返す。kcell では folder 省略でセル一覧、セル名を指定するとキー一覧。

#### `recent`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `count` | number | 任意 | 取得件数（1〜100、既定 10） |

#### `search`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `query` | string | 任意 | 空白区切りで複数語を AND 検索。`frontmatter` を指定する場合は省略可 |
| `limit` | number | 任意 | 最大件数（既定 20、最大 100） |
| `frontmatter` | object | 任意 | `{"status": "open", "to": "claude"}` のような key = value 条件（すべて一致） |

- クエリは正規化（NFKC、ひらがな→カタカナ、英字小文字化）してから空白で分割し、全語 AND で検索する
- すべての語が 3 文字以上なら FTS5 trigram（bm25 順）、2 文字以下の語を含む場合は正規化済み本文の走査（出現回数順）に切り替わる。結果の形式は同じ
- スニペットは原文から前後 30 文字を切り出し、一致箇所を【】で囲む
- scope・frontmatter 条件はどちらの経路でも同じ条件で適用される

#### `inbox`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | main / guest（kcell は不可） |
| `to` | string | 必須 | 自分の nickname（メッセージの frontmatter `to` に書かれる名前） |
| `limit` | number | 任意 | 最大件数（既定 20） |

frontmatter の `to` が一致するノート、または `to` がなく `from` があるノート（全員向け）のうち、`status` が `done` / `archived` / `closed` でないものを返す。`archive/` 配下は除く。処理済みにするには `status` を書き換えるか `archive/` へ move する。

### 書き込み

全文を渡す `write`、一部だけ直す `edit`、末尾に足す `append` の 3 本に分かれ、役割を名前で判別できる。いずれも `return` 引数で返す内容を選べる。

| `return` | 返すもの | 既定で使うツール |
|---------|---------|---------------|
| `path` | メッセージ、パス、version、サイズ | write・append・rollback |
| `diff` | 上に加えて unified diff | edit |
| `full` | `path` の内容に加えて、verify 結果（保存先と DB の一致）と保存先から読み直した本文。diff は含まない | 書き込み確認用 |

#### `write`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `path` | string | 任意 | 相対パス（`.md` 必須）。省略すると `memo/memoyyyyMMddHHmmss.md` に新規作成（同じ秒なら `_2` 等を付ける） |
| `content` | string | 必須 | ノートの全文 |
| `overwrite` | bool | 任意 | 既存ノートの上書きを許可する（`if_version` も必要） |
| `if_version` | integer | 任意 | 現在の version。違えばエラー |
| `return` | string | 任意 | path / diff / full |

- パスありで存在しなければ新規作成
- パスありで存在すれば既定ではエラー。`overwrite=true` と `if_version` を両方指定したときだけ全文を置換する

#### `edit`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `path` | string | 必須 | 相対パス（`.md` 必須） |
| `edits` | object[] | 必須 | `{"old_str": "...", "new_str": "..."}` の配列。順に適用する |
| `replace_all` | bool | 任意 | 各 `old_str` の全箇所を置換する（既定は一意一致を要求） |
| `if_version` | integer | 任意 | 現在の version。違えばエラー |
| `return` | string | 任意 | 既定 diff |

- どれかのペアが見つからない／一意でない、または version が合わない場合は何も書き込まず、現在の全文と version をエラーに同梱して返す
- ペア数の上限は `editing.max_edit_pairs`（既定 20）。超える場合は `write` への切替を促す
- 置換後の本文が元と同じ場合は書き込まずにエラーを返す
- 1 ペアだけなら `edits` の代わりにトップレベルの `old_str` / `new_str` でも渡せる。各要素のキーは旧名の `old_text` / `new_text` も受け付ける

#### `append`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `path` | string | 任意 | 相対パス（`.md` 必須）。省略時は `memo/` に新規作成 |
| `content` | string | 必須 | 追記する内容 |
| `journal` | bool | 任意 | `true` で先頭に `[yyyy-MM-dd HH:mm:ss] `（ローカル時刻）を付け、末尾を改行で終える |
| `return` | string | 任意 | 既定 path |

既存の中身を読まずに呼べる。追記前の末尾に改行がなければ補う。パス指定先が存在しなければ新規作成する。

### 管理

#### `move`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `source` | string | 必須 | 移動元の相対パス |
| `destination` | string | 必須 | 移動先の相対パス（`.md` 必須） |

移動後、scope 内の他ノートの `[[wikilink]]` を自動修正する（修正は履歴に `link` として残り、rollback できる）。version は引き継ぐ。

#### `delete`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `path` | string | 必須 | 相対パス |

`permissions.allow_delete` が OFF（既定）のときはエラーを返し、archive/ への move を促す。削除した `.md` は `rollback` で復元できる。kcell では権限に関係なく使え、path にセル名だけを指定するとセル全体を削除する。

#### `folder`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `op` | string | 必須 | `create` / `rename` / `delete` |
| `path` | string | 必須 | フォルダの相対パス |
| `destination` | string | rename 時 | 新しい相対パス |

`rename` は配下ノートの DB 上のパスと `[[wikilink]]` を一括更新する。`delete` は空フォルダのみで、`permissions.allow_folder_delete` が必要。kcell では未対応（セルはキー書き込み時に自動作成される）。

### 復元・確認

#### `rollback`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `path` | string | 必須 | 相対パス |
| `force` | bool | 任意 | 書き込み後に外部で変更されていても戻す |
| `return` | string | 任意 | 既定 path |

MCPVault 経由の直前の書き込み（write / edit / append / delete / link 修正）を 1 回分取り消す。呼ぶたびに 1 段ずつ戻る（1 ノートあたり最新 20 件）。

- 書き込み前に存在しなかったノートを戻すと、ノートは削除される
- `delete` を戻すと、ノートが復元される
- 履歴の直後の version と現在の version が違う（外部で編集された）場合はエラー。`force=true` で強制的に戻せる
- `move` は対象外。ロールバック自体は履歴に残らない
- kcell ではセル単位で戻す（`path` のセル名部分を使う）

#### `note_status`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `target` | string | 任意 | |
| `path` | string | 必須 | 相対パス |

version、DB とファイルそれぞれの mtime・サイズ、一致しているか、履歴件数を返す。`write` の `if_version` を知りたいとき、外部編集の有無を確認したいときに使う。

### 情報・ゲスト

#### `server_info`
バージョン・ビルド日、main の Vault パス・ノート数・scope・削除権限、guest の接続状態とモード、kcell の DB、HTTP の状態、ログと設定ファイルのパスを返す。パラメータなし。

#### `guest_open`
| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `vault_path` | string | 必須 | ゲスト Vault のルートフォルダの絶対パス |

別の Vault を一時的に開き、`guestnotes.db` をメイン Vault の `.MCPVault/` に作ってバックグラウンドで取り込む。同時に開けるのは 1 つ。再度呼ぶと既存を閉じてから開く。モードは `guest.mode`（既定 read_only）に従い、AI は変えられない。メイン Vault と同じフォルダは開けない。

接続状態はプロセス内にだけ持つ。MCPVault を再起動するとゲスト Vault は閉じた状態に戻り、前回の `guestnotes.db` は起動時に削除される。

#### `guest_close`
ゲスト Vault を閉じ、ウォッチャーを止めて `guestnotes.db`（履歴を含む）を削除する。パラメータなし。

---

## KnowledgeCell（target=kcell）

AI がセッションをまたいで情報を保持するための KV ストア。Vault とは別の SQLite（`KnowledgeCell.db`）に保存され、同じツールを `target=kcell` で使う。

- path は「セル名/キー」。`write` でパスを省略すると `memo/memoyyyyMMddHHmmss` に作る
- version はキーごとの整数（書き込みごとに +1）。`if_version` も同じように使える
- `list` はセル一覧（folder 省略）またはキー一覧（セル名指定）、`search` は値の全文走査（`query` 必須。`frontmatter` 条件は無視される）、`move` はキーの移動・改名
- `rollback` はセル単位で直前の操作を戻す（セルあたり最新 20 操作）
- scope・read_only・削除権限の対象外。`inbox` と `folder` は使えない

---

## 安全設計

AI が触れる範囲と権限は、人間が `mcp_config.json` で先に決めた枠の内側に限る。AI は接続・モード・scope・削除権限を変えられない。

- **パス制限**: すべてのファイル操作は Vault ルート配下に限定（`../` や絶対パスを拒否）。main のツールから guest、guest から main へはアクセスできない
- **scope**: `scope.main` / `scope.guest` を設定すると、範囲外のノートは一覧・検索・読み書き・wikilink 修正の対象外になる。絞り込みは SQL の条件で行う
- **ゲスト Vault**: 既定は read_only。`guest.mode` を `read_write` にしたときだけ書き込める
- **削除**: 既定 OFF。不要になったノートは archive/ へ move する運用にする
- **書き込みは `.md` のみ**: `write` / `edit` / `append` / `move` の移動先 / `rollback` は `.md` 限定。`read` は Vault 内の他のファイルも読める
- **エラー文面**: read_only への書き込み、scope 範囲外、削除権限 OFF、guest 未接続は、何が起きているかと誰が直すかを書き、AI では変更できないことを明記する

---

## HTTP インターフェース

GlassNote などローカルアプリから MCPVault を使うための口。`http.enabled: true` と `http.token` を設定して起動すると、`127.0.0.1:{port}` で待ち受ける。

| メソッド | パス | 説明 |
|---------|------|------|
| GET | `/tools` | ツール定義一覧（tools/list と同じ） |
| POST | `/tools/{name}` | ツール呼び出し。本文は MCP の `arguments` と同じ JSON |

- 認証: ヘッダー `Authorization: Bearer {token}`。一致しなければ 401
- 応答: `{"content":[{"type":"text","text":"..."}],"isError":false}`。ツールのエラーも HTTP 200 で返り、`isError` が `true` になる
- 本文の JSON が壊れていれば 400、パスが違えば 404 を返す
- scope、read_only、削除権限の判定は MCP 経由と同じ入口を通る。ツール呼び出しは MCP と HTTP の間で直列化される
- Windows で listener の起動に失敗する場合は、管理者で `netsh http add urlacl url=http://127.0.0.1:{port}/ user=Everyone` を実行する

```
curl -X POST http://127.0.0.1:39680/tools/recent -H "Authorization: Bearer <token>" -d "{\"count\": 5}"
```

---

## データベース

### notes.db（guestnotes.db も同一構造）

| テーブル | 用途 |
|---------|------|
| `notes` | パス・本文（LF 正規化）・正規化済み本文・version・ファイルの mtime とサイズ・更新日時 |
| `notes_fts` | FTS5 全文検索インデックス（trigram） |
| `note_meta` | YAML frontmatter のキー/値（`inbox` と `search` の `frontmatter` 条件に使う） |
| `note_headings` | 見出し（レベル・テキスト） |
| `note_aliases` | `aliases` の値 |
| `excluded_folders` | 取込対象外フォルダ |
| `note_history` | `rollback` 用の履歴（書き込み前の全文・前後の version。1 ノートあたり最新 20 件） |

`PRAGMA user_version` が 2 でない DB（1.x）は起動時に作り直し、フルスキャンで再取込する（1.x の履歴は引き継がれない）。

### KnowledgeCell.db

| テーブル | 用途 |
|---------|------|
| `knowledge_cells` | セル名・キー・値・更新日時・version |
| `kcell_ops` / `kcell_op_items` | `rollback` 用の操作履歴（セルあたり最新 20 操作） |

旧 DB には `version` 列が自動で追加される。

### 取込タイミング

- **起動時フルスキャン**: バックグラウンドで Vault 全体を走査し、mtime・サイズが変わったファイルだけ取り込み直す
- **FileSystemWatcher**: `.md` の作成・変更・削除・リネームをリアルタイムで追跡（500ms デバウンス）
- **ツール実行時**: `read` と書き込み系は対象ノートの mtime・サイズを確認し、違っていれば取り込み直してから処理する

---

## クロール除外

- `_NoCrawl` という名前のファイルをフォルダに置くと、そのフォルダ以下をスキャンしない（反映には再起動が必要）
- `mcp_config.json` の `exclusions` と環境変数 `MCP_EXCLUDED_FOLDERS` でも除外できる
- `.` 始まりフォルダと IDE 関連フォルダは既定で除外される

---

## ログファイル

すべてのログは `{VaultRoot}/.MCPVault/.log/` 以下に出力される。

| ファイル | 出力条件 | 内容 |
|---------|---------|------|
| `mcpvault_error.log` | 常時 | 起動情報（バージョン・ツール名）＋エラーの 1 行目 |
| `mcp_vault_YYYYMMDD_HHmmss.log` | `--log` 指定時 | 全リクエスト・レスポンス・取込の詳細 |
| `index_YYYYMMDD_HHmmss.log` | フルスキャン毎 | 取込の詳細（ADD/UPD/SKP/DEL） |

---

## 依存ライブラリ

| パッケージ | バージョン | 用途 |
|-----------|-----------|------|
| `Microsoft.Data.Sqlite` | 9.0.4 | SQLite / FTS5 |
| `System.IO.Ports` | 10.0.5 | （未使用。COM ポートデバッグ用に残している） |

---

## 変更履歴

### 2.0.0

**破壊的変更（ツール再編）**
- ツールを 42 個から 14 個に整理した。旧ツール名との互換は取らない
  - `list_notes` → `list`、`recent_notes` → `recent`、`search_notes` → `search`、`read_note` / `read_notes` → `read`
  - `create_note` / `update_note` → `write`（既存ノートの上書きには `overwrite=true` と `if_version` が必要。`template` は廃止）
  - `edit_note` → `edit`（`old_text` / `new_text` → `edits: [{old_str, new_str}]`）
  - `append_note` → `append`、`delete_note` → `delete`、`move_note` → `move`（`sourcePath` / `destinationPath` → `source` / `destination`）
  - `list_folders` → `list`、`create_folder` / `rename_folder` / `delete_empty_folder` → `folder`（`op`）
  - `rollback_note` → `rollback`、`read_back` → `return: "full"`
  - `guest_*` 16 ツール → 各ツールの `target: "guest"`
  - `kcell_*` 7 ツール → 各ツールの `target: "kcell"`（path は「セル名/キー」）
- `delete` は既定で権限 OFF（`permissions.allow_delete`）
- `include_folder_names`（フォルダ名の疑似ノート）を廃止。フォルダは `list` で確認する

**アーキテクチャ**
- SQLite を正とし、md へ書き戻す構成に変更。本文は LF で保持し、md は UTF-8 CRLF BOM なしで書く
- ノートごとの version を導入。`write` / `edit` の `if_version`、`note_status`、rollback の外部変更判定に使う
- 変更検出を hash から mtime + サイズに変更。`read` / 書き込み時に外部変更を取り込む
- 書き戻し直前に md を再確認し、外部で変わっていれば書かずにエラーを返す
- `notes.db` のスキーマを刷新（`user_version` 2）。旧 DB は起動時に作り直す

**追加**
- `target` 引数（main / guest / kcell）
- `inbox`（frontmatter `to` / `from` / `status` によるメッセージ取得）
- `note_status`
- `search` の `frontmatter` 条件
- `return` 引数（path / diff / full）と verify
- `edit` の複数ペア一括置換と、失敗時の全文・version 同梱
- scope（`scope.main` / `scope.guest`）
- ゲスト Vault の read_only 既定（`guest.mode`）
- 削除権限（`permissions.allow_delete` / `allow_folder_delete`）
- HTTP インターフェース（127.0.0.1、トークン認証、既定 OFF）
- 設定ファイルを呼び出しごとに読み直す（更新日時が変わらなければキャッシュ）
- KnowledgeCell に version 列を追加

**プラットフォーム**
- Linux に対応。パス比較の大文字小文字の扱いを OS に合わせた

**検索**
- 「3 文字以上」制限を撤廃。2 文字以下の語を含むクエリは正規化済み本文の走査で検索する
- クエリを空白で分割して各語を引用符で囲んで FTS5 に渡す。記号を含む語で構文エラーになる問題を修正
- スニペットを原文から切り出すよう変更

### 1.2.0

- `update_note` を `update_note` / `append_note` / `edit_note` に分割、`rollback_note` / `kcell_rollback` / `server_info` / `read_back` / `journal` を追加、各ツールのゲスト版を追加
- `read_note` / `create_note` の `template` / wikilink 修正で Vault 外にアクセスできた問題を修正

---

## ライセンス

このプロジェクトは [MIT License](LICENSE) のもとで公開されています。

依存ライブラリのライセンス詳細は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください。
