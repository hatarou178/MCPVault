# MCPVault

Obsidian の Vault を Claude などの AI エージェントからファイルシステム直接アクセスで操作するスタンドアロン MCP サーバー（Windows 向け、C# / .NET 8）。

Obsidian アプリや REST API に依存しない。ノートの全文検索インデックスを SQLite FTS5 trigram で保持し、日本語・英語の高速検索を提供する。
自前でファイルアクセスを行うため、複数Vaultを使用可能。（現状はメインVaultとゲストVaultの二つ）

- プロトコル: JSON-RPC 2.0 over stdio
- MCP プロトコルバージョン: 2024-11-05
- サーバー名: `mcpvault` / バージョン: `1.2.0`
- ターゲットフレームワーク: .NET 8.0

---

## セットアップ

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

#### VS Code（GitHub Copilot Agent モード）

`.vscode/mcp.json` を作成する。Claude Code の `.mcp.json` とは別ファイルのため競合しない。

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

**Copilot Chat の UI から登録する方法**

1. Copilot Chat パネルを開く
2. パネル右上の **歯車アイコン** をクリック
3. **MCP Servers** セクションの **`+`** ボタンをクリック
4. サーバー種別で **stdio** を選択
5. コマンド入力: `C:\path\to\MCPVault.exe`
6. 引数入力: `--vault C:\MCPVault`
7. サーバー名入力: `mcpvault`
8. 保存先を選択:
   - **Workspace Settings** → `.vscode/mcp.json`（プロジェクト共有）
   - **User Settings** → ユーザー設定（全プロジェクト共通）

登録後、MCP Servers 一覧に `mcpvault` が表示されれば接続完了。

#### Visual Studio 2022（GitHub Copilot Agent モード）

ソリューションファイル（`.sln`）と同じフォルダに `.mcp.json` を作成する。

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

#### `.mcp.json` の競合について

Claude Code と VS2022 はどちらもソリューション／プロジェクトルートの `.mcp.json` を読むが、**使用するキーが異なる**。

| クライアント | 読み込みファイル | トップレベルキー |
|-------------|---------------|--------------|
| Claude Code | `.mcp.json` | `mcpServers` |
| VS2022 Copilot | `.mcp.json` | `servers` |
| VS Code Copilot | `.vscode/mcp.json` | `servers` |

各ツールは自分が知らないキーを無視するため、**1 ファイルに両キーを共存させる**ことで競合を回避できる。

```json
{
  "mcpServers": {
    "mcpvault": {
      "command": "C:\\path\\to\\MCPVault.exe",
      "args": ["--vault", "C:\\MCPVault"]
    }
  },
  "servers": {
    "mcpvault": {
      "type": "stdio",
      "command": "C:\\path\\to\\MCPVault.exe",
      "args": ["--vault", "C:\\MCPVault"]
    }
  }
}
```

> **注意**: `.mcp.json` をリポジトリにコミットする場合、`command` のパスが各開発者の環境依存になる点に注意。個人パスを含む場合は `.gitignore` に追加するか、パスを変数化する運用を検討すること。

### コマンドライン引数

| 引数 | 説明 |
|------|------|
| `--vault <パス>` | Vault のルートフォルダパス（推奨）。環境変数 `OBSIDIAN_VAULT_PATH` より優先 |
| `--log [ファイルパス]` | 詳細ログをファイルに出力。パス省略時は `.MCPVault/.log/` に自動生成 |

### 環境変数

| 変数名 | 必須 | 説明 |
|--------|------|------|
| `OBSIDIAN_VAULT_PATH` | 任意 | `--vault` 未指定時の Vault パス（フォールバック） |
| `MCP_EXCLUDED_FOLDERS` | 任意 | 除外フォルダをセミコロン区切りで指定（例: `Tools3;Private`） |

### 初回起動時の動作

`--vault` で指定したフォルダに `.MCPVault` フォルダが存在しない場合、自動的に作成してデフォルト設定を生成する。

```
{VaultRoot}/
├── .MCPVault/
│   ├── mcp_config.json      # 設定ファイル（自動生成・手直し可）
│   ├── notes.db             # ノートインデックス DB
│   ├── KnowledgeCell.db     # KnowledgeCell DB
│   ├── guestnotes.db        # ゲストVaultインデックス DB（guest_open 時のみ生成、一時的）
│   └── .log/                # ログファイル
└── README.md                # ウェルカムノート（存在しない場合のみ生成）
```

