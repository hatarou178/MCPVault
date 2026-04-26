# MCPVault

Obsidian の Vault を Claude などの AI エージェントからファイルシステム直接アクセスで操作するスタンドアロン MCP サーバー（Windows 向け、C# / .NET 8）。

Obsidian アプリや REST API に依存しない。ノートの全文検索インデックスを SQLite FTS5 trigram で保持し、日本語・英語の高速検索を提供する。

- プロトコル: JSON-RPC 2.0 over stdio
- MCP プロトコルバージョン: 2024-11-05
- サーバー名: `mcpvault` / バージョン: `1.0.0`
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

#### `search_notes`（エイリアス: `search_vault`）
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

#### `update_note`
既存ノートを更新する。モードを指定しない場合は上書き。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `path` | string | 必須 | 相対パス（`.md` 必須） |
| `content` | string | 必須 | 書き込む内容 |
| `append` | bool | 任意 | `true` で末尾追記（デフォルト: `false`） |
| `create_if_not_exists` | bool | 任意 | `false` でファイル不在時エラー（デフォルト: `true`） |
| `mode` | string | 任意 | `replace` または `insert` |
| `old_text` | string | 任意 | `replace` モード: 置換対象テキスト |
| `replace_all` | bool | 任意 | `replace` モード: 全件置換（デフォルト: `false`） |
| `anchor` | string | 任意 | `insert` モード: 挿入位置の基準となる行のテキスト |
| `insert_after` | bool | 任意 | `insert` モード: `true` でアンカー行の後に挿入（デフォルト: `true`） |

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

#### `kcell_write`
キーと値を指定セルに書き込む。セルが存在しない場合は自動作成。同じキーへの再書き込みは上書き（upsert）。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名（例: `quick`, `session`, `novel`）。自動作成される |
| `key` | string | 必須 | キー |
| `value` | string | 必須 | 保存する値 |

#### `kcell_read`
セルからエントリを読み込む。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名 |
| `key` | string | 任意 | 読み込むキー。省略時はセル内全エントリを更新日時降順で返す |
| `latest` | bool | 任意 | `true` で最も最近書き込まれた 1 件のみ返す（デフォルト: `false`） |

#### `kcell_delete`
セルからキーを削除する。キー省略時はセル全体（全キー）を削除。

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|------|------|
| `cell_name` | string | 必須 | セル名 |
| `key` | string | 任意 | 削除するキー。省略時はセル全体を削除 |

#### `kcell_list`
全セルをキー数・最終更新タイムスタンプとともに返す。パラメータなし。

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

### KnowledgeCell.db

DB ファイル: `{VaultRoot}/.MCPVault/KnowledgeCell.db`

| テーブル | 用途 |
|---------|------|
| `knowledge_cells` | セル名・キー・値・更新タイムスタンプ（`(cell_name, key)` が主キー） |

### インデックス更新タイミング

- **起動時フルスキャン**: バックグラウンドで Vault 全体をスキャンし追加・更新・削除を反映
- **FileSystemWatcher**: `.md` ファイルの作成・変更・削除・リネームをリアルタイムで追跡（500ms デバウンス）
- **ツール実行時**: `read_note` / `update_note` / `create_note` / `move_note` 実行後に即時更新

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

- すべてのファイル操作は Vault ルート配下に限定（パストラバーサル防止）
- `.md` 以外への書き込み・作成は禁止
- `move_note` の移動先も `.md` のみ許可

---

## 依存ライブラリ

| パッケージ | バージョン | 用途 |
|-----------|-----------|------|
| `Microsoft.Data.Sqlite` | 9.0.4 | SQLite / FTS5 インデックス |
| `System.IO.Ports` | 10.0.5 | COM ポート（デバッグ用、現在コメントアウト） |

---

## ライセンス

このプロジェクトは [MIT License](LICENSE) のもとで公開されています。

依存ライブラリのライセンス詳細は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください。
