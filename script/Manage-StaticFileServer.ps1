<# 
.SYNOPSIS
  MinimalStaticFileServer を Windows サービスとして登録／削除（PowerShell 5.1 ネイティブ）

.DESCRIPTION
  - 登録は New-Service（Win32_Service.Create のラッパー）
  - 削除は Get-CimInstance / Remove-CimInstance（WMI/CIM 経由）
  - 既存サービスに同名がある場合、-Force 指定で一度削除して再作成します
  - 既定の実行アカウントは LocalSystem（-Credential を渡せば変更可）

#>

function Register-StaticFileServerService {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param(
        [Parameter(Mandatory=$false)]
        [string] $Name = "MinimalStaticFileServer",

        # アプリ本体のフルパス（例: C:\apps\MinimalStaticFileServer\MinimalStaticFileServer.exe）
        [Parameter(Mandatory=$true)]
        [ValidateNotNullOrEmpty()]
        [string] $BinaryPath,

        # コマンドライン引数（例: --urls "http://*:8080" など）※必要なければ空
        [string] $Arguments = "",

        # サービス表示名（未指定なら Name と同じ）
        [string] $DisplayName,

        # サービス説明
        [string] $Description = "Minimal static file server (ASP.NET Core)",

        # Automatic / Manual / Disabled
        [ValidateSet("Automatic","Manual","Disabled")]
        [string] $StartupType = "Automatic",

        # 既存がある場合に削除して作り直す
        [switch] $Force,

        # 別アカウントで動かす場合（例: DOMAIN\User）。指定しなければ LocalSystem
        [System.Management.Automation.PSCredential] $Credential
    )

    if (-not (Test-Path -LiteralPath $BinaryPath)) {
        throw "BinaryPath が存在しません: $BinaryPath"
    }

    # New-Service の -BinaryPathName は「exe と引数を含む 1 本の文字列」を要求
    $binPathName = if ([string]::IsNullOrWhiteSpace($Arguments)) { 
        "`"$BinaryPath`""
    } else {
        "`"$BinaryPath`" $Arguments"
    }

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($svc) {
        if ($Force) {
            Write-Verbose "既存サービス[$Name]を削除して再作成します。"
            if ($PSCmdlet.ShouldProcess($Name, "Stop & Remove existing service")) {
                try { Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue } catch {}
                # 完全ネイティブ削除（PS5.1対応）
                Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue |
                    Remove-CimInstance -ErrorAction SilentlyContinue
            }
        } else {
            throw "サービス [$Name] は既に存在します。上書きするには -Force を指定してください。"
        }
    }

    if ($PSCmdlet.ShouldProcess($Name, "Create service")) {
        if ($PSBoundParameters.ContainsKey('Credential')) {
            New-Service -Name $Name `
                        -BinaryPathName $binPathName `
                        -DisplayName ($DisplayName ?? $Name) `
                        -Description $Description `
                        -StartupType $StartupType `
                        -Credential $Credential | Out-Null
        } else {
            New-Service -Name $Name `
                        -BinaryPathName $binPathName `
                        -DisplayName ($DisplayName ?? $Name) `
                        -Description $Description `
                        -StartupType $StartupType | Out-Null
        }

        # 作成直後に説明文を念押し（環境によっては反映されないことがあるため）
        try {
            $svcCim = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
            if ($svcCim) {
                $null = Invoke-CimMethod -InputObject $svcCim -MethodName Change `
                    -Arguments @{ DisplayName = ($DisplayName ?? $Name); Description = $Description }
            }
        } catch {}

        Write-Host "サービス [$Name] を作成しました。"
    }

    # 自動起動なら起動しておく
    if ($StartupType -eq "Automatic") {
        try {
            Start-Service -Name $Name
            Write-Host "サービス [$Name] を起動しました。"
        } catch {
            Write-Warning "起動に失敗しました: $($_.Exception.Message)"
        }
    }
}

function Unregister-StaticFileServerService {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param(
        [Parameter(Mandatory=$false)]
        [string] $Name = "MinimalStaticFileServer"
    )

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "サービス [$Name] は存在しません。処理は不要です。"
        return
    }

    if ($PSCmdlet.ShouldProcess($Name, "Stop & Remove service")) {
        try { Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue } catch {}
        # 完全ネイティブ削除（PS5.1対応）
        Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue |
            Remove-CimInstance -ErrorAction SilentlyContinue
        Write-Host "サービス [$Name] を削除しました。"
    }
}