### 設定ファイル（mcp_config.json）

```json
{
  "exclusions": {
    "additional_folders": [],
    "exclude_dot_folders": true,
    "exclude_underscore_folders": false,
    "exclude_ide_folders": true
  },
  "indexing": {
    "include_folder_names": false
  }
}
```

| キー | 型 | 説明 |
|------|----|------|
| `exclusions.additional_folders` | string[] | インデックス対象外フォルダ |
| `exclusions.exclude_dot_folders` | bool | `.` 始まりフォルダを除外（`.MCPVault` もここで除外される） |
| `exclusions.exclude_underscore_folders` | bool | `_` 始まりフォルダを除外 |
| `exclusions.exclude_ide_folders` | bool | `node_modules` / `.git` / `.vscode` 等を除外 |
| `indexing.include_folder_names` | bool | フォルダ名もインデックス対象に含める |

---

## ツール一覧

全 42 ツール。ゲスト Vault 用の `guest_*` はメイン Vault 用ツールと同じ引数・挙動で、操作対象だけが異なる。

| 分類 | ツール |
|------|--------|
| サーバー情報 | `server_info` |
| ゲストVault管理 | `guest_open` / `guest_close` |
| 検索・一覧 | `list_notes` / `recent_notes` / `search_notes` |
| ノート読み取り | `read_note` / `read_notes` |
| ノート書き込み | `create_note` / `update_note` / `append_note` / `edit_note` / `delete_note` / `move_note` / `rollback_note` |
| フォルダ操作 | `list_folders` / `create_folder` / `rename_folder` / `delete_empty_folder` |
| KnowledgeCell | `kcell_read` / `kcell_list` / `kcell_write` / `kcell_append` / `kcell_edit` / `kcell_delete` / `kcell_rollback` |
| ゲストVault操作 | 上記「検索・一覧」〜「フォルダ操作」の `guest_*` 版（16 ツール） |

**書き込み系の使い分け**（ノート・KnowledgeCell 共通）

| やりたいこと | ノート | KnowledgeCell |
|-------------|--------|---------------|
| 全体を書き換える（なければ作成） | `update_note` | `kcell_write` |
| 末尾に足す（ログ・日誌は `journal: true`） | `append_note` | `kcell_append` |
| 一部を書き換える | `edit_note` | `kcell_edit` |
| 直前の書き込みを取り消す | `rollback_note` | `kcell_rollback` |

内容を書き込むツール（`create_note` / `update_note` / `append_note` / `edit_note` / `rollback_note` と `kcell_write` / `kcell_append` / `kcell_edit` / `kcell_rollback`）は `read_back` オプションを持つ。`true` にすると、書き込み後にノート（または値）を読み直して完了応答に全内容を付けるため、書き込み結果をその場で確認できる。

### サーバー情報

#### `server_info`
バージョン・ビルド日、メイン Vault のパスとインデックス済みノート数、データフォルダ（`.MCPVault`）、ゲスト Vault のオープン状態とノート数、ログファイルのパスを返す。操作対象の Vault を確認したいときに使う。パラメータなし。

### ゲストVault管理

#### `guest_open`
立ち上げ後に追加で別の Vault を一時的に開く。`guestnotes.db` をメイン Vault の `.MCPVault/` に作成してバックグラウンドでインデックスを構築する。同時に開けるゲスト Vault は 1 つのみ。再度呼ぶと既存ゲスト Vault を閉じてから開く。

- `vault_path` に指定できる場所に制限はない（存在するフォルダであればどこでも開ける）
- 開いた後の `guest_*` ツールの操作は、そのゲスト Vault の配下に限定される（メイン Vault を含め、ゲスト Vault の外は読み書きできない）
- 開いているゲスト Vault の場所は `server_info` で確認できる

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `vault_path` | string | 必須 | ゲスト Vault のルートフォルダの絶対パス |

#### `guest_close`
現在開いているゲスト Vault を閉じる。FileSystemWatcher を停止し、`guestnotes.db` を削除する。パラメータなし。

