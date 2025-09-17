# StaticFileServer

## サービス登録（PowerShell/管理者）

### 発行（推奨）

```
dotnet publish -c Release -r win-x64 --self-contained false
```

> 出力先：`bin/Release/net8.0/win-x64/publish/`

### インストール

```powershell
$exe = "C:\path\to\publish\YourApp.exe"     # 実行ファイル
New-Service -Name "MinimalStaticFileServer" `
            -BinaryPathName "`"$exe`"" `
            -DisplayName "Minimal Static File Server" `
            -StartupType Automatic

# 受信許可（例: TCP 8080/8443）
New-NetFirewallRule -DisplayName "MinimalStaticFileServer-HTTP" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow
New-NetFirewallRule -DisplayName "MinimalStaticFileServer-HTTPS" -Direction Inbound -Protocol TCP -LocalPort 8443 -Action Allow

# 起動
Start-Service MinimalStaticFileServer
```

> 既に `appsettings.json` で Kestrel の URL を設定していれば、そのポートで待受します。
> 例：`http://0.0.0.0:8080`、`https://0.0.0.0:8443`（証明書設定済みなら）

### アンインストール

```powershell
Stop-Service MinimalStaticFileServer
sc.exe delete MinimalStaticFileServer
```
---

上記は **PowerShell で Windows サービスを登録・起動**しているもの。
`sc create` と似た役割ですが、仕組みと書き方が少し違います。

## 🔎 コマンドの意味

```powershell
$exe = "C:\path\to\publish\YourApp.exe"     # 実行ファイルのパスを変数に入れる

# 新しいサービスを登録
New-Service -Name "MinimalStaticFileServer" `
            -BinaryPathName "`"$exe`"" `
            -DisplayName "Minimal Static File Server" `
            -StartupType Automatic

# Windows ファイアウォールで TCP 8080/8443 を開ける
New-NetFirewallRule -DisplayName "MinimalStaticFileServer-HTTP"  -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow
New-NetFirewallRule -DisplayName "MinimalStaticFileServer-HTTPS" -Direction Inbound -Protocol TCP -LocalPort 8443 -Action Allow

# サービスを起動
Start-Service MinimalStaticFileServer
```

---

## ✅ 何をしているか

1. **`New-Service`**

   * Windows サービスとして exe を登録
   * サービス名（内部名） = `"MinimalStaticFileServer"`
   * 実行ファイル = `$exe`
   * 表示名（管理ツールに出る名前） = `"Minimal Static File Server"`
   * 自動起動（Windows 起動と一緒に立ち上がる）

2. **`New-NetFirewallRule`**

   * Windows ファイアウォールで指定ポートを開放
   * 内部の TCP 接続を受けられるようにする

3. **`Start-Service`**

   * 登録したサービスを起動

---

## ⚖️ `New-Service` vs `sc create`

* `sc create` → 古くからある **サービス制御コマンド**（cmd.exe 系）。

  ```cmd
  sc create MinimalStaticFileServer binPath= "C:\path\to\YourApp.exe" start= auto
  ```
* `New-Service` → PowerShell 版。引数がオブジェクト指向っぽく扱える。

両方とも **サービス登録処理をしている点は同じ**です。
違いは「コマンド体系が cmd か PowerShell か」という程度。

---
# スクリプト

## 1. PowerShell で読み込む

管理者権限で PowerShell を開き、次のように読み込みます：

```powershell
. "C:\scripts\Manage-StaticFileServer.ps1"
```

※ 頭の `.`（ドット）＋スペースを忘れないでください。
これで、そのセッション内に `Register-StaticFileServerService` と `Unregister-StaticFileServerService` 関数が使えるようになります。

---

## 2. サービス登録（作成）

### 最低限の例

```powershell
Register-StaticFileServerService -BinaryPath "C:\apps\MinimalStaticFileServer\MinimalStaticFileServer.exe"
```

* `-BinaryPath` : 実行ファイル（ビルドした ASP.NET Core サーバーの exe）のフルパス

### 引数や表示名を付ける例

```powershell
Register-StaticFileServerService `
  -BinaryPath "C:\apps\MinimalStaticFileServer\MinimalStaticFileServer.exe" `
  -Arguments '--urls "http://*:8080"' `
  -DisplayName "Minimal Static File Server" `
  -Description "ASP.NET Core static file server (Negotiate auth for logging only)" `
  -StartupType Automatic `
  -Force
```

* `-Arguments` : `--urls` など Kestrel の起動引数を渡せる
* `-DisplayName` : サービス管理画面（services.msc）に表示される名前
* `-Description` : サービスの説明
* `-StartupType` : 自動起動 (`Automatic`)、手動 (`Manual`)、無効 (`Disabled`)
* `-Force` : 既に同名サービスがあれば削除して作り直す

---

## 3. サービス削除

登録したサービスを削除するときは：

```powershell
Unregister-StaticFileServerService
```

必要なら名前を変えて削除できます：

```powershell
Unregister-StaticFileServerService -Name "MinimalStaticFileServer"
```

---

## 4. 動作確認

サービスの状態を見るには：

```powershell
Get-Service -Name "MinimalStaticFileServer"
```

開始・停止は普通に PowerShell のコマンドでできます：

```powershell
Start-Service -Name "MinimalStaticFileServer"
Stop-Service  -Name "MinimalStaticFileServer"
```

---

✅ まとめると：

1. スクリプトを保存 → 読み込み
2. `Register-StaticFileServerService` でサービス登録
3. 不要になったら `Unregister-StaticFileServerService` で削除
