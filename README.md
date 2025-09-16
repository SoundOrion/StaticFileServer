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