---

### 検索・一覧

#### `list_notes`
Vault 内のノート一覧をパスとタイムスタンプ付きで返す。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `folder` | string | 任意 | フォルダで絞り込む（相対パス）。省略時は全件 |

#### `recent_notes`
最近更新されたノートを返す。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `count` | number | 任意 | 取得件数（1〜100、デフォルト 10） |

#### `search_notes`
FTS5 trigram による全文検索。日本語・英語対応。検索結果にスニペット（前後文脈）が含まれる。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `query` | string | 必須 | 検索クエリ（3文字以上必要） |
| `limit` | number | 任意 | 最大件数（デフォルト 20、最大 100） |

---

### ノート読み取り

#### `read_note`
単一ノートをファイルシステムから読み込み、インデックスも更新する。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス（例: `Daily/2024-01-15.md`） |

#### `read_notes`
複数ノートを一括読み込み。各ノートは `=== path ===` ヘッダー区切りで結合して返す。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `paths` | string[] | 必須 | 相対パスの配列 |

---

### ノート書き込み

#### `create_note`
新規ノートを作成する。既存ファイルがある場合はエラー。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス（`.md` 必須） |
| `content` | string | 任意 | 初期コンテンツ。`template` より優先 |
| `template` | string | 任意 | テンプレート名（例: `daily` → `templates/daily.md`） |
| `read_back` | bool | 任意 | `true` で書き込み後にノートをディスクから読み直し、全内容を応答に付ける（デフォルト: `false`） |

#### `update_note`
ノート全体を上書きする。ファイルが存在しない場合は作成する。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス（`.md` 必須） |
| `content` | string | 必須 | ノートの新しい全内容 |
| `read_back` | bool | 任意 | `true` で書き込み後にノートをディスクから読み直し、全内容を応答に付ける（デフォルト: `false`） |

#### `append_note`
ノート末尾に追記する。ファイルが存在しない場合は作成する。既存内容が改行で終わっていない場合は改行を挟んでから追記する。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス（`.md` 必須） |
| `content` | string | 必須 | 追記する内容 |
| `journal` | bool | 任意 | `true` で先頭に `[yyyy-MM-dd HH:mm:ss] `（ローカル時刻）を付け、末尾を改行で終える（デフォルト: `false`） |
| `read_back` | bool | 任意 | `true` で書き込み後にノートをディスクから読み直し、全内容を応答に付ける（デフォルト: `false`） |

#### `edit_note`
既存ノート内の `old_text` を `new_text` に置換する。ファイルが存在しない場合、`old_text` が見つからない場合、`replace_all=false` で複数箇所に一致した場合はエラー。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス（`.md` 必須） |
| `old_text` | string | 必須 | 置換対象テキスト（完全一致） |
| `new_text` | string | 必須 | 置換後テキスト（空文字で削除） |
| `replace_all` | bool | 任意 | `true` で全件置換（デフォルト: `false`） |
| `read_back` | bool | 任意 | `true` で書き込み後にノートをディスクから読み直し、全内容を応答に付ける（デフォルト: `false`） |

#### `delete_note`
ノートをファイルシステムから削除し、インデックスからも除去する。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス |

#### `move_note`
ノートを移動／リネームする。移動後、他ノートの `[[wikilink]]` を自動修正する。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `sourcePath` | string | 必須 | 移動元の相対パス |
| `destinationPath` | string | 必須 | 移動先の相対パス（`.md` 必須） |

#### `rollback_note`
ノートへの直前の書き込み（`create_note` / `update_note` / `append_note` / `edit_note` / `delete_note`）を 1 回分取り消す。呼ぶたびに 1 段ずつ前の状態に戻る（1 ノートあたり最新 20 件まで）。

- 書き込み前に存在しなかったノート（作成・新規追記など）を戻すと、ノートは削除される
- `delete_note` を戻すと、ノートが復元される（`.md` のみ）
- 書き込み後に Obsidian などでノートが変更されている場合はエラー（`force: true` で強制的に戻せるが、外部での変更は失われる）
- `move_note` は対象外。移動後のパスには移動前の履歴は引き継がれない
- ロールバック自体は履歴に残らない（やり直しはできない）
- 履歴は `notes.db` に保存されるため、`notes.db` を削除すると履歴も消える

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス（`.md` 必須） |
| `force` | bool | 任意 | `true` で外部変更があっても戻す（デフォルト: `false`） |
| `read_back` | bool | 任意 | `true` で戻した後にノートをディスクから読み直し、全内容を応答に付ける（デフォルト: `false`） |

---

### フォルダ操作

#### `list_folders`
ノートが存在するフォルダの一覧を返す（インデックスから取得）。パラメータなし。

#### `create_folder`
新規フォルダを作成する。既存の場合はエラー。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス |

#### `rename_folder`
フォルダを移動／リネームする。配下ノートのインデックスパスと `[[wikilink]]` を一括更新する。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `sourcePath` | string | 必須 | 現在の相対パス |
| `destinationPath` | string | 必須 | 新しい相対パス |

#### `delete_empty_folder`
空フォルダのみ削除する。ファイルやサブフォルダが存在する場合はエラー。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス |

---

### KnowledgeCell

AI がセッションをまたいで情報を保持するための軽量な KV ストア。Vault のノートとは独立した SQLite DB（`KnowledgeCell.db`）に保存される。

#### `kcell_read`
セルからエントリを読み込む。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名 |
| `key` | string | 任意 | 読み込むキー。省略時はセル内全エントリを更新日時降順で返す |
| `latest` | bool | 任意 | `true` で最も最近書き込まれた 1 件のみ返す（デフォルト: `false`） |

#### `kcell_list`
全セルをキー数・最終更新タイムスタンプとともに返す。パラメータなし。

#### `kcell_write`
キーと値を指定セルに書き込む。セルが存在しない場合は自動作成。同じキーへの再書き込みは上書き（upsert）。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名（例: `quick`, `session`, `novel`）。自動作成される |
| `key` | string | 必須 | キー |
| `value` | string | 必須 | 保存する値 |
| `read_back` | bool | 任意 | `true` で書き込み後に DB から値を読み直し、応答に付ける（デフォルト: `false`） |

#### `kcell_append`
キーの値の末尾に追記する。キーが存在しない場合は作成する。改行・`journal` の扱いは `append_note` と同じ。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名 |
| `key` | string | 必須 | キー |
| `value` | string | 必須 | 追記する内容 |
| `journal` | bool | 任意 | `true` で先頭に `[yyyy-MM-dd HH:mm:ss] `（ローカル時刻）を付け、末尾を改行で終える（デフォルト: `false`） |
| `read_back` | bool | 任意 | `true` で書き込み後に DB から値を読み直し、応答に付ける（デフォルト: `false`） |

#### `kcell_edit`
キーの値の中の `old_text` を `new_text` に置換する。キーが存在しない場合、`old_text` が見つからない場合、`replace_all=false` で複数箇所に一致した場合はエラー。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名 |
| `key` | string | 必須 | キー |
| `old_text` | string | 必須 | 置換対象テキスト（完全一致） |
| `new_text` | string | 必須 | 置換後テキスト（空文字で削除） |
| `replace_all` | bool | 任意 | `true` で全件置換（デフォルト: `false`） |
| `read_back` | bool | 任意 | `true` で書き込み後に DB から値を読み直し、応答に付ける（デフォルト: `false`） |

#### `kcell_delete`
セルからキーを削除する。キー省略時はセル全体（全キー）を削除。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名 |
| `key` | string | 任意 | 削除するキー。省略時はセル全体を削除 |

#### `kcell_rollback`
セルへの直前の操作（`kcell_write` / `kcell_append` / `kcell_edit` / `kcell_delete`）を 1 回分取り消し、対象キーの値と更新日時を操作前に戻す。呼ぶたびに 1 段ずつ戻る（セルあたり最新 20 操作まで）。

- ロールバックの単位はセル（キー単位ではない）。セル全体の削除を戻すと全キーが復元される
- 書き込みと履歴記録、復元と履歴削除はそれぞれ 1 トランザクションで行う
- KnowledgeCell は MCP 経由でしか更新されないため、`rollback_note` のような外部変更チェック（`force`）はない
- ロールバック自体は履歴に残らない（やり直しはできない）

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名 |
| `read_back` | bool | 任意 | `true` で戻したキーの現在値を応答に付ける（デフォルト: `false`） |

---

### ゲストVaultツール群

`guest_open` で Vault を開いた後に使用できる。パラメータ・挙動はメイン Vault の対応ツールと同一で、操作対象がゲスト Vault になる。ゲスト Vault が未オープンの場合はエラーを返す。

| ツール | 対応するメインツール |
|--------|-------------------|
| `guest_list_notes` | `list_notes` |
| `guest_recent_notes` | `recent_notes` |
| `guest_search_notes` | `search_notes` |
| `guest_read_note` | `read_note` |
| `guest_read_notes` | `read_notes` |
| `guest_create_note` | `create_note` |
| `guest_update_note` | `update_note` |
| `guest_append_note` | `append_note` |
| `guest_edit_note` | `edit_note` |
| `guest_delete_note` | `delete_note` |
| `guest_move_note` | `move_note` |
| `guest_rollback_note` | `rollback_note`（履歴は `guest_close` で破棄される） |
| `guest_list_folders` | `list_folders` |
| `guest_create_folder` | `create_folder` |
| `guest_rename_folder` | `rename_folder` |
| `guest_delete_empty_folder` | `delete_empty_folder` |

各ツールのパラメータ詳細はメイン Vault の対応ツールを参照。

---

## インデックス（SQLite）

### notes.db

DB ファイル: `{VaultRoot}/.MCPVault/notes.db`

| テーブル | 用途 |
|---------|------|
| `notes` | ノートのパス・タイムスタンプ・コンテンツ・ハッシュ |
| `notes_fts` | FTS5 全文検索インデックス（trigram トークナイザー） |
| `note_meta` | YAML フロントマターのキー/値 |
| `note_headings` | 見出し（レベル・テキスト） |
| `note_aliases` | `aliases` フィールドの値 |
| `excluded_folders` | インデックス対象外フォルダ |
| `note_history` | `rollback_note` 用の書き込み履歴（書き込み前の内容・書き込み後のハッシュ。1 ノートあたり最新 20 件） |

### KnowledgeCell.db

DB ファイル: `{VaultRoot}/.MCPVault/KnowledgeCell.db`

| テーブル | 用途 |
|---------|------|
| `knowledge_cells` | セル名・キー・値・更新タイムスタンプ（`(cell_name, key)` が主キー） |
| `kcell_ops` | `kcell_rollback` 用の操作履歴（セルあたり最新 20 操作） |
| `kcell_op_items` | 各操作で変更されたキーの操作前の値・更新タイムスタンプ |

### guestnotes.db

DB ファイル: `{MainVaultRoot}/.MCPVault/guestnotes.db`

`guest_open` 実行時に作成される一時的なインデックス。テーブル構造は `notes.db` と同一（`note_history` を含む）。`guest_close` 実行時、`guest_open` の再実行時、サーバー起動時に削除されるため、ゲスト Vault の書き込み履歴（`guest_rollback_note`）もそこで失われる。

### インデックス更新タイミング

- **起動時フルスキャン**: バックグラウンドで Vault 全体をスキャンし追加・更新・削除を反映
- **FileSystemWatcher**: `.md` ファイルの作成・変更・削除・リネームをリアルタイムで追跡（500ms デバウンス）
- **ツール実行時**: `read_note` / `read_notes` / `create_note` / `update_note` / `append_note` / `edit_note` / `delete_note` / `move_note` / `rollback_note` / `rename_folder` 実行後に即時更新

---

## クロール除外の仕組み

### _NoCrawl ファイル方式

`_NoCrawl` という名前のファイルをフォルダに置くと、そのフォルダ以下の再帰スキャンをスキップする。
設定ファイルの `additional_folders` で個別指定するより直感的に除外を管理できる。

- `_NoCrawl` 発見時点でそのフォルダ以下への再帰を停止
- そのフォルダの直上にある `.md` はスキャン済みのため影響なし
- Vault ルートに置いた場合は全除外になるため警告をログ出力
- `_NoCrawl` の追加・削除を反映するには MCPVault の再起動が必要

### 設定ファイルによる除外

`mcp_config.json` の `exclusions.additional_folders` にフォルダ名を列挙することでも除外できる。
`.` 始まりフォルダ（`exclude_dot_folders: true`）や IDE 関連フォルダ（`exclude_ide_folders: true`）はデフォルトで除外される。

---

## ログファイル

すべてのログは `{VaultRoot}/.MCPVault/.log/` 以下に出力される。

| ファイル | 出力条件 | 内容 |
|---------|---------|------|
| `mcpvault_error.log` | 常時（固定名） | 起動情報（バージョン・ツール名）＋エラーのみ |
| `mcp_vault_YYYYMMDD_HHmmss.log` | `--log` 指定時 | 全リクエスト・レスポンスの詳細ログ |
| `index_YYYYMMDD_HHmmss.log` | 起動時スキャン毎 | インデックス構築の詳細（ADD/UPD/SKP/DEL） |

---

## セキュリティ

- すべてのファイル操作（読み取り・書き込み・削除・移動・フォルダ操作・テンプレート読み込み・wikilink 修正）は Vault ルート配下に限定（`../` や絶対パスによるパストラバーサルを拒否）
  - メイン用ツールはメイン Vault 配下、`guest_*` ツールはゲスト Vault 配下のみ。メイン用ツールからゲスト Vault、`guest_*` ツールからメイン Vault へはアクセスできない
  - 拒否時のエラーには基準となった Vault ルートが含まれる（例: `Vault 外へのアクセスは禁止されています（Vault: D:\GuestVault）: ../x.md`）
- `guest_open` で開く場所自体には制限はない
- 読み取り（`read_note` / `read_notes`）は Vault 内であれば `.md` 以外のファイルも可
- 内容を書き込むツール（`create_note` / `update_note` / `append_note` / `edit_note` / `rollback_note`）は `.md` のみ許可
- `move_note` の移動先も `.md` のみ許可
- `delete_note` は `.md` 以外のファイルも削除できる（ただし `rollback_note` で復元できるのは `.md` のみ）

---

## 依存ライブラリ

| パッケージ | バージョン | 用途 |
|-----------|-----------|------|
| `Microsoft.Data.Sqlite` | 9.0.4 | SQLite / FTS5 インデックス |
| `System.IO.Ports` | 10.0.5 | COM ポート（デバッグ用、現在コメントアウト） |

---

## 変更履歴

### 1.2.0

**破壊的変更**
- `update_note` を分割。`update_note` は全体上書きのみとなり、`append` / `mode` / `old_text` / `replace_all` / `anchor` / `insert_after` / `create_if_not_exists` パラメータを廃止
  - 追記 → `append_note`、部分置換 → `edit_note` を使う
  - `insert` モードは廃止（`edit_note` で代替）
- 置換（`edit_note`）で `old_text` が複数箇所に一致した場合、`replace_all` 未指定ならエラーになる（従来は先頭 1 箇所を置換）
- `search_vault`（`search_notes` のエイリアス）を廃止
- ゲスト版（`guest_update_note` 等）も同様

**追加**
- `append_note` / `edit_note` / `rollback_note`（ノート）
- `kcell_append` / `kcell_edit` / `kcell_rollback`（KnowledgeCell）
- 内容を書き込むツールに `read_back` オプション
- `append_note` / `kcell_append` に `journal` オプション（タイムスタンプ付き追記）
- `server_info`（バージョン・ビルド日・Vault の場所などを返す）
- 各ツールのゲスト版

**セキュリティ修正**
- `read_note` / `read_notes`（およびゲスト版）で `../` や絶対パスを指定すると Vault 外のファイルを読み取れ、インデックスにも登録されていた問題を修正
- `create_note` の `template` で Vault 外の `.md` を読み込めた問題を修正
- `move_note` / `rename_folder` の wikilink 修正で、インデックスに紛れた Vault 外パスを書き換え対象にしないよう修正
- 旧バージョンでインデックスに登録された Vault 外のエントリは、1.2.0 の起動時フルスキャンで自動的に削除される
- Vault 外アクセス拒否時のエラーメッセージを全ツールで統一し、基準の Vault ルート（メイン／ゲスト）を表示するよう変更

---

## ライセンス

このプロジェクトは [MIT License](LICENSE) のもとで公開されています。

依存ライブラリのライセンス詳細は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください。
